// Harness.cs - standalone P/Invoke test host for lens_core.dll.
//
// Why this exists
// ---------------
// `cargo test` proves the *Rust* logic. It cannot prove that the managed decls match the native
// ABI: struct sizes, calling convention, string marshalling, and DllImport resolution are all
// outside its reach. This harness exercises exactly those, in an ordinary console process, so a
// broken bridge is found in one second instead of at game runtime.
//
// It links the very same RustBridge.cs the MelonMod uses, so nothing here can drift from the
// shipping code.
//
// Usage: CSharpHarness <nativeDir> [pathToAffixAbbrevTsv]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HybridMod.Capture;
using HybridMod.Ground;
using HybridMod.Interop;
using HybridMod.Reference;

internal static class Harness
{
    private static int _failures;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string nativeDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RustCore", "target", "release");

        Console.WriteLine("=== lens_core managed bridge harness ===");
        Console.WriteLine($"process      : {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"native dir   : {Path.GetFullPath(nativeDir)}");
        Console.WriteLine();

        // ---- 1. load ---------------------------------------------------------------------
        string err = RustBridge.Initialize(nativeDir);
        if (err != null)
        {
            Console.Error.WriteLine($"FAIL: {err}");
            return 1;
        }
        Console.WriteLine($"loaded       : {RustBridge.ResolvedPath}");

        // ---- 2. ABI self-check ------------------------------------------------------------
        err = RustBridge.ValidateAbi();
        if (err != null) Fail($"ABI validation: {err}");
        else Pass("ABI: struct sizes (24/56) and cdecl calling convention");

        Console.WriteLine($"version      : {RustBridge.Version()}");
        Console.WriteLine($"max input    : {RustBridge.MaxInputBytes()} bytes");
        Console.WriteLine();

        // ---- 3. affix table over real data ------------------------------------------------
        string tsvPath = args.Length > 1 ? args[1] : FindRealTsv();
        if (tsvPath != null && File.Exists(tsvPath))
        {
            Console.WriteLine($"affix table  : {tsvPath}");
            byte[] tsv = File.ReadAllBytes(tsvPath);
            Console.WriteLine($"bytes        : {tsv.Length}");

            long count = RustBridge.CountAffixRecords(tsv);
            Check(count == 1112, $"record count == 1112 (got {count})");

            using (var table = RustBridge.LoadAffixTable(tsv))
            {
                if (table == null || !table.IsValid)
                {
                    Fail("LoadAffixTable returned null");
                }
                else
                {
                    Check(table.Length == 1112, $"table.Length == 1112 (got {table.Length})");
                    Check(table.Skipped == 50, $"table.Skipped == 50 (got {table.Skipped})");

                    // CJK round-trip: proves UTF-8 survives both directions of the boundary.
                    string a1 = table.NameForId(1);
                    Check(a1 != null && a1.Length > 0, $"NameForId(1) is non-empty (got {Show(a1)})");
                    Check(a1 != null && !IsAscii(a1), $"NameForId(1) is CJK, i.e. UTF-8 intact (got {Show(a1)})");

                    // id -> name -> id must be a round trip for a known entry.
                    bool ok = table.TryGetId(a1, out uint back);
                    Check(ok && back == 1, $"TryGetId(\"{Show(a1)}\") == 1 (got {(ok ? back.ToString() : "false")})");

                    // Unknown keys must be reported, not silently mapped to id 0.
                    Check(!table.TryGetId("definitely_not_an_affix", out _), "unknown abbreviation rejected");
                    Check(table.NameForId(999_999u) == null, "unknown id returns null");
                }
            }
        }
        else
        {
            Console.WriteLine("affix table  : (no AffixAbbrev.tsv found; skipping real-data checks)");
        }
        Console.WriteLine();

        // ---- 4. DPS through the boundary --------------------------------------------------
        Console.WriteLine("DPS calculator:");
        using (var dps = RustBridge.CreateDps(5.0f, 0.0f))
        {
            if (dps == null || !dps.IsValid)
            {
                Fail("CreateDps returned null");
            }
            else
            {
                // 10 hits of 100, one every 0.5s, evaluated just after the last.
                for (int i = 0; i < 10; i++) dps.AddSample(i * 0.5f, 100.0);
                Check(dps.TimelineLength == 10, $"timeline length == 10 (got {dps.TimelineLength})");

                dps.Trim(4.5f);
                bool ok = dps.TrySnapshot(4.5f, out LensDpsSnapshot snap);
                Check(ok, "TrySnapshot succeeded");
                Console.WriteLine($"  snapshot   : {snap}");

                // weights 1.0..0.1 => sum 5.5 ; dps = 2 * 550 / 5 = 220
                Check(Math.Abs(snap.TotalDamage - 1000.0) < 1e-9, $"total damage == 1000 (got {snap.TotalDamage})");
                Check(snap.HitCount == 10, $"hit count == 10 (got {snap.HitCount})");
                Check(Math.Abs(snap.CurrentDps - 220.0) < 1e-3, $"current dps == 220 (got {snap.CurrentDps})");
                Check(Math.Abs(snap.CombatDurationSeconds - 4.5) < 1e-6,
                      $"combat duration == 4.5 (got {snap.CombatDurationSeconds})");
                Check(Math.Abs(snap.WindowSeconds - 5.0f) < 1e-6, $"window == 5.0 (got {snap.WindowSeconds})");

                // Integer-count path: crit rate must be exactly representable.
                dps.Reset(0f);
                dps.AddSample(0f, 10.0, isCrit: true);
                dps.AddSample(0f, 10.0);
                dps.TrySnapshot(0f, out snap);
                Check(Math.Abs(snap.CritRate - 0.5) < 1e-12, $"crit rate == 0.5 (got {snap.CritRate})");

                // Incoming damage is tracked separately and must not become a hit.
                dps.Reset(0f);
                dps.AddSample(0f, 777.0, isIncoming: true);
                dps.TrySnapshot(0f, out snap);
                Check(snap.HitCount == 0, $"incoming is not a hit (got {snap.HitCount})");
                Check(Math.Abs(snap.MaxIncomingDamage - 777.0) < 1e-9,
                      $"max incoming == 777 (got {snap.MaxIncomingDamage})");
            }
        }
        Console.WriteLine();

        // ---- 6. combat-text parsing rules ---------------------------------------------------
        // These decide whether a floating label counts as damage. A regex that also matches
        // rich-text tags, or a colour test that accepts healing, yields a plausible-looking DPS
        // number that is simply wrong — so the rules are pinned here rather than eyeballed in game.
        Console.WriteLine("combat-text parsing:");

        void Parses(string text, float expected)
        {
            bool ok = DamageTextParser.TryParseDamage(text, out float v);
            Check(ok && Math.Abs(v - expected) < 1e-3f,
                  $"\"{text}\" -> {expected} (got {(ok ? v.ToString("0.###") : "no match")})");
        }

        void Rejects(string text)
        {
            bool ok = DamageTextParser.TryParseDamage(text, out float v);
            Check(!ok, $"\"{text}\" rejected (got {v})");
        }

        Parses("1234", 1234f);
        Parses("1,234", 1234f);
        Parses("12.5", 12.5f);
        Parses("1,234,567", 1234567f);
        Parses("88<size=20>", 88f);                       // trailing tag
        Parses("<color=#FF0000>450</color>", 450f);       // wrapped in markup
        Parses("450 Critical!", 450f);                     // damage leads the string
        Parses("12\u00A0345", 12f);                        // NBSP acts as a separator, not a grouping

        Rejects("");
        Rejects("   ");
        Rejects("0");
        Rejects("Miss");
        Rejects("<color=#FF0000></color>");                // markup only, no digits
        Rejects("NaN");
        Rejects("Infinity");

        // ---- number-only gate, with the real strings that exposed the bug -------------------
        // These are verbatim captures from a live run in which the matcher took "the first number
        // anywhere in the label". They inflated average damage to 557 where LEns's own log averaged
        // 19.9, and none of them is a damage float, so each is pinned here.
        Console.WriteLine("number-only gate (real false positives):");

        void NotDamage(string text, string why)
        {
            Check(!DamageTextParser.IsNumberOnly(text), $"rejected as not-a-float: {why}");
        }

        NotDamage("远古纪元 · -6020 BE", "era label with a year");
        NotDamage("最后纪元 © 2026 Eleventh Hour Games, llc.", "copyright notice");
        NotDamage("BiuBiuBiu2024", "player name containing digits");
        NotDamage("帝国纪元\n<color=#BAA998><size=60%>1005<spac…", "era label with markup");
        NotDamage("毁灭纪元\n<color=#BAA998><size=60%>1290<spac…", "era label with markup");
        NotDamage("Level 42", "label with words");
        NotDamage("1234 Critical!", "trailing text");
        NotDamage("HP 100/100", "ratio");

        // And the shapes that must still be accepted, including the markup TMP actually emits.
        Check(DamageTextParser.IsNumberOnly("450"), "plain integer");
        Check(DamageTextParser.IsNumberOnly("1,234"), "grouped integer");
        Check(DamageTextParser.IsNumberOnly("12.5"), "decimal");
        Check(DamageTextParser.IsNumberOnly("  88  "), "surrounding whitespace");
        Check(DamageTextParser.IsNumberOnly("<color=#FFFFFF>450</color>"), "wrapped in colour markup");
        Check(DamageTextParser.IsNumberOnly("<size=80%>49</size>"), "wrapped in size markup");
        Console.WriteLine();

        // Colour classification, using LEns's thresholds. Which colour means what is the single
        // easiest thing to get backwards, so each outcome is pinned explicitly.
        Check(DamageTextParser.Classify(1.00f, 1.00f, 1.00f) == DamageClass.OutgoingNormal,
              "white (1,1,1) -> OutgoingNormal");
        Check(DamageTextParser.Classify(0.95f, 0.95f, 0.92f) == DamageClass.OutgoingNormal,
              "near-white -> OutgoingNormal");
        Check(DamageTextParser.Classify(0.90f, 0.85f, 0.20f) == DamageClass.OutgoingCrit,
              "yellow -> OutgoingCrit (crit dealt)");
        Check(DamageTextParser.Classify(1.00f, 0.20f, 0.10f) == DamageClass.IncomingHit,
              "red -> IncomingHit (damage taken)");

        // Predicate boundaries straight from the IL, so an off-by-one in a comparison is caught.
        Check(DamageTextParser.IsRedLike(0.70f, 0.35f, 0.35f), "IsRedLike accepts its lower bound");
        Check(!DamageTextParser.IsRedLike(0.69f, 0.30f, 0.30f), "IsRedLike rejects r < 0.70");
        Check(!DamageTextParser.IsRedLike(0.90f, 0.40f, 0.20f), "IsRedLike rejects g > 0.35");
        Check(!DamageTextParser.IsRedLike(0.90f, 0.65f, 0.20f), "IsRedLike rejects r-g < 0.30");
        Check(DamageTextParser.IsYellowLike(0.75f, 0.70f, 0.45f), "IsYellowLike accepts its bounds");
        Check(!DamageTextParser.IsYellowLike(0.80f, 0.60f, 0.20f), "IsYellowLike rejects g < 0.70");
        Check(!DamageTextParser.IsYellowLike(0.90f, 0.75f, 0.60f), "IsYellowLike rejects b > 0.45");
        Check(DamageTextParser.IsWhiteLike(0.90f, 0.90f, 0.90f), "IsWhiteLike accepts its bounds");
        Check(!DamageTextParser.IsWhiteLike(0.95f, 0.80f, 0.95f), "IsWhiteLike rejects channel spread");

        // Unmatched colours are NOT dropped: LEns falls through to OutgoingNormal, and so do we.
        Check(DamageTextParser.Classify(0.20f, 0.90f, 0.30f) == DamageClass.OutgoingNormal,
              "green (unmatched) still counts as OutgoingNormal, matching LEns");
        Check(DamageTextParser.Classify(0.10f, 0.10f, 0.10f) == DamageClass.OutgoingNormal,
              "black (unmatched) still counts as OutgoingNormal, matching LEns");

        // RGBA parsing, used to replay real log lines.
        Check(DamageTextParser.TryParseRgba("RGBA(1.000, 1.000, 1.000, 1.000)", out float pr, out float pg, out float pb, out float pa),
              "parses LEns RGBA notation");
        Check(Math.Abs(pr - 1f) < 1e-6f && Math.Abs(pg - 1f) < 1e-6f && Math.Abs(pb - 1f) < 1e-6f && Math.Abs(pa - 1f) < 1e-6f,
              "parsed RGBA components are correct");
        Check(!DamageTextParser.TryParseRgba("not a colour", out _, out _, out _, out _), "rejects non-colour text");
        Console.WriteLine();

        // ---- 6a2. LEns's own text rules (the ones the in-game capture now uses) -------------
        // These are transcriptions of DamageTextHook's IL, so the expectation is not "what would be
        // sensible" but "what LEns does". Notably the number regex ACCEPTS surrounding non-digits —
        // LEns keeps era labels parseable and relies on its GameObject-name guard to exclude them.
        // Reproducing that quirk exactly is the point; the name guard is covered in game.
        Console.WriteLine("LEns-exact rules (TryParseLensNumber / TryClassifyLensColour):");

        Check(DamageTextParser.TryParseLensNumber("7", out float ln) && ln == 7f, "plain integer \"7\"");
        Check(DamageTextParser.TryParseLensNumber("1,234", out ln) && ln == 1234f, "grouped \"1,234\"");
        Check(DamageTextParser.TryParseLensNumber("12.5", out ln) && Math.Abs(ln - 12.5f) < 1e-4f, "decimal \"12.5\"");
        Check(DamageTextParser.TryParseLensNumber("  88  ", out ln) && ln == 88f, "surrounding whitespace");
        Check(DamageTextParser.TryParseLensNumber(DamageTextParser.StripLensMarkup("<color=#FFFFFF>450</color>"), out ln) && ln == 450f,
              "markup-wrapped number via StripLensMarkup first (how the capture composes it)");
        Check(!DamageTextParser.TryParseLensNumber("<sprite=3>7", out _),
              "markup containing digits must be stripped first — raw regex would reject");
        Check(DamageTextParser.TryParseLensNumber("3.9k", out ln) && ln == 3900f, "k suffix");
        Check(DamageTextParser.TryParseLensNumber("1.2m", out ln) && ln == 1200000f, "m suffix");
        Check(DamageTextParser.TryParseLensNumber("2b", out ln) && ln == 2000000000f, "b suffix");
        Check(!DamageTextParser.TryParseLensNumber("12abc34", out _), "digits split by letters rejected");
        Check(!DamageTextParser.TryParseLensNumber("", out _), "empty rejected");
        Check(!DamageTextParser.TryParseLensNumber(null, out _), "null rejected");
        Check(DamageTextParser.TryParseLensNumber("远古纪元 · -6020 BE", out ln) && ln == 6020f,
              "era label parses as 6020 — LEns's regex is deliberately loose; its name guard excludes it");
        Check(DamageTextParser.TryParseLensNumber("BiuBiuBiu2024", out ln) && ln == 2024f,
              "player name parses as 2024 for the same reason");

        // Colour strings as they arrive from the set_color argument on the interop struct, i.e.
        // the exact notation observed in LEns's own trace log.
        Check(DamageTextParser.TryClassifyLensColour("RGBA(1.000, 1.000, 1.000, 1.000)", out DamageClass lc)
              && lc == DamageClass.OutgoingNormal, "white RGBA string -> OutgoingNormal");
        Check(DamageTextParser.TryClassifyLensColour("RGBA(0.896, 0.856, 0.241, 1.000)", out lc)
              && lc == DamageClass.OutgoingCrit, "observed crit RGBA(0.896, 0.856, 0.241) -> OutgoingCrit");
        Check(DamageTextParser.TryClassifyLensColour("RGBA(0.915, 0.125, 0.125, 1.000)", out lc)
              && lc == DamageClass.IncomingHit, "observed incoming RGBA(0.915, 0.125, 0.125) -> IncomingHit");
        Check(DamageTextParser.TryClassifyLensColour("not a colour", out _) == false, "non-colour string rejected");
        Check(DamageTextParser.TryClassifyLensColour(null, out _) == false, "null colour rejected");
        Console.WriteLine();

        // ---- 6a3. ground-label restyle (GroundLabelTransform) -------------------------------
        // Input shapes are hand-built to match GroundAffixLabelFormatter::Format's IL exactly:
        // " " + join(" ", prefixes) + NAME + LP/WW + " " + join(" ", suffixes), each affix being
        // <color=TIERHEX><sup|sub>TEXT</sup|sub></color>.
        Console.WriteLine("ground-label restyle (GroundLabelTransform):");

        string lensLabel = " <color=#5AA8FF><sup>爆伤</sup></color>虚空之刃" +
                           " <color=#E8C840><sub>生命</sub></color> <color=#52D670><sub>护甲</sub></color>";
        string styledLabel = GroundLabelTransform.Restyle(lensLabel);
        Check(styledLabel ==
              "虚空之刃 <size=80%>" +
              "<mark=#5AA8FF40><color=#5AA8FF>爆伤 T4</color></mark>  " +
              "<mark=#E8C84040><color=#E8C840>生命 T5</color></mark>  " +
              "<mark=#52D67040><color=#52D670>护甲 T3</color></mark></size>",
              "affixes trail the name on one line with tier-coloured backgrounds");
        Check(!styledLabel.Contains("<sup>") && !styledLabel.Contains("<sub>"), "no sup/sub remains after restyle");

        Check(GroundLabelTransform.Restyle(" <color=#B8B8B8><sup>力量</sup></color>白板之刃")
                  .EndsWith("<mark=#B8B8B840><color=#B8B8B8>力量 T1</color></mark></size>", StringComparison.Ordinal),
              "single prefix alone also restyles");

        Check(GroundLabelTransform.Restyle("虚空之刃 <color=#52D670>LP2</color>") == "虚空之刃 <color=#52D670>LP2</color>",
              "label without affixes passes through untouched");

        Check(GroundLabelTransform.Restyle(" <color=#B8B8B8><sub>x</sub></color>名", 90).Contains("<size=90%>"),
              "affix size percentage is honoured");

        string weird = " <color=#5AA8FF><sup>a</color>b</sup></color>名";
        Check(GroundLabelTransform.Restyle(weird) == weird,
              "unexpected markup inside an affix keeps LEns's layout (never half-transform)");

        Check(GroundLabelTransform.Restyle(null) == null, "null passes through");
        Check(GroundLabelTransform.Restyle("") == "", "empty passes through");
        Console.WriteLine();

        // ---- 6a4. user highlight rules (GroundLabelRules + RestyleOptions) -------------------
        Console.WriteLine("ground-label highlight rules:");

        string rulesText = @"
# comment lines are ignored
tier>=6 bg=#FF3355 fg=#FFFFFF bold
text:暴击 bg=#FFD70040 bold
lp>=1 bg=#FFD70060 fg=#FFD700 bold
this line is garbage
";
        var ruleSet = LabelRuleSet.Parse(rulesText);
        Check(ruleSet.Rules.Count == 3, $"3 valid rules parsed (got {ruleSet.Rules.Count})");
        Check(ruleSet.Warnings.Count == 1, $"garbage line reported as warning (got {ruleSet.Warnings.Count})");
        Check(LabelRuleSet.TierOfHex("#C070F0") == 6, "tier hex table maps T6 colour");
        Check(LabelRuleSet.TierOfHex("#FF5050") == 7, "tier hex table maps T7 colour");
        Check(LabelRuleSet.TierOfHex("#FFFFFF") == 0, "unknown colour maps to tier 0");

        // LEns label with a T6 suffix chip (C070F0), a T4 prefix chip (5AA8FF) and a 2LP segment.
        string ruleLabel = " <color=#5AA8FF><sup>暴击</sup></color>崇高之刃 <color=#C070F0><sub>生命</sub></color> <color=#5AA8FF> 2 </color>";
        var opts = new RestyleOptions { SizePct = 85, Rules = ruleSet, ItemLp = 2, ItemWw = 0 };
        string ruleStyled = GroundLabelTransform.Restyle(ruleLabel, opts);

        Check(ruleStyled.Contains("<mark=#FF3355><color=#FFFFFF><b>生命 T6</b></color></mark>"),
              "tier>=6 rule restyles the T6 chip with bg+fg+bold");
        Check(ruleStyled.Contains("<mark=#FFD70040><color=#5AA8FF><b>暴击 T4</b></color></mark>"),
              "text rule overrides only the background of the matching chip, keeps bold");
        Check(ruleStyled.Contains("<b><mark=#FFD70060><color=#FFD700>崇高之刃  <color=#5AA8FF> 2 </color></color></mark></b>"),
              "lp>=1 rule highlights the whole name line (bold outside, LP colour preserved inside)");
        Check(ruleStyled.Contains("<size=85%>"), "size option still applies with rules");

        // no matching rule -> default tier styling unchanged
        var noMatch = new RestyleOptions
        {
            SizePct = 85,
            Rules = LabelRuleSet.Parse("lp>=3 bg=#FFFFFF"),
            ItemLp = 2,
        };
        Check(GroundLabelTransform.Restyle(ruleLabel, noMatch) == GroundLabelTransform.Restyle(ruleLabel, 85),
              "labels that match no rule render exactly like the plain restyle");

        // ww rule needs the model-captured flag
        var wwRules = LabelRuleSet.Parse("ww bg=#40C0FF40 bold");
        string wwStyled = GroundLabelTransform.Restyle("织者之刃 <color=#E8C840><sub>护甲</sub></color>",
            new RestyleOptions { Rules = wwRules, ItemWw = 3 });
        Check(wwStyled.Contains("<b><mark=#40C0FF40>织者之刃</mark></b>"), "ww rule highlights the name line");
        string noWw = GroundLabelTransform.Restyle("织者之刃 <color=#E8C840><sub>护甲</sub></color>",
            new RestyleOptions { Rules = wwRules, ItemWw = 0 });
        Check(!noWw.Contains("#40C0FF40"), "ww rule does not fire without Weaver's Will");
        Console.WriteLine();

        // ---- 6b. replay REAL colour values from an in-game damage log -----------------------
        // This is the check that matters. An earlier version of the classifier assumed "red = dealt,
        // blue = taken, white = ignore" — it passed every hand-written test and would have produced a
        // DPS of exactly zero, because the game renders normal hits in WHITE. Replaying observed
        // values is what catches that class of error.
        string logPath = args.Length > 2 ? args[2] : FindDamageLog();
        if (logPath != null && File.Exists(logPath))
        {
            Console.WriteLine($"real damage log : {logPath}");
            var counts = new Dictionary<string, int>();
            int lines = 0, parsed = 0;

            foreach (string line in File.ReadLines(logPath))
            {
                if (!line.Contains("[DamageTextTrace]")) continue;
                lines++;

                // e.g. "... [DMG] dmg=7 class=OutgoingNormal color=RGBA(1.000, 1.000, 1.000, 1.000)"
                int dmgAt = line.IndexOf("dmg=", StringComparison.Ordinal);
                int clsAt = line.IndexOf("class=", StringComparison.Ordinal);
                int colAt = line.IndexOf("color=", StringComparison.Ordinal);
                if (dmgAt < 0 || clsAt < 0 || colAt < 0) continue;

                string dmgText = Token(line, dmgAt + 4);
                string clsText = Token(line, clsAt + 6);
                string colText = line.Substring(colAt + 6).Trim();

                if (!DamageTextParser.TryParseRgba(colText, out float r, out float g, out float b, out _)) continue;
                if (!DamageTextParser.TryParseDamage(dmgText, out float dmg)) { counts["<unparsed>"] = counts.GetValueOrDefault("<unparsed>") + 1; continue; }

                DamageClass got = DamageTextParser.Classify(r, g, b);
                string key = $"{clsText} -> {got}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
                parsed++;
            }

            Console.WriteLine($"  trace lines {lines}, parsed {parsed}");
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                Console.WriteLine($"    {kv.Key,-44} x{kv.Value}");

            // Every observed label must map to the class LEns itself recorded for it.
            var wrong = counts.Keys
                .Where(k => !k.StartsWith("OutgoingNormal -> OutgoingNormal")
                         && !k.StartsWith("OutgoingCrit -> OutgoingCrit")
                         && !k.StartsWith("IncomingHit -> IncomingHit")
                         && k != "<unparsed>")
                .ToList();
            Check(wrong.Count == 0, $"replayed colours agree with LEns's own class field (mismatches: {string.Join("; ", wrong)})");
            Check(parsed > 0, "at least one trace line was parsed and classified");
            Check(counts.ContainsKey("OutgoingNormal -> OutgoingNormal"),
                  "real logs contain white OutgoingNormal hits (the case the first classifier got wrong)");
        }
        else
        {
            Console.WriteLine("real damage log : (none found; skipping the replay check)");
        }
        Console.WriteLine();

        // ---- 6c. differential test: Rust core vs a literal C# transcription of LEns ----------
        // This is the check that actually validates the *port*. The Rust unit tests cannot do it: their
        // expected values came from the same IL reading that produced the Rust code, so a
        // misinterpretation would pass every one of them. Here the same real events are pushed through
        // both implementations and every counter must agree exactly.
        if (logPath != null && File.Exists(logPath))
        {
            const float Window = 5.0f;
            var events = ParseDamageLog(logPath);
            Console.WriteLine($"differential test : {events.Count} real damage events, window {Window}s");

            if (events.Count == 0)
            {
                Fail("parsed no damage events from the log");
            }
            else
            {
                var reference = new DpsReference(Window);
                using var rust = RustBridge.CreateDps(Window, events[0].Time);

                // Checkpoint periodically so a divergence is caught near where it starts, not only at
                // the end. The final checkpoint is also exercised after the last event.
                int checkpoints = 0, mismatches = 0;
                string firstMismatch = null;

                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    reference.AddSample(new RefDamageSample
                    {
                        Time = e.Time, Amount = e.Amount, IsIncoming = e.Kind == DamageClass.IncomingHit,
                        IsCrit = e.Kind == DamageClass.OutgoingCrit,
                    });
                    rust.AddSample(e.Time, e.Amount,
                        isIncoming: e.Kind == DamageClass.IncomingHit,
                        isCrit: e.Kind == DamageClass.OutgoingCrit);

                    bool last = i == events.Count - 1;
                    if (!last && i % 25 != 0) continue;

                    float now = e.Time;
                    reference.TrimExpiredPoints(now);
                    rust.Trim(now);

                    RefDpsSnapshot r = reference.GetSnapshot(now);
                    rust.TrySnapshot(now, out LensDpsSnapshot s);
                    checkpoints++;

                    var diffs = new List<string>();
                    if (r.HitCount != s.HitCount) diffs.Add($"hits {r.HitCount} vs {s.HitCount}");
                    if (r.CritCount != s.CritCount) diffs.Add($"crits {r.CritCount} vs {s.CritCount}");
                    if (!Near(r.TotalDamage, s.TotalDamage, 1e-6)) diffs.Add($"total {r.TotalDamage} vs {s.TotalDamage}");
                    if (!Near(r.MaxIncomingDamage, s.MaxIncomingDamage, 1e-6)) diffs.Add($"maxInc {r.MaxIncomingDamage} vs {s.MaxIncomingDamage}");
                    if (!Near(r.CritRate, s.CritRate, 1e-12)) diffs.Add($"critRate {r.CritRate} vs {s.CritRate}");
                    if (!Near(r.CurrentDps, s.CurrentDps, 1e-6)) diffs.Add($"dps {r.CurrentDps:F6} vs {s.CurrentDps:F6}");
                    if (!Near(r.CombatDurationSeconds, s.CombatDurationSeconds, 1e-4)) diffs.Add($"combat {r.CombatDurationSeconds} vs {s.CombatDurationSeconds}");
                    if (reference.TimelineLength != rust.TimelineLength) diffs.Add($"timeline {reference.TimelineLength} vs {rust.TimelineLength}");

                    if (diffs.Count > 0)
                    {
                        mismatches++;
                        firstMismatch ??= $"at event {i} (t={e.Time:F3}s): " + string.Join("; ", diffs);
                    }
                }

                Console.WriteLine($"  checkpoints {checkpoints}, mismatches {mismatches}");
                if (mismatches > 0) Console.WriteLine($"  first mismatch: {firstMismatch}");

                Check(mismatches == 0,
                      "Rust core matches a literal C# transcription of LEns over real recorded data");

                // Show the final numbers so the magnitude is visible, not just the verdict.
                float endNow = events[^1].Time;
                RefDpsSnapshot final = reference.GetSnapshot(endNow);
                Console.WriteLine($"  final (t={endNow:F3}s): {final.HitCount} hits, " +
                                  $"{final.TotalDamage:F1} total damage, {final.CurrentDps:F2} dps, " +
                                  $"crit rate {final.CritRate:P1}");
            }
        }
        Console.WriteLine();

        // ---- 6d. HitEvents flags ------------------------------------------------------------
        // The crit flag decides the crit rate, and a missed flag would simply read low with nothing to
        // indicate why — so the observed game values are asserted explicitly.
        Console.WriteLine("HitEvents flags:");

        Check(!DamageNumberFlags.IsCrit(DamageNumberFlags.None), "None (0) is not a crit");
        Check(!DamageNumberFlags.IsCrit(DamageNumberFlags.Hit), "Hit (1) alone is not a crit");
        Check(DamageNumberFlags.IsCrit(DamageNumberFlags.Crit), "Crit (2) is a crit");
        Check(DamageNumberFlags.IsCrit(DamageNumberFlags.SuperCrit), "SuperCrit (256) is a crit");
        Check(DamageNumberFlags.IsCrit(DamageNumberFlags.Crit | DamageNumberFlags.MeleeHit),
              "Crit combined with MeleeHit (2|64) is a crit");
        Check(!DamageNumberFlags.IsCrit(DamageNumberFlags.Kill | DamageNumberFlags.Stun),
              "Kill|Stun (4|16) is not a crit");

        Check(DamageNumberFlags.Describe(DamageNumberFlags.None) == "None", "Describe(None)");
        Check(DamageNumberFlags.Describe(DamageNumberFlags.Crit | DamageNumberFlags.MeleeHit) == "Crit|MeleeHit",
              "Describe(Crit|MeleeHit) lists both members");
        Check(DamageNumberFlags.Describe(1 << 20).Contains("未识别"),
              "an unknown flag is reported as unrecognised rather than silently ignored");

        // Documented limitation: the flags cannot express direction, so nothing may claim they do.
        Check(!DamageNumberFlags.IsIncoming(DamageNumberFlags.Crit),
              "IsIncoming is always false: HitEvents has no dealt/taken flag");
        Console.WriteLine();

        // ---- 7. error paths must not crash the process -------------------------------------
        Console.WriteLine("error paths:");
        Check(RustBridge.CountAffixRecords(null) == RustBridge.ErrNull, "null buffer -> LENS_ERR_NULL");
        Check(RustBridge.LoadAffixTable(new byte[0]) == null, "empty buffer -> null table");
        Check(RustBridge.CreateDps(5.0f, 0f) != null, "CreateDps twice is allowed");
        Check(RustBridge.CountAffixRecords(new byte[] { 0xFF, 0xFE }) == RustBridge.ErrUtf8,
              "invalid UTF-8 -> LENS_ERR_UTF8");
        Pass("no error path threw or crashed");
        Console.WriteLine();

        // ---- summary ------------------------------------------------------------------------
        if (_failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED");
            RustBridge.Shutdown();
            return 0;
        }
        Console.Error.WriteLine($"{_failures} CHECK(S) FAILED");
        RustBridge.Shutdown();
        return 1;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void Check(bool condition, string what)
    {
        if (condition) Pass(what);
        else Fail(what);
    }

    private static void Pass(string what) => Console.WriteLine($"  OK   {what}");

    private static void Fail(string what)
    {
        _failures++;
        Console.Error.WriteLine($"  FAIL {what}");
    }

    private static string Show(string s) => s == null ? "<null>" : (s.Length > 24 ? s.Substring(0, 24) + "…" : s);

    private static bool IsAscii(string s)
    {
        foreach (char c in s) if (c > 127) return false;
        return true;
    }

    /// <summary>A single observed damage float, recovered from a LEns damage-apply log.</summary>
    private readonly struct LoggedDamage
    {
        public readonly float Time;
        public readonly double Amount;
        public readonly DamageClass Kind;

        public LoggedDamage(float time, double amount, DamageClass kind)
        {
            Time = time; Amount = amount; Kind = kind;
        }
    }

    /// <summary>
    /// Recovers <c>[DamageTextTrace]</c> events from a LEns damage-apply log.
    /// </summary>
    /// <remarks>
    /// Timestamps come from the log's own <c>[HH:mm:ss.fff]</c> prefix, so the ordering and spacing
    /// are the game's real cadence rather than a synthetic one. The class field is LEns's own
    /// classification, which lets the differential test drive both implementations from the same
    /// ground truth instead of from our classifier.
    /// </remarks>
    private static List<LoggedDamage> ParseDamageLog(string path)
    {
        var result = new List<LoggedDamage>();
        float origin = 0f;
        bool haveOrigin = false;

        foreach (string line in File.ReadLines(path))
        {
            if (!line.Contains("[DamageTextTrace]")) continue;

            int dmgAt = line.IndexOf("dmg=", StringComparison.Ordinal);
            int clsAt = line.IndexOf("class=", StringComparison.Ordinal);
            if (dmgAt < 0 || clsAt < 0) continue;

            string dmgText = Token(line, dmgAt + 4);
            string clsText = Token(line, clsAt + 6);

            if (!DamageTextParser.TryParseDamage(dmgText, out float dmg)) continue;

            DamageClass kind = clsText switch
            {
                "IncomingHit" => DamageClass.IncomingHit,
                "OutgoingCrit" => DamageClass.OutgoingCrit,
                _ => DamageClass.OutgoingNormal,
            };

            // "[01:24:59.916] ..." -> seconds since the log began.
            float abs = ParseLogTime(line);
            if (!haveOrigin && abs >= 0f) { origin = abs; haveOrigin = true; }
            result.Add(new LoggedDamage(abs - origin, dmg, kind));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>Parses the leading <c>[HH:mm:ss.fff]</c> of a log line into seconds, or -1.</summary>
    private static float ParseLogTime(string line)
    {
        int open = line.IndexOf('[');
        int close = open < 0 ? -1 : line.IndexOf(']', open + 1);
        if (open < 0 || close < 0) return -1f;

        string stamp = line.Substring(open + 1, close - open - 1);
        string[] parts = stamp.Split(':', '.');
        if (parts.Length != 4) return -1f;

        if (!int.TryParse(parts[0], out int h)) return -1f;
        if (!int.TryParse(parts[1], out int m)) return -1f;
        if (!int.TryParse(parts[2], out int s)) return -1f;
        if (!int.TryParse(parts[3], out int ms)) return -1f;

        return h * 3600f + m * 60f + s + ms / 1000f;
    }

    /// <summary>Relative-difference comparison that treats identical non-finite values as equal.</summary>
    private static bool Near(double a, double b, double tol)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        if (double.IsInfinity(a) || double.IsInfinity(b)) return a.Equals(b);
        double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= tol * scale;
    }

    /// <summary>Reads the whitespace-delimited token starting at <paramref name="from"/>.</summary>
    private static string Token(string line, int from)
    {
        int end = from;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '"') end++;
        return line.Substring(from, end - from);
    }

    /// <summary>
    /// Finds the newest LEns damage-apply log, which contains real <c>[DamageTextTrace]</c> lines.
    /// </summary>
    private static string FindDamageLog()
    {
        // Newest sample wins: the live game Mods folder first, then the archived golden logs
        // (cleaned out of the game's Mods folder so it ships clean).
        foreach (string dir in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\Mods",
                     @"C:\Program Files\Steam\steamapps\common\Last Epoch\Mods",
                     @"C:\Users\tomas\Documents\LastEpoch_Mod_Workspace\out\damage_logs",
                 })
        {
            if (!Directory.Exists(dir)) continue;

            string newest = Directory.GetFiles(dir, "DamageApply_*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest != null) return newest;
        }
        return null;
    }

    /// <summary>Looks for the real table in the usual Last Epoch install location.</summary>
    private static string FindRealTsv()
    {
        foreach (string root in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common\Last Epoch",
                     @"C:\Program Files\Steam\steamapps\common\Last Epoch",
                     // archived golden samples (cleaned out of the game's Mods folder)
                     @"C:\Users\tomas\Documents\LastEpoch_Mod_Workspace\out\damage_logs",
                 })
        {
            string p = Path.Combine(root, "Mods", "AffixAbbrev.tsv");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
