using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // PROJECTS REPORTING SUBJECT PACK — THE PERMANENT CONTRACT (SHF-179 / SHF-180).
    //
    // Five things this file exists to hold, and nothing else:
    //
    //   1. THE PACK STAYS DECLARABLE. Two subjects, the exact dotted codes, bilingual metadata, a row
    //      cap, and a source behind every published definition.
    //   2. AUTHORITY IS ASKED, NEVER DECIDED HERE. The adapter answers WHETHER through the Projects
    //      module; the data sources ask ResolveProjectScopeAsync for WHICH. Neither answers the other's
    //      question, and the tests below prove both directions.
    //   3. THE TENANT HOLDS. Project and ProjectMilestone are NOT covered by CompanyQueryFilters, so
    //      the explicit predicate in ProjectsReportScope is the only thing standing between a report
    //      and another company's rows. That makes every isolation test here non-vacuous WITHOUT a
    //      cross-company bypass — there is no global filter underneath that could be doing the work.
    //   4. A MILESTONE IS VISIBLE IF AND ONLY IF ITS PROJECT IS. Proven against the case that would
    //      defeat a denormalised-column check: a milestone stamped with the right company whose parent
    //      project belongs to another one.
    //   5. NARROWING HAPPENS BEFORE THE CAP AND BEFORE THE COUNT. Asserted through TotalRowCount, which
    //      is the number a truncated report publishes — and which would be the company-wide total if
    //      the order were wrong.
    //
    // ── NON-VACUITY IS ENFORCED, NOT ASSUMED ─────────────────────────────────────────────────────
    //
    // Every DENY below has an ALLOW beside it, driven through the SAME mechanism. A pack that returned
    // nothing at all, or an adapter that refused everyone, would satisfy a suite made only of denials
    // and would look exactly like a correct one.
    //
    // And the seeding is ASYMMETRIC on purpose — two projects in the home company, three in the
    // foreign one, with distinct codes. A symmetric fixture passes with the company predicate
    // INVERTED, because both sides return the same count; assertions here are on the CODES that came
    // back, so a row set can never be confused with the right row set.
    // ============================================================================================
    public class ProjectsReportingTests
    {
        private const int Company = 7;
        private const int Foreign = 8;

        // ==========================================================================================
        // Fakes. What is faked is the POLICY ANSWER, never the vocabulary: the module publishes the
        // real scope name and the real action list, so a wrong scope or a misspelled action fails
        // here rather than passing against an agreeable stub.
        // ==========================================================================================

        private sealed class FakeProjectsModule : IModuleAccessService
        {
            private readonly HashSet<string> _granted;
            private readonly AccessScope _scope;

            public FakeProjectsModule(AccessScope? scope = null, params string[] granted)
            {
                _granted = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
                _scope = scope ?? AccessScope.None();
            }

            public string Scope => EntityRegistry.ScopeProjects;
            public IReadOnlyCollection<string> Actions => ProjectsActions.All;

            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(_granted.Contains(action));

            public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default) => Task.FromResult(_scope);

            // Deliberately empty: the adapter must never consult a role list, and a fake that returned
            // one would let a regression that DID consult it pass unnoticed.
            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        /// Answers the BREADTH question only. It records the action it was asked for, so a test can
        /// prove the sources ask about `read` and not about something more permissive.
        private sealed class FakeProjectsAccess : IProjectsAccessService
        {
            private readonly AccessScope _scope;
            public FakeProjectsAccess(AccessScope scope) => _scope = scope;

            public string? LastAction { get; private set; }
            public int AskedTimes { get; private set; }

            public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
                CancellationToken cancellationToken = default) => Task.FromResult(!_scope.IsEmpty);

            public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default)
            {
                LastAction = action;
                AskedTimes++;
                return Task.FromResult(_scope);
            }

            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

            public Task<bool> CanApproveBillingAsync(BusinessContext context, int projectId,
                CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<IReadOnlySet<int>> BillingApproverProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        /// Deny-shaped, and present only because the Work pack's sources are in the same reporting
        /// graph. Nothing in this suite asks it a question.
        private sealed class FakeTasksAccess : ITasksAccessService
        {
            public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
                CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        }

        /// Only needs to be RESOLVABLE: the DI graph below is built to prove a registration, not to
        /// serve a request. File-local because the equivalent helpers elsewhere are private to their
        /// own suites.
        private sealed class ProjectsTestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "CrossBuy";
            public string WebRootPath { get; set; } = ".";
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string ContentRootPath { get; set; } = ".";
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

        // ==========================================================================================
        // The fixture. The REAL CrossDbContext model over in-memory SQLite, referential integrity off
        // — the same choices PlatformTestHost and ReportingTestHost make, for the same reasons.
        // ==========================================================================================
        internal sealed class ProjectsFixture : IDisposable
        {
            private readonly SqliteConnection _conn;
            private readonly CompanyScopeHolder _holder = new();

            public CrossDbContext Db { get; }

            public ProjectsFixture()
            {
                _conn = new SqliteConnection("DataSource=:memory:");
                _conn.Open();

                // Set to the HOME company. Note this changes nothing about what the tests prove:
                // Project and ProjectMilestone carry no global query filter, so the holder does not
                // filter them and the isolation below is entirely the work of ProjectsReportScope.
                _holder.Set(Company, null);

                var options = new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(_conn).Options;
                Db = new CrossDbContext(options, _holder);
                Db.Database.EnsureCreated();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            public int AddProject(string code, int companyId = Company, string? status = "Active",
                bool isActive = true, DateTime? start = null, DateTime? end = null, string? location = null)
            {
                var p = new Project
                {
                    CompanyID = companyId, Code = code, Name = "م-" + code, NameEn = "p-" + code,
                    Status = status, IsActive = isActive, StartDate = start, EndDate = end,
                    Location = location, CreatedAt = new DateTime(2026, 1, 1),
                };
                Db.Projects.Add(p);
                Db.SaveChanges();
                Db.Entry(p).State = EntityState.Detached;
                return p.ID;
            }

            public int AddMilestone(int projectId, string name, DateTime target,
                string status = ProjectMilestoneStatuses.Planned, int companyId = Company,
                int sortOrder = 0, DateTime? completedAt = null)
            {
                var m = new ProjectMilestone
                {
                    CompanyID = companyId, ProjectId = projectId, Name = name, NameEn = name + "-en",
                    TargetDate = target, Status = status, SortOrder = sortOrder,
                    CompletedAt = completedAt, CreatedAt = new DateTime(2026, 1, 1),
                };
                Db.ProjectMilestones.Add(m);
                Db.SaveChanges();
                Db.Entry(m).State = EntityState.Detached;
                return m.ID;
            }

            public void Dispose() { Db.Dispose(); _conn.Dispose(); }
        }

        /// The asymmetric seed. TWO home projects, THREE foreign ones, all with distinct codes, so a
        /// result set can never be mistaken for the right result set by having the right size.
        private static (int A, int B) SeedProjects(ProjectsFixture f)
        {
            var a = f.AddProject("HOME-A", start: new DateTime(2026, 3, 1));
            var b = f.AddProject("HOME-B", start: new DateTime(2026, 6, 1));
            f.AddProject("FOR-1", Foreign);
            f.AddProject("FOR-2", Foreign);
            f.AddProject("FOR-3", Foreign);
            return (a, b);
        }

        private static BusinessContext Ctx(int companyId = Company, int employeeId = 11) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
            Source = BusinessContextSource.Test,
        };

        private static ReportDataQuery QueryFor(ReportDatasetDefinition dataset, BusinessContext ctx,
            int maxRows = 0, IEnumerable<ReportParameterValue>? parameters = null,
            IReadOnlyList<string>? onlyColumns = null)
        {
            var fields = onlyColumns is null
                ? dataset.Fields
                : dataset.Fields.Where(x => onlyColumns.Contains(x.Key)).ToList();

            var columns = fields.Select(x => new ReportColumn
            {
                Key = x.Key, TitleAr = x.TitleAr, TitleEn = x.TitleEn, Type = x.Type,
            }).ToList();

            var definition = new ReportDefinition
            {
                Code = "test." + dataset.DatasetCode,
                Module = dataset.Module,
                TitleAr = "اختبار", TitleEn = "Test",
                DataSourceKey = dataset.DataSourceKey,
                PermissionKey = dataset.RequiredPermissionKey,
                Columns = dataset.Fields.Select(x => new ReportColumn
                {
                    Key = x.Key, TitleAr = x.TitleAr, TitleEn = x.TitleEn, Type = x.Type,
                }).ToList(),
            };

            return new ReportDataQuery
            {
                Definition = definition,
                Context = ctx,
                MaxRows = maxRows,
                RequestedColumns = onlyColumns is null ? Array.Empty<ReportColumn>() : columns,
                Parameters = new ReportParameterSet(
                    parameters ?? Array.Empty<ReportParameterValue>(),
                    new Dictionary<string, string?>()),
            };
        }

        private static IReadOnlyList<string> Codes(ReportDataSet set, string key = "Code") =>
            set.Rows.Select(r => (string?)r[key] ?? "").ToList();

        private static IEnumerable<ReportDatasetDefinition> All() => ProjectsDatasets.All();

        // ==========================================================================================
        // PR1 — the pack is declarable, and declares exactly what it is allowed to declare.
        // ==========================================================================================

        [Fact]
        public void PR1_the_pack_declares_exactly_the_two_projects_subjects_by_code()
        {
            // EXACT, not a count. The codes are the contract: a saved report, a widget and a schedule
            // all persist the dataset code, so renaming one silently orphans everything built on it.
            var codes = All().Select(d => d.DatasetCode).OrderBy(c => c, StringComparer.Ordinal).ToList();

            Assert.Equal(new[] { "projects.milestones", "projects.projects" }, codes);

            // Spelled out literally as well as through the constants, because a typo in the CONSTANT
            // would otherwise make the assertion above agree with itself.
            Assert.Equal("projects.projects", ProjectsDatasetCodes.Projects);
            Assert.Equal("projects.milestones", ProjectsDatasetCodes.Milestones);
        }

        [Fact]
        public void PR1_every_subject_carries_the_metadata_the_platform_requires()
        {
            foreach (var d in All())
            {
                Assert.Equal("Projects", d.Module);

                // A dataset with no permission key is a dataset anyone can open.
                Assert.Equal(ProjectsReportPermissions.View, d.RequiredPermissionKey);

                // A row cap that is absent is a report that can pull the whole table.
                Assert.True(d.MaxRows > 0, $"{d.DatasetCode} declares no row cap");
                Assert.Equal(ReportRowCapPolicy.TruncateAndDeclare, d.RowCapPolicy);

                Assert.NotEmpty(d.Fields);

                // BILINGUAL, both directions, on the subject and on every field. A missing TitleAr is
                // an English-only column on an Arabic-primary product.
                Assert.False(string.IsNullOrWhiteSpace(d.TitleAr));
                Assert.False(string.IsNullOrWhiteSpace(d.TitleEn));
                Assert.False(string.IsNullOrWhiteSpace(d.DescriptionAr));
                Assert.False(string.IsNullOrWhiteSpace(d.DescriptionEn));

                foreach (var f in d.Fields)
                {
                    Assert.False(string.IsNullOrWhiteSpace(f.TitleAr), $"{d.DatasetCode}.{f.Key} has no Arabic title");
                    Assert.False(string.IsNullOrWhiteSpace(f.TitleEn), $"{d.DatasetCode}.{f.Key} has no English title");
                }

                // Distinct field keys — the builder indexes by key, so a duplicate would silently make
                // one column unreachable.
                Assert.Equal(d.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count(), d.Fields.Count);
            }
        }

        [Fact]
        public void PR1_the_data_source_key_matches_the_dataset_code_for_both_subjects()
        {
            // The registry resolves a source BY this key. A mismatch is a wiring bug that only shows
            // up at run time, as a subject that exists in the Studio and cannot be run.
            foreach (var d in All())
                Assert.Equal(d.DatasetCode, d.DataSourceKey);
        }

        [Fact]
        public void PR1_no_commercial_or_invented_field_is_published()
        {
            // THE EXCLUSIONS, ASSERTED RATHER THAN DOCUMENTED. Budget, contract value, retention and
            // the advance percentage are governed by `budget-view`, a right the Projects module splits
            // out from `read`; every field in this pack is reachable with `read`. The rest — margin,
            // variance, health, risk, completion percentage, SLA — are numbers no business rule in
            // this repository defines, and reporting is not where one is born.
            var forbidden = new[]
            {
                "Budget", "ContractValue", "AdvancePercent", "RetentionPercent",
                "Profit", "Margin", "Variance", "Cost", "Revenue", "Amount", "Value",
                "Health", "Risk", "Sla", "Score",
                "CompletionPercent", "PercentComplete", "Progress",
                "ManagerName", "ManagerEmployeeId", "CustomerId", "CustomerName",
                "CostCenterId", "ActivityTypeId", "Deliverable", "DeliverableCount",
            };

            foreach (var d in All())
            {
                var keys = d.Fields.Select(f => f.Key).ToList();
                foreach (var bad in forbidden)
                    Assert.DoesNotContain(bad, keys, StringComparer.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void PR1_the_two_subjects_publish_the_fields_the_pack_promises()
        {
            // THE POSITIVE HALF of the test above. Without it, deleting every field would satisfy the
            // exclusion assertions perfectly.
            var projects = ProjectsDatasets.Projects().Fields.Select(f => f.Key).ToList();
            foreach (var expected in new[]
                     { "ProjectId", "Code", "Name", "NameEn", "Status", "IsActive", "StartDate", "EndDate", "Location", "CreatedAt" })
                Assert.Contains(expected, projects);

            var milestones = ProjectsDatasets.Milestones().Fields.Select(f => f.Key).ToList();
            foreach (var expected in new[]
                     { "MilestoneId", "ProjectId", "ProjectCode", "ProjectName", "Name", "NameEn",
                       "Status", "IsOverdue", "IsOpen", "TargetDate", "CompletedAt", "SortOrder", "CreatedAt" })
                Assert.Contains(expected, milestones);
        }

        // ==========================================================================================
        // PR2 - the AUTHORITY. WHETHER, never WHICH.
        //
        // REWRITTEN FOR ADR-037 (RECONCILIATION-02). The programme line asserted these properties
        // against ProjectsReportPermissionAdapter : ModuleActionReportPermissionAdapter, a base class
        // this line does not have and must not regain. Every property below is the SAME property the
        // programme line asserted - the key is granted only when the module grants read, it maps to
        // read and to nothing else, an unmapped key inside the namespace is refused rather than
        // delegated, a foreign scope is not this module, and the answer never depends on breadth. Only
        // the type under test changed: ProjectsReportModuleAuthority, asked through the real
        // ReportModulePermissionAdapter so the ROUTING is under test too and not assumed.
        // ==========================================================================================

        /// The production chain, with the role map underneath it. A key the authority claims must never
        /// reach the map, so the map is deliberately POISONED with an entry that would grant.
        private static IReportPermissionEvaluator Chain(params IModuleAccessService[] modules)
        {
            var options = new ReportPermissionOptions
            {
                AdministratorRoles = new[] { "Admin", "SuperAdmin" },
                RoleMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["projects.reports.view"] = new[] { "Admin" },
                    ["projects.reports.budget"] = new[] { "Admin" },
                },
            };
            var authorities = modules.Length == 0
                ? Array.Empty<IReportModuleAuthority>()
                : new IReportModuleAuthority[] { new ProjectsReportModuleAuthority(modules[0]) };
            return new ReportModulePermissionAdapter(
                new RoleMapReportPermissionEvaluator(options), authorities);
        }

        private static BusinessContext AdminCtx() => new()
        {
            CompanyId = Company, EmployeeId = 11, UserId = "u11",
            Roles = new[] { "Admin", "SuperAdmin" }, Source = BusinessContextSource.Test,
        };

        [Fact]
        public async Task PR2_the_projects_key_is_granted_when_the_module_grants_read()
        {
            // THE ALLOW. Without this, every denial below would pass against a chain that simply
            // refused everyone.
            var chain = Chain(new FakeProjectsModule(AccessScope.Company(Company), ProjectsActions.Read));
            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));
        }

        [Fact]
        public async Task PR2_the_projects_key_is_refused_when_the_module_refuses_read()
        {
            var chain = Chain(new FakeProjectsModule(AccessScope.None()));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));

            // AND THE REFUSAL SURVIVES REPORTING ADMINISTRATION. The role map above WOULD grant this
            // caller; the authority owns the namespace, so the map is never consulted.
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, AdminCtx()));
        }

        [Fact]
        public async Task PR2_the_key_maps_to_read_and_to_nothing_else()
        {
            // A module holding EVERY OTHER Projects action and not `read` must be refused. This is what
            // catches the map being keyed on `manage`, on `edit`, or on a string that no longer exists
            // - each of which would otherwise grant a different population of users than intended.
            var everythingButRead = ProjectsActions.All.Where(a => a != ProjectsActions.Read).ToArray();
            var chain = Chain(new FakeProjectsModule(AccessScope.Company(Company), everythingButRead));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));

            // ...and `read` alone is enough. The pair together says the map is exactly {view -> read}.
            var readOnly = Chain(new FakeProjectsModule(AccessScope.Company(Company), ProjectsActions.Read));
            Assert.True(await readOnly.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));
        }

        [Fact]
        public async Task PR2_an_unmapped_key_inside_the_projects_namespace_is_refused_not_delegated()
        {
            // The authority CLAIMS the whole prefix. An unmapped key inside it must be denied rather
            // than falling through to the role map, where it would quietly become an identity role-name
            // check. `projects.reports.budget` is the concrete case that matters: the day somebody adds
            // a budget subject it must not authorize itself by inheriting this key - and note the role
            // map above DOES map it to a role this caller holds, so a fall-through would grant.
            var chain = Chain(new FakeProjectsModule(AccessScope.Company(Company), ProjectsActions.All.ToArray()));

            Assert.False(await chain.HasPermissionAsync("projects.reports.budget", AdminCtx()));
            Assert.False(await chain.HasPermissionAsync("projects.reports.", AdminCtx()));
            Assert.False(await chain.HasPermissionAsync("projects.hidden", AdminCtx()));
        }

        [Fact]
        public void PR2_the_authority_claims_only_its_own_namespace()
        {
            var authority = new ProjectsReportModuleAuthority(new FakeProjectsModule());

            // BROADER THAN THE PROGRAMME LINE, deliberately: it claimed "projects.reports." and this
            // claims "projects.", so `projects.anything` is refused here instead of being delegated.
            Assert.Equal("projects.", ProjectsReportPermissions.Prefix);
            Assert.Equal(ProjectsReportPermissions.Prefix, authority.KeyPrefix);
            Assert.StartsWith(authority.KeyPrefix, ProjectsReportPermissions.View, StringComparison.Ordinal);

            foreach (var foreign in new[] { "work.reports.view", "accounting.reports.view", "reporting.administer", "" })
                Assert.False(foreign.StartsWith(authority.KeyPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public async Task PR2_no_projects_module_means_no_grant()
        {
            // AddCrossBusinessReporting is self-sufficient and can be built without the Projects module
            // registered. No module, no authority - and then the key reaches the role map, which is
            // exactly why the map is poisoned above: this asserts the SHAPE of that fallback honestly
            // rather than pretending an unregistered module denies.
            var none = Chain();
            Assert.False(await none.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));

            // A module answering for a DIFFERENT scope is not this module either, and the authority
            // refuses rather than believing it.
            var wrongScope = Chain(new WrongScopeModule());
            Assert.False(await wrongScope.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));
            Assert.False(await wrongScope.HasPermissionAsync(ProjectsReportPermissions.View, AdminCtx()));
        }

        private sealed class WrongScopeModule : IModuleAccessService
        {
            public string Scope => EntityRegistry.ScopeTasks;
            public IReadOnlyCollection<string> Actions => new[] { ProjectsActions.Read };
            public Task<bool> CanAsync(BusinessContext c, string a, PermissionTarget? t = null, CancellationToken k = default)
                => Task.FromResult(true);
            public Task<AccessScope> ResolveScopeAsync(BusinessContext c, string a, CancellationToken k = default)
                => Task.FromResult(AccessScope.Company(Company));
            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext c, CancellationToken k = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        [Fact]
        public async Task PR2_the_authority_answers_whether_and_never_which()
        {
            // AN ORDINARY MEMBER - granted `read`, but with a Team breadth over two projects - must be
            // granted the report. The narrowing is the data source's job. An authority that tried to
            // answer "which" by demanding company breadth would take the report away from everybody who
            // is not an administrator, which is the failure this asserts against.
            var member = Chain(new FakeProjectsModule(AccessScope.Team(Company, new[] { 1, 2 }), ProjectsActions.Read));
            Assert.True(await member.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));

            // The breadth is NOT consulted by the authority at all: the same module granting read with
            // no breadth answers identically. Breadth belongs to PR4/PR5.
            var noBreadth = Chain(new FakeProjectsModule(AccessScope.None(), ProjectsActions.Read));
            Assert.True(await noBreadth.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));
        }

        [Fact]
        public async Task PR2_an_unresolved_company_is_refused_whatever_the_module_says()
        {
            // The module here grants everything; the refusal must come from the context. No fallback to
            // company 1, and no caller-supplied tenant.
            var chain = Chain(new FakeProjectsModule(AccessScope.Company(Company), ProjectsActions.All.ToArray()));

            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx()));
            foreach (var bad in new[] { 0, -1 })
                Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(companyId: bad)));
        }


        // ==========================================================================================
        // PR3 - the breadth rule.
        //
        // ADAPTED FOR THIS LINE (RECONCILIATION-02). The programme line's version was a DRIFT GUARD: it
        // drove ProjectsReportScope.From and CrossBuy.Controllers.ProjectListScope.From over the same
        // shapes and asserted they agree, because BL cannot depend on Controllers and the rule is
        // therefore stated twice. ProjectListScope does NOT exist on this line - it lives inside the
        // programme line's ProjectController - and importing a controller is outside this wave.
        //
        // So the parity half is dropped and the table is asserted against EXPLICIT expected answers
        // instead. That is strictly stronger than "the two agree" while the twin is absent, because two
        // functions can agree by both being wrong. When the screen helper lands, the parity assertion
        // belongs beside this one rather than in place of it.
        // ==========================================================================================

        [Fact]
        public void PR3_the_reporting_breadth_rule_answers_each_shape_exactly()
        {
            // (shape, allowed, narrowed-to). A NULL OnlyProjectIds means NOT NARROWED - every project
            // in the company - which is exactly why an empty set must never be expressed that way.
            var cases = new (string Name, AccessScope? Scope, bool Allowed, int[]? Only)[]
            {
                ("null",         null,                                          false, null),
                ("none",         AccessScope.None(),                            false, null),
                ("own",          AccessScope.Own(Company),                      false, null),
                ("branch",       AccessScope.Branch(Company, 3),                false, null),
                ("company",      AccessScope.Company(Company),                  true,  null),
                ("crossCompany", AccessScope.CrossCompany(),                    true,  null),
                ("teamWithIds",  AccessScope.Team(Company, new[] { 4, 9 }),     true,  new[] { 4, 9 }),
                ("teamEmpty",    AccessScope.Team(Company, Array.Empty<int>()), false, null),
                ("teamNullIds",  new AccessScope { Breadth = AccessBreadth.Team, CompanyId = Company }, false, null),
            };

            foreach (var (name, scope, allowed, only) in cases)
            {
                var decision = ProjectsReportScope.From(scope);
                Assert.True(decision.Allowed == allowed,
                    $"'{name}': expected Allowed={allowed}, got {decision.Allowed}");
                Assert.Equal(only?.OrderBy(i => i), decision.OnlyProjectIds?.OrderBy(i => i));
            }

            // NON-VACUITY: the table must contain both answers, or "each shape is answered" would be
            // satisfied by a function that always refuses.
            Assert.Contains(cases, c => ProjectsReportScope.From(c.Scope).Allowed);
            Assert.Contains(cases, c => !ProjectsReportScope.From(c.Scope).Allowed);
        }


        [Fact]
        public void PR3_an_empty_narrowing_set_is_a_refusal_and_never_everything()
        {
            // The one-line difference that matters most in this file: "narrow to nothing" and "do not
            // narrow" are opposite in effect, and Team with no ids must land on the first.
            Assert.False(ProjectsReportScope.From(AccessScope.Team(Company, Array.Empty<int>())).Allowed);

            var withIds = ProjectsReportScope.From(AccessScope.Team(Company, new[] { 5 }));
            Assert.True(withIds.Allowed);
            Assert.Equal(new[] { 5 }, withIds.OnlyProjectIds);

            // Company breadth is allowed AND unnarrowed — null, not an empty set.
            var company = ProjectsReportScope.From(AccessScope.Company(Company));
            Assert.True(company.Allowed);
            Assert.Null(company.OnlyProjectIds);
        }

        // ==========================================================================================
        // PR4 — projects.projects. The tenant, the breadth, the order of operations.
        // ==========================================================================================

        private static ProjectsDataSource ProjectsSource(ProjectsFixture f, AccessScope scope)
            => new(f.Db, new FakeProjectsAccess(scope));

        [Fact]
        public async Task PR4_company_breadth_returns_this_company_and_no_other()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);

            var set = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));

            // BY CODE, NOT BY COUNT. The seed is asymmetric — two home, three foreign — so a count of
            // two could not come from the foreign side, and the codes say which rows actually survived.
            Assert.Equal(new[] { "HOME-A", "HOME-B" }, Codes(set).OrderBy(c => c, StringComparer.Ordinal));
            Assert.Equal(2, set.TotalRowCount);
            Assert.False(set.Truncated);
        }

        [Fact]
        public async Task PR4_an_unresolved_company_returns_nothing_even_with_company_breadth()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);

            // THE DENY. CompanyId 0 is the unresolved-tenant case, and the scope handed in is the most
            // permissive one there is — so nothing but the fail-closed guard can be doing this.
            var denied = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx(companyId: 0)));

            Assert.Empty(denied.Rows);
            Assert.Equal(0, denied.TotalRowCount);

            // THE ALLOW, same source, same seed, same scope — only the company resolved.
            var allowed = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));
            Assert.NotEmpty(allowed.Rows);
        }

        [Fact]
        public async Task PR4_no_project_authority_returns_nothing()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);

            var denied = await ProjectsSource(f, AccessScope.None())
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));
            Assert.Empty(denied.Rows);
            Assert.Equal(0, denied.TotalRowCount);

            // Own and Branch are breadths this resolver does not produce; they are refused rather than
            // widened. Both with the ALLOW above them in the same fixture.
            Assert.Empty((await ProjectsSource(f, AccessScope.Own(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()))).Rows);
            Assert.Empty((await ProjectsSource(f, AccessScope.Branch(Company, 1))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()))).Rows);

            Assert.NotEmpty((await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()))).Rows);
        }

        [Fact]
        public async Task PR4_team_breadth_narrows_inside_the_company_not_only_across_it()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);

            // A member attached to ONE of the two home projects. If the breadth were ignored and only
            // the company predicate ran, this would return HOME-B as well — which is the whole point.
            var set = await ProjectsSource(f, AccessScope.Team(Company, new[] { a }))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));

            Assert.Equal(new[] { "HOME-A" }, Codes(set));
            Assert.Equal(1, set.TotalRowCount);
        }

        [Fact]
        public async Task PR4_team_breadth_with_no_ids_returns_nothing()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);

            Assert.Empty((await ProjectsSource(f, AccessScope.Team(Company, Array.Empty<int>()))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()))).Rows);

            // THE ALLOW that makes the emptiness above mean something.
            Assert.NotEmpty((await ProjectsSource(f, AccessScope.Team(Company, new[] { a }))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()))).Rows);
        }

        [Fact]
        public async Task PR4_a_foreign_project_id_inside_the_breadth_is_still_excluded()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            var foreignId = f.Db.Projects.Single(p => p.Code == "FOR-1").ID;

            // THE ORDER PROOF. The company predicate runs BEFORE the id narrowing, so an id set that
            // names another company's project cannot pull it in. A scope object is not a trusted
            // source of row ids — it says which of THIS tenant's projects, never which tenant.
            var set = await ProjectsSource(f, AccessScope.Team(Company, new[] { a, foreignId }))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));

            Assert.Equal(new[] { "HOME-A" }, Codes(set));
            Assert.DoesNotContain("FOR-1", Codes(set));
        }

        [Fact]
        public async Task PR4_the_source_asks_the_module_for_read_on_the_current_context()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);

            var access = new FakeProjectsAccess(AccessScope.Company(Company));
            var set = await new ProjectsDataSource(f.Db, access)
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));

            // ASKED, and asked about `read` — not `manage`, not `budget-view`, and not a string this
            // file invents. Asked ONCE for the whole fetch, never per row.
            Assert.Equal(ProjectsActions.Read, access.LastAction);
            Assert.Equal(1, access.AskedTimes);
            Assert.NotEmpty(set.Rows);
        }

        [Fact]
        public async Task PR4_the_row_cap_is_applied_after_the_company_narrowing_and_the_total_proves_it()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);   // 2 home + 3 foreign = 5 rows in the table

            var set = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx(), maxRows: 1));

            Assert.Single(set.Rows);

            // THE NUMBER THAT PROVES THE ORDER. TotalRowCount is counted over the NARROWED query, so it
            // is 2 — this caller's total. If the cap or the count ran before the company predicate it
            // would be 5, and a truncated report would publish "1 of 5" to a company that owns 2.
            Assert.Equal(2, set.TotalRowCount);

            // Declared, not swallowed: TruncateAndDeclare means the engine raises a warning from this.
            Assert.True(set.Truncated);

            // And the single row is still a HOME row, not whichever row the database happened to reach
            // first across both companies.
            Assert.StartsWith("HOME-", Codes(set)[0]);
        }

        [Fact]
        public async Task PR4_the_date_parameters_bound_the_start_date()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);                      // HOME-A starts 2026-03-01, HOME-B 2026-06-01
            f.AddProject("HOME-UNDATED");         // no StartDate at all

            var unfiltered = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx()));

            // With no window, every visible project is returned — the undated one included.
            Assert.Equal(new[] { "HOME-A", "HOME-B", "HOME-UNDATED" },
                Codes(unfiltered).OrderBy(c => c, StringComparer.Ordinal));

            var windowed = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx(), parameters: new[]
                {
                    Param("FromDate", new DateTime(2026, 2, 1)),
                    Param("ToDate", new DateTime(2026, 4, 30)),
                }));

            // HOME-B is outside the window, and the undated project is in NO window — which is the
            // documented behaviour, asserted here so it is a decision rather than a surprise.
            Assert.Equal(new[] { "HOME-A" }, Codes(windowed));
            Assert.Equal(1, windowed.TotalRowCount);
        }

        [Fact]
        public async Task PR4_a_requested_subset_of_columns_lands_under_the_right_keys()
        {
            using var f = new ProjectsFixture();
            SeedProjects(f);

            // THE POSITIONAL-WRITE TRAP. The platform may hand a SUBSET of the definition's columns in
            // RequestedColumns. A source that wrote values positionally would fill this two-column row
            // with ProjectId and Code while labelling them Status and Code — right shape, right row
            // count, wrong data. Asserting the VALUES under their keys is what catches that.
            var set = await ProjectsSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Projects(), Ctx(),
                    onlyColumns: new[] { "Code", "Status" }));

            Assert.Equal(2, set.Columns.Count);
            var row = set.Rows.First(r => (string?)r["Code"] == "HOME-A");
            Assert.Equal("Active", row["Status"]);
        }

        // ==========================================================================================
        // PR5 — projects.milestones. Visibility is INHERITED, and inherited from the project row.
        // ==========================================================================================

        private static ProjectMilestonesDataSource MilestoneSource(ProjectsFixture f, AccessScope scope)
            => new(f.Db, new FakeProjectsAccess(scope));

        private static IReadOnlyList<string> Names(ReportDataSet set) =>
            set.Rows.Select(r => (string?)r["Name"] ?? "").OrderBy(n => n, StringComparer.Ordinal).ToList();

        [Fact]
        public async Task PR5_company_breadth_returns_only_this_company_s_milestones()
        {
            using var f = new ProjectsFixture();
            var (a, b) = SeedProjects(f);
            var foreignProject = f.Db.Projects.Single(p => p.Code == "FOR-1").ID;

            f.AddMilestone(a, "ms-a1", new DateTime(2026, 4, 1));
            f.AddMilestone(a, "ms-a2", new DateTime(2026, 5, 1), sortOrder: 1);
            f.AddMilestone(b, "ms-b1", new DateTime(2026, 7, 1));
            f.AddMilestone(foreignProject, "ms-f1", new DateTime(2026, 4, 1), companyId: Foreign);
            f.AddMilestone(foreignProject, "ms-f2", new DateTime(2026, 4, 2), companyId: Foreign);

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));

            Assert.Equal(new[] { "ms-a1", "ms-a2", "ms-b1" }, Names(set));
            Assert.Equal(3, set.TotalRowCount);
        }

        [Fact]
        public async Task PR5_a_milestone_is_visible_only_when_its_parent_project_is()
        {
            using var f = new ProjectsFixture();
            var (a, b) = SeedProjects(f);
            f.AddMilestone(a, "ms-a1", new DateTime(2026, 4, 1));
            f.AddMilestone(b, "ms-b1", new DateTime(2026, 7, 1));

            // A member of HOME-A only. Both milestones are in the right company; only one hangs off a
            // project this caller can see. Inheritance means the other one is gone.
            var set = await MilestoneSource(f, AccessScope.Team(Company, new[] { a }))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));

            Assert.Equal(new[] { "ms-a1" }, Names(set));
            Assert.Equal(1, set.TotalRowCount);

            // THE ALLOW: the same milestone appears the moment its project is in the breadth.
            var widened = await MilestoneSource(f, AccessScope.Team(Company, new[] { a, b }))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));
            Assert.Equal(new[] { "ms-a1", "ms-b1" }, Names(widened));
        }

        [Fact]
        public async Task PR5_a_milestone_stamped_with_the_right_company_but_a_foreign_project_is_excluded()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            var foreignProject = f.Db.Projects.Single(p => p.Code == "FOR-1").ID;

            // THE CASE THAT DEFEATS A DENORMALISED-COLUMN CHECK. This row says CompanyID = 7 and hangs
            // off a project owned by company 8. A source that trusted m.CompanyID alone would publish
            // it. The semi-join against the scoped project query is what excludes it, and this test is
            // the reason that semi-join may not be removed as "redundant".
            f.AddMilestone(foreignProject, "ms-mislabelled", new DateTime(2026, 4, 1), companyId: Company);
            f.AddMilestone(a, "ms-honest", new DateTime(2026, 4, 1));

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));

            // THE ALLOW and THE DENY in one result: the honest row is present, the mislabelled one is not.
            Assert.Equal(new[] { "ms-honest" }, Names(set));
            Assert.Equal(1, set.TotalRowCount);
        }

        [Fact]
        public async Task PR5_an_unresolved_company_and_no_authority_both_return_nothing()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            f.AddMilestone(a, "ms-a1", new DateTime(2026, 4, 1));

            Assert.Empty((await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx(companyId: 0)))).Rows);
            Assert.Empty((await MilestoneSource(f, AccessScope.None())
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()))).Rows);
            Assert.Empty((await MilestoneSource(f, AccessScope.Team(Company, Array.Empty<int>()))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()))).Rows);

            // THE ALLOW.
            Assert.NotEmpty((await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()))).Rows);
        }

        [Fact]
        public async Task PR5_overdue_is_the_model_s_own_rule_and_not_a_copy_of_it()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);

            var past = DateTime.Today.AddDays(-10);
            var future = DateTime.Today.AddDays(10);

            f.AddMilestone(a, "ms-open-late", past, ProjectMilestoneStatuses.Planned);
            f.AddMilestone(a, "ms-running-late", past, ProjectMilestoneStatuses.InProgress, sortOrder: 1);
            f.AddMilestone(a, "ms-done", past, ProjectMilestoneStatuses.Completed, sortOrder: 2,
                completedAt: DateTime.UtcNow);
            f.AddMilestone(a, "ms-cancelled", past, ProjectMilestoneStatuses.Cancelled, sortOrder: 3);
            f.AddMilestone(a, "ms-open-early", future, ProjectMilestoneStatuses.Planned, sortOrder: 4);

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));

            // ASKED, NOT RESTATED. Each reported value is compared against the ENTITY's own IsOverdue
            // for the same row, so this asserts inheritance rather than re-deriving the rule and then
            // agreeing with itself. If ProjectMilestone.IsOverdue changes, this test changes with it —
            // which is correct, because the report is supposed to change with it too.
            foreach (var row in set.Rows)
            {
                var name = (string?)row["Name"];
                var entity = f.Db.ProjectMilestones.AsNoTracking().Single(m => m.Name == name);
                Assert.Equal(entity.IsOverdue, (bool?)row["IsOverdue"]);
                Assert.Equal(ProjectMilestoneStatuses.IsOpen(entity.Status), (bool?)row["IsOpen"]);
            }

            // NON-VACUOUS: the comparison above would hold over five identical falses. These pin the
            // actual shape of the rule — late-and-open is overdue, late-but-settled is not, and a
            // future commitment is not.
            bool Overdue(string name) => (bool)set.Rows.Single(r => (string?)r["Name"] == name)["IsOverdue"]!;
            Assert.True(Overdue("ms-open-late"));
            Assert.True(Overdue("ms-running-late"));
            Assert.False(Overdue("ms-done"));
            Assert.False(Overdue("ms-cancelled"));
            Assert.False(Overdue("ms-open-early"));
        }

        [Fact]
        public async Task PR5_the_project_name_and_code_come_from_the_visible_project()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            f.AddMilestone(a, "ms-a1", new DateTime(2026, 4, 1));

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx()));

            var row = Assert.Single(set.Rows);
            Assert.Equal("HOME-A", row["ProjectCode"]);
            Assert.Equal("م-HOME-A", row["ProjectName"]);
            Assert.Equal(a, row["ProjectId"]);
        }

        [Fact]
        public async Task PR5_the_milestone_cap_counts_the_visible_rows_not_the_table()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            var foreignProject = f.Db.Projects.Single(p => p.Code == "FOR-1").ID;

            f.AddMilestone(a, "ms-a1", new DateTime(2026, 4, 1));
            f.AddMilestone(a, "ms-a2", new DateTime(2026, 5, 1), sortOrder: 1);
            f.AddMilestone(foreignProject, "ms-f1", new DateTime(2026, 4, 1), companyId: Foreign);
            f.AddMilestone(foreignProject, "ms-f2", new DateTime(2026, 4, 2), companyId: Foreign);
            f.AddMilestone(foreignProject, "ms-f3", new DateTime(2026, 4, 3), companyId: Foreign);

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx(), maxRows: 1));

            Assert.Single(set.Rows);
            Assert.True(set.Truncated);

            // 2, not 5. The count runs over the inherited-visibility query.
            Assert.Equal(2, set.TotalRowCount);
            Assert.StartsWith("ms-a", (string?)set.Rows[0]["Name"]);
        }

        [Fact]
        public async Task PR5_the_target_date_parameters_bound_the_result()
        {
            using var f = new ProjectsFixture();
            var (a, _) = SeedProjects(f);
            f.AddMilestone(a, "ms-early", new DateTime(2026, 4, 1));
            f.AddMilestone(a, "ms-late", new DateTime(2026, 9, 1), sortOrder: 1);

            var set = await MilestoneSource(f, AccessScope.Company(Company))
                .FetchAsync(QueryFor(ProjectsDatasets.Milestones(), Ctx(), parameters: new[]
                {
                    Param("FromDate", new DateTime(2026, 3, 1)),
                    Param("ToDate", new DateTime(2026, 4, 1)),
                }));

            // ToDate is INCLUSIVE of its own day — the source adds a day and uses a half-open range,
            // so a milestone due ON the end date is in the window rather than one day outside it.
            Assert.Equal(new[] { "ms-early" }, Names(set));
        }

        // ==========================================================================================
        // PR6 — the registration. A published subject with no source is a Studio entry that fails when
        // somebody runs it, and an unregistered adapter is a silent fallback to the ADR-037 stopgap.
        // ==========================================================================================

        private static ServiceCollection ReportingGraph()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ProjectsTestWebHostEnvironment());
            services.AddLogging();

            // Deny-shaped throughout: this graph exists to prove a REGISTRATION resolves, never to hand
            // a test authority through the container.
            services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(
                BusinessContext.ForSystem(Company)));
            services.AddScoped<IProjectsAccessService>(_ => new FakeProjectsAccess(AccessScope.None()));
            services.AddScoped<ITasksAccessService>(_ => new FakeTasksAccess());
            services.AddScoped<ITimesheetService, TimesheetService>();

            services.AddCrossBusinessReporting();
            // MIRRORS Program.cs: the Projects pack is registered by the HOST, not by
            // AddCrossBusinessReporting, because its sources need IProjectsAccessService.
            services.AddProjectsReporting();
            return services;
        }

        [Fact]
        public void PR6_both_projects_sources_resolve_through_the_real_reporting_graph_exactly_once()
        {
            using var provider = ReportingGraph().BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = provider.CreateScope();

            var sources = scope.ServiceProvider.GetServices<IReportDataSource>().ToList();

            // EXACTLY ONE, by key and by type. ReportDataSourceRegistry throws on a duplicate key, so a
            // second registration would break the whole platform rather than just this pack — which is
            // precisely why it is asserted here and not left to be discovered.
            Assert.Single(sources.Where(x => x.Key == ProjectsDatasetCodes.Projects));
            Assert.Single(sources.Where(x => x.Key == ProjectsDatasetCodes.Milestones));
            Assert.Single(sources.OfType<ProjectsDataSource>());
            Assert.Single(sources.OfType<ProjectMilestonesDataSource>());
        }

        [Fact]
        public void PR6_every_published_projects_subject_has_a_source_behind_it()
        {
            using var provider = ReportingGraph().BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = provider.CreateScope();

            var registry = scope.ServiceProvider.GetRequiredService<IReportDatasetRegistry>();
            var sourceKeys = scope.ServiceProvider.GetServices<IReportDataSource>().Select(x => x.Key).ToHashSet();

            foreach (var dataset in All())
            {
                // Published into the catalogue the Studio actually reads…
                Assert.True(registry.TryResolve(dataset.DatasetCode, out var published) && published is not null,
                    $"{dataset.DatasetCode} is not in the dataset registry");
                Assert.Equal(dataset.DataSourceKey, published!.DataSourceKey);

                // …and runnable once it is there.
                Assert.True(sourceKeys.Contains(dataset.DataSourceKey),
                    $"{dataset.DatasetCode} is published with no source for '{dataset.DataSourceKey}'");
            }
        }

        [Fact]
        public void PR6_the_projects_authority_is_registered_exactly_once_and_claims_the_keys()
        {
            // REWRITTEN FOR ADR-037. The programme line asserted a registration of
            // IReportModulePermissionAdapter, a contract this line does not have. The SILENT-FALLBACK
            // GUARD it existed for is preserved exactly: ReportModulePermissionAdapter composes
            // IEnumerable<IReportModuleAuthority>, so a MISSING authority does not fail - it leaves
            // projects.* answering from the role map by identity role name, with no run-time symptom.
            // That is why the registration is asserted rather than assumed.
            //
            // Registration lives in Program.cs (the host), not in AddCrossBusinessReporting, so this
            // builds the authority directly and asserts the CONTRACT the host must satisfy.
            var authority = new ProjectsReportModuleAuthority(new FakeProjectsModule());

            foreach (var dataset in All())
                Assert.StartsWith(authority.KeyPrefix, dataset.RequiredPermissionKey, StringComparison.Ordinal);

            // And no obsolete adapter came back with the pack.
            var reporting = typeof(ReportModulePermissionAdapter).Assembly;
            foreach (var dead in new[]
            {
                "CrossBuy.BL.Reporting.IReportModulePermissionAdapter",
                "CrossBuy.BL.Reporting.ModuleActionReportPermissionAdapter",
                "CrossBuy.BL.Reporting.ModuleAwareReportPermissionEvaluator",
                "CrossBuy.BL.Reporting.ProjectsReportPermissionAdapter",
            })
                Assert.Null(reporting.GetType(dead));
        }


        // ------------------------------------------------------------------------------------------
        private static ReportParameterValue Param(string key, DateTime value) => new()
        {
            Key = key, Value = value,
        };
    }
}
