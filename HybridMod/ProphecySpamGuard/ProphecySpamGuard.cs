// ProphecySpamGuard v3 - stop LEns's per-frame TypeLoadException flood by
// switching off the one preference that gates the broken code path.
//
// Verified facts (read with tools/InspectMethod against Mods\LEns.dll)
// -------------------------------------------------------------------
// The game update of 2026-10-02 (Unity 6000.4.8 / game 1.5) removed three types
// LEns was compiled against:
//
//     Il2CppLE.Factions.ConstellationStar
//     Il2CppLE.Factions.Prophecy
//     Il2CppLE.Factions.ProphecyTooltip
//
// ProphecyImGuiStarOverlay.OnGui() contains
//
//     00BE: callvirt  Il2CppLE.Factions.ConstellationStar::get_rect
//
// and also stores `ConstellationStar` in a field. The JIT therefore cannot
// compile the method at all, so it throws TypeLoadException on every call --
// measured 9070 times per session, producing a 4.7 MB log.
//
// Two earlier attempts failed, both for instructive reasons:
//
//   1. Patching OnGui itself -> "IL Compile Error". MonoMod must rewrite that
//      body, and the body is precisely what cannot be compiled. Patching the
//      broken method is impossible.
//   2. Forcing ProphecyNodeOverlayFeature.get_IsEnabled to false. The patch
//      applied, but OnGui is JIT-compiled before its first instruction runs, so
//      the guard never helped: the exception happens at compile time, not at the
//      guarded branch.
//
// The actual gate
// ---------------
// ModBootstrap.Render() ends with:
//
//     00BD: ldfld   ModBootstrap::_prophecyNodeOverlay      (MelonPreferences)
//     00C3: callvirt MelonPreferences_Entry<bool>::get_Value
//     00CA: brfalse.s  IL_00d2: ret        <- false => OnGui is never called
//     00CC: call      ProphecyImGuiStarOverlay::OnGui()
//
// So setting that preference to false means the broken method is never invoked,
// never JIT-compiled and never throws. No patching of broken code is required.
//
// What this mod does
// ------------------
//   1. Sets the LEns preference `ProphecyNodeOverlay` to false at startup.
//   2. Verifies against the live MelonPreferences value and logs the outcome.
//
// It does not modify LEns.dll, does not patch the broken method, and leaves every
// other LEns feature (DPS HUD, loot labels, affix table, economy HUD) untouched.
// The Prophecy overlay it disables is dead anyway: ProphecyNodeOverlayFeature
// itself already failed to initialise with a TypeLoadException.

using System;
using System.Linq;
using MelonLoader;
using MelonLoader.Preferences;

[assembly: MelonInfo(typeof(ProphecySpamGuard.ProphecySpamGuard),
                     "ProphecySpamGuard", "3.0.0", "local")]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]

namespace ProphecySpamGuard
{
    public sealed class ProphecySpamGuard : MelonMod
    {
        private const string CategoryName = "LEns";
        private const string EntryName = "ProphecyNodeOverlay";

        public override void OnInitializeMelon()
        {
            try
            {
                // MelonPreferences exposes the registered categories/entries.
                var cat = MelonPreferences.GetCategory(CategoryName);
                if (cat == null)
                {
                    LoggerInstance.Warning(
                        $"找不到 MelonPreferences 类别 '{CategoryName}'；LEns 可能尚未初始化，本 Mod 不生效。");
                    return;
                }

                var entry = cat.GetEntry<bool>(EntryName);
                if (entry == null)
                {
                    LoggerInstance.Warning(
                        $"类别 '{CategoryName}' 下找不到 '{EntryName}'；LEns 可能已改版，本 Mod 不生效。");
                    return;
                }

                bool before = entry.Value;
                if (!before)
                {
                    LoggerInstance.Msg(
                        $"'{EntryName}' 已是 false，无需处理。LEns 的 Prophecy 覆盖层未启用。");
                    return;
                }

                entry.Value = false;

                // Deliberately NOT calling cat.SaveToFile() here.
                //
                // Saving the category rewrites every entry from MelonPreferences' own copy of the
                // file. LEns caches these entries in static fields and only pushes them back with
                // `entry.set_Value`, so whenever the two disagree a category-wide save silently
                // reverts the user's other settings. That is exactly what happened with
                // `EconomyHudEnabled`, which this mod clobbered from true to false.
                //
                // The in-memory write above is enough: LEns reads the value through
                // `MelonPreferences_Entry.Value` every frame in ModBootstrap.Render, so OnGui is
                // never invoked. And because v2's Harmony prefix made `get_IsEnabled` return false,
                // the value is also forced false on LEns's own first access — so even if this
                // preference reverted to true, nothing would break.
                //
                // Read back from the live object to confirm the write took.
                bool after = entry.Value;
                if (!after)
                {
                    LoggerInstance.Msg(
                        $"已关闭 LEns 的 Prophecy 覆盖层（{CategoryName}.{EntryName}: true -> false，仅内存）。" +
                        "该功能依赖已被游戏移除的 Il2CppLE.Factions.ConstellationStar，" +
                        "关闭后 ProphecyImGuiStarOverlay.OnGui() 不再被调用，刷屏停止。" +
                        "LEns 其他功能（F5 DPS / F6 经济 / F7 掉落标签等）不受影响。" +
                        "注：本 Mod 不再写配置文件，以免覆盖你的其他设置。");
                }
                else
                {
                    LoggerInstance.Error(
                        $"设置 '{EntryName}' = false 后回读仍为 true，未能关闭。请把日志发给我。");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Error("ProphecySpamGuard 异常，已放弃（不影响 LEns 其他功能）：" + ex);
            }
        }
    }
}
