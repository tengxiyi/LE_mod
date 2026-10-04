// HybridMod.cs - the MelonLoader entry point for the C#-host + Rust-core pair.
//
// What this mod does today
// ------------------------
// It proves the hybrid bridge works *inside the game* and reports a comparison between the Rust
// core and LEns's own DPS number, which is the end-to-end check that neither `cargo test` nor the
// standalone harness can provide:
//
//   * `cargo test`              -> proves the Rust logic
//   * CSharpHarness             -> proves the managed/native ABI and marshalling
//   * this mod, in game         -> proves it runs under the real runtime, and lets the Rust
//                                  output be compared against LEns's live value
//
// Scope is deliberately small. It computes nothing about the game itself yet and never patches
// anything; it is an observability harness for the bridge.
//
// Why the DPS comparison is done this way
// ---------------------------------------
// LEns's calculators are reachable only through its own object graph, and reading them by
// reflection is safe for a diagnostic but is not a foundation for real code: the field names are
// private implementation details that a LEns update may rename. So this mod:
//
//   * reads LEns's value reflectively, defensively, and never writes to it;
//   * feeds the Rust core a *synthetic* timeline whose expected result is known exactly, so the
//     Rust side is verifiable without depending on LEns at all;
//   * reports both, so a human can sanity-check one against the other while playing.
//
// A future version should replace the synthetic timeline with real damage samples fed from a
// hook, and drop the reflection entirely.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using HybridMod.Interop;

[assembly: MelonInfo(typeof(HybridMod.HybridMod), "HybridMod", "0.5.12", "local")]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]

namespace HybridMod
{
    public sealed class HybridMod : MelonMod
    {
        /// <summary>Window LEns uses for its DPS display, so the comparison is like-for-like.</summary>
        private const float DpsWindowSeconds = 5.0f;

        /// <summary>How often to print a comparison line, in seconds.</summary>
        private const float ReportIntervalSeconds = 10.0f;

        /// <summary>Most recent real damage timeline position used for the synthetic feed.</summary>
        private DpsCalculator _dps;
        private float _sessionEpoch;
        private float _nextReport;

        /// <summary>Subscribes to the game's own damage-number event and feeds <see cref="_dps"/>.</summary>
        private DamageNumberCapture _capture;

        /// <summary>On-screen panel for the Rust DPS numbers (F8).</summary>
        private DpsOverlay _overlay;

        /// <summary>Runtime restyle of LEns's ground-item labels (always on).</summary>
        private GroundLabelRestyle _restyle;

        /// <summary>Key map F5/F6/F7 + LEns panel integration (see LensPanelPolicy).</summary>
        private LensPanelPolicy _keyPolicy;

        /// <summary>Diagnostics for LEns lookups, so a missing member is visible once, not per frame.</summary>
        private object _lensCalculator;
        private PropertyInfo _lensCurrentDps;
        private string _lensStatus = "(not looked up yet)";

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("=== HybridMod: C# host + Rust core ===");

            // ---- 1. load the native core ------------------------------------------------
            // MelonLoader loads mods from memory, so Mods\ is not on the native search path.
            // RustBridge installs an explicit resolver for that reason.
            string modsDir = MelonEnvironment.ModsDirectory;
            string err = RustBridge.Initialize(modsDir);
            if (err != null)
            {
                LoggerInstance.Error($"原生核心加载失败，混合 Mod 无法工作：{err}");
                LoggerInstance.Error("  请确认 lens_core.dll 与 HybridMod.dll 一起放在 Mods\\ 目录下。");
                return;
            }
            LoggerInstance.Msg($"原生核心已加载 : {RustBridge.ResolvedPath}");
            LoggerInstance.Msg($"原生核心版本   : {RustBridge.Version()}");

            // ---- 2. verify the ABI before trusting it ----------------------------------
            err = RustBridge.ValidateAbi();
            if (err != null)
            {
                LoggerInstance.Error($"ABI 校验失败，出于安全考虑停用：{err}");
                LoggerInstance.Error("  这通常意味着 lens_core.dll 与 HybridMod.dll 版本不匹配。");
                return;
            }
            LoggerInstance.Msg("ABI 校验通过   : 结构体 24/56 字节，cdecl 调用约定正确");

            // ---- 3. parse the real affix table through the Rust core -------------------
            LoadAffixTable();

            // ---- 4. start the DPS bridge ------------------------------------------------
            _sessionEpoch = UnityEngine.Time.realtimeSinceStartup;
            _dps = RustBridge.CreateDps(DpsWindowSeconds, _sessionEpoch);
            if (_dps == null || !_dps.IsValid)
            {
                LoggerInstance.Error("DPS 计算器创建失败。");
                return;
            }
            _nextReport = _sessionEpoch + ReportIntervalSeconds;

            // ---- 5. deterministic self-check -------------------------------------------
            // A synthetic timeline with an exactly known answer. If this prints, the whole
            // in-game path (load -> ABI -> P/Invoke -> Rust -> back) is proven in the real runtime.
            RunSelfCheck();

            // ---- 6. install the real damage capture ------------------------------------
            InstallDamageCapture();

            // ---- 7. on-screen DPS panel (F5) -------------------------------------------
            _overlay = new DpsOverlay(_dps, m => LoggerInstance.Msg(m));

            // ---- 8. ground-label restyle (always on; F6 is the enhancement master switch) ----
            InstallGroundLabelRestyle();

            // ---- 8c. view distance (persisted in MelonLoader\HybridModSettings.txt) ------
            ViewDistance.LoadSettings();
            // ---- 8b. tooltip tier display (item info interface) --------------------------
            // Prepends "[T<n>]" to every affix line in the item tooltip, using the game's own
            // DisplayTier. Takes over the role of LEns's tooltip module, whose hook no longer
            // matches this game version.
            if (TooltipTier.Install(new HarmonyLib.Harmony("local.HybridMod.tooltiptier"),
                    m => LoggerInstance.Msg(m)) == null)
                LoggerInstance.Msg("TooltipTier 已挂钩：物品信息界面的词缀行会显示 [T品级] 标记");
            else
                LoggerInstance.Warning("TooltipTier 未生效（详见上方日志）。");

            // ---- 9. key map + panel integration -----------------------------------------
            // F5 unified panel | F6 ground enhancement | F7 damage log. Retires the old
            // LEns bindings (F5 ECO / F6 DPS+hook / F7 enhancement / F10 log) and keeps
            // LEns's own panels tucked away.
            _keyPolicy = new LensPanelPolicy(m => LoggerInstance.Msg(m));
            _keyPolicy.Initialize();

            LoggerInstance.Msg("初始化完成。每 10 秒会在日志里打一行对比结果。");
        }

        /// <summary>
        /// Installs the ground-label restyle: patches LEns's GroundAffixLabelFormatter.Format so
        /// affixes render as a chip line under the item name instead of sup/sub flanks.
        /// </summary>
        private void InstallGroundLabelRestyle()
        {
            _restyle = new GroundLabelRestyle(m => LoggerInstance.Msg(m));
            string err = _restyle.Install();

            if (err == null)
                LoggerInstance.Msg($"地上标签重排版已挂钩 : {_restyle.InstalledTargets}");
            else
                LoggerInstance.Warning($"地上标签重排版未生效：{err}");
        }

        /// <summary>
        /// Installs the damage capture.
        /// </summary>
        /// <remarks>
        /// Hooks <c>TMP_Text</c>'s <c>text</c> and <c>color</c> setters and pairs them — the only data
        /// source proven to work in this game build. See DamageNumberCapture's header for the four
        /// approaches that were tried and failed first.
        /// </remarks>
        private void InstallDamageCapture()
        {
            _capture = new DamageNumberCapture(_dps, m => LoggerInstance.Msg(m));
            string err = _capture.Install();

            if (err == null)
                LoggerInstance.Msg($"伤害采集已挂钩 : {_capture.InstalledTargets}");
            else
                LoggerInstance.Error($"伤害采集挂钩失败：{err}");
        }

        private void LoadAffixTable()
        {
            string path = Path.Combine(MelonEnvironment.ModsDirectory, "AffixAbbrev.tsv");
            if (!File.Exists(path))
            {
                LoggerInstance.Warning($"找不到词缀表 {path}；跳过后缀表校验。");
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var table = RustBridge.LoadAffixTable(bytes);
                if (table == null || !table.IsValid)
                {
                    LoggerInstance.Error("Rust 端解析词缀表失败。");
                    return;
                }

                string sample = table.NameForId(1);
                bool roundTrip = sample != null && table.TryGetId(sample, out uint back) && back == 1;

                LoggerInstance.Msg(
                    $"词缀表(Rust)   : {table.Length} 条数据 / {table.Skipped} 行跳过，" +
                    $"id=1 -> \"{sample}\"，反向查回 = {(roundTrip ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"读取词缀表异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Feeds a fixed synthetic timeline into the Rust core and asserts the known answer.
        /// </summary>
        /// <remarks>
        /// 10 hits of 100 damage, one every 0.5s, window 5.0s, evaluated 4.5s after the first hit.
        /// Ages are 0.0..4.5 so the triangular weights are 1.0..0.1, summing to 5.5; the weighted
        /// total is 550 and `CurrentDps = 2 * 550 / 5 = 220`. The same figure is asserted by the
        /// Rust unit tests and by the standalone harness, so all three agree by construction.
        /// </remarks>
        private void RunSelfCheck()
        {
            using var probe = RustBridge.CreateDps(DpsWindowSeconds, 0.0f);
            if (probe == null || !probe.IsValid)
            {
                LoggerInstance.Error("自检：计算器创建失败。");
                return;
            }

            for (int i = 0; i < 10; i++) probe.AddSample(i * 0.5f, 100.0);
            probe.Trim(4.5f);

            if (!probe.TrySnapshot(4.5f, out LensDpsSnapshot snap))
            {
                LoggerInstance.Error("自检：TrySnapshot 失败。");
                return;
            }

            bool ok = Math.Abs(snap.CurrentDps - 220.0) < 1e-3
                      && snap.HitCount == 10
                      && Math.Abs(snap.TotalDamage - 1000.0) < 1e-9;

            if (ok)
                LoggerInstance.Msg($"自检通过       : 合成 10 次伤害 -> {snap}（期望 dps=220.0）");
            else
                LoggerInstance.Error($"自检失败！期望 dps=220.0/hits=10/total=1000，实际 {snap}");
        }

        public override void OnUpdate()
        {
            // Overlay + key policy + restyle housekeeping run even before the DPS bridge exists.
            _restyle?.OnUpdate();
            _overlay?.OnUpdate(UnityEngine.Time.realtimeSinceStartup);
            _keyPolicy?.OnUpdate(UnityEngine.Time.realtimeSinceStartup);
            ViewDistance.OnUpdate(UnityEngine.Time.realtimeSinceStartup);

            if (_dps == null || !_dps.IsValid) return;

            // Expire pending text/colour pairings every frame, mirroring LEns's DamageTextHook.Update.
            _capture?.SweepExpired();

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextReport) return;
            _nextReport = now + ReportIntervalSeconds;

            try
            {
                ReportComparison(now);
            }
            catch (Exception ex)
            {
                // A diagnostic must never be able to break the game loop.
                LoggerInstance.Warning($"对比报告异常（已忽略）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Draws the Rust DPS panel. IMGUI via OnGUI is the path LEns's own HUD uses, proven in
        /// this IL2CPP build; the overlay only paints a cached string.
        /// </summary>
        public override void OnGUI()
        {
            _overlay?.OnGUI();
        }

        /// <summary>
        /// Resets the Rust statistics and capture pairing state on scene change, the way LEns's
        /// own scene reset does — without this, the "实测" number accumulates across scenes and
        /// the LEns comparison is apples-to-oranges.
        /// </summary>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            try
            {
                _capture?.ResetTransientState();
                if (_dps is { IsValid: true })
                {
                    _dps.Reset(UnityEngine.Time.realtimeSinceStartup);
                    LoggerInstance.Msg($"[DPS] 场景切换 [{buildIndex}] {sceneName}：Rust 统计已重置（与 LEns 对齐）");
                }
            }
            catch
            {
                // A scene-load diagnostic must never break loading.
            }
        }

        /// <summary>
        /// Reports live DPS computed by the Rust core from captured combat text, plus a synthetic
        /// scenario whose answer is known.
        /// </summary>
        /// <remarks>
        /// The synthetic half stays because it is the only part with a verifiable expected value: it
        /// prints a fixed scenario through the same native calculator, so "the bridge works" remains
        /// checkable even when no combat is happening.
        /// </remarks>
        private void ReportComparison(float now)
        {
            // --- live: everything the capture has fed us --------------------------------
            _dps.Trim(now);
            string live;
            if (_dps.TrySnapshot(now, out LensDpsSnapshot real))
            {
                live = $"{real.CurrentDps:F1} dps | {real.HitCount} hits | 累计 {real.TotalDamage:F0} " +
                       $"| 暴击 {real.CritRate:P0} | 时间线 {_dps.TimelineLength} 点";
            }
            else
            {
                live = "(快照失败)";
            }

            string capture = _capture == null
                ? "(未安装)"
                : _capture.Status() ?? _capture.Describe();

            // --- synthetic: fixed scenario, known answer --------------------------------
            using var probe = RustBridge.CreateDps(DpsWindowSeconds, 0.0f);
            float expected;
            if (probe != null && probe.IsValid)
            {
                for (int i = 0; i < 10; i++) probe.AddSample(i * 0.5f, 100.0);
                probe.Trim(4.5f);
                expected = probe.TrySnapshot(4.5f, out LensDpsSnapshot s) ? (float)s.CurrentDps : float.NaN;
            }
            else
            {
                expected = float.NaN;
            }

            LoggerInstance.Msg(
                $"[DPS] 实测 {live}");
            LoggerInstance.Msg(
                $"[DPS] 采集 {capture}   ‖  LEns(实况) {ReadLensDps()}   ‖  桥自检 {expected:F1}(期望 220.0)");
        }

        /// <summary>
        /// Reads LEns's current DPS through reflection. Read-only, never throws. Resolution is
        /// retried each report until it succeeds, so a transiently missing object graph (early
        /// startup) does not poison the whole session.
        /// </summary>
        private string ReadLensDps()
        {
            try
            {
                if (_lensCalculator == null || _lensCurrentDps == null)
                {
                    ResolveLens();
                    if (_lensCalculator == null || _lensCurrentDps == null) return _lensStatus;
                }

                object value = _lensCurrentDps.GetValue(_lensCalculator);
                string main = value is double d ? d.ToString("F1") + " dps" : _lensStatus;

                // Why-is-it-zero diagnostics: the calculator's own counters, read-only. When the
                // dps value sits at 0.0 these fields say whether the calculator is genuinely
                // empty (hits=0, i.e. LEns's capture is not feeding it) or holding data the
                // window math discards.
                string diag = DescribeLensCalculator();
                return string.IsNullOrEmpty(diag) ? main : $"{main} {diag}";
            }
            catch (Exception ex)
            {
                _lensStatus = $"(读取失败: {ex.GetType().Name})";
                _lensCalculator = null;
                _lensCurrentDps = null;
                return _lensStatus;
            }
        }

        /// <summary>
        /// One-line readout of LEns's calculator internals. Returns "" if any read fails; a
        /// diagnostic must never get in the way of the number it explains.
        /// </summary>
        private string DescribeLensCalculator()
        {
            try
            {
                object hits = GetFieldOrProperty(_lensCalculator, "_hitCount");
                object total = GetFieldOrProperty(_lensCalculator, "_totalDamage");
                object window = GetFieldOrProperty(_lensCalculator, "_windowSeconds");
                object timeline = GetFieldOrProperty(_lensCalculator, "_timeline");
                int tl = (timeline as System.Collections.ICollection)?.Count ?? -1;

                return $"[hits={hits} total={total} tl={tl} w={window}s]";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Locates LEns's active <c>DpsStatsCalculator</c> by walking the mod's own object graph.
        /// </summary>
        /// <remarks>
        /// Every step is optional: any missing type/field simply leaves <c>_lensStatus</c> with an
        /// explanation, and the mod keeps working. This is intentional — the comparison is a
        /// convenience, not a dependency.
        /// </remarks>
        private void ResolveLens()
        {
            Assembly lens = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "LEns");
            if (lens == null)
            {
                _lensStatus = "(LEns 未加载)";
                return;
            }

            // MelonLoader keeps loaded mods in a registry; find LEns's ModMain instance.
            object modMain = FindLoadedMelonInstance(lens);
            if (modMain == null)
            {
                _lensStatus = "(未找到 LEns 的 ModMain 实例)";
                return;
            }

            // ModMain -> _bootstrap (or similar) -> _dpsFeature -> _calculator
            object bootstrap = GetFieldOrProperty(modMain, "Bootstrap", "_bootstrap", "Core");
            object dpsFeature = bootstrap == null ? null : GetFieldOrProperty(bootstrap, "DpsFeature", "_dpsFeature");
            object calculator = dpsFeature == null ? null : GetFieldOrProperty(dpsFeature, "Calculator", "_calculator");

            if (calculator == null)
            {
                _lensStatus = "(未能从 LEns 对象图取到 DpsStatsCalculator)";
                return;
            }

            Type calcType = calculator.GetType();
            _lensCurrentDps = calcType.GetProperty("CurrentDps",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (_lensCurrentDps == null)
            {
                _lensStatus = "(DpsStatsCalculator 上没有 CurrentDps)";
                return;
            }

            _lensCalculator = calculator;
            _lensStatus = "(已就绪)";
        }

        /// <summary>Finds a loaded MelonMod instance of the given assembly via MelonLoader's registry.</summary>
        private static object FindLoadedMelonInstance(Assembly assembly)
        {
            // MelonLoader exposes the registry on MelonBase; read it reflectively so this file
            // needs no assumption about the exact API shape.
            Type baseType = typeof(MelonMod).BaseType;                 // MelonBase
            PropertyInfo all = baseType?.GetProperty("RegisteredMelons",
                BindingFlags.Public | BindingFlags.Static);
            if (all?.GetValue(null) is System.Collections.IEnumerable melons)
            {
                foreach (object m in melons)
                    if (m != null && m.GetType().Assembly == assembly) return m;
            }
            return null;
        }

        /// <summary>LEns's ModBootstrap (its feature hub), or null. Shared with the overlay.</summary>
        internal static object GetLensBootstrap()
        {
            try
            {
                Assembly lens = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "LEns");
                object modMain = lens == null ? null : FindLoadedMelonInstance(lens);
                return GetFieldOrProperty(modMain, "Bootstrap", "_bootstrap", "Core");
            }
            catch
            {
                return null;
            }
        }

        internal static object GetFieldOrProperty(object target, params string[] names)
        {
            if (target == null) return null;
            Type t = target.GetType();
            const BindingFlags FLAGS =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (string n in names)
            {
                FieldInfo f = t.GetField(n, FLAGS);
                if (f != null) return f.GetValue(target);

                PropertyInfo p = t.GetProperty(n, FLAGS);
                if (p != null && p.CanRead) return p.GetValue(target);
            }
            return null;
        }

        public override void OnDeinitializeMelon()
        {
            try
            {
                _restyle?.Uninstall();
                _restyle = null;
                _capture?.Uninstall();
                _capture = null;
                _dps?.Dispose();
                _dps = null;
                RustBridge.Shutdown();
            }
            catch { /* never throw from teardown */ }
        }
    }
}
