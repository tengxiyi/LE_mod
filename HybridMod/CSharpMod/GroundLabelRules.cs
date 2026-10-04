// GroundLabelRules.cs - user-defined highlight rules for the restyled ground labels (pure logic).
//
// The rules live in a plain-text file (Mods\GroundLabelRules.txt) the user edits freely; this
// file parses and applies them. Mods\ is inside this workspace's writable scope, so shipping and
// reading the rules file needs no exception to the iron rule, and hot-reloading is just a re-read.
//
// FILE FORMAT (one rule per line; blank lines and #-comments ignored):
//
//     条件 [样式...]
//
// Conditions that style ONE AFFIX CHIP:
//     text:关键词        chip text contains 关键词 (abbreviation or semantic line)
//     tier>=N            affix tier >= N (1..7, derived from LEns's own tier colour table)
// Conditions that style THE WHOLE LABEL (its name line, chips keep their own styling):
//     lp>=N              Legendary Potential >= N (exact value read off the item model)
//     ww                 the item has Weaver's Will
//     name:关键词        the item name line contains 关键词
//
// Styles (order-free):
//     bg=#RRGGBB or bg=#RRGGBBAA   chip/label background (TMP <mark>); 8-digit form sets alpha
//     fg=#RRGGBB                   text colour
//     bold                         TMP <b>
//
// First matching rule wins; unmatched chips keep LEns's tier colouring. Unknown tokens are
// reported through the parse result instead of being silently ignored.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HybridMod.Ground
{
    /// <summary>A highlight style; null/absent members mean "keep the default".</summary>
    public sealed class HighlightStyle
    {
        public string Bg;     // "#RRGGBB" or "#RRGGBBAA"
        public string Fg;     // "#RRGGBB"
        public bool Bold;
    }

    /// <summary>One parsed rule; <see cref="Match"/> decides what it applies to.</summary>
    public sealed class LabelRule
    {
        public enum Target { ChipText, ChipTier, LabelLp, LabelWw, LabelName }

        public Target Kind;
        public string Keyword;      // ChipText / LabelName
        public int MinValue;        // ChipTier / LabelLp

        public HighlightStyle Style;

        public bool MatchesChip(string chipText, int chipTier)
        {
            switch (Kind)
            {
                case Target.ChipText: return Keyword != null && chipText != null
                        && chipText.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                case Target.ChipTier: return chipTier > 0 && chipTier >= MinValue;
                default: return false;
            }
        }

        public bool MatchesLabel(int lp, bool ww, string nameLine)
        {
            switch (Kind)
            {
                case Target.LabelLp: return lp >= MinValue;
                case Target.LabelWw: return ww;
                case Target.LabelName: return Keyword != null && nameLine != null
                        && nameLine.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }
    }

    /// <summary>A parsed rule file, plus what the parser could not make sense of.</summary>
    public sealed class LabelRuleSet
    {
        public readonly List<LabelRule> Rules = new();
        public readonly List<string> Warnings = new();

        /// <summary>LEns's tier colour table (AffixLabelColors::cctor), hex (no #, upper) -> tier.</summary>
        private static readonly Dictionary<string, int> TierByHex = new(StringComparer.OrdinalIgnoreCase)
        {
            ["B8B8B8"] = 1, ["ECECEC"] = 2, ["52D670"] = 3,
            ["5AA8FF"] = 4, ["E8C840"] = 5, ["C070F0"] = 6, ["FF5050"] = 7,
        };

        public static int TierOfHex(string hex) =>
            hex != null && hex.Length == 7 && hex[0] == '#' && TierByHex.TryGetValue(hex.Substring(1), out int tier)
                ? tier : 0;

        public static LabelRuleSet Parse(string content)
        {
            var set = new LabelRuleSet();
            if (string.IsNullOrEmpty(content)) return set;

            int lineNo = 0;
            foreach (string rawLine in content.Split('\n'))
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                string[] tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                var rule = new LabelRule { Style = new HighlightStyle() };
                bool conditionOk = false;

                string cond = tokens[0];
                if (cond.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = LabelRule.Target.ChipText;
                    rule.Keyword = cond.Substring(5);
                    conditionOk = rule.Keyword.Length > 0;
                }
                else if (cond.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = LabelRule.Target.LabelName;
                    rule.Keyword = cond.Substring(5);
                    conditionOk = rule.Keyword.Length > 0;
                }
                else if (cond.StartsWith("tier>=", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = LabelRule.Target.ChipTier;
                    conditionOk = TryInt(cond.Substring(6), 1, 7, out rule.MinValue);
                }
                else if (cond.StartsWith("lp>=", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = LabelRule.Target.LabelLp;
                    conditionOk = TryInt(cond.Substring(4), 0, 9, out rule.MinValue);
                }
                else if (string.Equals(cond, "ww", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = LabelRule.Target.LabelWw;
                    conditionOk = true;
                }

                if (!conditionOk)
                {
                    set.Warnings.Add($"第 {lineNo} 行无法识别的条件「{cond}」，整行已忽略");
                    continue;
                }

                for (int i = 1; i < tokens.Length; i++)
                {
                    string t = tokens[i];
                    if (string.Equals(t, "bold", StringComparison.OrdinalIgnoreCase))
                    {
                        rule.Style.Bold = true;
                    }
                    else if (t.StartsWith("bg=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryHex(t.Substring(3), out string hex))
                            set.Warnings.Add($"第 {lineNo} 行的 {t} 不是 #RRGGBB(BB) 颜色，已忽略该样式");
                        else rule.Style.Bg = hex;
                    }
                    else if (t.StartsWith("fg=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryHex(t.Substring(3), out string hex))
                            set.Warnings.Add($"第 {lineNo} 行的 {t} 不是 #RRGGBB(BB) 颜色，已忽略该样式");
                        else rule.Style.Fg = hex;
                    }
                    else
                    {
                        set.Warnings.Add($"第 {lineNo} 行无法识别的样式「{t}」，已忽略");
                    }
                }

                set.Rules.Add(rule);
            }

            return set;
        }

        private static bool TryInt(string s, int min, int max, out int value)
        {
            value = 0;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                   && value >= min && value <= max;
        }

        /// <summary>Accepts "#RRGGBB" / "#RRGGBBAA" / "RRGGBB"; normalises to a leading "#".</summary>
        private static bool TryHex(string s, out string hex)
        {
            hex = null;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            hex = "#" + s.ToUpperInvariant();
            return true;
        }

        /// <summary>Human-readable summary for the log: "3 条规则（2 条词缀 / 1 条整签）".</summary>
        public string Describe()
        {
            int chips = 0, labels = 0;
            foreach (var r in Rules)
            {
                if (r.Kind == LabelRule.Target.ChipText || r.Kind == LabelRule.Target.ChipTier) chips++;
                else labels++;
            }
            var sb = new StringBuilder($"{Rules.Count} 条规则（{chips} 条词缀 / {labels} 条整签）");
            if (Warnings.Count > 0) sb.Append($"，{Warnings.Count} 条警告");
            return sb.ToString();
        }

        /// <summary>First rule that targets this chip; file order decides priority. Null = keep defaults.</summary>
        public HighlightStyle FirstChipMatch(string chipText, int chipTier)
        {
            foreach (var r in Rules)
                if (r.MatchesChip(chipText, chipTier)) return r.Style;
            return null;
        }

        /// <summary>First rule that targets this whole label; file order decides priority. Null = keep defaults.</summary>
        public HighlightStyle FirstLabelMatch(int lp, bool ww, string nameLine)
        {
            foreach (var r in Rules)
                if (r.MatchesLabel(lp, ww, nameLine)) return r.Style;
            return null;
        }
    }
}
