using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Platform Kernel slice 2 (ADR-007) — SQL Server integration fixture.
    //
    // WHY THIS EXISTS: SqlEventDispatchStore's claiming statement relies on SQL Server semantics —
    // UPDLOCK + READPAST + OUTPUT in a single UPDATE. SQLite has none of that, so the in-process suite can
    // only prove single-ownership sequentially. These tests exercise the REAL statement under genuine
    // parallelism.
    //
    // SAFETY, non-negotiable:
    //   * the tests run ONLY when CROSSBUY_TEST_SQL is set; otherwise every one reports SKIPPED, never
    //     silently passes, and never falls back to another database;
    //   * the connection string must point at a SQL Server INSTANCE, not at a database to use. The fixture
    //     creates its OWN database named CrossBuyPlatformTest_<guid> per run and drops it afterwards, so it
    //     cannot touch CrossBuyDB2 or any production data;
    //   * a connection string whose Initial Catalog looks like a real CrossBuy database is REFUSED outright.
    //
    // Schema comes from the real deployment script (deploy/sql/platform_business_events.sql), so the tests
    // run against the production DDL — including the filtered dedup index and the claiming index — rather
    // than an EF-generated approximation.
    public sealed class SqlServerFixture : IAsyncLifetime
    {
        public const string ConnectionStringVariable = "CROSSBUY_TEST_SQL";

        // Databases the fixture must never be pointed at, even by accident. Projected from the guard so there is
        // exactly ONE list; an existing test reads this field by reflection, and it must not be able to disagree
        // with the list the refusal actually consults.
        private static readonly string[] ForbiddenCatalogs = SqlEvidenceGuards.ProductionCatalogs;

        public string? SkipReason { get; private set; }
        public string TestConnectionString { get; private set; } = "";
        private string _databaseName = "";
        private string _masterConnectionString = "";

        // Manifest identity for the fixture's OWN database. Probes carry their family and a generated
        // operation id; the fixture creates exactly one database per run, so a constant is honest here.
        private const string FixtureFamily = "PlatformFixture";
        private const string FixtureOperation = "fixture";

        public bool Available => SkipReason == null;

        public async Task InitializeAsync()
        {
            // B2: bind the fixture's scope to company 1 before any context is created. See the Holder comment.
            Holder.Set(1, null);

            var raw = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                SkipReason =
                    $"SQL Server integration tests need the {ConnectionStringVariable} environment variable — " +
                    "a connection string to a SQL Server INSTANCE the tests may create a scratch database on, " +
                    @"e.g. ""Server=.;Trusted_Connection=True;TrustServerCertificate=True"". " +
                    "They are skipped rather than run against another database.";
                return;
            }

            // Batch 00-C guard 1: production-catalog refusal is decided by SqlEvidenceGuards — ONE implementation,
            // exercised directly by tests that hand it a production catalog and assert refusal. The previous
            // arrangement was verified only by a test that read the ForbiddenCatalogs array by reflection, which
            // proves a list exists and proves nothing refuses anything.
            var verdict = SqlEvidenceGuards.InspectCatalog(raw);
            if (verdict.Refused) { SkipReason = $"{ConnectionStringVariable}: {verdict.Reason}"; return; }

            SqlConnectionStringBuilder builder;
            try { builder = new SqlConnectionStringBuilder(raw); }
            catch (Exception ex) { SkipReason = $"{ConnectionStringVariable} is not a valid connection string: {ex.Message}"; return; }

            _databaseName = "CrossBuyPlatformTest_" + Guid.NewGuid().ToString("N");
            builder.InitialCatalog = "master";
            _masterConnectionString = builder.ConnectionString;
            builder.InitialCatalog = _databaseName;
            TestConnectionString = builder.ConnectionString;

            // FAIL CLOSED, AND DELIBERATELY OUTSIDE THE try. If the intent to create cannot be written
            // down durably, this run must not create a database it would then have no record of - that is
            // precisely how 295 databases appeared on 2026-10-21 with nothing on disk to attribute them to.
            // Throwing from a collection fixture's InitializeAsync fails the collection loudly; swallowing
            // it into SkipReason would turn an evidence failure into a quiet skip.
            SqlScratchManifest.CreateIntent(_databaseName, FixtureFamily, SqlScratchManifest.RoleFixture, FixtureOperation);

            try
            {
                await ExecuteOnMasterAsync($"CREATE DATABASE [{_databaseName}];");
                SqlScratchManifest.Created(_databaseName, FixtureFamily, SqlScratchManifest.RoleFixture, FixtureOperation);

                SqlScratchManifest.SchemaBegin(_databaseName, FixtureOperation);
                await RunDeploymentScriptAsync();
                SqlScratchManifest.SchemaComplete(_databaseName, FixtureOperation);
            }
            catch (Exception ex)
            {
                // The database may or may not exist at this point; the manifest already distinguishes the
                // two, because Created is only written once CREATE DATABASE returned without error.
                SqlScratchManifest.Failed(_databaseName, FixtureOperation, ex);
                SkipReason = $"Could not prepare the SQL Server test database: {ex.Message}";
                await SafeDropAsync();
            }
        }

        // THE REPORTING POINT. A collection fixture's DisposeAsync is the one place xUnit guarantees runs
        // after every class in the collection - demonstrated, not assumed. A throw here is surfaced as
        // "[Test Collection Cleanup Failure]" and fails the run with a non-zero exit, while the individual
        // test results are still reported: the cleanup error is ADDED to the output, it does not replace it.
        //
        // TWO KINDS OF LEFTOVER, ONE REPORT. A probe held by an unreleased lease and the fixture's own
        // database are both scratch databases this run failed to reclaim, and either one on its own is
        // enough to make a green result untrue.
        //
        // THEY ARE COLLECTED, NOT THROWN AT THE FIRST SIGHT. Reporting the fixture failure and returning
        // would hide outstanding leases behind it, and reporting leases first would hide the fixture
        // failure. One exception carries both, so neither can mask the other.
        public async Task DisposeAsync()
        {
            await SafeDropAsync();   // records into _fixtureCleanupError; never throws

            var problems = new List<string>();

            if (_fixtureCleanupError != null)
                problems.Add($"  [FixtureDropFailed] {_databaseName} - {_fixtureCleanupError}");

            problems.AddRange(OutstandingLeases.Select(r =>
                $"  [{r.Status}] {r.DatabaseName} (lease {r.LeaseId}, family {r.Family}, created {r.CreatedUtc:O})" +
                (r.CleanupError is null ? string.Empty : " - " + r.CleanupError)));

            // A DEGRADED MANIFEST IS A THIRD KIND OF PROBLEM, and it is reported rather than suppressed.
            // An event that could not be written is a database this run may not be able to account for
            // afterwards, which is the entire failure mode the manifest exists to close.
            problems.AddRange(SqlScratchManifest.Failures.Select(f => $"  [ManifestDegraded] {f}"));

            // RUN ENDED IS WRITTEN BEFORE THE THROW, and before the early return, so it marks every run that
            // reached teardown - including a failing one. ITS ABSENCE IS THE SIGNAL: a manifest with no
            // RunEnded is a run that was killed, which is how 295 of the 300 leftovers were produced.
            SqlScratchManifest.RunEnded(problems.Count);

            if (problems.Count == 0) return;

            // Names and recorded errors only. A connection string never appears here.
            throw new InvalidOperationException(
                "this run left scratch databases behind:" +
                Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        // ---- Stage 1 F4: the FULL EF schema in the scratch database ----
        //
        // WHY THIS EXISTS, and why hand-written DDL was the wrong mechanism.
        //
        // `TaskScopeQuerySqlTests` (Batch C.1) hand-authored a `TaskItems` table with the 9 columns its assertions
        // touched. The entity has ~28. Those tests were gated on CROSSBUY_TEST_SQL, the variable was never set, so
        // they reported SKIPPED for two batches and the defect stayed invisible — the first real execution failed
        // with twenty `Invalid column name` errors. That is the exact hazard of proving database behaviour against a
        // schema written by hand to match the test: the test agrees with itself.
        //
        // F4 needs stock, journal, payroll and event tables together, with their real FKs, so hand-written DDL would
        // repeat that mistake at ten times the scale. `EnsureCreatedAsync` derives every table from the SAME EF model
        // production uses — including the (19,4) decimal precision pins — so a column the model has and the test
        // forgot cannot exist.
        //
        // It is additive: the platform deployment scripts have already run, so this creates only what they did not.
        // Idempotent, and safe to call from many test classes.
        private bool _efSchemaCreated;
        private readonly SemaphoreSlim _efSchemaLock = new(1, 1);

        public async Task EnsureEfSchemaAsync()
        {
            if (_efSchemaCreated) return;
            await _efSchemaLock.WaitAsync();
            try
            {
                if (_efSchemaCreated) return;
                using var db = NewContext();

                // NOT EnsureCreatedAsync: it is all-or-nothing and does NOTHING when the database already has
                // tables — and this one does, because the platform deployment scripts ran at fixture start-up. The
                // first attempt used it and every table was silently absent ("Invalid object name 'dbo.TaskItems'"),
                // which is the same class of quiet no-op as the hand-written DDL it replaced.
                //
                // GenerateCreateScript() emits the model's own DDL, which is then applied statement by statement so
                // objects the deployment scripts already created can be skipped individually rather than aborting
                // the batch. Errors that mean "it already exists" are tolerated; anything else is thrown, so a real
                // mapping failure still fails loudly.
                // FINDING, recorded because it is not obvious: applying the model's own DDL to SQL Server FAILS on
                // `FK_Branches_CountriesLookup_CountryID` — "may cause cycles or multiple cascade paths". The EF model
                // therefore contains a cascade graph SQL Server will not accept, and nobody had noticed because
                // production's schema is hand-written idempotent SQL (never EF migrations), and the unit tests run on
                // SQLite, which does not enforce that restriction. It is NOT a production defect — no production
                // database was ever created from this script — but it does mean the model's relational configuration
                // is unvalidated against the real engine. Logged as Stage 2 technical debt.
                //
                // F4 proves LOCKING, TRANSACTION and ROLLBACK behaviour, which does not depend on the lookup-table FK
                // graph, so the inline FOREIGN KEY clauses are stripped and the tables, columns, types, precisions and
                // indexes still come from the model. Declared as a limitation rather than quietly worked around.
                var script = StripForeignKeys(db.Database.GenerateCreateScript());

                await using var connection = new SqlConnection(TestConnectionString);
                await connection.OpenAsync();

                foreach (var batch in SplitScript(script))
                {
                    try
                    {
                        await using var cmd = new SqlCommand(batch, connection) { CommandTimeout = 300 };
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex) when (IsAlreadyExists(ex))
                    {
                        // the deployment scripts own this object; theirs wins
                    }
                }
                _efSchemaCreated = true;
            }
            finally { _efSchemaLock.Release(); }
        }

        // Removes inline `CONSTRAINT [FK_…] FOREIGN KEY … [ON DELETE …]` clauses and standalone ALTER TABLE ADD
        // CONSTRAINT … FOREIGN KEY statements. Everything else — columns, types, precisions, primary keys, indexes —
        // is left exactly as the model emitted it.
        // =================================================================================================
        // D-5 — LINE-BASED, AND THE SECOND OF TWO COPIES.
        //
        // THIS LOGIC EXISTED TWICE, IDENTICALLY, and that is the reason the first repair looked complete and
        // was not: UatSqlProbeFixture carried a byte-identical copy of the broken expression, fixing it made
        // its own guard pass, and every SqlServer.* suite kept failing because it builds probe databases
        // through THIS fixture instead. Duplicated logic fails in duplicate.
        //
        // THE DEFECT. `FOREIGN KEY[^,\r\n]*` stops at the FIRST COMMA, which is correct for a single-column
        // key and wrong for a composite one. Several Construction entities declare
        // `HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID })`, so EF emits:
        //
        //   CONSTRAINT [FK_..._ParentId_CompanyID_Category] FOREIGN KEY ([ParentId], [CompanyID], [Category])
        //       REFERENCES [ConstructionCostCodes] ([ID], [CompanyID], [Category]) ON DELETE NO ACTION
        //
        // The expression consumed only as far as the first comma inside the REFERENCES list, leaving
        // `, [CompanyID], [Category]) ON DELETE NO ACTION` to attach itself to the PRECEDING line — an
        // alternate-key UNIQUE constraint. SQL Server then rejects the batch with "The definition for column
        // 'CompanyID' must include a data type" and "Incorrect syntax near 'ACTION'". Measured: 27 tables.
        //
        // WHY LINES. All 157 inline `CONSTRAINT [FK_...]` entries in the current model occupy exactly one
        // line each — measured, not assumed. A line is a complete, self-delimiting unit; an expression
        // spanning commas and parentheses is not. ProbeSchemaGeneratorDiagnosticTests asserts the resulting
        // invariant so the next model change that outruns this cannot do so silently.
        // =================================================================================================
        private static string StripForeignKeys(string script)
        {
            var lines = script.Replace("\r\n", "\n").Split('\n');
            var kept = new System.Collections.Generic.List<string>(lines.Length);

            foreach (var line in lines)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        line, @"^\s*CONSTRAINT \[FK_[^\]]+\] FOREIGN KEY\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;

                kept.Add(line);
            }

            // Removing the LAST item of a CREATE TABLE body leaves the line above it ending in a comma
            // immediately before the closing parenthesis, which is itself a syntax error.
            for (var i = 0; i < kept.Count - 1; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(kept[i + 1], @"^\s*\)\s*;?\s*$")
                    && kept[i].TrimEnd().EndsWith(","))
                    kept[i] = kept[i].TrimEnd().TrimEnd(',');
            }

            var joined = string.Join(System.Environment.NewLine, kept);

            // Standalone statements are genuinely statement-shaped and semicolon-terminated, so a regular
            // expression is the right tool for them.
            return System.Text.RegularExpressions.Regex.Replace(
                joined,
                @"ALTER TABLE[^;]*ADD CONSTRAINT \[FK_[^\]]+\][^;]*;",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        // ---- Stage 2A: ISOLATED PROBE DATABASES (RISK-036) ----
        //
        // A test family that GENERATES SCHEMA must never touch the shared platform fixture. Doing so broke three
        // PlatformSchemaDeploymentTests twice — once from IMP-003 and once from Batch P — because those tests
        // legitimately assert the platform scripts create no table outside the kernel and its outbox.
        //
        // This is the shared, verified implementation both families now use, so the isolation rule is enforced by
        // one helper rather than remembered separately in each class. It reuses the same model-derived script and
        // FK-stripping already proven by the F4 fixture work.
        public sealed record ProbeDatabase(string Name, string ConnectionString);

        public async Task<ProbeDatabase> CreateProbeDatabaseAsync(string prefix, bool applyModelSchema = true)
        {
            // OWNERSHIP MARKER: every probe this fixture creates carries the CrossBuyProbe_ prefix, so cleanup can
            // identify what it owns and can never drop a database it did not create.
            // Name is built, not sliced. The first version truncated with a computed length that could exceed the
            // string and threw ArgumentOutOfRangeException — a clever one-liner where plain construction was correct.
            var name = "CrossBuyProbe_" + prefix + "_" + Guid.NewGuid().ToString("N");
            var master = new SqlConnectionStringBuilder(TestConnectionString) { InitialCatalog = "master" };
            var mine = new SqlConnectionStringBuilder(TestConnectionString) { InitialCatalog = name };

            // ONE OPERATION ID PER CREATE, allocated here rather than reusing the lease sequence: a probe
            // can be created without a lease, and the lease id does not exist yet on the path that has one.
            // The DATABASE NAME is the manifest's correlation key - it carries a GUID and is unique across
            // every run on the instance - and a later Leased event binds a lease id to that name.
            var operationId = "probe:" + System.Threading.Interlocked.Increment(ref _operationSequence);

            // FAIL CLOSED before CREATE, for the same reason the fixture's own database does.
            SqlScratchManifest.CreateIntent(name, prefix, SqlScratchManifest.RoleProbe, operationId);

            await using (var m = new SqlConnection(master.ConnectionString))
            {
                await m.OpenAsync();
                await using var create = new SqlCommand($"CREATE DATABASE [{name}];", m) { CommandTimeout = 300 };
                await create.ExecuteNonQueryAsync();
            }

            // CREATE DATABASE returned without error: SQL has confirmed the catalog exists. An intent with
            // no Created after it is the signature of a create that failed or was interrupted mid-flight,
            // and orphan detection must treat those two cases differently from a completed create.
            SqlScratchManifest.Created(name, prefix, SqlScratchManifest.RoleProbe, operationId);

            var probe = new ProbeDatabase(name, mine.ConnectionString);
            if (!applyModelSchema) return probe;

            await ApplyModelSchemaAsync(probe);
            return probe;
        }

        // EXTRACTED, NOT COPIED. The lease path needs the same schema application, and this file already
        // records what duplicated logic costs: "Duplicated logic fails in duplicate" (D-5). Behaviour is
        // byte-for-byte what CreateProbeDatabaseAsync did inline; only the call site moved.
        private async Task ApplyModelSchemaAsync(ProbeDatabase probe)
        {
            // SCHEMA DEPLOYMENT IS BRACKETED because it is the window the lease comment already names: the
            // database exists, so a failure here leaves a real catalog behind. A SchemaBegin with no
            // SchemaComplete tells orphan detection the database was live and partially built when the run
            // stopped - which is exactly what cannot be reconstructed from the catalog alone.
            SqlScratchManifest.SchemaBegin(probe.Name, null);
            try
            {
                using var db = ContextFor(probe);
                var script = StripForeignKeys(db.Database.GenerateCreateScript());

                await using var c = new SqlConnection(probe.ConnectionString);
                await c.OpenAsync();
                foreach (var batch in SplitScript(script))
                {
                    try
                    {
                        await using var cmd = new SqlCommand(batch, c) { CommandTimeout = 300 };
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex) when (IsAlreadyExists(ex) || ex.Number is 1785 or 1767 or 1088)
                    {
                        // 1785/1767/1088 are the known cascade-path chain from IMP-003 — measured, classified and not
                        // this suite's subject. Anything else still throws.
                    }
                }
            }
            catch (Exception ex)
            {
                // RECORDED AND RETHROWN. The caller's behaviour is unchanged - LeaseProbeDatabaseAsync still
                // disposes the lease and wraps the original error - the manifest merely gains the fact.
                SqlScratchManifest.Failed(probe.Name, null, ex);
                throw;
            }

            SqlScratchManifest.SchemaComplete(probe.Name, null);
        }

        // Drops a probe and CONFIRMS removal. A green run with a leftover probe is not acceptance.
        // leaseId is OPTIONAL and additive: existing callers compile and behave exactly as before. It exists
        // so the manifest can attribute a drop to the lease that owned the database rather than to nothing.
        public async Task DropProbeDatabaseAsync(ProbeDatabase probe, string? leaseId = null)
        {
            if (probe is null || string.IsNullOrEmpty(probe.Name)) return;

            // Batch 00-C guard 2: ownership is decided by SqlEvidenceGuards, not re-implemented here. The rule is
            // asserted directly by a test that passes it a foreign name, so deleting this call breaks a test rather
            // than silently widening what cleanup may drop.
            SqlEvidenceGuards.AssertOwnedScratchName(probe.Name);

            // DROP INTENT GOES DOWN FIRST. A process killed between the intent and the confirmation leaves a
            // DropIntent with no Released, which is a database that MAY still be on the instance - the one
            // state the in-memory lease history could never survive to report.
            SqlScratchManifest.DropIntent(probe.Name, leaseId);

            var master = new SqlConnectionStringBuilder(TestConnectionString) { InitialCatalog = "master" };
            await using var m = new SqlConnection(master.ConnectionString);
            await m.OpenAsync();

            int still;
            try
            {
                await using (var drop = new SqlCommand(
                    $"IF DB_ID(@n) IS NOT NULL BEGIN ALTER DATABASE [{probe.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{probe.Name}]; END", m)
                { CommandTimeout = 300 })
                {
                    drop.Parameters.AddWithValue("@n", probe.Name);
                    await drop.ExecuteNonQueryAsync();
                }

                await using var check = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n;", m);
                check.Parameters.AddWithValue("@n", probe.Name);
                still = Convert.ToInt32(await check.ExecuteScalarAsync());
            }
            catch (Exception ex)
            {
                SqlScratchManifest.Failed(probe.Name, leaseId, ex);
                throw;
            }

            // RELEASED IS ONLY WRITTEN ON A CONFIRMED REMOVAL. "The DROP statement did not error" is not the
            // same claim as "the database is gone", and the manifest records the second one or neither.
            if (still != 0)
            {
                SqlScratchManifest.CleanupFailed(probe.Name, leaseId, "the database still exists after DROP");
                throw new InvalidOperationException($"probe database '{probe.Name}' was not removed — cleanup failed");
            }

            SqlScratchManifest.Released(probe.Name, leaseId);
        }

        // =================================================================================================
        // PROBE LEASES - a LIFETIME for a scratch database, where ProbeDatabase is only a VALUE.
        //
        // 300 scratch databases accumulated on this instance because releasing one was a thing a caller had
        // to REMEMBER. A lease makes release the compiler's business (`await using`) and makes a forgotten
        // or failed release VISIBLE instead of silent.
        //
        // WHY DisposeAsync NEVER THROWS. It runs during exception unwind inside `await using`, so throwing
        // there REPLACES the assertion that actually failed the test - the cleanup error wins and the real
        // defect disappears. It records instead, and the fixture reports at teardown.
        //
        // WHY TEARDOWN IS THE REPORTING POINT, and not a guard test. Measured, not assumed: the 51 classes
        // in this collection are discovered alphabetically, SqlCompanionGuardTests is #48, and
        // Stage1F4DatabaseProofTests (#49) and TaskScopeQuerySqlTests (#50) BOTH create probes after it.
        // A guard test would pass and then those two would leak - the F4 and TaskScope families are in fact
        // among the leftovers on the instance today. A collection fixture's DisposeAsync is the only point
        // xUnit guarantees runs after every class in the collection.
        // =================================================================================================

        public enum ProbeCleanupStatus { Leased, Released, Failed }

        /// <summary>A point-in-time record of one lease. History keeps these; it never removes them.</summary>
        public sealed record ProbeLeaseRecord(
            string LeaseId, string DatabaseName, string Family,
            DateTime CreatedUtc, ProbeCleanupStatus Status, string? CleanupError);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeLease> _leases = new();
        private int _leaseSequence;
        private int _operationSequence;   // manifest create operations; NOT the lease sequence - see CreateProbeDatabaseAsync

        /// <summary>Every lease this run created, in creation order. Released ones are KEPT.</summary>
        public IReadOnlyList<ProbeLeaseRecord> LeaseHistory =>
            _leases.Values.OrderBy(l => l.CreatedUtc).ThenBy(l => l.LeaseId, StringComparer.Ordinal)
                   .Select(l => l.ToRecord()).ToList();

        /// <summary>Leases still holding a database - not yet disposed.</summary>
        public IReadOnlyList<ProbeLeaseRecord> ActiveLeases =>
            LeaseHistory.Where(r => r.Status == ProbeCleanupStatus.Leased).ToList();

        /// <summary>Anything that must not be true at the end of a run: still held, or failed to release.</summary>
        public IReadOnlyList<ProbeLeaseRecord> OutstandingLeases =>
            LeaseHistory.Where(r => r.Status != ProbeCleanupStatus.Released).ToList();

        /// <summary>
        /// Creates a probe database and returns a LEASE that owns it. Prefer this over
        /// CreateProbeDatabaseAsync: `await using` releases it on every exception and cancellation path.
        /// </summary>
        public async Task<ProbeLease> LeaseProbeDatabaseAsync(string family, bool applyModelSchema = true)
        {
            // CREATED WITHOUT SCHEMA FIRST, so the name exists and the lease is REGISTERED before anything
            // that can fail. A schema error after a successful CREATE DATABASE is exactly how a probe is
            // orphaned with nobody holding its name.
            var probe = await CreateProbeDatabaseAsync(family, applyModelSchema: false);

            var lease = new ProbeLease(this, probe, family,
                $"{family}:{System.Threading.Interlocked.Increment(ref _leaseSequence)}");
            _leases[lease.LeaseId] = lease;

            // BINDS THE LEASE TO THE NAME. The create event could not carry a lease id because the lease did
            // not exist yet, and reordering the two to make it possible would change when the lease sequence
            // advances. A separate event costs one line in the manifest and changes no behaviour at all.
            SqlScratchManifest.Leased(probe.Name, family, lease.LeaseId);

            if (!applyModelSchema) return lease;

            try
            {
                await ApplyModelSchemaAsync(probe);
            }
            catch (Exception schemaError)
            {
                // THE ORIGINAL ERROR WINS. The lease is disposed to reclaim the database, and because
                // DisposeAsync records rather than throws, a secondary cleanup failure cannot replace the
                // schema error - it is kept on the lease and reported at teardown alongside it.
                await lease.DisposeAsync();
                throw new InvalidOperationException(
                    $"probe schema application failed for '{probe.Name}' (lease {lease.LeaseId}); " +
                    $"cleanup status {lease.Status}", schemaError);
            }

            return lease;
        }

        /// <summary>
        /// A lifetime for exactly ONE probe database. The name is captured at construction and is the only
        /// thing this lease will ever drop: no caller can redirect it by passing a different value.
        /// </summary>
        public sealed class ProbeLease : IAsyncDisposable
        {
            private readonly SqlServerFixture _fixture;
            private readonly string _name;          // immutable capture - the whole ownership guarantee
            private readonly string _connectionString;
            private int _disposed;

            internal ProbeLease(SqlServerFixture fixture, ProbeDatabase database, string family, string leaseId)
            {
                _fixture = fixture;
                _name = database.Name;
                _connectionString = database.ConnectionString;
                Database = database;
                Family = family;
                LeaseId = leaseId;
                CreatedUtc = DateTime.UtcNow;
            }

            public ProbeDatabase Database { get; }
            public string LeaseId { get; }
            public string Family { get; }
            public DateTime CreatedUtc { get; }
            public ProbeCleanupStatus Status { get; private set; } = ProbeCleanupStatus.Leased;
            public Exception? CleanupError { get; private set; }

            internal ProbeLeaseRecord ToRecord() =>
                new(LeaseId, _name, Family, CreatedUtc, Status,
                    CleanupError is null ? null : CleanupError.GetType().Name + ": " + CleanupError.Message);

            public async ValueTask DisposeAsync()
            {
                // IDEMPOTENT. A second disposal issues no second DROP and throws nothing.
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;

                try
                {
                    // Ownership is re-asserted here as well as inside DropProbeDatabaseAsync: this lease drops
                    // the name it captured, never one handed to it.
                    SqlEvidenceGuards.AssertOwnedScratchName(_name);
                    await _fixture.DropProbeDatabaseAsync(new ProbeDatabase(_name, _connectionString), LeaseId);
                    Status = ProbeCleanupStatus.Released;
                    CleanupError = null;
                }
                catch (Exception ex)
                {
                    // RECORDED, NEVER THROWN - see the block comment above.
                    Status = ProbeCleanupStatus.Failed;
                    CleanupError = ex;
                }
            }
        }

        public CrossDbContext ContextFor(ProbeDatabase probe, int companyId = 1)
        {
            var holder = new CrossBuy.BL.Platform.CompanyScopeHolder();
            holder.Set(companyId, null);
            return new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlServer(probe.ConnectionString)
                .EnableSensitiveDataLogging()
                .Options, holder);
        }

        // How many probes this fixture owns are still on the instance. Used by cleanup assertions.
        public async Task<List<string>> ListOwnedProbeDatabasesAsync()
        {
            var master = new SqlConnectionStringBuilder(TestConnectionString) { InitialCatalog = "master" };
            var names = new List<string>();
            await using var m = new SqlConnection(master.ConnectionString);
            await m.OpenAsync();
            await using var cmd = new SqlCommand(
                // LITERAL UNDERSCORES, AND ALL THREE OWNED FAMILIES.
                //
                // '_' is a SINGLE-CHARACTER WILDCARD in T-SQL, so 'CrossBuyProbe_%' also matched
                // CrossBuyProbeXrogue - a name this suite never creates and must never report as its own.
                // [_] matches the character itself. Measured on the live instance: the unescaped pattern
                // matches a literal CrossBuyProbeXrogue, the escaped one does not.
                //
                // AND IT LISTED ONE PREFIX OF THREE. CrossBuyPlatformTest_ and CrossBuyImp003_ are equally
                // owned by this suite (SqlEvidenceGuards.OwnedScratchPrefixes), so a fixture database left
                // behind by an interrupted run was invisible here while sitting on the server.
                "SELECT name FROM sys.databases WHERE name LIKE 'CrossBuyPlatformTest[_]%' "
                + "OR name LIKE 'CrossBuyProbe[_]%' OR name LIKE 'CrossBuyImp003[_]%' ORDER BY name;", m);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
            return names;
        }

        // A fingerprint of the SHARED fixture's objects, so a test family can prove it added nothing.
        public async Task<string> SharedFixtureFingerprintAsync()
        {
            await using var c = new SqlConnection(TestConnectionString);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(@"
SELECT CONCAT(
  (SELECT COUNT(*) FROM sys.tables), '|',
  (SELECT COUNT(*) FROM sys.indexes), '|',
  (SELECT COUNT(*) FROM sys.foreign_keys), '|',
  (SELECT COUNT(*) FROM sys.schemas), '|',
  (SELECT COUNT(*) FROM sys.columns));", c);
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        }

        // 2714 = object already exists · 1779/1913 = a key/index of that name already exists ·
        // 2705 = duplicate column name in the object. All four mean the deployment scripts got there first.
        private static bool IsAlreadyExists(SqlException ex)
            => ex.Errors.Cast<SqlError>().All(e => e.Number is 2714 or 1779 or 1913 or 2705 or 15248);

        // GenerateCreateScript separates batches with GO on its own line in some providers and with plain
        // statement terminators in others; split on both and drop anything blank.
        private static IEnumerable<string> SplitScript(string script)
            => System.Text.RegularExpressions.Regex
                .Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .SelectMany(part => part.Split(new[] { ";\r\n\r\n", ";\n\n" }, StringSplitOptions.None))
                .Select(s => s.Trim().TrimEnd(';').Trim())
                .Where(s => s.Length > 0);

        // ---- helpers the tests use ----

        // Interceptors are accepted so a test can inject a COMMITTED change by another connection at an exact point
        // inside a production method — the only way to reproduce a read-then-write interleave deterministically
        // instead of hoping a parallel loop happens to hit it. Test-only; nothing in production registers one.
        // Stage 1 Batch B / B2 — the holder every context from this fixture is filtered by.
        //
        // One holder for the whole fixture, resolved to company 1: these tests are about dispatch concurrency and
        // retry races, not about isolation, and they all operate as one company. The alternative — leaving it
        // unresolved — is not "no filtering": the single-argument CrossDbContext constructor fails CLOSED, so every
        // BusinessEvents read would return nothing and twelve concurrency tests would pass while proving nothing.
        //
        // A test that needs to read across companies takes a bypass on this holder, exactly as production does.
        public CrossBuy.BL.Platform.CompanyScopeHolder Holder { get; } = new();

        public CrossDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
            => new(new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlServer(TestConnectionString)
                .AddInterceptors(interceptors)
                // B4: as in production — see PlatformTestHost.NewContext.
                .AddInterceptors(new CrossBuy.BL.Platform.CompanyWriteGuardInterceptor(
                    NullLogger<CrossBuy.BL.Platform.CompanyWriteGuardInterceptor>.Instance))
                .Options,
                Holder);

        // B2 — a context in ANOTHER company's scope, for the isolation assertions. Its own holder, as a second
        // request would have: production never mixes "a context naming company 2" with "a scope that is company 1".
        public CrossDbContext NewContextAsCompany(
            int companyId, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
        {
            var holder = new CrossBuy.BL.Platform.CompanyScopeHolder();
            holder.Set(companyId, null);
            return new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlServer(TestConnectionString)
                .AddInterceptors(interceptors)
                .AddInterceptors(new CrossBuy.BL.Platform.CompanyWriteGuardInterceptor(
                    NullLogger<CrossBuy.BL.Platform.CompanyWriteGuardInterceptor>.Instance))
                .Options,
                holder);
        }

        public IEventDispatchStore Store(CrossDbContext db, BusinessEventDispatchOptions? options = null)
            => new SqlEventDispatchStore(db, Options.Create(options ?? new BusinessEventDispatchOptions()));

        public IBusinessEventService Events(CrossDbContext db, BusinessContext? context = null)
            => new BusinessEventService(
                db,
                new EntityRegistry(db),
                new StubContextAccessor(context ?? PlatformTestHost.DefaultContext()),
                NullLogger<BusinessEventService>.Instance);

        // Stage 0 (Slice-003): prepares the email-outbox tables the same way a real deployment does —
        // the BASE table as it exists in production today, then the additive slice-003 script on top. Running the
        // real script (rather than an EF-generated approximation) is what proves the script itself works and is
        // idempotent against a real table.
        public async Task EnsureCommOutboxAsync()
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();

            // Base shape, matching Models/Context/Comm/CommMessage.cs BEFORE slice 003 (no ClaimedAt/UpdatedAt).
            await ExecuteAsync(connection, @"
IF OBJECT_ID('CommMessages','U') IS NULL
CREATE TABLE CommMessages (
    Id        INT IDENTITY(1,1) PRIMARY KEY,
    CompanyID INT NOT NULL,
    ToAddress NVARCHAR(400) NOT NULL,
    Cc        NVARCHAR(400) NULL,
    Subject   NVARCHAR(400) NOT NULL,
    Body      NVARCHAR(MAX) NULL,
    Status    NVARCHAR(20) NOT NULL DEFAULT 'Queued',
    Error     NVARCHAR(1000) NULL,
    SentAt    DATETIME2 NULL,
    Attempts  INT NOT NULL DEFAULT 0,
    ParentId  INT NULL,
    Kind      NVARCHAR(20) NOT NULL DEFAULT 'New',
    Starred   BIT NOT NULL DEFAULT 0,
    DeletedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NULL,
    CreatedBy INT NULL,
    UpdatedBy INT NULL
);
IF OBJECT_ID('CommAttachments','U') IS NULL
CREATE TABLE CommAttachments (
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    CommMessageId  INT NOT NULL,
    FilePath       NVARCHAR(400) NOT NULL,
    FileName       NVARCHAR(260) NOT NULL,
    Size           BIGINT NOT NULL DEFAULT 0,
    CreatedAt      DATETIME2 NULL
);");

            // Then the real additive script — twice, which is also the idempotency proof for it.
            await RunScriptAsync(connection, "comm_outbox_slice_003.sql");
            await RunScriptAsync(connection, "comm_outbox_slice_003.sql");
        }

        // Stage 0 Batch B: prepares Notifications the way slice 2 will meet it in production — the PRE-slice-2 shape
        // (no EntityType/EntityId) — then applies the real slice-2 script on top, TWICE. Running it twice is the
        // idempotency proof: the second pass must add nothing and must not error.
        public async Task<int> EnsureNotificationsAndSlice2Async(int passes = 2)
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();

            await ExecuteAsync(connection, @"
IF OBJECT_ID('dbo.Notifications','U') IS NULL
CREATE TABLE dbo.Notifications (
    ID                  INT IDENTITY(1,1) PRIMARY KEY,
    RecipientEmployeeID INT NOT NULL,
    TitleAr             NVARCHAR(200) NULL,
    TitleEn             NVARCHAR(200) NULL,
    BodyAr              NVARCHAR(MAX) NULL,
    BodyEn              NVARCHAR(MAX) NULL,
    Type                NVARCHAR(60) NULL,
    RefId               INT NULL,
    IsRead              BIT NOT NULL DEFAULT 0,
    CompanyID           INT NULL,
    BranchID            INT NULL,
    Url                 NVARCHAR(400) NULL,
    Priority            NVARCHAR(20) NULL,
    Category            NVARCHAR(40) NULL,
    ActorEmployeeID     INT NULL,
    DedupKey            NVARCHAR(200) NULL,
    ExpiresAt           DATETIME2 NULL,
    ReadAt              DATETIME2 NULL,
    Icon                NVARCHAR(60) NULL,
    CreatedAt           DATETIME2 NULL,
    CreatedBy           INT NULL,
    UpdatedAt           DATETIME2 NULL,
    UpdatedBy           INT NULL
);");

            for (int i = 0; i < passes; i++)
                await RunScriptAsync(connection, "platform_business_events_slice_002.sql");
            return passes;
        }

        // Re-applies slice 1 on top of an already-deployed slice 1. The fixture ran it once at start-up, so this is
        // the second (and third…) pass — the idempotency proof for the kernel's own DDL.
        public async Task ReapplySlice1Async(int passes = 2)
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            for (int i = 0; i < passes; i++)
                await RunScriptAsync(connection, "platform_business_events.sql");
        }

        // Small schema-introspection helpers, so the schema tests read as assertions about the DATABASE rather than
        // as a pile of inline ADO.NET.
        public async Task<T?> ScalarAsync<T>(string sql)
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? default : (T)Convert.ChangeType(value, typeof(T));
        }

        public async Task<List<string>> StringsAsync(string sql)
        {
            var results = new List<string>();
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) results.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "");
            return results;
        }

        public async Task ResetCommOutboxAsync()
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, "DELETE FROM CommAttachments; DELETE FROM CommMessages;");
        }

        public async Task ResetAsync()
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            // Child first — the FK from dispatch to events is real in this schema.
            await ExecuteAsync(connection, "DELETE FROM BusinessEventDispatch; DELETE FROM BusinessEvents;");
        }

        // Inserts an event with an EXPLICIT EventId so a test can control identity relative to commit order.
        public static async Task InsertEventWithIdentityAsync(SqlConnection connection, SqlTransaction? tx, long? eventId, string entityCode, int entityId)
        {
            string sql = eventId.HasValue
                ? $@"SET IDENTITY_INSERT BusinessEvents ON;
                     INSERT INTO BusinessEvents (EventId, EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, CreatedAt)
                     VALUES (@id, NEWID(), 1, @entityType, @entityId, @eventType, 1, 'Internal', SYSUTCDATETIME());
                     SET IDENTITY_INSERT BusinessEvents OFF;"
                : @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, CreatedAt)
                    VALUES (NEWID(), 1, @entityType, @entityId, @eventType, 1, 'Internal', SYSUTCDATETIME());
                    SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

            await using var command = new SqlCommand(sql, connection, tx);
            if (eventId.HasValue) command.Parameters.AddWithValue("@id", eventId.Value);
            command.Parameters.AddWithValue("@entityType", entityCode);
            command.Parameters.AddWithValue("@entityId", entityId);
            command.Parameters.AddWithValue("@eventType", entityCode + ".Created");
            await command.ExecuteNonQueryAsync();
        }

        public static async Task InsertDispatchAsync(SqlConnection connection, SqlTransaction? tx, long eventId, string consumer, string status = "Pending")
        {
            const string sql = @"INSERT INTO BusinessEventDispatch (EventId, Consumer, Status, Attempts, UpdatedAt)
                                 VALUES (@eventId, @consumer, @status, 0, SYSUTCDATETIME());";
            await using var command = new SqlCommand(sql, connection, tx);
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@consumer", consumer);
            command.Parameters.AddWithValue("@status", status);
            await command.ExecuteNonQueryAsync();
        }

        // ---- internals ----

        private async Task RunDeploymentScriptAsync()
        {
            await using var connection = new SqlConnection(TestConnectionString);
            await connection.OpenAsync();
            await RunScriptAsync(connection, "platform_business_events.sql");
        }

        // Executes a real deploy/sql script. The scripts are GO-separated batches, which SqlCommand cannot run as
        // one string, so they are split the same way sqlcmd would.
        public async Task RunScriptAsync(SqlConnection connection, string fileName)
        {
            var script = await File.ReadAllTextAsync(LocateScript(fileName));
            foreach (var batch in script.Split(new[] { "\nGO", "\rGO", "\r\nGO" }, StringSplitOptions.None))
            {
                var trimmed = batch.Trim();
                if (trimmed.Length == 0) continue;
                await ExecuteAsync(connection, trimmed);
            }
        }

        // REMOVED ON RECOVERY: the CommStore factory.
        //
        // It built CrossBuy.BL.Comm.SqlCommMessageDispatchStore, an outbox dispatcher that is NOT in
        // canonical HEAD and is superseded there by BL/Communication/CommNotificationDispatcher - which
        // claims rows with ClaimedAt, recovers stale claims and is covered by CommTimelineAndDispatchTests.
        // Its only two consumers (Slice3CommOutboxTests, CommOutboxConcurrencyTests) test the superseded
        // implementation and were classified obsolete rather than recovered.
        //
        // The other 619 lines of this fixture are generic SQL Server probe infrastructure that nineteen
        // recovered suites depend on, so the fixture is recovered without this member rather than lost
        // with it.
        // Walks up from the test binary to the repository, so the script is read from source rather than
        // duplicated into the test project — the tests must run against the DDL that actually ships.
        private static string LocateScript(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "CrossBuy", "deploy", "sql", fileName);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            throw new FileNotFoundException($"Could not locate CrossBuy/deploy/sql/{fileName} above {AppContext.BaseDirectory}.");
        }

        private Task ExecuteOnMasterAsync(string sql) => ExecuteAsync(_masterConnectionString, sql);

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, sql);
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql)
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }

        // WHAT THIS USED TO SAY, and why it is no longer true:
        //
        //     catch { /* a leaked scratch database is harmless; failing teardown would mask the real
        //              result */ }
        //
        // Both halves were wrong. A leaked scratch database is not harmless - 300 of them accumulated on
        // this instance, and every run reported the instance clean. And a throw from a collection
        // fixture's DisposeAsync does NOT mask the real result: xUnit reports it as a Test Collection
        // Cleanup Failure ALONGSIDE the individual test outcomes, which were still printed. That was
        // measured in an isolated xUnit 2.9.2 project, not assumed.
        //
        // IT STILL NEVER THROWS FROM HERE. Throwing mid-teardown would skip the lease reporting that
        // follows it. It records, and DisposeAsync reports both together.
        //
        // OWNERSHIP IS UNCHANGED: this drops _databaseName and nothing else, and only when this fixture
        // actually created one.
        private string? _fixtureCleanupError;

        private async Task SafeDropAsync()
        {
            if (string.IsNullOrEmpty(_databaseName) || string.IsNullOrEmpty(_masterConnectionString)) return;

            SqlScratchManifest.DropIntent(_databaseName, FixtureOperation);

            try
            {
                SqlConnection.ClearAllPools();
                await ExecuteOnMasterAsync(
                    $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{_databaseName}]; END");

                // A DROP that returned without error and left the catalog behind is still a failed drop.
                // DropProbeDatabaseAsync has always confirmed removal for probes; the fixture's own
                // database is now held to the same standard.
                await using var master = new SqlConnection(_masterConnectionString);
                await master.OpenAsync();
                await using var check = new SqlCommand(
                    "SELECT COUNT(*) FROM sys.databases WHERE name = @n;", master);
                check.Parameters.AddWithValue("@n", _databaseName);
                if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 0)
                {
                    _fixtureCleanupError = "the database still exists after DROP";
                    SqlScratchManifest.CleanupFailed(_databaseName, FixtureOperation, _fixtureCleanupError);
                }
                else
                {
                    SqlScratchManifest.Released(_databaseName, FixtureOperation);
                }
            }
            catch (Exception ex)
            {
                // RECORDED, not swallowed. No retry: a DROP that failed once on a shared instance is a
                // fact to report, not a race to re-run.
                _fixtureCleanupError = ex.GetType().Name + ": " + ex.Message;
                SqlScratchManifest.Failed(_databaseName, FixtureOperation, ex);
            }
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
    {
        // One database per test RUN, and the tests inside it run sequentially: they assert on lock behaviour,
        // so parallel execution against the same tables would make them non-deterministic.
        public const string Name = "sqlserver-platform";
    }

    // =================================================================================================
    // THE DURABLE RUN MANIFEST (Batch 4A) - append-only evidence that outlives the process.
    //
    // WHY IN-MEMORY LEASE STATE WAS NOT ENOUGH. ProbeLease makes a forgotten release VISIBLE, and the
    // fixture reports it at teardown. Both live in the test process. The incident this programme exists
    // to close created 295 databases in 67 minutes across two runs that WERE NEVER REACHED TEARDOWN -
    // they were interrupted. Every record of what they created died with them, which is why the 300
    // leftovers can be counted today but not attributed. A lease answers "did this run clean up?" only
    // for a run that survived to be asked.
    //
    // WHAT THIS DOES NOT DO. It never creates, drops or touches a database, and it never reads the
    // instance. It is a write-only journal: the input to orphan detection, not an actor on the server.
    // Nothing here deletes anything, and nothing here runs at startup.
    //
    // STORAGE: one file per RUN, outside the repository and outside every database, under
    // %LOCALAPPDATA%\CrossBuy\sql-scratch-manifest (overridable with CROSSBUY_TEST_MANIFEST_DIR).
    // OUTSIDE THE REPOSITORY MATTERS TWICE OVER: a manifest inside it would be a tracked file that every
    // run dirties - the exact shape already recorded as "the guard suite rewrites a tracked file every
    // run, so a post-commit verification looks like landing drift".
    //
    // FORMAT: JSON LINES - one complete JSON object per line, appended, never rewritten. A JSON ARRAY
    // WOULD HAVE BEEN THE WRONG CHOICE: it has to be closed, so a process killed mid-run - the only case
    // this file exists for - would leave a document no parser accepts. With JSON Lines the worst a kill
    // can cost is a torn final line, and every line before it stays readable.
    //
    // CONCURRENCY: the file name carries the process id and the run id, so two test processes never
    // address the same file; within a process one lock serialises every write. The stream is opened with
    // FileShare.Read, so a second writer would FAIL LOUDLY rather than interleave bytes into a line.
    // =================================================================================================
    internal static class SqlScratchManifest
    {
        public const int SchemaVersion = 1;

        public const string DirectoryVariable = "CROSSBUY_TEST_MANIFEST_DIR";
        public const string RunIdVariable = "CROSSBUY_TEST_RUN_ID";

        public const string RoleFixture = "Fixture";
        public const string RoleProbe = "Probe";
        public const string RoleRun = "Run";

        // CLEANUP STATUS AS OF THIS EVENT. Pending means "this database is believed to exist"; it is the
        // honest answer for everything between CreateIntent and a CONFIRMED removal, including DropIntent.
        private const string StatusNone = "None";
        private const string StatusPending = "Pending";
        private const string StatusReleased = "Released";
        private const string StatusFailed = "Failed";

        /// <summary>Identifies this test run. Supply CROSSBUY_TEST_RUN_ID to correlate with a CI job.</summary>
        public static readonly string RunId = ResolveRunId();

        private static readonly object Gate = new();
        private static readonly List<string> Problems = new();
        private static int _sequence;
        private static string? _filePath;
        private static bool _headerWritten;

        /// <summary>The manifest file, or null if this run has not recorded anything yet.</summary>
        public static string? FilePath { get { lock (Gate) return _filePath; } }

        /// <summary>
        /// Events that could NOT be persisted. The fixture folds these into its teardown report, so a
        /// manifest that silently stopped recording fails the run instead of producing quiet evidence.
        /// </summary>
        public static IReadOnlyList<string> Failures { get { lock (Gate) return Problems.ToList(); } }

        // ---- lifecycle events -----------------------------------------------------------------------

        /// <summary>
        /// Written BEFORE CREATE DATABASE, and THE ONLY EVENT THAT THROWS. If the intent cannot be made
        /// durable, the caller must not go on to create a database the manifest would never know about:
        /// an unrecorded database is the precise defect this whole batch exists to prevent.
        /// </summary>
        public static void CreateIntent(string database, string family, string role, string owner)
            => Write(true, "CreateIntent", database, family, role, owner, StatusPending, null);

        /// <summary>Written only after CREATE DATABASE returned without error - SQL confirming the catalog.</summary>
        public static void Created(string database, string family, string role, string owner)
            => Write(false, "Created", database, family, role, owner, StatusPending, null);

        public static void SchemaBegin(string database, string? owner)
            => Write(false, "SchemaBegin", database, null, null, owner, StatusPending, null);

        public static void SchemaComplete(string database, string? owner)
            => Write(false, "SchemaComplete", database, null, null, owner, StatusPending, null);

        /// <summary>Binds a lease id to a database name already recorded by CreateIntent/Created.</summary>
        public static void Leased(string database, string family, string leaseId)
            => Write(false, "Leased", database, family, RoleProbe, leaseId, StatusPending, null);

        /// <summary>Written BEFORE DROP DATABASE. With no Released after it, the database MAY still exist.</summary>
        public static void DropIntent(string database, string? owner)
            => Write(false, "DropIntent", database, null, null, owner, StatusPending, null);

        /// <summary>
        /// WRITTEN ONLY ON A CONFIRMED REMOVAL - never merely because the DROP statement did not error.
        /// Every caller queries sys.databases first; a manifest that reported Released on an unconfirmed
        /// drop would recreate, on disk, the exact lie that let 300 databases accumulate unnoticed.
        /// </summary>
        public static void Released(string database, string? owner)
            => Write(false, "Released", database, null, null, owner, StatusReleased, null);

        public static void CleanupFailed(string database, string? owner, string error)
            => Write(false, "CleanupFailed", database, null, null, owner, StatusFailed, error);

        /// <summary>A create, schema or drop that threw. The exception is recorded sanitized, never raw.</summary>
        public static void Failed(string database, string? owner, Exception error)
            => Write(false, "Failed", database, null, null, owner, StatusFailed,
                     error.GetType().Name + ": " + error.Message);

        /// <summary>
        /// Closes the run. ITS ABSENCE IS THE SIGNAL ORPHAN DETECTION READS: a manifest whose last line is
        /// not RunEnded belongs to a process that was killed, and everything it created without a confirmed
        /// Released is a candidate orphan. A run with no SQL at all records nothing, not even this.
        /// </summary>
        public static void RunEnded(int problemCount)
        {
            lock (Gate) { if (!_headerWritten) return; }

            Write(false, "RunEnded", null, null, RoleRun, null,
                  problemCount == 0 ? StatusReleased : StatusFailed,
                  problemCount == 0 ? null : problemCount + " problem(s) reported at teardown");
        }

        // ---- persistence ------------------------------------------------------------------------------

        private static void Write(bool required, string @event, string? database, string? family,
                                  string? role, string? owner, string cleanupStatus, string? error)
        {
            lock (Gate)
            {
                try
                {
                    EnsureFileLocked();
                    AppendLocked(Compose(@event, database, family, role, owner, cleanupStatus, error));
                }
                catch (Exception ex)
                {
                    // NOT SUPPRESSED. The failure is remembered and the fixture reports it at teardown, so
                    // a manifest that stopped recording cannot leave behind a file that merely looks short.
                    var problem = $"{@event} for '{database ?? "(run)"}' was not persisted: " +
                                  Sanitize(ex.GetType().Name + ": " + ex.Message);
                    Problems.Add(problem);

                    if (required)
                        throw new InvalidOperationException(
                            "the SQL scratch run manifest could not be written, so this run would create a " +
                            "database it has no durable record of: " + problem, ex);
                }
            }
        }

        // THE FILE IS CREATED LAZILY, on the first event. A run that never touches SQL - every run without
        // CROSSBUY_TEST_SQL - leaves no file at all, rather than a trail of empty manifests.
        private static void EnsureFileLocked()
        {
            if (_headerWritten) return;

            var directory = ResolveDirectory();
            Directory.CreateDirectory(directory);

            // The name is computed ONCE and kept even if the first write fails, so a retry appends to the
            // same file instead of scattering one run across several.
            _filePath ??= Path.Combine(directory,
                $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Short(RunId)}.jsonl");

            AppendLocked(Compose("RunStarted", null, null, RoleRun, null, StatusNone, null));
            _headerWritten = true;
        }

        // DURABILITY. WriteThrough bypasses the OS write cache and Flush(true) issues the disk flush, so the
        // line is on the platter BEFORE the SQL statement it describes is executed. The handle is opened and
        // closed per event rather than held: a handle held across a kill is a buffer nobody flushes.
        private static void AppendLocked(string line)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");

            using var stream = new FileStream(_filePath!, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 1, FileOptions.WriteThrough);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        // Called only under Gate, which is what makes ++_sequence safe without an interlocked.
        private static string Compose(string @event, string? database, string? family, string? role,
                                      string? owner, string cleanupStatus, string? error)
        {
            var sb = new System.Text.StringBuilder(320);
            sb.Append('{');
            sb.Append("\"v\":").Append(SchemaVersion);
            sb.Append(",\"ts\":").Append(Quote(DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            sb.Append(",\"runId\":").Append(Quote(RunId));
            sb.Append(",\"seq\":").Append(++_sequence);
            sb.Append(",\"event\":").Append(Quote(@event));
            sb.Append(",\"role\":").Append(Quote(role));
            sb.Append(",\"database\":").Append(Quote(database));
            sb.Append(",\"family\":").Append(Quote(family));
            sb.Append(",\"owner\":").Append(Quote(owner));
            sb.Append(",\"cleanupStatus\":").Append(Quote(cleanupStatus));
            sb.Append(",\"machine\":").Append(Quote(Environment.MachineName));
            sb.Append(",\"pid\":").Append(Environment.ProcessId);
            sb.Append(",\"error\":").Append(Quote(error is null ? null : Sanitize(error)));
            sb.Append('}');
            return sb.ToString();
        }

        // ---- sanitation -------------------------------------------------------------------------------

        /// <summary>
        /// Strips anything connection-string shaped out of a message before it is written down. NO CALLER
        /// EVER HANDS THIS A CONNECTION STRING - the manifest records names, not endpoints - but a
        /// SqlException MESSAGE can quote one back, and a file on disk outlives the console it would have
        /// scrolled past. Server and catalog go too: the manifest correlates on the database NAME, which
        /// carries a GUID and is unique on its own, so nothing is lost by refusing to record where it lived.
        /// </summary>
        internal static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                value,
                @"(?i)\b(password|pwd|user\s*id|uid|access[\s_-]*token|authentication|trusted_connection|"
                + @"server|data\s*source|initial\s*catalog|addr|address|network\s*address)\s*=\s*[^;,""']*",
                "$1=<redacted>");

            cleaned = cleaned.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            // Bounded: one pathological exception message must not become a megabyte of evidence file.
            return cleaned.Length <= 400 ? cleaned : cleaned.Substring(0, 400) + " ...(truncated)";
        }

        private static string Quote(string? value)
        {
            if (value is null) return "null";

            var sb = new System.Text.StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ---- resolution -------------------------------------------------------------------------------

        private static string ResolveDirectory()
        {
            var custom = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();

            return Path.Combine(local, "CrossBuy", "sql-scratch-manifest");
        }

        private static string ResolveRunId()
        {
            var supplied = Environment.GetEnvironmentVariable(RunIdVariable);
            if (string.IsNullOrWhiteSpace(supplied)) return Guid.NewGuid().ToString("N");

            // A supplied id becomes part of a FILE NAME, so it is filtered rather than trusted.
            var cleaned = new string(supplied.Trim()
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());

            return cleaned.Length == 0 ? Guid.NewGuid().ToString("N") : cleaned;
        }

        private static string Short(string value) => value.Length <= 8 ? value : value.Substring(0, 8);
    }
}