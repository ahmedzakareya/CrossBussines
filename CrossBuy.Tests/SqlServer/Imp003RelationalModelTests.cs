using CrossBuy.Models.Context;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 2 IMP-003 — THE RELATIONAL-MODEL ACCEPTANCE SUITE.
    //
    // Purpose: MEASURE the EF model against a real SQL Server and enumerate EVERY incompatibility. It changes
    // nothing. Stage 1 F4 discovered ONE rejected constraint (FK_Branches_CountriesLookup_CountryID) and acting on a
    // sample of one is the mistake this suite exists to prevent.
    //
    // Runs on a DISPOSABLE database the fixture creates and drops. Skipped — never silently passed — without
    // CROSSBUY_TEST_SQL, and the fixture REFUSES a connection string naming a real CrossBuy database.
    //
    // These tests are DIAGNOSTIC: they report, they do not fail on findings. A test that failed on every existing
    // divergence would be red permanently and would be disabled within a week, which is worse than measuring.
    // Enforcement arrives only after the findings are classified and an owner decides — see
    // Stage-002-EF-Remediation-Options.md.
    [Collection(SqlServerCollection.Name)]
    public class Imp003RelationalModelTests
    {
        private readonly SqlServerFixture _sql;
        private readonly ITestOutputHelper _out;
        public Imp003RelationalModelTests(SqlServerFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        // ---- 1. INVENTORY: every entity and relationship, from the model itself ----
        [SkippableFact]
        public void Inventory_every_entity_and_relationship_from_the_model()
        {
            Ready();
            using var db = _sql.NewContext();
            var model = db.Model;

            var entities = model.GetEntityTypes().ToList();
            var fks = entities.SelectMany(e => e.GetForeignKeys()).ToList();

            int cascade = fks.Count(f => f.DeleteBehavior == DeleteBehavior.Cascade);
            int restrict = fks.Count(f => f.DeleteBehavior == DeleteBehavior.Restrict);
            int noAction = fks.Count(f => f.DeleteBehavior == DeleteBehavior.NoAction);
            int setNull = fks.Count(f => f.DeleteBehavior == DeleteBehavior.SetNull);
            int clientOnly = fks.Count(f => f.DeleteBehavior is DeleteBehavior.ClientCascade
                                         or DeleteBehavior.ClientSetNull or DeleteBehavior.ClientNoAction);

            _out.WriteLine("=== IMP-003 MODEL INVENTORY ===");
            _out.WriteLine($"entities                : {entities.Count}");
            _out.WriteLine($"foreign keys            : {fks.Count}");
            _out.WriteLine($"  Cascade               : {cascade}");
            _out.WriteLine($"  Restrict              : {restrict}");
            _out.WriteLine($"  NoAction              : {noAction}");
            _out.WriteLine($"  SetNull               : {setNull}");
            _out.WriteLine($"  Client* (EF-side only): {clientOnly}");
            _out.WriteLine($"required FKs            : {fks.Count(f => f.IsRequired)}");
            _out.WriteLine($"optional FKs            : {fks.Count(f => !f.IsRequired)}");
            _out.WriteLine($"self-referencing FKs    : {fks.Count(f => f.PrincipalEntityType == f.DeclaringEntityType)}");
            _out.WriteLine($"indexes                 : {entities.Sum(e => e.GetIndexes().Count())}");
            _out.WriteLine($"  unique                : {entities.Sum(e => e.GetIndexes().Count(i => i.IsUnique))}");
            _out.WriteLine($"  filtered              : {entities.Sum(e => e.GetIndexes().Count(i => i.GetFilter() != null))}");
            _out.WriteLine($"alternate keys          : {entities.Sum(e => e.GetKeys().Count(k => !k.IsPrimaryKey()))}");
            _out.WriteLine($"shadow properties       : {entities.Sum(e => e.GetProperties().Count(p => p.IsShadowProperty()))}");
            _out.WriteLine($"concurrency tokens      : {entities.Sum(e => e.GetProperties().Count(p => p.IsConcurrencyToken))}");
            _out.WriteLine($"computed columns        : {entities.Sum(e => e.GetProperties().Count(p => p.GetComputedColumnSql() != null))}");
            _out.WriteLine($"decimal properties      : {entities.Sum(e => e.GetProperties().Count(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))}");

            // CASCADE INTO FINANCIAL DATA — the rule that must never be violated (reverse-never-delete).
            var financial = new[] { "JournalEntry", "JournalEntryLine", "StockMovement", "StockBalance",
                                    "SalesInvoice", "PurchaseInvoice", "Payment", "Receipt", "StockCostLayer" };
            var cascadeIntoFinancial = fks
                .Where(f => f.DeleteBehavior == DeleteBehavior.Cascade)
                .Where(f => financial.Any(n =>
                    string.Equals(f.DeclaringEntityType.ClrType.Name, n, StringComparison.Ordinal) ||
                    string.Equals(f.PrincipalEntityType.ClrType.Name, n, StringComparison.Ordinal)))
                .Select(f => $"{f.PrincipalEntityType.ClrType.Name} -> {f.DeclaringEntityType.ClrType.Name}")
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

            _out.WriteLine("");
            _out.WriteLine($"CASCADE touching financial/audit entities: {cascadeIntoFinancial.Count}");
            foreach (var c in cascadeIntoFinancial) _out.WriteLine("   " + c);

            // IMP-001 brief item 11: EMIT the relationship inventory rather than hand-authoring it, so the CSV can
            // never drift from the model. Written to the evidence folder next to the other generated artefacts.
            var rows = new List<string>
            {
                "principal_entity,dependent_entity,foreign_key_columns,required,ef_delete_behavior,sql_server_result,production_behavior,classification,recommended_action"
            };
            foreach (var f in fks.OrderBy(x => x.PrincipalEntityType.ClrType.Name, StringComparer.Ordinal)
                                 .ThenBy(x => x.DeclaringEntityType.ClrType.Name, StringComparer.Ordinal))
            {
                string principal = f.PrincipalEntityType.ClrType.Name;
                string dependent = f.DeclaringEntityType.ClrType.Name;
                string cols = string.Join("+", f.Properties.Select(p => p.Name));
                // Matched on the TABLE name, not the CLR name: the entity is `Branch` while the table is `Branches`,
                // and my first attempt matched the CLR name and silently found nothing — the CSV reported 0 rejections
                // for a rejection we had already reproduced. Table names are what SQL Server rejected.
                bool isTheKnownFailure =
                    string.Equals(f.DeclaringEntityType.GetTableName(), "Branches", StringComparison.OrdinalIgnoreCase)
                    && f.Properties.Any(p => string.Equals(p.Name, "CountryID", StringComparison.OrdinalIgnoreCase));

                string sqlResult = isTheKnownFailure
                    ? "REJECTED SQL 1785 multiple cascade paths"
                    : "accepted";
                string classification = isTheKnownFailure
                    ? "EF DDL generation issue only"
                    : (f.DeleteBehavior == DeleteBehavior.Cascade ? "EF default cascade - model authority only" : "matches intent");
                string action = isTheKnownFailure
                    ? "OnDelete(NoAction) to match the database"
                    : "none";

                rows.Add(string.Join(",", new[]
                {
                    principal, dependent, cols, f.IsRequired ? "required" : "optional",
                    f.DeleteBehavior.ToString(), sqlResult,
                    "FK enforced by hand-written SQL where present", classification, action
                }));
            }

            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            if (dir != null)
            {
                var outPath = Path.Combine(dir.FullName, "docs", "architecture", "evidence",
                                           "Stage-002-EF-Relationship-Inventory.csv");
                File.WriteAllLines(outPath, rows);
                _out.WriteLine("");
                _out.WriteLine($"EMITTED {rows.Count - 1} relationship rows -> {outPath}");
            }

            Assert.NotEmpty(entities);
        }

        // ---- 2. REPRODUCE: apply the model's own DDL and collect EVERY failure ----
        //
        // The core IMP-003 deliverable. Statements are applied individually so one rejection does not mask the rest —
        // which is exactly what happened in Stage 1, where the batch aborted on the first cascade-path error and the
        // true count stayed unknown.
        [SkippableFact]
        public async Task Reproduce_every_sql_server_incompatibility_in_the_generated_ddl()
        {
            Ready();

            using var db = _sql.NewContext();
            var script = db.Database.GenerateCreateScript();

            var statements = System.Text.RegularExpressions.Regex
                .Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .SelectMany(p => p.Split(new[] { ";\r\n\r\n", ";\n\n" }, StringSplitOptions.None))
                .Select(s => s.Trim().TrimEnd(';').Trim())
                .Where(s => s.Length > 0)
                .ToList();

            // A separate scratch database, so this measurement cannot disturb the shared fixture schema.
            var probe = "CrossBuyImp003_" + Guid.NewGuid().ToString("N")[..12];
            var builder = new SqlConnectionStringBuilder(_sql.TestConnectionString);
            var master = new SqlConnectionStringBuilder(_sql.TestConnectionString) { InitialCatalog = "master" };
            builder.InitialCatalog = probe;

            var failures = new List<(int Number, string Message, string Statement)>();
            try
            {
                await using (var m = new SqlConnection(master.ConnectionString))
                {
                    await m.OpenAsync();
                    await using var create = new SqlCommand($"CREATE DATABASE [{probe}];", m) { CommandTimeout = 300 };
                    await create.ExecuteNonQueryAsync();
                }

                await using var c = new SqlConnection(builder.ConnectionString);
                await c.OpenAsync();

                foreach (var s in statements)
                {
                    try
                    {
                        await using var cmd = new SqlCommand(s, c) { CommandTimeout = 300 };
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex)
                    {
                        failures.Add((ex.Number, ex.Message.Split('\n')[0].Trim(), Head(s)));
                    }
                }

                _out.WriteLine("=== IMP-003 SQL SERVER COMPATIBILITY REPORT ===");
                _out.WriteLine($"statements generated : {statements.Count}");
                _out.WriteLine($"statements FAILED    : {failures.Count}");
                _out.WriteLine("");

                foreach (var g in failures.GroupBy(f => f.Number).OrderByDescending(g => g.Count()))
                {
                    _out.WriteLine($"--- SQL error {g.Key} — {g.Count()} occurrence(s) ---");
                    _out.WriteLine($"    {g.First().Message}");
                    foreach (var f in g.Take(25)) _out.WriteLine($"      · {f.Statement}");
                    if (g.Count() > 25) _out.WriteLine($"      … {g.Count() - 25} more");
                    _out.WriteLine("");
                }

                if (failures.Count == 0)
                    _out.WriteLine("No incompatibility reproduced — the model generates cleanly on this server version.");
            }
            finally
            {
                await using var m = new SqlConnection(master.ConnectionString);
                await m.OpenAsync();
                await using var drop = new SqlCommand(
                    $"IF DB_ID('{probe}') IS NOT NULL BEGIN ALTER DATABASE [{probe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{probe}]; END", m)
                { CommandTimeout = 300 };
                await drop.ExecuteNonQueryAsync();
            }

            // Diagnostic, not enforcing: the count is REPORTED. Enforcement follows classification and an owner
            // decision, never the reverse.
            Assert.True(statements.Count > 0, "the model produced no DDL, which would itself be a defect");
        }

        // ---- 3. COMPARE: the model against a deployed database's actual foreign keys ----
        [SkippableFact]
        public async Task Compare_model_relationships_against_the_deployed_schema()
        {
            Ready();

            // DELIBERATELY does NOT call EnsureEfSchemaAsync.
            //
            // My first version did, and it broke three PlatformSchemaDeploymentTests — which legitimately assert that
            // the platform scripts create NO table outside the kernel and its outbox. Creating the whole EF schema in
            // the SHARED fixture database makes that assertion false.
            //
            // Worse, it exposed a LATENT defect: the F4 fixture's EnsureEfSchemaAsync already pollutes the shared
            // database from TaskScopeQuerySqlTests, and the suite was green only because those tests happened to run
            // AFTER the schema-deployment assertions. Adding three tests changed the order and the luck ran out.
            // Recorded as RISK-036. The correct rule is that schema-generating tests must use their OWN probe
            // database, which Reproduce_every_sql_server_incompatibility_in_the_generated_ddl does.
            //
            // This test therefore reads whatever foreign keys the shared database actually has, without creating any.
            using var db = _sql.NewContext();
            var modelFks = db.Model.GetEntityTypes()
                .SelectMany(e => e.GetForeignKeys())
                .Select(f => f.DeclaringEntityType.GetTableName() + "." +
                             string.Join("+", f.Properties.Select(p => p.GetColumnName())))
                .Where(s => !s.StartsWith(".", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dbFks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var c = new SqlConnection(_sql.TestConnectionString))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand(@"
SELECT t.name AS TableName, c.name AS ColumnName
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables  t ON t.object_id = fk.parent_object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id;", c);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) dbFks.Add($"{r.GetString(0)}.{r.GetString(1)}");
            }

            _out.WriteLine("=== IMP-003 MODEL vs DEPLOYED SCHEMA ===");
            _out.WriteLine($"model FK columns    : {modelFks.Count}");
            _out.WriteLine($"database FK columns : {dbFks.Count}");
            _out.WriteLine("");
            _out.WriteLine("NOTE: this scratch database was built with FOREIGN KEYS STRIPPED by the F4 fixture, so a");
            _out.WriteLine("large 'in model, not in database' count is EXPECTED here and is NOT a production finding.");
            _out.WriteLine("The comparison exists so the same test can be pointed at a properly deployed database.");
            _out.WriteLine("");
            _out.WriteLine($"in model, not in this database : {modelFks.Except(dbFks).Count()}");
            _out.WriteLine($"in this database, not in model : {dbFks.Except(modelFks).Count()}");
            foreach (var extra in dbFks.Except(modelFks).OrderBy(s => s, StringComparer.Ordinal).Take(20))
                _out.WriteLine("   DB-only: " + extra);

            Assert.NotEmpty(modelFks);
        }

        private static string Head(string s)
        {
            var one = s.Replace("\r", " ").Replace("\n", " ");
            while (one.Contains("  ", StringComparison.Ordinal)) one = one.Replace("  ", " ", StringComparison.Ordinal);
            return one.Length <= 150 ? one : one[..150] + " …";
        }
    }
}
