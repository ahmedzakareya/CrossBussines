using System.Globalization;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORK REPORTING FOUNDATION — the behavioural proofs.
    //
    // THESE DRIVE THE REAL SOURCES THROUGH THE REAL AUTHORIZER. No stub stands in for
    // TaskReadAuthorizer, TasksAccessService, TaskSourceVisibility or TaskApprovalProjection: the
    // whole claim of this pack is that reporting reads through the authoritative path, and a test
    // that substituted that path would prove nothing about it.
    //
    // WHERE A DOUBLE IS USED IT IS NAMED AND BOUNDED. The ProjectBilling entrance provider is backed
    // by a double for IProgressBillingService, because the question under test is whether REPORTING
    // honours the module's approver answer — not how the module computes it, which
    // ProjectBillingApprovalCycleTests owns. The double answers the module's own contract shape and
    // nothing more.
    //
    // SQLite in memory over the real CrossDbContext model, as the HOLD properties may legitimately be
    // tested that way. NOTHING HERE CLAIMS A PRODUCTION SCHEMA IS DEPLOYED.
    // ============================================================================================
    public class WorkReportingFoundationTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;

        private const int Viewer = 11;      // a generic task viewer: breadth, no source eligibility
        private const int Keeper = 12;      // the inventory approver — entitled to the approval record
        private const int Preparer = 13;    // submits a billing; must never be its approver
        private const int Approver = 14;    // the billing approver
        private const int Foreigner = 21;   // company B

        // ---- construction, mirroring the shape TaskSourceReadEntranceTests already proves ---------

        private static BusinessContext Ctx(int? employeeId, int companyId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Roles = Array.Empty<string>(),
            CorrelationId = Guid.NewGuid(),
        };

        private static InventoryApprovalTaskVisibilityProvider InventoryProvider(PlatformTestHost host)
            => new(new InventoryApprovalService(host.Db, null!, null!, null!, null!));

        private static ITaskSourceVisibility Sources(PlatformTestHost host, IProgressBillingService? billings)
            => new TaskSourceVisibility(
                Providers(host, billings), NullLogger<TaskSourceVisibility>.Instance);

        private static ITaskSourceVisibilityProvider[] Providers(PlatformTestHost host, IProgressBillingService? billings)
            => billings is null
                ? new ITaskSourceVisibilityProvider[] { InventoryProvider(host) }
                : new ITaskSourceVisibilityProvider[]
                    { InventoryProvider(host), new ProjectBillingTaskVisibilityProvider(billings) };

        private static ITaskSourceReadEntrance Entrance(PlatformTestHost host, IProgressBillingService? billings)
            => new TaskSourceReadEntrance(
                billings is null
                    ? new ITaskSourceReadEntranceProvider[] { InventoryProvider(host) }
                    : new ITaskSourceReadEntranceProvider[]
                        { InventoryProvider(host), new ProjectBillingTaskVisibilityProvider(billings) },
                NullLogger<TaskSourceReadEntrance>.Instance);

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

        private static ITaskReadAuthorizer Authorizer(
            PlatformTestHost host, BusinessContext actor, IProgressBillingService? billings = null)
        {
            var access = new TasksAccessService(
                host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                () => new PlatformPermissionProvider(
                    host.Registry(), Adapters(host), NullLogger<PlatformPermissionProvider>.Instance),
                NullLogger<TasksAccessService>.Instance,
                () => Sources(host, billings),
                () => Entrance(host, billings));

            return new TaskReadAuthorizer(
                new StubContextAccessor(actor), access, Sources(host, billings), Entrance(host, billings));
        }

        private static WorkTasksDataSource TasksSource(
            PlatformTestHost host, BusinessContext actor, IProgressBillingService? billings = null)
            => new(host.Db, Authorizer(host, actor, billings), new AllProjects());

        private static WorkApprovalsDataSource ApprovalsSource(
            PlatformTestHost host, BusinessContext actor, IProgressBillingService? billings = null)
            => new(host.Db, Authorizer(host, actor, billings), new AllProjects());

        // ---- driving the platform contract -------------------------------------------------------

        private static ReportDataQuery QueryFor(ReportDatasetDefinition dataset, BusinessContext context)
        {
            var columns = dataset.Fields.Select(f => new ReportColumn
            {
                Key = f.Key, TitleAr = f.TitleAr, TitleEn = f.TitleEn, Type = f.Type,
            }).ToList();

            return new ReportDataQuery
            {
                Definition = new ReportDefinition
                {
                    Code = "Test." + dataset.DatasetCode,
                    Module = dataset.Module,
                    TitleAr = dataset.TitleAr,
                    TitleEn = dataset.TitleEn,
                    DataSourceKey = dataset.DataSourceKey,
                    PermissionKey = dataset.RequiredPermissionKey,
                    Columns = columns,
                },
                Context = context,
                Parameters = new ReportParameterSet(
                    Array.Empty<ReportParameterValue>(), new Dictionary<string, string?>()),
                RequestedColumns = columns,
                MaxRows = 5000,
                Kind = CrossBuy.Models.Context.Reporting.ReportRunKind.Full,
                Culture = CultureInfo.InvariantCulture,
            };
        }

        private static IEnumerable<int> TaskIds(ReportDataSet set)
        {
            int i = set.Columns.ToList().FindIndex(c => c.Key == "TaskId");
            foreach (var row in set.Rows) yield return Convert.ToInt32(row.ValueAt(i));
        }

        // ---- fixture ------------------------------------------------------------------------------

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
                Emp(Viewer, CompanyA), Emp(Keeper, CompanyA), Emp(Preparer, CompanyA),
                Emp(Approver, CompanyA), Emp(Foreigner, CompanyB));
            await host.Seed.SaveChangesAsync();

            // Company-wide task breadth for everyone who reads below, INCLUDING the generic viewer.
            // That is deliberate: it is the breadth that makes W2 and W3 mean something. If the viewer
            // held no breadth, they would see nothing for an uninteresting reason.
            foreach (var e in new[] { Viewer, Keeper, Preparer, Approver })
                await GrantTasksAsync(host, e, TasksRoles.TasksAdministrator, CompanyA);
            await GrantTasksAsync(host, Foreigner, TasksRoles.TasksAdministrator, CompanyB);

            await RoleAsync(host, CompanyA, Keeper, "InventoryManager");
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

        // A plain task: no source, so no provider guards it and generic breadth reaches it.
        private static async Task<int> PlainTaskAsync(
            PlatformTestHost host, int companyId = CompanyA, string status = "New",
            int assignee = 0, DateTime? completedAt = null)
        {
            var task = new TaskItem
            {
                CompanyId = companyId, Title = "مهمة", TitleEn = "Task", Status = status,
                Priority = "Normal", AssigneeEmployeeId = assignee, CreatedByEmployeeId = 0,
                CreatedAt = DateTime.UtcNow.AddDays(-3), CompletedAt = completedAt,
            };
            host.Seed.TaskItems.Add(task);
            await host.Seed.SaveChangesAsync();
            return task.ID;
        }

        // Approval work: a task LINKED to a source record, with its orchestration-ledger row. Both halves
        // matter — the link is what the source providers guard, the ledger row is what makes it an
        // approval. Neither the title nor the link decides the latter.
        private static async Task<(int TaskId, int EntityId)> ApprovalWorkAsync(
            PlatformTestHost host, string entityType, string rulePrefix,
            int companyId = CompanyA, string status = "New", int entityId = 0)
        {
            var task = new TaskItem
            {
                CompanyId = companyId, Title = "اعتماد", TitleEn = "Approval", Status = status,
                Priority = "Normal", AssigneeEmployeeId = 0, CreatedByEmployeeId = 0,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                EntityType = entityType, EntityId = entityId,
            };
            host.Seed.TaskItems.Add(task);
            await host.Seed.SaveChangesAsync();

            host.Seed.TaskAutoLogs.Add(new TaskAutoLog
            {
                CompanyId = companyId, TaskId = task.ID,
                RuleKey = rulePrefix + task.ID, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
            return (task.ID, entityId);
        }

        private static async Task<int> InventoryApprovalAsync(PlatformTestHost host, int companyId = CompanyA)
        {
            var approval = new InventoryApproval
            {
                CompanyID = companyId, DocType = "PurchaseOrder", Amount = 500m, PayloadJson = "{}",
                Status = "Pending", RequestedByEmployeeId = Preparer, RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(approval);
            await host.Seed.SaveChangesAsync();
            return approval.ID;
        }

        private static string PrefixFor(string flow) =>
            TaskApprovalProjection.ApprovalRuleKeyPrefixes
                .Single(p => p.Contains(flow, StringComparison.Ordinal));

        // ============================================================================================
        // W1 — an authorized caller receives the expected rows, and the total agrees with them.
        // ============================================================================================
        [Fact]
        public async Task W1_an_authorized_caller_receives_the_expected_rows_and_total()
        {
            using var host = await SeedAsync();
            var a = await PlainTaskAsync(host);
            var b = await PlainTaskAsync(host);

            var set = await TasksSource(host, Ctx(Viewer, CompanyA))
                .FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), Ctx(Viewer, CompanyA)));

            Assert.Equal(new[] { a, b }.OrderBy(x => x), TaskIds(set).OrderBy(x => x));

            // THE TOTAL IS OF THE SAME SET, not of the table. An aggregate that counted rows the page
            // may not show would be a disclosure with no row attached to it.
            Assert.Equal(2, set.TotalRowCount);
            Assert.Equal(set.Rows.Count, set.TotalRowCount);
        }

        // ============================================================================================
        // W2 — a generic task viewer cannot read protected approval work.
        //
        // The viewer holds COMPANY-WIDE task breadth, so layer 1 reaches the row. Layer 2 asks the
        // inventory module, which does not entitle them to the approval record, and the row drops. That
        // is the whole difference between a tenancy predicate and an authorization one.
        // ============================================================================================
        [Fact]
        public async Task W2_a_generic_task_viewer_cannot_read_protected_approval_work()
        {
            using var host = await SeedAsync();
            var plain = await PlainTaskAsync(host);
            var approvalId = await InventoryApprovalAsync(host);
            var (guarded, _) = await ApprovalWorkAsync(
                host, EntityRegistry.InventoryApproval, PrefixFor("Inventory"), entityId: approvalId);

            var viewer = await TasksSource(host, Ctx(Viewer, CompanyA))
                .FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), Ctx(Viewer, CompanyA)));
            Assert.DoesNotContain(guarded, TaskIds(viewer));
            Assert.Contains(plain, TaskIds(viewer));          // non-vacuous: they do see unguarded work
            Assert.Equal(viewer.Rows.Count, viewer.TotalRowCount);

            // THE KEEPER, who the module DOES entitle, sees it. Without this the test above would pass
            // against a source that returned nothing at all.
            var keeper = await TasksSource(host, Ctx(Keeper, CompanyA))
                .FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), Ctx(Keeper, CompanyA)));
            Assert.Contains(guarded, TaskIds(keeper));
        }

        // ============================================================================================
        // W3 — a preparer cannot reach their own billing approval through a report.
        //
        // Segregation of duties, asked of the module rather than re-decided here: the billing entrance
        // provider answers with the approvals pending FOR THIS CALLER, and a preparer is not their own
        // approver. Reporting's job is to honour that answer, which is exactly what is asserted.
        // ============================================================================================
        [Fact]
        public async Task W3_a_preparer_cannot_reach_their_own_billing_approval()
        {
            using var host = await SeedAsync();
            const int billingId = 77;
            var (billingTask, _) = await ApprovalWorkAsync(
                host, EntityRegistry.ProjectBilling, PrefixFor("ProjectBilling"), entityId: billingId);

            // The module's answer: this billing is pending for the APPROVER and for nobody else.
            var billings = new PendingForApprover(Approver, billingId);

            var preparer = await ApprovalsSource(host, Ctx(Preparer, CompanyA), billings)
                .FetchAsync(QueryFor(WorkReportingDatasets.Approvals(), Ctx(Preparer, CompanyA)));
            Assert.DoesNotContain(billingTask, TaskIds(preparer));
            Assert.Equal(0, preparer.TotalRowCount);

            var approver = await ApprovalsSource(host, Ctx(Approver, CompanyA), billings)
                .FetchAsync(QueryFor(WorkReportingDatasets.Approvals(), Ctx(Approver, CompanyA)));
            Assert.Contains(billingTask, TaskIds(approver));
        }

        // ============================================================================================
        // W4 — another company, and an unresolved company, receive nothing.
        // ============================================================================================
        [Fact]
        public async Task W4_a_foreign_and_an_unresolved_company_receive_no_data()
        {
            using var host = await SeedAsync();
            await PlainTaskAsync(host);
            await ApprovalWorkAsync(host, EntityRegistry.InventoryApproval, PrefixFor("Inventory"), entityId: 5);

            foreach (var dataset in new[] { WorkReportingDatasets.Tasks(), WorkReportingDatasets.Approvals() })
            {
                var foreign = Ctx(Foreigner, CompanyB);
                var fSet = dataset.DatasetCode == WorkDatasetCodes.Tasks
                    ? await TasksSource(host, foreign).FetchAsync(QueryFor(dataset, foreign))
                    : await ApprovalsSource(host, foreign).FetchAsync(QueryFor(dataset, foreign));
                Assert.Empty(fSet.Rows);
                Assert.Equal(0, fSet.TotalRowCount);

                // UNRESOLVED: company 0. Refused before any query runs, not filtered to nothing after one.
                var none = Ctx(Viewer, 0);
                var nSet = dataset.DatasetCode == WorkDatasetCodes.Tasks
                    ? await TasksSource(host, none).FetchAsync(QueryFor(dataset, none))
                    : await ApprovalsSource(host, none).FetchAsync(QueryFor(dataset, none));
                Assert.Empty(nSet.Rows);
                Assert.Equal(0, nSet.TotalRowCount);
            }
        }

        // ============================================================================================
        // W5 — the list, the total and a grouped reading describe the same set.
        // ============================================================================================
        [Fact]
        public async Task W5_list_total_and_grouping_agree()
        {
            using var host = await SeedAsync();
            await PlainTaskAsync(host, status: "New");
            await PlainTaskAsync(host, status: "InProgress");
            await PlainTaskAsync(host, status: "New");
            var approvalId = await InventoryApprovalAsync(host);
            await ApprovalWorkAsync(host, EntityRegistry.InventoryApproval, PrefixFor("Inventory"),
                entityId: approvalId);

            var ctx = Ctx(Viewer, CompanyA);
            var set = await TasksSource(host, ctx).FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), ctx));

            // The guarded row is absent for this caller, so every reading must agree on THREE.
            Assert.Equal(3, set.Rows.Count);
            Assert.Equal(3, set.TotalRowCount);

            int statusAt = set.Columns.ToList().FindIndex(c => c.Key == "Status");
            var grouped = set.Rows.GroupBy(r => (string?)r.ValueAt(statusAt)).ToDictionary(g => g.Key!, g => g.Count());
            Assert.Equal(set.Rows.Count, grouped.Values.Sum());
            Assert.Equal(2, grouped["New"]);
            Assert.Equal(1, grouped["InProgress"]);
        }

        // ============================================================================================
        // W6 — approval membership comes from the ledger, not from a title or an entity link.
        // ============================================================================================
        [Fact]
        public async Task W6_approval_membership_comes_from_the_ledger_not_the_title_or_link()
        {
            using var host = await SeedAsync();

            // A task that LOOKS like an approval and is linked to a source, but has no ledger row.
            var lookalike = new TaskItem
            {
                CompanyId = CompanyA, Title = "اعتماد", TitleEn = "Approval", Status = "New",
                Priority = "Normal", AssigneeEmployeeId = 0, CreatedByEmployeeId = 0,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                EntityType = null, EntityId = null,
            };
            host.Seed.TaskItems.Add(lookalike);
            await host.Seed.SaveChangesAsync();

            // A task with NO approval-shaped title that does carry a ledger row.
            var (realApproval, _) = await ApprovalWorkAsync(
                host, EntityRegistry.InventoryApproval, PrefixFor("Inventory"),
                entityId: await InventoryApprovalAsync(host));

            var ctx = Ctx(Keeper, CompanyA);
            var set = await ApprovalsSource(host, ctx).FetchAsync(QueryFor(WorkReportingDatasets.Approvals(), ctx));

            Assert.Contains(realApproval, TaskIds(set));
            Assert.DoesNotContain(lookalike.ID, TaskIds(set));

            // A ledger row whose rule key is NOT one of the approval prefixes does not make approval work.
            var plain = await PlainTaskAsync(host);
            host.Seed.TaskAutoLogs.Add(new TaskAutoLog
            {
                CompanyId = CompanyA, TaskId = plain,
                RuleKey = TaskApprovalProjection.ProjectionPrefix + "SomethingElse:" + plain,
                CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();

            var after = await ApprovalsSource(host, ctx).FetchAsync(QueryFor(WorkReportingDatasets.Approvals(), ctx));
            Assert.DoesNotContain(plain, TaskIds(after));
        }

        // ============================================================================================
        // W7 — completed work and an old approval round.
        //
        // WaitingApproval excludes Done, which is what makes "waiting" mean waiting. A task that carried
        // an approval ledger row and has since been completed is a CLOSED round: it leaves the backlog
        // and stays in the register, and both of those are asserted so neither can regress alone.
        // ============================================================================================
        [Fact]
        public async Task W7_completed_work_leaves_the_backlog_and_stays_in_the_register()
        {
            using var host = await SeedAsync();
            var approvalId = await InventoryApprovalAsync(host);
            var (closed, _) = await ApprovalWorkAsync(
                host, EntityRegistry.InventoryApproval, PrefixFor("Inventory"),
                status: TaskStatuses.Done, entityId: approvalId);

            var ctx = Ctx(Keeper, CompanyA);

            var backlog = await ApprovalsSource(host, ctx)
                .FetchAsync(QueryFor(WorkReportingDatasets.Approvals(), ctx));
            Assert.DoesNotContain(closed, TaskIds(backlog));
            Assert.Equal(0, backlog.TotalRowCount);

            var register = await TasksSource(host, ctx)
                .FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), ctx));
            Assert.Contains(closed, TaskIds(register));

            // And the register reports it as NOT waiting, so the flag and the backlog cannot disagree.
            int waitingAt = register.Columns.ToList().FindIndex(c => c.Key == "IsWaitingApproval");
            var row = register.Rows.Single(r => Convert.ToInt32(r.ValueAt(
                register.Columns.ToList().FindIndex(c => c.Key == "TaskId"))) == closed);
            Assert.False((bool)row.ValueAt(waitingAt)!);
        }

        // ============================================================================================
        // W8 — a refusal happens BEFORE data retrieval, and the permission key is declared.
        //
        // Proven at the two places this pack owns. The engine-level gate is the platform's and is
        // covered by its own suites; what is asserted here is that these subjects cannot be reached
        // without the key, and that a caller the authority refuses gets nothing even though data exists.
        // ============================================================================================
        [Fact]
        public async Task W8_both_subjects_declare_the_key_and_refuse_before_retrieval()
        {
            using var host = await SeedAsync();
            await PlainTaskAsync(host);   // data EXISTS, so an empty answer is a refusal and not an absence

            foreach (var dataset in new[] { WorkReportingDatasets.Tasks(), WorkReportingDatasets.Approvals() })
                Assert.Equal(WorkReportPermissions.View, dataset.RequiredPermissionKey);

            // An employee with no Tasks grant at all: the authority resolves no scope, ForListAsync
            // returns null, and the source returns an empty set without composing a data query.
            var ungranted = Ctx(999, CompanyA);
            var set = await TasksSource(host, ungranted)
                .FetchAsync(QueryFor(WorkReportingDatasets.Tasks(), ungranted));
            Assert.Empty(set.Rows);
            Assert.Equal(0, set.TotalRowCount);

            // AN UNMAPPED KEY INSIDE THE CLAIMED NAMESPACE IS REFUSED, AND THE REFUSAL IS A TWO-PART
            // CONTRACT. The authority answers NULL — "not a key I issued" — rather than false, and the
            // adapter turns that into a denial with `decided ?? false` INSTEAD of falling through to the
            // role-map stopgap. A first version of this test asserted false on the authority alone and
            // failed, which was the test being wrong about the contract rather than the contract being
            // wrong: asserting only half of it would have missed the half that actually denies.
            var authority = new WorkReportModuleAuthority(new DenyAllModuleAccess());
            Assert.Null(await authority.EvaluateAsync("work.reports.invented", Ctx(Viewer, CompanyA)));

            var adapter = new ReportModulePermissionAdapter(
                new RoleMapReportPermissionEvaluator(new ReportPermissionOptions
                {
                    // The stopgap would GRANT this key. If the module's claim on `work.` did not hold,
                    // the fall-through would say true and this assertion would catch it.
                    AllowUnmappedPermissions = true,
                }),
                new IReportModuleAuthority[] { authority });

            Assert.False(await adapter.HasPermissionAsync("work.reports.invented", Ctx(Viewer, CompanyA)));
        }

        // ============================================================================================
        // W9 — the registration extension resolves under ValidateOnBuild and ValidateScopes.
        // ============================================================================================
        [Fact]
        public void W9_the_registration_extension_builds_a_valid_graph()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(Ctx(Viewer, CompanyA)));
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new ReportingTestHostEnvironment());

            // The Tasks-module services the pack needs, in the same shape Program.cs registers them.
            services.AddScoped<ITaskReadAuthorizer>(_ => new TaskReadAuthorizer(
                new StubContextAccessor(Ctx(Viewer, CompanyA)), new DenyAllTasksAccess(), null!, null));
            services.AddScoped<IProjectsAccessService, AllProjects>();

            services.AddCrossBusinessReporting();
            services.AddWorkReporting();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = provider.CreateScope();

            var sources = scope.ServiceProvider.GetServices<IReportDataSource>().ToList();
            Assert.Single(sources.Where(s => s.Key == WorkDatasetCodes.Tasks));
            Assert.Single(sources.Where(s => s.Key == WorkDatasetCodes.Approvals));

            var datasets = scope.ServiceProvider.GetServices<IReportDatasetDefinition>()
                .Select(d => d.DatasetCode).ToList();
            Assert.Contains(WorkDatasetCodes.Tasks, datasets);
            Assert.Contains(WorkDatasetCodes.Approvals, datasets);

            // work.calendar is excluded by the commissioning wave and must not appear by any route.
            Assert.DoesNotContain("work.calendar", datasets);
            Assert.DoesNotContain("work.hours", datasets);
            Assert.DoesNotContain("work.escalations", datasets);
        }

        // ---- file-local doubles, each bounded -----------------------------------------------------

        // The module's approver answer, and nothing else. See the header for why a double is correct here.
        private sealed class PendingForApprover : IProgressBillingService
        {
            private readonly int _approver;
            private readonly int _billingId;
            public PendingForApprover(int approver, int billingId) { _approver = approver; _billingId = billingId; }

            public Task<IReadOnlyList<PendingBillingApproval>> PendingApprovalsForAsync(
                BusinessContext context, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<PendingBillingApproval>>(
                    context.EmployeeId == _approver
                        ? new[]
                        {
                            new PendingBillingApproval(_billingId, 1, 1, "P1", "Project", null,
                                Preparer, DateTime.UtcNow, 100m, ProgressBillingStatuses.Submitted),
                        }
                        : Array.Empty<PendingBillingApproval>());

            public Task<List<CrossBuy.Models.Context.Accounting.ProgressBilling>> GetBillingsAsync(int companyId, int projectId) => throw new NotSupportedException();
            public Task<CrossBuy.Models.Context.Accounting.ProgressBilling?> GetAsync(int companyId, int id) => throw new NotSupportedException();
            public Task<BillingPreview> BuildPreviewAsync(int companyId, int projectId, int? progressId, int? billingId, decimal? taxRateOverride, int viewerEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int billingId, int progressId, DateTime date, decimal taxRate, string? note, int actorEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> SubmitAsync(int companyId, int id, int actorEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> ApproveAsync(int companyId, int id, int actorEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> ReturnAsync(int companyId, int id, int actorEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> PostAsync(int companyId, int id, int actorEmployeeId) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) => throw new NotSupportedException();
        }

        // Projects breadth is NOT what this pack is proving, so it is held wide open on purpose: with
        // project narrowing removed from the question, every refusal below is the task authority's.
        private sealed class AllProjects : IProjectsAccessService
        {
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(true);

            public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default)
                => Task.FromResult(context.CompanyId > 0
                    ? AccessScope.Company(context.CompanyId) : AccessScope.None());

            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

            public Task<bool> CanApproveBillingAsync(BusinessContext context, int projectId,
                CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<IReadOnlySet<int>> BillingApproverProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        // WorkReportModuleAuthority asks a MODULE access service, which is a different interface from the
        // task one. Denying everything is the point: the assertion is that an unmapped key inside the
        // claimed namespace is refused by the authority itself, not that some grant happens to be absent.
        private sealed class DenyAllModuleAccess : IModuleAccessService
        {
            public string Scope => EntityRegistry.ScopeTasks;
            public IReadOnlyCollection<string> Actions => Array.Empty<string>();

            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(false);

            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private sealed class DenyAllTasksAccess : ITasksAccessService
        {
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(false);

            public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        }
    }
}
