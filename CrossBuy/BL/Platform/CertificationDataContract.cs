namespace CrossBuy.BL.Platform
{
    // UI CERTIFICATION — the DATA contract, stated as code rather than as a promise in a runbook.
    //
    // WHY THIS EXISTS, measured rather than assumed. A full governed matrix was captured on
    // 2026-08-15 against CrossBuyCert while two announcement fixtures were visible, and verified
    // later against CrossBuyDev where the same two rows were switched off. Every route came back
    // ~167px shorter and all 138 screenshots failed. Nothing about the application had changed:
    // the two runs simply looked at different DATA. A screenshot baseline is only falsifiable if
    // the dataset behind it is pinned as precisely as the code is, so the dataset is pinned here.
    //
    // WHAT THIS IS NOT. It is not a seeder, not a migration, and not a runtime feature. It holds no
    // connection and performs no I/O. It is the set of decisions a certification reset and the
    // pre-flight check must both agree on, expressed as pure functions so they can be tested
    // without a host and cannot drift apart — the same shape as CertificationDataGate and
    // ConformanceClock, deliberately.
    //
    // RELATIONSHIP TO THE CLOCK. ConformanceClock fixes the reference instant for relative-time
    // LABELS and says so explicitly: it governs presentation, never data. Announcement visibility
    // is a DATA query (AnnouncementService filters on DateTime.UtcNow), so the clock cannot and
    // must not reach it. The fixtures are therefore made time-independent by the values they are
    // materialised with — see FixtureWindow — instead of by bending production time semantics.
    public static class CertificationDataContract
    {
        // ---- identity -------------------------------------------------------------------------

        // The one catalogue certification may target. Reuses the gate's marker so a rename cannot
        // leave two different opinions about which database is the certification database.
        public const string CertificationDatabase = CertificationDataGate.CertificationDatabaseMarker;

        // Same switch the gate and the clock already use. One switch for "this host is running the
        // certification harness" is harder to half-enable than three.
        public const string EnableEnvironmentVariable = CertificationDataGate.EnableEnvironmentVariable;

        // Catalogues a certification reset must refuse under every flag combination. CrossBuyDev is
        // on this list deliberately and is the whole point of the list: the tempting "fix" for the
        // 138 failures was to switch the fixtures on in CrossBuyDev so the numbers matched, which
        // would have made a Dev run imitate a Cert baseline and hidden the real mismatch for good.
        public static readonly string[] ForbiddenCatalogs =
        {
            "CrossBuyDev", "CrossBuyDB", "CrossBuyDB2", "CrossBuy", "alprimedb_prod",
        };

        // ---- the fixtures ---------------------------------------------------------------------

        // The certification announcement fixtures, by the exact titles already present in
        // CrossBuyCert. They are addressed by title because that is what makes them recognisable in
        // a database a human is looking at, and the "ZZ-" prefix exists so they sort last and are
        // never mistaken for business content.
        public const string CriticalBannerTitle = "ZZ-UI-CONFORMANCE Critical banner";
        public const string HighBannerTitle = "ZZ-UI-CONFORMANCE High priority banner";

        // The company every governed route is certified against.
        public const int CertificationCompanyId = 1;

        // THE WALL-CLOCK FIX.
        //
        // The fixtures as found expired on 2026-08-20T19:49:47. That is not a certification dataset;
        // it is a certification dataset with a fuse. On 2026-08-21 both banners would have vanished,
        // every page would have lost the same ~167px, and all 138 screenshots would have failed
        // again — the identical failure, with the machine's calendar as the cause instead of the
        // database name.
        //
        // The window below is materialised by the reset and is wide enough that no real "now" a
        // certification host can plausibly report falls outside it. Visibility therefore stops being
        // a function of the date. This is a property of the FIXTURE DATA only: AnnouncementService
        // keeps filtering on DateTime.UtcNow exactly as it does for ordinary announcements, no title
        // is special-cased anywhere in business logic, and no production bypass is introduced.
        public static readonly DateTime FixtureWindowStart = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static readonly DateTime FixtureWindowEnd = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // The visibility predicate, mirroring AnnouncementService's active filter
        //   IsActive && (StartsAt == null || StartsAt <= now) && (ExpiresAt == null || ExpiresAt >= now)
        // so a test can assert the fixtures are visible at instants years apart WITHOUT a database
        // and without waiting for a calendar to move. Duplicating the predicate is the point: if the
        // production filter ever changes shape, the contract test that pins this one is where the
        // disagreement surfaces.
        public static bool IsVisibleAt(DateTime instant, bool isActive, DateTime? startsAt, DateTime? expiresAt)
            => isActive
               && (startsAt is null || startsAt <= instant)
               && (expiresAt is null || expiresAt >= instant);

        // Is the canonical fixture window visible at this instant? Must be true for every instant a
        // host can report.
        public static bool FixtureVisibleAt(DateTime instant)
            => IsVisibleAt(instant, isActive: true, FixtureWindowStart, FixtureWindowEnd);

        // ---- guards ---------------------------------------------------------------------------

        // Does this connection string name the catalogue given? Deliberately a NAME comparison on
        // the parsed catalogue rather than a substring scan of the whole string: "CrossBuyCert"
        // appears inside "CrossBuyCertScratch" too, and a reset that cannot tell those apart is a
        // reset that will eventually rebuild the wrong database.
        public static string? CatalogOf(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim();
                if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                    return part[(eq + 1)..].Trim();
            }
            return null;
        }

        // May a DESTRUCTIVE certification reset run against this target?
        //
        // Fail closed at every step: an absent connection string, an absent catalogue, a catalogue on
        // the forbidden list, and anything that is not exactly CrossBuyCert are all refused. There is
        // no flag that turns any of these into a yes, because "the operator passed --force" is how a
        // reset tool reaches a database nobody meant to rebuild.
        public static string? ResetRefusalReason(bool isDevelopment, string? flagValue, string? connectionString)
        {
            if (!isDevelopment)
                return "a certification reset may run only on a Development host.";

            if (!string.Equals(flagValue, "1", StringComparison.Ordinal))
                return $"{EnableEnvironmentVariable} must be exactly \"1\" — this host has not declared "
                       + "itself a certification host.";

            var catalog = CatalogOf(connectionString);
            if (string.IsNullOrWhiteSpace(catalog))
                return "the connection string names no catalogue; the reset refuses to guess one.";

            foreach (var forbidden in ForbiddenCatalogs)
                if (string.Equals(catalog, forbidden, StringComparison.OrdinalIgnoreCase))
                    return $"'{catalog}' is not a certification database. A certification reset is "
                           + "destructive and never runs against development, reference or production data.";

            if (!string.Equals(catalog, CertificationDatabase, StringComparison.OrdinalIgnoreCase))
                return $"'{catalog}' is not '{CertificationDatabase}'. Only the certification "
                       + "catalogue may be reset.";

            return null;
        }

        // Convenience for callers that only need the verdict.
        public static bool MayReset(bool isDevelopment, string? flagValue, string? connectionString)
            => ResetRefusalReason(isDevelopment, flagValue, connectionString) is null;

        // ---- golden artifact ------------------------------------------------------------------

        // Creating a golden backup and restoring one are the same decision as resetting: both are
        // certification-only operations against exactly one catalogue, and a mistake in either
        // direction is unrecoverable — a backup taken from the wrong database becomes the canonical
        // dataset, and a restore aimed at the wrong database destroys it. They therefore share the
        // guard rather than growing two that can drift apart.
        public static string? BackupRefusalReason(bool isDevelopment, string? flagValue, string? connectionString)
            => ResetRefusalReason(isDevelopment, flagValue, connectionString);

        public static string? RestoreRefusalReason(bool isDevelopment, string? flagValue, string? connectionString)
            => ResetRefusalReason(isDevelopment, flagValue, connectionString);

        public static bool MayBackup(bool isDevelopment, string? flagValue, string? connectionString)
            => BackupRefusalReason(isDevelopment, flagValue, connectionString) is null;

        public static bool MayRestore(bool isDevelopment, string? flagValue, string? connectionString)
            => RestoreRefusalReason(isDevelopment, flagValue, connectionString) is null;

        // The golden artifact must never live inside the repository: a 272MB binary is not
        // reviewable, cannot be diffed, and would be committed by the first person running
        // `git add -A`. This asks the question directly rather than trusting a .gitignore entry
        // that a future edit can remove.
        public static bool IsOutsideRepository(string? artifactPath, string? repositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(repositoryRoot)) return false;
            var artifact = Path.GetFullPath(artifactPath).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
            return !artifact.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && !artifact.Equals(root, StringComparison.OrdinalIgnoreCase);
        }

        // ---- certification RUNTIME mode -------------------------------------------------------

        // Is this process a certification run? All three conditions must hold, and the third is the
        // one that matters: UI_CONFORMANCE=1 pointed at any other database is already refused at
        // startup by CertificationDataGate, so this can only ever answer true for CrossBuyCert.
        //
        // Background writers are suppressed on exactly this signal. Six hosted services
        // (task generation, schedule matching, integrity checks, outbox dispatch, comm dispatch,
        // CRM reminders) mutate data the governed routes RENDER — a task generated between two
        // captures changes /Tasks, and an integrity run changes a row count on screen. They were
        // all live during the 2026-08-15 capture, which is part of why that baseline was never
        // reproducible. Suppression is decided here, once, so six services do not each grow their
        // own copy of an environment check.
        public static bool IsCertificationRuntime(bool isDevelopment, string? flagValue, string? connectionString)
            => isDevelopment
               && string.Equals(flagValue, "1", StringComparison.Ordinal)
               && string.Equals(CatalogOf(connectionString), CertificationDatabase, StringComparison.OrdinalIgnoreCase);
    }

    // The certification-mode decision, resolved ONCE at startup and then carried.
    //
    // WHY A CARRIED VALUE RATHER THAN A SECOND CALL. Two places need this answer: the hosted-service
    // registrations that suppress the six background writers, and /BusinessEventMonitor/Runtime, which
    // is what the determinism gate reads to decide which contract applies to a run. If the endpoint
    // re-evaluated the decision it could, in principle, disagree with the registrations — and the one
    // thing the gate must be able to trust is that "writers are suppressed" and "this is certification
    // mode" describe the same process. So the value is computed once in Program.cs, registered, and
    // read from here by everything downstream.
    //
    // BackgroundWritersSuppressed is deliberately NOT an independent flag. Suppression is not a
    // separate switch someone can half-set; it is what certification mode MEANS for this process.
    // Reporting them as one derived value makes the impossible combination impossible to report.
    public sealed class CertificationRuntimeState
    {
        public CertificationRuntimeState(bool certificationMode) => CertificationMode = certificationMode;

        public bool CertificationMode { get; }

        public bool BackgroundWritersSuppressed => CertificationMode;
    }
}
