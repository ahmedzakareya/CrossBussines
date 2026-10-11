using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // INVENTORY APPROVAL — SOURCE-AWARE TASK VISIBILITY.
    //
    // An inventory approval task is UNASSIGNED, and it has to be: approval authority is a ROLE right —
    // whoever holds InventoryManager in the company, minus the requester — so no individual can be named
    // when the task is created. It therefore carries AssigneeEmployeeId = 0 and CreatedByEmployeeId = 0,
    // and generic task scope alone gets it wrong in two directions that matter:
    //
    //   * a TasksViewer/TasksAdministrator holding COMPANY breadth sees the approval task while having no
    //     say whatever over inventory — an unauthorized disclosure of what is being bought, for how much;
    //   * the REQUESTER sees their own submission on a task surface, straight through the separation of
    //     duties that ApprovalInboxAsync exists to enforce.
    //
    // Both are closed here, and both are asserted as a PAIR — the refusal next to the identical call that
    // succeeds — because a provider that returned nothing at all would satisfy every negative on its own.
    //
    // WHAT THIS FILE DOES NOT COVER, and where it went. Everything here wires the authorizer WITHOUT the
    // entrance service, so it measures narrowing alone: scope INTERSECT source eligibility, which can only
    // ever remove rows. On that composition an eligible InventoryManager holding no task grant still does
    // not reach the task, because Own/Team match neither assignee 0 nor creator 0 — B6 keeps that claim,
    // and it is now a statement about THIS LAYER rather than about the product.
    //
    // The product moved on. ITaskSourceReadEntrance admits eligible approvers to exactly their own
    // actionable approvals, and TaskSourceReadEntranceTests owns that end-to-end proof. The reason the two
    // files stay separate is that they answer different questions, and the older one is what stops the
    // newer mechanism from being confused with a relaxation of this one.
    //
    // NOTHING HERE RESTATES INVENTORY POLICY. The provider forwards to ApprovalInboxAsync — the same
    // method the inventory approval inbox screen uses — and that one call already answers company,
    // InventoryManager role, Pending state and requester exclusion. The G-series pins that the provider
    // keeps no second copy of any of it.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1: a fixture built in company 1 cannot tell "the company argument was
    // used" from "the answer happened to be right".
    // ================================================================================================
    public class InventoryApprovalTaskVisibilityTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Requester = 5;
        private const int Manager = 6;
        private const int Keeper = 7;          // an inventory role that is NOT approval authority
        private const int Clerk = 8;           // no inventory role at all
        private const int ForeignManager = 9;

        // ------------------------------------------------------------------------------------------
        // Wiring. Only the context accessor is a stub — there is no HTTP request in a unit test. Every
        // other object below is the production one.
        // ------------------------------------------------------------------------------------------

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static BusinessContext CtxNoEmployee(int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = null, UserId = "svc",
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        // ApprovalInboxAsync takes its BusinessContext as an argument and reaches no stock, procurement or
        // notification path, so those four dependencies are genuinely unused here rather than stubbed away.
        private static IInventoryApprovalService Approvals(PlatformTestHost host)
            => new InventoryApprovalService(host.Db, null!, null!, null!, null!);

        private static InventoryApprovalTaskVisibilityProvider Provider(PlatformTestHost host)
            => new(Approvals(host));

        private static ITaskSourceVisibility Sources(PlatformTestHost host)
            => new TaskSourceVisibility(
                new ITaskSourceVisibilityProvider[] { Provider(host) },
                NullLogger<TaskSourceVisibility>.Instance);

        private static ITaskReadAuthorizer AuthorizerFor(PlatformTestHost host, BusinessContext? actor)
        {
            var accessor = actor == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(actor);
            var tasksAccess = new TasksAccessService(
                host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                () => new PlatformPermissionProvider(
                    host.Registry(), Adapters(host), NullLogger<PlatformPermissionProvider>.Instance),
                NullLogger<TasksAccessService>.Instance);
            return new TaskReadAuthorizer(accessor, tasksAccess, Sources(host));
        }

        // THE INVENTORY ADAPTER IS REGISTERED ON PURPOSE. EntityRegistry gives InventoryApproval the
        // Inventory permission scope, and with no adapter for that scope the platform denies View outright
        // — every negative below would then pass for a reason that has nothing to do with what is being
        // tested. With it registered, View maps to inventory `read`, which this module grants to any
        // authenticated employee of a configured company. So the entity gate says YES to everyone here,
        // and any difference between two callers is the source provider talking.
        private static IModulePermissionAdapter[] Adapters(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var inventory = new InventoryAccessService(
                host.Db, http,
                new BusinessContextAccessor(new BusinessContextFactory(
                    http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance)),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<InventoryAccessService>.Instance);
            return new IModulePermissionAdapter[]
            {
                new DefaultPermissionAdapter(),
                new InventoryPermissionAdapter(new IModuleAccessService[] { inventory }),
            };
        }

        private static ITaskService TasksFor(PlatformTestHost host, BusinessContext? actor)
        {
            var events = new BusinessEventService(
                host.Db, host.Registry(), new StubContextAccessor(Ctx(Manager, CompanyA)),
                NullLogger<BusinessEventService>.Instance);
            return new TaskService(
                host.Db, new TaskCalendarEventPublisher(events),
                new TaskNotificationService(host.Db, new UatRecordingNotificationService(host.Db)),
                AuthorizerFor(host, actor));
        }

        private static ITaskReportService ReportsFor(PlatformTestHost host, BusinessContext? actor)
            => new TaskReportService(host.Db, new EmployeeCostService(host.Db), AuthorizerFor(host, actor));

        // The per-task gate, wired with the SAME source visibility the list uses. Without the sources
        // factory the detail page would be weaker than the list it opens from.
        private static ITasksAccessService AccessFor(PlatformTestHost host)
            => new TasksAccessService(
                host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                () => new PlatformPermissionProvider(
                    host.Registry(), Adapters(host), NullLogger<PlatformPermissionProvider>.Instance),
                NullLogger<TasksAccessService>.Instance,
                () => Sources(host));

        private static Task<List<TaskRowDto>> ListAsync(PlatformTestHost host, int employeeId, int companyId)
            => TasksFor(host, Ctx(employeeId, companyId))
                .GetTasksAsync(companyId, "all", employeeId, null, null, null, page: 1, pageSize: 200);

        // ------------------------------------------------------------------------------------------
        // Fixture
        // ------------------------------------------------------------------------------------------

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "u" + id,
        };

        private static async Task<PlatformTestHost> SeedAsync()
        {
            var host = new PlatformTestHost(companyId: CompanyA);
            host.Seed.Employee.AddRange(
                Emp(Requester, CompanyA), Emp(Manager, CompanyA), Emp(Keeper, CompanyA),
                Emp(Clerk, CompanyA), Emp(ForeignManager, CompanyB));
            await host.Seed.SaveChangesAsync();

            // Inventory RBAC is CONFIGURED in both companies, so nothing below is answered by a
            // bootstrap-open shortcut inside the inventory module.
            await RoleAsync(host, CompanyA, Manager, "InventoryManager");
            await RoleAsync(host, CompanyA, Keeper, "WarehouseKeeper");
            await RoleAsync(host, CompanyB, ForeignManager, "InventoryManager");
            return host;
        }

        private static async Task RoleAsync(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = companyId, EmployeeId = employeeId, Role = role, CreatedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();
        }

        private static async Task GrantTasksAsync(PlatformTestHost host, int employeeId, string role, int companyId)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = EntityRegistry.ScopeTasks,
                PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId,
                Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // A pending approval, plus the UNASSIGNED task an orchestration run would produce for it.
        private static async Task<(int ApprovalId, int TaskId)> ApprovalWithTaskAsync(
            PlatformTestHost host, int companyId = CompanyA, string status = "Pending", int requester = Requester)
        {
            var approval = new InventoryApproval
            {
                CompanyID = companyId, DocType = "PurchaseOrder", Amount = 500m, PayloadJson = "{}",
                Status = status, RequestedByEmployeeId = requester, RequestedAt = DateTime.UtcNow,
                DecidedByEmployeeId = status == "Pending" ? null : Manager,
                DecidedAt = status == "Pending" ? null : DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(approval);
            await host.Seed.SaveChangesAsync();

            var task = new TaskItem
            {
                CompanyId = companyId, Title = "اعتماد مخزني", Description = "PO 500",
                Status = "New", Priority = "Normal",
                AssigneeEmployeeId = 0, CreatedByEmployeeId = 0,        // UNASSIGNED — the whole point
                EntityType = EntityRegistry.InventoryApproval, EntityId = approval.ID,
                CreatedAt = DateTime.UtcNow, DueDate = DateTime.Today.AddDays(-2),
            };
            host.Seed.TaskItems.Add(task);
            await host.Seed.SaveChangesAsync();
            return (approval.ID, task.ID);
        }

        // ============================================================================================
        // A. THE PROVIDER ITSELF.
        // ============================================================================================

        [Fact]
        public async Task A1_the_provider_answers_for_the_inventory_approval_entity_type()
        {
            using var host = await SeedAsync();
            Assert.Equal(EntityRegistry.InventoryApproval, Provider(host).EntityType);
            Assert.Equal("InventoryApproval", EntityRegistry.InventoryApproval);
        }

        [Fact]
        public void A2_the_provider_is_registered_in_the_composition_root()
        {
            string program = string.Join("\n", File.ReadAllLines(
                    Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"))
                .Where(l => !l.TrimStart().StartsWith("//")));

            Assert.Contains(
                "ITaskSourceVisibilityProvider, CrossBuy.BL.Platform.InventoryApprovalTaskVisibilityProvider>",
                program);
            // …and the project billing provider is still registered BESIDE it. ITaskSourceVisibilityProvider
            // is a multi-registration: a change that replaced rather than added would silently unguard every
            // billing approval task in the product.
            Assert.DoesNotContain(
                "ITaskSourceVisibilityProvider, CrossBuy.BL.Platform.ProjectBillingTaskVisibilityProvider>",
                program);
        }

        [Fact]
        public async Task A3_an_eligible_manager_sees_the_pending_approval_id()
        {
            using var host = await SeedAsync();
            var (approvalId, _) = await ApprovalWithTaskAsync(host);

            Assert.Contains(approvalId, await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyA)));
        }

        [Fact]
        public async Task A4_the_provider_returns_exactly_what_the_approval_inbox_returns()
        {
            using var host = await SeedAsync();
            var (first, _) = await ApprovalWithTaskAsync(host);
            var (second, _) = await ApprovalWithTaskAsync(host);
            var ctx = Ctx(Manager, CompanyA);

            var fromProvider = await Provider(host).VisibleEntityIdsAsync(ctx);
            var (isManager, fromInbox) = await Approvals(host).ApprovalInboxAsync(ctx, Manager);

            // Identical BY CONSTRUCTION, not by coincidence — and asserted against a non-empty expected set,
            // so "both happened to be empty" cannot pass this.
            Assert.True(isManager);
            Assert.Equal(new[] { first, second }.OrderBy(x => x), fromProvider.OrderBy(x => x));
            Assert.Equal(fromInbox.Select(a => a.ID).OrderBy(x => x), fromProvider.OrderBy(x => x));
        }

        [Fact]
        public async Task A5_a_caller_with_no_employee_identity_fails_closed()
        {
            using var host = await SeedAsync();
            await ApprovalWithTaskAsync(host);

            Assert.Empty(await Provider(host).VisibleEntityIdsAsync(CtxNoEmployee(CompanyA)));
        }

        [Fact]
        public async Task A6_no_pending_approval_yields_an_empty_set_not_everything()
        {
            using var host = await SeedAsync();
            Assert.Empty(await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyA)));
        }

        [Fact]
        public async Task A7_the_registry_definition_routes_the_approval_to_the_inventory_module()
        {
            using var host = await SeedAsync();
            Assert.True(host.Registry().TryGetDefinition(EntityRegistry.InventoryApproval, out var definition));

            // The permission SCOPE is the load-bearing field, and it is asserted rather than assumed: it is
            // what makes TasksAccessService ask the INVENTORY adapter whether the linked record may be
            // viewed. Pointed at the wrong scope, an approval would be answered by another module's roles —
            // or, at ScopeNone, by the default adapter's "any authenticated user may view".
            Assert.Equal(EntityRegistry.ScopeInventory, definition!.PermissionScope);
            Assert.Equal("Inventory", definition.Module);

            // An approval is a work item, not a record people browse to. It carries no timeline, comments or
            // files of its own — the document it approves has those — and it stays out of the record picker.
            Assert.False(definition.ListedInRecordPicker);
            Assert.False(definition.SupportsTimeline);
        }

        [Fact]
        public async Task A8_the_registry_resolves_an_approval_only_within_its_own_company()
        {
            using var host = await SeedAsync();
            var (approvalId, _) = await ApprovalWithTaskAsync(host, CompanyA);

            // Resolution is not decoration: PlatformPermissionProvider decides company membership by asking
            // the registry to resolve the record, so an entity that resolves for the WRONG company would let
            // a foreign caller past the tenancy gate before any module role is consulted — and one that
            // resolves for nobody would deny the eligible approver their own detail page.
            var mine = await host.Registry().ResolveAsync(
                EntityRegistry.InventoryApproval, approvalId, Ctx(Manager, CompanyA));
            var theirs = await host.Registry().ResolveAsync(
                EntityRegistry.InventoryApproval, approvalId, Ctx(ForeignManager, CompanyB));

            Assert.True(mine.Found);
            Assert.Contains("PurchaseOrder", mine.Label);
            Assert.False(theirs.Found);
        }

        // An inbox that answers "not an approver" while still handing back rows. No production path does
        // this — which is exactly why the provider's own gate has to be tested against something that does.
        private sealed class DisagreeingInbox : IInventoryApprovalService
        {
            private readonly bool _isManager;
            private readonly List<InventoryApproval> _rows;
            public int Calls { get; private set; }
            public int LastApprover { get; private set; } = -1;

            public DisagreeingInbox(bool isManager, params int[] ids)
            {
                _isManager = isManager;
                _rows = ids.Select(id => new InventoryApproval { ID = id, Status = "Pending" }).ToList();
            }

            public Task<(bool isInventoryManager, List<InventoryApproval> pending)> ApprovalInboxAsync(
                BusinessContext context, int approverEmployeeId, CancellationToken cancellationToken = default)
            {
                Calls++;
                LastApprover = approverEmployeeId;
                return Task.FromResult((_isManager, _rows));
            }

            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"The visibility provider must not call IInventoryApprovalService.{m}.");
            public Task<bool> RequiresApprovalAsync(decimal a) => throw No();
            public Task<int> SubmitAsync(string a, decimal b, object c, int? d) => throw No();
            public Task<List<InventoryApproval>> PendingAsync() => throw No();
            public Task<List<InventoryApproval>> RecentAsync(int a = 50) => throw No();
            public Task<(bool ok, string? error)> ApproveAsync(int a, int b, string? c) => throw No();
            public Task<(bool ok, string? error)> RejectAsync(int a, int b, string? c) => throw No();
        }

        [Fact]
        public async Task A9_the_provider_believes_the_authority_flag_over_the_rows()
        {
            var refused = new DisagreeingInbox(isManager: false, 101, 102);
            var allowed = new DisagreeingInbox(isManager: true, 101, 102);

            // THE FLAG IS THE GATE, and the guard that reads it is load-bearing rather than defensive
            // decoration. Today's inbox happens to return an empty list whenever the flag is false, so a
            // provider that ignored the flag would behave identically and no test against the real service
            // could tell the two apart — the difference only appears against an inbox that disagrees with
            // itself. Trusting the rows instead would make this provider silently depend on an invariant of
            // the inbox's RETURN SHAPE that nothing states or enforces.
            Assert.Empty(await new InventoryApprovalTaskVisibilityProvider(refused)
                .VisibleEntityIdsAsync(Ctx(Manager, CompanyA)));
            Assert.Equal(new[] { 101, 102 }, (await new InventoryApprovalTaskVisibilityProvider(allowed)
                .VisibleEntityIdsAsync(Ctx(Manager, CompanyA))).OrderBy(x => x));

            // …and it asks about the CONTEXT'S employee, not about anyone named by the caller.
            Assert.Equal(1, allowed.Calls);
            Assert.Equal(Manager, allowed.LastApprover);
        }

        [Fact]
        public async Task A10_a_caller_with_no_employee_identity_never_reaches_the_inbox_at_all()
        {
            var inbox = new DisagreeingInbox(isManager: true, 101);

            // Fail closed BEFORE asking. An inbox handed employee 0 would be answering a question about
            // nobody, and A5 alone cannot tell "refused" from "asked and got nothing".
            Assert.Empty(await new InventoryApprovalTaskVisibilityProvider(inbox)
                .VisibleEntityIdsAsync(CtxNoEmployee(CompanyA)));
            Assert.Equal(0, inbox.Calls);
        }

        // ============================================================================================
        // B. THE DISCLOSURES THIS CLOSES — each asserted against the call that succeeds.
        // ============================================================================================

        [Fact]
        public async Task B1_an_eligible_manager_with_a_task_read_grant_sees_the_unassigned_task()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // The task names nobody, so this caller reaches it through BREADTH — and the provider is what
            // makes granting that breadth safe: B2 is the same grant held by someone with no inventory
            // authority, and it does not reach this row.
            Assert.Contains(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task B2_a_task_administrator_with_no_inventory_authority_cannot_see_it()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksAdministrator, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // Company breadth reached this row before the provider existed. Holding every task role in the
            // company still confers no say over what inventory is buying, or for how much.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Clerk, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task B3_the_requester_cannot_see_the_task_for_their_own_submission()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Requester, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // The grant gives company breadth, so the refusal below is the provider's and not an artefact of
            // the requester happening to fall outside scope.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Requester, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task B4_the_requester_is_still_refused_while_holding_approval_authority_themself()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Requester, TasksRoles.TasksAdministrator, CompanyA);
            await RoleAsync(host, CompanyA, Requester, "InventoryManager");
            var (_, ownTask) = await ApprovalWithTaskAsync(host);
            var (_, othersTask) = await ApprovalWithTaskAsync(host, requester: Clerk);

            var ids = (await ListAsync(host, Requester, CompanyA)).Select(r => r.Id).ToList();

            // Task administrator AND inventory manager — and still refused their own, because separation of
            // duties is about THIS RECORD, not about roles. The same caller does see the one somebody else
            // requested, so the rule is per-record rather than a blanket exclusion of requesters.
            Assert.DoesNotContain(ownTask, ids);
            Assert.Contains(othersTask, ids);
        }

        [Fact]
        public async Task B5_an_inventory_role_that_is_not_approval_authority_cannot_see_it()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Keeper, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // A WarehouseKeeper is an inventory insider holding company task breadth. The provider asks for
            // APPROVAL authority specifically, not for "works in inventory".
            Assert.DoesNotContain(taskId, (await ListAsync(host, Keeper, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task B6_narrowing_alone_never_admits_the_manager_which_is_why_entrance_exists()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksViewer, CompanyA);   // turns bootstrap-open off
            var (approvalId, taskId) = await ApprovalWithTaskAsync(host);

            // SUPERSEDED IN MEANING, KEPT AS A CLAIM ABOUT THIS PROVIDER. When first written this pinned
            // product behaviour: an eligible manager could not reach the task at all. That is NO LONGER
            // true of the product — ITaskSourceReadEntrance now admits them, and TaskSourceReadEntranceTests
            // proves it end to end. What survives here is the narrower and still-important claim: the
            // VISIBILITY half never admits anyone. This fixture wires the authorizer with no entrance
            // service, which is exactly the composition every other caller in this file uses, so the
            // assertion below measures narrowing alone.
            //
            // It is worth keeping because it is the property the entrance mechanism depends on: scope
            // INTERSECT visibility can only remove rows, so the third layer had to CONTRIBUTE a set rather
            // than relax these two. If this test ever went green the other way, entrance would have leaked
            // into the narrowing path and the opt-in would no longer mean anything.
            Assert.Empty(await host.Db.PlatformRoleAssignments.AsNoTracking()
                .Where(r => r.PrincipalId == Manager && r.Scope == EntityRegistry.ScopeTasks).ToListAsync());
            Assert.DoesNotContain(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));

            // …and the eligibility was never in doubt: the provider named the approval all along. The gap
            // was only ever in how that answer reached a query.
            Assert.Contains(approvalId, await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyA)));
        }

        // ============================================================================================
        // C. COMPANY AND AUTHORITY LIFECYCLE.
        // ============================================================================================

        [Fact]
        public async Task C1_approval_authority_in_one_company_does_not_reach_another_companys_task()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, ForeignManager, TasksRoles.TasksAdministrator, CompanyB);
            var (approvalId, taskId) = await ApprovalWithTaskAsync(host, CompanyA);

            // A PENDING APPROVAL EXISTS IN COMPANY B TOO, and it has to for this to prove anything: without
            // it, "company A's manager sees nothing in company B" would be true simply because company B has
            // nothing, and a role check that ignored the company would pass unnoticed.
            var (foreignApprovalId, _) = await ApprovalWithTaskAsync(host, CompanyB, requester: ForeignManager);

            // InventoryUserRole is deliberately unfiltered — it is read to DECIDE permission — so a
            // cross-company leak here would be a real one rather than one a query filter hides.
            Assert.Empty(await Provider(host).VisibleEntityIdsAsync(Ctx(ForeignManager, CompanyB)));  // own request
            Assert.DoesNotContain(taskId, (await ListAsync(host, ForeignManager, CompanyB)).Select(r => r.Id));

            // …and it does not travel the other way either: company A's manager is eligible in A only, and
            // company B's pending approval stays invisible to them under either company argument.
            var inA = await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyA));
            Assert.Contains(approvalId, inA);
            Assert.DoesNotContain(foreignApprovalId, inA);
            Assert.Empty(await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyB)));
        }

        [Fact]
        public async Task C2_revoking_the_manager_role_removes_visibility_at_the_next_question()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.Contains(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));

            host.Seed.InventoryUserRoles.Remove(await host.Seed.InventoryUserRoles
                .FirstAsync(r => r.CompanyID == CompanyA && r.EmployeeId == Manager));
            await host.Seed.SaveChangesAsync();

            // No cache and no task rewrite: authority changed, and the next question gets the new answer.
            // The task row is untouched, which is exactly why visibility had to be a question and not a column.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
        }

        // ============================================================================================
        // D. THE ACTIONABILITY DECISION — recorded, not inherited by accident.
        // ============================================================================================

        [Theory]
        [InlineData("Approved")]
        [InlineData("Rejected")]
        public async Task D1_a_decided_approval_is_no_longer_visible_through_this_source(string status)
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host, status: status);

            // APPROVED PRODUCT DECISION, recorded here so it can never become an accident of the inbox's
            // Pending filter: inventory approval source visibility is ACTIONABILITY-based. A Pending
            // approval is visible to the eligible approvers; once Approved or Rejected the review work is
            // over and the task leaves generic task surfaces for EVERYONE, the deciding manager included.
            // Historical review belongs to the inventory approval history — a different screen with its own
            // authority — not to a broad new task permission nobody asked for. This matches the project
            // billing precedent.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
            Assert.Empty(await Provider(host).VisibleEntityIdsAsync(Ctx(Manager, CompanyA)));
        }

        [Fact]
        public async Task D2_a_pending_approval_beside_a_decided_one_shows_only_the_pending()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            var (_, decidedTask) = await ApprovalWithTaskAsync(host, status: "Approved");
            var (_, openTask) = await ApprovalWithTaskAsync(host, status: "Pending");

            // D1 alone would pass just as well if the provider hid everything.
            var ids = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).ToList();
            Assert.Contains(openTask, ids);
            Assert.DoesNotContain(decidedTask, ids);
        }

        // ============================================================================================
        // E. THE OTHER READ SURFACES INHERIT IT — no endpoint-specific inventory condition anywhere.
        // ============================================================================================

        [Fact]
        public async Task E1_task_detail_agrees_with_the_list()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksAdministrator, CompanyA);
            await GrantTasksAsync(host, Requester, TasksRoles.TasksViewer, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            var target = PermissionTarget.ForTask(taskId);

            // "Filter the rows, forget the row" is the classic second half of this bug: a caller who can no
            // longer SEE the approval task in any list must not reach it by typing its task id either.
            Assert.True(await AccessFor(host).CanAsync(Ctx(Manager, CompanyA), TasksActions.Read, target));
            Assert.False(await AccessFor(host).CanAsync(Ctx(Clerk, CompanyA), TasksActions.Read, target));
            Assert.False(await AccessFor(host).CanAsync(Ctx(Requester, CompanyA), TasksActions.Read, target));
        }

        [Fact]
        public async Task E2_the_overdue_report_lists_it_the_same_way()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksAdministrator, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.Contains(taskId,
                (await ReportsFor(host, Ctx(Manager, CompanyA)).OverdueAsync(CompanyA)).Select(r => r.Id));
            Assert.DoesNotContain(taskId,
                (await ReportsFor(host, Ctx(Clerk, CompanyA)).OverdueAsync(CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task E3_the_report_totals_move_with_it_not_just_the_rows()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksAdministrator, CompanyA);
            await ApprovalWithTaskAsync(host);

            // AN AGGREGATE IS NOT A WEAKER DISCLOSURE THAN A ROW. A total that still counted the hidden task
            // would tell the clerk an approval exists and roughly when — so the NUMBER is asserted, not just
            // the row list.
            var (_, clerkTeam) = await ReportsFor(host, Ctx(Clerk, CompanyA)).SummaryAsync(CompanyA, Clerk);
            var (_, managerTeam) = await ReportsFor(host, Ctx(Manager, CompanyA)).SummaryAsync(CompanyA, Manager);

            Assert.Equal(0, clerkTeam.Total);
            Assert.Equal(1, managerTeam.Total);
        }

        // ============================================================================================
        // F. NOTHING ELSE MOVED.
        // ============================================================================================

        [Fact]
        public async Task F1_a_task_from_another_source_is_untouched_by_this_provider()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksAdministrator, CompanyA);
            var other = new TaskItem
            {
                CompanyId = CompanyA, Title = "أمر تشغيل", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = 0, CreatedByEmployeeId = 0, CreatedAt = DateTime.UtcNow,
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = 1,
            };
            host.Seed.TaskItems.Add(other);
            await host.Seed.SaveChangesAsync();

            // Only InventoryApproval is guarded. A type with no provider keeps the answer it had — hiding a
            // manufacturing task here would be this increment quietly narrowing a module it never touched.
            Assert.Contains(other.ID, (await ListAsync(host, Clerk, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task F2_a_plain_task_with_no_source_record_is_untouched()
        {
            using var host = await SeedAsync();
            var plain = new TaskItem
            {
                CompanyId = CompanyA, Title = "عادي", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = Clerk, CreatedByEmployeeId = Clerk, CreatedAt = DateTime.UtcNow,
            };
            host.Seed.TaskItems.Add(plain);
            await host.Seed.SaveChangesAsync();

            Assert.Contains(plain.ID, (await ListAsync(host, Clerk, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public void F3_inventory_delegates_to_its_authority_without_importing_project_billing()
        {
            string src = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "TaskSourceVisibilityProviders.cs"));

            // The Inventory provider delegates to its own module's authority. Project billing is not converged,
            // and no billing authority is invented here.
            Assert.DoesNotContain("class ProjectBillingTaskVisibilityProvider", src);
            Assert.DoesNotContain("PendingApprovalsForAsync", src);
            Assert.Contains("class InventoryApprovalTaskVisibilityProvider", src);
            Assert.Contains("ApprovalInboxAsync", src);
        }

        // ============================================================================================
        // G. NO POLICY DUPLICATION.
        // ============================================================================================

        [Fact]
        public void G1_the_provider_states_no_inventory_rule_of_its_own()
        {
            string file = Path.Combine(RepoRoot(), "CrossBuy", "BL", "Platform", "TaskSourceVisibilityProviders.cs");
            string code = string.Join("\n", File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")));

            int start = code.IndexOf("class InventoryApprovalTaskVisibilityProvider", StringComparison.Ordinal);
            Assert.True(start > 0, "the provider class was not found");
            string body = code[start..];

            Assert.Contains("ApprovalInboxAsync", body);              // it ASKS…
            Assert.DoesNotContain("InventoryUserRoles", body);        // …and keeps no copy of the answer
            Assert.DoesNotContain("_db", body);

            // THE ROLE NAMES AND STATES ARE CHECKED AS STRING LITERALS, not as bare words, and the
            // distinction is the point. `isInventoryManager` appears in the provider — it is the name of the
            // flag ApprovalInboxAsync RETURNS, which is evidence of delegation rather than of a second copy.
            // A literal "InventoryManager" or "Pending" would be the copy: the provider deciding for itself
            // what approval authority is, or when an approval stops being actionable, in a second place that
            // would then have to be kept in step with the inbox by hand.
            Assert.DoesNotContain("\"InventoryManager\"", body);
            Assert.DoesNotContain("\"Pending\"", body);
            Assert.DoesNotContain("RequestedByEmployeeId", body);
            Assert.DoesNotContain("CompanyID", body);
        }

        [Fact]
        public void G2_the_generic_task_visibility_machinery_was_not_taught_about_inventory()
        {
            // The provider plugs INTO the existing mechanism; it does not adjust it. An inventory condition
            // appearing in any of these would mean the source model had been bypassed rather than used.
            foreach (var relative in new[]
            {
                Path.Combine("CrossBuy", "BL", "Platform", "TaskSourceVisibility.cs"),
                Path.Combine("CrossBuy", "BL", "TaskReadAuthorizer.cs"),
                Path.Combine("CrossBuy", "BL", "Platform", "TaskScopeQuery.cs"),
                Path.Combine("CrossBuy", "BL", "TasksAccessService.cs"),
                Path.Combine("CrossBuy", "BL", "TaskService.cs"),
                Path.Combine("CrossBuy", "BL", "TaskReportService.cs"),
            })
            {
                string src = File.ReadAllText(Path.Combine(RepoRoot(), relative));
                Assert.DoesNotContain("InventoryApproval", src);
                Assert.DoesNotContain("InventoryManager", src);
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
