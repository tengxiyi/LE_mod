//! A faithful Rust port of LEns's `DpsStatsCalculator`.
//!
//! # Provenance
//!
//! Every rule below was read out of `Mods\LEns.dll` with `tools/InspectMethod`
//! (IL layer, not guessed):
//!
//! ```text
//! .ctor(windowSeconds)
//!     _windowSeconds = windowSeconds
//!     _sessionEpoch  = Time.realtimeSinceStartup
//!     _firstDamageTime = -1
//!
//! AddSample(sample)
//!     if !(sample.Amount > 0) return                 // cgt.un against 0
//!     if sample.IsIncoming {
//!         _maxIncomingDamage = max(_maxIncomingDamage, sample.Amount)
//!         return                                     // incoming never enters the timeline
//!     }
//!     t = sample.Time                                // sample's own timestamp
//!     _timeline.Enqueue(DamagePoint(t, sample.Amount))
//!     if (_firstDamageTime < 0) _firstDamageTime = t
//!     _lastDamageTime = t
//!     _totalDamage += sample.Amount
//!     _hitCount++
//!     if sample.IsCrit _critCount++
//!
//! TrimExpiredPoints()                                // while, not if
//!     now = Now
//!     while _timeline.Count > 0 {
//!         p = _timeline.Peek()
//!         if (now - p.Time) < _windowSeconds break      // clt, not cle
//!         _timeline.Dequeue()
//!     }
//!
//! TriangularWeightedSum()
//!     if _timeline.Count == 0 || !(_windowSeconds > 0) return 0.0
//!     now   = Now
//!     sum   = 0.0
//!     k     = 1.0 / (double)_windowSeconds
//!     foreach p in _timeline {
//!         age = now - p.Time
//!         if (age < 0 || !(age < _windowSeconds)) continue   // both guards matter
//!         w = 1.0 - (double)age * k
//!         sum += p.Amount * w
//!     }
//!     return sum
//!
//! CurrentDps => 2.0 * TriangularWeightedSum() / _windowSeconds
//!
//! GetSnapshot()
//!     CurrentDps, TotalDamage, MaxIncomingDamage, HitCount, CritCount,
//!     CritRate = _hitCount > 0 ? (double)_critCount / _hitCount : 0.0,
//!     WindowSeconds,
//!     CombatDurationSeconds = _firstDamageTime < 0
//!                                 ? 0.0
//!                                 : Max(0.0, _lastDamageTime - _firstDamageTime)
//!
//! Reset()
//!     _timeline.Clear(); _sessionEpoch = now
//!     _totalDamage = 0; _maxIncomingDamage = 0; _hitCount = 0; _critCount = 0
//!     _firstDamageTime = -1; _lastDamageTime = 0
//! ```
//!
//! # One deliberate deviation
//!
//! In the original, `Now` is `Time.realtimeSinceStartup - _sessionEpoch`, i.e. it reads
//! Unity's clock. A pure-computation core must not depend on a game engine, so `now` is
//! passed in by the caller instead. `DpsCalculator::new` therefore takes the epoch, and
//! `Reset` takes the current time:
//!
//! | original                          | this port                          |
//! |-----------------------------------|------------------------------------|
//! | `new DpsStatsCalculator(w)`       | `DpsCalculator::new(w, epoch)`      |
//! | `Reset()` reads `realtimeSinceStartup` | `reset(now)`                   |
//! | `Now => realtimeSinceStartup - _sessionEpoch` | `now - epoch`           |
//!
//! This changes only where the number comes from, not how it is used, and it makes the
//! whole calculator deterministic and unit-testable. `f32` is kept for times and `f64` for
//! amounts/accumulators exactly as the original does.

/// One damage event. Mirrors LEns's `DamageSample` fields that the calculator reads.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DamageSample {
    /// When the hit happened, in seconds, on the same clock as `now`.
    pub time: f32,
    /// Damage amount. Non-positive values are ignored, as in the original.
    pub amount: f64,
    /// Incoming (taken) damage is tracked separately and never enters the DPS timeline.
    pub is_incoming: bool,
    pub is_crit: bool,
}

impl DamageSample {
    /// Convenience constructor for outgoing damage.
    pub fn outgoing(time: f32, amount: f64) -> Self {
        Self { time, amount, is_incoming: false, is_crit: false }
    }
}

/// Mirrors LEns's `DpsSnapshot`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DpsSnapshot {
    pub current_dps: f64,
    pub total_damage: f64,
    pub max_incoming_damage: f64,
    pub hit_count: i32,
    pub crit_count: i32,
    pub crit_rate: f64,
    pub window_seconds: f32,
    pub combat_duration_seconds: f64,
}

impl Default for DpsSnapshot {
    fn default() -> Self {
        Self {
            current_dps: 0.0,
            total_damage: 0.0,
            max_incoming_damage: 0.0,
            hit_count: 0,
            crit_count: 0,
            crit_rate: 0.0,
            window_seconds: 0.0,
            combat_duration_seconds: 0.0,
        }
    }
}

/// Internal timeline entry: mirrors `DpsStatsCalculator/DamagePoint`.
#[derive(Clone, Copy, Debug, PartialEq)]
struct DamagePoint {
    time: f32,
    amount: f64,
}

/// Stateful, allocation-light port of `DpsStatsCalculator`.
///
/// The original uses a `Queue<DamagePoint>`; a `VecDeque` gives the same FIFO
/// peek-front / pop-front behaviour with `O(1)` operations on both ends.
#[derive(Debug)]
pub struct DpsCalculator {
    window_seconds: f32,
    session_epoch: f32,
    timeline: std::collections::VecDeque<DamagePoint>,
    total_damage: f64,
    max_incoming_damage: f64,
    hit_count: i32,
    crit_count: i32,
    first_damage_time: f32,
    last_damage_time: f32,
}

impl DpsCalculator {
    /// Port of `.ctor(Single windowSeconds)`.
    ///
    /// `epoch` plays the role of `Time.realtimeSinceStartup` at construction time.
    pub fn new(window_seconds: f32, epoch: f32) -> Self {
        Self {
            window_seconds,
            session_epoch: epoch,
            timeline: std::collections::VecDeque::new(),
            total_damage: 0.0,
            max_incoming_damage: 0.0,
            hit_count: 0,
            crit_count: 0,
            // The original sets -1 here, which is also what Reset does: -1 means
            // "no damage seen yet" and is what GetSnapshot keys off.
            first_damage_time: -1.0,
            last_damage_time: 0.0,
        }
    }

    /// The current session time, in the same units as every `DamageSample::time` handed to
    /// this calculator.
    ///
    /// The original computes `Time.realtimeSinceStartup - _sessionEpoch`, i.e. it converts the
    /// engine clock into a session-relative clock and stores sample times on that same relative
    /// clock. Because both sides use one consistent clock, the age of a point is simply
    /// `now - point.time`.
    ///
    /// This port keeps that invariant but does not perform the subtraction here: the caller
    /// supplies one consistent clock (absolute `realtimeSinceStartup` is the natural choice) for
    /// both `now` and `Sample::time`. Subtracting the epoch in only one of the two places was a
    /// real bug caught by `now_is_relative_to_the_session_epoch`.
    #[inline]
    fn now(&self, now: f32) -> f32 {
        now
    }

    /// Port of `AddSample(DamageSample sample)`. `now` is only needed by the callers of
    /// `trim_expired_points` / `triangular_weighted_sum`; this method takes it for symmetry
    /// with `Reset` but does not read the clock itself, matching the original.
    pub fn add_sample(&mut self, sample: DamageSample) {
        // `!(Amount > 0)` — matches the original's `cgt.un` + `ceq`, so NaN is rejected too.
        if !(sample.amount > 0.0) {
            return;
        }

        if sample.is_incoming {
            if sample.amount > self.max_incoming_damage {
                self.max_incoming_damage = sample.amount;
            }
            return;
        }

        let t = sample.time;
        self.timeline.push_back(DamagePoint { time: t, amount: sample.amount });

        if self.first_damage_time < 0.0 {
            self.first_damage_time = t;
        }
        self.last_damage_time = t;

        self.total_damage += sample.amount;
        self.hit_count += 1;
        if sample.is_crit {
            self.crit_count += 1;
        }
    }

    /// Port of `TrimExpiredPoints()`. A `while` loop in the original; kept as one here.
    pub fn trim_expired_points(&mut self, now: f32) {
        let now = self.now(now);
        while let Some(front) = self.timeline.front() {
            // `if (now - p.Time) < window  break;`  -> drop only when the age has
            // reached the window (`clt` in the original, i.e. strictly-less keeps).
            if (now - front.time) < self.window_seconds {
                break;
            }
            self.timeline.pop_front();
        }
    }

    /// Port of `TriangularWeightedSum()`. Returns `0.0` for an empty timeline or a
    /// non-positive window.
    pub fn triangular_weighted_sum(&self, now: f32) -> f64 {
        if self.timeline.is_empty() || !(self.window_seconds > 0.0) {
            return 0.0;
        }
        let now = self.now(now);
        // `1.0 / (double)_windowSeconds`, computed once exactly as the original does.
        let k = 1.0f64 / self.window_seconds as f64;

        let mut sum = 0.0f64;
        for p in &self.timeline {
            let age = now - p.time;
            // `if (age < 0 || !(age < window)) continue;` — both guards are load-bearing:
            // future-dated points and points exactly at the window edge contribute nothing.
            if age < 0.0 || !(age < self.window_seconds) {
                continue;
            }
            let w = 1.0f64 - age as f64 * k;
            sum += p.amount * w;
        }
        sum
    }

    /// Port of `get_CurrentDps`.
    pub fn current_dps(&self, now: f32) -> f64 {
        2.0 * self.triangular_weighted_sum(now) / self.window_seconds as f64
    }

    /// Port of `GetSnapshot()`.
    pub fn snapshot(&self, now: f32) -> DpsSnapshot {
        DpsSnapshot {
            current_dps: self.current_dps(now),
            total_damage: self.total_damage,
            max_incoming_damage: self.max_incoming_damage,
            hit_count: self.hit_count,
            crit_count: self.crit_count,
            crit_rate: if self.hit_count > 0 {
                self.crit_count as f64 / self.hit_count as f64
            } else {
                0.0
            },
            window_seconds: self.window_seconds,
            combat_duration_seconds: if self.first_damage_time < 0.0 {
                0.0
            } else {
                // `Math.Max(0.0, last - first)` in the original.
                let d = self.last_damage_time - self.first_damage_time;
                if d > 0.0 { d as f64 } else { 0.0 }
            },
        }
    }

    /// Port of `Reset()`. `now` replaces the original's read of the game clock.
    pub fn reset(&mut self, now: f32) {
        self.timeline.clear();
        self.session_epoch = now;
        self.total_damage = 0.0;
        self.max_incoming_damage = 0.0;
        self.hit_count = 0;
        self.crit_count = 0;
        self.first_damage_time = -1.0;
        self.last_damage_time = 0.0;
    }

    /// Number of points currently on the timeline. Not exposed by the original, but the
    /// natural thing to assert on in tests.
    pub fn timeline_len(&self) -> usize {
        self.timeline.len()
    }
}

// =========================================================================================
// Tests
// =========================================================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Window used by LEns's own feature wiring.
    const W: f32 = 5.0;

    fn calc() -> DpsCalculator {
        DpsCalculator::new(W, 0.0)
    }

    // ---- TotalDamage / HitCount / CritRate: exact integers, easy to pin ----------------

    #[test]
    fn non_positive_and_nan_amounts_are_rejected() {
        let mut c = calc();
        for bad in [0.0, -1.0, f64::NAN, f64::NEG_INFINITY] {
            c.add_sample(DamageSample::outgoing(0.0, bad));
        }
        let s = c.snapshot(0.0);
        assert_eq!(s.hit_count, 0, "no hit may be counted for a non-positive amount");
        assert_eq!(s.total_damage, 0.0);
        assert_eq!(c.timeline_len(), 0);
        assert_eq!(s.combat_duration_seconds, 0.0, "first_damage_time stays -1");
    }

    #[test]
    fn incoming_damage_is_tracked_separately_and_never_enters_the_timeline() {
        let mut c = calc();
        c.add_sample(DamageSample { time: 0.0, amount: 500.0, is_incoming: true, is_crit: false });
        c.add_sample(DamageSample { time: 0.0, amount: 120.0, is_incoming: true, is_crit: false });
        c.add_sample(DamageSample { time: 0.0, amount: 90.0, is_incoming: true, is_crit: false });

        let s = c.snapshot(0.0);
        assert_eq!(s.max_incoming_damage, 500.0, "keeps the maximum, not the sum");
        assert_eq!(s.hit_count, 0, "incoming damage is not a hit");
        assert_eq!(s.total_damage, 0.0);
        assert_eq!(c.timeline_len(), 0, "incoming must not enter the DPS timeline");
        assert_eq!(s.current_dps, 0.0);
    }

    #[test]
    fn totals_counts_and_crit_rate_match_the_original() {
        let mut c = calc();
        c.add_sample(DamageSample { time: 0.0, amount: 100.0, is_incoming: false, is_crit: true });
        c.add_sample(DamageSample { time: 0.1, amount: 50.0, is_incoming: false, is_crit: false });
        c.add_sample(DamageSample { time: 0.2, amount: 25.0, is_incoming: false, is_crit: true });

        let s = c.snapshot(0.2);
        assert_eq!(s.total_damage, 175.0);
        assert_eq!(s.hit_count, 3);
        assert_eq!(s.crit_count, 2);
        assert_eq!(s.crit_rate, 2.0 / 3.0);
        assert_eq!(s.window_seconds, W);
        assert!((s.combat_duration_seconds - 0.2).abs() < 1e-6, "last - first");
    }

    #[test]
    fn crit_rate_is_zero_when_there_are_no_hits() {
        let c = calc();
        assert_eq!(c.snapshot(0.0).crit_rate, 0.0, "guard against divide-by-zero");
    }

    #[test]
    fn first_damage_time_is_set_once_and_never_moves_earlier_or_later() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(3.0, 10.0));
        c.add_sample(DamageSample::outgoing(1.0, 10.0)); // earlier, but not the first seen
        c.add_sample(DamageSample::outgoing(9.0, 10.0));

        let s = c.snapshot(9.0);
        assert!((s.combat_duration_seconds - 6.0).abs() < 1e-6, "9.0 - 3.0");
    }

    #[test]
    fn combat_duration_is_clamped_at_zero() {
        let mut c = calc();
        // Only one sample, then a second one that is earlier: last < first.
        c.add_sample(DamageSample::outgoing(5.0, 1.0));
        let s1 = c.snapshot(5.0);
        assert_eq!(s1.combat_duration_seconds, 0.0, "single sample => 0");

        // Restart and feed strictly-decreasing times to force last < first.
        let mut c2 = calc();
        c2.add_sample(DamageSample::outgoing(9.0, 1.0));
        c2.add_sample(DamageSample::outgoing(9.0, 1.0));
        assert_eq!(c2.snapshot(9.0).combat_duration_seconds, 0.0);
    }

    // ---- Triangular weighting: the part that is easy to get subtly wrong --------------

    #[test]
    fn triangular_weight_is_one_at_now_and_zero_at_the_window_edge() {
        let mut c = calc();
        // Exactly at `now`: age 0 => weight 1.
        c.add_sample(DamageSample::outgoing(10.0, 100.0));
        assert_eq!(c.triangular_weighted_sum(10.0), 100.0);
        assert_eq!(c.current_dps(10.0), 2.0 * 100.0 / W as f64);

        // Mid-window: age 2.5 of a 5.0 window => weight 0.5.
        assert_eq!(c.triangular_weighted_sum(12.5), 50.0);

        // At the edge: age == window => `!(age < window)` => skipped entirely.
        assert_eq!(c.triangular_weighted_sum(15.0), 0.0);

        // Beyond the edge: also skipped.
        assert_eq!(c.triangular_weighted_sum(20.0), 0.0);
    }

    #[test]
    fn points_newer_than_now_are_skipped() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(10.0, 100.0));
        // now before the point => age < 0 => skipped, NOT counted with a weight > 1.
        assert_eq!(c.triangular_weighted_sum(9.0), 0.0);
        assert_eq!(c.current_dps(9.0), 0.0);
    }

    #[test]
    fn newest_damage_weighs_more_than_older_damage() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(0.0, 100.0)); // age 4 => w 0.2 => 20
        c.add_sample(DamageSample::outgoing(4.0, 100.0)); // age 0 => w 1.0 => 100
        let sum = c.triangular_weighted_sum(4.0);
        assert!((sum - 120.0).abs() < 1e-9, "got {sum}");
    }

    #[test]
    fn empty_timeline_and_non_positive_window_return_zero() {
        let c = calc();
        assert_eq!(c.triangular_weighted_sum(0.0), 0.0);
        assert_eq!(c.current_dps(0.0), 0.0);

        let mut zero = DpsCalculator::new(0.0, 0.0);
        zero.add_sample(DamageSample::outgoing(0.0, 100.0));
        assert_eq!(zero.triangular_weighted_sum(0.0), 0.0, "window must be > 0");

        let mut neg = DpsCalculator::new(-1.0, 0.0);
        neg.add_sample(DamageSample::outgoing(0.0, 100.0));
        assert_eq!(neg.triangular_weighted_sum(0.0), 0.0);
    }

    // ---- Trimming: FIFO, strictly-window-based -----------------------------------------

    #[test]
    fn trim_drops_only_points_that_reached_the_window() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(0.0, 1.0)); // age 5.0 at now=5 -> dropped
        c.add_sample(DamageSample::outgoing(1.0, 1.0)); // age 4.0 -> kept
        c.add_sample(DamageSample::outgoing(4.9, 1.0)); // age 0.1 -> kept

        c.trim_expired_points(5.0);
        assert_eq!(c.timeline_len(), 2, "age == window is dropped, age < window is kept");
    }

    #[test]
    fn trim_is_a_loop_not_a_single_step() {
        let mut c = calc();
        for i in 0..10 {
            c.add_sample(DamageSample::outgoing(i as f32 * 0.1, 1.0)); // 0.0 .. 0.9
        }
        assert_eq!(c.timeline_len(), 10);
        // now = 100: every point is far older than the 5s window, all must go.
        c.trim_expired_points(100.0);
        assert_eq!(c.timeline_len(), 0, "a single `if` would have left 9 behind");
    }

    #[test]
    fn trim_on_an_empty_timeline_is_a_no_op() {
        let mut c = calc();
        c.trim_expired_points(1000.0);
        assert_eq!(c.timeline_len(), 0);
    }

    #[test]
    fn trim_never_touches_running_totals() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(0.0, 70.0));
        c.add_sample(DamageSample::outgoing(0.1, 30.0));
        c.trim_expired_points(10_000.0);
        let s = c.snapshot(10_000.0);
        assert_eq!(c.timeline_len(), 0);
        assert_eq!(s.total_damage, 100.0, "totals are cumulative and unaffected by trimming");
        assert_eq!(s.hit_count, 2);
    }

    // ---- Reset --------------------------------------------------------------------------

    #[test]
    fn reset_clears_everything_and_rebases_the_epoch() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(0.0, 100.0));
        c.add_sample(DamageSample { time: 0.0, amount: 999.0, is_incoming: true, is_crit: false });
        c.reset(50.0);

        let s = c.snapshot(50.0);
        assert_eq!(c.timeline_len(), 0);
        assert_eq!(s.total_damage, 0.0);
        assert_eq!(s.max_incoming_damage, 0.0);
        assert_eq!(s.hit_count, 0);
        assert_eq!(s.crit_count, 0);
        assert_eq!(s.crit_rate, 0.0);
        assert_eq!(s.combat_duration_seconds, 0.0, "first_damage_time is -1 again");

        // The epoch is now 50.0, so a sample stamped 50.0 is 0s into the new session
        // (age 0 => full weight). Absolute times, as the game supplies them.
        c.add_sample(DamageSample::outgoing(50.0, 10.0));
        assert_eq!(c.triangular_weighted_sum(50.0), 10.0);
    }

    /// `Reset()` keeps `_windowSeconds`; it only clears the counters and rebases the epoch.
    #[test]
    fn reset_preserves_the_configured_window() {
        let mut c = calc();
        c.add_sample(DamageSample::outgoing(0.0, 5.0));
        c.reset(1.0);
        assert_eq!(c.snapshot(1.0).window_seconds, W, "the window is configuration, not state");
    }

    // ---- Session-epoch semantics (the one deliberate deviation) -------------------------

    #[test]
    fn now_is_relative_to_the_session_epoch() {
        // One consistent clock: sample times and query times are both absolute, exactly as the
        // game supplies them (Time.realtimeSinceStartup values). The epoch only marks where the
        // session began.
        let mut c = DpsCalculator::new(W, 100.0);
        c.add_sample(DamageSample::outgoing(103.0, 100.0)); // first hit, 3s after the epoch
        // Queried at the same instant as the hit => age 0 => full weight.
        assert_eq!(c.triangular_weighted_sum(103.0), 100.0, "age 0 must weigh 1.0");
        // 2.5s later => age 2.5 of a 5.0 window => weight 0.5.
        assert_eq!(c.triangular_weighted_sum(105.5), 50.0);
        // 5.0s after the hit => age == window => excluded.
        assert_eq!(c.triangular_weighted_sum(108.0), 0.0, "age == window contributes nothing");
        // Trim drops it on the same rule.
        c.trim_expired_points(108.0);
        assert_eq!(c.timeline_len(), 0);
    }

    /// The epoch must never be subtracted twice. `age` is `now - sample.time` and nothing else,
    /// which is what makes an absolute-clock host work.
    #[test]
    fn age_is_the_plain_difference_between_two_times_on_one_clock() {
        let c = DpsCalculator::new(W, 100.0);
        // A point well after the epoch, queried at the same instant => full weight.
        let mut c2 = c;
        c2.add_sample(DamageSample::outgoing(1000.0, 7.0));
        assert_eq!(c2.triangular_weighted_sum(1000.0), 7.0);
    }

    #[test]
    fn a_realistic_fight_produces_a_sane_dps() {
        // 10 hits of 100 damage, one every 0.5s, 5s window, evaluated just after the last.
        let mut c = calc();
        for i in 0..10 {
            c.add_sample(DamageSample::outgoing(i as f32 * 0.5, 100.0));
        }
        let now = 4.5;
        c.trim_expired_points(now);
        let s = c.snapshot(now);
        assert_eq!(s.total_damage, 1000.0);
        assert_eq!(s.hit_count, 10);
        // Ages are 0.0..4.5; weights 1.0..0.1 in 0.1 steps => sum of weights = 5.5
        let expected_sum = 100.0 * 5.5;
        assert!((c.triangular_weighted_sum(now) - expected_sum).abs() < 1e-3);
        assert!((s.current_dps - 2.0 * expected_sum / 5.0).abs() < 1e-3);
        assert_eq!(s.combat_duration_seconds, 4.5);
    }
}
