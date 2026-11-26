namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE CLASSIFIER — step 5, and the only component that reaches a verdict.
    //
    // IT IS A PURE FUNCTION. Manifest lifecycles, a catalog snapshot and liveness verdicts go in; findings
    // come out. No SQL connection, no filesystem, no process table, no clock. That is what makes all
    // eighteen scenarios testable without SQL Server, and it is also what makes the decisions auditable:
    // every verdict below is reproducible from its three inputs and nothing else.
    //
    // FOUR SAFETY RULES, ENFORCED RATHER THAN DOCUMENTED:
    //
    //   1. A CLASSIFICATION IS NEVER PERMISSION TO DELETE. Nothing here, and nothing downstream, drops a
    //      database. The strongest verdict available is "a person should look at this".
    //   2. DEGRADED EVIDENCE CANNOT PRODUCE A CONFIDENT ORPHAN. A manifest with a hole in it buys
    //      Indeterminate, because a detector that guesses hardest exactly where its evidence is weakest is
    //      the failure mode that started this programme.
    //   3. PRESENT AFTER Released IS A MISMATCH, never a quiet correction. Released is only written after
    //      the run itself confirmed absence, so a disagreement means one of the two sources is wrong and a
    //      human has to decide which.
    //   4. NO MANIFEST MEANS UNATTRIBUTED, NOT SAFE. 301 databases currently sit in this class, and a name
    //      beginning CrossBuyProbe_ is a naming convention, not a provenance.
    // =================================================================================================

    public enum ScratchClassification
    {
        /// <summary>Present, and its owning run is demonstrably still running.</summary>
        ManifestedActive,

        /// <summary>Present, attributed to a run, and that run is demonstrably gone.</summary>
        ManifestedOrphanCandidate,

        /// <summary>The manifest confirmed removal and the catalog agrees it is absent.</summary>
        ManifestedReleased,

        /// <summary>Matches an owned scratch prefix but no manifest accounts for it.</summary>
        UnattributedScratch,

        /// <summary>The manifest and the catalog contradict each other.</summary>
        ManifestCatalogMismatch,

        /// <summary>The evidence cannot establish a lifecycle state.</summary>
        Indeterminate,
    }

    public enum EvidenceSource { None, Manifest, Catalog, ManifestAndCatalog }

    public enum ClassificationConfidence { Low, Medium, High }

    public sealed record ScratchFinding(
        string Database,
        ScratchClassification Classification,
        EvidenceSource Evidence,
        string? RunId,
        string? LatestEvent,
        DateTime? LatestEventUtc,
        bool PresentInCatalog,
        DateTime? CreateDateServerLocal,
        string? CatalogState,
        ClassificationConfidence Confidence,
        IReadOnlyList<string> Limitations,
        string RecommendedAction);

    public sealed record ScratchAssessment(
        IReadOnlyList<ScratchFinding> Findings,
        IReadOnlyList<string> IntegrityWarnings,
        string ManifestDirectory,
        bool ManifestDirectoryAccessible,
        string? ManifestAccessError,
        bool CatalogAvailable,
        string? CatalogUnavailableReason)
    {
        public int CountOf(ScratchClassification classification) =>
            Findings.Count(f => f.Classification == classification);
    }

    public static class ScratchOrphanClassifier
    {
        public const string ActionNone = "None - the database does not exist.";
        public const string ActionReviewOrphan =
            "MANUAL REVIEW: a person must confirm the owning run is finished before anything is done with this database. No automatic action is authorized.";
        public const string ActionLeaveActive = "None - leave it alone; a live run owns it.";
        public const string ActionInvestigateMismatch =
            "MANUAL REVIEW: the manifest and the SQL catalog disagree. Establish which is wrong before acting on either.";
        public const string ActionUnattributed =
            "MANUAL REVIEW: no manifest accounts for this database. It is NOT established as safe to remove; attribution must come from independent evidence first.";
        public const string ActionIndeterminate =
            "MANUAL REVIEW: the available evidence cannot establish what state this database is in. Gather more evidence rather than acting.";

        public static ScratchAssessment Classify(
            ManifestWorld world,
            ScratchCatalogSnapshot catalog,
            Func<ScratchRun, RunLiveness> liveness)
        {
            var findings = new List<ScratchFinding>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lifecycle in world.Lifecycles)
            {
                claimed.Add(lifecycle.Database);
                findings.Add(ClassifyManifested(world, lifecycle, catalog, liveness));
            }

            // Anything the catalog holds that no manifest mentions. Today that is every scratch database on
            // the instance, because the manifest only began recording in Batch 4A.
            if (catalog.Available)
            {
                foreach (var row in catalog.Databases.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (claimed.Contains(row.Name)) continue;
                    findings.Add(Unattributed(world, row));
                }
            }

            return new ScratchAssessment(
                findings
                    .OrderBy(f => (int)Rank(f.Classification))
                    .ThenBy(f => f.Database, StringComparer.Ordinal)
                    .ToList(),
                world.IntegrityWarnings,
                world.Directory,
                world.DirectoryAccessible,
                world.AccessError,
                catalog.Available,
                catalog.UnavailableReason);
        }

        private static int Rank(ScratchClassification c) => c switch
        {
            ScratchClassification.ManifestCatalogMismatch => 0,
            ScratchClassification.ManifestedOrphanCandidate => 1,
            ScratchClassification.Indeterminate => 2,
            ScratchClassification.UnattributedScratch => 3,
            ScratchClassification.ManifestedActive => 4,
            ScratchClassification.ManifestedReleased => 5,
            _ => 6,
        };

        private static ScratchFinding ClassifyManifested(
            ManifestWorld world,
            ScratchLifecycle lifecycle,
            ScratchCatalogSnapshot catalog,
            Func<ScratchRun, RunLiveness> liveness)
        {
            var limitations = new List<string>(lifecycle.Warnings);
            var run = world.RunOf(lifecycle.RunId);
            var row = catalog.Available ? catalog.Find(lifecycle.Database) : null;
            var present = row != null;

            ScratchFinding Make(ScratchClassification classification, ClassificationConfidence confidence,
                                EvidenceSource evidence, string action) =>
                new(lifecycle.Database, classification, evidence, lifecycle.RunId,
                    lifecycle.LatestEvent.Event, lifecycle.LatestEvent.TimestampUtc,
                    present, row?.CreateDateServerLocal, row?.StateDescription,
                    confidence, limitations, action);

            // --- the catalog could not be read: nothing may be concluded about presence -------------------
            if (!catalog.Available)
            {
                limitations.Add("the SQL catalog could not be read, so presence is unknown: " +
                                (catalog.UnavailableReason ?? "no reason recorded"));
                return Make(ScratchClassification.Indeterminate, ClassificationConfidence.Low,
                    EvidenceSource.Manifest, ActionIndeterminate);
            }

            // --- degraded manifest: rule 2 ----------------------------------------------------------------
            if (run is { Integrity: ManifestIntegrity.Degraded or ManifestIntegrity.Unreadable })
            {
                limitations.Add($"the manifest for run {lifecycle.RunId} is {run.Integrity} - events may be " +
                                "missing, so no confident lifecycle verdict is possible");
                return Make(ScratchClassification.Indeterminate, ClassificationConfidence.Low,
                    EvidenceSource.ManifestAndCatalog, ActionIndeterminate);
            }

            var truncated = run is { Integrity: ManifestIntegrity.TruncatedTail };
            if (truncated)
                limitations.Add("the manifest's final record is torn, consistent with a killed run; the " +
                                "records before it are intact but the run's last action is unrecorded");

            // --- rule 3: present after a confirmed release ------------------------------------------------
            if (lifecycle.Terminal == ScratchTerminalState.Released && present)
            {
                limitations.Add("Released is only written after the run itself confirmed the database was " +
                                "absent, yet the catalog reports it present");
                return Make(ScratchClassification.ManifestCatalogMismatch, ClassificationConfidence.High,
                    EvidenceSource.ManifestAndCatalog, ActionInvestigateMismatch);
            }

            // --- confirmed release, and the catalog agrees ------------------------------------------------
            if (lifecycle.Terminal == ScratchTerminalState.Released)
            {
                return Make(ScratchClassification.ManifestedReleased,
                    truncated ? ClassificationConfidence.Medium : ClassificationConfidence.High,
                    EvidenceSource.ManifestAndCatalog, ActionNone);
            }

            // --- absent, with no confirmed release --------------------------------------------------------
            if (!present)
            {
                if (lifecycle.Creation == ScratchCreationState.IntentOnly)
                    limitations.Add("CreateIntent was recorded but never confirmed by a Created event, so the " +
                                    "manifest cannot say whether CREATE DATABASE ever ran; the catalog shows nothing");
                else
                    limitations.Add("the run never confirmed removal, but the catalog shows the database is gone " +
                                    "- most likely dropped without the confirmation being written");

                return Make(ScratchClassification.Indeterminate, ClassificationConfidence.Medium,
                    EvidenceSource.ManifestAndCatalog, ActionNone);
            }

            // --- present, and the run never released it ---------------------------------------------------
            if (lifecycle.Creation == ScratchCreationState.IntentOnly)
                limitations.Add("creation was never confirmed in the manifest, but the database is present - " +
                                "the run was interrupted between the intent and the confirmation");

            if (lifecycle.SchemaStarted && !lifecycle.SchemaCompleted)
                limitations.Add("schema deployment began and never completed - the catalog may be partially built");

            if (lifecycle.LastError != null)
                limitations.Add("last recorded error: " + lifecycle.LastError);

            if (run == null)
            {
                limitations.Add("no run record accompanies these events, so liveness cannot be assessed");
                return Make(ScratchClassification.Indeterminate, ClassificationConfidence.Low,
                    EvidenceSource.ManifestAndCatalog, ActionIndeterminate);
            }

            switch (liveness(run))
            {
                case RunLiveness.Alive:
                    return Make(ScratchClassification.ManifestedActive, ClassificationConfidence.High,
                        EvidenceSource.ManifestAndCatalog, ActionLeaveActive);

                case RunLiveness.Dead:
                    if (lifecycle.Terminal is ScratchTerminalState.CleanupFailed or ScratchTerminalState.Failed)
                        limitations.Add("the run attempted cleanup and recorded that it failed");
                    else if (lifecycle.Terminal == ScratchTerminalState.DropIntended)
                        limitations.Add("a drop was begun and never confirmed");

                    return Make(ScratchClassification.ManifestedOrphanCandidate,
                        truncated ? ClassificationConfidence.Medium : ClassificationConfidence.High,
                        EvidenceSource.ManifestAndCatalog, ActionReviewOrphan);

                default:
                    limitations.Add("the owning run's process could not be identified, so it cannot be shown " +
                                    "to have finished; it is NOT assumed dead");
                    return Make(ScratchClassification.Indeterminate, ClassificationConfidence.Low,
                        EvidenceSource.ManifestAndCatalog, ActionIndeterminate);
            }
        }

        private static ScratchFinding Unattributed(ManifestWorld world, CatalogDatabase row)
        {
            var limitations = new List<string>
            {
                "no manifest records this database; its name matches an owned scratch prefix and nothing more",
                "a matching prefix is a naming convention, not a provenance - this is NOT established as safe to remove",
            };

            if (!world.DirectoryAccessible)
                limitations.Add("the manifest directory could not be read (" +
                                (world.AccessError ?? "no reason recorded") +
                                "), so the absence of a manifest proves nothing at all");
            else
                limitations.Add("it may predate the durable manifest, or have been created by a runner that " +
                                "does not write one (for example UatSqlProbeFixture, which has its own lifecycle)");

            return new ScratchFinding(
                row.Name, ScratchClassification.UnattributedScratch, EvidenceSource.Catalog,
                RunId: null, LatestEvent: null, LatestEventUtc: null,
                PresentInCatalog: true, CreateDateServerLocal: row.CreateDateServerLocal, CatalogState: row.StateDescription,
                Confidence: ClassificationConfidence.Low,
                Limitations: limitations,
                RecommendedAction: ActionUnattributed);
        }
    }
}
