using System.Reflection;
using Microsoft.Data.SqlClient;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    /// <summary>
    /// Stage 2A Batch 00-C — the SQL companion guards, with a failure proof for each.
    ///
    /// DELIBERATELY NOT GATED on CROSSBUY_TEST_SQL, except the four that genuinely need a server. The whole point of
    /// guard 4 is to catch the case where the SQL-gated tests are silently absent, so a guard that skips with them
    /// would be useless — that was the Stage 1 failure: 46 tests reported Skipped for two batches while the skip was
    /// counted as coverage.
    ///
    /// "Every guard must prove it can fail" is taken literally. Each guard is exercised with an input it MUST reject,
    /// and the rejection is asserted. A guard whose negative branch is never executed is a guard nobody has tested.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class SqlCompanionGuardTests
    {
        private readonly SqlServerFixture _sql;

        public SqlCompanionGuardTests(SqlServerFixture sql) => _sql = sql;

        private static bool ConnectionConfigured =>
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(SqlServerFixture.ConnectionStringVariable));

        private static bool StrictMode =>
            Environment.GetEnvironmentVariable(SqlEvidenceGuards.StrictModeVariable) is "1" or "true" or "TRUE";

        private const string SqlNamespace = "CrossBuy.Tests.SqlServer.";

        // =============================================================================================
        // GUARD 1 — production catalog refusal
        // =============================================================================================

        [Theory]
        [InlineData("CrossBuyDB2")]
        [InlineData("CrossBuyDB")]
        [InlineData("CrossBuy")]
        [InlineData("crossbuydb2")]      // case must not be an escape hatch
        [InlineData("CROSSBUYDB2")]
        public void Guard1_refuses_a_connection_string_naming_a_production_catalog(string catalog)
        {
            var verdict = SqlEvidenceGuards.InspectCatalog(
                $"Server=.;Database={catalog};Trusted_Connection=True;TrustServerCertificate=True");

            Assert.True(verdict.Refused, $"'{catalog}' must be refused");
            Assert.Equal(catalog, verdict.Catalog);
            Assert.Contains("real CrossBuy database", verdict.Reason);
        }

        [Fact]
        public void Guard1_accepts_an_instance_only_connection_string()
        {
            var verdict = SqlEvidenceGuards.InspectCatalog("Server=.;Trusted_Connection=True;TrustServerCertificate=True");

            Assert.False(verdict.Refused);
            Assert.Null(verdict.Catalog);
        }

        [Fact]
        public void Guard1_accepts_a_scratch_catalog_because_the_suite_creates_those_itself()
        {
            Assert.False(SqlEvidenceGuards.InspectCatalog(
                "Server=.;Database=CrossBuyPlatformTest_abc;Trusted_Connection=True").Refused);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("NotARealKeyword=1")]                 // SqlConnectionStringBuilder rejects unknown keywords
        [InlineData("Server=.;Integrated Security=maybe")] // and non-boolean booleans
        public void Guard1_refuses_a_missing_or_malformed_connection_string_rather_than_guessing(string? raw)
        {
            Assert.True(SqlEvidenceGuards.InspectCatalog(raw).Refused);
        }

        [Fact]
        public void Guard1_does_not_claim_to_detect_every_malformed_string()
        {
            // Honesty about the boundary, pinned so it is not later mistaken for a stronger guarantee.
            // "this is not a connection string;;;=" parses without error — SqlConnectionStringBuilder tolerates empty
            // segments — so it yields no catalog and is NOT refused by this guard. The fixture would then fail to
            // connect, which is loud. The guard's job is the CATALOG decision, not connection-string validation.
            var verdict = SqlEvidenceGuards.InspectCatalog("this is not a connection string;;;=");

            Assert.False(verdict.Refused);
            Assert.Null(verdict.Catalog);
        }

        [Fact]
        public void Guard1_is_the_rule_the_FIXTURE_actually_consults_not_a_parallel_copy()
        {
            // The defect this closes: the pre-existing test asserted only that a ForbiddenCatalogs ARRAY contained
            // "CrossBuyDB2". Delete the refusal branch, keep the array, and it still passed. The fixture now projects
            // its list from the guard, so the two can never disagree.
            var field = typeof(SqlServerFixture).GetField(
                "ForbiddenCatalogs", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(field);
            Assert.Same(SqlEvidenceGuards.ProductionCatalogs, field!.GetValue(null));
        }

        [Fact]
        public void Guard1_the_configured_connection_string_is_itself_clean()
        {
            // Applied to the REAL environment, not only to synthetic inputs. Unconditional: if someone points the
            // variable at CrossBuyDB2, that must fail here rather than quietly skip every SQL proof.
            if (!ConnectionConfigured) return;

            var raw = Environment.GetEnvironmentVariable(SqlServerFixture.ConnectionStringVariable);
            var verdict = SqlEvidenceGuards.InspectCatalog(raw);

            Assert.False(verdict.Refused,
                "the configured SQL evidence connection string is refused: " + verdict.Reason);
        }

        // =============================================================================================
        // GUARD 2 — scratch database ownership validation
        // =============================================================================================

        [Theory]
        [InlineData("CrossBuyPlatformTest_deadbeef")]
        [InlineData("CrossBuyProbe_F4_deadbeef")]
        [InlineData("CrossBuyProbe_BatchP_deadbeef")]
        [InlineData("CrossBuyImp003_deadbeef")]
        public void Guard2_accepts_a_name_the_suite_creates(string name)
        {
            Assert.True(SqlEvidenceGuards.IsOwnedScratchName(name));
            SqlEvidenceGuards.AssertOwnedScratchName(name);   // must not throw
        }

        [Theory]
        [InlineData("CrossBuyDB2")]
        [InlineData("master")]
        [InlineData("msdb")]
        [InlineData("SomeCustomerDatabase")]
        [InlineData("crossbuyprobe_lowercase")]   // ordinal prefix: case is not ownership
        [InlineData("XCrossBuyProbe_prefixed")]   // must be a PREFIX, not a substring
        [InlineData("")]
        [InlineData(null)]
        public void Guard2_refuses_a_name_the_suite_did_not_create(string? name)
        {
            Assert.False(SqlEvidenceGuards.IsOwnedScratchName(name));

            var thrown = Assert.Throws<InvalidOperationException>(() => SqlEvidenceGuards.AssertOwnedScratchName(name));
            Assert.Contains("refusing to drop", thrown.Message);
        }

        [Fact]
        public async Task Guard2_the_FIXTURE_refuses_to_drop_a_database_it_does_not_own()
        {
            // Behavioural, not declarative: the fixture is handed a foreign name and must refuse. No server needed —
            // the refusal happens before any connection is opened, which is itself the right design.
            var foreign = new SqlServerFixture.ProbeDatabase("CrossBuyDB2", "Server=.;Database=CrossBuyDB2;");

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _sql.DropProbeDatabaseAsync(foreign));

            Assert.Contains("refusing to drop", thrown.Message);
            Assert.Contains("CrossBuyDB2", thrown.Message);
        }

        // =============================================================================================
        // GUARD 3 — leftover scratch database detection
        // =============================================================================================

        [Fact]
        public void Guard3_reports_an_owned_database_that_no_current_run_accounts_for()
        {
            var leftovers = SqlEvidenceGuards.Leftovers(
                onServer: new[] { "CrossBuyPlatformTest_current", "CrossBuyProbe_F4_orphan", "CrossBuyDB2", "master" },
                accountedFor: new[] { "CrossBuyPlatformTest_current" });

            Assert.Equal(new[] { "CrossBuyProbe_F4_orphan" }, leftovers);
        }

        [Fact]
        public void Guard3_never_reports_a_database_the_suite_does_not_own()
        {
            // A leak detector that reports production databases would be worse than none: the fix for a reported
            // leftover is to drop it.
            var leftovers = SqlEvidenceGuards.Leftovers(
                onServer: new[] { "CrossBuyDB2", "master", "msdb", "SomeCustomerDatabase" },
                accountedFor: Array.Empty<string>());

            Assert.Empty(leftovers);
        }

        [Fact]
        public void Guard3_is_case_insensitive_about_what_is_accounted_for()
        {
            // SQL Server collations are frequently case-insensitive, so a name that differs only in case is the SAME
            // database. Treating it as a leftover would make the guard demand the deletion of a database in use.
            Assert.Empty(SqlEvidenceGuards.Leftovers(
                onServer: new[] { "CrossBuyProbe_F4_ABC" },
                accountedFor: new[] { "crossbuyprobe_f4_abc" }));
        }

        [SkippableFact]
        public async Task Guard3_no_leftover_scratch_database_exists_on_the_real_instance()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);

            var master = new SqlConnectionStringBuilder(_sql.TestConnectionString) { InitialCatalog = "master" };
            var onServer = await SqlEvidenceGuards.OwnedDatabasesOnServerAsync(master.ConnectionString);

            // Accounted for: ONLY this run's shared fixture database.
            //
            // DEFECT FOUND AND FIXED HERE. The first version also added ListOwnedProbeDatabasesAsync(), which
            // returns EVERY CrossBuyProbe_% database on the instance — so every leftover was automatically
            // "accounted for" and this assertion could never fail. It passed while 36 leaked probes sat on the
            // server. That is the "guard that passes by looking away" failure this whole batch of guards exists to
            // prevent, committed by the guard itself.
            //
            // The SqlServer tests share one xUnit collection and therefore run serially, so no other family's probe
            // is legitimately alive while this runs. Any probe present IS a leftover.
            var accountedFor = new List<string>
            {
                new SqlConnectionStringBuilder(_sql.TestConnectionString).InitialCatalog,
            };

            var leftovers = SqlEvidenceGuards.Leftovers(onServer, accountedFor);

            Assert.True(leftovers.Count == 0,
                "scratch databases from an earlier run survive on the instance. The next run would inherit state the " +
                "tests did not create, which is how an order-dependent pass appears:\n  " +
                string.Join("\n  ", leftovers));
        }

        // =============================================================================================
        // GUARD 4 — mandatory SQL execution verification
        // =============================================================================================

        [Fact]
        public void Guard4_FAILS_when_the_variable_is_set_but_the_fixture_is_unavailable()
        {
            // The Stage 1 failure mode, reproduced as an input. Never acceptable in any mode: the configuration looks
            // present and every proof reports Skipped.
            var requirement = SqlEvidenceGuards.RequireExecution(
                connectionConfigured: true, fixtureAvailable: false, skipReason: "could not create the database", strict: false);

            Assert.False(requirement.Satisfied);
            Assert.False(requirement.Executed);
            Assert.Contains("IS set", requirement.Failure);
            Assert.Contains("Stage 1 failure mode", requirement.Failure);
        }

        [Fact]
        public void Guard4_FAILS_in_strict_mode_when_no_instance_is_configured()
        {
            var requirement = SqlEvidenceGuards.RequireExecution(
                connectionConfigured: false, fixtureAvailable: false, skipReason: "variable unset", strict: true);

            Assert.False(requirement.Satisfied);
            Assert.Contains(SqlEvidenceGuards.StrictModeVariable, requirement.Failure);
        }

        [Fact]
        public void Guard4_allows_a_developer_machine_with_no_instance_but_records_that_nothing_executed()
        {
            var requirement = SqlEvidenceGuards.RequireExecution(
                connectionConfigured: false, fixtureAvailable: false, skipReason: "variable unset", strict: false);

            Assert.True(requirement.Satisfied);
            Assert.False(requirement.Executed);   // satisfied is NOT the same as executed
        }

        [Fact]
        public void Guard4_reports_execution_when_the_fixture_is_available()
        {
            var requirement = SqlEvidenceGuards.RequireExecution(
                connectionConfigured: true, fixtureAvailable: true, skipReason: null, strict: true);

            Assert.True(requirement.Satisfied);
            Assert.True(requirement.Executed);
        }

        [Fact]
        public void Guard4_applied_to_THIS_run()
        {
            var requirement = SqlEvidenceGuards.RequireExecution(
                ConnectionConfigured, _sql.Available, _sql.SkipReason, StrictMode);

            Assert.True(requirement.Satisfied, requirement.Failure);
        }

        // =============================================================================================
        // GUARD 5 — execution-status verification (manifest -> code, against the DECLARED class)
        // =============================================================================================

        [Fact]
        public void Guard5_every_manifest_declared_SQL_test_exists_on_the_class_that_declares_it()
        {
            var manifest = SqlEvidenceGuards.LoadManifest(SqlEvidenceGuards.RepoRoot());
            var discovered = SqlEvidenceGuards.DiscoverTests(typeof(SqlCompanionGuardTests).Assembly, SqlNamespace);

            var verification = SqlEvidenceGuards.VerifyManifest(manifest, discovered, SqlNamespace);

            Assert.True(verification.DeclaredButMissing.Count == 0,
                "the manifest names SQL evidence tests that do not exist on the class it declares them under. " +
                "Either the test was renamed or moved without updating the manifest, or it was deleted:\n  " +
                string.Join("\n  ", verification.DeclaredButMissing));
        }

        [Fact]
        public void Guard5_FAILS_when_a_declared_test_is_missing_from_its_class()
        {
            // Proof of failability, and proof that the check is CLASS-scoped. The pre-existing Batch 00 guard matched
            // a declared name against methods on ANY type, so moving a test between classes passed silently.
            var manifest = new SqlEvidenceGuards.ManifestModel();
            manifest.Declared[SqlNamespace + "GhostTests"] = new HashSet<string> { "A_test_that_moved_away" };

            var discovered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                // same test NAME, different class — must not satisfy the declaration
                [SqlNamespace + "SomewhereElseTests"] = new HashSet<string> { "A_test_that_moved_away" },
            };

            var verification = SqlEvidenceGuards.VerifyManifest(manifest, discovered, SqlNamespace);

            Assert.Contains(
                verification.DeclaredButMissing,
                m => m.Contains("GhostTests.A_test_that_moved_away"));
        }

        // =============================================================================================
        // GUARD 6 — manifest enforcement INSIDE the SQL evidence (code -> manifest)
        // =============================================================================================

        [Fact]
        public void Guard6_every_SQL_evidence_test_in_the_assembly_is_registered_in_the_manifest()
        {
            var manifest = SqlEvidenceGuards.LoadManifest(SqlEvidenceGuards.RepoRoot());
            var discovered = SqlEvidenceGuards.DiscoverTests(typeof(SqlCompanionGuardTests).Assembly, SqlNamespace);

            var verification = SqlEvidenceGuards.VerifyManifest(manifest, discovered, SqlNamespace);

            Assert.True(verification.DiscoveredButUnregistered.Count == 0,
                $"{verification.DiscoveredButUnregistered.Count} SQL evidence tests exist that the manifest does not " +
                "register. Unregistered evidence is evidence nobody notices the loss of — add them to " +
                "engineering/required-evidence-manifest.json:\n  " +
                string.Join("\n  ", verification.DiscoveredButUnregistered));
        }

        [Fact]
        public void Guard6_FAILS_when_an_unregistered_SQL_test_appears()
        {
            var manifest = new SqlEvidenceGuards.ManifestModel();
            manifest.Declared[SqlNamespace + "KnownTests"] = new HashSet<string> { "Registered" };

            var discovered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                [SqlNamespace + "KnownTests"] = new HashSet<string> { "Registered", "Added_without_registering" },
            };

            var verification = SqlEvidenceGuards.VerifyManifest(manifest, discovered, SqlNamespace);

            Assert.Equal(
                new[] { SqlNamespace + "KnownTests.Added_without_registering" },
                verification.DiscoveredButUnregistered);
        }

        [Fact]
        public void Guard6_recognizes_SkippableFact_as_a_test_or_it_would_see_almost_no_SQL_evidence()
        {
            // Nearly every SQL proof is [SkippableFact]. A discovery routine that only knew [Fact] would report
            // "0 unregistered" while checking almost nothing — a guard that passes by looking away.
            var discovered = SqlEvidenceGuards.DiscoverTests(typeof(SqlCompanionGuardTests).Assembly, SqlNamespace);

            Assert.Contains(SqlNamespace + "Stage1F4DatabaseProofTests", discovered.Keys);
            Assert.Contains(
                "Updlock_serialises_two_readers_of_the_same_row",
                discovered[SqlNamespace + "Stage1F4DatabaseProofTests"]);
        }

        // =============================================================================================
        // GUARD 7 — SQL evidence summary output
        // =============================================================================================

        [Fact]
        public async Task Guard7_writes_the_SQL_evidence_summary()
        {
            var repoRoot = SqlEvidenceGuards.RepoRoot();
            var manifest = SqlEvidenceGuards.LoadManifest(repoRoot);
            var discovered = SqlEvidenceGuards.DiscoverTests(typeof(SqlCompanionGuardTests).Assembly, SqlNamespace);
            var verification = SqlEvidenceGuards.VerifyManifest(manifest, discovered, SqlNamespace);

            var onServer = new List<string>();
            var accountedFor = new List<string>();
            string? fingerprint = null;

            if (_sql.Available)
            {
                var master = new SqlConnectionStringBuilder(_sql.TestConnectionString) { InitialCatalog = "master" };
                onServer = await SqlEvidenceGuards.OwnedDatabasesOnServerAsync(master.ConnectionString);
                accountedFor.Add(new SqlConnectionStringBuilder(_sql.TestConnectionString).InitialCatalog);
                accountedFor.AddRange(await _sql.ListOwnedProbeDatabasesAsync());
                fingerprint = await _sql.SharedFixtureFingerprintAsync();
            }

            var raw = Environment.GetEnvironmentVariable(SqlServerFixture.ConnectionStringVariable);

            var summary = new SqlEvidenceGuards.SqlEvidenceSummary
            {
                ConnectionConfigured = ConnectionConfigured,
                FixtureAvailable = _sql.Available,
                SkipReason = _sql.SkipReason,
                StrictMode = StrictMode,
                CatalogInspected = ConnectionConfigured ? SqlEvidenceGuards.InspectCatalog(raw).Catalog : null,
                DeclaredTests = manifest.Declared
                    .Where(kv => kv.Key.StartsWith(SqlNamespace, StringComparison.Ordinal))
                    .Sum(kv => kv.Value.Count),
                DiscoveredTests = discovered.Sum(kv => kv.Value.Count),
                DeclaredButMissing = verification.DeclaredButMissing.Count,
                Unregistered = verification.DiscoveredButUnregistered.Count,
                OwnedDatabasesOnServer = onServer,
                Leftovers = SqlEvidenceGuards.Leftovers(onServer, accountedFor).ToList(),
                SharedFixtureFingerprint = fingerprint,
            };

            var directory = Path.Combine(repoRoot, "docs", "architecture", "evidence");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "SQL-Evidence-Summary.md");
            File.WriteAllText(path, SqlEvidenceGuards.RenderSummary(summary));

            Assert.True(File.Exists(path));

            var text = File.ReadAllText(path);
            Assert.Contains("# SQL Evidence Summary", text);
            Assert.Contains("## Verdict", text);
        }

        [Fact]
        public void Guard7_says_NOT_EXECUTED_loudly_when_nothing_ran()
        {
            // The Stage 1 defect was not that tests skipped — it was that the output gave no sign of it. A summary
            // that reads the same whether or not the evidence ran is worse than no summary.
            var text = SqlEvidenceGuards.RenderSummary(new SqlEvidenceGuards.SqlEvidenceSummary
            {
                ConnectionConfigured = false,
                FixtureAvailable = false,
                SkipReason = "variable unset",
            });

            Assert.Contains("**NOT EXECUTED**", text);
            Assert.Contains("may be cited as SQL coverage", text);
        }

        [Fact]
        public void Guard7_says_EXECUTED_only_when_the_evidence_actually_ran()
        {
            var text = SqlEvidenceGuards.RenderSummary(new SqlEvidenceGuards.SqlEvidenceSummary
            {
                ConnectionConfigured = true,
                FixtureAvailable = true,
            });

            Assert.Contains("**EXECUTED**", text);
            Assert.DoesNotContain("**NOT EXECUTED**", text);
        }

        [Fact]
        public void Guard7_lists_leftovers_by_name_so_the_fix_is_actionable()
        {
            var text = SqlEvidenceGuards.RenderSummary(new SqlEvidenceGuards.SqlEvidenceSummary
            {
                ConnectionConfigured = true,
                FixtureAvailable = true,
                Leftovers = new List<string> { "CrossBuyProbe_F4_orphan" },
            });

            Assert.Contains("### Leftovers", text);
            Assert.Contains("CrossBuyProbe_F4_orphan", text);
        }
    }
}
