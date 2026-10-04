// TooltipTier.cs - show each affix's tier in the game's item info tooltip.
//
// The game composes tooltip affix lines in TooltipItemManager.AddAffixAltText
// (private static, returns the finished line). The game itself does not display
// which tier an affix is — LEns used to add a coloured tier prefix but its patch
// no longer matches this game version's signature (its log warns about the
// missing 11-parameter AddAffixAltText).
//
// This feature takes over with the CURRENT signature (7 parameters, verified
// against the regenerated interop assembly):
//
//     String AddAffixAltText(ItemDataUnpacked item, ItemAffix affix,
//         TooltipMode mode, Int32 implicitIndex, Int32 uniqueModIndex,
//         BasePropertyInfo propertyInfo, String returnString)
//
// Harmony postfix rewrites `ref string __result`, prepending a coloured
// "[T<n>]" tag. The tier comes from the game's own `ItemAffix.DisplayTier`
// (read via reflection, cached), so sealed/exalted display rules are the
// game's, not ours. Rows without a tier (implicits and similar) resolve to 0
// and are left untouched.
//
// Tier colours match the ground-label chips (AffixLabelColors.Tier1To7ColorHex)
// so both UIs read the same way.

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HybridMod
{
    public static class TooltipTier
    {
        private static readonly string[] TierHex =
        {
            "#B8B8B8", "#ECECEC", "#52D670", "#5AA8FF", "#E8C840", "#C070F0", "#FF5050",
        };

        private static bool _resolved;
        private static MethodInfo _addAffixAltText;
        private static System.Reflection.PropertyInfo _displayTierGet;
        private static System.Reflection.MethodInfo _rollFloatGet;
        private static long _tagged;
        private static long _skipped;

        private static HarmonyLib.Harmony _harmony;

        public static long Tagged => _tagged;
        public static long Skipped => _skipped;

        /// <summary>Installs the postfix. Returns null on success.</summary>
        public static string Install(HarmonyLib.Harmony harmony, Action<string> log)
        {
            if (_addAffixAltText != null) return null;

            try
            {
                Assembly ile = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Il2CppLE");
                if (ile == null)
                    return Fail(log, "Il2CppLE 程序集未加载");

                Type mgr = ile.GetType("Il2Cpp.TooltipItemManager", throwOnError: false);
                MethodInfo m = mgr?.GetMethod("AddAffixAltText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null)
                    return Fail(log, "TooltipItemManager 上找不到 AddAffixAltText");

                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(TooltipTier), nameof(PostfixAddAffixAltText))));

                _addAffixAltText = m;
                _harmony = harmony;
                return null;
            }
            catch (Exception ex)
            {
                return Fail(log, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string Fail(Action<string> log, string reason)
        {
            log?.Invoke($"[TooltipTier] 未生效：{reason}");
            return reason;
        }

        /// <summary>
        /// Postfix: prepend the roll grade ("A95%") and the tier tag ("[T7]") to the finished
        /// affix line. Tier comes from the game's own DisplayTier property; the roll quality
        /// from getRollFloat() (a 0..1 fraction of the tier's value range — LE stores it as a
        /// 0..255 byte and this getter decodes it). Implicits and unknown shapes have no roll
        /// and are left with the tier tag only.
        /// </summary>
        private static void PostfixAddAffixAltText(object affix, ref string __result)
        {
            try
            {
                if (__result == null || affix == null) return;

                if (!_resolved)
                {
                    _resolved = true;
                    _displayTierGet = affix.GetType().GetProperty("DisplayTier",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _rollFloatGet = affix.GetType().GetMethod("getRollFloat",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_displayTierGet == null) return;

                object v = _displayTierGet.GetValue(affix);
                int tier = v is int i ? i : 0;
                if (tier < 1 || tier > 7) { _skipped++; return; }

                // roll quality: getRollFloat() as a 0..1 fraction -> 0..100
                double roll100 = -1;
                if (_rollFloatGet != null)
                {
                    object r = _rollFloatGet.Invoke(affix, null);
                    if (r is float f)
                    {
                        roll100 = f <= 1.001 ? f * 100.0 : f;   // tolerate 0..100-shaped getters
                        roll100 = Math.Clamp(roll100, 0, 100);
                    }
                }

                string prefix = "";
                if (roll100 >= 0)
                {
                    string grade = GradeLetter(roll100);
                    prefix = $"<color={GradeHex(grade)}>{grade}{roll100:F0}%</color> ";
                }

                string hex = TierHex[tier - 1];
                __result = $"{prefix}<color={hex}>[T{tier}]</color> {__result}";
                _tagged++;
            }
            catch
            {
                // A tooltip line must never break because of us.
            }
        }

        /// <summary>LEns's grade thresholds (verbatim): F &lt;50, C &lt;70, B &lt;80, A &lt;95, S ≥95.</summary>
        private static string GradeLetter(double roll0To100) =>
            roll0To100 < 50 ? "F" : roll0To100 < 70 ? "C" : roll0To100 < 80 ? "B" : roll0To100 < 95 ? "A" : "S";

        private static string GradeHex(string grade) => grade switch
        {
            "S" => "#E8C840",   // gold
            "A" => "#5AA8FF",   // blue
            "B" => "#52D670",   // green
            "C" => "#ECECEC",   // white
            _ => "#9E9E9E",     // grey (F)
        };
    }
}
