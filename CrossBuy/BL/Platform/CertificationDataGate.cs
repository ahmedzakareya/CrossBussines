using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // UI CERTIFICATION — the gate that makes a certification DATABASE impossible outside a
    // development/conformance host.
    //
    // WHY THIS EXISTS. Certification now runs against a dedicated database (CrossBuyCert) that is
    // restored from a golden snapshot before every run, so the rendered UI depends on fixed DATA as well
    // as a fixed clock. The database is selected by the connection string, which is host configuration —
    // and host configuration is exactly the kind of thing that gets copied to the wrong environment.
    //
    // A production host pointed at a certification database would serve restored, stale, snapshot data to
    // real users, and would do so silently. So the application REFUSES to start in that combination
    // rather than trusting deployment discipline. The refusal is loud and at startup, not at first query.
    //
    // This mirrors ConformanceClock exactly: the same two-condition gate (Development AND an explicit
    // flag), the same pure decision function so it can be tested without a host, and the same rule that
    // nothing a browser sends can influence it — a connection string is never request-supplied.
    public static class CertificationDataGate
    {
        // The database-name marker. Deliberately a NAME check rather than a full connection-string
        // parse: the thing being guarded is "am I pointed at the certification dataset", and the name is
        // what makes that unmistakable to a human reading a config file too.
        public const string CertificationDatabaseMarker = "CrossBuyCert";

        // Same flag the deterministic clock already uses. One switch for "this host is running the
        // certification harness" is easier to reason about — and harder to half-enable — than two.
        public const string EnableEnvironmentVariable = "UI_CONFORMANCE";

        // May this host use a certification database?
        //
        // Both conditions are required and the flag must be exactly "1". "true", "yes", "0", empty and
        // null are all REFUSED: a permissive parse is how a half-configured host quietly enables a mode
        // nobody meant to enable.
        public static bool ShouldAllowCertificationDatabase(bool isDevelopment, string? flagValue)
            => isDevelopment && string.Equals(flagValue, "1", StringComparison.Ordinal);

        // Does this connection string point at a certification database?
        //
        // Ordinal-ignore-case because SQL Server database names are case-insensitive, so a differently
        // cased name reaches the same database and must reach the same decision.
        public static bool TargetsCertificationDatabase(string? connectionString)
            => !string.IsNullOrWhiteSpace(connectionString)
               && connectionString.Contains(CertificationDatabaseMarker, StringComparison.OrdinalIgnoreCase);

        // The startup assertion. Returns the reason to refuse, or null when the combination is safe.
        //
        // Note what is NOT refused: a development host WITHOUT the flag pointed at an ordinary database.
        // That is the normal case and must stay untouched. Only the dangerous combination — a
        // certification database on a host that has not declared itself a certification host — fails.
        public static string? RefusalReason(bool isDevelopment, string? flagValue, string? connectionString)
        {
            if (!TargetsCertificationDatabase(connectionString)) return null;

            if (!ShouldAllowCertificationDatabase(isDevelopment, flagValue))
                return $"This host is configured to use a certification database " +
                       $"('{CertificationDatabaseMarker}'), but it is not a certification host " +
                       $"(Development = {isDevelopment}, {EnableEnvironmentVariable} = " +
                       $"{(string.IsNullOrEmpty(flagValue) ? "<unset>" : "<set, not \"1\">")}). " +
                       "A certification database holds restored snapshot data and must never serve real users.";

            return null;
        }

        // Called once at startup. Throws rather than logging: a production host serving snapshot data is
        // not a condition to warn about and continue through.
        //
        // The message deliberately does NOT include the connection string — it carries credentials.
        public static void AssertSafe(bool isDevelopment, string? flagValue, string? connectionString, ILogger? log = null)
        {
            var reason = RefusalReason(isDevelopment, flagValue, connectionString);
            if (reason == null)
            {
                if (TargetsCertificationDatabase(connectionString))
                    log?.LogWarning(
                        "This host is running against the UI CERTIFICATION database. Its data is restored from a " +
                        "snapshot before every certification run and is not development data.");
                return;
            }

            log?.LogCritical("Startup refused: {Reason}", reason);
            throw new InvalidOperationException(reason);
        }
    }
}
