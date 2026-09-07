using System.Security.Cryptography;
using CrossBuy.BL.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // =============================================================================================
    // A0 — REPORTING DATABASE DEPLOYMENT PROOF.
    //
    // WHY THIS FILE HAD TO EXIST, stated plainly because it is the whole point of A0:
    //
    //     EnsureCreated() IS NOT DEPLOYMENT EVIDENCE.
    //
    // Every Reporting test was green while /Reports/Viewer returned a 500 on a real database with
    // `Invalid object name 'ReportShares'`. ReportingTestHost builds its SQLite tables from the EF model
    // via EnsureCreated(), which proves the MODEL and says nothing about the authored SQL. The two can
    // disagree indefinitely and no test notices.
    //
    // These tests apply the REAL file — CrossBuy/deploy/sql/reporting_platform.sql — to an EMPTY SQL
    // Server database and assert against the resulting schema. That is the only thing that counts as
    // deployment evidence, and it is what the previous increment's suite could not do.
    //
    // ISOLATION. Every test creates its OWN probe database through SqlServerFixture.CreateProbeDatabaseAsync
    // with applyModelSchema:false — an EMPTY database, not the shared fixture one. The shared fixture already
    // has the platform kernel deployed and PlatformSchemaDeploymentTests legitimately asserts that the
    // platform scripts create no table outside the kernel; adding twelve reporting tables to it would break
    // those tests, which is exactly the RISK-036 collision the probe helper exists to prevent.
    //
    // SAFETY comes from the existing guards, not from new ones: CROSSBUY_TEST_SQL gating, production-catalog
    // refusal in SqlEvidenceGuards, the CrossBuyProbe_ ownership prefix, and drop-with-confirmation.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class ReportingSchemaDeploymentTests
    {
        private const string Slice = "reporting_platform.sql";

        // The twelve tables. Derived from the EF model at run time by ReportingModelTables(), NOT from this
        // list — the list exists only so a failure message can name what was expected.
        private static readonly string[] ExpectedTables =
        {
            "ReportTemplates", "ReportTemplateVersions", "ReportCategories", "ReportTags", "ReportTagLinks",
            "ReportFavorites", "ReportShares", "ReportRuns", "ReportArchiveEntries",
            "ReportSchedules", "ReportScheduleRecipients", "ReportDeliveryAttempts",
        };

        private readonly SqlServerFixture _sql;
        public ReportingSchemaDeploymentTests(SqlServerFixture sql) { _sql = sql; }

        private bool Skip(out string reason)
        {
            reason = _sql.SkipReason ?? "";
            return !_sql.Available;
        }

        // The Reporting tables the EF MODEL declares, read from the model itself. This is what makes the
        // parity test mechanical: adding a 13th Reporting entity to CrossDbContext changes this set, and the
        // test then fails until the SQL slice creates its table.
        //
        // Ownership is decided by the CLR NAMESPACE (CrossBuy.Models.Context.Reporting), not by a name prefix
        // and not by a hand-kept list — a future `ReportingSnapshot` entity is owned by this slice whether or
        // not somebody remembers to add it here.
        private static IReadOnlyList<string> ReportingModelTables(CrossBuy.Models.Context.CrossDbContext db) =>
            db.Model.GetEntityTypes()
                .Where(e => e.ClrType.Namespace == "CrossBuy.Models.Context.Reporting")
                .Select(e => e.GetTableName() ?? e.ClrType.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static async Task ApplyAsync(SqlServerFixture fixture, SqlServerFixture.ProbeDatabase probe,
            int passes = 1)
        {
            await using var connection = new SqlConnection(probe.ConnectionString);
            await connection.OpenAsync();
            for (var i = 0; i < passes; i++) await fixture.RunScriptAsync(connection, Slice);
        }

        private static async Task<List<string>> QueryAsync(SqlServerFixture.ProbeDatabase probe, string sql)
        {
            var rows = new List<string>();
            await using var connection = new SqlConnection(probe.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var parts = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                    parts.Add(reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "");
                rows.Add(string.Join("|", parts));
            }
            return rows;
        }

        private static Task<List<string>> TablesAsync(SqlServerFixture.ProbeDatabase probe) =>
            QueryAsync(probe, @"SELECT name FROM sys.tables WHERE name LIKE 'Report%' ORDER BY name;");

        // A complete fingerprint of everything the slice creates. Used for the idempotency comparison, so
        // "identical" means identical objects — not merely "the same number of tables".
        private static Task<List<string>> InventoryAsync(SqlServerFixture.ProbeDatabase probe) =>
            QueryAsync(probe, @"
SELECT 'TABLE|'  + t.name FROM sys.tables t WHERE t.name LIKE 'Report%'
UNION ALL
SELECT 'COLUMN|' + t.name + '.' + c.name + '|' + ty.name + '|'
       + CAST(c.max_length AS NVARCHAR(10)) + '|' + CAST(c.is_nullable AS NVARCHAR(1))
  FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
                     JOIN sys.types ty ON ty.user_type_id = c.user_type_id
 WHERE t.name LIKE 'Report%'
UNION ALL
SELECT 'INDEX|'  + t.name + '.' + i.name + '|' + CAST(i.is_unique AS NVARCHAR(1)) + '|'
       + CAST(CASE WHEN i.filter_definition IS NULL THEN 0 ELSE 1 END AS NVARCHAR(1))
  FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
 WHERE t.name LIKE 'Report%' AND i.name IS NOT NULL
UNION ALL
SELECT 'FK|'     + t.name + '.' + f.name FROM sys.foreign_keys f
  JOIN sys.tables t ON t.object_id = f.parent_object_id WHERE t.name LIKE 'Report%'
UNION ALL
SELECT 'CHECK|'  + t.name + '.' + cc.name FROM sys.check_constraints cc
  JOIN sys.tables t ON t.object_id = cc.parent_object_id WHERE t.name LIKE 'Report%'
UNION ALL
SELECT 'DEFAULT|' + t.name + '.' + dc.name FROM sys.default_constraints dc
  JOIN sys.tables t ON t.object_id = dc.parent_object_id WHERE t.name LIKE 'Report%'
ORDER BY 1;");

        // ============================================================================================
        // 1. FROM EMPTY
        // ============================================================================================
        [Fact]
        public async Task The_reporting_slice_creates_its_whole_schema_in_an_empty_database()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptEmpty", applyModelSchema: false);
            try
            {
                // The starting state is asserted, not assumed. A proof that begins on a database somebody
                // already prepared proves only that it stayed prepared.
                Assert.Empty(await TablesAsync(probe));

                await ApplyAsync(_sql, probe);

                var tables = await TablesAsync(probe);
                foreach (var expected in ExpectedTables)
                    Assert.Contains(expected, tables, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(ExpectedTables.Length, tables.Count);

                // ReportShares in particular — the table whose absence produced the 500 that started A0.
                var shares = await QueryAsync(probe, @"
SELECT c.name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
 WHERE t.name = 'ReportShares' ORDER BY c.name;");

                foreach (var column in new[]
                {
                    "Id", "CompanyID", "ReportCode", "TemplateId", "PrincipalType", "PrincipalKey",
                    "AccessLevel", "ExpiresAt", "GrantedByEmpId", "DeletedAt",
                })
                    Assert.Contains(column, shares, StringComparer.OrdinalIgnoreCase);

                // The two indexes the authorization path rides on. HighestShareLevelAsync runs on EVERY
                // report authorization, so a missing index here is a production-wide table scan.
                var indexes = await QueryAsync(probe, @"
SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
 WHERE t.name = 'ReportShares' AND i.name IS NOT NULL ORDER BY i.name;");

                Assert.Contains("UX_ReportShares_Unique", indexes);
                Assert.Contains("IX_ReportShares_Lookup", indexes);
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 2. IDEMPOTENCY — object-for-object, not count-for-count
        // ============================================================================================
        [Fact]
        public async Task Applying_the_slice_twice_leaves_an_identical_object_inventory()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptIdem", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);
                var afterFirst = await InventoryAsync(probe);

                await ApplyAsync(_sql, probe, passes: 2);   // third and fourth applications
                var afterMore = await InventoryAsync(probe);

                // FULL SEQUENCE EQUALITY. Comparing counts would pass if one index vanished while another
                // appeared; comparing the ordered inventory cannot.
                Assert.Equal(afterFirst, afterMore);

                // And nothing is duplicated within a single snapshot.
                Assert.Equal(afterMore.Distinct().Count(), afterMore.Count);
                Assert.NotEmpty(afterFirst);
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 3. EF ↔ SQL PARITY, DERIVED — this is the test that stops A0 recurring
        // ============================================================================================
        [Fact]
        public async Task Every_reporting_entity_in_the_ef_model_has_a_table_in_the_authored_slice()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptParity", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);

                using var db = _sql.ContextFor(probe);
                var modelTables = ReportingModelTables(db);
                var deployed = await TablesAsync(probe);

                Assert.NotEmpty(modelTables);

                var missing = modelTables
                    .Where(t => !deployed.Contains(t, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                Assert.True(missing.Count == 0,
                    "These Reporting entities are mapped in the EF model but the authored SQL slice creates no " +
                    "table for them — deploying this build would reproduce the 'Invalid object name' failure A0 " +
                    "exists to close: " + string.Join(", ", missing));

                // And nothing is deployed that the model does not know about, which would be dead schema.
                var orphaned = deployed
                    .Where(t => !modelTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                Assert.True(orphaned.Count == 0,
                    "The slice creates tables the EF model does not map: " + string.Join(", ", orphaned));
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // Column-level parity for every Reporting entity: a property the model persists must exist as a
        // column. Catches the failure mode TaskScopeQuerySqlTests already paid for once — a hand-written
        // table with the columns the test happened to touch.
        [Fact]
        public async Task Every_persisted_reporting_property_has_a_column()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptColumns", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);

                using var db = _sql.ContextFor(probe);

                var deployed = (await QueryAsync(probe, @"
SELECT t.name + '.' + c.name FROM sys.columns c
  JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name LIKE 'Report%';"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = new List<string>();

                foreach (var entity in db.Model.GetEntityTypes()
                             .Where(e => e.ClrType.Namespace == "CrossBuy.Models.Context.Reporting"))
                {
                    var table = entity.GetTableName();
                    if (table is null) continue;

                    foreach (var property in entity.GetProperties())
                    {
                        var column = property.GetColumnName();
                        if (string.IsNullOrEmpty(column)) continue;
                        if (!deployed.Contains(table + "." + column)) missing.Add(table + "." + column);
                    }
                }

                Assert.True(missing.Count == 0,
                    "Mapped properties with no column in the deployed schema: " + string.Join(", ", missing));
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 4. CONSTRAINTS ACTUALLY REJECT — a CHECK nobody tested is a comment
        // ============================================================================================
        [Theory]
        [InlineData("ReportTemplates", "CK_ReportTemplates_Scope")]
        [InlineData("ReportShares", "CK_ReportShares_PrincipalType")]
        [InlineData("ReportShares", "CK_ReportShares_AccessLevel")]
        [InlineData("ReportRuns", "CK_ReportRuns_Kind")]
        [InlineData("ReportRuns", "CK_ReportRuns_Status")]
        [InlineData("ReportSchedules", "CK_ReportSchedules_Frequency")]
        [InlineData("ReportDeliveryAttempts", "CK_ReportDeliveryAttempts_Status")]
        public async Task Each_vocabulary_check_constraint_is_present(string table, string constraint)
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptCk", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);
                var found = await QueryAsync(probe,
                    $@"SELECT cc.name FROM sys.check_constraints cc
                         JOIN sys.tables t ON t.object_id = cc.parent_object_id
                        WHERE t.name = '{table}' AND cc.name = '{constraint}';");
                Assert.Single(found);
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // The constraints REFUSE data, proven by inserting a value one past the top of each enum. Asserting
        // that a constraint exists proves a row in sys.check_constraints; it does not prove the predicate is
        // the right way round.
        [Fact]
        public async Task Out_of_range_vocabulary_values_are_rejected_by_the_database()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptReject", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);

                // (statement, the enum member count — so the injected value is exactly one past the end)
                var attempts = new (string What, string Sql)[]
                {
                    ("ReportTemplates.Scope = 4",
                     "INSERT INTO dbo.ReportTemplates (CompanyID, ReportCode, Scope) VALUES (1, N'X', 4);"),
                    ("ReportShares.PrincipalType = 4",
                     "INSERT INTO dbo.ReportShares (CompanyID, ReportCode, PrincipalType, AccessLevel) VALUES (1, N'X', 4, 2);"),
                    ("ReportShares.AccessLevel = 5",
                     "INSERT INTO dbo.ReportShares (CompanyID, ReportCode, PrincipalType, AccessLevel) VALUES (1, N'X', 0, 5);"),
                    ("ReportRuns.Kind = 3",
                     "INSERT INTO dbo.ReportRuns (CompanyID, ReportCode, Kind, Status, StartedAt) VALUES (1, N'X', 3, 0, SYSUTCDATETIME());"),
                    ("ReportRuns.Status = 4",
                     "INSERT INTO dbo.ReportRuns (CompanyID, ReportCode, Kind, Status, StartedAt) VALUES (1, N'X', 0, 4, SYSUTCDATETIME());"),
                    ("ReportSchedules.Frequency = 5",
                     "INSERT INTO dbo.ReportSchedules (CompanyID, ReportCode, Frequency, OwnerEmpId) VALUES (1, N'X', 5, 7);"),
                    ("ReportDeliveryAttempts.Status = 4",
                     "INSERT INTO dbo.ReportDeliveryAttempts (CompanyID, Status, AttemptedAt) VALUES (1, 4, SYSUTCDATETIME());"),
                };

                await using var connection = new SqlConnection(probe.ConnectionString);
                await connection.OpenAsync();

                foreach (var (what, sql) in attempts)
                {
                    await using var command = new SqlCommand(sql, connection);
                    var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

                    // 547 = "conflicted with the CHECK constraint". Asserting the NUMBER matters: a NOT NULL
                    // violation or a missing column would also throw, and would let this pass for the wrong
                    // reason while proving nothing about the constraint.
                    Assert.True(error.Number == 547,
                        $"{what} should have been refused by a CHECK constraint (547) but failed with " +
                        $"{error.Number}: {error.Message}");
                }

                // And the legal top-of-range value is ACCEPTED — an off-by-one in the predicate would make
                // every real Personal template (Scope 3) unsavable, which the rejection tests alone miss.
                await using var legal = new SqlCommand(
                    "INSERT INTO dbo.ReportTemplates (CompanyID, ReportCode, Scope) VALUES (1, N'Legal', 3);",
                    connection);
                await legal.ExecuteNonQueryAsync();
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // The unique constraint that stops two live grants answering "what does this person have?" two ways.
        [Fact]
        public async Task A_duplicate_live_share_grant_is_rejected()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptUnique", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);

                await using var connection = new SqlConnection(probe.ConnectionString);
                await connection.OpenAsync();

                const string insert = @"INSERT INTO dbo.ReportShares
                    (CompanyID, ReportCode, TemplateId, PrincipalType, PrincipalKey, AccessLevel)
                    VALUES (1, N'Test.Report', NULL, 0, N'7', 2);";

                await using (var first = new SqlCommand(insert, connection)) await first.ExecuteNonQueryAsync();

                await using var second = new SqlCommand(insert, connection);
                var error = await Assert.ThrowsAsync<SqlException>(() => second.ExecuteNonQueryAsync());
                Assert.True(error.Number is 2601 or 2627, $"expected a uniqueness violation, got {error.Number}");

                // The index is FILTERED on DeletedAt IS NULL, so revoking then re-granting must work — a
                // grant you can never reinstate would make revocation permanent by accident.
                await using (var revoke = new SqlCommand(
                    "UPDATE dbo.ReportShares SET DeletedAt = SYSUTCDATETIME();", connection))
                    await revoke.ExecuteNonQueryAsync();

                await using var regrant = new SqlCommand(insert, connection);
                await regrant.ExecuteNonQueryAsync();
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 5. PLATFORM SCHEMA HISTORY — through the EXISTING mechanism, no new history table
        // ============================================================================================
        [Fact]
        public async Task The_reporting_slice_records_through_the_existing_platform_schema_history()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptHistory", applyModelSchema: false);
            try
            {
                await using var connection = new SqlConnection(probe.ConnectionString);
                await connection.OpenAsync();

                // The history slice is a PREREQUISITE, applied exactly as a deployment would.
                await _sql.RunScriptAsync(connection, "platform_schema_history.sql");
                await _sql.RunScriptAsync(connection, Slice);

                // Recorded the way the runbook records it: repo-relative name + the file's real SHA-256.
                var path = LocateSlice();
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))
                    .ToLowerInvariant();

                await using (var record = new SqlCommand(@"
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedBy, Success)
VALUES (@name, @hash, @by, 1);", connection))
                {
                    record.Parameters.AddWithValue("@name", "CrossBuy/deploy/sql/" + Slice);
                    record.Parameters.AddWithValue("@hash", hash);
                    record.Parameters.AddWithValue("@by", "A0-proof");
                    await record.ExecuteNonQueryAsync();
                }

                var current = await QueryAsync(probe, @"
SELECT ScriptName, ApplyCount FROM dbo.vw_PlatformSchemaCurrent
 WHERE ScriptName LIKE '%reporting_platform.sql';");

                var row = Assert.Single(current);
                Assert.EndsWith("|1", row);   // ApplyCount = 1 after the first record

                // The hash CHECK is real: an upper-cased digest is refused, so a mis-recorded hash cannot
                // make the slice look permanently "changed" in the drift report.
                await using var bad = new SqlCommand(@"
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedBy, Success)
VALUES ('x', '" + hash.ToUpperInvariant() + @"', 'A0-proof', 1);", connection);
                var error = await Assert.ThrowsAsync<SqlException>(() => bad.ExecuteNonQueryAsync());
                Assert.Equal(547, error.Number);
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // A CHANGED FILE MUST SHOW AS DRIFT. Recording a second, different hash for the same script is what
        // the drift report reads; if the view collapsed them the report could never say "this changed".
        [Fact]
        public async Task A_changed_slice_hash_is_visible_as_a_second_apply()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptDrift", applyModelSchema: false);
            try
            {
                await using var connection = new SqlConnection(probe.ConnectionString);
                await connection.OpenAsync();
                await _sql.RunScriptAsync(connection, "platform_schema_history.sql");

                const string name = "CrossBuy/deploy/sql/" + Slice;
                var a = new string('a', 64);
                var b = new string('b', 64);

                foreach (var hash in new[] { a, b })
                {
                    await using var record = new SqlCommand(@"
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedBy, Success)
VALUES (@name, @hash, 'A0-proof', 1);", connection);
                    record.Parameters.AddWithValue("@name", name);
                    record.Parameters.AddWithValue("@hash", hash);
                    await record.ExecuteNonQueryAsync();
                }

                var current = await QueryAsync(probe,
                    "SELECT ScriptHash, ApplyCount FROM dbo.vw_PlatformSchemaCurrent WHERE ScriptName = '" + name + "';");

                var row = Assert.Single(current);
                Assert.StartsWith(b, row);      // the LATEST hash wins
                Assert.EndsWith("|2", row);     // and both applies are counted
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 6. RUNTIME — the real services, against the authored schema
        // ============================================================================================
        //
        // The point of A0 is that /Reports/Viewer works on a database built by the SQL file. Asserting the
        // tables exist is necessary and not sufficient: the columns must also be the ones EF binds to, which
        // only a real query proves.
        [Fact]
        public async Task The_reporting_services_run_against_the_authored_schema()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptRuntime", applyModelSchema: false);
            try
            {
                await ApplyAsync(_sql, probe);

                using var db = _sql.ContextFor(probe);

                // THE QUERY THAT PRODUCED THE 500. HighestShareLevelAsync reads ReportShares on every
                // authorization, and it was `Invalid object name 'ReportShares'` that started A0.
                var shares = await db.ReportShares.AsNoTracking()
                    .Where(s => s.CompanyID == 1 && s.DeletedAt == null).ToListAsync();
                Assert.Empty(shares);

                // Every other Reporting set EF binds — a column mismatch surfaces here as a real SqlException
                // rather than at a user's first click.
                Assert.Empty(await db.ReportTemplates.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportTemplateVersions.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportCategories.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportTags.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportTagLinks.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportFavorites.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportRuns.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportArchiveEntries.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportSchedules.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportScheduleRecipients.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ReportDeliveryAttempts.AsNoTracking().ToListAsync());

                // A round trip through EF, not just a read: writing exercises the defaults and the column
                // types together, which a SELECT over an empty table cannot.
                db.ReportTemplates.Add(new CrossBuy.Models.Context.Reporting.ReportTemplate
                {
                    CompanyID = 1,
                    ReportCode = BusinessEventsReportCodes.ReportCode,
                    Name = "A0 proof",
                    Scope = CrossBuy.Models.Context.Reporting.ReportTemplateScope.Company,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();

                using var fresh = _sql.ContextFor(probe);
                var saved = Assert.Single(await fresh.ReportTemplates.AsNoTracking().ToListAsync());
                Assert.Equal(BusinessEventsReportCodes.ReportCode, saved.ReportCode);
                Assert.Equal(CrossBuy.Models.Context.Reporting.ReportTemplateScope.Company, saved.Scope);
            }
            finally { await _sql.DropProbeDatabaseAsync(probe); }
        }

        // ============================================================================================
        // 7. CLEANUP IS PART OF THE PROOF
        // ============================================================================================
        [Fact]
        public async Task No_probe_database_is_left_behind()
        {
            if (Skip(out var reason)) { Assert.True(true, reason); return; }

            var probe = await _sql.CreateProbeDatabaseAsync("rptCleanup", applyModelSchema: false);
            await ApplyAsync(_sql, probe);
            await _sql.DropProbeDatabaseAsync(probe);

            var remaining = await _sql.ListOwnedProbeDatabasesAsync();
            Assert.DoesNotContain(probe.Name, remaining);
        }

        private static string LocateSlice()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "CrossBuy", "deploy", "sql", Slice);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            throw new FileNotFoundException($"Could not locate CrossBuy/deploy/sql/{Slice}.");
        }
    }
}
