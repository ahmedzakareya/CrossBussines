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
    // Stage 2A Batch A — THE RISK-037 CLOSURE EVIDENCE.
    //
    // RISK-037 is: PlatformRoleAssignments is the designated grant store and has no production writer, so
    // every module that reads it is bootstrap-open in practice. Closing it needs one thing proven —
    // that an authorized administrator can, THROUGH PRODUCTION CODE, create a grant that a real access
    // service then honours, and revoke it so the authorization goes away.
    //
    // THE RULE THIS FILE OBEYS, from A14: the grant is NEVER seeded directly. Every row under test is
    // created by IPlatformGrantWriter. A test that inserts the row itself proves the READER works and
    // says nothing about the writer — which is exactly the gap that let RISK-037 survive a whole stage.
    //
    // PROOF ACTION: HrActions.PayrollManage. Chosen because HrAccessService declares it NEVER
    // bootstrap-open. With a bootstrap-eligible action, revoking the only grant returns the company to
    // bootstrap-open and authorization comes BACK, making the revocation proof ambiguous. With
    // payroll-manage the chain is unambiguous: denied -> granted -> denied.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class PlatformGrantWriterAcceptanceTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        // Fixed ids inside an isolated probe. Safe because the probe is created and dropped per class, so
        // repeated runs cannot collide — the re-runnability rule.
        private const int AdminEmployee = 9001;      // company administrator (Identity role)
        private const int TargetEmployee = 9002;     // receives the grant
        private const int OtherEmployee = 9003;      // holds nothing
        private const int InactiveEmployee = 9004;
        private const int ForeignEmployee = 9005;    // company 2
        private const int ModuleAdminEmployee = 9006; // HR role holder, no Identity role

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;

            _probe = await _sql.CreateProbeDatabaseAsync("GrantWriter");
            await SeedAsync();
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;

            // Clear the pool FIRST. This class creates several CrossDbContext instances per test and does not
            // dispose them all, so ADO.NET keeps pooled connections open against the probe. DROP DATABASE then
            // blocks or fails, and the probe survives the run — 36 of them accumulated before this was found.
            //
            // ClearAllPools is safe here because the probe is this class's own database and the shared fixture
            // reconnects on demand.
            SqlConnection.ClearAllPools();

            await _sql.DropProbeDatabaseAsync(_probe);
        }

        public PlatformGrantWriterAcceptanceTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        private CrossDbContext Db(int companyId = CompanyOne) => _sql.ContextFor(_probe!, companyId);

        // ---------------------------------------------------------------------------------------------
        // wiring — the REAL services, assembled by hand rather than through DI
        // ---------------------------------------------------------------------------------------------

        /// <summary>Identity roles, stubbed. The one seam a test may control: it stands in for AspNetUserRoles.</summary>
        private sealed class StubIdentity : IPlatformAdminIdentity
        {
            private readonly Dictionary<string, string[]> _byUser = new(StringComparer.Ordinal);
            public StubIdentity Grant(string userId, params string[] roles) { _byUser[userId] = roles; return this; }

            public Task<IReadOnlyList<string>> RolesAsync(string? userId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<string>>(
                    userId != null && _byUser.TryGetValue(userId, out var r) ? r : Array.Empty<string>());
        }

        private static readonly StubIdentity Identity = new StubIdentity()
            .Grant("user-admin", "Admin")                 // company administrator
            .Grant("user-platform", "PlatformOps")        // platform security administrator
            .Grant("user-target")                         // no Identity role
            .Grant("user-other")
            .Grant("user-moduleadmin");                   // module authority only, via an HR grant

        /// <summary>
        /// The writer, built from the REAL role directory, the REAL HR access service and the REAL business event
        /// service. Only the Identity-role lookup and the business-context accessor are stubbed, because neither
        /// exists outside a request.
        /// </summary>
        private (IPlatformGrantWriter writer, HrAccessService hr, CrossDbContext db) Build(int companyId = CompanyOne)
        {
            var db = Db(companyId);
            var directory = new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance);
            var org = new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance);
            var hr = new HrAccessService(db, directory, org, NullLogger<HrAccessService>.Instance);

            var events = new BusinessEventService(
                db, new EntityRegistry(db), new StubContextAccessor(), NullLogger<BusinessEventService>.Instance);

            // The same four vocabularies Program.cs registers. The writer no longer carries the role map
            // itself; the modules publish it. Registering the real set keeps this acceptance test measuring
            // production behaviour rather than a test-local role list that could drift away from it.
            var vocabulary = new PlatformPermissionVocabularyRegistry(new IPlatformPermissionVocabulary[]
            {
                new HrPermissionVocabulary(), new ProjectsPermissionVocabulary(),
                new TasksPermissionVocabulary(), new CommunicationPermissionVocabulary(),
            });

            var writer = new PlatformGrantWriter(
                db, events, directory, new IModuleAccessService[] { hr }, Identity, vocabulary,
                NullLogger<PlatformGrantWriter>.Instance);

            return (writer, hr, db);
        }

        /// <summary>
        /// A resolvable context for the kernel's event service.
        ///
        /// FINDING, recorded because the first version of this stub threw and every create-path test failed on it:
        /// BusinessEventService.RecordAsync calls GetCurrentAsync UNCONDITIONALLY — supplying CompanyIdOverride and
        /// ActorEmployeeIdOverride does not exempt a caller. That is the same hard coupling recorded as HM-D58 for
        /// the sale/purchase path, and it applies to grant administration too: a grant cannot be written without a
        /// resolvable BusinessContext.
        ///
        /// Harmless in production, where administration is always an interactive request — and it is also a real
        /// constraint on any future background or migration writer, which is why it is written down here rather
        /// than worked around silently. The overrides still decide the stored company and actor.
        /// </summary>
        private sealed class StubContextAccessor : IBusinessContextAccessor
        {
            private static readonly BusinessContext Resolved = new()
            {
                CompanyId = CompanyOne,
                EmployeeId = AdminEmployee,
                UserId = "user-admin",
                Source = BusinessContextSource.Http,
            };

            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Resolved);

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult<BusinessContext?>(Resolved);
        }

        private static BusinessContext Actor(int employeeId, string userId, int companyId = CompanyOne) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = userId,
            Source = BusinessContextSource.Http,
        };

        private static BusinessContext CompanyAdmin => Actor(AdminEmployee, "user-admin");
        private static BusinessContext PlatformAdmin => Actor(AdminEmployee, "user-platform");

        private static CreatePlatformGrantCommand PayrollGrant(int principal = TargetEmployee, string? key = null) => new()
        {
            CompanyId = CompanyOne,
            Scope = EntityRegistry.ScopeHr,
            PrincipalType = PlatformPrincipalTypes.Employee,
            PrincipalId = principal,
            Role = HrRoles.PayrollOfficer,
            Reason = "Batch A acceptance",
            IdempotencyKey = key,
        };

        // =========================================================================================
        // A14.1-A14.7 — the production capability that was missing
        // =========================================================================================

        [SkippableFact]
        public async Task An_authorized_administrator_creates_a_grant_that_HR_then_honours_and_revoking_removes_it()
        {
            Ready();
            var (writer, hr, db) = Build();

            var target = Actor(TargetEmployee, "user-target");

            // ---- 1. BEFORE: payroll-manage is denied. It is never bootstrap-open, so this is a real deny. ----
            Assert.False(await hr.CanAsync(target, HrActions.PayrollManage),
                "payroll-manage must be denied before any grant exists");

            // ---- 2. the PRODUCTION writer creates the grant ----
            var created = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);
            Assert.NotNull(created.Grant);

            // ---- 3. the row is persisted, read back from a NEW context ----
            await using (var fresh = Db())
            {
                var row = await fresh.PlatformRoleAssignments.AsNoTracking()
                    .SingleAsync(r => r.ID == created.Grant!.Id);

                Assert.Equal(CompanyOne, row.CompanyID);
                Assert.Equal(EntityRegistry.ScopeHr, row.Scope);
                Assert.Equal(HrRoles.PayrollOfficer, row.Role);
                Assert.True(row.IsActive);

                // ---- 6. audit fields ----
                Assert.Equal(AdminEmployee, row.CreatedBy);
                Assert.NotEqual(default, row.CreatedAt);
                Assert.Equal(PlatformGrantSources.GrantWriter, row.SourceSystem);
                Assert.Equal("Batch A acceptance", row.Reason);
            }

            // ---- 4. the REAL access service now allows it: bootstrap/no-config -> ROLE-DRIVEN ----
            await using (var freshHr = Db())
            {
                var directory = new PlatformRoleDirectory(freshHr, NullLogger<PlatformRoleDirectory>.Instance);
                var service = new HrAccessService(freshHr, directory,
                    new OrgHierarchy(freshHr, NullLogger<OrgHierarchy>.Instance),
                    NullLogger<HrAccessService>.Instance);

                Assert.True(await service.CanAsync(target, HrActions.PayrollManage),
                    "the grant created by the production writer must be honoured by the real access service");
            }

            // ---- 7. BusinessEvents were written, in the same transaction as the grant ----
            await using (var fresh = Db())
            {
                var events = await fresh.BusinessEvents.AsNoTracking()
                    .Where(e => e.EntityType == EntityRegistry.PlatformRoleAssignment
                             && e.EntityId == created.Grant!.Id)
                    .Select(e => e.EventType)
                    .ToListAsync();

                Assert.Contains("PlatformRoleAssignment.Created", events);
            }

            // ---- 5. revocation removes authorization ----
            var revoked = await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            {
                GrantId = created.Grant!.Id,
                CompanyId = CompanyOne,
                Reason = "Batch A acceptance — revoke",
            });
            Assert.Equal(PlatformGrantOutcome.Success, revoked.Outcome);

            await using (var fresh = Db())
            {
                var directory = new PlatformRoleDirectory(fresh, NullLogger<PlatformRoleDirectory>.Instance);
                var service = new HrAccessService(fresh, directory,
                    new OrgHierarchy(fresh, NullLogger<OrgHierarchy>.Instance),
                    NullLogger<HrAccessService>.Instance);

                Assert.False(await service.CanAsync(target, HrActions.PayrollManage),
                    "revocation must remove the authorization");

                // no hard delete — the row IS the audit trail
                var row = await fresh.PlatformRoleAssignments.AsNoTracking()
                    .SingleAsync(r => r.ID == created.Grant!.Id);
                Assert.False(row.IsActive);
                Assert.NotNull(row.RevokedAt);
                Assert.Equal(AdminEmployee, row.RevokedBy);
                Assert.Contains("revoke", row.Reason!);

                var events = await fresh.BusinessEvents.AsNoTracking()
                    .Where(e => e.EntityId == created.Grant!.Id).Select(e => e.EventType).ToListAsync();
                Assert.Contains("PlatformRoleAssignment.Revoked", events);
            }
        }

        [SkippableFact]
        public async Task A_grant_closes_bootstrap_open_for_the_company_and_scope()
        {
            Ready();
            var (writer, _, _) = Build();

            var other = Actor(OtherEmployee, "user-other");

            // leave-manage IS bootstrap-eligible: with nothing configured, an employee with no HR role passes.
            await using (var before = Db())
            {
                var svc = new HrAccessService(before,
                    new PlatformRoleDirectory(before, NullLogger<PlatformRoleDirectory>.Instance),
                    new OrgHierarchy(before, NullLogger<OrgHierarchy>.Instance),
                    NullLogger<HrAccessService>.Instance);

                Assert.True(await svc.CanAsync(other, HrActions.LeaveManage,
                        new PermissionTarget { SubjectEmployeeId = OtherEmployee }),
                    "with nothing configured the module is bootstrap-open, which is the state Batch A ends");
            }

            var created = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);

            // Now HR IS configured for company 1, so bootstrap-open is over and role rules apply. The employee
            // who holds nothing loses the compatibility pass — that transition is the whole point of the store.
            await using (var after = Db())
            {
                var svc = new HrAccessService(after,
                    new PlatformRoleDirectory(after, NullLogger<PlatformRoleDirectory>.Instance),
                    new OrgHierarchy(after, NullLogger<OrgHierarchy>.Instance),
                    NullLogger<HrAccessService>.Instance);

                Assert.False(await svc.CanAsync(other, HrActions.LeaveManage,
                        new PermissionTarget { SubjectEmployeeId = OtherEmployee }),
                    "once a grant exists the module is role-driven, so an employee with no role is denied");
            }

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        // =========================================================================================
        // A14.8 — idempotency
        // =========================================================================================

        [SkippableFact]
        public async Task A_retry_with_the_same_idempotency_key_returns_the_original_and_creates_no_second_row()
        {
            Ready();
            var (writer, _, _) = Build();

            var first = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant(key: "batch-a-key-1"));
            Assert.Equal(PlatformGrantOutcome.Success, first.Outcome);

            var replay = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant(key: "batch-a-key-1"));
            Assert.Equal(PlatformGrantOutcome.IdempotentReplay, replay.Outcome);
            Assert.Equal(first.Grant!.Id, replay.Grant!.Id);

            await using var fresh = Db();
            Assert.Equal(1, await fresh.PlatformRoleAssignments
                .CountAsync(r => r.IdempotencyKey == "batch-a-key-1"));

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = first.Grant.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task The_same_key_with_a_DIFFERENT_payload_is_a_conflict_not_a_silent_replay()
        {
            Ready();
            var (writer, _, _) = Build();

            var first = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant(key: "batch-a-key-2"));
            Assert.Equal(PlatformGrantOutcome.Success, first.Outcome);

            var different = new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne,
                Scope = EntityRegistry.ScopeHr,
                PrincipalId = TargetEmployee,
                Role = HrRoles.HrViewer,          // different role, same key
                IdempotencyKey = "batch-a-key-2",
            };

            // Returning the original would tell the caller their NEW intent succeeded. It did not.
            var conflict = await writer.CreateGrantAsync(CompanyAdmin, different);
            Assert.Equal(PlatformGrantOutcome.Conflict, conflict.Outcome);

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = first.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task A_duplicate_active_grant_is_refused()
        {
            Ready();
            var (writer, _, _) = Build();

            var first = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Success, first.Outcome);

            var duplicate = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Duplicate, duplicate.Outcome);

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = first.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });

            // Revoke-then-regrant must work — that is why the unique index is FILTERED on IsActive.
            var again = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Success, again.Outcome);
            Assert.NotEqual(first.Grant.Id, again.Grant!.Id);

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = again.Grant.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        // =========================================================================================
        // A14.9-A14.13 — the refusals. Each must leave NO row and NO event.
        // =========================================================================================

        [SkippableFact]
        public async Task An_unauthorized_actor_creates_no_row_and_no_event()
        {
            Ready();
            var (writer, _, _) = Build();

            var nobody = Actor(OtherEmployee, "user-other");   // no Identity role, no HR grant
            var before = await CountRowsAndEventsAsync();

            var result = await writer.CreateGrantAsync(nobody, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);

            Assert.Equal(before, await CountRowsAndEventsAsync());
        }

        [SkippableFact]
        public async Task A_cross_company_grant_is_denied_without_platform_authority()
        {
            Ready();
            var (writer, _, _) = Build();

            var foreign = new CreatePlatformGrantCommand
            {
                CompanyId = CompanyTwo,                 // the admin's context resolves to company 1
                Scope = EntityRegistry.ScopeHr,
                PrincipalId = ForeignEmployee,
                Role = HrRoles.PayrollOfficer,
            };

            var result = await writer.CreateGrantAsync(CompanyAdmin, foreign);
            Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);
            Assert.Contains("platform security", result.Message);

            await using var fresh = Db();
            Assert.False(await fresh.PlatformRoleAssignments.AnyAsync(r => r.CompanyID == CompanyTwo));
        }

        [SkippableFact]
        public async Task Self_escalation_is_refused_below_platform_authority()
        {
            Ready();
            var (writer, _, _) = Build();

            // RISK-039. The company administrator granting THEMSELVES a payroll role is the shortest path from
            // "may administer" to "may do anything", and it needs a second person.
            var result = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant(principal: AdminEmployee));

            Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);
            Assert.Contains("yourself", result.Message);

            await using var fresh = Db();
            Assert.False(await fresh.PlatformRoleAssignments.AnyAsync(r => r.PrincipalId == AdminEmployee));
        }

        [SkippableFact]
        public async Task A_module_administrator_may_not_confer_a_role_they_do_not_hold()
        {
            Ready();
            var (writer, _, _) = Build();

            // Establish HR configuration first, so module-level authority is admissible at all (a module admin
            // claim is refused under bootstrap-open by design). The module admin holds HrManager only.
            var seedGrant = await writer.CreateGrantAsync(PlatformAdmin, new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne,
                Scope = EntityRegistry.ScopeHr,
                PrincipalId = ModuleAdminEmployee,
                Role = HrRoles.HrManager,
                Reason = "module admin seed",
            });
            Assert.Equal(PlatformGrantOutcome.Success, seedGrant.Outcome);

            var moduleAdmin = Actor(ModuleAdminEmployee, "user-moduleadmin");

            // HrManager holds organization-manage, so the actor IS a module administrator for Hr. But
            // PayrollOfficer is a role they do not hold, and money is a separate right.
            var result = await writer.CreateGrantAsync(moduleAdmin, PayrollGrant());

            Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);
            Assert.Contains("may never exceed the grantor", result.Message);

            await writer.RevokeGrantAsync(PlatformAdmin, new RevokePlatformGrantCommand
            { GrantId = seedGrant.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task Module_level_authority_is_NOT_admissible_under_bootstrap_open()
        {
            Ready();
            var (writer, _, _) = Build();

            // A5 rule 6 / RISK-043. With nothing configured, HrAccessService answers TRUE for organization-manage
            // under bootstrap-open compatibility. Treating that as administrative authority would let any employee
            // of an unconfigured company grant themselves a role — the FIRST grant must come from a real
            // administrator.
            var moduleAdmin = Actor(ModuleAdminEmployee, "user-moduleadmin");

            var result = await writer.CreateGrantAsync(moduleAdmin, PayrollGrant());

            Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);
            Assert.Contains("bootstrap-open", result.Message);
        }

        [SkippableFact]
        public async Task An_inactive_employee_cannot_receive_a_grant()
        {
            Ready();
            var (writer, _, _) = Build();

            var result = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant(principal: InactiveEmployee));

            Assert.Equal(PlatformGrantOutcome.ValidationFailed, result.Outcome);
            Assert.Contains(result.Errors, e => e.Contains("inactive"));
        }

        [SkippableFact]
        public async Task An_expired_grant_does_not_authorize()
        {
            Ready();
            var (writer, _, _) = Build();

            var created = await writer.CreateGrantAsync(CompanyAdmin, new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne,
                Scope = EntityRegistry.ScopeHr,
                PrincipalId = TargetEmployee,
                Role = HrRoles.PayrollOfficer,
                ValidFrom = DateTime.UtcNow.AddDays(-10),
                ValidTo = DateTime.UtcNow.AddDays(-1),    // already over
                Reason = "expired window",
            });
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);

            await using var fresh = Db();
            var svc = new HrAccessService(fresh,
                new PlatformRoleDirectory(fresh, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(fresh, NullLogger<OrgHierarchy>.Instance),
                NullLogger<HrAccessService>.Instance);

            Assert.False(await svc.CanAsync(Actor(TargetEmployee, "user-target"), HrActions.PayrollManage),
                "an expired grant must not authorize");

            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task A_revoked_grant_cannot_be_brought_back_by_a_validity_change()
        {
            Ready();
            var (writer, _, _) = Build();

            var created = await writer.CreateGrantAsync(CompanyAdmin, PayrollGrant());
            await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant!.Id, CompanyId = CompanyOne, Reason = "revoked" });

            var attempt = await writer.UpdateValidityAsync(CompanyAdmin, new UpdateGrantValidityCommand
            {
                GrantId = created.Grant.Id,
                CompanyId = CompanyOne,
                ValidTo = DateTime.UtcNow.AddYears(1),
                Reason = "trying to reactivate",
            });

            // Reactivation by side effect is the thing this refuses.
            Assert.Equal(PlatformGrantOutcome.Conflict, attempt.Outcome);

            var second = await writer.RevokeGrantAsync(CompanyAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant.Id, CompanyId = CompanyOne, Reason = "again" });
            Assert.Equal(PlatformGrantOutcome.AlreadyRevoked, second.Outcome);
        }

        [SkippableFact]
        public async Task An_unknown_role_or_scope_never_creates_a_silent_no_op_row()
        {
            Ready();
            var (writer, _, _) = Build();

            var badRole = await writer.CreateGrantAsync(CompanyAdmin, new CreatePlatformGrantCommand
            { CompanyId = CompanyOne, Scope = EntityRegistry.ScopeHr, PrincipalId = TargetEmployee, Role = "NotARole" });
            Assert.Equal(PlatformGrantOutcome.ValidationFailed, badRole.Outcome);

            var badScope = await writer.CreateGrantAsync(CompanyAdmin, new CreatePlatformGrantCommand
            { CompanyId = CompanyOne, Scope = "NotAScope", PrincipalId = TargetEmployee, Role = HrRoles.PayrollOfficer });
            Assert.Equal(PlatformGrantOutcome.ValidationFailed, badScope.Outcome);

            var pos = await writer.CreateGrantAsync(CompanyAdmin, new CreatePlatformGrantCommand
            { CompanyId = CompanyOne, Scope = EntityRegistry.ScopePos, PrincipalId = TargetEmployee, Role = "Cashier" });
            Assert.Equal(PlatformGrantOutcome.ValidationFailed, pos.Outcome);

            var unsupportedPrincipal = await writer.CreateGrantAsync(CompanyAdmin, new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne, Scope = EntityRegistry.ScopeHr,
                PrincipalType = PlatformPrincipalTypes.CustomerContact,
                PrincipalId = TargetEmployee, Role = HrRoles.PayrollOfficer,
            });
            Assert.Equal(PlatformGrantOutcome.ValidationFailed, unsupportedPrincipal.Outcome);

            await using var fresh = Db();
            Assert.False(await fresh.PlatformRoleAssignments.AnyAsync());
        }

        [SkippableFact]
        public async Task A_foreign_company_grant_is_indistinguishable_from_not_found()
        {
            Ready();
            var (writer, _, _) = Build();

            var created = await writer.CreateGrantAsync(PlatformAdmin, PayrollGrant());
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);

            // Same id, wrong company. Must be NotFound — confirming it exists elsewhere is a cross-tenant leak.
            var wrongCompany = await writer.GetGrantAsync(PlatformAdmin, created.Grant!.Id, CompanyTwo);
            Assert.Equal(PlatformGrantOutcome.NotFound, wrongCompany.Outcome);

            var nonexistent = await writer.GetGrantAsync(PlatformAdmin, 999999, CompanyOne);
            Assert.Equal(PlatformGrantOutcome.NotFound, nonexistent.Outcome);
            Assert.Equal(wrongCompany.Outcome, nonexistent.Outcome);

            await writer.RevokeGrantAsync(PlatformAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task A_branch_scoped_grant_keeps_its_branch_exactly()
        {
            Ready();
            var (writer, _, _) = Build();

            var created = await writer.CreateGrantAsync(PlatformAdmin, new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne,
                Scope = EntityRegistry.ScopeHr,
                PrincipalId = TargetEmployee,
                Role = HrRoles.HrOfficer,
                ScopeBranchId = 501,
                Reason = "branch scoped",
            });
            Assert.Equal(PlatformGrantOutcome.Success, created.Outcome);

            await using var fresh = Db();
            var row = await fresh.PlatformRoleAssignments.AsNoTracking().SingleAsync(r => r.ID == created.Grant!.Id);
            Assert.Equal(501, row.ScopeBranchId);   // never widened to company scope

            // A branch in ANOTHER company is refused.
            var foreignBranch = await writer.CreateGrantAsync(PlatformAdmin, new CreatePlatformGrantCommand
            {
                CompanyId = CompanyOne, Scope = EntityRegistry.ScopeHr, PrincipalId = TargetEmployee,
                Role = HrRoles.HrViewer, ScopeBranchId = 502,
            });
            Assert.Equal(PlatformGrantOutcome.ValidationFailed, foreignBranch.Outcome);

            await writer.RevokeGrantAsync(PlatformAdmin, new RevokePlatformGrantCommand
            { GrantId = created.Grant!.Id, CompanyId = CompanyOne, Reason = "cleanup" });
        }

        [SkippableFact]
        public async Task A_worker_or_system_context_may_not_administer_grants()
        {
            Ready();
            var (writer, _, _) = Build();

            foreach (var context in new[]
            {
                BusinessContext.ForWorker(CompanyOne),
                BusinessContext.ForSystem(CompanyOne),
            })
            {
                var result = await writer.CreateGrantAsync(context, PayrollGrant());
                Assert.Equal(PlatformGrantOutcome.Forbidden, result.Outcome);
            }

            await using var fresh = Db();
            Assert.False(await fresh.PlatformRoleAssignments.AnyAsync());
        }

        // =========================================================================================
        // helpers
        // =========================================================================================

        /// <summary>
        /// Fills one identity table with explicit ids, then turns the setting straight back off.
        ///
        /// Every added entity has its still-null string properties filled with a placeholder first. These are old
        /// tables with many NOT NULL nvarchar columns and no defaults; enumerating them in a test would be a list
        /// that goes stale the first time a column is added, and the failure would look like a grant defect.
        /// </summary>
        private static async Task IdentityInsertAsync(CrossDbContext db, string table, Action add)
        {
            await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} ON;");
            try
            {
                add();
                FillRequiredStrings(db);
                await db.SaveChangesAsync();
            }
            finally
            {
                // OFF in a finally: leaving it ON would let a later unrelated insert supply its own key, and the
                // setting outlives this method on a held-open connection.
                await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} OFF;");
            }
        }

        /// <summary>
        /// Gives every pending entity's null string property a placeholder. Only NULL ones, so the values the test
        /// deliberately set (UserId, FullName) are never overwritten.
        /// </summary>
        private static void FillRequiredStrings(CrossDbContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
            {
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.ClrType != typeof(string)) continue;
                    if (property.Metadata.IsNullable) continue;
                    if (property.CurrentValue != null) continue;

                    property.CurrentValue = "ZZ";
                }
            }
        }

        private async Task<(int rows, int events)> CountRowsAndEventsAsync()
        {
            await using var db = Db();
            return (await db.PlatformRoleAssignments.CountAsync(), await db.BusinessEvents.CountAsync());
        }

        private async Task SeedAsync()
        {
            await using var db = Db();

            // The slice-2 columns, applied to the probe. CreateProbeDatabaseAsync builds the schema from the EF
            // model, which already carries them — this asserts that rather than assuming it, because a missing
            // column here would surface as an unrelated failure in every test below.
            await using (var connection = new SqlConnection(_probe!.ConnectionString))
            {
                await connection.OpenAsync();
                await using var check = connection.CreateCommand();
                check.CommandText =
                    "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PlatformRoleAssignments') " +
                    "AND name IN ('RevokedAt','RevokedBy','Reason','SourceSystem','MigrationBatchId','IdempotencyKey');";
                var present = Convert.ToInt32(await check.ExecuteScalarAsync());
                Assert.Equal(6, present);
            }

            // Companies, Branches and Employee all have IDENTITY primary keys, so EF cannot insert the fixed ids
            // these tests use. IDENTITY_INSERT is session-scoped and SQL Server permits it on ONE table at a time,
            // so each table is filled and saved in its own window on a single held-open connection.
            //
            // Fixed ids are worth this: a failure that says "employee 9002 was denied" is legible, and one that
            // says "employee 41 was denied" is not.
            await db.Database.OpenConnectionAsync();
            try
            {
                await IdentityInsertAsync(db, "dbo.Companies", () =>
                {
                    db.Companies.Add(new Companies { CompanyID = CompanyOne, CompanyName = "ZZ Acceptance One" });
                    db.Companies.Add(new Companies { CompanyID = CompanyTwo, CompanyName = "ZZ Acceptance Two" });
                });

                await IdentityInsertAsync(db, "dbo.Branches", () =>
                {
                    db.Branches.Add(new Branch
                    { ID = 501, CompanyID = CompanyOne, Name = "ZZ Branch One", NameAr = "ZZ فرع ١" });
                    db.Branches.Add(new Branch
                    { ID = 502, CompanyID = CompanyTwo, Name = "ZZ Branch Two", NameAr = "ZZ فرع ٢" });
                });

                await IdentityInsertAsync(db, "dbo.Employee", () =>
                {
                    void Add(int id, int companyId, bool active, string userId) =>
                        db.Employee.Add(new Employee
                        {
                            ID = id, EmpCompanyID = companyId, IsActive = active, UserId = userId,
                            FullName = "ZZ Employee " + id, FirstName = "ZZ", LastName = id.ToString(),
                        });

                    Add(AdminEmployee, CompanyOne, true, "user-admin");
                    Add(TargetEmployee, CompanyOne, true, "user-target");
                    Add(OtherEmployee, CompanyOne, true, "user-other");
                    Add(InactiveEmployee, CompanyOne, false, "user-inactive");
                    Add(ForeignEmployee, CompanyTwo, true, "user-foreign");
                    Add(ModuleAdminEmployee, CompanyOne, true, "user-moduleadmin");
                });
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
