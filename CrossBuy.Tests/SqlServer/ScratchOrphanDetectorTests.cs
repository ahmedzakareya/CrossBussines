using System.Text;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE SYNTHETIC SUITE — every classification decision, with no SQL Server anywhere.
    //
    // THIS CLASS CARRIES NO [Collection] ATTRIBUTE, AND THAT IS THE POINT. Joining the SqlServer
    // collection would construct SqlServerFixture, and on this machine CROSSBUY_TEST_SQL is a persisted
    // USER variable, so the fixture would initialise and CREATE A DATABASE - which is exactly what a
    // batch forbidden from creating databases must not do by accident. These tests reach the classifier
    // through strings and fakes instead, which is what the whole component split was for.
    //
    // The scenarios below are the eighteen required by the batch, plus boundary cases where a required
    // one would otherwise pass for the wrong reason.
    // =================================================================================================
    public class ScratchOrphanDetectorTests
    {
        private const string Machine = "TESTMACHINE";
        private const string Dir = @"C:\synthetic\manifests";
        private static readonly DateTime T0 = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

        // ---- building synthetic manifests ------------------------------------------------------------

        private static string Ev(
            int seq, string @event, string? database = null, string runId = "run-a", int pid = 4242,
            string? role = null, string? family = null, string? owner = null,
            string cleanupStatus = "Pending", string? error = null, int secondsAfterT0 = 0,
            string machine = Machine, int version = 1)
        {
            string Q(string? v) => v is null ? "null" : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            return "{" +
                   $"\"v\":{version}," +
                   $"\"ts\":{Q(T0.AddSeconds(secondsAfterT0).ToString("O"))}," +
                   $"\"runId\":{Q(runId)}," +
                   $"\"seq\":{seq}," +
                   $"\"event\":{Q(@event)}," +
                   $"\"role\":{Q(role)}," +
                   $"\"database\":{Q(database)}," +
                   $"\"family\":{Q(family)}," +
                   $"\"owner\":{Q(owner)}," +
                   $"\"cleanupStatus\":{Q(cleanupStatus)}," +
                   $"\"machine\":{Q(machine)}," +
                   $"\"pid\":{pid}," +
                   $"\"error\":{Q(error)}" +
                   "}";
        }

        /// <summary>Joins lines into a COMPLETE file - every line terminated, as the writer leaves it.</summary>
        private static string File_(params string[] lines)
        {
            var text = new StringBuilder();
            foreach (var line in lines) text.Append(line).Append('\n');
            return text.ToString();
        }

        private static ManifestWorld World(params (string name, string content)[] files) =>
            ScratchLifecycleBuilder.Build(new ManifestSet(Dir, true, null,
                files.Select(f => ScratchManifestReader.ParseContent(Path.Combine(Dir, f.name), f.content)).ToList()));

        private static ManifestWorld NoManifestDirectory() =>
            ScratchLifecycleBuilder.Build(ManifestSet.Inaccessible(Dir, "access is denied"));

        private static ManifestWorld EmptyManifestDirectory() =>
            ScratchLifecycleBuilder.Build(new ManifestSet(Dir, true, null, Array.Empty<ManifestFileResult>()));

        // ---- a fake process table --------------------------------------------------------------------

        private sealed class FakeProcesses : IProcessInspector
        {
            private readonly Dictionary<int, (ProcessProbe Probe, ProcessFact? Fact)> _map = new();

            public FakeProcesses Running(int pid, DateTime startUtc, string name = "testhost")
            { _map[pid] = (ProcessProbe.Running, new ProcessFact(pid, startUtc, name)); return this; }

            public FakeProcesses NotRunning(int pid)
            { _map[pid] = (ProcessProbe.NotRunning, null); return this; }

            public FakeProcesses Unreadable(int pid)
            { _map[pid] = (ProcessProbe.Unreadable, null); return this; }

            public ProcessProbe Probe(int processId, out ProcessFact? fact)
            {
                if (_map.TryGetValue(processId, out var entry)) { fact = entry.Fact; return entry.Probe; }
                fact = null;
                return ProcessProbe.NotRunning;
            }
        }

        private static Func<ScratchRun, RunLiveness> Liveness(IProcessInspector inspector) =>
            run => ScratchRunLiveness.Assess(run, inspector, Machine);

        private static readonly Func<ScratchRun, RunLiveness> DeadProcess =
            Liveness(new FakeProcesses().NotRunning(4242));

        private static readonly Func<ScratchRun, RunLiveness> LiveProcess =
            Liveness(new FakeProcesses().Running(4242, T0.AddMinutes(-5)));

        private static ScratchFinding Only(ScratchAssessment assessment, string database) =>
            Assert.Single(assessment.Findings, f => f.Database == database);

        private static ScratchLifecycle LifecycleOf(ManifestWorld world, string database) =>
            Assert.Single(world.Lifecycles, l => l.Database == database);

        // =============================================================================================
        // 1. CLEAN COMPLETED RUN
        // =============================================================================================
        [Fact]
        public void Scenario01_clean_completed_run_is_released_and_needs_no_action()
        {
            const string db = "CrossBuyProbe_Clean_00000000000000000000000000000001";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Clean", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Clean", owner: "probe:1"),
                Ev(4, "SchemaBegin", db),
                Ev(5, "SchemaComplete", db),
                Ev(6, "DropIntent", db),
                Ev(7, "Released", db, cleanupStatus: "Released"),
                Ev(8, "RunEnded", role: "Run", cleanupStatus: "Released"))));

            var result = ScratchOrphanClassifier.Classify(world, ScratchCatalogSnapshot.Synthetic(), DeadProcess);
            var finding = Only(result, db);

            Assert.Equal(ScratchClassification.ManifestedReleased, finding.Classification);
            Assert.Equal(ClassificationConfidence.High, finding.Confidence);
            Assert.Equal(ScratchOrphanClassifier.ActionNone, finding.RecommendedAction);
            Assert.False(finding.PresentInCatalog);
            Assert.Equal("run-a", finding.RunId);
        }

        // =============================================================================================
        // 2. INTERRUPTED RUN, DATABASE REMAINS — the shape of the 2026-10-21 incident
        // =============================================================================================
        [Fact]
        public void Scenario02_interrupted_run_leaves_an_orphan_candidate()
        {
            const string db = "CrossBuyProbe_Interrupted_000000000000000000000000002";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Interrupted", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Interrupted", owner: "probe:1"),
                Ev(4, "SchemaBegin", db))));          // no SchemaComplete, no RunEnded

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.Equal(ClassificationConfidence.High, finding.Confidence);
            Assert.True(finding.PresentInCatalog);
            Assert.Contains(finding.Limitations, l => l.Contains("schema deployment began and never completed"));
            Assert.Contains("MANUAL REVIEW", finding.RecommendedAction);
        }

        // =============================================================================================
        // 3. ACTIVE RUN — a live run's database must never be called an orphan
        // =============================================================================================
        [Fact]
        public void Scenario03_active_run_database_is_left_alone()
        {
            const string db = "CrossBuyProbe_Active_000000000000000000000000000003";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Active", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Active", owner: "probe:1"))));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, LiveProcess), db);

            Assert.Equal(ScratchClassification.ManifestedActive, finding.Classification);
            Assert.Equal(ScratchOrphanClassifier.ActionLeaveActive, finding.RecommendedAction);
        }

        // =============================================================================================
        // 4. CreateIntent THAT NEVER BECAME A DATABASE — the fail-closed direction
        // =============================================================================================
        [Fact]
        public void Scenario04_create_intent_without_creation_is_not_an_orphan()
        {
            const string db = "CrossBuyProbe_IntentOnly_00000000000000000000000004";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "IntentOnly", owner: "probe:1"),
                Ev(3, "RunEnded", role: "Run", cleanupStatus: "Released"))));

            var finding = Only(ScratchOrphanClassifier.Classify(
                world, ScratchCatalogSnapshot.Synthetic(), DeadProcess), db);

            Assert.NotEqual(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.Equal(ScratchOrphanClassifier.ActionNone, finding.RecommendedAction);
            Assert.Contains(finding.Limitations, l => l.Contains("never confirmed by a Created event"));
        }

        // =============================================================================================
        // 5. Created, NO RunEnded, DEAD PROCESS
        // =============================================================================================
        [Fact]
        public void Scenario05_created_without_run_ended_and_a_dead_process_is_an_orphan_candidate()
        {
            const string db = "CrossBuyProbe_NoEnd_0000000000000000000000000000005";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "NoEnd", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "NoEnd", owner: "probe:1"))));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.Equal(ScratchCreationState.Confirmed, LifecycleOf(world, db).Creation);
        }

        // =============================================================================================
        // 6. DUPLICATE Released — expected after a failed InitializeAsync, and benign
        // =============================================================================================
        [Fact]
        public void Scenario06_duplicate_released_events_resolve_to_one_released_finding()
        {
            const string db = "CrossBuyPlatformTest_00000000000000000000000000000006";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Fixture", family: "PlatformFixture", owner: "fixture"),
                Ev(3, "Created", db, role: "Fixture", family: "PlatformFixture", owner: "fixture"),
                Ev(4, "DropIntent", db, owner: "fixture"),
                Ev(5, "Released", db, owner: "fixture", cleanupStatus: "Released"),
                Ev(6, "DropIntent", db, owner: "fixture"),
                Ev(7, "Released", db, owner: "fixture", cleanupStatus: "Released"),
                Ev(8, "RunEnded", role: "Run", cleanupStatus: "Released"))));

            var result = ScratchOrphanClassifier.Classify(world, ScratchCatalogSnapshot.Synthetic(), DeadProcess);
            var finding = Only(result, db);                       // Single() is the duplicate check

            Assert.Equal(ScratchClassification.ManifestedReleased, finding.Classification);
            Assert.Equal(ClassificationConfidence.High, finding.Confidence);

            // Nothing was discarded: both drops are still in the history a reviewer can read.
            var lifecycle = LifecycleOf(world, db);
            Assert.Equal(2, lifecycle.History.Count(e => e.Event == "DropIntent"));
            Assert.Equal(2, lifecycle.History.Count(e => e.Event == "Released"));
        }

        // =============================================================================================
        // 7. TRUNCATED FINAL LINE — the failure JSON Lines was chosen to absorb
        // =============================================================================================
        [Fact]
        public void Scenario07_truncated_final_line_preserves_everything_before_it()
        {
            const string db = "CrossBuyProbe_Torn_00000000000000000000000000000007";

            // No trailing newline, and the last record is cut off mid-way.
            var content = File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Torn", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Torn", owner: "probe:1"))
                + "{\"v\":1,\"ts\":\"2026-10-25T10:00:04\",\"runId\":\"run-a\",\"seq\":4,\"eve";

            var file = ScratchManifestReader.ParseContent(Path.Combine(Dir, "run-a.jsonl"), content);

            Assert.Equal(ManifestIntegrity.TruncatedTail, file.Integrity);
            Assert.Equal(3, file.Events.Count);
            Assert.Contains(file.Warnings, w => w.Contains("final record is incomplete"));

            var world = ScratchLifecycleBuilder.Build(new ManifestSet(Dir, true, null, new[] { file }));
            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            // Still an orphan candidate - but DEGRADED CONFIDENCE, because the run's last act is unknown.
            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.Equal(ClassificationConfidence.Medium, finding.Confidence);
            Assert.Contains(finding.Limitations, l => l.Contains("final record is torn"));
        }

        // =============================================================================================
        // 8. CORRUPTED MIDDLE LINE — rule 2: degraded evidence buys no confident verdict
        // =============================================================================================
        [Fact]
        public void Scenario08_corrupted_middle_line_degrades_every_verdict_from_that_file()
        {
            const string db = "CrossBuyProbe_Corrupt_0000000000000000000000000008";

            var content = File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Corrupt", owner: "probe:1"),
                "{\"v\":1,\"ts\":\"nonsense\",\"runId\":\"run-a\",\"seq\":3,\"event\":\"Created\"}",
                Ev(4, "SchemaBegin", db));

            var file = ScratchManifestReader.ParseContent(Path.Combine(Dir, "run-a.jsonl"), content);
            Assert.Equal(ManifestIntegrity.Degraded, file.Integrity);
            Assert.Contains(file.Warnings, w => w.Contains("unusable record"));

            var world = ScratchLifecycleBuilder.Build(new ManifestSet(Dir, true, null, new[] { file }));
            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.Equal(ClassificationConfidence.Low, finding.Confidence);
        }

        // =============================================================================================
        // 9. MISSING MANIFEST DIRECTORY — silence is never a clean bill of health
        // =============================================================================================
        [Fact]
        public void Scenario09_an_unreadable_manifest_directory_is_reported_not_treated_as_zero_orphans()
        {
            const string db = "CrossBuyProbe_Legacy_000000000000000000000000000009";

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0.AddDays(-4), "ONLINE"));
            var result = ScratchOrphanClassifier.Classify(NoManifestDirectory(), catalog, DeadProcess);

            Assert.False(result.ManifestDirectoryAccessible);
            Assert.Equal("access is denied", result.ManifestAccessError);

            var finding = Only(result, db);
            Assert.Equal(ScratchClassification.UnattributedScratch, finding.Classification);
            Assert.Contains(finding.Limitations, l => l.Contains("proves nothing at all"));

            // And the report must say so before it says anything about counts.
            var text = ScratchOrphanReport.Render(result);
            Assert.Contains("manifest readable  : NO", text);
            Assert.Contains("NOTHING below may be read as an absence of orphans.", text);
        }

        // =============================================================================================
        // 10. UNATTRIBUTED LEGACY DATABASE — all 301 on the instance are in this class today
        // =============================================================================================
        [Fact]
        public void Scenario10_a_legacy_database_with_no_manifest_is_unattributed_and_never_cleared()
        {
            const string db = "CrossBuyProbe_B6Conversion_000000000000000000000010";

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0.AddDays(-4), "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(EmptyManifestDirectory(), catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.UnattributedScratch, finding.Classification);
            Assert.Equal(ClassificationConfidence.Low, finding.Confidence);
            Assert.Null(finding.RunId);
            Assert.Contains("NOT established as safe to remove", finding.RecommendedAction);
            Assert.Contains(finding.Limitations, l => l.Contains("naming convention, not a provenance"));
        }

        // =============================================================================================
        // 11. RELEASED BUT STILL PRESENT — rule 3
        // =============================================================================================
        [Fact]
        public void Scenario11_a_released_database_that_still_exists_is_a_mismatch()
        {
            const string db = "CrossBuyProbe_Ghost_0000000000000000000000000011";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Ghost", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Ghost", owner: "probe:1"),
                Ev(4, "DropIntent", db),
                Ev(5, "Released", db, cleanupStatus: "Released"),
                Ev(6, "RunEnded", role: "Run", cleanupStatus: "Released"))));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.ManifestCatalogMismatch, finding.Classification);
            Assert.Equal(ClassificationConfidence.High, finding.Confidence);
            Assert.Equal(ScratchOrphanClassifier.ActionInvestigateMismatch, finding.RecommendedAction);
        }

        // =============================================================================================
        // 12. PID REUSE — a matching id is a coincidence until the start time agrees
        // =============================================================================================
        [Fact]
        public void Scenario12_a_reused_pid_does_not_keep_a_dead_run_alive()
        {
            const string db = "CrossBuyProbe_Reuse_0000000000000000000000000012";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Reuse", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Reuse", owner: "probe:1"))));

            // Same PID, but the process holding it started an hour AFTER the run began.
            var reused = new FakeProcesses().Running(4242, T0.AddHours(1), "notepad");
            var run = Assert.Single(world.Runs);
            Assert.Equal(RunLiveness.Dead, ScratchRunLiveness.Assess(run, reused, Machine));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, Liveness(reused)), db);
            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);

            // The control: the SAME pid, started before the run, is alive.
            var genuine = new FakeProcesses().Running(4242, T0.AddMinutes(-10), "testhost");
            Assert.Equal(RunLiveness.Alive, ScratchRunLiveness.Assess(run, genuine, Machine));
        }

        // =============================================================================================
        // 13. A UatSqlProbeFixture DATABASE — a second lifecycle that writes no manifest
        // =============================================================================================
        [Fact]
        public void Scenario13_a_database_from_the_other_fixture_is_unattributed_and_says_why()
        {
            const string db = "CrossBuyProbe_Uat_00000000000000000000000000000013";

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(EmptyManifestDirectory(), catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.UnattributedScratch, finding.Classification);
            Assert.Contains(finding.Limitations, l => l.Contains("UatSqlProbeFixture"));
        }

        // =============================================================================================
        // 14. CONCURRENT MANIFESTS — two runs at once, no cross-contamination
        // =============================================================================================
        [Fact]
        public void Scenario14_concurrent_manifests_are_resolved_independently()
        {
            const string liveDb = "CrossBuyProbe_ConcLive_000000000000000000000014a";
            const string deadDb = "CrossBuyProbe_ConcDead_000000000000000000000014b";

            var world = World(
                ("run-a.jsonl", File_(
                    Ev(1, "RunStarted", runId: "run-a", pid: 1111, role: "Run", cleanupStatus: "None"),
                    Ev(2, "CreateIntent", liveDb, runId: "run-a", pid: 1111, role: "Probe", family: "ConcLive"),
                    Ev(3, "Created", liveDb, runId: "run-a", pid: 1111, role: "Probe", family: "ConcLive"))),
                ("run-b.jsonl", File_(
                    Ev(1, "RunStarted", runId: "run-b", pid: 2222, role: "Run", cleanupStatus: "None"),
                    Ev(2, "CreateIntent", deadDb, runId: "run-b", pid: 2222, role: "Probe", family: "ConcDead"),
                    Ev(3, "Created", deadDb, runId: "run-b", pid: 2222, role: "Probe", family: "ConcDead"))));

            Assert.Equal(2, world.Runs.Count);

            var processes = new FakeProcesses()
                .Running(1111, T0.AddMinutes(-1))
                .NotRunning(2222);

            var catalog = ScratchCatalogSnapshot.Synthetic(
                new CatalogDatabase(liveDb, T0, "ONLINE"),
                new CatalogDatabase(deadDb, T0, "ONLINE"));

            var result = ScratchOrphanClassifier.Classify(world, catalog, Liveness(processes));

            Assert.Equal(ScratchClassification.ManifestedActive, Only(result, liveDb).Classification);
            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, Only(result, deadDb).Classification);
            Assert.Equal("run-a", Only(result, liveDb).RunId);
            Assert.Equal("run-b", Only(result, deadDb).RunId);
        }

        // =============================================================================================
        // 15. MANIFEST WRITE FAILURE — events missing, including the one that mattered
        // =============================================================================================
        [Fact]
        public void Scenario15_a_manifest_that_lost_writes_cannot_produce_a_confident_verdict()
        {
            const string db = "CrossBuyProbe_LostWrites_000000000000000000000015";

            // seq 3, 4 and 5 were never persisted - among them the Created for this database.
            var file = ScratchManifestReader.ParseContent(Path.Combine(Dir, "run-a.jsonl"), File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "LostWrites", owner: "probe:1"),
                Ev(6, "SchemaBegin", db)));

            Assert.Equal(ManifestIntegrity.Degraded, file.Integrity);
            Assert.Contains(file.Warnings, w => w.Contains("was not persisted"));

            var world = ScratchLifecycleBuilder.Build(new ManifestSet(Dir, true, null, new[] { file }));
            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.Equal(ClassificationConfidence.Low, finding.Confidence);
            Assert.Contains("MANUAL REVIEW", finding.RecommendedAction);
        }

        // =============================================================================================
        // 16. SEQUENCE GAP — detected on its own, with the numbers named
        // =============================================================================================
        [Fact]
        public void Scenario16_a_single_sequence_gap_is_detected_and_named()
        {
            var file = ScratchManifestReader.ParseContent(Path.Combine(Dir, "run-a.jsonl"), File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", "CrossBuyProbe_Gap_000000000000000000000000000016"),
                Ev(4, "Created", "CrossBuyProbe_Gap_000000000000000000000000000016")));

            Assert.Equal(ManifestIntegrity.Degraded, file.Integrity);
            Assert.Contains(file.Warnings, w => w.Contains("sequence gap: expected 3"));
        }

        // =============================================================================================
        // 17. CleanupFailed THEN Released — last terminal event wins, history is kept
        // =============================================================================================
        [Fact]
        public void Scenario17_cleanup_failed_then_released_resolves_to_released()
        {
            const string db = "CrossBuyProbe_Retry_0000000000000000000000000017";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Retry", owner: "probe:1"),
                Ev(3, "Created", db, role: "Probe", family: "Retry", owner: "probe:1"),
                Ev(4, "DropIntent", db),
                Ev(5, "CleanupFailed", db, cleanupStatus: "Failed", error: "the database still exists after DROP"),
                Ev(6, "DropIntent", db),
                Ev(7, "Released", db, cleanupStatus: "Released"),
                Ev(8, "RunEnded", role: "Run", cleanupStatus: "Released"))));

            var lifecycle = LifecycleOf(world, db);
            Assert.Equal(ScratchTerminalState.Released, lifecycle.Terminal);
            Assert.Contains(lifecycle.History, e => e.Event == "CleanupFailed");   // nothing discarded

            var finding = Only(ScratchOrphanClassifier.Classify(
                world, ScratchCatalogSnapshot.Synthetic(), DeadProcess), db);

            Assert.Equal(ScratchClassification.ManifestedReleased, finding.Classification);
        }

        // =============================================================================================
        // 18. ABSENT AFTER AN UNRESOLVED CreateIntent — never an orphan, never a release
        // =============================================================================================
        [Fact]
        public void Scenario18_an_unresolved_intent_with_no_database_claims_neither_orphan_nor_release()
        {
            const string db = "CrossBuyProbe_Unresolved_00000000000000000000018";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Unresolved", owner: "probe:1"))));

            var lifecycle = Assert.Single(world.Lifecycles);
            Assert.Equal(ScratchCreationState.IntentOnly, lifecycle.Creation);
            Assert.Equal(ScratchTerminalState.None, lifecycle.Terminal);

            var finding = Only(ScratchOrphanClassifier.Classify(
                world, ScratchCatalogSnapshot.Synthetic(), DeadProcess), db);

            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.NotEqual(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.NotEqual(ScratchClassification.ManifestedReleased, finding.Classification);
            Assert.False(finding.PresentInCatalog);
        }

        // =============================================================================================
        // BOUNDARY CASES - each one stops a required scenario above from passing for the wrong reason.
        // =============================================================================================

        [Fact]
        public void A_catalog_that_could_not_be_read_yields_no_conclusions_at_all()
        {
            const string db = "CrossBuyProbe_NoCatalog_00000000000000000000000019";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "NoCatalog"),
                Ev(3, "Created", db, role: "Probe", family: "NoCatalog"))));

            var result = ScratchOrphanClassifier.Classify(
                world, ScratchCatalogSnapshot.Unavailable("login failed"), DeadProcess);

            Assert.False(result.CatalogAvailable);
            var finding = Only(result, db);
            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.Contains(finding.Limitations, l => l.Contains("could not be read"));

            // And no unattributed rows can be invented from a catalog that was never read.
            Assert.Equal(0, result.CountOf(ScratchClassification.UnattributedScratch));
        }

        [Fact]
        public void A_run_recorded_on_another_machine_is_unknown_not_dead()
        {
            const string db = "CrossBuyProbe_Remote_000000000000000000000000020";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None", machine: "SOMEBUILDAGENT"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Remote", machine: "SOMEBUILDAGENT"),
                Ev(3, "Created", db, role: "Probe", family: "Remote", machine: "SOMEBUILDAGENT"))));

            Assert.Equal(RunLiveness.Unknown,
                ScratchRunLiveness.Assess(Assert.Single(world.Runs), new FakeProcesses(), Machine));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
            Assert.Contains(finding.Limitations, l => l.Contains("NOT assumed dead"));
        }

        [Fact]
        public void Unreadable_process_metadata_is_unknown_not_dead()
        {
            const string db = "CrossBuyProbe_Opaque_000000000000000000000000021";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Opaque"),
                Ev(3, "Created", db, role: "Probe", family: "Opaque"))));

            var opaque = new FakeProcesses().Unreadable(4242);
            Assert.Equal(RunLiveness.Unknown, ScratchRunLiveness.Assess(Assert.Single(world.Runs), opaque, Machine));

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, Liveness(opaque)), db);

            Assert.Equal(ScratchClassification.Indeterminate, finding.Classification);
        }

        [Fact]
        public void A_record_from_a_newer_schema_version_is_refused_rather_than_guessed_at()
        {
            var file = ScratchManifestReader.ParseContent(Path.Combine(Dir, "run-a.jsonl"), File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "Created", "CrossBuyProbe_Future_00000000000000000000000022", version: 99)));

            Assert.Equal(ManifestIntegrity.Degraded, file.Integrity);
            Assert.Contains(file.Warnings, w => w.Contains("newer than this reader understands"));
        }

        [Fact]
        public void The_reader_and_the_writer_agree_on_the_schema_version()
        {
            // A coupling guard: if the writer's schema moves, this fails here rather than silently
            // rejecting every record the next run produces.
            Assert.Equal(SqlScratchManifest.SchemaVersion, ScratchManifestReader.SupportedSchemaVersion);
        }

        [Fact]
        public void The_catalog_component_can_only_select()
        {
            var sql = ScratchCatalogSnapshot.CatalogQuery;

            Assert.StartsWith("SELECT ", sql, StringComparison.Ordinal);

            // WORD BOUNDARIES, not substrings: the statement legitimately selects the column `create_date`,
            // and a naive Contains("CREATE") would both fail here and teach the next person to delete the
            // assertion rather than trust it.
            foreach (var verb in new[] { "CREATE", "DROP", "ALTER", "RESTORE", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "EXEC", "MERGE" })
            {
                Assert.False(
                    System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{verb}\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    $"the catalog statement must contain no {verb}: {sql}");
            }
        }

        [Fact]
        public void The_report_states_that_nothing_was_cleaned_up_and_clears_nothing()
        {
            const string db = "CrossBuyProbe_Reported_00000000000000000000000023";

            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, T0, "ONLINE"));
            var text = ScratchOrphanReport.Render(
                ScratchOrphanClassifier.Classify(EmptyManifestDirectory(), catalog, DeadProcess));

            Assert.Contains(ScratchOrphanReport.NoCleanupStatement, text);
            Assert.Contains("NOT confirmed safe for deletion", text);
            Assert.Contains("Unattributed legacy databases: 1", text);

            // The phrase a careless reader would look for must not be in there in any form.
            Assert.DoesNotContain("safe to delete", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("can be deleted", text, StringComparison.OrdinalIgnoreCase);
        }

        // =============================================================================================
        // TIMESTAMP REPRESENTATION (Batch 4E) - the defect Batch 4D found in live output.
        //
        // sys.databases.create_date was read with SpecifyKind(..., DateTimeKind.Utc) and rendered with the
        // "u" format. SpecifyKind RELABELS, it does not convert, so a wall-clock reading from a UTC+3 host
        // was asserted to be a UTC instant and printed with a trailing Z - three hours wrong, and confident
        // about it. Every test below fails against that implementation; none of them touches SQL Server.
        //
        // THESE ARE BEHAVIOURAL, NOT GREPS OVER THE SOURCE. A source-text guard would also have to walk up
        // to the repository root, which does not exist under an isolated build output - and this file
        // already records why a grep-shaped assertion is the weaker instrument anyway.
        // =============================================================================================

        /// A. The wall clock the server reported is kept as a wall clock, with no zone asserted.
        [Fact]
        public void Timestamp_A_a_catalog_creation_date_is_server_local_and_never_relabelled_utc()
        {
            // Exactly what SqlDataReader.GetDateTime hands back for create_date: digits, no zone.
            var fromServer = new DateTime(2026, 10, 25, 8, 38, 18, DateTimeKind.Unspecified);

            var kept = ScratchCatalogSnapshot.AsServerLocal(fromServer);

            Assert.Equal(DateTimeKind.Unspecified, kept.Kind);   // the old code produced Utc here
            Assert.Equal(fromServer.Ticks, kept.Ticks);          // and nothing was shifted
        }

        /// B. The report prints that value with no Z and says which clock it came from.
        [Fact]
        public void Timestamp_B_the_report_never_labels_a_creation_date_as_utc()
        {
            const string db = "CrossBuyProbe_Stamp_0000000000000000000000024";
            var created = new DateTime(2026, 10, 25, 8, 38, 18, DateTimeKind.Unspecified);

            var text = ScratchOrphanReport.Render(ScratchOrphanClassifier.Classify(
                EmptyManifestDirectory(),
                ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, created, "ONLINE")),
                DeadProcess));

            Assert.Contains("created 2026-10-25 08:38:18 (server local)", text);

            // The exact string the defective build produced for this same input.
            Assert.DoesNotContain("created 2026-10-25 08:38:18Z", text);
        }

        /// C. Manifest timestamps really are UTC, and keep their Z - that one is a true statement.
        [Fact]
        public void Timestamp_C_manifest_event_timestamps_keep_their_utc_semantics()
        {
            const string db = "CrossBuyProbe_Utc_00000000000000000000000000025";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Utc", secondsAfterT0: 1),
                Ev(3, "Created", db, role: "Probe", family: "Utc", secondsAfterT0: 2))));

            var latest = LifecycleOf(world, db).LatestEvent;

            Assert.Equal(DateTimeKind.Utc, latest.TimestampUtc.Kind);
            Assert.Equal(T0.AddSeconds(2), latest.TimestampUtc);

            var text = ScratchOrphanReport.Render(ScratchOrphanClassifier.Classify(
                world,
                ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(
                    db, new DateTime(2026, 10, 25, 8, 38, 18, DateTimeKind.Unspecified), "ONLINE")),
                DeadProcess));

            // The manifest clock keeps its Z; the catalog clock, in the same finding, does not.
            Assert.Contains("at " + T0.AddSeconds(2).ToString("O"), text);
            Assert.Contains("created 2026-10-25 08:38:18 (server local)", text);
        }

        /// D. The representation change is inert: the same scenario classifies identically whatever Kind
        ///    the creation date carries. No decision has ever read this value, and this keeps it that way.
        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Local)]
        public void Timestamp_D_the_creation_date_kind_changes_no_classification(DateTimeKind kind)
        {
            const string db = "CrossBuyProbe_Inert_000000000000000000000000026";

            var world = World(("run-a.jsonl", File_(
                Ev(1, "RunStarted", role: "Run", cleanupStatus: "None"),
                Ev(2, "CreateIntent", db, role: "Probe", family: "Inert"),
                Ev(3, "Created", db, role: "Probe", family: "Inert"))));

            var created = DateTime.SpecifyKind(new DateTime(2026, 10, 25, 8, 38, 18), kind);
            var catalog = ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, created, "ONLINE"));

            var finding = Only(ScratchOrphanClassifier.Classify(world, catalog, DeadProcess), db);

            Assert.Equal(ScratchClassification.ManifestedOrphanCandidate, finding.Classification);
            Assert.Equal(ClassificationConfidence.High, finding.Confidence);
            Assert.Equal(ScratchOrphanClassifier.ActionReviewOrphan, finding.RecommendedAction);
            Assert.Equal(created, finding.CreateDateServerLocal);
        }

        /// E. The SQL Server's zone is never taken to be the runner's. If any step converted using the
        ///    local offset the digits would move - on this UTC+3 machine, by three hours.
        [Fact]
        public void Timestamp_E_the_sql_server_zone_is_never_assumed_to_be_the_runners()
        {
            const string db = "CrossBuyProbe_Zone_0000000000000000000000000027";
            var wall = new DateTime(2026, 10, 25, 8, 38, 18, DateTimeKind.Unspecified);

            var kept = ScratchCatalogSnapshot.AsServerLocal(wall);
            Assert.Equal(wall.Ticks, kept.Ticks);

            var text = ScratchOrphanReport.Render(ScratchOrphanClassifier.Classify(
                EmptyManifestDirectory(),
                ScratchCatalogSnapshot.Synthetic(new CatalogDatabase(db, kept, "ONLINE")),
                DeadProcess));

            Assert.Contains("08:38:18 (server local)", text);

            // Whatever this runner's offset happens to be, the value a conversion WOULD have produced must
            // appear nowhere. On a runner at UTC the two coincide and there is nothing to distinguish, so
            // the assertion is made only where it can actually discriminate.
            var offset = TimeZoneInfo.Local.GetUtcOffset(wall);
            if (offset != TimeSpan.Zero)
            {
                foreach (var converted in new[] { wall.Add(-offset), wall.Add(offset) })
                {
                    Assert.NotEqual(converted.Ticks, kept.Ticks);
                    Assert.DoesNotContain(
                        converted.ToString(ScratchOrphanReport.ServerLocalFormat,
                            System.Globalization.CultureInfo.InvariantCulture),
                        text, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void Reading_a_directory_that_does_not_exist_reports_inaccessible_rather_than_empty()
        {
            var missing = Path.Combine(Path.GetTempPath(), "crossbuy-no-such-manifest-dir-" + Guid.NewGuid().ToString("N"));

            var set = ScratchManifestReader.ReadDirectory(missing);

            Assert.False(set.DirectoryAccessible);
            Assert.NotNull(set.AccessError);
            Assert.Empty(set.Files);
        }
    }
}
