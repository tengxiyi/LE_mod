// GroundLabelTransform.cs - restyle LEns's enhanced ground-item label, as pure string work.
//
// WHAT LENS EMITS TODAY (transcribed from GroundAffixLabelFormatter::Format with tools/DumpIl):
//
//     { " " + join(" ", prefixes) }{ NAME }{ LP segment }{ WW segment }{ " " + join(" ", suffixes) }
//
// where each affix segment is exactly
//
//     <color=TIERHEX><sup>TEXT</sup></color>   (a prefix affix)
//     <color=TIERHEX><sub>TEXT</sub></color>   (a suffix affix)
//
// <sup>/<sub> render TEXT small and raise/lower it - the "tiny affixes hugging both sides of the
// name" the user saw. TierHex comes from AffixLabelColors.Tier1To7ColorHex, e.g. "#5AA8FF".
// The LP segment is " <color=LPHEX> N </color>" (LP colour table), WW is " N " plain.
//
// WHAT WE TURN IT INTO
//
//     NAME (and LP/WW segments, untouched - plus any whole-label highlight styling)
//      <size=PCT%><mark=HEX40><color=HEX>affix1</color></mark>  <mark=..>affix2</mark>  ...</size>
//
// i.e. affixes trail the name on the SAME line, sized just under the name's font
// (80% — a touch smaller than the name; the earlier 85% below-name layout made labels
// two lines tall and they overlapped neighbouring labels in item piles), and each
// affix gets its own tier-coloured background chip via TMP's <mark> tag. The
// background reuses the affix's tier colour with extra alpha (40 = 25%), so the colour
// still says "which tier" while the text keeps full-contrast tier colouring on top.
// Each chip also carries its tier as a number ("爆伤 T7") derived from the colour.
//
// On top of that, user rules (GroundLabelRules) can restyle individual chips - background,
// text colour, bold - or highlight a whole label (LP / Weaver's Will / name keyword).
//
// Pure string rules, no Unity/MelonLoader/Harmony, so the standalone harness exercises them
// against hand-built samples of LEns's exact output shape.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HybridMod.Ground
{
    /// <summary>Options for one restyle pass beyond the fixed layout.</summary>
    public sealed class RestyleOptions
    {
        /// <summary>Affix-line font size as a percentage of the label's base (name) size.</summary>
        public int SizePct = GroundLabelTransform.DefaultAffixSizePct;

        /// <summary>User highlight rules; null means "layout only, no highlights".</summary>
        public LabelRuleSet Rules;

        /// <summary>Exact item values captured from the model (0 when absent/unknown).</summary>
        public int ItemLp;
        public int ItemWw;
    }

    /// <summary>Stateless restyling of LEns's ground-item label text.</summary>
    public static class GroundLabelTransform
    {
        /// <summary>
        /// 80% — chips trail the name on the SAME line, one notch below the name's size.
        /// Single-line labels keep the game's own stacking pitch intact (no overlap with
        /// neighbouring labels).
        /// </summary>
        public const int DefaultAffixSizePct = 80;

        /// <summary>
        /// Appends the affix's tier to every chip, e.g. "爆伤 T7". The tier is derived from
        /// LEns's own tier-colour table (colour ↔ tier is a bijection), so the number always
        /// agrees with the chip's colour.
        /// </summary>
        public const bool ShowTierNumbers = true;

        /// <summary>Background alpha appended to the tier hex (25%), as TMP #RRGGBBAA.</summary>
        private const string MarkAlpha = "40";

        /// <summary>LEns's exact per-affix emission shape, prefix form.</summary>
        private static readonly Regex PrefixSeg =
            new(@"<color=([^>]+)><sup>(.*?)</sup></color>", RegexOptions.Compiled);

        /// <summary>LEns's exact per-affix emission shape, suffix form.</summary>
        private static readonly Regex SuffixSeg =
            new(@"<color=([^>]+)><sub>(.*?)</sub></color>", RegexOptions.Compiled);

        /// <summary>Both forms at once, for removal in one pass.</summary>
        private static readonly Regex AnySeg =
            new(@"<color=([^>]+)><(?:sup|sub)>(.*?)</(?:sup|sub)></color>", RegexOptions.Compiled);

        /// <summary>Default-options restyle: layout only, no highlight rules.</summary>
        public static string Restyle(string lensFormatted)
        {
            return Restyle(lensFormatted, new RestyleOptions());
        }

        /// <summary>
        /// Moves LEns's side affixes to a chip line below the name. Returns the input unchanged
        /// whenever the text does not look exactly like LEns's output - a label we do not
        /// understand must render exactly as LEns wrote it, never half-transformed.
        /// </summary>
        public static string Restyle(string lensFormatted, int affixSizePct)
        {
            return Restyle(lensFormatted, new RestyleOptions { SizePct = affixSizePct });
        }

        /// <summary>Full restyle with optional user highlight rules and item context.</summary>
        public static string Restyle(string lensFormatted, RestyleOptions options)
        {
            if (string.IsNullOrEmpty(lensFormatted)) return lensFormatted;
            options ??= new RestyleOptions();

            MatchCollection matches = AnySeg.Matches(lensFormatted);
            if (matches.Count == 0) return lensFormatted;   // no affixes: name/LP/WW only

            var rules = options.Rules;

            var chips = new List<string>(matches.Count);
            foreach (Match m in matches)
            {
                string hex = m.Groups[1].Value;
                string text = m.Groups[2].Value;

                // LEns's affix text is plain (abbreviation or a semantic line). Markup inside it
                // would mean we misparsed the segment nesting - keep LEns's original layout.
                if (text.IndexOf('<') >= 0) return lensFormatted;

                HighlightStyle style = rules?.FirstChipMatch(text, LabelRuleSet.TierOfHex(hex));
                chips.Add(BuildChip(hex, text, style));
            }

            string nameLine = AnySeg.Replace(lensFormatted, string.Empty).Trim();
            if (nameLine.Length == 0) return lensFormatted;

            // Anything unaccounted for (a stray sup/sub outside the recognised shape) means our
            // model of the input is wrong this time; bail out rather than mangle the label.
            if (nameLine.Contains("<sup>") || nameLine.Contains("<sub>")) return lensFormatted;

            HighlightStyle labelStyle = rules?.FirstLabelMatch(options.ItemLp, options.ItemWw > 0, nameLine);
            nameLine = ApplyLabelStyle(nameLine, labelStyle);

            // Chips trail the name on the SAME line: single-line labels keep the game's own
            // stacking pitch, so neighbouring labels never overlap the affix information.
            return $"{nameLine} <size={options.SizePct}%>{string.Join("  ", chips)}</size>";
        }

        /// <summary>
        /// One chip: tier-coloured background (user override wins), tier-coloured text
        /// (user override wins), optional bold, and the tier number appended
        /// ("爆伤 T7") so each affix states its tier explicitly.
        /// </summary>
        private static string BuildChip(string hex, string text, HighlightStyle style)
        {
            string bg = style?.Bg ?? WithAlpha(hex);
            string fg = style?.Fg ?? hex;

            int tier = LabelRuleSet.TierOfHex(hex);
            string display = ShowTierNumbers && tier > 0 ? $"{text} T{tier}" : text;

            string inner = style != null && style.Bold ? $"<b>{display}</b>" : display;
            return $"<mark={bg}><color={fg}>{inner}</color></mark>";
        }

        /// <summary>hex "#RRGGBB" -> "#RRGGBBAA"; other shapes pass through for TMP to judge.</summary>
        private static string WithAlpha(string hex)
        {
            if (hex != null && hex.Length == 7 && hex[0] == '#') return hex + MarkAlpha;
            return hex;
        }

        /// <summary>
        /// Whole-label highlight: bold and/or a full-width mark bar behind the name line and/or a
        /// text colour for the un-coloured part of it (inner colours such as the LP segment win,
        /// deliberately - they are LEns's own semantics).
        /// </summary>
        private static string ApplyLabelStyle(string nameLine, HighlightStyle style)
        {
            if (style == null) return nameLine;

            string inner = nameLine;
            if (style.Fg != null) inner = $"<color={style.Fg}>{inner}</color>";
            if (style.Bg != null) inner = $"<mark={style.Bg}>{inner}</mark>";
            if (style.Bold) inner = $"<b>{inner}</b>";

            return inner == nameLine ? nameLine : inner;
        }
    }
}
