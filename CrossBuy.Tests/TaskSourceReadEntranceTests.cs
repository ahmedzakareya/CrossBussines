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
    // SOURCE-EXPLICIT TASK READ ENTRANCE.
    //
    // Task read authorization was scope INTERSECT source eligibility, and both layers test the ROW for a
    // person: Own and Team match AssigneeEmployeeId or CreatedByEmployeeId, Company matches the tenant. Work
    // whose authority is a ROLE right names nobody — an inventory approval is reviewed by whoever holds
    // InventoryManager minus the requester, so no individual can be written onto the task — and such a task
    // is therefore unreachable by anything except Company breadth, which reaches everything else too.
    //
    // THIS ADDS A THIRD LAYER, AND IT IS THE ONLY ONE THAT WIDENS. Its safety is four properties, and every
    // test below defends one of them:
    //
    //   SOURCE-EXPLICIT   only a registered ITaskSourceReadEntranceProvider admits anything (N-series)
    //   OPT-IN            implementing the VISIBILITY provider admits nothing, ever (N2, N3)
    //   ENTITYTYPE-LOCAL  a source admits its own type and its own returned ids, nothing else (L-series)
    //   FAIL-CLOSED       no provider, no ids, an exception, no employee, wrong company ⇒ no entrance (F)
    //
    // THE HARD GATE IS THE W-SERIES. An entrance that made the approver reachable by widening them into the
    // company's other work would be a worse bug than the one it fixes, so every provider-less source is
    // measured before and after: plain, manufacturing, POS, CRM, sales invoice, purchase invoice, document,
    // and project billing. All eight must be untouched.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1.
    // ================================================================================================
    public class TaskSourceReadEntranceTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Requester = 5;
        private const int Manager = 6;      // eligible approver, holds NO task role
        private const int Keeper = 7;       // inventory insider, not approval authority
        private const int Clerk = 8;        // task roles, no inventory authority
        private const int ForeignManager = 9;

        // ------------------------------------------------------------------------------------------
        // Wiring — the production object graph. Only the context accessor is a stub.
        // ------------------------------------------------------------------------------------------

        private static BusinessContext Ctx(int? employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static IInventoryApprovalService Approvals(PlatformTestHost host)
            => new InventoryApprovalService(host.Db, null!, null!, null!, null!);

        private static InventoryApprovalTaskVisibilityProvider InventoryProvider(PlatformTestHost host)
            => new(Approvals(host));

        private static ITaskSourceVisibility Sources(PlatformTestHost host)
            => new TaskSourceVisibility(
                new ITaskSourceVisibilityProvider[] { InventoryProvider(host) },
                NullLogger<TaskSourceVisibility>.Instance);

        // TWO helpers on purpose. `Entrance` is the production composition - the inventory provider, the
        // only source registered for entrance. `EntranceOf` builds an arbitrary one, INCLUDING an empty one,
        // so the framework can be tested apart from inventory policy. They are separate methods because a
        // params overload cannot tell "no argument" from "deliberately none", and that distinction is the
        // difference between the default composition and the fail-closed case.
        private static ITaskSourceReadEntrance Entrance(PlatformTestHost host)
            => EntranceOf(InventoryProvider(host));

        private static ITaskSourceReadEntrance EntranceOf(params ITaskSourceReadEntranceProvider[] providers)
            => new TaskSourceReadEntrance(providers, NullLogger<TaskSourceReadEntrance>.Instance);

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

        // `entrance: null` reproduces the pre-change composition exactly, which is how the W-series measures
        // "before" and "after" against the same fixture rather than against a memory of the old behaviour.
        private static TasksAccessService Access(
            PlatformTestHost host, ITaskSourceReadEntrance? entrance = null, bool withEntrance = true)
            => new(
                host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                () => new PlatformPermissionProvider(
                    host.Registry(), Adapters(host), NullLogger<PlatformPermissionProvider>.Instance),
                NullLogger<TasksAccessService>.Instance,
                () => Sources(host),
                withEntrance ? () => entrance ?? Entrance(host) : null);

        private static ITaskReadAuthorizer AuthorizerFor(
            PlatformTestHost host, BusinessContext actor, ITaskSourceReadEntrance? entrance = null,
            bool withEntrance = true)
            => new TaskReadAuthorizer(
                new StubContextAccessor(actor), Access(host, entrance, withEntrance), Sources(host),
                withEntrance ? entrance ?? Entrance(host) : null);

        private static ITaskService TasksFor(
            PlatformTestHost host, BusinessContext actor, ITaskSourceReadEntrance? entrance = null,
            bool withEntrance = true)
        {
            var events = new BusinessEventService(
                host.Db, host.Registry(), new StubContextAccessor(actor),
                NullLogger<BusinessEventService>.Instance);
            return new TaskService(
                host.Db, new TaskCalendarEventPublisher(events),
                new TaskNotificationService(host.Db, new UatRecordingNotificationService(host.Db)),
                AuthorizerFor(host, actor, entrance, withEntrance));
        }

        private static ITaskReportService ReportsFor(PlatformTestHost host, BusinessContext actor)
            => new TaskReportService(host.Db, new EmployeeCostService(host.Db), AuthorizerFor(host, actor));

        private static Task<List<TaskRowDto>> ListAsync(
            PlatformTestHost host, int employeeId, int companyId, bool withEntrance = true)
            => TasksFor(host, Ctx(employeeId, companyId), null, withEntrance)
                .GetTasksAsync(companyId, "all", employeeId, null, null, null, page: 1, pageSize: 500);

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
            await RoleAsync(host, CompanyA, Manager, "InventoryManager");
            await RoleAsync(host, CompanyA, Keeper, "WarehouseKeeper");
            await RoleAsync(host, CompanyB, ForeignManager, "InventoryManager");

            // Someone holds a Tasks grant, so the company is CONFIGURED and nothing below is answered by
            // bootstrap-open. The manager deliberately holds none.
            await GrantTasksAsync(host, Clerk, TasksRoles.TasksViewer, CompanyA);
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
                CompanyId = companyId, Title = "اعتماد مخزني", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = 0, CreatedByEmployeeId = 0,      // names NOBODY — the whole reason
                EntityType = EntityRegistry.InventoryApproval, EntityId = approval.ID,
                CreatedAt = DateTime.UtcNow, DueDate = DateTime.Today.AddDays(-2),
            };
            host.Seed.TaskItems.Add(task);
            await host.Seed.SaveChangesAsync();
            return (approval.ID, task.ID);
        }

        // Every task-producing source that has NO visibility provider, plus project billing which has one.
        // None of it is inventory's business and none of it may move.
        private static readonly (string Label, string? Type)[] ForeignSources =
        {
            ("plain", null),
            ("manufacturing", EntityRegistry.ManufWorkOrder),
            ("pos", EntityRegistry.PosOrder),
            ("crm", EntityRegistry.Lead),
            ("sales invoice", EntityRegistry.SalesInvoice),
            ("purchase invoice", EntityRegistry.PurchaseInvoice),
            // A DOCUMENT task carries the code of the record the document is ABOUT - the verification
            // rule sets EntityType = envelope.Entity.Code - so there is no separate Document code to
            // test. Quotation is that shape.
            ("document", EntityRegistry.Quotation),
            ("project billing", EntityRegistry.ProjectBilling),
        };

        private static async Task<Dictionary<string, int>> ForeignWorkAsync(
            PlatformTestHost host, int entityId = 900)
        {
            var made = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (label, type) in ForeignSources)
            {
                var task = new TaskItem
                {
                    CompanyId = CompanyA, Title = "شغل " + label, Status = "New", Priority = "Normal",
                    // Assigned to the clerk: the manager is neither assignee, creator nor anyone's manager,
                    // so only BREADTH or a faulty entrance could ever surface these.
                    AssigneeEmployeeId = Clerk, CreatedByEmployeeId = Clerk,
                    EntityType = type, EntityId = type == null ? null : entityId,
                    CreatedAt = DateTime.UtcNow, DueDate = DateTime.Today.AddDays(-3),
                };
                host.Seed.TaskItems.Add(task);
                await host.Seed.SaveChangesAsync();
                made[label] = task.ID;
            }
            return made;
        }

        // A provider that admits whatever it is told to, so the FRAMEWORK can be tested independently of
        // inventory policy. Nothing registers it outside these tests.
        private sealed class FakeEntranceProvider : ITaskSourceReadEntranceProvider
        {
            private readonly int[] _ids;
            private readonly Exception? _throw;
            public int Calls { get; private set; }
            public string EntityType { get; }

            public FakeEntranceProvider(string entityType, params int[] ids)
            { EntityType = entityType; _ids = ids; }

            public FakeEntranceProvider(string entityType, Exception boom)
            { EntityType = entityType; _ids = Array.Empty<int>(); _throw = boom; }

            public Task<IReadOnlySet<int>> AdmissibleEntityIdsAsync(
                BusinessContext context, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (_throw != null) throw _throw;
                return Task.FromResult((IReadOnlySet<int>)_ids.ToHashSet());
            }
        }

        // A visibility provider that would admit the world IF visibility implied entrance. It does not
        // implement the entrance interface, which is the point.
        private sealed class GreedyVisibilityOnlyProvider : ITaskSourceVisibilityProvider
        {
            public string EntityType { get; }
            private readonly int[] _ids;
            public GreedyVisibilityOnlyProvider(string entityType, params int[] ids)
            { EntityType = entityType; _ids = ids; }
            public Task<IReadOnlySet<int>> VisibleEntityIdsAsync(
                BusinessContext context, CancellationToken cancellationToken = default)
                => Task.FromResult((IReadOnlySet<int>)_ids.ToHashSet());
        }

        // ============================================================================================
        // E. THE ELIGIBLE APPROVER REACHES THE WORK — all four read surfaces.
        // ============================================================================================

        [Fact]
        public async Task E1_the_eligible_approver_with_no_task_role_now_sees_it_in_the_list()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // The actor holds NO Tasks grant at all — asserted, not assumed, because the whole claim is that
            // they no longer need one.
            Assert.Empty(await host.Db.PlatformRoleAssignments.AsNoTracking()
                .Where(r => r.PrincipalId == Manager && r.Scope == EntityRegistry.ScopeTasks).ToListAsync());
            Assert.Contains(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task E2_the_eligible_approver_can_open_it_by_id()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.True(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(taskId)));
        }

        [Fact]
        public async Task E3_the_overdue_report_lists_it_for_the_eligible_approver()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.Contains(taskId,
                (await ReportsFor(host, Ctx(Manager, CompanyA)).OverdueAsync(CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task E4_the_aggregate_counts_it_for_the_eligible_approver_and_only_it()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);

            // ONE, not nine. The count is the disclosure test: an entrance that admitted the caller into the
            // company would show eight more here while the row list still looked correct.
            var (_, team) = await ReportsFor(host, Ctx(Manager, CompanyA)).SummaryAsync(CompanyA, Manager);
            Assert.Equal(1, team.Total);
        }

        [Fact]
        public async Task E5_the_four_surfaces_agree_on_exactly_one_row()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            var actor = Ctx(Manager, CompanyA);

            var list = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).OrderBy(x => x).ToList();
            var report = (await ReportsFor(host, actor).OverdueAsync(CompanyA)).Select(r => r.Id).OrderBy(x => x).ToList();
            var (_, team) = await ReportsFor(host, actor).SummaryAsync(CompanyA, Manager);
            bool detail = await Access(host).CanAsync(actor, TasksActions.Read, PermissionTarget.ForTask(taskId));

            // Convergence asserted as ONE claim rather than four independent ones: list, report rows, total
            // and detail must describe the same single task, or some surface has its own idea of the rule.
            Assert.Equal(new[] { taskId }, list);
            Assert.Equal(new[] { taskId }, report);
            Assert.Equal(1, team.Total);
            Assert.True(detail);
        }

        [Fact]
        public async Task E6_entrance_grants_read_and_nothing_else()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            var actor = Ctx(Manager, CompanyA);
            var target = PermissionTarget.ForTask(taskId);

            // Being entitled to REVIEW work that names nobody is not being entitled to hand it to someone
            // else, close it, or administer it. Entrance is a read entrance and the switch below it is
            // untouched.
            Assert.True(await Access(host).CanAsync(actor, TasksActions.Read, target));
            foreach (var action in new[]
                { TasksActions.Edit, TasksActions.Complete, TasksActions.Reopen,
                  TasksActions.Reassign, TasksActions.Assign, TasksActions.Manage })
                Assert.False(await Access(host).CanAsync(actor, action, target), action);
        }

        // ============================================================================================
        // N. NEGATIVE ACTORS — every one of them still excluded.
        // ============================================================================================

        [Fact]
        public async Task N1_the_requester_is_still_excluded_from_their_own_approval()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // SoD comes from ApprovalInboxAsync and entrance inherits it — a mechanism built to WIDEN access
            // must not be the thing that quietly drops the rule that narrows it.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Requester, CompanyA)).Select(r => r.Id));
            Assert.False(await Access(host).CanAsync(
                Ctx(Requester, CompanyA), TasksActions.Read, PermissionTarget.ForTask(taskId)));
        }

        [Fact]
        public async Task N2_the_requester_holding_approval_authority_and_every_task_role_is_still_excluded()
        {
            using var host = await SeedAsync();
            await RoleAsync(host, CompanyA, Requester, "InventoryManager");
            await GrantTasksAsync(host, Requester, TasksRoles.TasksAdministrator, CompanyA);
            var (_, ownTask) = await ApprovalWithTaskAsync(host);
            var (_, othersTask) = await ApprovalWithTaskAsync(host, requester: Clerk);

            var ids = (await ListAsync(host, Requester, CompanyA)).Select(r => r.Id).ToList();
            Assert.DoesNotContain(ownTask, ids);
            Assert.Contains(othersTask, ids);       // per-record, not a blanket exclusion of requesters
        }

        [Fact]
        public async Task N3_a_tasks_viewer_without_inventory_authority_is_still_excluded()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.DoesNotContain(taskId, (await ListAsync(host, Clerk, CompanyA)).Select(r => r.Id));
            Assert.False(await Access(host).CanAsync(
                Ctx(Clerk, CompanyA), TasksActions.Read, PermissionTarget.ForTask(taskId)));
        }

        [Fact]
        public async Task N4_a_tasks_administrator_without_inventory_authority_is_still_excluded()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, Keeper, TasksRoles.TasksAdministrator, CompanyA);
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            Assert.DoesNotContain(taskId, (await ListAsync(host, Keeper, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task N5_an_ordinary_employee_is_still_excluded()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // Keeper is an inventory insider without approval authority — the sharpest ordinary case.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Keeper, CompanyA)).Select(r => r.Id));
            Assert.Empty(await Entrance(host).AdmissionsAsync(Ctx(Keeper, CompanyA)));
        }

        [Fact]
        public async Task N6_a_foreign_company_approver_is_still_excluded()
        {
            using var host = await SeedAsync();
            await GrantTasksAsync(host, ForeignManager, TasksRoles.TasksAdministrator, CompanyB);
            var (_, taskId) = await ApprovalWithTaskAsync(host, CompanyA);
            await ApprovalWithTaskAsync(host, CompanyB, requester: ForeignManager);

            Assert.Empty(await Entrance(host).AdmissionsAsync(Ctx(ForeignManager, CompanyB)));
            Assert.DoesNotContain(taskId, (await ListAsync(host, ForeignManager, CompanyB)).Select(r => r.Id));
            // …and company A's approver is not admitted under a company B context either.
            Assert.Empty(await Entrance(host).AdmissionsAsync(Ctx(Manager, CompanyB)));
        }

        [Fact]
        public async Task N7_revoking_approval_authority_removes_the_entrance_at_the_next_question()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            Assert.Contains(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));

            host.Seed.InventoryUserRoles.Remove(await host.Seed.InventoryUserRoles
                .FirstAsync(r => r.CompanyID == CompanyA && r.EmployeeId == Manager));
            await host.Seed.SaveChangesAsync();

            // No cached admission and no task rewrite: the authority changed and the next question answers
            // differently. An entrance that outlived the authority would be worse than no entrance.
            Assert.DoesNotContain(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
            Assert.False(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(taskId)));
        }

        [Fact]
        public async Task N8_a_caller_with_no_employee_identity_is_admitted_to_nothing()
        {
            using var host = await SeedAsync();
            await ApprovalWithTaskAsync(host);

            Assert.Empty(await Entrance(host).AdmissionsAsync(Ctx(null, CompanyA)));
            Assert.False(await Entrance(host).AdmitsAsync(
                Ctx(null, CompanyA), EntityRegistry.InventoryApproval, 1));
        }

        // ============================================================================================
        // A. ACTIONABILITY — preserved through the new mechanism.
        // ============================================================================================

        [Fact]
        public async Task A1_a_pending_approval_admits_the_eligible_approver()
        {
            using var host = await SeedAsync();
            var (approvalId, _) = await ApprovalWithTaskAsync(host, status: "Pending");

            var admissions = await Entrance(host).AdmissionsAsync(Ctx(Manager, CompanyA));
            Assert.Equal(EntityRegistry.InventoryApproval, Assert.Single(admissions).EntityType);
            Assert.Contains(approvalId, admissions[0].EntityIds);
        }

        [Theory]
        [InlineData("Approved")]
        [InlineData("Rejected")]
        public async Task A2_a_decided_approval_admits_nobody(string status)
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host, status: status);

            // The ACTIONABILITY decision survives the new mechanism. Once decided the review is over, so the
            // task stops being reachable this way for everyone — the deciding manager included. Entrance
            // does not reopen historical visibility.
            Assert.Empty(await Entrance(host).AdmissionsAsync(Ctx(Manager, CompanyA)));
            Assert.DoesNotContain(taskId, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
            Assert.False(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(taskId)));
        }

        [Fact]
        public async Task A3_a_pending_one_beside_a_decided_one_admits_only_the_pending()
        {
            using var host = await SeedAsync();
            var (_, decided) = await ApprovalWithTaskAsync(host, status: "Approved");
            var (_, open) = await ApprovalWithTaskAsync(host, status: "Pending");

            var ids = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).ToList();
            Assert.Contains(open, ids);
            Assert.DoesNotContain(decided, ids);
        }

        // ============================================================================================
        // W. THE HARD GATE — no unrelated task moved. Measured before and after.
        // ============================================================================================

        [Fact]
        public async Task W1_no_provider_less_source_becomes_visible_to_the_admitted_approver()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);
            var (_, approvalTask) = await ApprovalWithTaskAsync(host);

            var withEntrance = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).ToHashSet();

            // The approver gained EXACTLY ONE row and it is theirs to act on. Every other source is absent.
            Assert.Equal(new[] { approvalTask }, withEntrance);
            foreach (var (label, id) in foreign)
                Assert.False(withEntrance.Contains(id), $"the {label} task must not have become visible");
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("manufacturing")]
        [InlineData("pos")]
        [InlineData("crm")]
        [InlineData("sales invoice")]
        [InlineData("purchase invoice")]
        [InlineData("document")]
        [InlineData("project billing")]
        public async Task W2_each_unrelated_source_is_identical_before_and_after_entrance(string label)
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);
            int id = foreign[label];

            // BEFORE is the real previous composition — the authorizer built with no entrance service at
            // all — so this compares behaviour against behaviour rather than against a remembered claim.
            bool before = (await ListAsync(host, Manager, CompanyA, withEntrance: false)).Any(r => r.Id == id);
            bool after = (await ListAsync(host, Manager, CompanyA, withEntrance: true)).Any(r => r.Id == id);

            Assert.False(before);
            Assert.Equal(before, after);
        }

        [Fact]
        public async Task W3_the_clerk_who_could_already_see_the_company_still_sees_exactly_what_they_did()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);

            // Entrance must not remove anything either. The clerk holds company breadth and no inventory
            // authority: they keep all eight unrelated tasks and are still refused the approval.
            var before = (await ListAsync(host, Clerk, CompanyA, withEntrance: false)).Select(r => r.Id).OrderBy(x => x).ToList();
            var after = (await ListAsync(host, Clerk, CompanyA, withEntrance: true)).Select(r => r.Id).OrderBy(x => x).ToList();

            Assert.Equal(before, after);
            Assert.Equal(foreign.Count, after.Count);
        }

        [Fact]
        public async Task W4_project_billing_did_not_opt_in_and_did_not_move()
        {
            using var host = await SeedAsync();

            // The registered entrance providers are exactly one, and it is not project billing. Asserted on
            // the composition root rather than on behaviour, because "did not opt in" is a registration fact.
            string program = string.Join("\n", File.ReadAllLines(
                    Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"))
                .Where(l => !l.TrimStart().StartsWith("//")));

            Assert.Contains(
                "ITaskSourceReadEntranceProvider, CrossBuy.BL.Platform.InventoryApprovalTaskVisibilityProvider>",
                program);
            Assert.DoesNotContain("ITaskSourceReadEntranceProvider, CrossBuy.BL.Platform.ProjectBilling", program);
            Assert.Equal(1, program.Split('\n').Count(l => l.Contains("ITaskSourceReadEntranceProvider,", StringComparison.Ordinal)));

            // …and the two visibility registrations are still there, unchanged and still narrowing-only.
            Assert.Equal(1, program.Split('\n').Count(l => l.Contains("ITaskSourceVisibilityProvider,", StringComparison.Ordinal)));
        }

        [Fact]
        public void W5_the_project_billing_provider_is_not_an_entrance_provider()
        {
            // The type system carries the decision, so this cannot drift with a registration edit.
            Assert.Null(typeof(InventoryApprovalTaskVisibilityProvider).Assembly.GetType(
                "CrossBuy.BL.Platform.ProjectBillingTaskVisibilityProvider"));
            Assert.True(typeof(ITaskSourceReadEntranceProvider)
                .IsAssignableFrom(typeof(InventoryApprovalTaskVisibilityProvider)));
        }

        [Fact]
        public void W6_there_is_exactly_one_entrance_provider_implementation()
        {
            var implementations = typeof(ITaskSourceReadEntranceProvider).Assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false }
                            && typeof(ITaskSourceReadEntranceProvider).IsAssignableFrom(t))
                .ToList();

            // One class, serving both roles — so there is one policy and one place it can be got wrong.
            Assert.Equal(new[] { typeof(InventoryApprovalTaskVisibilityProvider) }, implementations);
            Assert.True(typeof(ITaskSourceVisibilityProvider).IsAssignableFrom(implementations[0]));
        }

        // ============================================================================================
        // L. ENTITYTYPE LOCALITY — a source admits its own type and its own ids, and nothing else.
        // ============================================================================================

        [Fact]
        public async Task L1_the_same_entity_id_under_another_type_is_not_admitted()
        {
            using var host = await SeedAsync();
            var (approvalId, approvalTask) = await ApprovalWithTaskAsync(host);

            // A manufacturing task carrying the SAME numeric id as the admitted approval. Ids collide across
            // types constantly, and admitting this one because the approval was authorised would be a
            // cross-module disclosure caused by an integer.
            var collide = new TaskItem
            {
                CompanyId = CompanyA, Title = "أمر تشغيل", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = Clerk, CreatedByEmployeeId = Clerk,
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = approvalId,
                CreatedAt = DateTime.UtcNow, DueDate = DateTime.Today.AddDays(-2),
            };
            host.Seed.TaskItems.Add(collide);
            await host.Seed.SaveChangesAsync();

            var ids = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).ToList();
            Assert.Equal(new[] { approvalTask }, ids);
            Assert.False(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(collide.ID)));
        }

        [Fact]
        public async Task L2_a_provider_admits_only_the_ids_it_returned()
        {
            using var host = await SeedAsync();
            var (admitted, admittedTask) = await ApprovalWithTaskAsync(host);
            // A second approval requested by the manager themself — so the inbox excludes it by SoD and it
            // is NOT among the returned ids, though it is the same type and the same company.
            var (other, otherTask) = await ApprovalWithTaskAsync(host, requester: Manager);

            var ids = (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id).ToList();
            Assert.Equal(new[] { admittedTask }, ids);
            Assert.NotEqual(admitted, other);
            Assert.False(await Entrance(host).AdmitsAsync(Ctx(Manager, CompanyA), EntityRegistry.InventoryApproval, other));
            Assert.False(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(otherTask)));
        }

        [Fact]
        public async Task L3_a_provider_cannot_admit_a_type_it_does_not_own()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);

            // A provider claiming InventoryApproval, returning the id the MANUFACTURING task carries. The
            // pair test is what refuses it: right id, wrong type.
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.InventoryApproval, 900));
            var admitted = await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA));

            Assert.DoesNotContain(foreign["manufacturing"], await admitted.Select(t => t.ID).ToListAsync());
            Assert.False(await entrance.AdmitsAsync(Ctx(Manager, CompanyA), EntityRegistry.ManufWorkOrder, 900));
        }

        [Fact]
        public async Task L4_one_source_opting_in_does_not_admit_another_sources_work()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);

            // A fake provider that DOES own manufacturing admits only manufacturing — not the POS or CRM
            // tasks that share its id. Locality holds for any source, not just the inventory one.
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.ManufWorkOrder, 900));
            var ids = await (await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .Select(t => t.ID).ToListAsync();

            Assert.Equal(new[] { foreign["manufacturing"] }, ids);
        }

        [Fact]
        public async Task L5_admission_does_not_cross_the_company_boundary()
        {
            using var host = await SeedAsync();
            var (approvalId, _) = await ApprovalWithTaskAsync(host, CompanyA);
            // The same approval id echoed onto a company B task, which the entrance must not reach even
            // though the id was genuinely admitted — in another company.
            host.Seed.TaskItems.Add(new TaskItem
            {
                CompanyId = CompanyB, Title = "شركة أخرى", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = 0, CreatedByEmployeeId = 0,
                EntityType = EntityRegistry.InventoryApproval, EntityId = approvalId,
                CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();

            var admitted = await Entrance(host).AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA));
            var companies = await admitted.Select(t => t.CompanyId).Distinct().ToListAsync();

            Assert.Equal(new[] { CompanyA }, companies);
        }

        // ============================================================================================
        // F. FAIL-CLOSED — every uncertain answer is no.
        // ============================================================================================

        [Fact]
        public async Task F1_no_entrance_provider_at_all_admits_nothing()
        {
            using var host = await SeedAsync();
            await ApprovalWithTaskAsync(host);
            var entrance = EntranceOf();

            Assert.Empty(await entrance.AdmissionsAsync(Ctx(Manager, CompanyA)));
            // …and the query answer is an EMPTY query, never the source untouched — the single worst
            // mistake this mechanism could make.
            Assert.Empty(await (await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .ToListAsync());
        }

        [Fact]
        public async Task F2_a_visibility_provider_without_the_entrance_interface_admits_nothing()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);

            // A visibility provider naming every manufacturing id in sight. It is not an entrance provider,
            // so the entrance composition never sees it — opt-in is a TYPE, not a convention.
            var greedy = new GreedyVisibilityOnlyProvider(EntityRegistry.ManufWorkOrder, 900);

            // Asserted REFLECTIVELY because the compiler refuses the direct `is` test as provably false
            // (CS0184) — which is the strongest possible form of this claim: a visibility provider cannot
            // be mistaken for an entrance one even by accident, because the conversion does not exist.
            Assert.False(typeof(ITaskSourceReadEntranceProvider).IsInstanceOfType(greedy));

            var entrance = EntranceOf();
            Assert.Empty(await (await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .ToListAsync());
            Assert.DoesNotContain(foreign["manufacturing"], (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task F3_an_empty_provider_result_admits_nothing()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var provider = new FakeEntranceProvider(EntityRegistry.ManufWorkOrder);   // no ids
            var entrance = EntranceOf(provider);

            Assert.Empty(await entrance.AdmissionsAsync(Ctx(Manager, CompanyA)));
            Assert.Equal(1, provider.Calls);

            Assert.Empty(await (await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .ToListAsync());

            // ONCE PER QUESTION, and the empty answer did not short-circuit the asking: a provider that was
            // never consulted would also produce an empty result, and the two must not be confused.
            Assert.Equal(2, provider.Calls);
        }

        [Fact]
        public async Task F4_a_throwing_provider_admits_nothing_and_does_not_break_the_others()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.PosOrder, new InvalidOperationException("boom")),
                new FakeEntranceProvider(EntityRegistry.ManufWorkOrder, 900));

            var ids = await (await entrance.AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .Select(t => t.ID).ToListAsync();

            // The failure admits nothing rather than everything, and the healthy provider is unaffected —
            // an outage in one module must not become a disclosure, nor an outage in another.
            Assert.DoesNotContain(foreign["pos"], ids);
            Assert.Contains(foreign["manufacturing"], ids);
        }

        [Fact]
        public async Task F5_a_source_whose_visibility_provider_refuses_cannot_be_entered()
        {
            using var host = await SeedAsync();
            var (approvalId, taskId) = await ApprovalWithTaskAsync(host);

            // Entrance says yes; visibility says no. The composition applies source visibility to the
            // entrance branch too, so an admission can never outlive the eligibility it came from.
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.InventoryApproval, approvalId));
            var authorizer = new TaskReadAuthorizer(
                new StubContextAccessor(Ctx(Clerk, CompanyA)),      // clerk: no inventory eligibility
                Access(host), Sources(host), entrance);

            var auth = await authorizer.ForListAsync();
            Assert.NotNull(auth);
            var rows = await (await authorizer.ApplyAsync(
                host.Db.TaskItems.AsNoTracking().Where(t => t.CompanyId == CompanyA), auth!))
                .Select(t => t.ID).ToListAsync();

            Assert.DoesNotContain(taskId, rows);
        }

        [Fact]
        public async Task F6_a_task_with_no_source_can_never_be_admitted()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);

            // A REAL ADMISSION EXISTS FIRST. Without it the caller admits nothing anyway and the guard below
            // would never be reached — the assertion would pass because the loop had nothing to iterate,
            // which is not the same as the guard working.
            await ApprovalWithTaskAsync(host);
            Assert.NotEmpty(await Entrance(host).AdmissionsAsync(Ctx(Manager, CompanyA)));

            // The plain task carries no EntityType at all. No source owns it, so no source can admit it —
            // which is what keeps every unguarded task exactly as it was.
            Assert.False(await Entrance(host).AdmitsAsync(Ctx(Manager, CompanyA), null, null));
            Assert.False(await Entrance(host).AdmitsAsync(Ctx(Manager, CompanyA), "", 900));

            // AND THE HALF-LINKED CASE, which is the one that bites. TaskItem.EntityId is nullable, so a row
            // may name an ADMITTED type while carrying no id at all - a hand-linked task, or a source that
            // wrote the type before the record existed. The type matches an admission, so a check that
            // guarded only on the type would reach the id and dereference nothing. It must REFUSE, not throw:
            // an exception here fails a read that should simply have said no.
            Assert.False(await Entrance(host).AdmitsAsync(Ctx(Manager, CompanyA), EntityRegistry.InventoryApproval, null));
            Assert.False(await Entrance(host).AdmitsAsync(Ctx(Manager, CompanyA), EntityRegistry.InventoryApproval, 0));

            var halfLinked = new TaskItem
            {
                CompanyId = CompanyA, Title = "مرتبط جزئيا", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = Clerk, CreatedByEmployeeId = Clerk,
                EntityType = EntityRegistry.InventoryApproval, EntityId = null,
                CreatedAt = DateTime.UtcNow,
            };
            host.Seed.TaskItems.Add(halfLinked);
            await host.Seed.SaveChangesAsync();

            Assert.False(await Access(host).CanAsync(
                Ctx(Manager, CompanyA), TasksActions.Read, PermissionTarget.ForTask(halfLinked.ID)));
            Assert.DoesNotContain(halfLinked.ID, (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
            Assert.DoesNotContain(foreign["plain"], (await ListAsync(host, Manager, CompanyA)).Select(r => r.Id));
        }

        [Fact]
        public async Task F7_an_unresolved_context_is_refused_before_entrance_is_considered()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);

            // Refusal beats entrance: no context, no list, and emphatically not an entrance-only list.
            var authorizer = new TaskReadAuthorizer(
                StubContextAccessor.Unresolved(), Access(host), Sources(host), Entrance(host));
            Assert.Null(await authorizer.ForListAsync());

            var svc = new TaskService(
                host.Db,
                new TaskCalendarEventPublisher(new BusinessEventService(
                    host.Db, host.Registry(), new StubContextAccessor(Ctx(Manager, CompanyA)),
                    NullLogger<BusinessEventService>.Instance)),
                new TaskNotificationService(host.Db, new UatRecordingNotificationService(host.Db)),
                authorizer);
            Assert.DoesNotContain(taskId,
                (await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, null, 1, 500)).Select(r => r.Id));
        }

        [Fact]
        public async Task F8_the_framework_refuses_a_context_with_no_employee_without_asking_anyone()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var provider = new FakeEntranceProvider(EntityRegistry.ManufWorkOrder, 900);

            // THE GUARD IS THE FRAMEWORK'S, NOT THE PROVIDER'S, and this uses a provider that would happily
            // admit a row to prove it. Relying on every provider to check the context itself would make the
            // security model only as strong as its least careful implementer.
            Assert.Empty(await EntranceOf(provider).AdmissionsAsync(Ctx(null, CompanyA)));
            Assert.Empty(await (await EntranceOf(provider)
                .AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(null, CompanyA))).ToListAsync());
            Assert.Equal(0, provider.Calls);
        }

        [Fact]
        public async Task L6_the_entrance_branch_alone_carries_only_the_ids_the_provider_returned()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host, entityId: 900);
            var second = new TaskItem
            {
                CompanyId = CompanyA, Title = "أمر تشغيل آخر", Status = "New", Priority = "Normal",
                AssigneeEmployeeId = Clerk, CreatedByEmployeeId = Clerk,
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = 901,
                CreatedAt = DateTime.UtcNow,
            };
            host.Seed.TaskItems.Add(second);
            await host.Seed.SaveChangesAsync();

            // ASSERTED ON THE BRANCH ITSELF, with no source-visibility pass behind it. In the composed
            // pipeline these ids are filtered a second time, so an entrance that admitted the whole type
            // would still LOOK correct end to end — and would be one provider away from not being. The
            // branch has to be right on its own.
            var ids = await (await EntranceOf(new FakeEntranceProvider(EntityRegistry.ManufWorkOrder, 900))
                .AdmittedAsync(host.Db.TaskItems.AsNoTracking(), Ctx(Manager, CompanyA)))
                .Select(t => t.ID).ToListAsync();

            Assert.Equal(new[] { foreign["manufacturing"] }, ids);
            Assert.DoesNotContain(second.ID, ids);
        }

        [Fact]
        public async Task L7_the_detail_gate_asks_about_this_task_not_merely_whether_the_caller_has_any()
        {
            using var host = await SeedAsync();
            var (admittedId, admittedTask) = await ApprovalWithTaskAsync(host);
            var (otherId, otherTask) = await ApprovalWithTaskAsync(host);

            // BOTH approvals are eligible, so source VISIBILITY allows both and cannot be what separates
            // them. The entrance provider names only the first. A gate that answered "this caller has some
            // admission, so yes" would open the second — which is the difference between a per-record
            // authority and a per-caller one, and only the first is worth having.
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.InventoryApproval, admittedId));
            var access = Access(host, entrance);
            var actor = Ctx(Manager, CompanyA);

            Assert.NotEqual(admittedId, otherId);
            Assert.True(await access.CanAsync(actor, TasksActions.Read, PermissionTarget.ForTask(admittedTask)));
            Assert.False(await access.CanAsync(actor, TasksActions.Read, PermissionTarget.ForTask(otherTask)));
        }

        [Fact]
        public async Task L8_the_admitted_list_is_per_record_for_the_same_reason()
        {
            using var host = await SeedAsync();
            var (admittedId, admittedTask) = await ApprovalWithTaskAsync(host);
            var (_, otherTask) = await ApprovalWithTaskAsync(host);

            // The list half of L7: same fixture, same asymmetry, so the two surfaces cannot drift apart on
            // the one property that makes entrance a record-level authority.
            var entrance = EntranceOf(new FakeEntranceProvider(EntityRegistry.InventoryApproval, admittedId));
            var ids = (await TasksFor(host, Ctx(Manager, CompanyA), entrance)
                .GetTasksAsync(CompanyA, "all", Manager, null, null, null, 1, 500)).Select(r => r.Id).ToList();

            Assert.Equal(new[] { admittedTask }, ids);
            Assert.DoesNotContain(otherTask, ids);
        }

        // ============================================================================================
        // Q. THE QUERY — search, filter, paging and counting all see the same authorized set.
        // ============================================================================================

        [Fact]
        public async Task Q1_the_count_matches_the_rows_for_an_admitted_caller()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            // A count computed over a wider set than the rows tells the caller how much work exists that
            // they may not see. Both come from the same authorized base, so they cannot disagree.
            var rows = await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, null, 1, 500);
            int count = await svc.CountTasksAsync(CompanyA, "all", Manager, null, null, null);
            Assert.Equal(rows.Count, count);
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task Q2_a_search_term_cannot_reach_an_unadmitted_row()
        {
            using var host = await SeedAsync();
            var foreign = await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            // Searching for the clerk's manufacturing work by its own title finds nothing: the filter runs
            // on top of the authorized union, never on the raw table.
            var hits = await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, "شغل", 1, 500);
            Assert.Empty(hits);
            Assert.Equal(0, await svc.CountTasksAsync(CompanyA, "all", Manager, null, null, "شغل"));
            Assert.NotEmpty(foreign);
        }

        [Fact]
        public async Task Q3_a_status_filter_narrows_the_admitted_row_and_never_widens()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            Assert.Equal(new[] { taskId },
                (await svc.GetTasksAsync(CompanyA, "all", Manager, "New", null, null, 1, 500)).Select(r => r.Id));
            Assert.Empty(await svc.GetTasksAsync(CompanyA, "all", Manager, "Done", null, null, 1, 500));
        }

        [Fact]
        public async Task Q4_paging_an_admitted_caller_never_pages_someone_elses_work()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var expected = new List<int>();
            for (int i = 0; i < 3; i++) expected.Add((await ApprovalWithTaskAsync(host)).TaskId);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            // Page size 2 over three admitted rows and eight unrelated ones: two pages, three rows, and the
            // eight never appear on any page.
            var page1 = await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, null, 1, 2);
            var page2 = await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, null, 2, 2);

            Assert.Equal(2, page1.Count);
            Assert.Single(page2);
            Assert.Equal(expected.OrderBy(x => x), page1.Concat(page2).Select(r => r.Id).OrderBy(x => x));
            Assert.Equal(3, await svc.CountTasksAsync(CompanyA, "all", Manager, null, null, null));
        }

        [Fact]
        public async Task Q5_the_kpi_tiles_describe_the_same_set_as_the_list()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            await ApprovalWithTaskAsync(host);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            var kpi = await svc.GetKpisAsync(CompanyA, "all", Manager);
            Assert.Equal(1, kpi.Total);
            Assert.Equal(1, kpi.Overdue);
        }

        [Fact]
        public async Task Q6_the_admitted_row_appears_once_even_when_scope_would_also_include_it()
        {
            using var host = await SeedAsync();
            var (_, taskId) = await ApprovalWithTaskAsync(host);
            // Give the approver company breadth as well, so the row is in BOTH branches of the union.
            await GrantTasksAsync(host, Manager, TasksRoles.TasksViewer, CompanyA);
            var svc = TasksFor(host, Ctx(Manager, CompanyA));

            var rows = await svc.GetTasksAsync(CompanyA, "all", Manager, null, null, null, 1, 500);
            Assert.Equal(1, rows.Count(r => r.Id == taskId));
            Assert.Equal(rows.Count, await svc.CountTasksAsync(CompanyA, "all", Manager, null, null, null));
        }

        [Fact]
        public async Task Q7_one_authorized_query_asks_each_entrance_provider_exactly_once()
        {
            using var host = await SeedAsync();
            await ForeignWorkAsync(host);
            var provider = new FakeEntranceProvider(EntityRegistry.ManufWorkOrder, 900);
            var authorizer = new TaskReadAuthorizer(
                new StubContextAccessor(Ctx(Manager, CompanyA)), Access(host), Sources(host),
                EntranceOf(provider));

            var auth = await authorizer.ForListAsync();
            var rows = await (await authorizer.ApplyAsync(
                host.Db.TaskItems.AsNoTracking().Where(t => t.CompanyId == CompanyA), auth!))
                .ToListAsync();

            // ONCE PER QUERY, NEVER PER ROW. The set-shaped question is the whole reason this is a provider
            // contract rather than a per-row permission check - a per-row call would put the authorization
            // cost on the data volume and materialise the company before authorizing any of it.
            Assert.Equal(1, provider.Calls);
            Assert.NotEmpty(rows);
        }

        // ============================================================================================
        // P. THE PROVIDER STILL STATES NO POLICY.
        // ============================================================================================

        [Fact]
        public void P1_the_entrance_answer_delegates_to_the_inventory_inbox()
        {
            string file = Path.Combine(RepoRoot(), "CrossBuy", "BL", "Platform", "TaskSourceVisibilityProviders.cs");
            string code = string.Join("\n", File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")));
            string body = code[code.IndexOf("class InventoryApprovalTaskVisibilityProvider", StringComparison.Ordinal)..];

            Assert.Contains("AdmissibleEntityIdsAsync", body);
            Assert.Contains("ApprovalInboxAsync", body);
            // No second copy of company, role, state or separation-of-duties anywhere in the provider.
            Assert.DoesNotContain("InventoryUserRoles", body);
            Assert.DoesNotContain("\"InventoryManager\"", body);
            Assert.DoesNotContain("\"Pending\"", body);
            Assert.DoesNotContain("RequestedByEmployeeId", body);
            Assert.DoesNotContain("_db", body);
        }

        // An inbox that answers "not an approver" while still handing back rows. No production path does
        // this - it exists so the provider's own gate can be tested rather than inferred.
        private sealed class DisagreeingInbox : IInventoryApprovalService
        {
            private readonly bool _isManager;
            private readonly List<InventoryApproval> _rows;
            public DisagreeingInbox(bool isManager, params int[] ids)
            {
                _isManager = isManager;
                _rows = ids.Select(id => new InventoryApproval { ID = id, Status = "Pending" }).ToList();
            }

            public Task<(bool isInventoryManager, List<InventoryApproval> pending)> ApprovalInboxAsync(
                BusinessContext context, int approverEmployeeId, CancellationToken cancellationToken = default)
                => Task.FromResult((_isManager, _rows));

            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"The entrance provider must not call IInventoryApprovalService.{m}.");
            public Task<bool> RequiresApprovalAsync(decimal a) => throw No();
            public Task<int> SubmitAsync(string a, decimal b, object c, int? d) => throw No();
            public Task<List<InventoryApproval>> PendingAsync() => throw No();
            public Task<List<InventoryApproval>> RecentAsync(int a = 50) => throw No();
            public Task<(bool ok, string? error)> ApproveAsync(int a, int b, string? c) => throw No();
            public Task<(bool ok, string? error)> RejectAsync(int a, int b, string? c) => throw No();
        }

        [Fact]
        public async Task P3_the_entrance_answer_believes_the_authority_flag_over_the_rows()
        {
            var refused = new InventoryApprovalTaskVisibilityProvider(new DisagreeingInbox(false, 101, 102));
            var allowed = new InventoryApprovalTaskVisibilityProvider(new DisagreeingInbox(true, 101, 102));
            var ctx = Ctx(Manager, CompanyA);

            // THE FLAG IS THE GATE ON THE ENTRANCE SIDE TOO. Today's inbox returns an empty list whenever
            // the flag is false, so a provider that trusted the ROWS would behave identically against the
            // real service and no end-to-end test could tell them apart. Admitting on rows alone would make
            // the strongest authority in the model - the one that reaches past task breadth - depend on an
            // invariant of the inbox's return shape that nothing states or enforces.
            Assert.Empty(await refused.AdmissibleEntityIdsAsync(ctx));
            Assert.Equal(new[] { 101, 102 }, (await allowed.AdmissibleEntityIdsAsync(ctx)).OrderBy(x => x));

            // …and the entrance composition inherits that refusal rather than re-deriving it.
            Assert.Empty(await EntranceOf(refused).AdmissionsAsync(ctx));
        }

        [Fact]
        public void P2_the_generic_entrance_machinery_names_no_module()
        {
            foreach (var relative in new[]
            {
                Path.Combine("CrossBuy", "BL", "Platform", "TaskSourceReadEntrance.cs"),
                Path.Combine("CrossBuy", "BL", "TaskReadAuthorizer.cs"),
                Path.Combine("CrossBuy", "BL", "Platform", "TaskScopeQuery.cs"),
                Path.Combine("CrossBuy", "BL", "TasksAccessService.cs"),
            })
            {
                string src = File.ReadAllText(Path.Combine(RepoRoot(), relative));
                // The mechanism is reusable. An `if (EntityType == InventoryApproval)` anywhere in here
                // would mean one module had been special-cased into generic authorization.
                Assert.DoesNotContain("EntityRegistry.InventoryApproval", src);
                Assert.DoesNotContain("InventoryManager", src);
                Assert.DoesNotContain("ApprovalInbox", src);
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
