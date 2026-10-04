// DamageNumberCapture.cs - capture floating combat text the way LEns proves works in this game.
//
// DATA SOURCES THAT FAILED (kept here so they are never tried again)
// -----------------------------------------------------------------
//   1. `LocalPlayer.instance.actorSync.gameplayActor.protection` — every Actor-typed member of
//      `ActorSync` reads back null; it is a netcode component holding only ActorData/ActorVisuals.
//   2. `ProtectionClass.ApplyDamage` (our own Harmony patch) — installed cleanly, never invoked.
//   3. `DamageStatsHolder.applyDamage` — the name LEns's own finder looks for; also never invoked
//      for us over 15 minutes of combat.
//   4. `UnityEngine.Object.FindObjectsOfType(ProtectionClass)` — succeeds but returns zero
//      instances even mid-combat.
//
// What IS proven: LEns (v26.0929) runs in this exact game build and reports ~2.08 clean
// damage events per second from hooking `TMP_Text` property setters. Its algorithm was
// transcribed method-by-method from LEns.dll with tools/DumpIl (class
// LastEpochMods.Template.Data.DamageTextHook) and is reproduced here 1:1. The two things the
// previous version of this file got wrong, both now fixed against the IL:
//
//   * THE SOURCE GUARD. LEns accepts only labels whose GameObject `name` equals
//     "Damage Number(Clone)" exactly (DamageTextHook::PassesSourceGuards). That — not a
//     "label is only a number" rule — is what keeps mana counters, XP and other numeric UI
//     labels out. Our old number-only gate rejected far less, and LEns does not use it at all.
//
//   * THE COLOUR VALUE COMES FROM THE set_color ARGUMENT. The game writes text first and
//     colour after, so reading the `color` property at text time returns the PREVIOUS label's
//     colour — which is why the old colour gate rejected exactly zero labels ("颜色不符 0" in
//     every single report). LEns's postfix signature is the trick:
//
//         static void Postfix(object __instance, object __0)
//
//     `__0` is Harmony's positional injection of the setter's first argument and works for the
//     IL2CPP interop setter (verified: DamageTextHook::PostfixReadColor, which runs in game
//     today). The colour string arrives as e.g. "RGBA(1.000, 1.000, 1.000, 1.000)".
//
// LEns's pipeline, verbatim from the IL:
//
//     set_text  postfix : name guard -> strip <.*?>, trim
//                         -> ^\D*(\d[\d,\.]*)([kKmMbB]?)\D*$  (LEns's own number rule)
//                         -> 2.0 <= dmg <= 1e8
//                         -> same-instance same-frame same-value guard (Time.frameCount)
//                         -> _pendingHits[id] = (dmg, TickCount64)
//     set_color postfix : name guard -> pending exists for id -> 0 <= age <= 220 ms
//                         -> classify the RGBA string (red=taken, yellow=crit, else=normal)
//                         -> emit sample, remove pending
//     per-frame update  : drop pending older than 220 ms; clear frame guards above 4096 entries
//
// Count-up tween frames (one text write per frame with a growing value) collapse naturally:
// each write merely overwrites _pendingHits[id], and the single colour write that follows the
// spawn consumes the latest value once. That is the mechanism the old same-value dedup could
// not replicate, and it is why big crits previously landed as dozens of pseudo-hits.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using HybridMod.Capture;
using HybridMod.Interop;

namespace HybridMod
{
    /// <summary>
    /// Captures damage floats by hooking <c>TMP_Text</c>'s <c>text</c> and <c>color</c> setters,
    /// pairing each text write with the colour write that follows it, and feeding the Rust DPS core.
    /// </summary>
    public sealed class DamageNumberCapture
    {
        /// <summary>Candidates for the game's floating-text component, tried in order.</summary>
        /// <remarks>
        /// Same four candidates as LEns's <c>ResolveTextType</c>, in the same order: the type lives in
        /// the <c>Unity.TextMeshPro</c> or <c>Il2Cpp__Generated</c> assembly and is spelled either way.
        /// Matching the assembly name first keeps us from picking a same-named type out of an
        /// unrelated assembly when interop assemblies load lazily.
        /// </remarks>
        private static readonly (string Assembly, string Type)[] TmpTextCandidates =
        {
            ("Unity.TextMeshPro", "TMPro.TMP_Text"),
            ("Unity.TextMeshPro", "Il2CppTMPro.TMP_Text"),
            ("Il2Cpp__Generated", "TMPro.TMP_Text"),
            ("Il2Cpp__Generated", "Il2CppTMPro.TMP_Text"),
        };

        /// <summary>
        /// The only GameObject name whose text/colour writes count as damage, verbatim from
        /// LEns's <c>PassesSourceGuards</c>.
        /// </summary>
        private const string DamageSourceName = "Damage Number(Clone)";

        /// <summary>Minimum parsed value accepted, verbatim from LEns's damage guards.</summary>
        private const float MinDamage = 2.0f;

        /// <summary>Maximum accepted value, verbatim from LEns.</summary>
        private const float MaxDamage = 100000000f;

        /// <summary>How long a text write stays eligible to be paired with a colour write.</summary>
        /// <remarks>Verbatim from DamageTextHook::HandleColorObject (220 ms).</remarks>
        private const long PairWindowMs = 220;

        /// <summary>Frame-guard repeat tolerance, verbatim from PassesDamageGuards (0.001).</summary>
        private const float SameFrameTolerance = 0.001f;

        /// <summary>Frame-guard table cap, verbatim from DamageTextHook::Update (4096).</summary>
        private const int FrameGuardCap = 4096;

        /// <summary>A pending text write, awaiting its colour write.</summary>
        private readonly struct PendingHit
        {
            public readonly float Damage;
            public readonly long TickMs;
            public PendingHit(float damage, long tickMs) { Damage = damage; TickMs = tickMs; }
        }

        /// <summary>Frame guard: the last value this instance reported and in which frame.</summary>
        private readonly struct TextGuardState
        {
            public readonly float LastDamage;
            public readonly int LastFrame;
            public TextGuardState(float lastDamage, int lastFrame)
            {
                LastDamage = lastDamage;
                LastFrame = lastFrame;
            }
        }

        private readonly DpsCalculator _dps;
        private readonly Action<string> _log;

        private readonly Dictionary<int, PendingHit> _pending = new();
        private readonly Dictionary<int, TextGuardState> _frameGuards = new();

        private HarmonyLib.Harmony _harmony;
        private bool _hooked;
        private string _reason = "(尚未安装)";

        private string _usedTypeName = "";

        // ---- counters. Everything the pipeline can reject is counted, so the next log line
        // ---- proves which stage is or is not working (the old build's colour stage was blind).
        private int _textsSeen;          // raw set_text postfix invocations
        private int _colorsSeen;         // raw set_color postfix invocations
        private int _textNameRejected;   // set_text on labels that are not "Damage Number(Clone)"
        private int _colorNameRejected;  // set_color ditto
        private int _colorGuardPassed;   // colour writes that passed the name guard
        private int _parseFailed;        // passed name guard, failed LEns's number rule
        private int _outOfRange;         // parsed but outside 2..1e8
        private int _frameDup;           // same instance, same frame, same value
        private int _pendingStored;      // text writes recorded as pending hits
        private int _colorNoPending;     // colour write with no pending hit for that instance
        private int _colorStale;         // pending existed but was older than 220 ms
        private int _colorUnparsed;      // colour string did not match RGBA(...)
        private int _expiredPending;     // pending entries dropped by the sweep

        private int _events;
        private int _outgoing;
        private int _crit;
        private double _totalDamage;

        // value-distribution diagnostics, measured only — never used to reject (LEns doesn't)
        private float _minDamage = float.MaxValue;
        private float _maxDamage;
        private readonly float[] _recent = new float[512];
        private int _recentCount;
        private int _recentNext;

        public DamageNumberCapture(DpsCalculator dps, Action<string> log)
        {
            _dps = dps ?? throw new ArgumentNullException(nameof(dps));
            _log = log ?? (_ => { });
        }

        public bool IsSubscribed => _hooked;
        public int Events => _events;
        public int ColorsSeen => _colorsSeen;

        /// <summary>Installs the two postfixes. Returns null on success.</summary>
        public string Install()
        {
            if (_hooked) return null;

            try
            {
                Type tmpText = ResolveTextType(out _usedTypeName);
                if (tmpText == null)
                    return Fail("找不到 TMP_Text 候选类型（与 LEns 的 ResolveTextType 相同的四个候选均未命中）");

                var setText = AccessTools.PropertySetter(tmpText, "text");
                var setColor = AccessTools.PropertySetter(tmpText, "color");
                if (setText == null || setColor == null)
                    return Fail($"{tmpText.FullName} 上找不到 text/color 的 setter" +
                                $"（text={setText != null}, color={setColor != null}）");

                _harmony = new HarmonyLib.Harmony("local.HybridMod.damagecapture");
                _harmony.Patch(setText, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DamageNumberCapture), nameof(PostfixSetText))));
                _harmony.Patch(setColor, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DamageNumberCapture), nameof(PostfixSetColor))));

                _hooked = true;
                Active = this;
                return null;
            }
            catch (Exception ex)
            {
                return Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public string InstalledTargets =>
            $"{_usedTypeName}.set_text + set_color（名字守卫 \"{DamageSourceName}\"，配对窗口 {PairWindowMs}ms，与 LEns IL 一致）";

        /// <summary>Reference to the live capture, for the static Harmony postfixes.</summary>
        internal static DamageNumberCapture Active;

        // ---- Harmony entry points -------------------------------------------------------------

        /// <summary>
        /// Postfix for <c>TMP_Text.set_text</c>. <c>__0</c> is the setter's first argument
        /// (Harmony positional injection) — the new text itself.
        /// </summary>
        public static void PostfixSetText(object __instance, object __0)
        {
            try { Active?.OnText(__instance, __0); } catch { /* never break rendering */ }
        }

        /// <summary>
        /// Postfix for <c>TMP_Text.set_color</c>. <c>__0</c> is the setter's first argument —
        /// the new colour, whose ToString() is "RGBA(r, g, b, a)" on the interop struct.
        /// </summary>
        public static void PostfixSetColor(object __instance, object __0)
        {
            try { Active?.OnColor(__instance, __0); } catch { /* never break rendering */ }
        }

        // ---- capture logic --------------------------------------------------------------------

        /// <summary>Handles one text write: name guard, parse, guards, park as pending.</summary>
        private void OnText(object instance, object valueObj)
        {
            if (instance == null) return;
            _textsSeen++;

            if (!PassesSourceGuard(instance))
            {
                _textNameRejected++;
                return;
            }

            string text = valueObj?.ToString();
            if (string.IsNullOrEmpty(text)) return;

            string clean = DamageTextParser.StripLensMarkup(text).Trim();
            if (!DamageTextParser.TryParseLensNumber(clean, out float damage))
            {
                _parseFailed++;
                return;
            }

            int id = InstanceId(instance);

            // LEns's frame guard: the same label re-writing the same value within one render frame
            // is one hit, not many.
            int frame = UnityEngine.Time.frameCount;
            if (_frameGuards.TryGetValue(id, out TextGuardState guard)
                && guard.LastFrame == frame
                && Math.Abs(guard.LastDamage - damage) <= SameFrameTolerance)
            {
                _frameDup++;
                return;
            }
            _frameGuards[id] = new TextGuardState(damage, frame);
            if (_frameGuards.Count > FrameGuardCap) _frameGuards.Clear();

            if (damage < MinDamage || damage > MaxDamage)
            {
                _outOfRange++;
                return;
            }

            _pending[id] = new PendingHit(damage, Environment.TickCount64);
            _pendingStored++;
            TrackMagnitude(damage);
        }

        /// <summary>Handles one colour write: name guard, pairing window, classify, emit.</summary>
        private void OnColor(object instance, object valueObj)
        {
            if (instance == null) return;
            _colorsSeen++;

            if (!PassesSourceGuard(instance))
            {
                _colorNameRejected++;
                return;
            }
            _colorGuardPassed++;

            int id = InstanceId(instance);
            if (!_pending.TryGetValue(id, out PendingHit pending))
            {
                _colorNoPending++;
                return;
            }

            long age = Environment.TickCount64 - pending.TickMs;
            if (age < 0 || age > PairWindowMs)
            {
                _colorStale++;
                return;   // LEns leaves the entry parked; the sweep drops it
            }

            if (!DamageTextParser.TryClassifyLensColour(valueObj?.ToString(), out DamageClass cls))
            {
                _colorUnparsed++;
                return;
            }

            bool crit = cls == DamageClass.OutgoingCrit;
            bool incoming = cls == DamageClass.IncomingHit;

            _events++;
            _totalDamage += pending.Damage;
            if (crit) _crit++; else _outgoing++;
            RememberAccepted(pending.Damage);

            _dps.AddSample(UnityEngine.Time.realtimeSinceStartup, pending.Damage,
                isIncoming: incoming, isCrit: crit);

            _pending.Remove(id);
        }

        /// <summary>
        /// LEns's source guard: the label's GameObject must be named exactly
        /// "Damage Number(Clone)".
        /// </summary>
        private bool PassesSourceGuard(object instance)
        {
            return string.Equals(
                TryGetUnityObjectName(instance), DamageSourceName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The GameObject name of a component, read the way LEns's
        /// <c>TryGetUnityObjectName</c> does: reflection on the <c>name</c> property of the
        /// interop wrapper. Empty string when unavailable.
        /// </summary>
        private static string TryGetUnityObjectName(object instance)
        {
            try
            {
                var prop = instance.GetType().GetProperty("name");
                return prop?.GetValue(instance)?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Drops stale pending entries; clears the frame-guard table when oversized.</summary>
        /// <remarks>Called every frame from HybridMod.OnUpdate, mirroring LEns's Update.</remarks>
        public void SweepExpired()
        {
            if (_pending.Count > 0)
            {
                long now = Environment.TickCount64;
                List<int> expired = null;
                foreach (var kv in _pending)
                {
                    if (now - kv.Value.TickMs > PairWindowMs)
                        (expired ??= new List<int>()).Add(kv.Key);
                }
                if (expired != null)
                {
                    foreach (int k in expired) _pending.Remove(k);
                    _expiredPending += expired.Count;
                }
            }

            if (_frameGuards.Count > FrameGuardCap) _frameGuards.Clear();
        }

        /// <summary>Drops transient pairing state, called on scene change like LEns does.</summary>
        public void ResetTransientState()
        {
            _pending.Clear();
            _frameGuards.Clear();
        }

        // ---- value-distribution diagnostics (measured, never used to reject) ------------------

        private void TrackMagnitude(float damage)
        {
            if (damage < _minDamage) _minDamage = damage;
            if (damage > _maxDamage) _maxDamage = damage;
            _recent[_recentNext] = damage;
            _recentNext = (_recentNext + 1) % _recent.Length;
            if (_recentCount < _recent.Length) _recentCount++;
        }

        private void RememberAccepted(float damage)
        {
            if (damage < _minDamage) _minDamage = damage;
            if (damage > _maxDamage) _maxDamage = damage;
        }

        private float RecentMedian()
        {
            if (_recentCount < 32) return 0f;

            var copy = new float[_recentCount];
            Array.Copy(_recent, copy, _recentCount);
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        /// <summary>
        /// Type resolution with LEns's exact candidate list: assembly name first, then type name,
        /// because the interop assemblies load lazily and same-named types can exist elsewhere.
        /// </summary>
        private static Type ResolveTextType(out string usedName)
        {
            usedName = null;

            foreach (var (assemblyName, typeName) in TmpTextCandidates)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.Ordinal))
                        continue;

                    Type t = asm.GetType(typeName, throwOnError: false);
                    if (t != null)
                    {
                        usedName = $"{assemblyName}:{typeName}";
                        return t;
                    }
                }
            }

            // Fallback: the previous build's looser search, in case the assembly names change.
            foreach (string typeName in new[] { "Il2CppTMPro.TMP_Text", "TMPro.TMP_Text" })
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType(typeName, throwOnError: false);
                    if (t != null)
                    {
                        usedName = $"(fallback){asm.GetName().Name}:{typeName}";
                        return t;
                    }
                }
            }
            return null;
        }

        /// <summary>Component identity, matching LEns's <c>TryGetInstanceId</c> exactly: GetHashCode.</summary>
        private static int InstanceId(object instance)
        {
            try { return instance.GetHashCode(); }
            catch { return 0; }
        }

        private string Fail(string reason)
        {
            _reason = reason;
            return reason;
        }

        public string Status() => _hooked
            ? null
            : $"尚未挂钩；{_reason}";

        public void Uninstall()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* never throw from teardown */ }
            if (ReferenceEquals(Active, this)) Active = null;
            _pending.Clear();
            _frameGuards.Clear();
            _hooked = false;
        }

        public string Describe()
        {
            float median = RecentMedian();

            string s =
                $"事件 {_events} 次 / 普通 {_outgoing} + 暴击 {_crit} / 累计 {_totalDamage:F0}" +
                $"\n         set_text {_textsSeen} 次（其中 \"{DamageSourceName}\" 之外 {_textNameRejected}）→ " +
                $"解析失败 {_parseFailed}，范围外 {_outOfRange}，同帧重复 {_frameDup}，暂存 {_pendingStored}" +
                $"\n         set_color {_colorsSeen} 次（名字之外 {_colorNameRejected}，通过守卫 {_colorGuardPassed}）→ " +
                $"无暂存 {_colorNoPending}，超时 {_colorStale}，颜色无法解析 {_colorUnparsed}，配对成功 {_events}，清扫过期 {_expiredPending}" +
                $"\n         数值 {(_minDamage == float.MaxValue ? 0 : _minDamage):F1} ~ {_maxDamage:F1}" +
                $"，中位 {(median > 0 ? median : 0):F1}，平均 {(_events > 0 ? _totalDamage / _events : 0):F1}";

            return s;
        }
    }
}
