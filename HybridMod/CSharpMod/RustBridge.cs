// RustBridge.cs - the managed half of the C#-host + Rust-core pair.

// Nullable annotations are off because this project is compiled with `csc -nostdlib+` against the
// net6.0 reference pack only, which does not carry System.Runtime.CompilerServices.NullableAttribute.
// The warnings are still available in a normal SDK build; they are simply not emitted here.
#nullable disable

//
// This file contains *only* the interop layer: P/Invoke declarations, the native-library
// resolver, and thin managed wrappers. It deliberately has no MelonLoader dependency so the
// exact same code can be exercised by the standalone test harness
// (contrib/HybridMod/CSharpHarness) without launching the game. The MelonMod entry point lives
// in HybridMod.cs and simply calls into here.
//
// Layout contract
// ---------------
// The two structs below mirror the `#[repr(C)]` structs in RustCore/src/lib.rs. Their sizes are
// asserted by Rust-side tests (24 and 56 bytes) and re-checked here in
// `RustBridge.ValidateAbi()` so a mismatch is reported instead of silently corrupting memory.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HybridMod.Interop
{
    /// <summary>Mirror of Rust `LensDamageSample` (24 bytes, align 8).</summary>
    /// <remarks>
    /// Field order matters: `time` (4) + padding (4) + `amount` (8) + two flags (2) + padding (6).
    /// Do not reorder to "save space" — that would change the offsets the native side reads.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct LensDamageSample
    {
        public float Time;
        public double Amount;
        [MarshalAs(UnmanagedType.U1)] public bool IsIncoming;
        [MarshalAs(UnmanagedType.U1)] public bool IsCrit;

        public static LensDamageSample Outgoing(float time, double amount, bool isCrit = false)
            => new LensDamageSample { Time = time, Amount = amount, IsIncoming = false, IsCrit = isCrit };

        public static LensDamageSample Incoming(float time, double amount)
            => new LensDamageSample { Time = time, Amount = amount, IsIncoming = true, IsCrit = false };
    }

    /// <summary>Mirror of Rust `LensDpsSnapshot` (56 bytes, align 8).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LensDpsSnapshot
    {
        public double CurrentDps;
        public double TotalDamage;
        public double MaxIncomingDamage;
        public int HitCount;
        public int CritCount;
        public double CritRate;
        public float WindowSeconds;
        public double CombatDurationSeconds;

        public override string ToString() =>
            $"dps={CurrentDps:F1} total={TotalDamage:F0} hits={HitCount} crits={CritCount} " +
            $"critRate={CritRate:P1} window={WindowSeconds:F1}s combat={CombatDurationSeconds:F2}s " +
            $"maxIncoming={MaxIncomingDamage:F0}";
    }

    /// <summary>
    /// Owns the native library handle and exposes the C ABI as managed methods.
    ///
    /// A process may only have one instance: the DLL-import resolver is global, and
    /// re-registering it would be pointless (and would leak a handle).
    /// </summary>
    public static class RustBridge
    {
        public const string LibraryName = "lens_core";

        /// <summary>Status code returned by the native side on success.</summary>
        public const int OK = 0;
        public const int ErrNull = -1;
        public const int ErrLen = -2;
        public const int ErrUtf8 = -3;
        public const int ErrShape = -4;

        private static IntPtr _handle = IntPtr.Zero;
        private static bool _resolverInstalled;
        private static string _resolvedPath;

        /// <summary>Absolute path the library was loaded from, or null when not loaded.</summary>
        public static string ResolvedPath => _resolvedPath;

        public static bool IsLoaded => _handle != IntPtr.Zero;

        // ---- P/Invoke surface (bare `lens_core`, redirected by the resolver) ----------------

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lens_core_version();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lens_core_add(int a, int b);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong lens_core_max_input_bytes();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long lens_core_parse_affix_tsv(byte[] ptr, UIntPtr len);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lens_affix_table_new(byte[] ptr, UIntPtr len);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lens_affix_table_free(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long lens_affix_table_len(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long lens_affix_table_skipped(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lens_affix_table_name_for_id(IntPtr handle, uint id);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lens_affix_table_id_for_name(IntPtr handle, byte[] ptr, UIntPtr len, out uint outId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lens_dps_new(float windowSeconds, float epoch);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lens_dps_free(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lens_dps_add_sample(IntPtr handle, LensDamageSample sample);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lens_dps_trim(IntPtr handle, float now);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lens_dps_snapshot(IntPtr handle, float now, out LensDpsSnapshot outSnapshot);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lens_dps_reset(IntPtr handle, float now);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long lens_dps_timeline_len(IntPtr handle);

        // ---- loading -----------------------------------------------------------------------

        /// <summary>
        /// Loads `lens_core.dll` from <paramref name="nativeDirectory"/> and installs the
        /// DLL-import resolver so the bare-name P/Invokes above resolve to it.
        /// </summary>
        /// <remarks>
        /// The resolver is required because a MelonLoader mod is loaded from memory, so the
        /// directory containing the mod is not on the native search path. Resolving explicitly
        /// also means a missing or wrong-bitness DLL produces a clear message here rather than an
        /// opaque `DllNotFoundException` at the first call.
        /// </remarks>
        /// <returns>null on success, otherwise a human-readable reason.</returns>
        public static string Initialize(string nativeDirectory)
        {
            if (IsLoaded) return null;

            if (string.IsNullOrEmpty(nativeDirectory))
                return "nativeDirectory was null or empty";
            if (!Directory.Exists(nativeDirectory))
                return $"native directory not found: {nativeDirectory}";

            string candidate = Path.Combine(nativeDirectory, LibraryName + ".dll");
            string archNote = Environment.Is64BitProcess ? "x64" : "x86";

            if (!File.Exists(candidate))
                return $"native library not found: {candidate} (process is {archNote})";

            try
            {
                _handle = NativeLibrary.Load(candidate);
            }
            catch (BadImageFormatException ex)
            {
                // Almost always a bitness mismatch: the Rust cdylib defaults to
                // x86_64-pc-windows-msvc, which cannot load into a 32-bit process (or vice versa).
                return $"bitness/format mismatch loading {candidate}: {ex.Message} " +
                       $"(process is {archNote}; rebuild the crate for the matching target)";
            }
            catch (Exception ex)
            {
                return $"failed to load {candidate}: {ex.GetType().Name}: {ex.Message}";
            }

            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(RustBridge).Assembly,
                    (name, _, _) =>
                        name == LibraryName && _handle != IntPtr.Zero ? _handle : IntPtr.Zero);
                _resolverInstalled = true;
            }

            _resolvedPath = candidate;
            return null;
        }

        /// <summary>Unloads the library. Only used by the test harness.</summary>
        public static void Shutdown()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeLibrary.Free(_handle);
                _handle = IntPtr.Zero;
                _resolvedPath = null;
            }
        }

        // ---- ABI self-check ----------------------------------------------------------------

        /// <summary>
        /// Verifies the managed struct layouts and calling convention against the native side.
        /// </summary>
        /// <remarks>
        /// Struct-size mismatch is the classic way an FFI bridge silently corrupts memory, so it
        /// is checked explicitly rather than assumed. `lens_core_add` doubles as a calling
        /// convention probe: under the wrong convention (stdcall vs cdecl) the result is garbage.
        /// </remarks>
        public static string ValidateAbi()
        {
            if (!IsLoaded) return "library not loaded";

            int sampleSize = Marshal.SizeOf<LensDamageSample>();
            if (sampleSize != 24)
                return $"LensDamageSample is {sampleSize} bytes, native expects 24";

            int snapshotSize = Marshal.SizeOf<LensDpsSnapshot>();
            if (snapshotSize != 56)
                return $"LensDpsSnapshot is {snapshotSize} bytes, native expects 56";

            int sum = Add(2000, 37);
            if (sum != 2037)
                return $"calling convention probe failed: lens_core_add(2000,37) returned {sum}, expected 2037";

            return null;
        }

        // ---- managed wrappers --------------------------------------------------------------

        /// <summary>Native core version string, e.g. "lens_core/0.1.0".</summary>
        public static string Version()
        {
            IntPtr p = lens_core_version();
            return p == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUTF8(p) ?? "(undecodable)";
        }

        public static int Add(int a, int b) => lens_core_add(a, b);

        public static ulong MaxInputBytes() => lens_core_max_input_bytes();

        /// <summary>Counts data records in a TSV byte buffer without building a table.</summary>
        public static long CountAffixRecords(byte[] tsv)
        {
            if (tsv == null) return ErrNull;
            return lens_core_parse_affix_tsv(tsv, (UIntPtr)tsv.Length);
        }

        /// <summary>Parses a TSV byte buffer into a queryable table, or null on failure.</summary>
        public static AffixTable LoadAffixTable(byte[] tsv)
        {
            if (tsv == null || tsv.Length == 0) return null;
            IntPtr h = lens_affix_table_new(tsv, (UIntPtr)tsv.Length);
            return h == IntPtr.Zero ? null : new AffixTable(h);
        }

        internal static long TableLen(IntPtr h) => lens_affix_table_len(h);
        internal static long TableSkipped(IntPtr h) => lens_affix_table_skipped(h);
        internal static IntPtr TableNameForId(IntPtr h, uint id) => lens_affix_table_name_for_id(h, id);
        internal static void TableFree(IntPtr h) => lens_affix_table_free(h);
        internal static int TableIdForName(IntPtr h, byte[] key, out uint id) =>
            lens_affix_table_id_for_name(h, key, (UIntPtr)key.Length, out id);

        /// <summary>Creates a DPS calculator, or null if the native allocation failed.</summary>
        public static DpsCalculator CreateDps(float windowSeconds, float epoch)
        {
            IntPtr h = lens_dps_new(windowSeconds, epoch);
            return h == IntPtr.Zero ? null : new DpsCalculator(h);
        }

        internal static void DpsAdd(IntPtr h, LensDamageSample s) => lens_dps_add_sample(h, s);
        internal static void DpsTrim(IntPtr h, float now) => lens_dps_trim(h, now);
        internal static int DpsSnapshot(IntPtr h, float now, out LensDpsSnapshot snap) =>
            lens_dps_snapshot(h, now, out snap);
        internal static void DpsReset(IntPtr h, float now) => lens_dps_reset(h, now);
        internal static long DpsTimelineLen(IntPtr h) => lens_dps_timeline_len(h);
        internal static void DpsFree(IntPtr h) => lens_dps_free(h);
    }

    /// <summary>Managed handle over a native affix table. Dispose to release it.</summary>
    public sealed class AffixTable : IDisposable
    {
        private IntPtr _handle;

        internal AffixTable(IntPtr handle) { _handle = handle; }

        public bool IsValid => _handle != IntPtr.Zero;

        /// <summary>Number of data records, or a negative LENS_ERR_* code.</summary>
        public long Length => _handle == IntPtr.Zero ? RustBridge.ErrNull : RustBridge.TableLen(_handle);

        /// <summary>Number of rows skipped by the parser (comments, blanks, malformed).</summary>
        public long Skipped => _handle == IntPtr.Zero ? RustBridge.ErrNull : RustBridge.TableSkipped(_handle);

        /// <summary>Abbreviation for an affix id, or null when the id is unknown.</summary>
        public string NameForId(uint id)
        {
            if (_handle == IntPtr.Zero) return null;
            IntPtr p = RustBridge.TableNameForId(_handle, id);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }

        /// <summary>
        /// Affix id for an abbreviation. Returns false when the key is unknown.
        /// </summary>
        /// <remarks>
        /// The native contract reports `OK` with id 0 for an unknown-but-well-formed key, so the
        /// wrapper turns that into an explicit false rather than leaking the sentinel.
        /// </remarks>
        public bool TryGetId(string abbreviation, out uint id)
        {
            id = 0;
            if (_handle == IntPtr.Zero || string.IsNullOrEmpty(abbreviation)) return false;

            byte[] key = System.Text.Encoding.UTF8.GetBytes(abbreviation);
            int rc = RustBridge.TableIdForName(_handle, key, out uint raw);
            if (rc != RustBridge.OK) return false;

            id = raw;
            return raw != 0;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                RustBridge.TableFree(_handle);
                _handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~AffixTable() => Dispose();
    }

    /// <summary>Managed handle over a native DPS calculator. Dispose to release it.</summary>
    public sealed class DpsCalculator : IDisposable
    {
        private IntPtr _handle;

        internal DpsCalculator(IntPtr handle) { _handle = handle; }

        public bool IsValid => _handle != IntPtr.Zero;

        /// <summary>Feeds one damage event. Non-positive and NaN amounts are ignored natively.</summary>
        public void AddSample(float time, double amount, bool isIncoming = false, bool isCrit = false)
        {
            if (_handle == IntPtr.Zero) return;
            RustBridge.DpsAdd(_handle, new LensDamageSample
            {
                Time = time,
                Amount = amount,
                IsIncoming = isIncoming,
                IsCrit = isCrit,
            });
        }

        /// <summary>Drops timeline points that have aged out of the window.</summary>
        public void Trim(float now)
        {
            if (_handle != IntPtr.Zero) RustBridge.DpsTrim(_handle, now);
        }

        /// <summary>Reads a snapshot. Returns false when the native call rejected the arguments.</summary>
        public bool TrySnapshot(float now, out LensDpsSnapshot snapshot)
        {
            if (_handle == IntPtr.Zero)
            {
                snapshot = default;
                return false;
            }
            return RustBridge.DpsSnapshot(_handle, now, out snapshot) == RustBridge.OK;
        }

        /// <summary>Clears counters and rebases the session epoch to <paramref name="now"/>.</summary>
        public void Reset(float now)
        {
            if (_handle != IntPtr.Zero) RustBridge.DpsReset(_handle, now);
        }

        /// <summary>Points currently on the timeline, or a negative LENS_ERR_* code.</summary>
        public long TimelineLength =>
            _handle == IntPtr.Zero ? RustBridge.ErrNull : RustBridge.DpsTimelineLen(_handle);

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                RustBridge.DpsFree(_handle);
                _handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~DpsCalculator() => Dispose();
    }
}
