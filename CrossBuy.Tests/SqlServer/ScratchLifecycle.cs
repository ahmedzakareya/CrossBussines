namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // LIFECYCLE RECONSTRUCTION — step 2. Events in, one story per database out.
    //
    // THE SINGLE MOST IMPORTANT RULE IN THIS FILE: CreateIntent is not proof that a database exists.
    // The writer flushes the intent BEFORE issuing CREATE DATABASE, precisely so a kill in between
    // produces a false candidate rather than a missed one. Reconstruction must preserve that asymmetry
    // instead of flattening it, so creation has THREE states and not two.
    //
    // LAST EVENT WINS, BUT NOTHING IS THROWN AWAY. A database can legitimately carry DropIntent twice
    // (a failed InitializeAsync drops once, teardown drops again) and CleanupFailed followed by a later
    // Released. The terminal state is the LAST terminal event; the full chain is kept because the
    // sequence "failed, then succeeded" is a different fact from "succeeded", and a reviewer needs both.
    // =================================================================================================

    /// <summary>What the manifest can honestly say about whether the catalog was ever created.</summary>
    public enum ScratchCreationState
    {
        /// <summary>No create event at all.</summary>
        NotAttempted,

        /// <summary>CreateIntent was recorded but never confirmed - the database MAY or may not exist.</summary>
        IntentOnly,

        /// <summary>CREATE DATABASE returned without error.</summary>
        Confirmed,
    }

    /// <summary>The last thing the manifest recorded about reclaiming the database.</summary>
    public enum ScratchTerminalState
    {
        /// <summary>No drop was ever attempted.</summary>
        None,

        /// <summary>A drop was begun and never confirmed - the database may still exist.</summary>
        DropIntended,

        /// <summary>Removal was CONFIRMED against sys.databases by the run itself.</summary>
        Released,

        /// <summary>The drop ran and the database survived it.</summary>
        CleanupFailed,

        /// <summary>An operation threw.</summary>
        Failed,
    }

    /// <summary>One database's complete story, as the manifest tells it.</summary>
    public sealed record ScratchLifecycle(
        string Database,
        string RunId,
        string Machine,
        int ProcessId,
        string? Family,
        string? Owner,
        string? Role,
        DateTime FirstSeenUtc,
        ManifestEvent LatestEvent,
        ScratchCreationState Creation,
        ScratchTerminalState Terminal,
        bool SchemaStarted,
        bool SchemaCompleted,
        bool Leased,
        string? LastError,
        IReadOnlyList<ManifestEvent> History,
        IReadOnlyList<string> Warnings);

    /// <summary>One test run, as one manifest file describes it.</summary>
    public sealed record ScratchRun(
        string RunId,
        string Machine,
        int ProcessId,
        DateTime StartedUtc,
        bool Ended,
        ManifestIntegrity Integrity,
        string SourcePath,
        IReadOnlyList<string> Warnings);

    /// <summary>Everything the manifests know, ready for classification.</summary>
    public sealed record ManifestWorld(
        string Directory,
        bool DirectoryAccessible,
        string? AccessError,
        IReadOnlyList<ScratchRun> Runs,
        IReadOnlyList<ScratchLifecycle> Lifecycles,
        IReadOnlyList<string> IntegrityWarnings)
    {
        public ScratchRun? RunOf(string runId) =>
            Runs.FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
    }

    public static class ScratchLifecycleBuilder
    {
        public const string RunStarted = "RunStarted";
        public const string RunEnded = "RunEnded";
        public const string CreateIntent = "CreateIntent";
        public const string Created = "Created";
        public const string SchemaBegin = "SchemaBegin";
        public const string SchemaComplete = "SchemaComplete";
        public const string Leased = "Leased";
        public const string DropIntent = "DropIntent";
        public const string Released = "Released";
        public const string CleanupFailed = "CleanupFailed";
        public const string Failed = "Failed";

        public static ManifestWorld Build(ManifestSet set)
        {
            var runs = new List<ScratchRun>();
            var integrityWarnings = new List<string>();

            // Grouped by NAME, across files. The name carries a GUID and is unique on the instance, so it
            // is the only key that survives a run that never got to write anything else about itself.
            var byDatabase = new Dictionary<string, List<ManifestEvent>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in set.Files)
            {
                foreach (var warning in file.Warnings)
                    integrityWarnings.Add($"{Path.GetFileName(file.Path)}: {warning}");

                if (file.Events.Count == 0)
                {
                    if (file.RunId != null || file.Integrity == ManifestIntegrity.Unreadable)
                        runs.Add(new ScratchRun(file.RunId ?? "(unknown)", "(unknown)", 0, default, false,
                            file.Integrity, file.Path, file.Warnings));
                    continue;
                }

                var first = file.Events[0];
                var start = file.Events.FirstOrDefault(e => e.Event == RunStarted)?.TimestampUtc
                            ?? first.TimestampUtc;

                runs.Add(new ScratchRun(
                    first.RunId, first.Machine, first.ProcessId, start,
                    file.Events.Any(e => e.Event == RunEnded),
                    file.Integrity, file.Path, file.Warnings));

                foreach (var e in file.Events)
                {
                    if (string.IsNullOrEmpty(e.Database)) continue;

                    if (!byDatabase.TryGetValue(e.Database!, out var list))
                        byDatabase[e.Database!] = list = new List<ManifestEvent>();

                    list.Add(e);
                }
            }

            var lifecycles = byDatabase
                .Select(pair => Reconstruct(pair.Key, pair.Value))
                .OrderBy(l => l.FirstSeenUtc)
                .ThenBy(l => l.Database, StringComparer.Ordinal)
                .ToList();

            return new ManifestWorld(set.Directory, set.DirectoryAccessible, set.AccessError,
                runs, lifecycles, integrityWarnings);
        }

        private static ScratchLifecycle Reconstruct(string database, List<ManifestEvent> raw)
        {
            var history = raw.OrderBy(e => e.TimestampUtc).ThenBy(e => e.Sequence).ToList();
            var warnings = new List<string>();

            var runIds = history.Select(e => e.RunId).Distinct(StringComparer.Ordinal).ToList();
            if (runIds.Count > 1)
                warnings.Add("this database name appears under more than one runId: " + string.Join(", ", runIds));

            // CREATION: Confirmed outranks IntentOnly. An intent that was later confirmed is a confirmed
            // creation; an intent that never was stays an intent, and that difference is the whole reason
            // the writer flushes before the statement.
            var creation = history.Any(e => e.Event == Created) ? ScratchCreationState.Confirmed
                         : history.Any(e => e.Event == CreateIntent) ? ScratchCreationState.IntentOnly
                         : ScratchCreationState.NotAttempted;

            // TERMINAL: the LAST terminal event, not the worst one. CleanupFailed followed by Released is a
            // released database; Released followed by CleanupFailed would be the reverse, and both orderings
            // occur in real runs.
            var terminal = ScratchTerminalState.None;
            foreach (var e in history)
            {
                terminal = e.Event switch
                {
                    DropIntent => ScratchTerminalState.DropIntended,
                    Released => ScratchTerminalState.Released,
                    CleanupFailed => ScratchTerminalState.CleanupFailed,
                    Failed => ScratchTerminalState.Failed,
                    _ => terminal,
                };
            }

            var creating = history.FirstOrDefault(e => e.Event == CreateIntent || e.Event == Created)
                           ?? history[0];

            var lastError = history.LastOrDefault(e => !string.IsNullOrEmpty(e.Error))?.Error;

            return new ScratchLifecycle(
                Database: database,
                RunId: creating.RunId,
                Machine: creating.Machine,
                ProcessId: creating.ProcessId,
                Family: history.Select(e => e.Family).LastOrDefault(f => !string.IsNullOrEmpty(f)),
                Owner: history.Select(e => e.Owner).LastOrDefault(o => !string.IsNullOrEmpty(o)),
                Role: history.Select(e => e.Role).LastOrDefault(r => !string.IsNullOrEmpty(r)),
                FirstSeenUtc: history[0].TimestampUtc,
                LatestEvent: history[^1],
                Creation: creation,
                Terminal: terminal,
                SchemaStarted: history.Any(e => e.Event == SchemaBegin),
                SchemaCompleted: history.Any(e => e.Event == SchemaComplete),
                Leased: history.Any(e => e.Event == Leased),
                LastError: lastError,
                History: history,
                Warnings: warnings);
        }
    }
}
