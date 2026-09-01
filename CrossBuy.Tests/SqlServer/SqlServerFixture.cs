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

            try
            {
                await ExecuteOnMasterAsync($"CREATE DATABASE [{_databaseName}];");
                await RunDeploymentScriptAsync();
            }
            catch (Exception ex)
            {
                SkipReason = $"Could not prepare the SQL Server test database: {ex.Message}";
                await SafeDropAsync();
            }
        }

        public async Task DisposeAsync() => await SafeDropAsync();

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
        private static string StripForeignKeys(string script)
        {
            var noInline = System.Text.RegularExpressions.Regex.Replace(
                script,
                @",\s*CONSTRAINT \[FK_[^\]]+\] FOREIGN KEY[^,\r\n]*(\([^)]*\))?\s*REFERENCES[^,\r\n]*(\([^)]*\))?(\s+ON DELETE [A-Z ]+)?(\s+ON UPDATE [A-Z ]+)?",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return System.Text.RegularExpressions.Regex.Replace(
                noInline,
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

            await using (var m = new SqlConnection(master.ConnectionString))
            {
                await m.OpenAsync();
                await using var create = new SqlCommand($"CREATE DATABASE [{name}];", m) { CommandTimeout = 300 };
                await create.ExecuteNonQueryAsync();
            }

            var probe = new ProbeDatabase(name, mine.ConnectionString);
            if (!applyModelSchema) return probe;

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
            return probe;
        }

        // Drops a probe and CONFIRMS removal. A green run with a leftover probe is not acceptance.
        public async Task DropProbeDatabaseAsync(ProbeDatabase probe)
        {
            if (probe is null || string.IsNullOrEmpty(probe.Name)) return;

            // Batch 00-C guard 2: ownership is decided by SqlEvidenceGuards, not re-implemented here. The rule is
            // asserted directly by a test that passes it a foreign name, so deleting this call breaks a test rather
            // than silently widening what cleanup may drop.
            SqlEvidenceGuards.AssertOwnedScratchName(probe.Name);

            var master = new SqlConnectionStringBuilder(TestConnectionString) { InitialCatalog = "master" };
            await using var m = new SqlConnection(master.ConnectionString);
            await m.OpenAsync();

            await using (var drop = new SqlCommand(
                $"IF DB_ID(@n) IS NOT NULL BEGIN ALTER DATABASE [{probe.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{probe.Name}]; END", m)
            { CommandTimeout = 300 })
            {
                drop.Parameters.AddWithValue("@n", probe.Name);
                await drop.ExecuteNonQueryAsync();
            }

            await using var check = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n;", m);
            check.Parameters.AddWithValue("@n", probe.Name);
            var still = Convert.ToInt32(await check.ExecuteScalarAsync());
            if (still != 0)
                throw new InvalidOperationException($"probe database '{probe.Name}' was not removed — cleanup failed");
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
                "SELECT name FROM sys.databases WHERE name LIKE 'CrossBuyProbe_%' ORDER BY name;", m);
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

        private async Task SafeDropAsync()
        {
            if (string.IsNullOrEmpty(_databaseName) || string.IsNullOrEmpty(_masterConnectionString)) return;
            try
            {
                SqlConnection.ClearAllPools();
                await ExecuteOnMasterAsync(
                    $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{_databaseName}]; END");
            }
            catch { /* a leaked scratch database is harmless; failing teardown would mask the real result */ }
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
    {
        // One database per test RUN, and the tests inside it run sequentially: they assert on lock behaviour,
        // so parallel execution against the same tables would make them non-deterministic.
        public const string Name = "sqlserver-platform";
    }
}