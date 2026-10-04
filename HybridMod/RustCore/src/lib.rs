//! `lens_core` — the native (Rust) half of the HybridMod C#-host + Rust-core architecture.
//!
//! # Why this crate exists
//!
//! MelonLoader mods are managed .NET assemblies, so anything CPU-heavy or allocation-heavy
//! they do is subject to the GC and to the IL2CPP/managed interop boundary. This crate is a
//! plain `cdylib` exposing a small, stable **C ABI** so the managed host can push raw bytes
//! across the boundary once and get cheap, allocation-free computation back.
//!
//! # Design rules honoured here
//!
//! * Every entry point is `#[no_mangle] pub extern "C"` and uses only C-compatible types.
//!   No Rust type ever crosses the boundary; `#[repr(C)]` structs are used where a record
//!   layout has to be agreed on by both sides.
//! * Every entry point is defensive: null pointers, bogus lengths, invalid UTF-8, and
//!   out-of-range indices all produce a documented sentinel/error code instead of a crash.
//! * A panic must never unwind into the .NET runtime — that would take the game down. The
//!   `panic-guard` feature (on by default) wraps each body in `catch_unwind`.
//! * The whole data model is *pure computation*: no file I/O, no globals that mutate, no
//!   dependency on the game. That is what makes `cargo test` a genuine correctness check.
//!
//! # Threading
//!
//! The affix table is created/queried/destroyed by the caller. Handles are opaque
//! (`*mut LensAffixTable`) and are **not** internally synchronised: a handle must be used by
//! one thread at a time. The managed host creates its table on MelonLoader's init thread and
//! only reads from it afterwards.

pub mod dps;

use dps::{DamageSample, DpsCalculator, DpsSnapshot};

use std::collections::HashMap;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;
use std::str;

// ---------------------------------------------------------------------------------------
// Status codes / sentinels (documented contract shared with the C# side)
// ---------------------------------------------------------------------------------------

/// Operation succeeded. For `lens_affix_id_hash` this is *also* the hash of a valid key.
pub const LENS_OK: i32 = 0;
/// A required pointer argument was null.
pub const LENS_ERR_NULL: i32 = -1;
/// `len` was zero, or larger than `MAX_INPUT_BYTES`.
pub const LENS_ERR_LEN: i32 = -2;
/// The input bytes were not valid UTF-8.
pub const LENS_ERR_UTF8: i32 = -3;
/// A record had fewer than the 3 mandatory tab-separated fields.
pub const LENS_ERR_SHAPE: i32 = -4;

/// Upper bound on any single buffer accepted across the boundary (64 MiB).
/// Prevents a bogus `len` from turning into a wild pointer walk.
pub const MAX_INPUT_BYTES: usize = 64 * 1024 * 1024;

/// Returned by every `*const c_char` getter when there is nothing valid to return.
/// Never dereference this; always test for it first.
pub const LENS_NULL_CSTR: *const c_char = std::ptr::null();

/// Version reported by [`lens_core_version`]. Must stay in sync with `Cargo.toml`.
const VERSION_CSTR: &[u8] = b"lens_core/0.1.0\0";

// ---------------------------------------------------------------------------------------
// C-visible data layout
// ---------------------------------------------------------------------------------------

/// One observed damage event, as aggregated by the managed host from the game's combat log.
///
/// Layout is `#[repr(C)]` and deliberately made of `u64` + `f64` only, so both sides agree
/// on size/alignment on x86_64 Windows with no padding surprises:
/// `size_of::<LensDpsSample>() == 24`, `align_of == 8`.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct LensDpsSample {
    /// Approximate in-game millisecond timestamp.
    pub timestamp_ms: u64,
    /// Non-negative damage amount.
    pub amount: f64,
    /// Non-negative number of hits rolled into `amount`.
    pub hits: f64,
}

/// Compact aggregate returned by [`lens_dps_summarize`]. All fields are always written,
/// including on the error paths, so the caller never reads uninitialised memory.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct LensDpsSummary {
    /// Number of samples actually consumed (bounded by `max_samples`).
    pub samples: u64,
    /// Sum of all `amount` values.
    pub total_damage: f64,
    /// Sum of all `hits` values.
    pub total_hits: f64,
    /// Highest single-sample `amount` seen, or `0.0` when there were no samples.
    pub max_hit: f64,
    /// `(last.timestamp_ms - first.timestamp_ms) / 1000.0`, or `0.0` for < 2 samples.
    pub window_seconds: f64,
    /// `total_damage / window_seconds`, or `0.0` when the window is not positive.
    pub dps: f64,
}

/// One abbreviation, stored as raw bytes plus an explicit trailing NUL.
///
/// The NUL is the whole point: [`lens_affix_table_name_at`] hands a `*const c_char` to the
/// managed side, which will `Marshal.PtrToStringAnsi`/`UTF8` it. A `String`'s own buffer is
/// *not* NUL-terminated, so returning `String::as_ptr()` would make the marshaller walk into
/// whatever heap bytes follow — silently yielding a corrupt abbreviation.
#[derive(Debug)]
struct Abbrev {
    /// ASCII/UTF-8 text, always ending in a single `0` byte.
    nul_terminated: Vec<u8>,
}

impl Abbrev {
    /// Holds abbreviations short enough that the extra NUL never matters.
    const INLINE_HINT: usize = 32;

    fn new(text: &str) -> Self {
        let mut nul_terminated = Vec::with_capacity(text.len().max(Self::INLINE_HINT) + 1);
        nul_terminated.extend_from_slice(text.as_bytes());
        nul_terminated.push(0);
        Self { nul_terminated }
    }

    /// Borrows the buffer as a C string. Guaranteed non-null and NUL-terminated.
    fn as_c_ptr(&self) -> *const c_char {
        self.nul_terminated.as_ptr() as *const c_char
    }
}

/// Opaque handle to a parsed affix abbreviation table.
///
/// The inner fields are private on purpose: the managed side only ever holds the pointer.
pub struct LensAffixTable {
    /// `entry_for_id[i]` is the abbreviation for affix id `i`, if the table listed it.
    entry_for_id: Vec<Option<Abbrev>>,
    /// `id_for_name[abbreviation]` is the affix id.
    id_for_name: HashMap<String, u32>,
    /// How many data rows were accepted.
    records: u64,
    /// How many data rows were skipped (blank, comment, or malformed).
    skipped: u64,
}

// ---------------------------------------------------------------------------------------
// Panic containment
// ---------------------------------------------------------------------------------------

/// Runs `body`, converting any panic into `on_panic()` instead of letting it unwind
/// across the FFI boundary into the .NET runtime.
///
/// Generic over the returned value so the same guard covers `i32` status codes, `i64` counts,
/// and raw pointers.
///
/// `AssertUnwindSafe` is sound here because every body operates purely on its own arguments
/// and the owned table behind the handle; there is no shared mutable state to poison.
#[inline]
fn guarded<T, F, G>(body: F, on_panic: G) -> T
where
    F: FnOnce() -> T,
    G: FnOnce() -> T,
{
    #[cfg(feature = "panic-guard")]
    {
        catch_unwind(AssertUnwindSafe(body)).unwrap_or_else(|_| on_panic())
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        let _ = &on_panic;
        body()
    }
}

// ---------------------------------------------------------------------------------------
// Internal helpers (safe Rust, fully unit-testable)
// ---------------------------------------------------------------------------------------

/// Maximum identifier length kept as an affix name. Longer fields are ignored rather than
/// truncated, so we never build a silently wrong key.
const MAX_NAME_LEN: usize = 256;

/// FNV-1a, 64-bit. Small, stable across platforms/versions, and good enough to use as a
/// compact map key for short ASCII identifiers.
#[inline]
fn fnv1a64(bytes: &[u8]) -> u64 {
    const OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
    const PRIME: u64 = 0x0000_0100_0000_01b3;
    let mut hash = OFFSET;
    for &b in bytes {
        hash ^= u64::from(b);
        hash = hash.wrapping_mul(PRIME);
    }
    hash
}

/// Parses a `u32` affix id, rejecting negatives, signs, whitespace and overflow.
fn parse_affix_id(field: &str) -> Option<u32> {
    if field.is_empty() || field.len() > 10 {
        return None;
    }
    if !field.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    field.parse::<u32>().ok()
}

/// A line is skipped entirely when it is empty or starts with `#` (the table's comment
/// marker, also used for its header).
fn is_skippable_line(line: &str) -> bool {
    let trimmed = line.trim();
    trimmed.is_empty() || trimmed.starts_with('#')
}

/// Builds a table from raw TSV bytes.
///
/// Grammar accepted (deliberately liberal, mirroring the real `AffixAbbrev.tsv`):
/// * one record per line, `\n` or `\r\n`;
/// * `affixId <TAB> abbreviation <TAB> displayName [<TAB> ...extra ignored]`;
/// * blank lines and lines whose first non-space character is `#` are comments;
/// * a trailing `\r` on each field is trimmed.
///
/// Malformed data rows are counted as `skipped` rather than aborting the whole parse, so one
/// bad line cannot take the mod (or the game) down.
fn build_table(text: &str) -> Result<LensAffixTable, i32> {
    let mut entry_for_id: Vec<Option<Abbrev>> = Vec::new();
    let mut id_for_name: HashMap<String, u32> = HashMap::new();
    let mut records = 0u64;
    let mut skipped = 0u64;

    for raw_line in text.split('\n') {
        let line = raw_line.strip_suffix('\r').unwrap_or(raw_line);
        if is_skippable_line(line) {
            skipped += 1;
            continue;
        }

        let mut fields = line.split('\t').map(|f| f.trim_end_matches('\r'));
        let id_field = fields.next().unwrap_or("");
        let name_field = fields.next().unwrap_or("");

        let id = match parse_affix_id(id_field.trim()) {
            Some(id) => id,
            None => {
                skipped += 1;
                continue;
            }
        };
        let name = name_field.trim();
        if name.is_empty() || name.len() > MAX_NAME_LEN {
            skipped += 1;
            continue;
        }

        let idx = id as usize;
        if idx >= entry_for_id.len() {
            entry_for_id.resize_with(idx + 1, || None);
        }
        // First occurrence wins; a duplicate id later in the file does not overwrite it.
        if entry_for_id[idx].is_none() {
            entry_for_id[idx] = Some(Abbrev::new(name));
        }
        id_for_name.entry(name.to_owned()).or_insert(id);
        records += 1;
    }

    Ok(LensAffixTable {
        entry_for_id,
        id_for_name,
        records,
        skipped,
    })
}

/// Validates a raw pointer/length pair and converts it to a `&str`.
///
/// # Safety
/// `ptr` must either be null or point to at least `len` initialised, readable bytes that stay
/// valid for the duration of the call. The caller (the managed host) guarantees this by
/// pinning/`fixed`-ing a managed byte array for the length of the P/Invoke.
unsafe fn str_from_raw(ptr: *const u8, len: usize) -> Result<&'static str, i32> {
    if ptr.is_null() {
        return Err(LENS_ERR_NULL);
    }
    if len == 0 || len > MAX_INPUT_BYTES {
        return Err(LENS_ERR_LEN);
    }
    // SAFETY: non-null and length-checked by the caller contract above.
    let bytes = slice::from_raw_parts(ptr, len);
    str::from_utf8(bytes).map_err(|_| LENS_ERR_UTF8)
}

/// Borrows the table behind an opaque handle.
///
/// # Safety
/// `handle` must be null or a pointer previously returned by [`lens_affix_table_new`] that
/// has not yet been passed to [`lens_affix_table_free`].
unsafe fn table_ref<'a>(handle: *mut LensAffixTable) -> Result<&'a LensAffixTable, i32> {
    if handle.is_null() {
        return Err(LENS_ERR_NULL);
    }
    Ok(&*handle)
}

// ---------------------------------------------------------------------------------------
// FFI surface
// ---------------------------------------------------------------------------------------

/// Returns the core's version as a static NUL-terminated ASCII C string.
///
/// The returned pointer is valid for the lifetime of the process and must **not** be freed.
///
/// # Safety
/// Always safe to call: takes no arguments and never touches caller memory.
#[no_mangle]
pub extern "C" fn lens_core_version() -> *const c_char {
    VERSION_CSTR.as_ptr() as *const c_char
}

/// Sanity check used by the host to prove the boundary is live and the calling convention
/// (`cdecl`) matches. Returns `a + b` using saturating arithmetic so no input can trap.
///
/// # Safety
/// Always safe to call: no pointers involved.
#[no_mangle]
pub extern "C" fn lens_core_add(a: i32, b: i32) -> i32 {
    a.saturating_add(b)
}

/// Number of bytes the core will accept in a single buffer.
///
/// # Safety
/// Always safe to call.
#[no_mangle]
pub extern "C" fn lens_core_max_input_bytes() -> u64 {
    MAX_INPUT_BYTES as u64
}

/// Hashes a key with FNV-1a/64 for use as a compact lookup key.
///
/// `out_hash` is zeroed before any validation, so on error it reads `0` rather than stale
/// garbage. Returns `LENS_ERR_NULL` for a null `ptr` or `out_hash`, and `LENS_ERR_LEN` when
/// `len` is `0` or greater than `MAX_INPUT_BYTES`.
///
/// # Safety
/// `ptr` must be null or point to `len` readable bytes; `out_hash` must be null or point to
/// one writable `u64`.
#[no_mangle]
pub extern "C" fn lens_affix_id_hash(ptr: *const u8, len: usize, out_hash: *mut u64) -> i32 {
    guarded(
        || {
            if out_hash.is_null() {
                return LENS_ERR_NULL;
            }
            // SAFETY: null-checked directly above. Zeroing before any other validation means
            // every error path still leaves the caller with an initialised value.
            unsafe { *out_hash = 0 };
            let text = match unsafe { str_from_raw(ptr, len) } {
                Ok(t) => t,
                Err(code) => return code,
            };
            let hash = fnv1a64(text.trim().as_bytes());
            // SAFETY: null-checked above; aligned by the caller's `out ulong`.
            unsafe { *out_hash = hash };
            LENS_OK
        },
        || LENS_ERR_UTF8,
    )
}

/// Counts *data* records in a tab-separated affix table.
///
/// Header and comment lines (leading whitespace then `#`) and lines that are not
/// `u32 <TAB> non-empty-name` are not counted. Returns a non-negative record count, or one of
/// the negative `LENS_ERR_*` codes.
///
/// # Safety
/// `ptr` must be null or point to `len` readable bytes that stay valid for the call.
#[no_mangle]
pub extern "C" fn lens_core_parse_affix_tsv(ptr: *const u8, len: usize) -> i64 {
    guarded(
        || {
            let text = match unsafe { str_from_raw(ptr, len) } {
                Ok(t) => t,
                Err(code) => return i64::from(code),
            };
            match build_table(text) {
                Ok(table) => table.records as i64,
                Err(code) => i64::from(code),
            }
        },
        || i64::from(LENS_ERR_UTF8),
    )
}

/// Parses `len` bytes of TSV and returns an owned, opaque table handle.
///
/// Returns null on any error (null pointer, bad length, invalid UTF-8, allocation failure);
/// a null handle makes every other table call return an error instead of crashing.
///
/// # Safety
/// `ptr` must be null or point to `len` readable bytes. The returned handle must eventually be
/// released with [`lens_affix_table_free`].
#[no_mangle]
pub extern "C" fn lens_affix_table_new(ptr: *const u8, len: usize) -> *mut LensAffixTable {
    #[cfg(feature = "panic-guard")]
    {
        catch_unwind(AssertUnwindSafe(|| table_new_inner(ptr, len))).unwrap_or(std::ptr::null_mut())
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        table_new_inner(ptr, len)
    }
}

fn table_new_inner(ptr: *const u8, len: usize) -> *mut LensAffixTable {
    let text = match unsafe { str_from_raw(ptr, len) } {
        Ok(t) => t,
        Err(_) => return std::ptr::null_mut(),
    };
    match build_table(text) {
        Ok(table) => Box::into_raw(Box::new(table)),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Frees a handle from [`lens_affix_table_new`]. Passing null is a no-op.
///
/// # Safety
/// `handle` must be null or a handle returned by [`lens_affix_table_new`], and must not be
/// used again afterwards (double free is undefined behaviour).
#[no_mangle]
pub extern "C" fn lens_affix_table_free(handle: *mut LensAffixTable) {
    if handle.is_null() {
        return;
    }
    let free = || {
        // SAFETY: the handle came from `Box::into_raw` and is freed at most once by contract.
        unsafe { drop(Box::from_raw(handle)) };
    };
    #[cfg(feature = "panic-guard")]
    {
        // A panic in `Drop` still must not cross the boundary; the allocation is leaked
        // rather than the process being torn down.
        let _ = catch_unwind(AssertUnwindSafe(free));
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        free();
    }
}

/// Number of data records in a table, or a negative `LENS_ERR_*` code.
///
/// # Safety
/// See [`table_ref`].
#[no_mangle]
pub extern "C" fn lens_affix_table_len(handle: *mut LensAffixTable) -> i64 {
    guarded(
        || match unsafe { table_ref(handle) } {
            Ok(table) => table.records as i64,
            Err(code) => i64::from(code),
        },
        || i64::from(LENS_ERR_NULL),
    )
}

/// Number of rows the parser skipped (comments, blanks, malformed).
///
/// # Safety
/// See [`table_ref`].
#[no_mangle]
pub extern "C" fn lens_affix_table_skipped(handle: *mut LensAffixTable) -> i64 {
    guarded(
        || match unsafe { table_ref(handle) } {
            Ok(table) => table.skipped as i64,
            Err(code) => i64::from(code),
        },
        || i64::from(LENS_ERR_NULL),
    )
}

/// Looks up an abbreviation by **ordinal record index** (`0 .. len`).
///
/// Returns a pointer to a NUL-terminated UTF-8 string owned by the table (do not free it), or
/// `LENS_NULL_CSTR` when the index is out of range, the slot is empty, or the handle is null.
///
/// # Safety
/// See [`table_ref`]. The returned pointer is invalidated by [`lens_affix_table_free`].
#[no_mangle]
pub extern "C" fn lens_affix_table_name_at(handle: *mut LensAffixTable, index: usize) -> *const c_char {
    guarded(
        || match unsafe { table_ref(handle) } {
            Ok(table) => match table.entry_for_id.get(index).and_then(|slot| slot.as_ref()) {
                // `Abbrev` owns a NUL-terminated buffer, so this is a real C string.
                Some(abbrev) => abbrev.as_c_ptr(),
                None => LENS_NULL_CSTR,
            },
            Err(_) => LENS_NULL_CSTR,
        },
        || LENS_NULL_CSTR,
    )
}

/// Looks up an abbreviation by numeric **affix id**.
///
/// Returns the same kind of borrowed pointer as [`lens_affix_table_name_at`], or
/// `LENS_NULL_CSTR` when the id is unknown or the handle is null.
///
/// # Safety
/// See [`table_ref`].
#[no_mangle]
pub extern "C" fn lens_affix_table_name_for_id(handle: *mut LensAffixTable, id: u32) -> *const c_char {
    guarded(
        || match unsafe { table_ref(handle) } {
            Ok(table) => match table.entry_for_id.get(id as usize).and_then(|slot| slot.as_ref()) {
                Some(abbrev) => abbrev.as_c_ptr(),
                None => LENS_NULL_CSTR,
            },
            Err(_) => LENS_NULL_CSTR,
        },
        || LENS_NULL_CSTR,
    )
}

/// Looks up a numeric affix id from an abbreviation.
///
/// Writes `LENS_OK` and the id into `out_id`, or returns `LENS_ERR_NULL` when `out_id` is
/// null. An unknown but well-formed key yields `LENS_OK` with `out_id = 0` — use
/// [`lens_affix_table_name_for_id`] to confirm, or treat id `0` as "absent" as the managed
/// host does.
///
/// # Safety
/// `ptr` must be null or point to `len` readable bytes; `out_id` must be null or writable.
#[no_mangle]
pub extern "C" fn lens_affix_table_id_for_name(
    handle: *mut LensAffixTable,
    ptr: *const u8,
    len: usize,
    out_id: *mut u32,
) -> i32 {
    guarded(
        || {
            if out_id.is_null() {
                return LENS_ERR_NULL;
            }
            // SAFETY: null-checked directly above.
            unsafe { *out_id = 0 };
            let table = match unsafe { table_ref(handle) } {
                Ok(t) => t,
                Err(code) => return code,
            };
            let key = match unsafe { str_from_raw(ptr, len) } {
                Ok(k) => k.trim(),
                Err(code) => return code,
            };
            if let Some(&id) = table.id_for_name.get(key) {
                // SAFETY: null-checked above.
                unsafe { *out_id = id };
            }
            LENS_OK
        },
        || LENS_ERR_UTF8,
    )
}

/// Aggregates a caller-supplied array of [`LensDpsSample`] into a [`LensDpsSummary`] —
/// the canonical "pass a struct array across the boundary in one call" demonstration.
///
/// `max_samples` caps how many elements are read, which is how the host avoids walking off the
/// end of a shorter-than-expected buffer. `out_summary` is always fully written (zeroed) before
/// any validation failure returns, so the caller can safely read it unconditionally.
///
/// Returns `LENS_OK`, or `LENS_ERR_NULL` (null `samples`/`out_summary`, or non-positive
/// `max_samples`), or `LENS_ERR_LEN` when `max_samples` exceeds `MAX_INPUT_BYTES / 8`.
///
/// # Safety
/// `samples` must be null or point to `max_samples` initialised `LensDpsSample` values
/// (`#[repr(C)]`, 24 bytes each); `out_summary` must be null or point to one writable
/// `LensDpsSummary`.
#[no_mangle]
pub extern "C" fn lens_dps_summarize(
    samples: *const LensDpsSample,
    max_samples: usize,
    out_summary: *mut LensDpsSummary,
) -> i32 {
    guarded(
        || {
            if out_summary.is_null() {
                return LENS_ERR_NULL;
            }
            // SAFETY: null-checked directly above. Zeroing first means every error path still
            // leaves the caller with a fully-initialised struct.
            unsafe { *out_summary = LensDpsSummary::default() };

            if samples.is_null() || max_samples == 0 {
                return LENS_ERR_NULL;
            }
            if max_samples > MAX_INPUT_BYTES / std::mem::size_of::<LensDpsSample>() {
                return LENS_ERR_LEN;
            }

            // SAFETY: non-null, and the element count was bounded just above.
            let data = unsafe { slice::from_raw_parts(samples, max_samples) };

            let mut summary = LensDpsSummary::default();
            let mut first_ms = 0u64;
            let mut last_ms = 0u64;
            for (i, sample) in data.iter().enumerate() {
                // NaN/negative values would silently poison every downstream total, so they
                // are treated as "no data" rather than propagated.
                let amount = if sample.amount.is_finite() && sample.amount > 0.0 {
                    sample.amount
                } else {
                    0.0
                };
                let hits = if sample.hits.is_finite() && sample.hits > 0.0 {
                    sample.hits
                } else {
                    0.0
                };
                if i == 0 {
                    first_ms = sample.timestamp_ms;
                }
                last_ms = sample.timestamp_ms;
                summary.total_damage += amount;
                summary.total_hits += hits;
                if amount > summary.max_hit {
                    summary.max_hit = amount;
                }
            }

            summary.samples = max_samples as u64;
            summary.window_seconds = if max_samples >= 2 && last_ms > first_ms {
                (last_ms - first_ms) as f64 / 1000.0
            } else {
                0.0
            };
            summary.dps = if summary.window_seconds > 0.0 {
                summary.total_damage / summary.window_seconds
            } else {
                0.0
            };

            // SAFETY: null-checked above; aligned by the caller's `out LensDpsSummary`.
            unsafe { *out_summary = summary };
            LENS_OK
        },
        || LENS_ERR_NULL,
    )
}

// ---------------------------------------------------------------------------------------
// DPS calculator (stateful port of LEns's DpsStatsCalculator)
//
// This is the "real work" half of the core: the managed host feeds it raw damage events
// and reads back a snapshot, instead of re-implementing the sliding-window triangular
// weighting in IL. See `dps.rs` for the IL-level provenance of every rule.
// ---------------------------------------------------------------------------------------

/// One damage event as passed from the managed side.
///
/// `#[repr(C)]` with explicit padding-free layout: `f32` + `f64` + two `u8` flags.
/// Size is 24 bytes (4 + 4 pad + 8 + 1 + 1 + 6 pad), alignment 8.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct LensDamageSample {
    /// Event time in seconds, on the same clock as the `now` passed to the snapshot call.
    pub time: f32,
    /// Damage amount; non-positive and NaN values are ignored, as the original does.
    pub amount: f64,
    /// Non-zero when this is damage *taken*; such samples never enter the DPS timeline.
    pub is_incoming: u8,
    /// Non-zero when the hit was a critical strike.
    pub is_crit: u8,
}

/// Snapshot returned by [`lens_dps_snapshot`]. Mirrors LEns's `DpsSnapshot`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct LensDpsSnapshot {
    pub current_dps: f64,
    pub total_damage: f64,
    pub max_incoming_damage: f64,
    pub hit_count: i32,
    pub crit_count: i32,
    pub crit_rate: f64,
    pub window_seconds: f32,
    pub combat_duration_seconds: f64,
}

impl From<DpsSnapshot> for LensDpsSnapshot {
    fn from(s: DpsSnapshot) -> Self {
        Self {
            current_dps: s.current_dps,
            total_damage: s.total_damage,
            max_incoming_damage: s.max_incoming_damage,
            hit_count: s.hit_count,
            crit_count: s.crit_count,
            crit_rate: s.crit_rate,
            window_seconds: s.window_seconds,
            combat_duration_seconds: s.combat_duration_seconds,
        }
    }
}

/// Creates a DPS calculator. `window_seconds` is the sliding window length.
///
/// `epoch` is the host's current clock reading (Unity `Time.realtimeSinceStartup`), which
/// establishes the zero point for all later times — the same role the original's
/// `_sessionEpoch` plays. Returns null only if allocation fails.
///
/// # Safety
/// No pointers are involved. The returned handle must be freed with [`lens_dps_free`].
#[no_mangle]
pub extern "C" fn lens_dps_new(window_seconds: f32, epoch: f32) -> *mut DpsCalculator {
    #[cfg(feature = "panic-guard")]
    {
        catch_unwind(AssertUnwindSafe(|| {
            Box::into_raw(Box::new(DpsCalculator::new(window_seconds, epoch)))
        }))
        .unwrap_or(std::ptr::null_mut())
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        Box::into_raw(Box::new(DpsCalculator::new(window_seconds, epoch)))
    }
}

/// Frees a handle from [`lens_dps_new`]. Passing null is a no-op.
///
/// # Safety
/// `handle` must be null or a handle from [`lens_dps_new`] that has not been freed.
#[no_mangle]
pub extern "C" fn lens_dps_free(handle: *mut DpsCalculator) {
    if handle.is_null() {
        return;
    }
    #[cfg(feature = "panic-guard")]
    {
        let _ = catch_unwind(AssertUnwindSafe(|| unsafe { drop(Box::from_raw(handle)) }));
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        unsafe { drop(Box::from_raw(handle)) };
    }
}

/// Feeds one damage event into the calculator. A null handle is ignored.
///
/// # Safety
/// `handle` must be null or a live handle from [`lens_dps_new`].
#[no_mangle]
pub extern "C" fn lens_dps_add_sample(handle: *mut DpsCalculator, sample: LensDamageSample) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind_silent(|| {
        // SAFETY: null-checked above and owned by the caller for the call's duration.
        let calc = unsafe { &mut *handle };
        calc.add_sample(DamageSample {
            time: sample.time,
            amount: sample.amount,
            is_incoming: sample.is_incoming != 0,
            is_crit: sample.is_crit != 0,
        });
    });
}

/// Drops timeline points that have aged out of the window. A null handle is ignored.
///
/// The original calls this from its per-frame `Tick`; the host may call it as often as it
/// likes, since trimming is idempotent for a given `now`.
///
/// # Safety
/// `handle` must be null or a live handle from [`lens_dps_new`].
#[no_mangle]
pub extern "C" fn lens_dps_trim(handle: *mut DpsCalculator, now: f32) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind_silent(|| {
        // SAFETY: null-checked above.
        let calc = unsafe { &mut *handle };
        calc.trim_expired_points(now);
    });
}

/// Reads a snapshot into `out_snapshot`, which is zeroed first so it is always safe to read.
///
/// Returns `LENS_OK`, or `LENS_ERR_NULL` when `handle` or `out_snapshot` is null.
///
/// # Safety
/// `handle` must be null or a live handle; `out_snapshot` must be null or point to one
/// writable [`LensDpsSnapshot`].
#[no_mangle]
pub extern "C" fn lens_dps_snapshot(
    handle: *mut DpsCalculator,
    now: f32,
    out_snapshot: *mut LensDpsSnapshot,
) -> i32 {
    guarded(
        || {
            if out_snapshot.is_null() {
                return LENS_ERR_NULL;
            }
            // SAFETY: null-checked directly above; zeroing first makes every error path safe.
            unsafe { *out_snapshot = LensDpsSnapshot::default() };

            if handle.is_null() {
                return LENS_ERR_NULL;
            }
            // SAFETY: null-checked above.
            let calc = unsafe { &*handle };
            // SAFETY: null-checked above.
            unsafe { *out_snapshot = calc.snapshot(now).into() };
            LENS_OK
        },
        || LENS_ERR_NULL,
    )
}

/// Clears all counters and rebases the epoch to `now`. A null handle is ignored.
///
/// # Safety
/// `handle` must be null or a live handle from [`lens_dps_new`].
#[no_mangle]
pub extern "C" fn lens_dps_reset(handle: *mut DpsCalculator, now: f32) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind_silent(|| {
        // SAFETY: null-checked above.
        let calc = unsafe { &mut *handle };
        calc.reset(now);
    });
}

/// Number of points currently on the timeline, or a negative `LENS_ERR_*` code.
///
/// # Safety
/// `handle` must be null or a live handle from [`lens_dps_new`].
#[no_mangle]
pub extern "C" fn lens_dps_timeline_len(handle: *mut DpsCalculator) -> i64 {
    guarded(
        || {
            if handle.is_null() {
                return i64::from(LENS_ERR_NULL);
            }
            // SAFETY: null-checked above.
            i64::try_from(unsafe { &*handle }.timeline_len()).unwrap_or(i64::MAX)
        },
        || i64::from(LENS_ERR_NULL),
    )
}

/// Runs `body`, swallowing any panic. Used by the void-returning mutators, which have no
/// sensible sentinel to report; a panic there would otherwise cross the FFI boundary.
#[inline]
fn catch_unwind_silent<F: FnOnce()>(body: F) {
    #[cfg(feature = "panic-guard")]
    {
        let _ = catch_unwind(AssertUnwindSafe(body));
    }
    #[cfg(not(feature = "panic-guard"))]
    {
        body();
    }
}

// ---------------------------------------------------------------------------------------
// Tests — these prove the logic of the very same functions the managed host calls.
// ---------------------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    /// Minimal but structurally faithful stand-in for `Mods\AffixAbbrev.tsv`.
    const SAMPLE_TSV: &str = "# AffixId\tAbbr\tItemName\n\
                              # a comment line\n\
                              \n\
                              0\tSTR\tStrength\n\
                              7\tDEX\tDexterity\n\
                              7\tDUP\tDuplicate id is ignored\n\
                              100\tLIGHTNING_RES\tLightning Resistance\n\
                              \tMISSING_ID\tno id\n\
                              abc\tBAD_ID\tnot numeric\n\
                              12\t\tempty abbr\n";

    fn parse(text: &str) -> i64 {
        lens_core_parse_affix_tsv(text.as_ptr(), text.len())
    }

    fn new_table(text: &str) -> *mut LensAffixTable {
        lens_affix_table_new(text.as_ptr(), text.len())
    }

    /// # Safety: `p` is either null or a live table owned by the caller of this helper.
    unsafe fn name_at(handle: *mut LensAffixTable, index: usize) -> Option<String> {
        let p = lens_affix_table_name_at(handle, index);
        if p.is_null() {
            return None;
        }
        Some(std::ffi::CStr::from_ptr(p).to_str().unwrap().to_owned())
    }

    #[test]
    fn version_is_a_stable_non_null_cstr() {
        let p = lens_core_version();
        assert!(!p.is_null());
        let s = unsafe { std::ffi::CStr::from_ptr(p) }.to_str().unwrap();
        assert_eq!(s, "lens_core/0.1.0");
        // Same static buffer on every call — the host may cache the pointer.
        assert_eq!(p, lens_core_version());
    }

    #[test]
    fn add_uses_cdecl_and_saturates() {
        assert_eq!(lens_core_add(2, 3), 5);
        assert_eq!(lens_core_add(-10, 4), -6);
        assert_eq!(lens_core_add(i32::MAX, 1), i32::MAX);
        assert_eq!(lens_core_add(i32::MIN, -1), i32::MIN);
    }

    #[test]
    fn tsv_parser_counts_only_valid_data_rows() {
        // 4 well-formed data rows; comment/blank/duplicate-id wins/orphan-abbr rows excluded.
        assert_eq!(parse(SAMPLE_TSV), 4);
    }

    #[test]
    fn tsv_parser_handles_crlf_and_no_trailing_newline() {
        let crlf = "1\tA\tA\r\n2\tB\tB\r\n3\tC\tC";
        assert_eq!(parse(crlf), 3);
        let table = new_table(crlf);
        assert!(!table.is_null());
        unsafe {
            assert_eq!(name_at(table, 1).as_deref(), Some("A"));
            assert_eq!(name_at(table, 2).as_deref(), Some("B"));
            assert_eq!(name_at(table, 3).as_deref(), Some("C"));
            lens_affix_table_free(table);
        }
    }

    #[test]
    fn tsv_parser_rejects_bad_pointers_and_lengths() {
        assert_eq!(lens_core_parse_affix_tsv(std::ptr::null(), 10), i64::from(LENS_ERR_NULL));
        assert_eq!(lens_core_parse_affix_tsv(SAMPLE_TSV.as_ptr(), 0), i64::from(LENS_ERR_LEN));
        assert_eq!(
            lens_core_parse_affix_tsv(SAMPLE_TSV.as_ptr(), MAX_INPUT_BYTES + 1),
            i64::from(LENS_ERR_LEN)
        );
        // Invalid UTF-8 (lone 0xFF continuation byte).
        let bad = [b'1', b'\t', 0xFF, b'\n'];
        assert_eq!(
            lens_core_parse_affix_tsv(bad.as_ptr(), bad.len()),
            i64::from(LENS_ERR_UTF8)
        );
    }

    #[test]
    fn table_lookup_by_index_and_id_and_name() {
        let handle = new_table(SAMPLE_TSV);
        assert!(!handle.is_null());
        unsafe {
            assert_eq!(lens_affix_table_len(handle), 4);
            // 2 comment lines + 1 blank + 1 duplicate-id row + 3 malformed rows
            // (empty id, non-numeric id, empty abbreviation).
            assert_eq!(lens_affix_table_skipped(handle), 7);

            assert_eq!(name_at(handle, 0).as_deref(), Some("STR"));
            assert_eq!(name_at(handle, 7).as_deref(), Some("DEX"), "first id wins");
            assert_eq!(name_at(handle, 100).as_deref(), Some("LIGHTNING_RES"));
            assert_eq!(name_at(handle, 99), None, "unmapped slot is null");
            assert_eq!(name_at(handle, usize::MAX), None, "absurd index must not fault");

            let by_id = lens_affix_table_name_for_id(handle, 100);
            assert!(!by_id.is_null());
            assert_eq!(
                std::ffi::CStr::from_ptr(by_id).to_str().unwrap(),
                "LIGHTNING_RES"
            );
            assert!(lens_affix_table_name_for_id(handle, 4242).is_null());

            let mut id = 999u32;
            let key = "DEX";
            assert_eq!(
                lens_affix_table_id_for_name(handle, key.as_ptr(), key.len(), &mut id),
                LENS_OK
            );
            assert_eq!(id, 7);

            let missing = "NOPE";
            assert_eq!(
                lens_affix_table_id_for_name(handle, missing.as_ptr(), missing.len(), &mut id),
                LENS_OK
            );
            assert_eq!(id, 0, "unknown key yields the documented absent sentinel");

            lens_affix_table_free(handle);
        }
    }

    #[test]
    fn table_handles_null_and_double_free_safely() {
        assert!(lens_affix_table_new(std::ptr::null(), 5).is_null());
        assert_eq!(lens_affix_table_len(std::ptr::null_mut()), i64::from(LENS_ERR_NULL));
        assert!(lens_affix_table_name_for_id(std::ptr::null_mut(), 1).is_null());
        // Freeing null must be a no-op, not a crash.
        lens_affix_table_free(std::ptr::null_mut());
    }

    #[test]
    fn hash_is_deterministic_and_guarded() {
        let key = b"LIGHTNING_RES";
        let mut a = 0u64;
        let mut b = 0u64;
        assert_eq!(lens_affix_id_hash(key.as_ptr(), key.len(), &mut a), LENS_OK);
        assert_eq!(lens_affix_id_hash(key.as_ptr(), key.len(), &mut b), LENS_OK);
        assert_eq!(a, b);
        assert_eq!(a, fnv1a64(key), "internal helper and FFI entry point agree");

        // Distinct keys must not collide on the realistic affix-name alphabet.
        assert_ne!(a, {
            let mut h = 0u64;
            let other = b"LIGHTNING_DAMAGE";
            lens_affix_id_hash(other.as_ptr(), other.len(), &mut h);
            h
        });

        // Whitespace is trimmed, so padded input hashes identically to the bare key.
        let padded = b"  LIGHTNING_RES\t";
        let mut c = 0u64;
        assert_eq!(lens_affix_id_hash(padded.as_ptr(), padded.len(), &mut c), LENS_OK);
        assert_eq!(c, a, "trimming must make padded and bare keys hash alike");

        // --- error paths. Each uses its own out-parameter so earlier assertions cannot be
        // invalidated by a rejected (but not zeroing) call.
        let mut null_src = 55u64;
        assert_eq!(lens_affix_id_hash(std::ptr::null(), 4, &mut null_src), LENS_ERR_NULL);
        assert_eq!(null_src, 0, "out-parameter is zeroed even when rejected");

        let mut zero_len = 55u64;
        assert_eq!(lens_affix_id_hash(key.as_ptr(), 0, &mut zero_len), LENS_ERR_LEN);
        assert_eq!(zero_len, 0);

        // A NULL out-pointer must be rejected instead of dereferenced.
        assert_eq!(
            lens_affix_id_hash(key.as_ptr(), key.len(), std::ptr::null_mut()),
            LENS_ERR_NULL
        );

        // Invalid UTF-8 must be reported, not hashed.
        let bad = [b'x', 0xFF, b'y'];
        let mut invalid = 55u64;
        assert_eq!(lens_affix_id_hash(bad.as_ptr(), bad.len(), &mut invalid), LENS_ERR_UTF8);
        assert_eq!(invalid, 0);
    }

    /// Pins the hash against the published FNV-1a/64 reference vectors, so an accidental change
    /// to the algorithm cannot silently invalidate every key the host already persisted.
    #[test]
    fn hash_matches_published_fnv1a64_vectors() {
        assert_eq!(fnv1a64(b""), 0xcbf2_9ce4_8422_2325, "FNV offset basis");
        assert_eq!(fnv1a64(b"a"), 0xaf63_dc4c_8601_ec8c);
        assert_eq!(fnv1a64(b"foobar"), 0x8594_4171_f739_67e8);

        // The FFI entry point must agree with the helper on a realistic affix key.
        let key = b"LIGHTNING_RES";
        let mut out = 0u64;
        assert_eq!(lens_affix_id_hash(key.as_ptr(), key.len(), &mut out), LENS_OK);
        assert_eq!(out, fnv1a64(key));
    }

    #[test]
    fn repr_c_layout_matches_the_managed_declaration() {
        use std::mem::{align_of, size_of};
        assert_eq!(size_of::<LensDpsSample>(), 24);
        assert_eq!(align_of::<LensDpsSample>(), 8);
        assert_eq!(size_of::<LensDpsSummary>(), 48);
        assert_eq!(align_of::<LensDpsSummary>(), 8);
    }

    #[test]
    fn dps_summary_aggregates_a_struct_array() {
        let samples = [
            LensDpsSample { timestamp_ms: 1_000, amount: 100.0, hits: 1.0 },
            LensDpsSample { timestamp_ms: 1_500, amount: 250.0, hits: 2.0 },
            LensDpsSample { timestamp_ms: 2_000, amount: 50.0, hits: 1.0 },
        ];
        let mut out = LensDpsSummary::default();
        let rc = lens_dps_summarize(samples.as_ptr(), samples.len(), &mut out);
        assert_eq!(rc, LENS_OK);
        assert_eq!(out.samples, 3);
        assert_eq!(out.total_damage, 400.0);
        assert_eq!(out.total_hits, 4.0);
        assert_eq!(out.max_hit, 250.0);
        assert_eq!(out.window_seconds, 1.0);
        assert_eq!(out.dps, 400.0);
    }

    #[test]
    fn dps_summary_ignores_poison_values_and_degenerate_windows() {
        let samples = [
            LensDpsSample { timestamp_ms: 10, amount: f64::NAN, hits: 1.0 },
            LensDpsSample { timestamp_ms: 10, amount: -5.0, hits: f64::INFINITY },
            LensDpsSample { timestamp_ms: 10, amount: 20.0, hits: 1.0 },
        ];
        let mut out = LensDpsSummary::default();
        assert_eq!(lens_dps_summarize(samples.as_ptr(), samples.len(), &mut out), LENS_OK);
        assert_eq!(out.total_damage, 20.0, "NaN and negative amounts are dropped");
        assert_eq!(
            out.total_hits, 2.0,
            "infinite hits are dropped (1.0), finite hits are kept (1.0 + 1.0)"
        );
        assert_eq!(out.window_seconds, 0.0, "identical timestamps give no window");
        assert_eq!(out.dps, 0.0, "no division by a zero window");

        // Single sample: valid totals, but still no measurable window.
        let one = [LensDpsSample { timestamp_ms: 5, amount: 7.0, hits: 1.0 }];
        assert_eq!(lens_dps_summarize(one.as_ptr(), 1, &mut out), LENS_OK);
        assert_eq!(out.samples, 1);
        assert_eq!(out.total_damage, 7.0);
        assert_eq!(out.dps, 0.0);

        // Empty array is a legal (if useless) call.
        assert_eq!(lens_dps_summarize(one.as_ptr(), 0, &mut out), LENS_ERR_NULL);
        assert_eq!(out, LensDpsSummary::default(), "output is zeroed even on error");
    }

    #[test]
    fn dps_summary_respects_max_samples_cap() {
        let samples = [
            LensDpsSample { timestamp_ms: 0, amount: 10.0, hits: 1.0 },
            LensDpsSample { timestamp_ms: 1_000, amount: 999.0, hits: 1.0 },
        ];
        let mut out = LensDpsSummary::default();
        assert_eq!(lens_dps_summarize(samples.as_ptr(), 1, &mut out), LENS_OK);
        assert_eq!(out.samples, 1);
        assert_eq!(out.total_damage, 10.0, "the cap must stop the walk");
    }

    #[test]
    fn dps_summary_rejects_null_and_absurd_arguments() {
        let one = [LensDpsSample { timestamp_ms: 0, amount: 1.0, hits: 1.0 }];
        let mut out = LensDpsSummary::default();
        assert_eq!(lens_dps_summarize(std::ptr::null(), 1, &mut out), LENS_ERR_NULL);
        assert_eq!(lens_dps_summarize(one.as_ptr(), 1, std::ptr::null_mut()), LENS_ERR_NULL);
        assert_eq!(
            lens_dps_summarize(one.as_ptr(), MAX_INPUT_BYTES, &mut out),
            LENS_ERR_LEN
        );
    }

    #[test]
    fn max_input_is_advertised_consistently() {
        assert_eq!(lens_core_max_input_bytes(), MAX_INPUT_BYTES as u64);
    }

    // ---- real-data and new-FFI coverage -------------------------------------------------

    /// The genuine `Mods\AffixAbbrev.tsv` produced by LEns, embedded at compile time.
    ///
    /// Testing against the real 1112-record table is what turns "the parser is plausible"
    /// into "the parser handles the actual input". This file is stable (LEns only rewrites
    /// it while the game runs) but is not required to build: if it is absent the test below
    /// skips instead of failing the build.
    const REAL_TSV: Option<&str> = option_env!("LENS_REAL_TSV");

    #[test]
    fn parser_handles_the_real_affix_table_when_available() {
        let Some(path) = REAL_TSV else {
            // No real table wired in; the synthetic tests above still cover the grammar.
            return;
        };
        let text = match std::fs::read_to_string(path) {
            Ok(t) => t,
            Err(_) => return,
        };

        // Measured against the file shipped with LEns: 1161 lines, of which 49 are
        // comment/blank, leaving exactly 1112 data rows — and every one of them contains a tab.
        // Asserting the exact number means a parser regression cannot slip through as "still
        // more than a thousand".
        let records = lens_core_parse_affix_tsv(text.as_ptr(), text.len());
        assert_eq!(records, 1112, "real AffixAbbrev.tsv must yield exactly 1112 records");

        let handle = lens_affix_table_new(text.as_ptr(), text.len());
        assert!(!handle.is_null(), "must parse the real table without failing");
        unsafe {
            assert_eq!(lens_affix_table_len(handle), records);
            // 49 comment/blank lines in the file, plus the empty element that `split('\n')`
            // produces after the final newline: 50 skipped. (A naive line count via
            // `Get-Content` reports 49 because it drops that trailing empty line — the
            // parser is right and the first version of this assertion was wrong.)
            assert_eq!(lens_affix_table_skipped(handle), 50);

            // Every populated slot must round-trip to valid, non-empty UTF-8, and the real
            // abbreviations are CJK text, so this also proves UTF-8 survives the boundary.
            let n = lens_affix_table_len(handle);
            let mut seen = 0usize;
            for i in 0..=n {
                let p = lens_affix_table_name_at(handle, i as usize);
                if p.is_null() {
                    continue;
                }
                seen += 1;
                let s = std::ffi::CStr::from_ptr(p).to_str().expect("valid UTF-8");
                assert!(!s.is_empty(), "slot {i} must not be an empty string");
            }
            assert!(seen > 0, "the real table must populate at least one slot");

            // Affix id 1 exists in the real file and its abbreviation is non-ASCII.
            let p1 = lens_affix_table_name_for_id(handle, 1);
            assert!(!p1.is_null(), "affix id 1 must be present in the real table");
            let s1 = std::ffi::CStr::from_ptr(p1).to_str().unwrap();
            assert!(!s1.is_ascii(), "expected a CJK abbreviation, got {s1:?}");

            lens_affix_table_free(handle);
        }
    }

    // ---- DPS calculator FFI -------------------------------------------------------------

    fn sample(time: f32, amount: f64) -> LensDamageSample {
        LensDamageSample { time, amount, is_incoming: 0, is_crit: 0 }
    }

    #[test]
    fn dps_ffi_end_to_end() {
        let h = lens_dps_new(5.0, 0.0);
        assert!(!h.is_null());

        for i in 0..10 {
            lens_dps_add_sample(h, sample(i as f32 * 0.5, 100.0));
        }
        assert_eq!(lens_dps_timeline_len(h), 10);

        lens_dps_trim(h, 4.5);
        let mut snap = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(h, 4.5, &mut snap), LENS_OK);

        assert_eq!(snap.hit_count, 10);
        assert_eq!(snap.total_damage, 1000.0);
        assert_eq!(snap.window_seconds, 5.0);
        assert!((snap.combat_duration_seconds - 4.5).abs() < 1e-6);
        // weights 1.0 .. 0.1 => sum 5.5 ; dps = 2 * 550 / 5
        assert!((snap.current_dps - 220.0).abs() < 1e-3, "got {}", snap.current_dps);

        lens_dps_reset(h, 100.0);
        assert_eq!(lens_dps_timeline_len(h), 0);
        assert_eq!(lens_dps_snapshot(h, 100.0, &mut snap), LENS_OK);
        // Reset clears the counters but preserves the configured window, so compare fields
        // rather than the whole struct against `default()`.
        assert_eq!(snap.hit_count, 0);
        assert_eq!(snap.crit_count, 0);
        assert_eq!(snap.total_damage, 0.0);
        assert_eq!(snap.max_incoming_damage, 0.0);
        assert_eq!(snap.current_dps, 0.0);
        assert_eq!(snap.combat_duration_seconds, 0.0);
        assert_eq!(snap.window_seconds, 5.0, "window survives Reset()");

        lens_dps_free(h);
    }

    #[test]
    fn dps_ffi_incoming_and_crit_flags_are_honoured() {
        let h = lens_dps_new(5.0, 0.0);
        lens_dps_add_sample(h, LensDamageSample { time: 0.0, amount: 42.0, is_incoming: 1, is_crit: 0 });
        lens_dps_add_sample(h, LensDamageSample { time: 0.0, amount: 10.0, is_incoming: 0, is_crit: 1 });
        lens_dps_add_sample(h, LensDamageSample { time: 0.0, amount: 10.0, is_incoming: 0, is_crit: 0 });

        let mut snap = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(h, 0.0, &mut snap), LENS_OK);
        assert_eq!(snap.max_incoming_damage, 42.0);
        assert_eq!(snap.hit_count, 2, "the incoming sample must not count as a hit");
        assert_eq!(snap.crit_count, 1);
        assert_eq!(snap.crit_rate, 0.5);
        lens_dps_free(h);
    }

    #[test]
    fn dps_ffi_survives_null_handles_and_absurd_values() {
        // Every mutator must tolerate null rather than dereference it.
        lens_dps_add_sample(std::ptr::null_mut(), sample(0.0, 1.0));
        lens_dps_trim(std::ptr::null_mut(), 0.0);
        lens_dps_reset(std::ptr::null_mut(), 0.0);
        lens_dps_free(std::ptr::null_mut());
        assert_eq!(lens_dps_timeline_len(std::ptr::null_mut()), i64::from(LENS_ERR_NULL));

        let mut snap = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(std::ptr::null_mut(), 0.0, &mut snap), LENS_ERR_NULL);
        // Zeroed before validation, so the caller can always read it — but note that a
        // *successful* snapshot of a live handle reports the configured window, so compare
        // against `default()` only on this error path.
        assert_eq!(snap, LensDpsSnapshot::default(), "zeroed even on error");

        let h = lens_dps_new(5.0, 0.0);
        assert_eq!(lens_dps_snapshot(h, 0.0, std::ptr::null_mut()), LENS_ERR_NULL);

        // A zero-length window must not divide by zero.
        lens_dps_add_sample(h, sample(0.0, 100.0));
        let mut zero_win = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(h, 0.0, &mut zero_win), LENS_OK);
        assert!(zero_win.current_dps.is_finite());
        lens_dps_free(h);

        // Dropping only what the original drops. LEns's check is `if (!(Amount > 0)) return;`
        // (IL: cgt.un against 0, then ceq), so:
        //   NaN  -> `NaN > 0` is false  -> rejected
        //   -5.0 -> rejected
        //   +inf -> `inf > 0` is TRUE   -> ACCEPTED, exactly as in the original
        // This test pins that fidelity rather than inventing a "saner" rule.
        let h2 = lens_dps_new(5.0, 0.0);
        lens_dps_add_sample(h2, sample(0.0, f64::NAN));
        lens_dps_add_sample(h2, sample(0.0, -5.0));
        let mut s2 = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(h2, 0.0, &mut s2), LENS_OK);
        assert_eq!(s2.hit_count, 0, "NaN and negatives are rejected");
        assert_eq!(s2.total_damage, 0.0);

        lens_dps_add_sample(h2, sample(0.0, f64::INFINITY));
        let mut s3 = LensDpsSnapshot::default();
        assert_eq!(lens_dps_snapshot(h2, 0.0, &mut s3), LENS_OK);
        assert_eq!(s3.hit_count, 1, "+inf passes `Amount > 0`, same as the original");
        assert!(s3.total_damage.is_infinite());
        lens_dps_free(h2);
    }

    #[test]
    fn dps_ffi_struct_layout_matches_the_managed_declaration() {
        use std::mem::{align_of, size_of};
        // LensDamageSample: f32(4) + pad(4) + f64(8) + u8 + u8 + pad(6) = 24, align 8.
        assert_eq!(size_of::<LensDamageSample>(), 24);
        assert_eq!(align_of::<LensDamageSample>(), 8);
        // LensDpsSnapshot: 2*f64 + f64 + i32 + i32 + pad(0) + f64 + f32 + pad(4) + f64
        //                = 8+8+8+4+4+8+4+4+8 = 56, align 8.
        // The C# side must declare the same size; `tools/LoadProbe`-style verification of
        // the managed mirror is the caller's responsibility.
        assert_eq!(size_of::<LensDpsSnapshot>(), 56);
        assert_eq!(align_of::<LensDpsSnapshot>(), 8);
    }

    #[test]
    fn parser_tolerates_a_table_of_only_comments() {
        assert_eq!(parse("# only\n#comments\n\n"), 0);
        let handle = new_table("# only\n");
        assert!(!handle.is_null(), "an empty-but-valid table is still a table");
        assert_eq!(lens_affix_table_len(handle), 0);
        assert!(lens_affix_table_name_for_id(handle, 0).is_null());
        lens_affix_table_free(handle);
    }
}
