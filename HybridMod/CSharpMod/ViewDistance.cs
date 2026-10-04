// ViewDistance.cs - adjust the game's view distance (调整游戏视距).
//
// The game's own visual-distance backend is Il2Cpp.ZoneVisualsManager — a static
// singleton ("instance") that exposes:
//
//     float farViewDistance            ← how far zone visuals render
//     bool  hasCustomViewDistance
//     int   minimumVegetationDistance  (vegetation draw distance)
//     ... plus player-light settings
//
// The game's own settings UI does not bind these; the original author's newer
// LEns added a slider to the game's settings panel. We expose the same control
// as a slider in OUR panel instead — same effect, no settings-UI patching.
//
// Approach: capture the game's base distance once, then keep applying
// base × multiplier (re-applied every poll, so zone loads / game resets are
// self-healing). The multiplier persists in MelonLoader\HybridModSettings.txt
// (inside our writable scope; UserData is not).

using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HybridMod
{
    public static class ViewDistance
    {
        private const float PollSeconds = 1.0f;
        public const float MinMultiplier = 0.5f;
        public const float MaxMultiplier = 3.0f;

        private static readonly object _gate = new object();

        private static bool _resolved;
        private static object _manager;
        private static System.Reflection.PropertyInfo _farGet;
        private static System.Reflection.PropertyInfo _farSet;
        private static float _baseDistance = -1f;
        private static float _nextPoll;
        private static float _nextSave = -1f;
        private static bool _dirty;

        public static float Multiplier { get; private set; } = 1f;

        /// <summary>True once the game's ZoneVisualsManager singleton has been captured.</summary>
        public static bool Available => _manager != null && _farGet != null && _farSet != null;

        private static string SettingsPath =>
            Path.Combine(Path.GetDirectoryName(MelonLoader.Utils.MelonEnvironment.ModsDirectory) ?? "",
                "MelonLoader", "HybridModSettings.txt");

        public static void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string raw in File.ReadAllLines(SettingsPath))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("viewDistanceMultiplier=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (float.TryParse(line.Substring(23), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float m))
                        Multiplier = Math.Clamp(m, MinMultiplier, MaxMultiplier);
                }
            }
            catch { /* settings are optional */ }
        }

        public static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath,
                    $"viewDistanceMultiplier={Multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
            }
            catch { /* settings are optional */ }
        }

        public static void SetMultiplier(float m)
        {
            Multiplier = Math.Clamp(m, MinMultiplier, MaxMultiplier);
            _dirty = true;
            try { if (EnsureResolved()) Apply(); }   // immediate feedback on the slider
            catch { /* the 1 s poll re-applies anyway */ }
        }

        /// <summary>Periodic resolve + (re)apply; called from MelonMod.OnUpdate.</summary>
        public static void OnUpdate(float now)
        {
            try
            {
                if (now < _nextPoll) return;
                _nextPoll = now + PollSeconds;

                if (!EnsureResolved()) return;

                Apply();

                if (_dirty && now >= _nextSave)
                {
                    _nextSave = now + 1.0f;
                    _dirty = false;
                    SaveSettings();
                }
            }
            catch
            {
                // A visual nicety must never break the update loop.
            }
        }

        /// <summary>Forces an immediate apply after a slider change.</summary>
        public static void ApplyNow()
        {
            try { if (EnsureResolved()) Apply(); _dirty = true; }
            catch { /* never break the loop */ }
        }

        private static bool EnsureResolved()
        {
            if (_manager != null && _farGet != null && _farSet != null) return true;

            try
            {
                if (_manager == null)
                {
                    Assembly ile = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Il2CppLE");
                    Type t = ile?.GetType("Il2Cpp.ZoneVisualsManager", throwOnError: false);
                    if (t == null) return false;

                    PropertyInfo inst = t.GetProperty("instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    _manager = inst?.GetValue(null);
                    if (_manager == null) return false;
                }

                if (_farGet == null || _farSet == null)
                {
                    _farGet = _manager.GetType().GetProperty("farViewDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _farSet = _manager.GetType().GetProperty("farViewDistance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_farGet == null || _farSet == null) return false;
                }
                return true;
            }
            catch
            {
                return false;   // retried on a later poll (the manager appears once a zone loads)
            }
        }

        private static void Apply()
        {
            float cur = (float)_farGet.GetValue(_manager);
            if (_baseDistance < 0 && cur > 1f) _baseDistance = cur;   // capture once
            if (_baseDistance <= 0) return;

            float target = (float)(_baseDistance * Multiplier);
            if (Math.Abs(target - cur) > 0.01f) _farSet.SetValue(_manager, target);
        }
    }
}
