using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 2A Batch 00-B — THE EVIDENCE GUARD.
    //
    // This class exists because of the single most expensive testing failure in Stage 1: `CROSSBUY_TEST_SQL` was
    // unset, so 46 SQL tests reported **Skipped** for two batches while being counted as coverage. Their first real
    // execution failed with twenty `Invalid column name` errors. A total pass count is not evidence — RISK-026.
    //
    // These tests are DELIBERATELY not gated on CROSSBUY_TEST_SQL. They must run everywhere, because their whole
    // purpose is to catch the case where the SQL-gated tests are silently absent.
    public class Batch00EvidenceGuardTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static JsonDocument Load(string relative)
        {
            var path = Path.Combine(RepoRoot(), "engineering", relative);
            Assert.True(File.Exists(path), $"{relative} is missing — Batch 00 requires it to exist");
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        // ============================================================================================
        // 1. The manifest names real tests — a rename cannot silently drop evidence
        // ============================================================================================

        // THE CORE GUARD. Every non-planned test named in the manifest must EXIST as a method in this assembly.
        // A rename that is not reflected in the manifest fails here, which is what stops evidence disappearing
        // quietly. Stage 1's 46 skipped tests would not have been caught by a count; they would be caught by this.
        [Fact]
        public void Every_required_evidence_test_named_in_the_manifest_exists_in_this_assembly()
        {
            using var doc = Load("required-evidence-manifest.json");

            var methods = typeof(Batch00EvidenceGuardTests).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods())
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            var missing = new List<string>();
            foreach (var group in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                // groups carrying a "status" are PLANNED for a later batch and are not yet expected to exist
                if (group.TryGetProperty("status", out _)) continue;

                var cls = group.GetProperty("class").GetString()!;
                foreach (var test in group.GetProperty("tests").EnumerateArray())
                {
                    var name = test.GetString()!;
                    if (!methods.Contains(name)) missing.Add($"{cls}.{name}");
                }
            }

            Assert.True(missing.Count == 0,
                "Required evidence tests named in the manifest do not exist. Either the test was renamed without " +
                "updating engineering/required-evidence-manifest.json, or it was deleted:\n  " +
                string.Join("\n  ", missing));
        }

        [Fact]
        public void The_manifest_declares_the_sql_environment_variable_and_the_no_count_rule()
        {
            using var doc = Load("required-evidence-manifest.json");

            Assert.True(doc.RootElement.GetProperty("requiredEnvironment")
                .TryGetProperty("CROSSBUY_TEST_SQL", out _),
                "the manifest must declare CROSSBUY_TEST_SQL — without it every SQL proof reports skipped");

            var rules = string.Join(" ", doc.RootElement.GetProperty("rules").EnumerateArray()
                .Select(x => x.GetString()));
            Assert.Contains("NOT DISCOVERED", rules, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Skipped", rules, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================================================
        // 2. The authorization debt baseline is shrink-only
        // ============================================================================================

        [Fact]
        public void The_authorization_baseline_matches_the_frozen_gap_count_and_may_only_shrink()
        {
            using var doc = Load("authorization-baseline.json");
            var root = doc.RootElement;

            int frozen = root.GetProperty("frozenBaseline").GetProperty("gaps").GetInt32();
            int count = root.GetProperty("count").GetInt32();
            int entries = root.GetProperty("entries").GetArrayLength();

            Assert.Equal(count, entries);

            // SHRINK-ONLY: the baseline may fall below the frozen figure as waves protect endpoints, but it may
            // never rise. A rise means a new unprotected mutating action was appended instead of being fixed.
            Assert.True(count <= frozen,
                $"the authorization baseline GREW from {frozen} to {count}. A new unprotected mutating action must " +
                "fail the build, not be added to the baseline.");
        }

        [Fact]
        public void Every_baseline_entry_carries_traceability_and_no_blanket_suppression_exists()
        {
            using var doc = Load("authorization-baseline.json");

            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = e.GetProperty("id").GetString();
                foreach (var required in new[] { "controller", "action", "httpMethod", "module", "file",
                                                 "classification", "targetWave", "addedBy", "reason" })
                {
                    Assert.True(e.TryGetProperty(required, out var v) && !string.IsNullOrWhiteSpace(v.ToString()),
                        $"baseline entry {id} is missing '{required}' — every allowance must be traceable");
                }
            }

            var rules = string.Join(" ", doc.RootElement.GetProperty("rules").EnumerateArray()
                .Select(x => x.GetString()));
            Assert.Contains("only SHRINK", rules, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No blanket suppression", rules, StringComparison.OrdinalIgnoreCase);

            // A rename must not become a silent allowance — the rule has to be stated, because the mechanism
            // (id = Controller.Action) makes a rename look like a brand-new unlisted action, which is correct.
            Assert.Contains("Renaming", rules, StringComparison.OrdinalIgnoreCase);
        }

        // The two anonymous-by-design logins are the ONLY entries permitted to be classified as anything other
        // than a gap. Anything else claiming AnonymousByDesign is an escape hatch and must be reviewed.
        [Fact]
        public void Only_the_two_login_actions_are_classified_anonymous_by_design()
        {
            using var doc = Load("authorization-baseline.json");

            var anon = doc.RootElement.GetProperty("entries").EnumerateArray()
                .Where(e => e.GetProperty("classification").GetString() == "AnonymousByDesign")
                .Select(e => e.GetProperty("id").GetString())
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "AccountController.Login", "AuthApiController.Login" }, anon);
        }

        // ============================================================================================
        // 3. The frozen Phase 0 baseline still reconciles against the committed evidence
        // ============================================================================================

        [Fact]
        public void The_frozen_phase_0_figures_still_reconcile()
        {
            using var doc = Load("authorization-baseline.json");
            var f = doc.RootElement.GetProperty("frozenBaseline");

            int mutating = f.GetProperty("mutating").GetInt32();
            int attr = f.GetProperty("attributeProtected").GetInt32();
            int inBody = f.GetProperty("inBodyProtected").GetInt32();
            int gaps = f.GetProperty("gaps").GetInt32();

            // Stage 2A Batch A added three mutating endpoints, all authorized in-body through the new
            // IPlatformGrantWriter. The DEBT did not move, which is the property this guard exists to protect:
            // 391 = 157 + 91 + 143, and the 143 entries are byte-identical to Stage 1's.
            // frozenBaselineHistory in the file records the change and its reason.
            //
            // NEW-MACHINE RECONCILIATION (TAB-1, 2026-08-12): the descriptive totals move again, to
            // 410 = 157 + 110 + 143, for the nineteen protected endpoints the modernization work had already
            // added to the tree without recording them — seven Reporting write endpoints through
            // IReportAuthorizationService, eleven Tasks endpoints through ITasksAccessService, and
            // CalendarController.SaveSchedule through ICalendarAccessService. ATTRIBUTE-PROTECTED AND DEBT ARE
            // BOTH UNMOVED, which is the whole point: +19 mutating met by +19 in-body protected is what adding
            // protected endpoints looks like. Verified independently of the analyzer by differencing the gap set
            // rebuilt from docs/architecture/evidence/Roslyn-Authorization-Inventory.csv against this file's
            // entries — 143 vs 143, zero ids on either side alone.
            // ============================ WHY THERE ARE NO LITERALS HERE ============================
            //
            // Three frozen literals stood here - mutating, attributeProtected and inBodyProtected - and every
            // legitimate growth of the codebase turned this test red until somebody edited them. That is the
            // wrong failure to teach: the habit it builds is "the guard went red, bump the number", and a real
            // regression arrives wearing exactly that costume. The literals also went stale on their own,
            // 481 -> 566 and 170 -> 255 -> 263, faster than anyone reconciled them.
            //
            // Worse, they could not see the defect they were meant to see. Moving entries BETWEEN the two
            // protected buckets preserves the sum, so the identity below is blind to a misclassification and
            // only ever catches a miscount. That is not hypothetical: six SEC-01 endpoints were carried as
            // in-body while every one of them holds an [ApiPerm(...)] in source.
            //
            // The numbers are therefore asserted against THE FILE'S OWN RECORD instead of against this source.
            // =======================================================================================
            int anonymous = f.TryGetProperty("anonymousByDesign", out var a) ? a.GetInt32() : 0;

            // THE IDENTITY. Kept, with its known limit stated above: it catches a miscount, never a move.
            Assert.Equal(mutating, attr + inBody + anonymous + gaps);
            Assert.Equal(gaps, doc.RootElement.GetProperty("entries").GetArrayLength());

            // THE RATCHET, DERIVED RATHER THAN TYPED. This used to read Assert.InRange(gaps, 0, 94): a ceiling
            // a person maintained, which means a ceiling a person could raise. It is now taken from the file's
            // own frozenBaselineHistory - debt may never exceed the LOWEST level history has ever recorded -
            // and the ceiling tightens by itself each time a record books a lower one.
            var history = doc.RootElement.GetProperty("frozenBaselineHistory");
            var recordedGapLevels = new List<int>();
            var reconciled = 0;
            var unreadable = new List<string>();
            var partial = new List<string>();
            int[]? newest = null;

            foreach (var record in history.EnumerateArray())
            {
                // The records are not uniform: the older ones carry their totals at the root, three carry
                // them as prose strings, and the recent ones nest them under "measured". Only numeric
                // totals can be checked, and the two assertions after the loop account for every record
                // that could not be - none is allowed to disappear between them.
                var totals = Totals(record);
                if (!TryTotal(totals, "gaps", out var hGaps)) { unreadable.Add(Stage(record)); continue; }
                recordedGapLevels.Add(hGaps);

                if (!TryTotal(totals, "mutating", out var hMut)) { partial.Add(Stage(record)); continue; }
                if (!TryTotal(totals, "attributeProtected", out var hAttr)) { partial.Add(Stage(record)); continue; }
                if (!TryTotal(totals, "inBodyProtected", out var hBody)) { partial.Add(Stage(record)); continue; }
                TryTotal(totals, "anonymousByDesign", out var hAnon);

                var stage = Stage(record);
                Assert.True(hMut == hAttr + hBody + hAnon + hGaps,
                    $"frozenBaselineHistory record '{stage}' does not reconcile: " +
                    $"{hAttr} + {hBody} + {hAnon} + {hGaps} != {hMut}");
                reconciled++;
                newest = new[] { hMut, hAttr, hBody, hAnon, hGaps };
            }

            // A GUARD THAT CHECKS NOTHING PASSES - but the check for that must not itself depend on which
            // tree it runs against. This read `reconciled >= 21`, a count measured on a working tree that
            // carried three uncommitted records; against HEAD only 18 reconcile and the guard failed for
            // being on the wrong tree rather than for anything being wrong. SUCCESSES ARE TREE-DEPENDENT BY
            // DEFINITION, because records are appended as work lands, so no floor on them is portable.
            //
            // The SKIPS are portable. Exactly three records state their gaps as prose - "143 (unchanged) -
            // no unprotected endpoint was added", and two like it - and those three are present at HEAD and
            // in any working tree, because history is only ever appended to. So the unreadable set is
            // bounded instead: a fourth record the guard cannot read is a regression on every tree, whether
            // it arrives as a new prose total or as a shape this test has not learned to read.
            Assert.True(unreadable.Count == 3,
                $"{unreadable.Count} history records could not be read, expected the 3 known prose-total "
                + "records: " + string.Join(" | ", unreadable));
            Assert.NotEmpty(recordedGapLevels);

            // A RECORD READS COMPLETELY OR NOT AT ALL. The three reads above used to fall through on a bare
            // `continue`, so a record stating a numeric gaps but a missing or prose `mutating` would feed
            // the ceiling and then never be identity-checked - counted in neither list, reported by nothing.
            // That is the same shape as the defect this whole test exists for: a number trusted because no
            // assertion happened to look at it.
            Assert.True(partial.Count == 0,
                "these history records state a debt level but not the totals that must reconcile with it: "
                + string.Join(" | ", partial));

            // And every record is in exactly one of the three buckets, which is what stops a future edit to
            // the loop losing one silently. `reconciled` exists to be counted here; without this it was an
            // incremented variable nothing read, which reads as a live check and is not one.
            Assert.Equal(history.GetArrayLength(), reconciled + unreadable.Count + partial.Count);

            // THE CEILING MUST BE TIED TO REALITY, NOT MERELY BE A CEILING. This test shipped with the
            // ratchet reading only the root of each record, so the five records that nest their totals
            // under "measured" were invisible and the ceiling stood at 93 against a live debt of 73 - every
            // closure since R1 unseen. Debt could have climbed 20 and `gaps <= ceiling` would have passed
            // the whole way. A guard can be live and slack at once, and mutation-testing that it FIRES does
            // not show that it fires at the right height.
            //
            // So the live header must equal the NEWEST recorded state. A baseline edit without a record now
            // fails here instead of quietly loosening the ratchet, which is the governance property this
            // file was always reaching for.
            Assert.True(newest is not null, "no history record states a complete set of totals");
            Assert.Equal(new[] { mutating, attr, inBody, anonymous, gaps }, newest);

            var ceiling = recordedGapLevels.Min();
            Assert.True(gaps <= ceiling,
                $"authorization debt rose: {gaps} gaps against a recorded floor of {ceiling}. " +
                "This baseline may only shrink.");
        }

        /// Names a record for a failure message. Unnamed records exist, so this never returns null.
        private static string Stage(JsonElement record)
            => record.TryGetProperty("stage", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()! : "(unnamed record)";

        /// WHERE A RECORD KEEPS ITS TOTALS. The older records are flat; every record since R1 nests them
        /// under "measured", which is the state that record LEFT BEHIND. "previous" is deliberately never
        /// consulted: it is the state before the record's own change, so a ratchet fed from it would
        /// measure debt the record itself removed and would never tighten.
        private static JsonElement Totals(JsonElement record)
        {
            if (record.TryGetProperty("gaps", out var atRoot) && atRoot.ValueKind == JsonValueKind.Number)
                return record;
            return record.TryGetProperty("measured", out var measured)
                && measured.ValueKind == JsonValueKind.Object ? measured : record;
        }

        /// Reads one total from a history record, accepting only a real number. Records carrying their totals
        /// as strings are skipped rather than coerced: a figure nobody typed as a number is a figure nobody
        /// measured, and parsing it silently would let the weakest records drive the ratchet.
        private static bool TryTotal(JsonElement record, string name, out int value)
        {
            value = 0;
            return record.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out value);
        }

        // ============================================================================================
        // 3. The debt entries still describe real, still-unprotected code
        // ============================================================================================

        // THE ENTRY LIST IS THE DEBT, so counting it is not enough. An entry whose action was renamed or
        // deleted keeps the total looking honest while protecting nothing, and an entry that has since
        // acquired an attribute inflates the debt and hides how much is genuinely open. Either way the number
        // above becomes a story rather than a measurement, so every entry is resolved against its own source.
        [Fact]
        public void Every_baseline_entry_still_names_a_real_and_still_unprotected_action()
        {
            using var doc = Load("authorization-baseline.json");
            var root = RepoRoot();

            // Attributes are written both bare and fully qualified in this codebase ([ApiPerm] and
            // [CrossBuy.Models.ApiPerm]), which is why the prefix is optional here.
            var authorization = new Regex(@"\[\s*(?:[\w.]+\.)?(?:\w*Perm\w*|Authorize)\s*[\(\]]",
                RegexOptions.Compiled);

            var unresolved = new List<string>();
            var protectedAfterAll = new List<string>();
            var examined = 0;

            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                var relative = entry.GetProperty("file").GetString()!;
                var action = entry.GetProperty("action").GetString()!;
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path)) { unresolved.Add($"{id}: no such file '{relative}'"); continue; }

                var lines = File.ReadAllLines(path);
                var declaration = new Regex(
                    @"\b(?:public|internal|protected)\s[^\n;{]*\b" + Regex.Escape(action) + @"\s*\(",
                    RegexOptions.Compiled);

                var at = -1;
                for (var i = 0; i < lines.Length && at < 0; i++)
                    if (declaration.IsMatch(lines[i])) at = i;

                if (at < 0) { unresolved.Add($"{id}: '{action}' is no longer declared in {relative}"); continue; }
                examined++;

                // Walk back over the contiguous block of attributes and comments above the declaration.
                for (var i = at - 1; i >= 0; i--)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("["))
                    {
                        if (authorization.IsMatch(line))
                            protectedAfterAll.Add($"{id}: carries {line} but is still listed as a gap");
                        continue;
                    }
                    if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*"))
                        continue;
                    break;
                }
            }

            Assert.True(unresolved.Count == 0,
                "baseline entries no longer resolve to code:\n  " + string.Join("\n  ", unresolved));
            Assert.True(protectedAfterAll.Count == 0,
                "baseline entries are protected and should have been retired with a frozenBaselineHistory " +
                "record:\n  " + string.Join("\n  ", protectedAfterAll));
            Assert.Equal(doc.RootElement.GetProperty("entries").GetArrayLength(), examined);
        }
    }
}
