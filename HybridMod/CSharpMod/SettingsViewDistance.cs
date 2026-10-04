// SettingsViewDistance.cs - add a "视距" (view distance) slider row to the game's own
// Gameplay settings panel, at the bottom.
//
// How: the gameplay settings page is built by GameplaySettingsUIManager.InitializeUI,
// which wires one control object per setting (NumericSettingUI rows for sliders —
// menuScaling, tooltipScaling, lootlabelsScaling, hudScaling, ...). We let the game
// build its page, then CLONE an existing slider row (_tooltipScaling), disconnect the
// clone from its setting, relabel it "视距", and bind its slider to our
// ViewDistance.SetMultiplier. A name-based guard keeps it from cloning twice.
//
// Result: the slider lives in the game's own settings screen, scrolls with it, and
// survives re-opening; a language change or page rebuild re-runs InitializeUI and the
// name-guard re-clones if needed.

using HarmonyLib;
using Il2CppLE.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HybridMod
{
    public static class SettingsViewDistance
    {
        private const string RowName = "HybridModViewDistanceRow";

        /// <summary>Installs the InitializeUI postfix. Returns null on success.</summary>
        public static string Install(HarmonyLib.Harmony harmony, Action<string> log)
        {
            try
            {
                Assembly ile = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Il2CppLE");
                if (ile == null)
                    return Fail(log, "Il2CppLE 程序集未加载");

                Type mgrType = ile.GetType("Il2CppLE.Settings.GameplaySettingsUIManager", throwOnError: false);
                if (mgrType == null)
                    return Fail(log, "找不到 GameplaySettingsUIManager");

                MethodInfo init = mgrType.GetMethod("InitializeUI",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (init == null)
                    return Fail(log, "GameplaySettingsUIManager 上找不到 InitializeUI");

                harmony.Patch(init, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SettingsViewDistance), nameof(PostfixInitializeUI))));
                return null;
            }
            catch (Exception ex)
            {
                return Fail(log, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Runs right after the game builds its gameplay settings page.</summary>
        private static void PostfixInitializeUI(GameplaySettingsUIManager __instance)
        {
            try
            {
                object rowObj = AccessTools.Field(__instance.GetType(), "_tooltipScaling")
                    ?.GetValue(__instance);
                var row = rowObj as Component;
                if (row == null) return;

                GameObject rowGo = row.gameObject;
                Transform parent = rowGo.transform.parent;
                if (parent == null) return;
                if (parent.Find(RowName) != null) return;   // already cloned

                // capture the original row's TMP texts (same hierarchy in the clone)
                var origTexts = rowGo.GetComponentsInChildren<TMP_Text>(true);

                GameObject cloneGo = UnityEngine.Object.Instantiate(rowGo, parent);
                cloneGo.name = RowName;
                cloneGo.transform.SetAsLastSibling();       // the bottom of the page

                // sever the clone's connection to the game's tooltip-scaling setting
                var nui = cloneGo.GetComponent<NumericSettingUI>();
                nui?.Disconnect();

                // relabel: the clone's TMP holding the same text as the original label
                var cloneTexts = cloneGo.GetComponentsInChildren<TMP_Text>(true);
                string label = LongestText(origTexts);
                if (!string.IsNullOrEmpty(label))
                    foreach (var t in cloneTexts)
                        if (t != null && t.text == label) { t.text = "视距"; break; }

                // slider: our range, our multiplier, our handler
                var slider = cloneGo.GetComponentInChildren<Slider>(true);
                if (slider == null) return;
                slider.minValue = ViewDistance.MinMultiplier;
                slider.maxValue = ViewDistance.MaxMultiplier;
                slider.value = ViewDistance.Multiplier;
                slider.onValueChanged.AddListener(new UnityEngine.Events.UnityAction<float>(v =>
                {
                    ViewDistance.SetMultiplier(v);
                    var valueText = FindNumericText(cloneTexts);
                    if (valueText != null)
                        valueText.text = $"x{ViewDistance.Multiplier:F2}";
                }));
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[SettingsViewDistance] 注入失败（不影响其他功能）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string LongestText(TMP_Text[] texts)
        {
            string best = null;
            foreach (var t in texts)
                if (t != null && !string.IsNullOrEmpty(t.text) && (best == null || t.text.Length > best.Length))
                    best = t.text;
            return best;
        }

        /// <summary>The TMP whose original text was numeric (the old value readout), if any.</summary>
        private static TMP_Text FindNumericText(TMP_Text[] texts)
        {
            foreach (var t in texts)
            {
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                string s = t.text.Replace("x", "").Replace("%", "").Trim();
                if (s.Length > 0 && s.IndexOfAny(new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' }) >= 0
                    && float.TryParse(s, out _))
                    return t;
            }
            return null;
        }
    }
}
