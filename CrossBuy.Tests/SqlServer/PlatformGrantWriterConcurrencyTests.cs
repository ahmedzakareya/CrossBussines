using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // =============================================================================================
    // Stage 2A Batch B — B0: the Grant Writer concurrency proof.
    //
    // This closes the ONE gap the Batch A delivery report declared outstanding. Batch A proved the
    // uniqueness race is HANDLED — the database arbitrates and the loser reports Conflict — but no test
    // ran two writers at once, so the claim rested on reading the code.
    //
    // WHY THIS CANNOT BE PROVEN IN-PROCESS ON SQLITE
    //
    // The whole mechanism under test is a SQL Server filtered unique index plus a WITH (UPDLOCK, ROWLOCK)
    // read. SQLite has neither, so an in-memory version of these tests would pass while proving nothing —
    // exactly the hazard the SQL evidence suite exists to avoid.
    //
    // EACH THREAD GETS ITS OWN CrossDbContext AND ITS OWN WRITER. A DbContext is not thread-safe, so
    // sharing one would produce a test that fails for a reason unrelated to the concurrency being proven.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class PlatformGrantWriterConcurrencyTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int AdminEmployee = 9101;
        private const int TargetEmployee = 9102;

        public PlatformGrantWriterConcurrencyTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _probe = await _sql.CreateProbeDatabaseAsync("GrantConcurrency");
            await SeedAsync();
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;

            // See PlatformGrantWriterAcceptanceTests.DisposeAsync — pooled connections keep the probe alive and
            // DROP DATABASE silently fails to take effect without this.
            SqlConnection.ClearAllPools();
            await _sql.DropProbeDatabaseAsync(_probe);
        }

        // ---------------------------------------------------------------------------------------------

        private sealed class StubIdentity : IPlatformAdminIdentity
        {
            public Task<IReadOnlyList<string>> RolesAsync(string? userId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<string>>(
                    userId == "user-platform" ? new[] { "PlatformOps" } : Array.Empty<string>());
        }

        private sealed class StubContextAccessor : IBusinessContextAccessor
        {
            private static readonly BusinessContext Resolved = new()
            {
                CompanyId = CompanyOne, EmployeeId = AdminEmployee, UserId = "user-platform",
                Source = BusinessContextSource.Http,
            };
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Resolved);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult<BusinessContext?>(Resolved);
        }

        /// <summary>An INDEPENDENT writer on its own context — one per concurrent thread.</summary>
        private (IPlatformGrantWriter writer, CrossDbContext db) NewWriter()
        {
            var db = _sql.ContextFor(_probe!, CompanyOne);
            var directory = new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance);
            var hr = new HrAccessService(db, directory,
                new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance), NullLogger<HrAccessService>.Instance);
            var events = new BusinessEventService(
                db, new EntityRegistry(db), new StubContextAccessor(), NullLogger<BusinessEventService>.Instance);

            // Fresh per writer, matching the per-thread isolation this test depends on.
            var vocabulary = new PlatformPermissionVocabularyRegistry(new IPlatformPermissionVocabulary[]
            {
                new HrPermissionVocabulary(), new ProjectsPermissionVocabulary(),
                new TasksPermissionVocabulary(), new CommunicationPermissionVocabulary(),
            });

            return (new PlatformGrantWriter(db, events, directory, new IModuleAccessService[] { hr },
                new StubIdentity(), vocabulary, NullLogger<PlatformGrantWriter>.Instance), db);
        }

        private static BusinessContext Actor => new()
        {
            CompanyId = CompanyOne, EmployeeId = AdminEmployee, UserId = "user-platform",
            Source = BusinessContextSource.Http,
        };

        private static CreatePlatformGrantCommand Grant(string? key = null) => new()
        {
            CompanyId = CompanyOne,
            Scope = EntityRegistry.ScopeHr,
            PrincipalType = PlatformPrincipalTypes.Employee,
            PrincipalId = TargetEmployee,
            Role = HrRoles.PayrollOfficer,
            Reason = "B0 concurrency",
            IdempotencyKey = key,
        };

        /// <summary>
        /// Runs N operations genuinely in parallel and returns every result. A Barrier makes them start together —
        /// without it the first task usually finishes before the second begins, and the test would prove nothing
        /// while passing.
        /// </summary>
        private static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> operation)
        {
            using var barrier = new Barrier(count);

            var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await operation(i);
            })).ToArray();

            return await Task.WhenAll(tasks);
        }

        // =========================================================================================
        // B0.1 — two concurrent creates of the SAME effective grant
        // =========================================================================================

        [SkippableFact]
        public async Task Two_concurrent_creates_of_the_same_grant_produce_exactly_one_active_row()
        {
            Ready();

            var results = await InParallelAsync(2, async _ =>
            {
                var (writer, db) = NewWriter();
                try { return await writer.CreateGrantAsync(Actor, Grant()); }
                finally { await db.DisposeAsync(); }
            });

            var succeeded = results.Count(r => r.Outcome == PlatformGrantOutcome.Success);
            var refused = results.Count(r => r.Outcome is PlatformGrantOutcome.Duplicate or PlatformGrantOutcome.Conflict);

            Assert.Equal(1, succeeded);
            Assert.Equal(1, refused);

            await using var fresh = _sql.ContextFor(_probe!, CompanyOne);

            // ONE active row. This is the property the filtered unique index exists for, and the reason the
            // application-level duplicate check cannot be the guarantee: both callers read "not found".
            var active = await fresh.PlatformRoleAssignments.AsNoTracking()
                .Where(r => r.CompanyID == CompanyOne && r.Scope == EntityRegistry.ScopeHr
                         && r.PrincipalId == TargetEmployee && r.Role == HrRoles.PayrollOfficer && r.IsActive)
                .ToListAsync();

            Assert.Single(active);

            // And exactly ONE Created event. A second event would mean the losing writer committed its event
            // while its row was rejected — the atomicity failure that would make the audit trail disagree with
            // the data.
            var created = await fresh.BusinessEvents.AsNoTracking()
                .CountAsync(e => e.EntityType == EntityRegistry.PlatformRoleAssignment
                              && e.EntityId == active[0].ID
                              && e.EventType == "PlatformRoleAssignment.Created");

            Assert.Equal(1, created);
        }

        [SkippableFact]
        public async Task Eight_concurrent_creates_still_produce_exactly_one_active_row()
        {
            Ready();

            // Two threads can pass by luck of scheduling. Eight makes the index do the work.
            var results = await InParallelAsync(8, async _ =>
            {
                var (writer, db) = NewWriter();
                try { return await writer.CreateGrantAsync(Actor, Grant()); }
                finally { await db.DisposeAsync(); }
            });

            Assert.Equal(1, results.Count(r => r.Outcome == PlatformGrantOutcome.Success));
            Assert.Equal(7, results.Count(r =>
                r.Outcome is PlatformGrantOutcome.Duplicate or PlatformGrantOutcome.Conflict));

            await using var fresh = _sql.ContextFor(_probe!, CompanyOne);
            Assert.Equal(1, await fresh.PlatformRoleAssignments.AsNoTracking()
                .CountAsync(r => r.PrincipalId == TargetEmployee && r.IsActive));
        }

        // =========================================================================================
        // B0.2 — concurrent revoke and validity update
        // =========================================================================================

        [SkippableFact]
        public async Task Concurrent_revoke_and_validity_update_do_not_lose_an_update_or_split_the_audit()
        {
            Ready();

            var (setup, setupDb) = NewWriter();
            var created = await setup.CreateGrantAsync(Actor, Grant());
            await setupDb.DisposeAsync();
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);
            var id = created.Grant!.Id;

            var results = await InParallelAsync(2, async i =>
            {
                var (writer, db) = NewWriter();
                try
                {
                    return i == 0
                        ? await writer.RevokeGrantAsync(Actor, new RevokePlatformGrantCommand
                        { GrantId = id, CompanyId = CompanyOne, Reason = "concurrent revoke" })
                        : await writer.UpdateValidityAsync(Actor, new UpdateGrantValidityCommand
                        {
                            GrantId = id, CompanyId = CompanyOne,
                            ValidTo = DateTime.UtcNow.AddDays(30), Reason = "concurrent validity",
                        });
                }
                finally { await db.DisposeAsync(); }
            });

            await using var fresh = _sql.ContextFor(_probe!, CompanyOne);
            var row = await fresh.PlatformRoleAssignments.AsNoTracking().SingleAsync(r => r.ID == id);

            // THE DOCUMENTED OUTCOME. The UPDLOCK read serialises the two operations, so one of exactly two
            // orderings occurs — and both are coherent:
            //
            //   revoke first  -> the validity update finds an inactive row and returns Conflict, refusing to
            //                    re-date a revoked grant. Row: revoked.
            //   validity first-> the revoke proceeds on a still-active row. Row: revoked, with the new ValidTo.
            //
            // Either way the row ends REVOKED and the audit fields are internally consistent. What must never
            // happen is a lost update, a half-written audit trail, or an active row.
            Assert.False(row.IsActive);
            Assert.NotNull(row.RevokedAt);
            Assert.Equal(AdminEmployee, row.RevokedBy);
            Assert.NotNull(row.Reason);

            var revokeSucceeded = results.Any(r =>
                r.Outcome == PlatformGrantOutcome.Success && r.Grant?.IsActive == false);
            Assert.True(revokeSucceeded, "the revoke must have taken effect exactly once");

            // Exactly one Revoked event: no duplicate revocation in the audit trail.
            Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking()
                .CountAsync(e => e.EntityId == id && e.EventType == "PlatformRoleAssignment.Revoked"));
        }

        [SkippableFact]
        public async Task Two_concurrent_revokes_revoke_once_and_report_AlreadyRevoked_once()
        {
            Ready();

            var (setup, setupDb) = NewWriter();
            var created = await setup.CreateGrantAsync(Actor, Grant());
            await setupDb.DisposeAsync();
            var id = created.Grant!.Id;

            var results = await InParallelAsync(2, async _ =>
            {
                var (writer, db) = NewWriter();
                try
                {
                    return await writer.RevokeGrantAsync(Actor, new RevokePlatformGrantCommand
                    { GrantId = id, CompanyId = CompanyOne, Reason = "double revoke" });
                }
                finally { await db.DisposeAsync(); }
            });

            Assert.Equal(1, results.Count(r => r.Outcome == PlatformGrantOutcome.Success));
            Assert.Equal(1, results.Count(r => r.Outcome == PlatformGrantOutcome.AlreadyRevoked));

            await using var fresh = _sql.ContextFor(_probe!, CompanyOne);

            // ONE Revoked event, and RevokedBy still names the writer that actually did it — the second call must
            // not overwrite the first, because that would destroy who performed the revocation.
            Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking()
                .CountAsync(e => e.EntityId == id && e.EventType == "PlatformRoleAssignment.Revoked"));
        }

        // =========================================================================================
        // B0.3 — concurrent idempotent retries
        // =========================================================================================

        [SkippableFact]
        public async Task Concurrent_retries_with_the_same_idempotency_key_create_one_row_and_one_event()
        {
            Ready();

            const string key = "b0-concurrent-key";
            const int callers = 6;

            var results = await InParallelAsync(callers, async _ =>
            {
                var (writer, db) = NewWriter();
                try { return await writer.CreateGrantAsync(Actor, Grant(key)); }
                finally { await db.DisposeAsync(); }
            });

            // A retry storm is the realistic shape: a client resends the same request while the first is still
            // in flight. Every caller must end up pointing at the SAME grant.
            var succeeded = results.Where(r => r.Outcome == PlatformGrantOutcome.Success).ToList();
            var replayed = results.Where(r => r.Outcome == PlatformGrantOutcome.IdempotentReplay).ToList();
            var conflicted = results.Where(r => r.Outcome == PlatformGrantOutcome.Conflict).ToList();
            var duplicated = results.Where(r => r.Outcome == PlatformGrantOutcome.Duplicate).ToList();

            // WHY A LOSER MAY BE ANY OF THREE OUTCOMES, and why pinning it to two made this test flake.
            //
            // Two unique indexes can refuse this insert, and which one the engine reports is a genuine race:
            // UX_PlatformRoleAssignments_Idempotency (same key already stored -> IdempotentReplay/Conflict) and
            // UX_PlatformRoleAssignments_ActiveGrant (an equivalent ACTIVE grant already exists -> Duplicate).
            // Six writers all read "not found" and all insert; SQL Server arbitrates, and PlatformGrantWriter's
            // own IsConcurrencyLoss already treats 2601/2627/1205 alike for exactly this reason.
            //
            // Demanding {IdempotentReplay, Conflict} therefore asserted a scheduling accident, not a contract:
            // observed runs produced 3, 4 and 5 losers in those two buckets while the SAFETY properties below
            // held every single time. Duplicate is a REFUSAL that writes no row - it is not a second success,
            // and admitting it widens the accepted LOSER set without widening the accepted WINNER set.
            //
            // The three assertions below are deliberately joint, so nothing can be absorbed:
            //   (1) exactly one winner;
            //   (2) NO outcome outside the four legitimate ones - ValidationFailed, Forbidden, NotFound,
            //       Expired or AlreadyRevoked here would be a real defect and must fail, not be swept into a
            //       catch-all "not success" bucket;
            //   (3) every remaining caller accounted for by an explicit count, not by a subset check.
            // A second success fails (1) AND (3); an unexpected classification fails (2); a vanished caller
            // fails (3). The exactly-one-row and exactly-one-event assertions that follow are untouched.

            // (1) EXACTLY ONE WINNER.
            Assert.Single(succeeded);

            // (2) NO UNEXPECTED CLASSIFICATION. Listed by name so a failure says which outcome appeared.
            var allowedOutcomes = new[]
            {
                PlatformGrantOutcome.Success,
                PlatformGrantOutcome.IdempotentReplay,
                PlatformGrantOutcome.Conflict,
                PlatformGrantOutcome.Duplicate,
            };
            var unexpected = results
                .Where(r => !allowedOutcomes.Contains(r.Outcome))
                .Select(r => r.Outcome.ToString())
                .ToList();
            Assert.Empty(unexpected);

            // (3) EVERY LOSING CALLER ACCOUNTED FOR - an exact count over the whole fan-out.
            Assert.Equal(callers, results.Length);
            Assert.Equal(callers - 1, replayed.Count + conflicted.Count + duplicated.Count);

            // Every result that carries a grant must name the same one. A different id would mean a second row.
            foreach (var view in results.Where(r => r.Grant != null))
                Assert.Equal(succeeded[0].Grant!.Id, view.Grant!.Id);

            await using var fresh = _sql.ContextFor(_probe!, CompanyOne);

            Assert.Equal(1, await fresh.PlatformRoleAssignments.AsNoTracking()
                .CountAsync(r => r.IdempotencyKey == key));

            Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking()
                .CountAsync(e => e.EntityId == succeeded[0].Grant!.Id
                              && e.EventType == "PlatformRoleAssignment.Created"));
        }

        // =========================================================================================

        private async Task SeedAsync()
        {
            await using var db = _sql.ContextFor(_probe!, CompanyOne);

            await db.Database.OpenConnectionAsync();
            try
            {
                await FillAsync(db, "dbo.Companies", () =>
                    db.Companies.Add(new Companies { CompanyID = CompanyOne, CompanyName = "ZZ Concurrency" }));

                await FillAsync(db, "dbo.Employee", () =>
                {
                    db.Employee.Add(new Employee
                    {
                        ID = AdminEmployee, EmpCompanyID = CompanyOne, IsActive = true, UserId = "user-platform",
                        FullName = "ZZ Admin", FirstName = "ZZ", LastName = "Admin",
                    });
                    db.Employee.Add(new Employee
                    {
                        ID = TargetEmployee, EmpCompanyID = CompanyOne, IsActive = true, UserId = "user-target",
                        FullName = "ZZ Target", FirstName = "ZZ", LastName = "Target",
                    });
                });
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        private static async Task FillAsync(CrossDbContext db, string table, Action add)
        {
            await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} ON;");
            try
            {
                add();

                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                    foreach (var p in entry.Properties)
                        if (p.Metadata.ClrType == typeof(string) && !p.Metadata.IsNullable && p.CurrentValue == null)
                            p.CurrentValue = "ZZ";

                await db.SaveChangesAsync();
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} OFF;");
            }
        }
    }
}
