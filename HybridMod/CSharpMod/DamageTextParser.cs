// DamageTextParser.cs - parsing/classification for floating combat text.
//
// Deliberately free of MelonLoader, Harmony and Unity so the standalone harness can exercise it
// without the game running. DamageCapture.cs owns the hook and calls into here.
//
// WHERE THESE RULES COME FROM
// ---------------------------
// Not invented. They are LEns's own rules, read out of `DamageTextHook` with tools/DumpIl, and then
// checked against a real 414 KB `DamageApply_*.log` captured in game.
//
// An earlier version of this file guessed "red = damage dealt, blue = damage taken, everything else
// ignored". That passed 24 self-written tests and would have produced a DPS of exactly zero in
// game, because the game renders NORMAL outgoing damage in WHITE:
//
//     [DamageTextTrace] [DMG] dmg=7  class=OutgoingNormal color=RGBA(1.000, 1.000, 1.000, 1.000)
//     [DamageTextTrace] [DMG] dmg=19 class=OutgoingNormal color=RGBA(1.000, 1.000, 1.000, 1.000)
//
// The lesson: test against observed game data, not against a plausible-sounding model.
//
// LEns's classification (thresholds verbatim from its IL):
//
//     enum DamageClass { OutgoingNormal = 0, OutgoingCrit = 1, IncomingHit = 2 }
//
//     IsRedLike   : r >= 0.70 && g <= 0.35 && b <= 0.35 && (r - g) >= 0.30
//     IsYellowLike: r >= 0.75 && g >= 0.70 && b <= 0.45 && (r - g) >= -0.08
//     IsWhiteLike : r >= 0.90 && g >= 0.90 && b >= 0.90
//                   && |r - g| <= 0.08 && |g - b| <= 0.08
//
//     classify: red -> 2, yellow -> 1, white -> 0, anything else -> 0
//
// i.e. RED is damage taken, YELLOW is a crit the player dealt, WHITE is a normal hit the player
// dealt — and there is no "ignore" outcome at all; unmatched colours fall through to OutgoingNormal.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HybridMod.Capture
{
    /// <summary>Mirrors LEns's <c>DamageClass</c>, with an explicit "not damage" member added.</summary>
    /// <remarks>
    /// LEns has no ignore case, so this enum carries one extra value at the end. Keeping the first
    /// three members numerically identical to the original makes cross-checking against LEns logs
    /// straightforward.
    /// </remarks>
    public enum DamageClass
    {
        /// <summary>White label: a normal hit the player dealt.</summary>
        OutgoingNormal = 0,
        /// <summary>Yellow label: a critical hit the player dealt.</summary>
        OutgoingCrit = 1,
        /// <summary>Red label: damage the player took.</summary>
        IncomingHit = 2,

        /// <summary>
        /// Not damage. This member does not exist in LEns. It is used only for labels that cannot
        /// be a damage float at all (no digits, or a pooled label being cleared), never for a
        /// colour the game might legitimately use — those stay <see cref="OutgoingNormal"/>, exactly
        /// as the original does.
        /// </summary>
        NotDamage = 3,
    }

    /// <summary>Stateless rules for interpreting a combat-text label.</summary>
    public static class DamageTextParser
    {
        /// <summary>
        /// Matches the first number in a float: grouped integers (`1,234`), plain integers, or
        /// decimals. Anchoring on the leading number is correct because the damage value leads the
        /// string; anything after it is decoration (`Critical`, a sprite, a suffix).
        /// </summary>
        private static readonly Regex NumberRegex =
            new(@"(\d{1,3}(?:,\d{3})+|\d+)(?:\.(\d+))?", RegexOptions.Compiled);

        /// <summary>Matches TMP rich-text tags such as `&lt;color=#FF0000&gt;` and `&lt;sprite=3&gt;`.</summary>
        private static readonly Regex MarkupRegex = new(@"<[^>]*>", RegexOptions.Compiled);

        /// <summary>Removes rich-text tags so digits inside markup are never mistaken for damage.</summary>
        public static string StripMarkup(string text) =>
            string.IsNullOrEmpty(text) ? text : MarkupRegex.Replace(text, string.Empty);

        /// <summary>
        /// True when the label is <em>only</em> a number, allowing rich-text markup and surrounding
        /// whitespace.
        /// </summary>
        /// <remarks>
        /// This is the difference between a damage float and a UI label, and it was learned from
        /// evidence: an earlier capture accepted "the first number anywhere in the string" and produced
        /// samples such as
        /// <code>
        /// 6020  &lt;= "远古纪元 · -6020 BE"                      (era label)
        /// 2026  &lt;= "最后纪元 © 2026 Eleventh Hour Games, llc." (copyright notice)
        /// 2024  &lt;= "BiuBiuBiu2024"                            (player name)
        /// 1005  &lt;= "帝国纪元 1005"                             (era label)
        /// </code>
        /// Those inflated the average damage to 557 where LEns's own trace log averaged 19.9. Requiring
        /// the whole label to be a single number excludes them without needing a colour pairing, which
        /// is what LEns relies on instead.
        /// </remarks>
        public static bool IsNumberOnly(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string clean = StripMarkup(text).Replace('\u00A0', ' ').Trim();
            if (clean.Length == 0) return false;

            // The trimmed remainder must be fully consumed by one number (optionally decimal, possibly
            // with a trailing multiplier or percent sign that the game appends).
            Match m = Regex.Match(clean, @"^(\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?\s*[%xX×]?$");
            return m.Success;
        }

        /// <summary>
        /// Extracts the first damage-like number from a label.
        /// </summary>
        /// <remarks>
        /// Rejects zero and non-finite values. A label reading "0" is far more likely to be a pooled
        /// object being cleared than a real hit, and letting zeros through would inflate the hit
        /// count without changing the damage total.
        /// </remarks>
        public static bool TryParseDamage(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            // TMP inserts non-breaking spaces when grouping, so normalise before matching.
            string clean = StripMarkup(text).Replace('\u00A0', ' ');
            Match m = NumberRegex.Match(clean);
            if (!m.Success) return false;

            string digits = m.Groups[1].Value.Replace(",", string.Empty);
            if (m.Groups[2].Success) digits += "." + m.Groups[2].Value;

            if (!float.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return false;

            if (!(parsed > 0f) || float.IsInfinity(parsed)) return false;

            value = parsed;
            return true;
        }

        // ---- LEns's own text rules, transcribed verbatim from DamageTextHook ------------------
        //
        // These are what LEns actually runs (its .cctor and TryParseDamage), and they differ from
        // the generic rules above in one important way: the number regex is ANCHORED with
        // non-digit padding on both sides, so "远古纪元 · -6020 BE" DOES parse as 6020. That is
        // intentional in LEns — it does not filter by "label is only a number" at all; it relies
        // on the GameObject-name guard ("Damage Number(Clone)") in the hook to keep unrelated
        // labels out. Reproduced as-is so both implementations behave identically on the same
        // text, whatever that text is.

        /// <summary>LEns's NumberRegex, verbatim: `^\D*(\d[\d,\.]*)([kKmMbB]?)\D*$`.</summary>
        private static readonly Regex LensNumberRegex =
            new(@"^\D*(\d[\d,\.]*)([kKmMbB]?)\D*$", RegexOptions.Compiled);

        /// <summary>LEns's RichTextRegex, verbatim: `&lt;.*?&gt;`.</summary>
        private static readonly Regex LensRichTextRegex = new("<.*?>", RegexOptions.Compiled);

        /// <summary>
        /// LEns's RgbaRegex, verbatim: three channels plus a mandatory alpha. The colour arrives
        /// as the <c>set_color</c> argument's ToString(), e.g. "RGBA(1.000, 1.000, 1.000, 1.000)".
        /// </summary>
        private static readonly Regex LensRgbaRegex =
            new(@"RGBA\(([\d\.]+),\s*([\d\.]+),\s*([\d\.]+),\s*[\d\.]+\)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>LEns's markup strip + trim, in the same order its HandleTextObject does.</summary>
        public static string StripLensMarkup(string text) =>
            string.IsNullOrEmpty(text) ? text : LensRichTextRegex.Replace(text, string.Empty).Trim();

        /// <summary>
        /// Parses a number the way LEns's <c>TryParseDamage</c> does: anchored regex, strip
        /// thousands separators, invariant float parse, then k/m/b suffix multiplier.
        /// </summary>
        /// <remarks>
        /// Deliberately loose about surrounding non-digits (see the section comment above): the
        /// caller's source guard, not this regex, decides which labels are damage.
        /// </remarks>
        public static bool TryParseLensNumber(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            Match m = LensNumberRegex.Match(text);
            if (!m.Success) return false;

            string digits = m.Groups[1].Value.Replace(",", string.Empty);
            if (!float.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return false;

            float multiplier = 1f;
            string suffix = m.Groups[2].Value;
            if (suffix.Equals("k", StringComparison.OrdinalIgnoreCase)) multiplier = 1000f;
            else if (suffix.Equals("m", StringComparison.OrdinalIgnoreCase)) multiplier = 1000000f;
            else if (suffix.Equals("b", StringComparison.OrdinalIgnoreCase)) multiplier = 1000000000f;

            value = parsed * multiplier;
            return true;
        }

        /// <summary>
        /// Classifies a colour string the way LEns's <c>ClassifyDamageColor</c> does: parse the
        /// RGBA triple with its own regex, then red → incoming, yellow → crit, everything else →
        /// normal. Returns false when the string does not match the RGBA shape at all.
        /// </summary>
        public static bool TryClassifyLensColour(string colourText, out DamageClass cls)
        {
            cls = DamageClass.OutgoingNormal;
            if (string.IsNullOrEmpty(colourText)) return false;

            Match m = LensRgbaRegex.Match(colourText);
            if (!m.Success) return false;

            if (!float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                || !float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                || !float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                return false;

            cls = Classify(r, g, b);
            return true;
        }

        // ---- colour predicates, thresholds copied verbatim from DamageTextHook IL -------------

        /// <summary>`r >= 0.70 &amp;&amp; g &lt;= 0.35 &amp;&amp; b &lt;= 0.35 &amp;&amp; (r - g) >= 0.30` — damage taken.</summary>
        public static bool IsRedLike(float r, float g, float b) =>
            r >= 0.7f && g <= 0.35f && b <= 0.35f && (r - g) >= 0.3f;

        /// <summary>`r >= 0.75 &amp;&amp; g >= 0.70 &amp;&amp; b &lt;= 0.45 &amp;&amp; (r - g) >= -0.08` — a crit dealt.</summary>
        public static bool IsYellowLike(float r, float g, float b) =>
            r >= 0.75f && g >= 0.7f && b <= 0.45f && (r - g) >= -0.08f;

        /// <summary>`r,g,b >= 0.90` and all channels within 0.08 of each other — a normal hit dealt.</summary>
        public static bool IsWhiteLike(float r, float g, float b) =>
            r >= 0.9f && g >= 0.9f && b >= 0.9f
            && Math.Abs(r - g) <= 0.08f && Math.Abs(g - b) <= 0.08f;

        /// <summary>
        /// Classifies a label's colour exactly as LEns does: red = incoming, yellow = crit, white =
        /// normal, everything else falls through to normal.
        /// </summary>
        public static DamageClass Classify(float r, float g, float b)
        {
            if (IsRedLike(r, g, b)) return DamageClass.IncomingHit;
            if (IsYellowLike(r, g, b)) return DamageClass.OutgoingCrit;

            // LEns returns 0 for white *and* for every unmatched colour, so an unusual tint is
            // counted as a normal hit rather than dropped. Preserved deliberately.
            return DamageClass.OutgoingNormal;
        }

        /// <summary>
        /// Parses LEns's own colour notation, e.g. <c>RGBA(1.000, 1.000, 1.000, 1.000)</c>.
        /// </summary>
        /// <remarks>
        /// Used by the harness to replay colour values straight out of a real damage log, so the
        /// classifier is validated against what the game actually produced rather than against
        /// hand-picked numbers.
        /// </remarks>
        public static bool TryParseRgba(string text, out float r, out float g, out float b, out float a)
        {
            r = g = b = a = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            Match m = Regex.Match(text, @"RGBA?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;

            if (!TryFloat(m.Groups[1].Value, out r)) return false;
            if (!TryFloat(m.Groups[2].Value, out g)) return false;
            if (!TryFloat(m.Groups[3].Value, out b)) return false;
            a = m.Groups[4].Success && TryFloat(m.Groups[4].Value, out float parsedA) ? parsedA : 1f;
            return true;
        }

        private static bool TryFloat(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
