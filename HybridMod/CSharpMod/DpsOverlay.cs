// DpsOverlay.cs - the unified on-screen panel (task: "把这 3 个的信息整合一下，集中显示在
// 左侧上边偏下一点的位置").
//
// What it shows
// -------------
//   * DPS block  - the Rust core's own snapshot (current DPS / total / hits / crit / combat
//     time / max hit). Numbers are proven identical to LEns's calculator, so this replaces
//     LEns's "LEns - DMG" panel (F6) without losing information.
//   * ECO block  - LEns's EconomyHudSnapshot rows (gold / favour income per minute over its
//     1m and 5m triangular windows), read reflectively from LEns's object graph. Same data its
//     bottom-left "LEns - ECO" panel (F5) renders, so that panel can be turned off too.
//     HybridMod never writes to LEns state on its own for this - the hiding of the two LEns
//     panels is done once at startup, explicitly logged, and F5/F6 bring them back anytime.
//
// Rendering notes
// ---------------
//   * Unity IMGUI (OnGUI) - the same rendering path LEns's HUD uses, proven in this IL2CPP
//     build, Chinese text included.
//   * Panel text is refreshed at 5 Hz from OnUpdate and cached; OnGUI only draws the cached
//     string. OnGUI runs several times per frame and must not do work, let alone allocate.
//   * Position: top-left, 192 px down (user asked for ~180 px to clear the game's icons there).
//     F8 toggles visibility. Height adapts to the line count; the ECO block disappears
//     gracefully when LEns's economy feature is unavailable.

using System;
using System.Collections.Generic;
using System.Reflection;
using HybridMod.Interop;
using UnityEngine;

namespace HybridMod
{
    public sealed class DpsOverlay
    {
        /// <summary>Show/hide hotkey: the unified panel owns F5 in the new key map.</summary>
        private const KeyCode ToggleKey = KeyCode.F5;

        private const float RefreshIntervalSeconds = 0.2f;
        private const float PanelX = 12f, PanelY = 192f, PanelW = 340f;

        private readonly DpsCalculator _dps;
        private readonly Action<string> _log;

        public bool Visible = true;

        private string[] _lines = Array.Empty<string>();
        private float _nextRefresh;
        private GUIStyle _labelStyle;
        private bool _styleReady;

        // LEns economy reflection (resolved once, then cheap property reads at 5 Hz)
        private bool _ecoResolved;
        private object _ecoFeature;
        private PropertyInfo _ecoSnapshotProp;
        private PropertyInfo _ecoRowsProp;

        public DpsOverlay(DpsCalculator dps, Action<string> log)
        {
            _dps = dps;
            _log = log ?? (_ => { });
        }

        /// <summary>Hotkey handling and text refresh; called from MelonMod.OnUpdate.</summary>
        public void OnUpdate(float now)
        {
            try
            {
                if (Input.GetKeyDown(ToggleKey))
                {
                    Visible = !Visible;
                    _log($"[DPS面板] {(Visible ? "显示" : "隐藏")}（F5）");
                }

                if (!Visible || now < _nextRefresh) return;
                _nextRefresh = now + RefreshIntervalSeconds;
                Refresh(now);
            }
            catch
            {
                // A display must never break the update loop.
            }
        }

        private void Refresh(float now)
        {
            var lines = new List<string>(10);

            if (_dps == null || !_dps.IsValid)
            {
                lines.Add("HybridMod");
                lines.Add("计算器未就绪");
            }
            else
            {
                _dps.Trim(now);
                if (_dps.TrySnapshot(now, out LensDpsSnapshot s))
                {
                    lines.Add($"HybridMod   DPS({s.WindowSeconds:F0}s): {s.CurrentDps:N0}");
                    lines.Add($"总伤害: {s.TotalDamage:N0}    命中: {s.HitCount}");
                    lines.Add($"暴击: {s.CritRate:P0}    战斗: {s.CombatDurationSeconds:F1}s    最大被击: {s.MaxIncomingDamage:N0}");
                }
                else
                {
                    lines.Add("HybridMod");
                    lines.Add("快照失败");
                }
            }

            string[] eco = ReadEcoLines();
            if (eco != null && eco.Length > 0)
            {
                lines.Add("──────────────────");
                lines.AddRange(eco);
            }

            // Live on/off state of the three function keys, as requested. F5 is this panel
            // itself; F6/F7 states come from LensPanelPolicy, which owns those toggles.
            lines.Add("[F5]面板 " + Mark(true) +
                      "  [F6]增强 " + Mark(LensPanelPolicy.GroundEnhanceOn) +
                      "  [F7]日志 " + Mark(LensPanelPolicy.DamageLogOn));

            _lines = lines.ToArray();
        }

        /// <summary>Green 开 / grey 关, as TMP-free IMGUI rich text.</summary>
        private static string Mark(bool on) =>
            on ? "<color=#7CFC00>开</color>" : "<color=#B0B0B0>关</color>";

        /// <summary>
        /// LEns's economy rows via reflection: ModBootstrap._economyHudFeature.Snapshot.Rows,
        /// each row = (Abbrev, PerMinute1m, PerMinute5m). Returns null when LEns is absent or
        /// the object graph changed - the panel simply loses the ECO block.
        /// </summary>
        private string[] ReadEcoLines()
        {
            try
            {
                if (!_ecoResolved)
                {
                    _ecoResolved = true;
                    object bootstrap = HybridMod.GetLensBootstrap();
                    _ecoFeature = bootstrap == null
                        ? null
                        : HybridMod.GetFieldOrProperty(bootstrap, "EconomyHudFeature", "_economyHudFeature");
                    if (_ecoFeature != null)
                    {
                        _ecoSnapshotProp = _ecoFeature.GetType().GetProperty("Snapshot");
                        Type snapType = _ecoSnapshotProp?.PropertyType;
                        _ecoRowsProp = snapType?.GetProperty("Rows");
                    }
                    if (_ecoRowsProp == null) _log("[DPS面板] LEns 收益数据不可读，面板只显示 DPS");
                }

                if (_ecoFeature == null || _ecoSnapshotProp == null || _ecoRowsProp == null) return null;

                object snapshot = _ecoSnapshotProp.GetValue(_ecoFeature);
                if (!(snapshot != null && _ecoRowsProp.GetValue(snapshot) is System.Collections.IEnumerable rows))
                    return Array.Empty<string>();

                var lines = new List<string>(4);
                foreach (object row in rows)
                {
                    if (row == null) continue;
                    string abbrev = row.GetType().GetProperty("Abbrev")?.GetValue(row) as string ?? "?";
                    double pm1 = ToDouble(row.GetType().GetProperty("PerMinute1m")?.GetValue(row));
                    double pm5 = ToDouble(row.GetType().GetProperty("PerMinute5m")?.GetValue(row));
                    lines.Add($"{abbrev,-6} 收益/分  1m {pm1:N0}    5m {pm5:N0}");
                }
                return lines.ToArray();
            }
            catch
            {
                return null;   // diagnostics must never break the frame
            }
        }

        private static double ToDouble(object v) => v is double d ? d : 0.0;

        /// <summary>Draws the cached lines; called from MelonMod.OnGUI.</summary>
        public void OnGUI()
        {
            if (!Visible || _lines.Length == 0) return;

            try
            {
                if (!_styleReady)
                {
                    _labelStyle = new GUIStyle(GUI.skin.label);
                    _labelStyle.fontSize = 13;
                    _labelStyle.wordWrap = false;
                    _labelStyle.richText = true;   // the key-status line is colour-coded
                    _labelStyle.margin = new RectOffset(0, 0, 0, 0);
                    _labelStyle.padding = new RectOffset(0, 0, 0, 0);
                    // CJK glyphs run taller than latin at the same font size; without these the
                    // label boxes clipped the top and bottom of every line.
                    _labelStyle.alignment = TextAnchor.MiddleLeft;
                    _labelStyle.clipping = TextClipping.Overflow;
                    _styleReady = true;
                }

                // 22 px per line at fontSize 13 gives CJK glyphs headroom (19 clipped them).
                const float padX = 10f, padTop = 8f, padBottom = 10f, lineHeight = 22f;
                float h = padTop + padBottom + _lines.Length * lineHeight;

                GUI.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(new Rect(PanelX, PanelY, PanelW, h), GUIContent.none);
                GUI.backgroundColor = Color.white;

                float y = PanelY + padTop;
                for (int i = 0; i < _lines.Length; i++)
                {
                    GUI.Label(new Rect(PanelX + padX, y, PanelW - 2 * padX, lineHeight),
                        _lines[i], _labelStyle);
                    y += lineHeight;
                }
            }
            catch
            {
                // Never break rendering.
            }
        }
    }
}
