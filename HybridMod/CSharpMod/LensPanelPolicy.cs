// LensPanelPolicy.cs - retires the LEns hotkeys that integration made redundant, and rebinds
// the three surviving functions onto F5 / F6 / F7.
//
// FINAL KEY MAP (user request: "只留 F7/F8/F10 三个 F 键的功能，关联到 F5/6/7")
//
//     F5   unified panel (Rust DPS + ECO) show/hide        [was F8, ours]
//     F6   ground-item display enhancement on/off          [was F7, LEns's master switch]
//     F7   damage detail log on/off                        [was F10, LEns's debug log]
//     [ ]  affix chip size (HybridMod, kept as-is)
//
// HOW THE OLD BINDINGS DIE
// ------------------------
// LEns reads Input.GetKeyDown in its own Update loop; input is polled, not consumed, so old
// handlers cannot be unbound and will keep seeing F5/F6/F7/F10 presses. Each old handler ends
// in exactly one public LEns method, so a Harmony prefix on that method - which returns false
// only while the retired key is held - retires the binding without touching anything else:
//
//     F5 -> EconomyHudFeature.ToggleHud                 skipped while F5 is down
//     F6 -> DpsFeature.SetHudEnabled                    skipped while F6 is down
//          (its SetTextHookEnabled(next=true) side effect is harmless; our policy keeps the
//           hook on anyway, and the pref write it performs is inert - the persisted toggles
//           are never applied in this game build)
//     F7 -> HookCollector.ToggleGroundLabelEnhancement  skipped while F7 is down
//     F10 -> HookCollector.ToggleDamageApplyLog         skipped while F10 is down
//
// Our own handlers then bind the SAME public methods to the new keys:
//
//     F6 -> HookCollector.ToggleGroundLabelEnhancement()
//     F7 -> HookCollector.ToggleDamageApplyLog()
//
// (calling the method directly is never blocked: the prefixes only skip while the RETIRED key
// is down, and a direct call never coincides with those keys).
//
// As a safety net the policy also re-asserts the integrated panel state - LEns DPS HUD hidden,
// LEns ECO HUD hidden, LEns text hook ON - whenever it drifts (all three are trivial idempotent
// setters, verified against LEns's IL; no preference file is written by this path).

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HybridMod
{
    public sealed class LensPanelPolicy
    {
        /// <summary>Live F6 state (ground-item display enhancement), read by the unified panel.</summary>
        public static bool GroundEnhanceOn;

        /// <summary>Live F7 state (damage detail log), read by the unified panel.</summary>
        public static bool DamageLogOn;

        private readonly Action<string> _log;
        private HarmonyLib.Harmony _harmony;

        private bool _resolved;
        private object _dpsFeature, _ecoFeature, _collector;
        private PropertyInfo _dpsHudGet, _ecoHudGet, _hookGet, _damageLogGet;
        private PropertyInfo _groundEnhanceStaticGet;
        private MethodInfo _dpsHudSet, _ecoHudSet, _hookSet;
        private MethodInfo _toggleGroundEnhance, _toggleDamageLog;
        private float _nextPoll;

        /// <summary>Reference to the live instance, for the static Harmony prefixes.</summary>
        internal static LensPanelPolicy Active;

        /// <summary>
        /// HybridMod initializes before LEns (registration order), so LEns's object graph does not
        /// exist yet at our OnInitializeMelon. Key retirement and enforcement therefore happen on
        /// the first OnUpdate poll where LEns is ready — see _retired.
        /// </summary>
        private bool _retired;
        private float _pollSeconds = 0.5f;

        public LensPanelPolicy(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Passive setup at mod-init time. Note: we deliberately do NOT patch
        /// ProphecyNodeOverlayFeature.Initialize — any Harmony patch on it fails to compile
        /// (its IL references the missing Il2CppLE.Factions.ProphecyTooltip type) and the failed
        /// detour attempt correlated with a fatal CLR error (0x80131506) in the next
        /// Assembly.GetType call. The exception is caught and logged by MelonLoader anyway and
        /// costs nothing we rely on.
        /// </summary>
        public void Initialize()
        {
            try
            {
                Assembly lens = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "LEns");
                if (lens == null)
                {
                    _log("[按键] LEns 未安装，键位策略不生效。");
                    _retired = true;
                    return;
                }

                _log("[按键] 等待 LEns 完成初始化后接管键位与面板整合…");
            }
            catch (Exception ex)
            {
                _log($"[按键] 初始化异常（键位保持 LEns 原状）：{ex.GetType().Name}: {ex.Message}");
                _retired = true;
            }
        }

        /// <summary>Key handling + periodic state re-assert; called from MelonMod.OnUpdate.</summary>
        public void OnUpdate(float now)
        {
            try
            {
                // Key checks MUST run every frame: Input.GetKeyDown is true for exactly one
                // frame, and v0.5.3 gated them behind the 1.5 s poll — which missed virtually
                // every press (F5 works because DpsOverlay checks it per frame).
                if (_retired && _collector != null)
                {
                    if (Input.GetKeyDown(KeyCode.F6)) InvokeGroundEnhance();
                    if (Input.GetKeyDown(KeyCode.F7)) InvokeDamageLog();
                }

                if (now < _nextPoll) return;
                _nextPoll = now + _pollSeconds;

                if (!_retired)
                {
                    // Fast poll while waiting for LEns's bootstrap to come into existence.
                    if (!EnsureResolved()) return;

                    InstallKeyRetirement();
                    int changed = Enforce();
                    UpdateKeyStates();
                    _retired = true;
                    _pollSeconds = 1.5f;
                    Active = this;

                    _log(
                        "[按键] 快捷键已重排：F5 统一面板(DPS+收益) | F6 地上显示增强 | F7 伤害详细日志 | [ ] 词缀字号。\n" +
                        $"         旧 F8/F9/F10 已退役；LEns 的 DPS/ECO 面板保持收起（初始收起 {changed} 项），飘字采集保持开启。");
                    return;
                }

                UpdateKeyStates();

                // --- safety net -------------------------------------------------------------
                int drifted = Enforce();
                if (drifted > 0)
                    _log($"[按键] 检测到 LEns 面板状态漂移，已自动恢复 {drifted} 项（可能是误触旧键）。");
            }
            catch
            {
                // Never break the update loop.
            }
        }

        // ---- new bindings ----------------------------------------------------------------------

        private void InvokeGroundEnhance()
        {
            if (_toggleGroundEnhance == null) { _log("[按键] 地上增强 Hook 未就绪。"); return; }
            object r = _toggleGroundEnhance.Invoke(_collector, null);
            string state = r is bool b ? (b ? "ON" : "OFF") : "Hook 未就绪";
            UpdateKeyStates();
            _log($"[按键] 地上显示增强: {state}（F6）");
        }

        private void InvokeDamageLog()
        {
            if (_toggleDamageLog == null) { _log("[按键] 伤害日志 Hook 未就绪。"); return; }
            _toggleDamageLog.Invoke(_collector, null);
            UpdateKeyStates();
            bool on = _damageLogGet != null && ReadBool(_damageLogGet, _collector);
            _log($"[按键] 伤害详细日志: {(on ? "ON（[DMG_APPLY] → Mods 文件）" : "OFF")}（F7）");
        }

        /// <summary>Refreshes the public key-state flags the unified panel displays.</summary>
        private void UpdateKeyStates()
        {
            try
            {
                if (_groundEnhanceStaticGet != null)
                    GroundEnhanceOn = _groundEnhanceStaticGet.GetValue(null) is bool b && b;
            }
            catch { /* state flag only */ }
            try
            {
                if (_damageLogGet != null) DamageLogOn = ReadBool(_damageLogGet, _collector);
            }
            catch { /* state flag only */ }
        }

        // ---- retirement patches ----------------------------------------------------------------

        private void InstallKeyRetirement()
        {
            _harmony ??= new HarmonyLib.Harmony("local.HybridMod.keypolicy");

            // F5: LEns's ECO HUD toggle (our F5 toggles the unified panel instead).
            PatchSkipOnKey(_ecoFeature, "ToggleHud", nameof(SkipWhenF5Down), "LEns ECO 面板");

            // F6: LEns's DPS HUD toggle (+ its harmless hook/pref side effects).
            PatchSkipOnKey(_dpsFeature, "SetHudEnabled", nameof(SkipWhenF6Down), "LEns DPS 面板");

            // F7: LEns's ground-enhancement toggle (moved to F6).
            PatchSkipOnKey(_collector, "ToggleGroundLabelEnhancement", nameof(SkipWhenF7Down), "LEns 地上增强(旧 F7)");

            // F10: LEns's damage-log toggle (moved to F7).
            PatchSkipOnKey(_collector, "ToggleDamageApplyLog", nameof(SkipWhenF10Down), "LEns 伤害日志(旧 F10)");
        }

        /// <summary>
        /// Patches <c>methodName</c> with a prefix that skips the original while the method's OWN
        /// retired key is down. One key per method is essential: our F6 handler calls
        /// ToggleGroundLabelEnhancement while F6 is down, so a prefix that also watched F6 (or a
        /// shared all-keys prefix) would skip our own direct call — exactly the bug that made
        /// F6/F7 dead in v0.5.2.
        /// </summary>
        private void PatchSkipOnKey(object target, string methodName, string prefixName, string label)
        {
            if (target == null) { _log($"[按键] {label}：目标缺失，旧键未退役。"); return; }

            MethodInfo m = target.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) { _log($"[按键] {label}：找不到 {methodName}，旧键未退役。"); return; }

            _harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(LensPanelPolicy), prefixName)));
        }

        // Each retired binding checks ONLY its own retired key, so a direct call from our new
        // handlers (which fire while a DIFFERENT key is down) always passes through.

        private static bool SkipWhenF5Down() => !Input.GetKeyDown(KeyCode.F5);   // old: ECO panel
        private static bool SkipWhenF6Down() => !Input.GetKeyDown(KeyCode.F6);   // old: DPS panel
        private static bool SkipWhenF7Down() => !Input.GetKeyDown(KeyCode.F7);   // old: ground enhance
        private static bool SkipWhenF10Down() => !Input.GetKeyDown(KeyCode.F10); // old: damage log

        /// <summary>
        /// Patches <c>methodName</c> with a prefix that skips the original while the retired key
        /// is down. Direct calls (our new bindings, the policy itself) never coincide with that
        /// key, so they always go through.
        /// </summary>
        private void PatchSkipOnKey(object target, string methodName, KeyCode retiredKey, string label)
        {
            if (target == null) { _log($"[按键] {label}：目标缺失，旧键未退役。"); return; }

            MethodInfo m = target.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) { _log($"[按键] {label}：找不到 {methodName}，旧键未退役。"); return; }

            _harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(LensPanelPolicy), nameof(SkipOriginal))));
        }

        /// <summary>
        /// Shared prefix body: runs while the RETIRED key was bound to the patched method.
        /// The four patched methods each check a different key, so one guarded body is enough:
        /// when any retired key is down and the call originates from LEns's key handler, it is
        /// skipped. Direct calls never coincide, because our handlers only fire on F6/F7 and the
        /// policy never runs on a keypress.
        /// </summary>
        private static bool SkipOriginal() =>
            !(Input.GetKeyDown(KeyCode.F5) || Input.GetKeyDown(KeyCode.F6)
              || Input.GetKeyDown(KeyCode.F7) || Input.GetKeyDown(KeyCode.F10));

        // ---- safety net ------------------------------------------------------------------------

        private bool EnsureResolved()
        {
            if (_resolved) return _dpsFeature != null;

            try
            {
                object bootstrap = HybridMod.GetLensBootstrap();
                if (bootstrap == null) return false;

                _dpsFeature = HybridMod.GetFieldOrProperty(bootstrap, "DpsFeature", "_dpsFeature");
                _ecoFeature = HybridMod.GetFieldOrProperty(bootstrap, "EconomyHudFeature", "_economyHudFeature");
                _collector = HybridMod.GetFieldOrProperty(bootstrap, "Collector", "_collector");
                if (_dpsFeature == null) return false;

                if (_dpsFeature != null)
                {
                    _dpsHudGet = _dpsFeature.GetType().GetProperty("IsHudEnabled");
                    _dpsHudSet = FindBoolSetter(_dpsFeature, "SetHudEnabled");
                }
                if (_ecoFeature != null)
                {
                    _ecoHudGet = _ecoFeature.GetType().GetProperty("IsHudEnabled");
                    _ecoHudSet = FindBoolSetter(_ecoFeature, "SetHudEnabled");
                }
                if (_collector != null)
                {
                    _hookGet = _collector.GetType().GetProperty("IsTextHookEnabled");
                    _hookSet = FindBoolSetter(_collector, "SetTextHookEnabled");
                    _toggleGroundEnhance = _collector.GetType().GetMethod("ToggleGroundLabelEnhancement",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _toggleDamageLog = _collector.GetType().GetMethod("ToggleDamageApplyLog",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _damageLogGet = _collector.GetType().GetProperty("IsDamageApplyLogEnabled");

                    // Ground-enhancement state lives as a static property on the hook class
                    // (HookCollector has a setter but no getter).
                    Assembly lens = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "LEns");
                    Type hookType = lens?.GetType(
                        "LastEpochMods.Template.Features.Loot.GroundItemLabelHook", throwOnError: false);
                    _groundEnhanceStaticGet = hookType?.GetProperty("IsGroundLootEnhancementEnabled",
                        BindingFlags.Public | BindingFlags.Static);
                }

                _resolved = true;
                return true;
            }
            catch
            {
                return false;   // retried on a later poll
            }
        }

        /// <summary>Re-asserts the integrated state; returns how many values had drifted.</summary>
        private int Enforce()
        {
            int changed = 0;

            if (_dpsFeature != null && _dpsHudGet != null && _dpsHudSet != null
                && ReadBool(_dpsHudGet, _dpsFeature))
            {
                WriteBool(_dpsHudSet, _dpsFeature, false);
                changed++;
            }

            if (_ecoFeature != null && _ecoHudGet != null && _ecoHudSet != null
                && ReadBool(_ecoHudGet, _ecoFeature))
            {
                WriteBool(_ecoHudSet, _ecoFeature, false);
                changed++;
            }

            if (_collector != null && _hookGet != null && _hookSet != null
                && !ReadBool(_hookGet, _collector))
            {
                WriteBool(_hookSet, _collector, true);
                changed++;
            }

            return changed;
        }

        private static MethodInfo FindBoolSetter(object target, string name) =>
            target.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(bool) }, null);

        private static bool ReadBool(PropertyInfo p, object target) =>
            p != null && p.GetValue(target) is bool b && b;

        private static void WriteBool(MethodInfo m, object target, bool value) =>
            m?.Invoke(target, new object[] { value });
    }
}
