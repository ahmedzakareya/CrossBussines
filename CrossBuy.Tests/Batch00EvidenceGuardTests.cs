using System.Text.Json;
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
            Assert.Equal(410, mutating);
            Assert.Equal(157, attr);
            Assert.Equal(110, inBody);

            // THE INVARIANT. Descriptive totals grow when protected endpoints are added; this one may only shrink.
            Assert.Equal(143, gaps);
            Assert.Equal(mutating, attr + inBody + gaps);
        }
    }
}
