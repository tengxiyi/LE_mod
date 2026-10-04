// DpsReference.cs - a literal transcription of LEns's `DpsStatsCalculator`, in C#.
//
// Why a second implementation exists
// ----------------------------------
// `cargo test` proves the Rust core is self-consistent. It cannot prove the Rust core agrees with
// LEns, because the expected values in those tests were derived from the same reading of the IL that
// produced the Rust code. If that reading was wrong, both are wrong together and every test passes.
//
// This file breaks that circularity. It is transcribed field-by-field and statement-by-statement
// from the IL of `LastEpochMods.Template.Features.Dps.DpsStatsCalculator` (read with
// tools/DumpIl), keeping the original's structure visible rather than idiomatic:
//
//     fields            _windowSeconds, _totalDamage, _maxIncomingDamage, _hitCount, _critCount,
//                       _firstDamageTime, _lastDamageTime
//     AddSample         reject !(amount > 0); incoming updates only max; else enqueue + totals
//     TrimExpiredPoints while front age >= window, dequeue
//     TriangularWeightedSum  weight = 1 - age/window, skip age<0 or age>=window
//     CurrentDps        2.0 * sum / window
//     GetSnapshot       critRate guarded; combat duration = Max(0, last - first) or 0
//
// Deliberately NOT rewritten to be "nicer": being a faithful mirror is the entire point, because the
// harness compares it against the Rust core over real recorded data.

using System;
using System.Collections.Generic;

namespace HybridMod.Reference
{
    /// <summary>One damage event, mirroring LEns's `DamageSample` fields the calculator reads.</summary>
    public struct RefDamageSample
    {
        public float Time;
        public double Amount;
        public bool IsIncoming;
        public bool IsCrit;
    }

    /// <summary>Mirrors LEns's `DpsSnapshot`.</summary>
    public struct RefDpsSnapshot
    {
        public double CurrentDps;
        public double TotalDamage;
        public double MaxIncomingDamage;
        public int HitCount;
        public int CritCount;
        public double CritRate;
        public float WindowSeconds;
        public double CombatDurationSeconds;
    }

    /// <summary>Literal transcription of LEns's `DpsStatsCalculator`.</summary>
    public sealed class DpsReference
    {
        private readonly float _windowSeconds;
        private readonly Queue<(float Time, double Amount)> _timeline = new();
        private double _totalDamage;
        private double _maxIncomingDamage;
        private int _hitCount;
        private int _critCount;
        private float _firstDamageTime;
        private float _lastDamageTime;

        /// <summary>
        /// The original takes the epoch from `Time.realtimeSinceStartup` and exposes
        /// `Now => realtimeSinceStartup - _sessionEpoch`. The caller supplies `now` here for the same
        /// reason the Rust port does: to keep this testable. Only the clock source differs.
        /// </summary>
        public DpsReference(float windowSeconds)
        {
            _windowSeconds = windowSeconds;
            _firstDamageTime = -1f;   // set by the original's .ctor
        }

        /// <summary>Literal `AddSample(DamageSample sample)`.</summary>
        public void AddSample(RefDamageSample sample)
        {
            // if (!(sample.Amount > 0)) return;
            if (!(sample.Amount > 0)) return;

            if (sample.IsIncoming)
            {
                if (sample.Amount > _maxIncomingDamage) _maxIncomingDamage = sample.Amount;
                return;
            }

            float t = sample.Time;
            _timeline.Enqueue((t, sample.Amount));

            if (_firstDamageTime < 0f) _firstDamageTime = t;
            _lastDamageTime = t;

            _totalDamage += sample.Amount;
            _hitCount++;
            if (sample.IsCrit) _critCount++;
        }

        /// <summary>Literal `TrimExpiredPoints()`: a `while`, not an `if`.</summary>
        public void TrimExpiredPoints(float now)
        {
            while (_timeline.Count > 0)
            {
                var front = _timeline.Peek();
                // if ((now - p.Time) < window) break;
                if ((now - front.Time) < _windowSeconds) break;
                _timeline.Dequeue();
            }
        }

        /// <summary>Literal `TriangularWeightedSum()`.</summary>
        public double TriangularWeightedSum(float now)
        {
            if (_timeline.Count == 0 || !(_windowSeconds > 0f)) return 0.0;

            double sum = 0.0;
            double k = 1.0 / _windowSeconds;          // 1.0 / (double)window

            foreach (var p in _timeline)
            {
                float age = now - p.Time;
                if (age < 0f || !(age < _windowSeconds)) continue;
                double w = 1.0 - age * k;
                sum += p.Amount * w;
            }
            return sum;
        }

        /// <summary>Literal `get_CurrentDps`.</summary>
        public double CurrentDps(float now) => 2.0 * TriangularWeightedSum(now) / _windowSeconds;

        /// <summary>Literal `GetSnapshot()`.</summary>
        public RefDpsSnapshot GetSnapshot(float now)
        {
            return new RefDpsSnapshot
            {
                CurrentDps = CurrentDps(now),
                TotalDamage = _totalDamage,
                MaxIncomingDamage = _maxIncomingDamage,
                HitCount = _hitCount,
                CritCount = _critCount,
                CritRate = _hitCount > 0 ? (double)_critCount / _hitCount : 0.0,
                WindowSeconds = _windowSeconds,
                CombatDurationSeconds = _firstDamageTime < 0f
                    ? 0.0
                    : Math.Max(0.0, _lastDamageTime - _firstDamageTime),
            };
        }

        /// <summary>Literal `Reset()`; `now` stands in for the game clock.</summary>
        public void Reset()
        {
            _timeline.Clear();
            _totalDamage = 0.0;
            _maxIncomingDamage = 0.0;
            _hitCount = 0;
            _critCount = 0;
            _firstDamageTime = -1f;
            _lastDamageTime = 0f;
        }

        public int TimelineLength => _timeline.Count;
    }
}
