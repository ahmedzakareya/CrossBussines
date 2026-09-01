using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 1 Batch C.1 item 1 — the set-based scope query against a REAL SQL Server.
    //
    // WHY THIS EXISTS ALONGSIDE THE SQLITE TESTS. `BatchC1ScopeQueryTests` proves the predicate produces the
    // right SET, and it runs everywhere. It cannot prove the claim that actually matters in production: that the
    // predicate is EXECUTED BY THE DATABASE. SQLite and SQL Server have different translation rules, and the
    // specific failure this guards — a `Contains` over a principal list, or a filter that quietly falls back to
    // client evaluation — is provider-specific. EF Core 9 throws on unintended client evaluation rather than
    // degrading silently, so a query that translates here is genuinely running server-side.
    //
    // These read from `sys.dm_exec_query_stats`-free ground truth instead: the row counts SQL Server returns for
    // the filtered query, and the SQL text EF sent. If the filter had been applied in memory, the SQL sent would
    // have no WHERE clause on CompanyId and the counts would not vary by scope.
    //
    // Runs on a DISPOSABLE database the fixture creates and drops. Skipped — never silently passed — without
    // CROSSBUY_TEST_SQL, and the fixture REFUSES a connection string naming a real CrossBuy database.
    [Collection(SqlServerCollection.Name)]
    public class TaskScopeQuerySqlTests : IAsyncLifetime
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        private readonly SqlServerFixture _sql;
        public TaskScopeQuerySqlTests(SqlServerFixture sql) { _sql = sql; }

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);
        // ---- ISOLATED PROBE DATABASE (RISK-036) ----
        //
        // This family GENERATES SCHEMA, so it owns a dedicated probe database and never touches the shared platform
        // fixture. Calling EnsureEfSchemaAsync on the shared fixture is what broke three PlatformSchemaDeploymentTests
        // twice. The lifecycle comes from SqlServerFixture so the rule lives in ONE helper.
        private SqlServerFixture.ProbeDatabase? _probe;
        private string _sharedFingerprintBefore = "";

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _sharedFingerprintBefore = await _sql.SharedFixtureFingerprintAsync();
            _probe = await _sql.CreateProbeDatabaseAsync("TaskScope");
        }

        public async Task DisposeAsync()
        {
            if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
        }

        private string Conn => _probe!.ConnectionString;
        private CrossBuy.Models.Context.CrossDbContext Db() => _sql.ContextFor(_probe!, CompanyOne);

        // Independent purity proof for this family.
        [SkippableFact]
        public async Task This_family_adds_nothing_to_the_shared_platform_fixture()
        {
            Ready();
            var after = await _sql.SharedFixtureFingerprintAsync();
            Assert.Equal(_sharedFingerprintBefore, after);
            Assert.NotNull(_probe);
            Assert.StartsWith("CrossBuyProbe_TaskScope_", _probe!.Name, StringComparison.Ordinal);
        }


        // Stage 1 F4 — the hand-written DDL this method used to contain was WRONG: it declared 9 of the entity's
        // ~28 columns, and because these tests were gated on an unset CROSSBUY_TEST_SQL they reported SKIPPED for two
        // batches instead of failing. The first real run produced twenty `Invalid column name` errors.
        //
        // The schema now comes from the EF model itself (SqlServerFixture.EnsureEfSchemaAsync), so a column the model
        // has and the test forgot cannot exist. Rows are cleared per test so repeated runs neither drain nor collide.
        private async Task EnsureTaskTableAsync()
        {
            await using var connection = new SqlConnection(Conn);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand("DELETE FROM dbo.TaskItems;", connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }

        private static TaskItem NewTask(int companyId, int assignee, int creator) => new()
        {
            CompanyId = companyId, Title = "c1", Status = "New", AssigneeEmployeeId = assignee,
            CreatedByEmployeeId = creator, CreatedAt = DateTime.UtcNow,
        };

        private async Task SeedAsync(params TaskItem[] tasks)
        {
            using var db = Db();
            db.TaskItems.AddRange(tasks);
            await db.SaveChangesAsync();
        }

        // =======================================================================================
        // the predicate reaches SQL Server, and SQL Server does the filtering
        // =======================================================================================

        [SkippableFact]
        public async Task Each_breadth_translates_to_real_sql_and_returns_the_right_rows()
        {
            Ready();
            await EnsureTaskTableAsync();
            await SeedAsync(
                NewTask(CompanyOne, 10, 10),
                NewTask(CompanyOne, 11, 11),
                NewTask(CompanyOne, 12, 12),
                NewTask(CompanyTwo, 10, 10));   // same employee id, other company

            using var db = Db();

            // ---- Company ----
            var company = db.TaskItems.WithinScope(AccessScope.Company(CompanyOne), 10);
            Assert.Contains("WHERE", company.ToQueryString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, await company.CountAsync());

            // ---- Team: Contains over a principal list must become an IN (...), server-side ----
            var team = db.TaskItems.WithinScope(AccessScope.Team(CompanyOne, new[] { 10, 11 }), 10);
            var teamSql = team.ToQueryString();
            Assert.Contains("WHERE", teamSql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, await team.CountAsync());

            // ---- Own ----
            var own = db.TaskItems.WithinScope(AccessScope.Own(CompanyOne), 10);
            Assert.Equal(1, await own.CountAsync());

            // ---- None ----
            Assert.Equal(0, await db.TaskItems.WithinScope(AccessScope.None(), 10).CountAsync());
        }

        // A COUNT is the sharpest proof that filtering happened in SQL: EF can only answer it with a server-side
        // COUNT(*), so if the count is right, the WHERE ran on the server. Had the predicate been evaluated in
        // memory, EF Core 9 would have thrown instead of quietly loading the table.
        [SkippableFact]
        public async Task The_count_is_computed_by_the_server_and_never_loads_the_rows()
        {
            Ready();
            await EnsureTaskTableAsync();

            var seed = new List<TaskItem>();
            for (int i = 0; i < 300; i++) seed.Add(NewTask(CompanyOne, 10, 10));
            for (int i = 0; i < 300; i++) seed.Add(NewTask(CompanyOne, 12, 12));
            for (int i = 0; i < 300; i++) seed.Add(NewTask(CompanyTwo, 10, 10));
            await SeedAsync(seed.ToArray());

            using var db = Db();

            var query = db.TaskItems.WithinScope(AccessScope.Own(CompanyOne), 10);
            var sql = query.ToQueryString();

            Assert.Equal(300, await query.CountAsync());
            Assert.Contains("CompanyId", sql, StringComparison.OrdinalIgnoreCase);

            // the 900-row table is never materialised: paging composes onto the same predicate
            var page = await query.OrderBy(t => t.ID).Skip(10).Take(25).ToListAsync();
            Assert.Equal(25, page.Count);
            Assert.All(page, t => Assert.Equal(CompanyOne, t.CompanyId));
            Assert.All(page, t => Assert.Equal(10, t.AssigneeEmployeeId));
        }

        [SkippableFact]
        public async Task Company_isolation_holds_against_a_real_server_for_every_breadth()
        {
            Ready();
            await EnsureTaskTableAsync();
            await SeedAsync(
                NewTask(CompanyOne, 10, 10),
                NewTask(CompanyTwo, 10, 10),
                NewTask(CompanyTwo, 11, 11));

            using var db = Db();

            foreach (var scope in new[]
            {
                AccessScope.Company(CompanyOne),
                AccessScope.Team(CompanyOne, new[] { 10, 11 }),
                AccessScope.Own(CompanyOne),
            })
            {
                var rows = await db.TaskItems.WithinScope(scope, 10).ToListAsync();
                Assert.NotEmpty(rows);
                Assert.All(rows, t => Assert.Equal(CompanyOne, t.CompanyId));
            }
        }

        // The breadth TaskItem cannot express, asserted against the real provider too: it must throw rather than
        // emit SQL that widens or narrows the set.
        [SkippableFact]
        public void A_branch_scope_never_reaches_sql()
        {
            Ready();
            using var db = Db();

            Assert.Throws<NotSupportedException>(() =>
                db.TaskItems.WithinScope(AccessScope.Branch(CompanyOne, 3), 10).ToQueryString());
            Assert.Throws<NotSupportedException>(() =>
                db.TaskItems.WithinScope(AccessScope.CrossCompany(), 10).ToQueryString());
        }
    }
}
