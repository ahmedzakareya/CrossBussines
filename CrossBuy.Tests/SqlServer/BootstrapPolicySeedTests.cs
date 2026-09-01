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
    // CrossBusiness Platform — Stage 2A Batch B — B3 seed evidence, on a real SQL Server probe.
    //
    // THE PLAN IS FOUR ENTRIES, and that is a source-derived correction the tests pin:
    //   41 = superseded historical planning figure
    //   32 = complete live-source bootstrap-eligible inventory (recorded, not seeded)
    //    4 = implemented seed set — Accounting.read, Inventory.read, Crm.read, Crm.edit
    // Each of the four is reconciled to the exact B6 call site that will read it, and a test asserts
    // that reconciliation exists for every entry. A policy no production path consumes is misleading
    // configuration, so "names its call site" is a rule, not documentation.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class BootstrapPolicySeedTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Actor = 9201;
        private const int ForeignActor = 9202;
        private const int InactiveActor = 9203;

        public BootstrapPolicySeedTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _probe = await _sql.CreateProbeDatabaseAsync("BootstrapSeeding");
            await SeedFixtureAsync();
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;
            SqlConnection.ClearAllPools();
            await _sql.DropProbeDatabaseAsync(_probe);
        }

        private CrossDbContext Db(int companyId = CompanyOne) => _sql.ContextFor(_probe!, companyId);

        private static IBootstrapAccessPolicySeeder Seeder(CrossDbContext db) =>
            new BootstrapAccessPolicySeeder(db, NullLogger<BootstrapAccessPolicySeeder>.Instance);

        private static BusinessContext Ctx(int companyId = CompanyOne) => new()
        {
            CompanyId = companyId, EmployeeId = Actor, UserId = "u", Source = BusinessContextSource.Http,
        };

        private static BootstrapSeedCommand Cmd(
            int companyId = CompanyOne, int actor = Actor,
            SeedExecutionMode mode = SeedExecutionMode.Execute, Guid? batch = null) => new()
            {
                CompanyId = companyId,
                ActorEmployeeId = actor,
                SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed,
                Reason = "B3 evidence",
                MigrationBatchId = batch ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Mode = mode,
            };

        // =========================================================================================
        // the plan itself
        // =========================================================================================

        [SkippableFact]
        public void The_plan_is_the_four_entries_B6_will_actually_read()
        {
            var plan = Seeder(null!).GetPlan();

            Assert.Equal(4, plan.Count);
            Assert.Equal(
                new[] { "Accounting.read", "Crm.edit", "Crm.read", "Inventory.read" },
                plan.Select(e => e.Identity).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [SkippableFact]
        public void Every_planned_entry_names_the_B6_call_site_that_will_read_it()
        {
            // The reconciliation rule. A seeded policy that no production path consumes is misleading
            // configuration and future cleanup debt, so naming the site is enforced rather than documented.
            foreach (var entry in Seeder(null!).GetPlan())
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.B6CallSite), entry.Identity + " names no call site");
                Assert.False(string.IsNullOrWhiteSpace(entry.SourceEvidence), entry.Identity + " cites no evidence");
                Assert.False(string.IsNullOrWhiteSpace(entry.Reason), entry.Identity + " gives no reason");
                Assert.Contains("AccessService.cs:", entry.B6CallSite);
            }
        }

        [SkippableFact]
        public void The_plan_is_deterministic_across_calls()
        {
            var first = Seeder(null!).GetPlan().Select(e => e.Identity + "|" + e.PolicyState).ToArray();
            var second = Seeder(null!).GetPlan().Select(e => e.Identity + "|" + e.PolicyState).ToArray();

            Assert.Equal(first, second);
        }

        [SkippableFact]
        public void The_plan_validates_and_contains_no_Never_and_no_POS_entry()
        {
            Assert.Empty(Seeder(null!).ValidatePlan());

            foreach (var entry in Seeder(null!).GetPlan())
            {
                Assert.False(NeverBootstrapOpen.Contains(entry.Scope, entry.ActionCode),
                    entry.Identity + " is Never-Bootstrap-Open and must not be planned");
                Assert.NotEqual("Pos", entry.Scope);
            }
        }

        [SkippableFact]
        public void The_plan_excludes_every_module_B6_does_not_convert()
        {
            // HR, Projects, Tasks and Communication already run through the action-aware
            // ModuleAccessServiceBase and never consult this reader in B6 — 28 of the 32 eligible actions.
            // Manufacturing delegates to Inventory's roles and has no access service of its own.
            var scopes = Seeder(null!).GetPlan().Select(e => e.Scope).Distinct().OrderBy(s => s).ToArray();

            Assert.Equal(new[] { "Accounting", "Crm", "Inventory" }, scopes);
        }

        // =========================================================================================
        // apply / idempotency
        // =========================================================================================

        [SkippableFact]
        public async Task Apply_inserts_the_four_policies_then_a_second_run_inserts_nothing()
        {
            Ready();

            await using (var db = Db())
            {
                var first = await Seeder(db).ApplyAsync(Ctx(), Cmd());

                Assert.Equal(SeedResultCode.Success, first.ResultCode);
                Assert.Equal(4, first.PlannedCount);
                Assert.Equal(4, first.InsertedCount);
                Assert.Equal(0, first.ExistingEquivalentCount);
                Assert.Equal(0, first.ConflictCount);
                Assert.False(first.IsDryRun);
            }

            await using (var db = Db())
            {
                var second = await Seeder(db).ApplyAsync(Ctx(), Cmd());

                Assert.Equal(SeedResultCode.AlreadyApplied, second.ResultCode);
                Assert.Equal(0, second.InsertedCount);
                Assert.Equal(4, second.ExistingEquivalentCount);
            }

            await using var fresh = Db();
            Assert.Equal(4, await fresh.BootstrapAccessPolicies
                .CountAsync(p => p.CompanyID == CompanyOne && p.IsActive));
        }

        [SkippableFact]
        public async Task A_dry_run_writes_nothing_but_reports_what_it_would_do()
        {
            Ready();
            await using var db = Db();

            var preview = await Seeder(db).PreviewAsync(Ctx(), Cmd(mode: SeedExecutionMode.Execute));

            // Preview FORCES DryRun even though the command said Execute — a preview must not be able to write
            // because a mode field was set wrong.
            Assert.True(preview.IsDryRun);
            Assert.Equal(SeedResultCode.DryRun, preview.ResultCode);
            Assert.Equal(4, preview.PlannedCount);
            Assert.Equal(0, preview.InsertedCount);
            Assert.Empty(preview.InsertedIdentities);

            await using var fresh = Db();
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync());
        }

        [SkippableFact]
        public async Task Two_companies_are_independent()
        {
            Ready();

            await using (var db = Db()) await Seeder(db).ApplyAsync(Ctx(CompanyOne), Cmd(CompanyOne));

            await using var fresh = Db();
            Assert.Equal(4, await fresh.BootstrapAccessPolicies.CountAsync(p => p.CompanyID == CompanyOne));
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync(p => p.CompanyID == CompanyTwo));
        }

        [SkippableFact]
        public async Task The_seed_reports_the_Never_and_POS_filtering_explicitly()
        {
            Ready();
            await using var db = Db();

            var result = await Seeder(db).PreviewAsync(Ctx(), Cmd());

            // The plan contains no Never or POS entry, so both counts are zero — and that is REPORTED rather
            // than inferred from silence. A seed that filtered silently could not be audited.
            Assert.Equal(0, result.SkippedNeverCount);
            Assert.Equal(0, result.SkippedPosCount);
            Assert.Empty(result.SkippedNeverIdentities);
            Assert.Empty(result.SkippedPosIdentities);
        }

        [SkippableFact]
        public async Task No_seeded_row_is_ever_a_Never_action_or_POS()
        {
            Ready();

            await using (var db = Db()) await Seeder(db).ApplyAsync(Ctx(), Cmd());

            await using var fresh = Db();
            var rows = await fresh.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == CompanyOne).ToListAsync();

            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.False(NeverBootstrapOpen.Contains(r.Scope, r.ActionCode),
                $"{r.Scope}.{r.ActionCode} is Never-Bootstrap-Open and must never be seeded"));
            Assert.DoesNotContain("Pos", rows.Select(r => r.Scope));
        }

        // =========================================================================================
        // refusals
        // =========================================================================================

        [SkippableFact]
        public async Task A_company_mismatch_is_refused_and_writes_nothing()
        {
            Ready();
            await using var db = Db();

            // Request-supplied company validated against the resolved context, never trusted.
            var result = await Seeder(db).ApplyAsync(Ctx(CompanyOne), Cmd(CompanyTwo));

            Assert.Equal(SeedResultCode.CompanyMismatch, result.ResultCode);
            await using var fresh = Db();
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync());
        }

        [SkippableTheory]
        [InlineData(0, SeedResultCode.ValidationFailed)]        // no company, and no fallback to 1
        [InlineData(999999, SeedResultCode.CompanyNotFound)]
        public async Task An_invalid_company_is_refused(int companyId, SeedResultCode expected)
        {
            Ready();
            await using var db = Db();

            var context = new BusinessContext
            { CompanyId = companyId == 0 ? CompanyOne : companyId, EmployeeId = Actor, UserId = "u" };

            var result = await Seeder(db).ApplyAsync(context, Cmd(companyId));

            Assert.Equal(expected, result.ResultCode);
            await using var fresh = Db();
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync());
        }

        [SkippableFact]
        public async Task An_inactive_or_foreign_actor_is_refused()
        {
            Ready();

            await using (var db = Db())
            {
                var inactive = await Seeder(db).ApplyAsync(Ctx(), Cmd(actor: InactiveActor));
                Assert.Equal(SeedResultCode.Forbidden, inactive.ResultCode);
            }

            await using (var db = Db())
            {
                var foreign = await Seeder(db).ApplyAsync(Ctx(), Cmd(actor: ForeignActor));
                Assert.Equal(SeedResultCode.Forbidden, foreign.ResultCode);
            }

            await using var fresh = Db();
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync());
        }

        [SkippableFact]
        public async Task An_unresolved_context_a_missing_reason_and_an_unknown_source_are_all_refused()
        {
            Ready();
            await using var db = Db();
            var seeder = Seeder(db);

            Assert.Equal(SeedResultCode.Forbidden,
                (await seeder.ApplyAsync(new BusinessContext { CompanyId = 0 }, Cmd())).ResultCode);

            Assert.Equal(SeedResultCode.ValidationFailed, (await seeder.ApplyAsync(Ctx(), new BootstrapSeedCommand
            {
                CompanyId = CompanyOne, ActorEmployeeId = Actor,
                SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed, Reason = "",
                Mode = SeedExecutionMode.Execute,
            })).ResultCode);

            Assert.Equal(SeedResultCode.ValidationFailed, (await seeder.ApplyAsync(Ctx(), new BootstrapSeedCommand
            {
                CompanyId = CompanyOne, ActorEmployeeId = Actor,
                SourceSystem = "NotAKnownSource", Reason = "x", Mode = SeedExecutionMode.Execute,
            })).ResultCode);

            await using var fresh = Db();
            Assert.Equal(0, await fresh.BootstrapAccessPolicies.CountAsync());
        }

        // =========================================================================================
        // history and conflict
        // =========================================================================================

        [SkippableFact]
        public async Task A_disabled_historical_policy_is_NOT_silently_reactivated()
        {
            Ready();

            await using (var db = Db()) await Seeder(db).ApplyAsync(Ctx(), Cmd());

            // Somebody turned this compatibility OFF deliberately. Re-running the seed is not a reason to undo it.
            await using (var db = Db())
            {
                var row = await db.BootstrapAccessPolicies
                    .FirstAsync(p => p.CompanyID == CompanyOne && p.Scope == "Accounting" && p.ActionCode == "read");
                row.IsActive = false;
                row.State = BootstrapPolicyStates.Disabled;
                await db.SaveChangesAsync();
            }

            await using (var db = Db())
            {
                var again = await Seeder(db).ApplyAsync(Ctx(), Cmd());

                Assert.Equal(SeedResultCode.Conflict, again.ResultCode);
                Assert.Contains(again.Conflicts, c => c.Contains("Accounting.read") && c.Contains("disabled"));
                Assert.Equal(0, again.InsertedCount);
            }

            await using var fresh = Db();
            Assert.False(await fresh.BootstrapAccessPolicies
                .AnyAsync(p => p.Scope == "Accounting" && p.ActionCode == "read" && p.IsActive));
        }

        [SkippableFact]
        public async Task A_differing_active_policy_is_reported_as_a_conflict_and_not_overwritten()
        {
            Ready();

            await using (var db = Db())
            {
                // An administrator's deliberate decision: this compatibility is Temporary, not Legacy.
                db.BootstrapAccessPolicies.Add(new BootstrapAccessPolicy
                {
                    CompanyID = CompanyOne, Scope = "Crm", ActionCode = "read",
                    State = BootstrapPolicyStates.Temporary,
                    EnabledAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30),
                    CreatedAt = DateTime.UtcNow, IsActive = true,
                    SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = Db())
            {
                var result = await Seeder(db).ApplyAsync(Ctx(), Cmd());

                Assert.Equal(SeedResultCode.Conflict, result.ResultCode);
                Assert.Contains(result.Conflicts, c => c.Contains("Crm.read") && c.Contains("Temporary"));

                // NOTHING is written when any conflict exists. A half-seeded company is worse than an unseeded
                // one: B6 would find compatibility for some actions and not others, and that would look deliberate.
                Assert.Equal(0, result.InsertedCount);
            }

            await using var fresh = Db();
            Assert.Equal(1, await fresh.BootstrapAccessPolicies.CountAsync(p => p.CompanyID == CompanyOne));
            Assert.Equal(BootstrapPolicyStates.Temporary,
                (await fresh.BootstrapAccessPolicies.FirstAsync(p => p.Scope == "Crm" && p.ActionCode == "read")).State);
        }

        [SkippableFact]
        public async Task Concurrent_equivalent_seeds_leave_exactly_four_active_policies()
        {
            Ready();

            using var barrier = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = Db();
                var seeder = Seeder(db);
                barrier.SignalAndWait();
                return await seeder.ApplyAsync(Ctx(), Cmd());
            })).ToArray();

            var results = await Task.WhenAll(tasks);

            // Exactly one run inserts; the rest report AlreadyApplied or Conflict. The unique index is the final
            // arbiter — four runs all read "not found" before any of them writes.
            Assert.Equal(1, results.Count(r => r.ResultCode == SeedResultCode.Success));

            await using var fresh = Db();
            Assert.Equal(4, await fresh.BootstrapAccessPolicies
                .CountAsync(p => p.CompanyID == CompanyOne && p.IsActive));
        }

        // =========================================================================================
        // persistence
        // =========================================================================================

        [SkippableFact]
        public async Task Audit_fields_source_reason_and_migration_batch_all_persist()
        {
            Ready();
            var batch = Guid.NewGuid();

            await using (var db = Db()) await Seeder(db).ApplyAsync(Ctx(), Cmd(batch: batch));

            await using var fresh = Db();
            var rows = await fresh.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == CompanyOne).ToListAsync();

            Assert.Equal(4, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(BootstrapPolicySources.BehaviourPreservingSeed, r.SourceSystem);
                Assert.Equal(batch, r.MigrationBatchId);
                Assert.Equal(Actor, r.EnabledBy);
                Assert.Equal(Actor, r.CreatedBy);
                Assert.NotNull(r.EnabledAt);
                Assert.NotEqual(default, r.CreatedAt);
                Assert.Contains("B3.v1", r.Reason!);          // the plan version, for provenance
                Assert.Contains("B3 evidence", r.Reason!);    // the caller's reason
                Assert.Null(r.ExpiresAt);                     // LegacyCompatibility is unbounded by design
                Assert.Equal(BootstrapPolicyStates.LegacyCompatibility, r.State);
                Assert.True(r.IsActive);
            });
        }

        [SkippableFact]
        public async Task The_seeded_policies_are_INERT_until_B6_because_the_reader_is_not_yet_consulted()
        {
            Ready();

            await using (var db = Db()) await Seeder(db).ApplyAsync(Ctx(), Cmd());

            await using var fresh = Db();
            var reader = new BootstrapAccessPolicyReader(fresh, NullLogger<BootstrapAccessPolicyReader>.Instance);

            // The reader HONOURS them — which is what B6 will rely on.
            var allowed = await reader.ResolveDecisionAsync(Ctx(), "Accounting", "read");
            Assert.True(allowed.IsAllowed);
            Assert.Equal(AuthorizationDecisionSources.BootstrapLegacyCompatibility, allowed.DecisionSource);
            Assert.True(allowed.IsBootstrap);
            Assert.False(allowed.IsDirectGrant);   // never mistaken for role authorization

            // And a Never action stays closed even with the company fully seeded.
            var denied = await reader.ResolveDecisionAsync(Ctx(), "Accounting", "post");
            Assert.False(denied.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NeverBootstrapOpen, denied.ReasonCode);
        }

        // =========================================================================================

        private async Task SeedFixtureAsync()
        {
            await using var db = Db();
            await db.Database.OpenConnectionAsync();
            try
            {
                await FillAsync(db, "dbo.Companies", () =>
                {
                    db.Companies.Add(new Companies { CompanyID = CompanyOne, CompanyName = "ZZ Seed One" });
                    db.Companies.Add(new Companies { CompanyID = CompanyTwo, CompanyName = "ZZ Seed Two" });
                });

                await FillAsync(db, "dbo.Employee", () =>
                {
                    void Add(int id, int company, bool active) => db.Employee.Add(new Employee
                    {
                        ID = id, EmpCompanyID = company, IsActive = active, UserId = "u" + id,
                        FullName = "ZZ " + id, FirstName = "ZZ", LastName = id.ToString(),
                    });

                    Add(Actor, CompanyOne, true);
                    Add(ForeignActor, CompanyTwo, true);
                    Add(InactiveActor, CompanyOne, false);
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
