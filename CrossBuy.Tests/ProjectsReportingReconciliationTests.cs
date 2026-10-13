using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static CrossBuy.Tests.B6TestWiring;

namespace CrossBuy.Tests
{
    // ==================================================================================================
    // RECONCILIATION-02 — what the transplanted pack's own tests do NOT prove.
    //
    // ProjectsReportingTests came from the programme line and proves the pack's metadata, its breadth
    // rule, its row scope and its milestone semi-join. It proves those against FAKES of the Projects
    // module, which is right for testing the pack. It cannot prove:
    //
    //   * that the REAL ProjectsAccessService produces the breadths the pack relies on,
    //   * that an external-portal or worker identity is refused by the real module,
    //   * that a company-A grant cannot answer a company-B question,
    //   * that reporting administration cannot widen the row scope, as opposed to being refused the key,
    //   * and what the CATALOGUE now contains - in particular that the Work pack did NOT land.
    //
    // Those are this file's job, and every one of them uses the real module and the real chain.
    // ==================================================================================================
    public class ProjectsReportingReconciliationTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Caller = 10;
        private const int Somebody = 999;

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId, params string[] roles) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = roles, Source = BusinessContextSource.Test, CorrelationId = Guid.NewGuid(),
        };

        private static IPlatformRoleDirectory Directory(PlatformTestHost host)
            => new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance);

        private static ProjectsAccessService Projects(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(host.Db, http, accessor, Policies(host.Db),
                NullLogger<AccountingAccessService>.Instance);
            return new ProjectsAccessService(host.Db, Directory(host), accounting,
                NullLogger<ProjectsAccessService>.Instance);
        }

        /// The production chain, with a role map that WOULD grant every key under test. Any pass that
        /// comes from the map rather than from the module is therefore visible as a failure.
        private static IReportPermissionEvaluator Chain(PlatformTestHost host) =>
            new ReportModulePermissionAdapter(
                new RoleMapReportPermissionEvaluator(new ReportPermissionOptions
                {
                    AdministratorRoles = new[] { "Admin", "SuperAdmin" },
                    RoleMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["projects.reports.view"] = new[] { "Admin" },
                        ["projects.reports.budget"] = new[] { "Admin" },
                    },
                }),
                new IReportModuleAuthority[] { new ProjectsReportModuleAuthority(Projects(host)) });

        private static async Task GrantAsync(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = EntityRegistry.ScopeProjects,
                PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId,
                Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        private static PlatformTestHost Host()
        {
            var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(Caller, CompanyOne));
            host.Seed.Employee.Add(Emp(Somebody, CompanyOne));
            host.Seed.SaveChanges();
            return host;
        }

        // ==============================================================================================
        // 26 — REPORTING ADMINISTRATION CANNOT WIDEN THE ROW SCOPE.
        //
        // Being refused the KEY is one property and the pack's own tests cover the shape of it. This is
        // the other one: even where the key IS held, reporting roles must not change WHICH rows the
        // module resolves. The two are different failures and only this one is about breadth.
        // ==============================================================================================

        [Fact]
        public async Task Reporting_administration_neither_grants_the_key_nor_widens_the_breadth()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, Somebody, ProjectsRoles.ProjectsViewer);   // closes bootstrap-open

            var plain = Ctx(Caller, CompanyOne);
            var admin = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");

            // The key: refused for both, though the role map above maps it to "Admin".
            Assert.False(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, plain));
            Assert.False(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, admin));

            // The breadth: identical for both, and NOT Company. Reporting roles are not in the module's
            // answer at all - if they were, the two lines below would differ.
            var asPlain = await Projects(host).ResolveProjectScopeAsync(plain, ProjectsActions.Read);
            var asAdmin = await Projects(host).ResolveProjectScopeAsync(admin, ProjectsActions.Read);
            Assert.Equal(asPlain.Breadth, asAdmin.Breadth);
            Assert.NotEqual(AccessBreadth.Company, asAdmin.Breadth);
        }

        [Fact]
        public async Task A_member_resolves_to_their_own_projects_and_an_administrator_to_the_company()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, Somebody, ProjectsRoles.ProjectsViewer);

            host.Seed.Projects.Add(new Project { ID = 41, CompanyID = CompanyOne, Name = "p41", Code = "P41" });
            host.Seed.Projects.Add(new Project { ID = 42, CompanyID = CompanyOne, Name = "p42", Code = "P42" });
            host.Seed.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = 41, EmployeeId = Caller, CompanyID = CompanyOne,
                RoleOnProject = ProjectMemberRoles.Member, IsActive = true,
            });
            await host.Seed.SaveChangesAsync();

            // A MEMBER: Team, carrying PROJECT ids - not employee ids - and only the one they are on.
            var member = await Projects(host).ResolveProjectScopeAsync(Ctx(Caller, CompanyOne), ProjectsActions.Read);
            Assert.Equal(AccessBreadth.Team, member.Breadth);
            Assert.Equal(new[] { 41 }, member.PrincipalIds!.OrderBy(i => i));
            Assert.DoesNotContain(42, member.PrincipalIds!);

            // AN ADMINISTRATOR: Company, with no id list at all. Asymmetric on purpose - a fixture where
            // both answers looked alike would pass with the membership predicate inverted.
            await GrantAsync(host, CompanyOne, Caller, ProjectsRoles.ProjectsAdministrator);
            var admin = await Projects(host).ResolveProjectScopeAsync(Ctx(Caller, CompanyOne), ProjectsActions.Read);
            Assert.Equal(AccessBreadth.Company, admin.Breadth);
        }

        // ==============================================================================================
        // 29-33 — CONTEXT, against the REAL module.
        // ==============================================================================================

        [Fact]
        public async Task An_unresolved_company_is_refused_for_a_caller_who_otherwise_passes()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, Caller, ProjectsRoles.ProjectsAdministrator);
            var chain = Chain(host);

            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
            foreach (var bad in new[] { 0, -1 })
            {
                Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, bad)));

                // And the row scope agrees - there is no fallback to company 1 on either side.
                var scope = await Projects(host).ResolveProjectScopeAsync(Ctx(Caller, bad), ProjectsActions.Read);
                Assert.Equal(AccessBreadth.None, scope.Breadth);
            }
        }

        [Fact]
        public async Task An_external_portal_or_worker_identity_cannot_satisfy_internal_projects_authority()
        {
            using var host = Host();
            var chain = Chain(host);

            foreach (var source in new[] { BusinessContextSource.ExternalPortal, BusinessContextSource.Worker })
            {
                var ctx = new BusinessContext
                {
                    CompanyId = CompanyOne, EmployeeId = null, UserId = "outside",
                    Roles = new[] { "Admin", "SuperAdmin" }, Source = source,
                };
                Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, ctx));

                var scope = await Projects(host).ResolveProjectScopeAsync(ctx, ProjectsActions.Read);
                Assert.Equal(AccessBreadth.None, scope.Breadth);
            }

            // NON-VACUITY: the company is bootstrap-open, so an EMPLOYEE in it passes. Without this the
            // refusals above would be satisfied by a chain that refuses everyone.
            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
        }

        [Fact]
        public async Task A_grant_in_company_two_cannot_answer_company_ones_question()
        {
            using var host = Host();
            await GrantAsync(host, CompanyTwo, Caller, ProjectsRoles.ProjectsAdministrator);
            await GrantAsync(host, CompanyOne, Somebody, ProjectsRoles.ProjectsViewer);   // closes company 1

            var chain = Chain(host);

            // Asymmetric by construction: the grant exists in exactly one company.
            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyTwo)));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));

            var inTwo = await Projects(host).ResolveProjectScopeAsync(Ctx(Caller, CompanyTwo), ProjectsActions.Read);
            var inOne = await Projects(host).ResolveProjectScopeAsync(Ctx(Caller, CompanyOne), ProjectsActions.Read);
            Assert.Equal(AccessBreadth.Company, inTwo.Breadth);
            Assert.NotEqual(AccessBreadth.Company, inOne.Breadth);
        }

        // ==============================================================================================
        // 41-43 — THE CATALOGUE. What landed, and just as importantly what did NOT.
        // ==============================================================================================

        [Fact]
        public void The_reconciled_catalogue_carries_exactly_the_two_projects_subjects()
        {
            var codes = ProjectsDatasets.All().Select(d => d.DatasetCode).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { "projects.milestones", "projects.projects" }, codes);

            // Both authorize on the one canonical key, and nothing else.
            foreach (var d in ProjectsDatasets.All())
                Assert.Equal(ProjectsReportPermissions.View, d.RequiredPermissionKey);
        }

        [Fact]
        public void The_two_work_subjects_landed_and_deferred_subjects_remain_absent()
        {
            // Foundation convergence lands only tasks and approvals. Code presence is distinct
            // from runtime activation, which still requires the deployed Hold schema.
            Assert.NotNull(typeof(CrossBuy.Models.Context.Tasks.TaskItem).GetProperty("HoldApprovedAtUtc"));
            Assert.NotNull(typeof(CrossBuy.BL.TasksCalendar.ITaskEscalationService)
                .GetMethod("CurrentlyEscalatedTaskIdsAsync"));
            var reporting = typeof(ReportModulePermissionAdapter).Assembly;
            Assert.NotNull(reporting.GetType("CrossBuy.BL.Platform.Orchestration.LeaveApprovalRule"));
            Assert.NotNull(reporting.GetType("CrossBuy.BL.Platform.Orchestration.EmployeeRequestApprovalRule"));
            Assert.NotNull(reporting.GetType("CrossBuy.BL.Platform.Orchestration.InventoryApprovalRule"));
            Assert.NotNull(reporting.GetType("CrossBuy.BL.Platform.Orchestration.ProjectBillingApprovalRule"));
            Assert.NotNull(reporting.GetType("CrossBuy.BL.TasksCalendar.TaskApprovalProjection"));
            foreach (var present in new[]
            {
                "CrossBuy.BL.Reporting.WorkReportingDatasets",
                "CrossBuy.BL.Reporting.WorkTasksDataSource",
                "CrossBuy.BL.Reporting.WorkApprovalsDataSource",
            })
                Assert.NotNull(reporting.GetType(present));
            foreach (var absent in new[]
            {
                "CrossBuy.BL.Reporting.WorkHoursDataSource",
                "CrossBuy.BL.Reporting.WorkCalendarDataSource",
                "CrossBuy.BL.Reporting.WorkEscalationsDataSource",
            })
                Assert.Null(reporting.GetType(absent));

            // work.calendar has no key, no code and no dataset anywhere on this line.
            Assert.DoesNotContain("work.calendar",
                ProjectsDatasets.All().Select(d => d.DatasetCode));

            // The existing authority owns the namespace; convergence adds no duplicate vocabulary.
            Assert.Equal("work.", WorkReportPermissions.Prefix);
        }

        [Fact]
        public void No_obsolete_donor_permission_type_came_back_with_the_pack()
        {
            var reporting = typeof(ReportModulePermissionAdapter).Assembly;
            foreach (var dead in new[]
            {
                "CrossBuy.BL.Reporting.IReportModulePermissionAdapter",
                "CrossBuy.BL.Reporting.ModuleActionReportPermissionAdapter",
                "CrossBuy.BL.Reporting.ModuleAwareReportPermissionEvaluator",
                "CrossBuy.BL.Reporting.ProjectsReportPermissionAdapter",
                "CrossBuy.BL.Reporting.WorkReportPermissionAdapter",
            })
                Assert.Null(reporting.GetType(dead));

            // And exactly ONE definition of the permission vocabulary, on the authority's side.
            Assert.NotNull(reporting.GetType("CrossBuy.BL.Reporting.ProjectsReportPermissions"));
            Assert.Equal("projects.reports.view", ProjectsReportPermissions.View);
        }

        // ==============================================================================================
        // 32 — DRILL-DOWN. There is no client-supplied identifier to re-authorize, and that is the proof.
        // ==============================================================================================

        [Fact]
        public void Neither_subject_exposes_a_row_identifier_the_caller_could_name()
        {
            foreach (var d in ProjectsDatasets.All())
            {
                // No drill-down hierarchy is declared, so the engine offers no navigation that could
                // carry a row id back to the server.
                Assert.Empty(d.DrillDowns);

                // And every declared parameter is a DATE. A ProjectId or MilestoneId parameter would be
                // the one shape that lets a caller name a row the breadth excluded - so its absence is
                // asserted rather than assumed, and this fails the moment one is added without a
                // re-authorization story.
                foreach (var p in d.Parameters)
                    Assert.True(p.Type == ReportFieldType.Date,
                        $"{d.DatasetCode} parameter '{p.Key}' is {p.Type}; only Date parameters are " +
                        "proven safe here, an identifier parameter needs its own authorization test");
            }
        }

        // ==============================================================================================
        // THE GRAPH — the pack resolves, and the authority contract the host must satisfy holds.
        // ==============================================================================================

        [Fact]
        public void Both_projects_sources_resolve_once_through_the_registered_reporting_graph()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CrossBuy.Models.Context.CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddLogging();
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ReconWebHostEnvironment());
            services.AddScoped<IBusinessContextAccessor>(_ => new ReconStubContext());
            services.AddScoped<IProjectsAccessService>(_ => new ReconDenyProjects());
            services.AddScoped<ITasksAccessService>(_ => new ReconDenyTasks());
            services.AddScoped<ITimesheetService, TimesheetService>();
            services.AddCrossBusinessReporting();
            // MIRRORS Program.cs: the Projects pack is registered by the HOST, not by
            // AddCrossBusinessReporting, because its sources need IProjectsAccessService.
            services.AddProjectsReporting();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
            using var scope = provider.CreateScope();

            var sources = scope.ServiceProvider.GetServices<IReportDataSource>().ToList();
            Assert.Single(sources.Where(s => s.Key == ProjectsDatasetCodes.Projects));
            Assert.Single(sources.Where(s => s.Key == ProjectsDatasetCodes.Milestones));

            // NO WORK SOURCE RESOLVED, by key. This is the registration-side twin of the type-absence
            // test above: a half-withdrawn pack would leave a source behind here.
            Assert.Empty(sources.Where(s => s.Key.StartsWith("work.", StringComparison.Ordinal)));

            var datasets = scope.ServiceProvider.GetServices<IReportDatasetDefinition>().ToList();
            Assert.Single(datasets.Where(d => d.DatasetCode == ProjectsDatasetCodes.Projects));
            Assert.Single(datasets.Where(d => d.DatasetCode == ProjectsDatasetCodes.Milestones));
            Assert.Empty(datasets.Where(d => d.DatasetCode.StartsWith("work.", StringComparison.Ordinal)));
        }

        private sealed class ReconWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = System.IO.Path.GetTempPath();
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
                = new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string ApplicationName { get; set; } = "CrossBuy.Tests";
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
                = new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string ContentRootPath { get; set; } = System.IO.Path.GetTempPath();
            public string EnvironmentName { get; set; } = "Test";
        }

        private sealed class ReconStubContext : IBusinessContextAccessor
        {
            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(BusinessContext.ForSystem(CompanyOne));

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<BusinessContext?>(BusinessContext.ForSystem(CompanyOne));
        }

        // Deny-shaped on purpose: this graph proves a REGISTRATION resolves, never hands a test authority.
        private sealed class ReconDenyProjects : IProjectsAccessService
        {
            public Task<bool> CanAsync(BusinessContext c, string a, PermissionTarget? t = null, CancellationToken k = default)
                => Task.FromResult(false);
            public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string a, CancellationToken k = default)
                => Task.FromResult(AccessScope.None());
            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken k = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        private sealed class ReconDenyTasks : ITasksAccessService
        {
            public Task<bool> CanAsync(BusinessContext c, string a, PermissionTarget? t = null, CancellationToken k = default)
                => Task.FromResult(false);
            public Task<AccessScope> ResolveScopeAsync(BusinessContext c, string a, CancellationToken k = default)
                => Task.FromResult(AccessScope.None());
        }
    }
}
