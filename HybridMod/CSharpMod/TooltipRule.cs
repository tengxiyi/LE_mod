// TooltipRule.cs - show which loot filter rule matched, in the item info tooltip.
//
// Takes over the second half of LEns's broken tooltip module ("tooltip 中匹配战利品
// 过滤规则号"): for every item tooltip, evaluate the user's enabled filter rules
// top-down against the item (the same order the game uses) and inject the first
// matching rule's number into the tooltip text.
//
// Data path (all via reflection, no compile-time interop references):
//     ItemFilterManager.Instance  (singleton, Il2CppItemFiltering)
//         .Filter                 (ItemFilter)
//             .rules              (List<Rule>)
//                 Rule.get_isEnabled() / Rule.Match(item, outcome, 0) -> bool
//
// The first ENABLED rule whose Match(item) returns true — counted over the FULL
// rules list so disabled rules still occupy their editor numbers — is reported as
// "#<n>". Injection: the tooltip content lives in ItemTooltipInfo._stringBuilder
// (an il2cpp StringBuilder); we insert the rule line right after its first line.
// CreateTooltip* builds a fresh builder every call, so re-injection never accumulates.

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace HybridMod
{
    public static class TooltipRule
    {
        private static bool _resolved;
        private static object _manager;                          // ItemFilterManager.Instance
        private static MethodInfo _filterGet;                    // manager.get_Filter
        private static System.Reflection.PropertyInfo _ruleIsEnabledGet; // Rule.get_isEnabled
        private static System.Reflection.MethodInfo _ruleMatch;          // Rule.Match(item, outcome, idx)
        private static object _ruleOutcomeDefault;               // default(RuleOutcome) boxed
        private static long _injected;
        private static long _noMatch;

        public static long Injected => _injected;
        public static long NoMatch => _noMatch;

        /// <summary>Installs postfixes on both tooltip creators. Returns null on success.</summary>
        public static string Install(HarmonyLib.Harmony harmony, Action<string> log)
        {
            try
            {
                Assembly ile = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Il2CppLE");
                if (ile == null)
                    return Fail(log, "Il2CppLE 程序集未加载");

                Type mgr = ile.GetType("Il2Cpp.TooltipItemManager", throwOnError: false);
                if (mgr == null)
                    return Fail(log, "找不到 Il2Cpp.TooltipItemManager");

                int patched = 0;
                foreach (string creator in new[] { "CreateTooltipFullContent", "CreateTooltipDefaultContent" })
                {
                    MethodInfo m = mgr.GetMethod(creator,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (m == null) continue;

                    harmony.Patch(m, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(TooltipRule), nameof(PostfixCreateTooltip))));
                    patched++;
                }

                if (patched == 0)
                    return Fail(log, "CreateTooltip* 方法一个都没找到");

                return null;
            }
            catch (Exception ex)
            {
                return Fail(log, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void PostfixCreateTooltip(object item, object __result)
        {
            try
            {
                if (__result == null || item == null) return;
                if (!EnsureResolved()) return;

                int number = FindMatchedRuleNumber(item);
                if (number <= 0) { _noMatch++; return; }

                // tooltip content = ItemTooltipInfo._stringBuilder (il2cpp StringBuilder)
                PropertyInfo sbProp = __result.GetType().GetProperty("_stringBuilder",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object sb = sbProp?.GetValue(__result);
                if (sb == null) return;

                Type sbType = sb.GetType();
                string content = sbType.GetMethod("ToString", Type.EmptyTypes)?.Invoke(sb, null) as string;
                if (string.IsNullOrEmpty(content)) return;

                // insert right after the first line (under the item name)
                string ruleLine = $"<color=#FFD700>匹配过滤器规则: #{number}</color>";
                int nl = content.IndexOf('\n');
                string updated = nl >= 0
                    ? content.Substring(0, nl + 1) + ruleLine + "\n" + content.Substring(nl + 1)
                    : content + "\n" + ruleLine;

                // rebuild the builder: Length = 0, Append(newContent)
                PropertyInfo lenProp = sbType.GetProperty("Length");
                MethodInfo append = sbType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(mm => mm.Name == "Append" && mm.GetParameters().Length == 1
                                          && mm.GetParameters()[0].ParameterType.Name == "String");
                if (lenProp == null || append == null) return;

                lenProp.SetValue(sb, 0);
                append.Invoke(sb, new[] { updated });
                _injected++;
            }
            catch
            {
                // A tooltip must never break because of us.
            }
        }

        // ---- filter evaluation -----------------------------------------------------------------

        private static bool EnsureResolved()
        {
            if (_manager != null) return true;

            try
            {
                Assembly ile = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Il2CppLE");
                Type mgrType = ile?.GetType("Il2CppItemFiltering.ItemFilterManager", throwOnError: false);
                if (mgrType == null) return false;

                PropertyInfo instance = mgrType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _manager = instance?.GetValue(null);
                if (_manager == null) return false;

                _filterGet = _manager.GetType().GetProperty("Filter",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                object filter = _filterGet != null ? _filterGet.GetValue(_manager) : null;
                if (filter == null) return false;

                if (_ruleMatch == null)
                {
                    var rulesProp = filter.GetType().GetProperty("rules",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var rules = rulesProp?.GetValue(filter) as IEnumerable;
                    if (rules == null) return false;

                    foreach (object rule in rules)
                    {
                        if (rule == null) continue;
                        Type ruleType = rule.GetType();
                        _ruleIsEnabledGet = ruleType.GetProperty("isEnabled",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _ruleMatch = ruleType.GetMethod("Match",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        ParameterInfo outcome = _ruleMatch.GetParameters().Length > 1
                            ? _ruleMatch.GetParameters()[1]
                            : null;
                        _ruleOutcomeDefault = outcome != null
                            ? Activator.CreateInstance(outcome.ParameterType)
                            : null;
                        break;
                    }
                    if (_ruleMatch == null) return false;
                }
                return true;
            }
            catch
            {
                return false;   // retried on the next tooltip
            }
        }

        /// <summary>
        /// Evaluates enabled rules top-down (the game's own order) and returns the 1-based
        /// editor number of the first match; 0 when nothing matches.
        /// </summary>
        private static int FindMatchedRuleNumber(object item)
        {
            try
            {
                object filter = _filterGet.Invoke(_manager, null);
                if (filter == null) return 0;

                var rules = filter.GetType().GetProperty("rules",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(filter) as IEnumerable;
                if (rules == null) return 0;

                int index = 0;
                foreach (var rule in (IEnumerable)rules)
                {
                    index++;
                    if (rule == null) continue;

                    bool enabled = _ruleIsEnabledGet != null
                        && _ruleIsEnabledGet.GetValue(rule) is bool b && b;
                    if (!enabled) continue;

                    object outcome = _ruleOutcomeDefault;
                    bool matched = _ruleMatch != null
                        && _ruleMatch.Invoke(rule, new[] { item, outcome, (object)0 }) is bool m && m;
                    if (matched) return index;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
