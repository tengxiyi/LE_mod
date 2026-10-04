// GroundLabelRestyle.cs - restyle LEns's enhanced ground-item labels at runtime.
//
// (v0.4.0) Layout: affixes move from <sup>/<sub> flanks to a chip line under the name.
// (v0.4.1) User-defined highlight rules (colour + bold) via Mods\GroundLabelRules.txt, with
//          exact LP / Weaver's Will values read from the item model.
//
// WHY A RUNTIME PATCH AND NOT A LENS EDIT
// ---------------------------------------
// The two-side small affixes are produced by LEns's GroundAffixLabelFormatter.Format. LEns.dll
// is a third-party file in Mods\; per this workspace's iron rule we do not modify or rebuild it.
// What we do instead is Harmony-patch that one static method from HybridMod and rewrite the
// string it returns - LEns keeps doing all the hard work (item model resolution, abbreviation
// lookup, tier colours, keeping the label alive against the game's late-frame overwrite), and
// only the final text changes.
//
// THE PATCH (two hooks on the same method)
// ----------------------------------------
//     Prefix: captures the ItemDataUnpackedModel argument so the postfix can read exact
//             LegendaryPotential / WeaversWill values instead of guessing them from text.
//     Postfix: runs the returned string through GroundLabelTransform.Restyle with the parsed
//              rule set and the captured item context. Anything that does not match LEns's
//              known output shape is passed through untouched.
//
// The model field below assumes Format is not re-entered concurrently - it is called from the
// main thread for label composition, and the worst case of a violation is one label styled
// with the previous label's LP/WW context.
//
// RULES FILE
// ----------
//     Mods\GroundLabelRules.txt - plain text, editable while the game runs; changes hot-reload
//     within about a second (mtime polling). Format is documented in the shipped template.
//
// KEYS (runtime only, no persistent config - UserData\ is outside the writable scope):
//     The restyle itself is always on; the ground-enhancement master switch is F6 (see
//     LensPanelPolicy) and acts as the emergency revert, since it disables LEns's whole label
//     enhancement. [ and ] shrink / grow the affix line (50%..120%).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader.Utils;
using UnityEngine;

namespace HybridMod
{
    public sealed class GroundLabelRestyle
    {
        private const string FormatterTypeName =
            "LastEpochMods.Template.Features.Loot.GroundAffixLabelFormatter";

        private const string RulesFileName = "GroundLabelRules.txt";
        private const float RulesPollSeconds = 1.0f;

        private const string DefaultRulesFile =
@"# HybridMod 地上标签高亮规则（改完保存，约 1 秒内自动生效）
# 每行一条：条件 [样式...]；# 开头是注释
#
# 条件（作用于单个词缀色块）
#   text:关键词     词缀文字包含该关键词（简写或全文均可）
#   tier>=N         词缀品级 >= N（1..7）
# 条件（作用于整条标签的名字行）
#   lp>=N           传奇潜能 >= N
#   ww              有织者意志
#   name:关键词     物品名包含该关键词
#
# 样式
#   bg=#RRGGBB 或 bg=#RRGGBBAA   背景色（8 位时后两位是透明度，如 40=25%）
#   fg=#RRGGBB                   文字色
#   bold                         加粗
# 同一目标命中多条规则时，写在文件里靠前的生效。
#
# 例子（去掉行首的 # 即可启用）：
# tier>=6 bg=#FF3355 fg=#FFFFFF bold
# text:暴击 bg=#FFD70040 bold
# text:移速 bg=#33FF8840 bold
# lp>=1 bg=#FFD70060 fg=#FFD700 bold
# ww bg=#40C0FF40 bold
# name:崇高 fg=#FF8800 bold
";

        private readonly Action<string> _log;
        private HarmonyLib.Harmony _harmony;
        private bool _hooked;
        private string _reason = "(尚未安装)";

        private int _sizePct = Ground.GroundLabelTransform.DefaultAffixSizePct;

        private Ground.RestyleOptions _options = new();
        private Ground.LabelRuleSet _rules = new();
        private string _rulesPath;
        private DateTime _rulesMtimeUtc;
        private float _nextRulesPoll;

        private long _restyled;
        private long _passedThrough;

        /// <summary>Item model captured by the prefix, consumed by the postfix.</summary>
        private static object _pendingModel;

        /// <summary>Reflection cache for the two model properties (resolved once).</summary>
        private static PropertyInfo _lpProperty;
        private static PropertyInfo _wwProperty;
        private static bool _modelPropsResolved;

        /// <summary>Reference to the live instance, for the static Harmony hooks.</summary>
        internal static GroundLabelRestyle Active;

        public GroundLabelRestyle(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public long Restyled => _restyled;
        public long PassedThrough => _passedThrough;

        /// <summary>Installs the prefix + postfix. Returns null on success.</summary>
        public string Install()
        {
            if (_hooked) return null;

            try
            {
                Assembly lens = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "LEns");
                if (lens == null)
                    return Fail("LEns 程序集未加载，无法重排地上标签");

                Type formatter = lens.GetType(FormatterTypeName, throwOnError: false);
                if (formatter == null)
                    return Fail($"找不到 {FormatterTypeName}");

                MethodInfo format = AccessTools.Method(formatter, "Format");
                if (format == null)
                    return Fail("GroundAffixLabelFormatter 上找不到 Format 方法");

                _harmony = new HarmonyLib.Harmony("local.HybridMod.groundrestyle");
                _harmony.Patch(format,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(GroundLabelRestyle), nameof(PrefixCaptureModel))),
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(GroundLabelRestyle), nameof(PostfixFormat))));

                _hooked = true;
                Active = this;
                LoadRules(initial: true);
                return null;
            }
            catch (Exception ex)
            {
                return Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public string InstalledTargets =>
            $"LEns!{FormatterTypeName}.Format 改写（始终启用，[ ] 调字号，当前 {_sizePct}%；" +
            $"规则文件 {RulesFileName}，{_rules.Describe()}）";

                // ---- height sync: RETIRED ---------------------------------------------------------------
        //
        // v0.5.6/v0.5.7 synced the floating-line height after each two-line restyle (a
        // TMP_Text.set_text patch, then a FindObjectsOfType sweep). With the chips now
        // trailing the name on the SAME line, labels are single-line again - the game's
        // own stacking pitch fits them with no adjustment, so this entire mechanism is
        // gone. (History: the set_text-patch variant caused fatal CLR errors 0x80131506
        // and was removed in v0.5.7; do not reintroduce it.)
// ---- Harmony hooks ---------------------------------------------------------------------

        /// <summary>Stores the item model (a boxed Nullable&lt;ItemDataUnpackedModel&gt;) for the postfix.</summary>
        private static void PrefixCaptureModel(object __0)
        {
            try { _pendingModel = __0; }
            catch { /* never break the caller */ }
        }

        private static void PostfixFormat(ref string __result)
        {
            object model = _pendingModel;
            _pendingModel = null;

            try
            {
                var self = Active;
                if (self == null || __result == null) return;

                Ground.RestyleOptions opts = self._options;
                opts.SizePct = self._sizePct;
                ResolveModelProps(model);
                opts.ItemLp = ReadModelInt(model, _lpProperty);
                opts.ItemWw = ReadModelInt(model, _wwProperty);
                opts.Rules = self._rules;

                string styled = Ground.GroundLabelTransform.Restyle(__result, opts);
                if (!string.Equals(styled, __result, StringComparison.Ordinal))
                {
                    self._restyled++;
                    __result = styled;
                }
                else
                {
                    self._passedThrough++;
                }
            }
            catch
            {
                // A loot label must never break because of us; worst case it renders as LEns wrote it.
            }
        }

        /// <summary>
        /// Reads an int-ish property off the boxed model. Nullable&lt;int&gt; boxes to either null
        /// or a boxed int, which is what the `is int` test observes.
        /// </summary>
        private static int ReadModelInt(object model, PropertyInfo property)
        {
            if (model == null || property == null) return 0;
            try
            {
                object v = property.GetValue(model);
                if (v is int i) return i;
                return int.TryParse(v?.ToString(), out int parsed) ? parsed : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Resolves the model's LP/WW properties on first use (single model type in practice).</summary>
        private static void ResolveModelProps(object model)
        {
            if (_modelPropsResolved || model == null) return;
            try
            {
                Type t = model.GetType();
                _lpProperty = t.GetProperty("LegendaryPotential");
                _wwProperty = t.GetProperty("WeaversWill");
                _modelPropsResolved = true;
                Active?._log($"[GroundRestyle] 物品模型属性解析：LP={_lpProperty != null}，WW={_wwProperty != null}");
            }
            catch
            {
                _modelPropsResolved = true;   // do not retry forever on a broken model type
            }
        }

        // ---- rules file ------------------------------------------------------------------------

        private void LoadRules(bool initial)
        {
            try
            {
                _rulesPath ??= Path.Combine(MelonEnvironment.ModsDirectory, RulesFileName);

                if (!File.Exists(_rulesPath))
                {
                    File.WriteAllText(_rulesPath, DefaultRulesFile);
                    _log($"[GroundRestyle] 已生成规则模板 {_rulesPath}（编辑保存即生效）");
                    _rulesMtimeUtc = File.GetLastWriteTimeUtc(_rulesPath);
                    return;
                }

                DateTime mtime = File.GetLastWriteTimeUtc(_rulesPath);
                if (!initial && mtime == _rulesMtimeUtc) return;
                _rulesMtimeUtc = mtime;

                string content = File.ReadAllText(_rulesPath);
                var parsed = Ground.LabelRuleSet.Parse(content);

                if (parsed.Rules.Count == 0 && parsed.Warnings.Count == 0 && !initial)
                    return;   // whitespace-only touch, nothing to say

                _rules = parsed;
                _log($"[GroundRestyle] 规则已加载：{_rules.Describe()}");
                foreach (string w in _rules.Warnings)
                    _log($"[GroundRestyle]   {w}");
            }
            catch (Exception ex)
            {
                _log($"[GroundRestyle] 规则文件读取失败（沿用旧规则）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Rules hot-reload + size keys + height sweep; called from MelonMod.OnUpdate.</summary>
        public void OnUpdate()
        {
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now >= _nextRulesPoll)
                {
                    _nextRulesPoll = now + RulesPollSeconds;
                    LoadRules(initial: false);
                }


                if (Input.GetKeyDown(KeyCode.LeftBracket))
                {
                    _sizePct = Math.Max(50, _sizePct - 5);
                    _log($"[GroundRestyle] 词缀字号 {_sizePct}%（[ ] 调整）");
                }
                else if (Input.GetKeyDown(KeyCode.RightBracket))
                {
                    _sizePct = Math.Min(120, _sizePct + 5);
                    _log($"[GroundRestyle] 词缀字号 {_sizePct}%（[ ] 调整）");
                }
            }
            catch
            {
                // Never break the update loop.
            }
        }

        public void Uninstall()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* never throw from teardown */ }
            if (ReferenceEquals(Active, this)) Active = null;
            _hooked = false;
        }

        private string Fail(string reason)
        {
            _reason = reason;
            return reason;
        }
    }
}
