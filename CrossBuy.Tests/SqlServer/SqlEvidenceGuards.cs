using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace CrossBuy.Tests.SqlServer
{
    /// <summary>
    /// Stage 2A Batch 00-C — the SQL companion guards.
    ///
    /// WHY THESE ARE FUNCTIONS AND NOT ASSERTIONS INSIDE TESTS.
    ///
    /// Until now the SQL safety rules were verified by *inspection*: a test read `SqlServerFixture.ForbiddenCatalogs`
    /// by reflection and asserted the array contained "CrossBuyDB2". That proves a LIST EXISTS. It does not prove
    /// anything refuses anything — delete the `if (ForbiddenCatalogs.Any(...))` block, keep the array, and the test
    /// still passes while the fixture happily runs against production.
    ///
    /// So each rule is a function with a return value. The fixture calls it, the tests call it with inputs that must
    /// be refused, and removing the call from the fixture breaks a test. A guard that cannot fail is not a guard, and
    /// a guard that can only be satisfied by a declaration is a comment with a test framework attached.
    /// </summary>
    public static class SqlEvidenceGuards
    {
        // -------------------------------------------------------------------------------------------------
        // Guard 1 — production catalog refusal
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Catalogs the evidence suite may never be pointed at. `CrossBuy` is included as a bare name because a
        /// developer instance may host it, and the cost of a false refusal (a skipped run with a clear reason) is
        /// nothing next to the cost of a false acceptance.
        /// </summary>
        public static readonly string[] ProductionCatalogs = { "CrossBuyDB", "CrossBuyDB2", "CrossBuy" };

        public sealed class CatalogVerdict
        {
            public bool Refused { get; init; }
            public string? Catalog { get; init; }
            public string? Reason { get; init; }
        }

        /// <summary>
        /// Decides whether a connection string may be used by the evidence suite. The ONE implementation — the
        /// fixture delegates here, so the rule cannot be satisfied in one place and skipped in another.
        /// </summary>
        public static CatalogVerdict InspectCatalog(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return new CatalogVerdict
                {
                    Refused = true,
                    Reason = "no connection string was supplied; the evidence suite refuses to guess an instance",
                };

            SqlConnectionStringBuilder builder;
            try { builder = new SqlConnectionStringBuilder(connectionString); }
            catch (Exception ex)
            {
                return new CatalogVerdict { Refused = true, Reason = "not a valid connection string: " + ex.Message };
            }

            var catalog = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(catalog))
                return new CatalogVerdict { Refused = false, Catalog = null };

            foreach (var production in ProductionCatalogs)
            {
                if (!string.Equals(production, catalog, StringComparison.OrdinalIgnoreCase)) continue;

                return new CatalogVerdict
                {
                    Refused = true,
                    Catalog = catalog,
                    Reason =
                        $"'{catalog}' is a real CrossBuy database. Point {SqlServerFixture.ConnectionStringVariable} " +
                        "at the INSTANCE only — the suite creates and drops its own scratch databases.",
                };
            }

            return new CatalogVerdict { Refused = false, Catalog = catalog };
        }

        // -------------------------------------------------------------------------------------------------
        // Guard 2 — scratch database ownership validation
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The only database-name prefixes the evidence suite creates, and therefore the only ones it may drop.
        /// </summary>
        public static readonly string[] OwnedScratchPrefixes =
        {
            "CrossBuyPlatformTest_",   // the shared fixture's own database
            "CrossBuyProbe_",          // per-family probes (RISK-036)
            "CrossBuyImp003_",         // the IMP-003 DDL reproduction probe
        };

        public static bool IsOwnedScratchName(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            OwnedScratchPrefixes.Any(p => name!.StartsWith(p, StringComparison.Ordinal));

        /// <summary>
        /// Refuses any name the suite did not create. This is the property that matters most about cleanup: it can
        /// never drop a database it does not own, whatever a caller passes it.
        /// </summary>
        public static void AssertOwnedScratchName(string? name)
        {
            if (IsOwnedScratchName(name)) return;

            throw new InvalidOperationException(
                $"refusing to drop '{name}' — the evidence suite only drops databases it created " +
                $"(prefixes: {string.Join(", ", OwnedScratchPrefixes)}).");
        }

        // -------------------------------------------------------------------------------------------------
        // Guard 3 — leftover scratch database detection
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Owned scratch databases that exist on the server but are not accounted for by the current run.
        ///
        /// A leftover is not cosmetic. It means a previous run died before teardown, so the NEXT run inherits state
        /// the tests did not create — which is how an order-dependent pass appears, and how RISK-036 hid.
        /// </summary>
        public static IReadOnlyList<string> Leftovers(IEnumerable<string> onServer, IEnumerable<string> accountedFor)
        {
            var known = new HashSet<string>(accountedFor, StringComparer.OrdinalIgnoreCase);

            return onServer
                .Where(IsOwnedScratchName)
                .Where(name => !known.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Lists every owned-prefix database on the instance. Read-only; touches no user database.</summary>
        public static async Task<List<string>> OwnedDatabasesOnServerAsync(string masterConnectionString)
        {
            var names = new List<string>();

            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sys.databases WHERE " +
                string.Join(" OR ", OwnedScratchPrefixes.Select((_, i) => $"name LIKE @p{i} + '%'")) +
                " ORDER BY name;";

            for (var i = 0; i < OwnedScratchPrefixes.Length; i++)
                command.Parameters.AddWithValue("@p" + i, OwnedScratchPrefixes[i]);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            return names;
        }

        // -------------------------------------------------------------------------------------------------
        // Guard 4 — mandatory SQL execution verification
        // -------------------------------------------------------------------------------------------------

        public const string StrictModeVariable = "CROSSBUY_SQL_REQUIRED";

        public sealed class ExecutionRequirement
        {
            public bool Satisfied { get; init; }
            public string? Failure { get; init; }
            public bool Executed { get; init; }
        }

        /// <summary>
        /// Decides whether "the SQL evidence did not run" is acceptable.
        ///
        /// Two situations, and only one of them is ever acceptable:
        ///
        ///   * the variable is UNSET — no instance was offered. Acceptable on a developer machine, and reported in the
        ///     summary so it cannot be mistaken for coverage. Set <c>CROSSBUY_SQL_REQUIRED=1</c> (CI does) to make it
        ///     a failure.
        ///
        ///   * the variable is SET and the fixture is still unavailable — NEVER acceptable, in any mode. This is the
        ///     Stage 1 failure exactly: the configuration looked present, 46 tests reported Skipped, and the skip was
        ///     counted as coverage for two batches. A misconfiguration that silently disables every SQL proof must be
        ///     loud, not a skip.
        /// </summary>
        public static ExecutionRequirement RequireExecution(
            bool connectionConfigured,
            bool fixtureAvailable,
            string? skipReason,
            bool strict)
        {
            if (fixtureAvailable)
                return new ExecutionRequirement { Satisfied = true, Executed = true };

            if (connectionConfigured)
                return new ExecutionRequirement
                {
                    Satisfied = false,
                    Executed = false,
                    Failure =
                        $"{SqlServerFixture.ConnectionStringVariable} IS set, yet the SQL fixture is unavailable, so " +
                        "every SQL proof reported Skipped while looking configured. That is the Stage 1 failure mode " +
                        "(46 tests skipped for two batches). Reason given: " + (skipReason ?? "(none)"),
                };

            if (strict)
                return new ExecutionRequirement
                {
                    Satisfied = false,
                    Executed = false,
                    Failure =
                        $"{StrictModeVariable} is set, so the SQL evidence is mandatory, but " +
                        $"{SqlServerFixture.ConnectionStringVariable} is not configured. No SQL proof executed.",
                };

            return new ExecutionRequirement
            {
                Satisfied = true,
                Executed = false,
                Failure = null,
            };
        }

        // -------------------------------------------------------------------------------------------------
        // Guards 5 & 6 — the evidence manifest, in both directions
        // -------------------------------------------------------------------------------------------------

        public sealed class ManifestModel
        {
            /// <summary>declared class name -> declared test names.</summary>
            public Dictionary<string, HashSet<string>> Declared { get; } = new(StringComparer.Ordinal);

            /// <summary>Groups carrying a "status" are PLANNED for a later batch and are not expected to exist yet.</summary>
            public List<string> PlannedGroups { get; } = new();

            public int DeclaredTestCount => Declared.Values.Sum(v => v.Count);
        }

        public static ManifestModel LoadManifest(string repoRoot)
        {
            var path = Path.Combine(repoRoot, "engineering", "required-evidence-manifest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            var model = new ManifestModel();

            foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
            {
                var name = group.GetProperty("group").GetString()!;
                if (group.TryGetProperty("status", out _)) { model.PlannedGroups.Add(name); continue; }

                var cls = group.GetProperty("class").GetString()!;
                if (!model.Declared.TryGetValue(cls, out var tests))
                    model.Declared[cls] = tests = new HashSet<string>(StringComparer.Ordinal);

                foreach (var test in group.GetProperty("tests").EnumerateArray())
                    tests.Add(test.GetString()!);
            }

            return model;
        }

        /// <summary>Every xunit test method in the assembly, keyed by its full type name.</summary>
        public static Dictionary<string, HashSet<string>> DiscoverTests(Assembly assembly, string namespacePrefix)
        {
            var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var type in assembly.GetTypes())
            {
                if (type.FullName == null) continue;
                if (!type.FullName.StartsWith(namespacePrefix, StringComparison.Ordinal)) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!IsTestMethod(method)) continue;

                    if (!found.TryGetValue(type.FullName, out var names))
                        found[type.FullName] = names = new HashSet<string>(StringComparer.Ordinal);

                    names.Add(method.Name);
                }
            }

            return found;
        }

        /// <summary>
        /// A test method by ATTRIBUTE, matched on the attribute type's name rather than a reference to it, so
        /// [Fact], [Theory] and Xunit.SkippableFact are all recognised without the guard needing to know which
        /// package each one lives in.
        /// </summary>
        private static bool IsTestMethod(MethodInfo method) =>
            method.GetCustomAttributes(inherit: true).Any(a =>
            {
                var name = a.GetType().Name;
                return name is "FactAttribute" or "TheoryAttribute" or "SkippableFactAttribute" or "SkippableTheoryAttribute";
            });

        public sealed class ManifestVerification
        {
            /// <summary>Declared on a class that does not exist, or not present on the class that declares it.</summary>
            public List<string> DeclaredButMissing { get; } = new();

            /// <summary>A real test that no manifest group registers. Guard 6.</summary>
            public List<string> DiscoveredButUnregistered { get; } = new();

            public bool Clean => DeclaredButMissing.Count == 0 && DiscoveredButUnregistered.Count == 0;
        }

        /// <summary>
        /// Reconciles the manifest against the assembly in BOTH directions.
        ///
        /// The existing Batch 00 guard checks one direction, and does it by GLOBAL method name: a declared test is
        /// satisfied by a method of that name on ANY type. That passes when a test is moved to a different class, and
        /// it cannot see a SQL test that nobody registered at all. Both directions are checked here, and the
        /// declared direction is checked against the DECLARED CLASS.
        /// </summary>
        public static ManifestVerification VerifyManifest(
            ManifestModel manifest,
            Dictionary<string, HashSet<string>> discovered,
            string namespacePrefix)
        {
            var verification = new ManifestVerification();

            foreach (var (cls, tests) in manifest.Declared)
            {
                if (!cls.StartsWith(namespacePrefix, StringComparison.Ordinal)) continue;

                if (!discovered.TryGetValue(cls, out var actual))
                {
                    foreach (var test in tests.OrderBy(t => t, StringComparer.Ordinal))
                        verification.DeclaredButMissing.Add($"{cls}.{test} (the class itself was not discovered)");
                    continue;
                }

                foreach (var test in tests.OrderBy(t => t, StringComparer.Ordinal))
                    if (!actual.Contains(test)) verification.DeclaredButMissing.Add($"{cls}.{test}");
            }

            foreach (var (cls, tests) in discovered)
            {
                manifest.Declared.TryGetValue(cls, out var declared);

                foreach (var test in tests.OrderBy(t => t, StringComparer.Ordinal))
                    if (declared == null || !declared.Contains(test))
                        verification.DiscoveredButUnregistered.Add($"{cls}.{test}");
            }

            verification.DeclaredButMissing.Sort(StringComparer.Ordinal);
            verification.DiscoveredButUnregistered.Sort(StringComparer.Ordinal);
            return verification;
        }

        // -------------------------------------------------------------------------------------------------
        // Guard 7 — SQL evidence summary output
        // -------------------------------------------------------------------------------------------------

        public sealed class SqlEvidenceSummary
        {
            public bool ConnectionConfigured { get; init; }
            public bool FixtureAvailable { get; init; }
            public string? SkipReason { get; init; }
            public bool StrictMode { get; init; }
            public string? CatalogInspected { get; init; }
            public int DeclaredTests { get; init; }
            public int DiscoveredTests { get; init; }
            public int DeclaredButMissing { get; init; }
            public int Unregistered { get; init; }
            public List<string> OwnedDatabasesOnServer { get; init; } = new();
            public List<string> Leftovers { get; init; } = new();
            public string? SharedFixtureFingerprint { get; init; }
        }

        /// <summary>
        /// Renders the summary. Its job is to make a NON-execution impossible to overlook: the Stage 1 defect was not
        /// that tests skipped, it was that nobody could tell from the output that they had.
        /// </summary>
        public static string RenderSummary(SqlEvidenceSummary s)
        {
            var text = new StringBuilder();

            text.AppendLine("# SQL Evidence Summary");
            text.AppendLine();
            text.AppendLine("Generated by `SqlCompanionGuardTests`. Regenerated on every run of the suite.");
            text.AppendLine();

            var verdict = s.FixtureAvailable
                ? "**EXECUTED** — the SQL evidence ran against a real SQL Server instance."
                : s.StrictMode
                    ? "**FAILED** — strict mode is on and the SQL evidence did not run."
                    : "**NOT EXECUTED** — no instance configured. Nothing below may be cited as SQL coverage.";

            text.AppendLine("## Verdict");
            text.AppendLine();
            text.AppendLine(verdict);
            text.AppendLine();

            text.AppendLine("## Environment");
            text.AppendLine();
            text.AppendLine("| Item | Value |");
            text.AppendLine("|---|---|");
            text.AppendLine($"| `{SqlServerFixture.ConnectionStringVariable}` configured | {s.ConnectionConfigured} |");
            text.AppendLine($"| `{StrictModeVariable}` (strict) | {s.StrictMode} |");
            text.AppendLine($"| Fixture available | {s.FixtureAvailable} |");
            text.AppendLine($"| Initial catalog inspected | {s.CatalogInspected ?? "(instance only — correct)"} |");
            if (!s.FixtureAvailable) text.AppendLine($"| Skip reason | {s.SkipReason ?? "(none given)"} |");
            text.AppendLine();

            text.AppendLine("## Manifest reconciliation");
            text.AppendLine();
            text.AppendLine("| Item | Count |");
            text.AppendLine("|---|---|");
            text.AppendLine($"| Declared in the manifest (SQL evidence) | {s.DeclaredTests} |");
            text.AppendLine($"| Discovered in the assembly | {s.DiscoveredTests} |");
            text.AppendLine($"| Declared but missing | {s.DeclaredButMissing} |");
            text.AppendLine($"| Discovered but unregistered | {s.Unregistered} |");
            text.AppendLine();

            text.AppendLine("## Scratch database hygiene");
            text.AppendLine();
            text.AppendLine("| Item | Value |");
            text.AppendLine("|---|---|");
            text.AppendLine($"| Owned databases on the instance | {s.OwnedDatabasesOnServer.Count} |");
            text.AppendLine($"| Leftovers from earlier runs | {s.Leftovers.Count} |");
            text.AppendLine($"| Shared fixture fingerprint | `{s.SharedFixtureFingerprint ?? "(not read)"}` |");
            text.AppendLine();

            if (s.Leftovers.Count > 0)
            {
                text.AppendLine("### Leftovers");
                text.AppendLine();
                foreach (var name in s.Leftovers) text.AppendLine($"* `{name}`");
                text.AppendLine();
            }

            text.AppendLine("Owned prefixes: " +
                            string.Join(", ", OwnedScratchPrefixes.Select(p => "`" + p + "`")) +
                            ". Production catalogs refused: " +
                            string.Join(", ", ProductionCatalogs.Select(p => "`" + p + "`")) + ".");

            return text.ToString();
        }

        // -------------------------------------------------------------------------------------------------

        public static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln")))
                directory = directory.Parent;

            if (directory == null)
                throw new InvalidOperationException("CrossBuy.sln not found above " + AppContext.BaseDirectory);

            return directory.FullName;
        }
    }
}
