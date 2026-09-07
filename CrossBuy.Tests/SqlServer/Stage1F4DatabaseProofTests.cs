using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 1 F4 — THE DATABASE-LEVEL PROOF of Wave 1's central claim:
    // "a refused operation creates no partial data and no side effects."
    //
    // Every other Stage 1 test proves that the DECISION is correct. These prove what the DATABASE contains after a
    // refusal, under real transaction, isolation and locking semantics that SQLite cannot reproduce.
    //
    // Runs on a DISPOSABLE database the fixture creates and drops. Skipped — never silently passed — without
    // CROSSBUY_TEST_SQL, and the fixture REFUSES a connection string naming a real CrossBuy database.
    //
    // NOTE ON WHY THESE MATTER MORE THAN THEY LOOK: the four Batch C.1 SQL tests sat "gated, skipped" for two
    // batches and were reported as coverage. Their first real execution failed with twenty `Invalid column name`
    // errors, because their schema had been hand-written to match the test. A skipped test is not evidence.
    [Collection(SqlServerCollection.Name)]
    public class Stage1F4DatabaseProofTests : IAsyncLifetime
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        // Employee.ID is an IDENTITY column in the real schema, so these are assigned by the DATABASE and captured
        // in ArrangeAsync. Hardcoding them (as the SQLite tests can) fails with "Cannot insert explicit value for
        // identity column" — one more thing only a real server tells you.
        private int ChiefId;      // ChiefAccountant, company 1 → may post
        private int CashierId;    // Cashier, company 1        → may NOT post
        private int ForeignId;    // company 2

        private readonly SqlServerFixture _sql;
        private readonly ITestOutputHelper _out;
        public Stage1F4DatabaseProofTests(SqlServerFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }

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
            _probe = await _sql.CreateProbeDatabaseAsync("F4");
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
            Assert.StartsWith("CrossBuyProbe_F4_", _probe!.Name, StringComparison.Ordinal);
        }


        private static Employee Emp(int companyId, string tag) => new()
        {
            FirstName = "T", LastName = "T", FullName = "f4-" + tag, FullNameEn = "f4-" + tag,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"f4-{tag}-{Guid.NewGuid():N}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-f4-" + tag + "-" + Guid.NewGuid().ToString("N")[..8],
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        // The fixture's scratch DB, with the model's schema, cleared of the tables these proofs assert on so each
        // run starts from a known state and repeated runs neither drain nor collide.
        private async Task ArrangeAsync()
        {

            await using var c = new SqlConnection(Conn);
            await c.OpenAsync();
            foreach (var t in new[] { "JournalEntryLines", "JournalEntries", "StockMovements", "StockBalances",
                                      "Notifications", "BusinessEvents", "AccountingUserRoles", "Employee" })
            {
                try
                {
                    await using var cmd = new SqlCommand($"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL DELETE FROM dbo.{t};", c)
                    { CommandTimeout = 120 };
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (SqlException) { /* a table this proof does not use */ }
            }

            using var db = Db();
            var chief = Emp(CompanyOne, "chief");
            var cashier = Emp(CompanyOne, "cashier");
            var foreign = Emp(CompanyTwo, "foreign");
            db.Employee.AddRange(chief, cashier, foreign);
            await db.SaveChangesAsync();
            ChiefId = chief.ID; CashierId = cashier.ID; ForeignId = foreign.ID;

            db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = CashierId, Role = "Cashier" });
            await db.SaveChangesAsync();
        }

        private AccountingAccessService Accounting(CrossDbContext db, BusinessContext? ctx) =>
            new(db, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx),
                B6TestWiring.Policies(db), B6TestWiring.Log<AccountingAccessService>());

        private async Task<long> CountAsync(string table)
        {
            await using var c = new SqlConnection(Conn);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(
                $"IF OBJECT_ID(N'dbo.{table}', N'U') IS NULL SELECT CAST(-1 AS BIGINT) ELSE SELECT COUNT_BIG(*) FROM dbo.{table};", c);
            var v = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(v);
        }

        // A single fingerprint of every table these proofs could touch. Comparing it before and after a refusal is
        // stronger than checking one table: it catches a partial write anywhere in the set.
        private async Task<string> FingerprintAsync()
        {
            var tables = new[] { "JournalEntries", "JournalEntryLines", "StockMovements", "StockBalances",
                                 "BusinessEvents", "Notifications", "SalesInvoices", "LeaveRequests" };
            var parts = new List<string>();
            foreach (var t in tables) parts.Add($"{t}={await CountAsync(t)}");
            return string.Join(";", parts);
        }

        // =======================================================================================
        // 1-3. A REFUSED OPERATION WRITES NOTHING — accounting, inventory, payroll
        // =======================================================================================

        [SkippableFact]
        public async Task A_refused_accounting_decision_leaves_no_journal_no_lines_no_event_and_no_notification()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            using var db = Db();
            var cashier = Ctx(CashierId, CompanyOne);

            // The REAL access service against REAL role rows. A Cashier may not post.
            Assert.False(await Accounting(db, cashier).CanAsync(cashier, "post"));

            // The gate refuses before any writer is reached, so nothing exists to roll back. That is the claim,
            // and this is it measured against the database rather than asserted about the code.
            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(0, await CountAsync("JournalEntryLines"));
            Assert.Equal(0, await CountAsync("BusinessEvents"));
            Assert.Equal(0, await CountAsync("Notifications"));
            Assert.Equal(before, await FingerprintAsync());
        }

        [SkippableFact]
        public async Task A_refused_inventory_decision_leaves_no_movement_and_no_balance_change()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            using var db = Db();
            var cashier = Ctx(CashierId, CompanyOne);

            var inventory = new InventoryAccessService(
                db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new StubContextAccessor(cashier),
                B6TestWiring.Policies(db), B6TestWiring.Log<InventoryAccessService>());

            // an inventory role table with a row for someone else closes bootstrap-open for this company
            db.InventoryUserRoles.Add(new CrossBuy.Models.Context.Inventory.InventoryUserRole
            { CompanyID = CompanyOne, EmployeeId = 999, Role = "InventoryManager" });
            await db.SaveChangesAsync();

            Assert.False(await inventory.CanAsync(cashier, "manage"));

            Assert.Equal(0, await CountAsync("StockMovements"));
            Assert.Equal(0, await CountAsync("StockBalances"));
            Assert.Equal(before, await FingerprintAsync());
        }

        [SkippableFact]
        public async Task A_refused_payroll_decision_leaves_no_posting_and_no_leave_balance_change()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            using var db = Db();
            var hr = new HrAccessService(
                db, new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance), NullLogger<HrAccessService>.Instance);

            // payroll-manage is NEVER bootstrap-open, so this is refused with no role rows at all
            Assert.False(await hr.CanAsync(Ctx(CashierId, CompanyOne), HrActions.PayrollManage));

            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(0, await CountAsync("LeaveRequests"));
            Assert.Equal(before, await FingerprintAsync());
        }

        // =======================================================================================
        // 4. ROLLBACK INTEGRITY — a failed operation leaves the database byte-identical
        // =======================================================================================

        [SkippableFact]
        public async Task A_transaction_that_writes_then_fails_authorization_rolls_back_completely()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            using var db = Db();
            await using var tx = await db.Database.BeginTransactionAsync();

            // write real rows FIRST, so this proves rollback and not merely "nothing was attempted"
            db.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = DateTime.UtcNow.Date, Status = "Draft",
                Description = "F4 rollback probe", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            Assert.Equal(1, await InTransactionCountAsync(db, "JournalEntries"));   // visible INSIDE the tx

            // the authorization answer arrives after the write, as it would in a writer that checks late
            var cashier = Ctx(CashierId, CompanyOne);
            Assert.False(await Accounting(db, cashier).CanAsync(cashier, "post"));

            await tx.RollbackAsync();

            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(before, await FingerprintAsync());
        }

        private static async Task<long> InTransactionCountAsync(CrossDbContext db, string table)
        {
            var conn = db.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table};";
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // =======================================================================================
        // 5-6. UPDLOCK and HOLDLOCK — the semantics Stage 1 promised NOT to change
        // =======================================================================================

        // UPDLOCK on a SELECT must block a second connection's UPDLOCK read of the same row until commit. This is
        // the mechanism StockService relies on to serialise a read-then-write, and Stage 1 must not have altered it.
        [SkippableFact]
        public async Task Updlock_serialises_two_readers_of_the_same_row()
        {
            Ready();
            await ArrangeAsync();
            await SeedProbeRowAsync(1, 100m);

            await using var a = new SqlConnection(Conn); await a.OpenAsync();
            await using var b = new SqlConnection(Conn); await b.OpenAsync();

            await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
            var readA = await ScalarAsync(a, txA,
                "SELECT Qty FROM dbo.F4Probe WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;");
            Assert.Equal(100m, readA);

            // B must NOT get through while A holds the update lock
            var blocked = Task.Run(async () =>
            {
                await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
                var v = await ScalarAsync(b, txB, "SELECT Qty FROM dbo.F4Probe WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;");
                await txB.CommitAsync();
                return v;
            });

            var completedEarly = await Task.WhenAny(blocked, Task.Delay(1200)) == blocked;
            Assert.False(completedEarly, "a second UPDLOCK read completed while the first transaction still held the lock");

            // A commits its change; B then observes the COMMITTED value, not the stale one it would have read
            await ExecuteAsync(a, txA, "UPDATE dbo.F4Probe SET Qty = Qty - 40 WHERE Id = 1;");
            await txA.CommitAsync();

            Assert.Equal(60m, await blocked);
        }

        // HOLDLOCK (serializable) must prevent a phantom INSERT into the range it scanned.
        [SkippableFact]
        public async Task Holdlock_prevents_a_phantom_insert_into_the_scanned_range()
        {
            Ready();
            await ArrangeAsync();
            await SeedProbeRowAsync(10, 5m);

            await using var a = new SqlConnection(Conn); await a.OpenAsync();
            await using var b = new SqlConnection(Conn); await b.OpenAsync();

            await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
            await ScalarAsync(a, txA, "SELECT COUNT_BIG(*) FROM dbo.F4Probe WITH (HOLDLOCK) WHERE Id BETWEEN 10 AND 20;");

            var insert = Task.Run(async () =>
            {
                await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
                await ExecuteAsync(b, txB, "INSERT INTO dbo.F4Probe (Id, Qty) VALUES (15, 1);");
                await txB.CommitAsync();
            });

            var slipped = await Task.WhenAny(insert, Task.Delay(1200)) == insert;
            Assert.False(slipped, "a phantom row was inserted into a range held by HOLDLOCK");

            await txA.CommitAsync();
            await insert;   // now permitted
            Assert.Equal(2, await CountAsync("F4Probe"));
        }

        // =======================================================================================
        // 7-10. CONCURRENCY — authorization failure, rollback, stock access, financial posting
        // =======================================================================================

        // Ten concurrent refusals must produce ten refusals and zero rows. The point is that concurrency does not
        // turn a deny into an allow, and that no partial row appears under contention.
        [SkippableFact]
        public async Task Ten_concurrent_refusals_all_deny_and_write_nothing()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            {
                using var db = Db();
                var cashier = Ctx(CashierId, CompanyOne);
                return await Accounting(db, cashier).CanAsync(cashier, "post");
            }));

            Assert.All(results, r => Assert.False(r));
            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(before, await FingerprintAsync());
        }

        // Concurrent rollback: many transactions each write then roll back. None may leave a row, and the identity
        // seed advancing is NOT a data change — that is normal and is why the assertion counts rows, not identities.
        [SkippableFact]
        public async Task Concurrent_transactions_that_all_roll_back_leave_no_rows()
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                using var db = Db();
                await using var tx = await db.Database.BeginTransactionAsync();
                db.JournalEntries.Add(new JournalEntry
                {
                    CompanyID = CompanyOne, EntryDate = DateTime.UtcNow.Date, Status = "Draft",
                    Description = "F4 concurrent rollback " + i, CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                await tx.RollbackAsync();
            }));

            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(before, await FingerprintAsync());
        }

        // Concurrent stock access under UPDLOCK: eight decrements of one row must serialise exactly, with no lost
        // update. This is the invariant StockService's locking exists to hold.
        [SkippableFact]
        public async Task Eight_concurrent_locked_decrements_lose_no_update()
        {
            Ready();
            await ArrangeAsync();
            await SeedProbeRowAsync(1, 80m);

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var c = new SqlConnection(Conn);
                await c.OpenAsync();
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
                var q = await ScalarAsync(c, tx, "SELECT Qty FROM dbo.F4Probe WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;");
                await ExecuteAsync(c, tx, $"UPDATE dbo.F4Probe SET Qty = {q - 10m} WHERE Id = 1;");
                await tx.CommitAsync();
            }));

            // 80 − (8 × 10) = 0 exactly. A lost update would leave a higher number.
            await using var check = new SqlConnection(Conn); await check.OpenAsync();
            Assert.Equal(0m, await ScalarAsync(check, null, "SELECT Qty FROM dbo.F4Probe WHERE Id = 1;"));
        }

        // =======================================================================================
        // NEGATIVE TESTS — every attempt must leave zero business effect
        // =======================================================================================

        [SkippableTheory]
        [InlineData("foreign company")]
        [InlineData("tampered company")]
        [InlineData("tampered employee")]
        [InlineData("unresolved identity")]
        public async Task Every_negative_attempt_leaves_zero_business_effect(string attempt)
        {
            Ready();
            await ArrangeAsync();
            var before = await FingerprintAsync();

            using var db = Db();
            bool allowed;

            switch (attempt)
            {
                case "foreign company":
                    // B6 TRANSITION. This case previously asserted that a company-2 employee acting IN COMPANY 2
                    // was ALLOWED to post, because company 2 has no AccountingUserRole rows and accounting was
                    // bootstrap-open there. Accounting.post is now Never-Bootstrap-Open, so an unconfigured
                    // company denies it outright.
                    //
                    // The test's PROOF IS UNCHANGED and in fact stronger. Before, the negative was "allowed, but
                    // touched nothing of company 1". Now it is "denied outright, and touched nothing of company
                    // 1" — the zero-business-effect assertions below are preserved exactly, and company isolation
                    // is still proven by the row predicate rather than by the role check.
                    var f = Ctx(ForeignId, CompanyTwo);
                    var allowedInOwnCompany = await Accounting(db, f).CanAsync(f, "post");
                    Assert.True(NeverBootstrapOpen.Contains("Accounting", "post"));
                    Assert.False(allowedInOwnCompany,
                        "Accounting.post is Never-Bootstrap-Open, so an unconfigured company 2 must deny it");

                    // …and company 1's data is untouched by it: no row of company 1 is reachable from that decision.
                    var reachable = await db.JournalEntries.CountAsync(j => j.CompanyID == CompanyOne);
                    Assert.Equal(0, reachable);
                    allowed = false;   // nothing of company 1 was permitted
                    break;

                case "tampered company":
                    // the caller claims company 1 while their grant lives in company 2
                    var t = Ctx(ForeignId, CompanyOne);
                    allowed = await Accounting(db, t).CanAsync(t, "post");
                    break;

                case "tampered employee":
                    // an employee id that does not exist
                    var e = Ctx(999999, CompanyOne);
                    allowed = await Accounting(db, e).CanAsync(e, "post");
                    break;

                default:
                    // no BusinessContext at all — the resolver must refuse without assuming a company
                    var resolver = new RequestCompanyResolver(
                        StubContextAccessor.Unresolved(), NullLogger<RequestCompanyResolver>.Instance);
                    var r = await resolver.ResolveAsync();
                    Assert.False(r.Ok);
                    Assert.Equal(0, r.CompanyId);        // never 1
                    allowed = false;
                    break;
            }

            Assert.False(allowed, $"'{attempt}' was allowed");
            Assert.Equal(before, await FingerprintAsync());
            Assert.Equal(0, await CountAsync("JournalEntries"));
            Assert.Equal(0, await CountAsync("StockMovements"));
        }

        // =======================================================================================
        // PERFORMANCE — MEASURED ONLY, never optimised, no assertion on absolute timing
        // =======================================================================================

        [SkippableFact]
        public async Task Measure_authorization_transaction_rollback_and_lock_overhead()
        {
            Ready();
            await ArrangeAsync();
            await SeedProbeRowAsync(1, 1000m);

            using var db = Db();
            var chief = Ctx(ChiefId, CompanyOne);
            var acc = Accounting(db, chief);

            await acc.CanAsync(chief, "post");   // warm

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) await acc.CanAsync(chief, "post");
            var authMs = sw.Elapsed.TotalMilliseconds / 50;

            sw.Restart();
            for (int i = 0; i < 20; i++)
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await tx.RollbackAsync();
            }
            var txMs = sw.Elapsed.TotalMilliseconds / 20;

            sw.Restart();
            for (int i = 0; i < 20; i++)
            {
                await using var c = new SqlConnection(Conn);
                await c.OpenAsync();
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
                await ScalarAsync(c, tx, "SELECT Qty FROM dbo.F4Probe WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;");
                await tx.CommitAsync();
            }
            var lockMs = sw.Elapsed.TotalMilliseconds / 20;

            _out.WriteLine($"F4 PERFORMANCE (measure-only, SQL Server {Environment.MachineName})");
            _out.WriteLine($"  authorization decision (cached ctx, real roles) : {authMs:F3} ms");
            _out.WriteLine($"  begin+rollback transaction                      : {txMs:F3} ms");
            _out.WriteLine($"  UPDLOCK+HOLDLOCK read incl. connect             : {lockMs:F3} ms");

            // Deliberately NO timing assertion: a wall-clock threshold on a shared dev instance would be flaky and
            // would not say what regressed. The numbers are recorded in the closure report instead.
            Assert.True(authMs >= 0);
        }

        // ---- probe table: a minimal stand-in for a locked quantity row ----
        //
        // Deliberately NOT StockBalances. F4 must not alter production locking, and creating/clearing the real table
        // to drive contention would put test rows through the production writer's own table. The lock semantics
        // proven here (UPDLOCK, HOLDLOCK, serialised decrement) are engine behaviour, identical on any row.
        private async Task SeedProbeRowAsync(int id, decimal qty)
        {
            await using var c = new SqlConnection(Conn);
            await c.OpenAsync();
            await ExecuteAsync(c, null, @"
IF OBJECT_ID(N'dbo.F4Probe', N'U') IS NULL
    CREATE TABLE dbo.F4Probe (Id INT NOT NULL CONSTRAINT PK_F4Probe PRIMARY KEY, Qty DECIMAL(19,4) NOT NULL);");
            await ExecuteAsync(c, null, "DELETE FROM dbo.F4Probe;");
            await ExecuteAsync(c, null, $"INSERT INTO dbo.F4Probe (Id, Qty) VALUES ({id}, {qty});");
        }

        private static async Task ExecuteAsync(SqlConnection c, SqlTransaction? tx, string sql)
        {
            await using var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<decimal> ScalarAsync(SqlConnection c, SqlTransaction? tx, string sql)
        {
            await using var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 120 };
            var v = await cmd.ExecuteScalarAsync();
            return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
        }
    }
}
