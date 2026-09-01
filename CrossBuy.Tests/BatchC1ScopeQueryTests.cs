using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch C.1 item 1 — THE SET-BASED QUERY PROOF.
    //
    // `ResolveScopeAsync` was delivered in Batch C with an agreement test against `CanAsync`, but nothing proved
    // the property it exists for: that authorizing a LIST costs one permission evaluation rather than one per
    // row, and that the filtering happens in the database rather than in memory after everything was loaded.
    //
    // The evaluation-count tests below are the centre of this file. They do not measure time — a timing
    // assertion would be flaky and would not say what went wrong. They COUNT the permission evaluations by
    // interposing a counting IPlatformRoleDirectory (every evaluation, whether CanAsync or ResolveScopeAsync,
    // must consult the role directory) and assert the count is the SAME for 10 rows and for 500. A regression to
    // per-row CanAsync moves that number from 1 to N and fails loudly.
    public class BatchC1ScopeQueryTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        // ============================================================================================
        // fixtures
        // ============================================================================================

        private static Employee Emp(int id, int companyId, bool active = true) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private sealed class AllowAllProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(BusinessContext c, string entityType, int entityId, string action,
                CancellationToken ct = default) => Task.FromResult(PermissionDecision.Allow("test"));
        }

        // Counts every permission evaluation by counting role-directory consultations. Both CanAsync and
        // ResolveScopeAsync go through RolesAsync, so this is a faithful count of "how many times did we ask the
        // permission system a question" — the number the no-N+1 requirement is about.
        private sealed class CountingRoleDirectory : IPlatformRoleDirectory
        {
            private readonly IPlatformRoleDirectory _inner;
            public int RoleQueries;
            public int ConfiguredQueries;
            public CountingRoleDirectory(IPlatformRoleDirectory inner) { _inner = inner; }

            public Task<IReadOnlyList<RoleGrant>> RolesAsync(
                BusinessContext context, string scope, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref RoleQueries);
                return _inner.RolesAsync(context, scope, cancellationToken);
            }

            public Task<bool> AnyConfiguredAsync(int companyId, string scope, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref ConfiguredQueries);
                return _inner.AnyConfiguredAsync(companyId, scope, cancellationToken);
            }
        }

        // Counts hierarchy resolutions too: a per-row implementation would re-walk the org tree for every row,
        // which is the second way this path degrades.
        private sealed class CountingOrgHierarchy : IOrgHierarchy
        {
            private readonly IOrgHierarchy _inner;
            public int Walks;
            public CountingOrgHierarchy(IOrgHierarchy inner) { _inner = inner; }

            public Task<IReadOnlySet<int>> DirectAndIndirectReportsAsync(
                int companyId, int managerEmployeeId, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Walks);
                return _inner.DirectAndIndirectReportsAsync(companyId, managerEmployeeId, cancellationToken);
            }

            // COUNTED TOO, and delegated rather than stubbed. This double exists to prove the batch path does
            // not re-walk the org tree per row, so every tree read has to go through the counter - a member that
            // returned a canned value would let a per-row manager lookup slip through the very test that is
            // supposed to catch it.
            public Task<int?> DirectManagerAsync(
                int companyId, int employeeId, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Walks);
                return _inner.DirectManagerAsync(companyId, employeeId, cancellationToken);
            }
        }

        private static IPlatformRoleDirectory RealDirectory(PlatformTestHost host)
            => new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance);

        private static IOrgHierarchy RealOrg(PlatformTestHost host)
            => new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance);

        private static TasksAccessService Tasks(
            PlatformTestHost host, IPlatformRoleDirectory? roles = null, IOrgHierarchy? org = null)
        {
            var provider = new AllowAllProvider();
            return new TasksAccessService(host.Db, roles ?? RealDirectory(host), org ?? RealOrg(host),
                () => provider, NullLogger<TasksAccessService>.Instance);
        }

        private static async Task GrantAsync(
            PlatformTestHost host, int companyId, string scope, int employeeId, string role)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = scope, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        private static TaskItem NewTask(int companyId, int assignee, int creator) => new()
        {
            CompanyId = companyId, Title = "t", AssigneeEmployeeId = assignee,
            CreatedByEmployeeId = creator, Status = "New", CreatedAt = DateTime.UtcNow,
        };

        // ============================================================================================
        // 1. EACH BREADTH TRANSLATES TO THE RIGHT SET
        // ============================================================================================

        [Fact]
        public async Task An_empty_scope_selects_no_rows_rather_than_every_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.TaskItems.AddRange(NewTask(CompanyOne, 10, 10), NewTask(CompanyOne, 11, 11));
            await host.Seed.SaveChangesAsync();

            var rows = await host.Db.TaskItems.WithinScope(AccessScope.None(), 10).ToListAsync();

            // The failure this guards: returning `source` untouched for an empty scope, which would turn
            // "sees nothing" into "sees everything" for any caller that just enumerates the result.
            Assert.Empty(rows);
        }

        [Fact]
        public async Task Own_breadth_selects_only_the_callers_own_tasks()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 10, 99),     // assigned to me
                NewTask(CompanyOne, 99, 10),     // created by me
                NewTask(CompanyOne, 11, 11));    // neither
            await host.Seed.SaveChangesAsync();

            var rows = await host.Db.TaskItems
                .WithinScope(AccessScope.Own(CompanyOne), 10).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, t => Assert.True(t.AssigneeEmployeeId == 10 || t.CreatedByEmployeeId == 10));
        }

        [Fact]
        public async Task Team_breadth_selects_the_teams_tasks_and_nothing_outside_it()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 10, 10),     // the manager
                NewTask(CompanyOne, 11, 11),     // a report
                NewTask(CompanyOne, 12, 12));    // outside the team
            await host.Seed.SaveChangesAsync();

            var scope = AccessScope.Team(CompanyOne, new[] { 10, 11 });
            var rows = await host.Db.TaskItems.WithinScope(scope, 10).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain(rows, t => t.AssigneeEmployeeId == 12);
        }

        [Fact]
        public async Task Company_breadth_selects_the_whole_company_and_only_that_company()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 10, 10), NewTask(CompanyOne, 11, 11),
                NewTask(CompanyTwo, 20, 20));
            await host.Seed.SaveChangesAsync();

            var rows = await host.Db.TaskItems
                .WithinScope(AccessScope.Company(CompanyOne), 10).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, t => Assert.Equal(CompanyOne, t.CompanyId));
        }

        // ---- the breadth the DATA does not support ----
        //
        // Required by the C.1 brief, and the honest answer is that TaskItem has no BranchId, so Branch cannot be
        // proven for tasks — it can only be REFUSED. Neither silent widening (to Company) nor silent narrowing
        // (to Own) is acceptable: both answer a question the data cannot answer.
        [Fact]
        public void A_branch_scope_is_refused_because_TaskItem_has_no_branch_column()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);

            var ex = Assert.Throws<NotSupportedException>(() =>
                host.Db.TaskItems.WithinScope(AccessScope.Branch(CompanyOne, 5), 10).ToQueryString());

            Assert.Contains("BranchId", ex.Message);
        }

        [Fact]
        public void A_cross_company_scope_is_refused_here_and_left_to_the_audited_bypass()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);

            Assert.Throws<NotSupportedException>(() =>
                host.Db.TaskItems.WithinScope(AccessScope.CrossCompany(), 10).ToQueryString());
        }

        [Fact]
        public void A_company_scoped_breadth_with_no_company_refuses_rather_than_returning_everything()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var malformed = new AccessScope { Breadth = AccessBreadth.Company, CompanyId = null };

            Assert.Throws<InvalidOperationException>(() =>
                host.Db.TaskItems.WithinScope(malformed, 10).ToQueryString());
        }

        // ============================================================================================
        // 2. THE FILTER RUNS IN THE DATABASE, NOT IN MEMORY
        // ============================================================================================

        // The requirement "do not load all tasks before filtering" is a property of the generated SQL, so that
        // is what is asserted. ToQueryString shows the command EF will send: a company predicate must be in it.
        [Fact]
        public void The_scope_predicate_is_translated_into_the_sql_where_clause()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);

            var sql = host.Db.TaskItems
                .WithinScope(AccessScope.Team(CompanyOne, new[] { 10, 11 }), 10)
                .ToQueryString();

            Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CompanyId", sql, StringComparison.OrdinalIgnoreCase);
        }

        // Paging composes ON TOP of the predicate and still executes server-side — the property that makes the
        // IQueryable return type worth having. If the predicate had been applied in memory, LIMIT would apply to
        // the unfiltered set and page 1 would contain other people's rows.
        [Fact]
        public async Task Paging_composes_on_top_of_the_scope_predicate_in_one_sql_statement()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            for (int i = 0; i < 40; i++) host.Seed.TaskItems.Add(NewTask(CompanyOne, 10, 10));
            for (int i = 0; i < 40; i++) host.Seed.TaskItems.Add(NewTask(CompanyOne, 12, 12));
            await host.Seed.SaveChangesAsync();

            var query = host.Db.TaskItems
                .WithinScope(AccessScope.Own(CompanyOne), 10)
                .OrderBy(t => t.ID).Skip(5).Take(10);

            var sql = query.ToQueryString();
            Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);

            var page = await query.ToListAsync();
            Assert.Equal(10, page.Count);
            Assert.All(page, t => Assert.Equal(10, t.AssigneeEmployeeId));   // never employee 12's rows
        }

        // ============================================================================================
        // 3. THE COUNT DOES NOT GROW WITH THE ROW COUNT  ← the point of the whole item
        // ============================================================================================

        private static async Task<(int roleQueries, int walks, int rows)> AuthorizeListAsync(
            PlatformTestHost host, int rowCount, int callerEmployeeId)
        {
            for (int i = 0; i < rowCount; i++)
                host.Seed.TaskItems.Add(NewTask(CompanyOne, callerEmployeeId, callerEmployeeId));
            await host.Seed.SaveChangesAsync();

            var roles = new CountingRoleDirectory(RealDirectory(host));
            var org = new CountingOrgHierarchy(RealOrg(host));
            var tasks = Tasks(host, roles, org);
            var ctx = Ctx(callerEmployeeId, CompanyOne);

            // THE WHOLE PATTERN: resolve once…
            var scope = await tasks.ResolveScopeAsync(ctx, TasksActions.Read);
            // …then one predicate for the entire list. No CanAsync in this loop, because there is no loop.
            var rows = await host.Db.TaskItems.WithinScope(scope, ctx.EmployeeId).CountAsync();

            return (roles.RoleQueries, org.Walks, rows);
        }

        [Fact]
        public async Task Authorizing_ten_rows_and_five_hundred_rows_costs_the_same_number_of_evaluations()
        {
            int roleQueriesSmall, walksSmall, roleQueriesLarge, walksLarge;

            using (var small = new PlatformTestHost(companyId: CompanyOne))
            {
                small.Seed.Employee.Add(Emp(10, CompanyOne));
                await small.Seed.SaveChangesAsync();
                await GrantAsync(small, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksAdministrator);

                var (rq, w, rows) = await AuthorizeListAsync(small, 10, 10);
                roleQueriesSmall = rq; walksSmall = w;
                Assert.Equal(10, rows);
            }

            using (var large = new PlatformTestHost(companyId: CompanyOne))
            {
                large.Seed.Employee.Add(Emp(10, CompanyOne));
                await large.Seed.SaveChangesAsync();
                await GrantAsync(large, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksAdministrator);

                var (rq, w, rows) = await AuthorizeListAsync(large, 500, 10);
                roleQueriesLarge = rq; walksLarge = w;
                Assert.Equal(500, rows);
            }

            // 50× the data, IDENTICAL permission cost. This is the assertion that fails the moment someone
            // reintroduces a per-row CanAsync — the count would become 10 vs 500.
            Assert.Equal(roleQueriesSmall, roleQueriesLarge);
            Assert.Equal(walksSmall, walksLarge);

            // And the absolute number is one evaluation, not merely a constant one.
            Assert.Equal(1, roleQueriesLarge);
        }

        // The control that gives the test above its meaning: the per-row shape really does cost N. Without this,
        // "the count is 1" could be true of a path that never authorized anything.
        [Fact]
        public async Task The_per_row_shape_this_replaces_really_does_cost_one_evaluation_per_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksAdministrator);

            var ids = new List<int>();
            for (int i = 0; i < 12; i++)
            {
                var t = NewTask(CompanyOne, 10, 10);
                host.Seed.TaskItems.Add(t);
                await host.Seed.SaveChangesAsync();
                ids.Add(t.ID);
            }

            var roles = new CountingRoleDirectory(RealDirectory(host));
            var tasks = Tasks(host, roles);
            var ctx = Ctx(10, CompanyOne);

            foreach (var id in ids)
                Assert.True(await tasks.CanAsync(ctx, TasksActions.Read, PermissionTarget.ForTask(id)));

            Assert.Equal(ids.Count, roles.RoleQueries);   // 12 rows ⇒ 12 evaluations
        }

        // ============================================================================================
        // 4. COMPANY ISOLATION AND THE HIERARCHY INTERSECTION, THROUGH THE SET PATH
        // ============================================================================================

        [Fact]
        public async Task The_resolved_set_never_contains_another_companys_task_even_for_an_administrator()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 10, 10),
                NewTask(CompanyTwo, 20, 20),     // another company
                NewTask(CompanyTwo, 10, 10));    // SAME employee id, other company — the id is not the anchor
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksAdministrator);

            var tasks = Tasks(host);
            var ctx = Ctx(10, CompanyOne);

            var scope = await tasks.ResolveScopeAsync(ctx, TasksActions.Read);
            var rows = await host.Db.TaskItems.WithinScope(scope, ctx.EmployeeId).ToListAsync();

            Assert.Single(rows);
            Assert.All(rows, t => Assert.Equal(CompanyOne, t.CompanyId));
        }

        // Hierarchical carries no CompanyID, so the team set is where a cross-company graft would leak in. The
        // set path must inherit the same intersection the record path has.
        [Fact]
        public async Task A_cross_company_report_grafted_into_the_tree_does_not_widen_the_resolved_set()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },   // manager
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 },      // real report
                new Hierarchical { H_ID = 3, H_Type = 5, H_ObjectID = 20, H_Parent = 1 });     // ANOTHER company
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 11, 11),     // the real report's task
                NewTask(CompanyOne, 20, 20));    // the grafted employee, but a company-1 row
            await host.Seed.SaveChangesAsync();

            var tasks = Tasks(host);
            var scope = await tasks.ResolveScopeAsync(Ctx(10, CompanyOne), TasksActions.Read);

            Assert.Equal(AccessBreadth.Team, scope.Breadth);
            Assert.Contains(11, scope.PrincipalIds!);
            Assert.DoesNotContain(20, scope.PrincipalIds!);

            var rows = await host.Db.TaskItems.WithinScope(scope, 10).ToListAsync();
            Assert.Single(rows);
            Assert.Equal(11, rows[0].AssigneeEmployeeId);
        }

        [Fact]
        public async Task A_worker_context_resolves_to_nothing_and_the_query_returns_nothing()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.TaskItems.Add(NewTask(CompanyOne, 10, 10));
            await host.Seed.SaveChangesAsync();

            var worker = new BusinessContext
            {
                CompanyId = CompanyOne, EmployeeId = null, UserId = "", Roles = Array.Empty<string>(),
                CorrelationId = Guid.NewGuid(), Source = BusinessContextSource.Worker,
            };

            var scope = await Tasks(host).ResolveScopeAsync(worker, TasksActions.Read);

            Assert.True(scope.IsEmpty);
            Assert.Empty(await host.Db.TaskItems.WithinScope(scope, worker.EmployeeId).ToListAsync());
        }

        // ============================================================================================
        // 5. CanAsync AND THE RESOLVED SET AGREE — ROW BY ROW, OVER THE WHOLE TABLE
        // ============================================================================================

        // The drift this catches is the expensive kind: a list screen showing rows the detail screen then
        // refuses, or worse, a list screen showing rows the detail screen would also have shown but should not.
        // Every row in the table is checked BOTH ways and the two answers must be identical.
        //
        // Scope note: the linked-entity gate in CanAsync is cross-module and cannot be expressed as a task
        // predicate, so these fixtures use tasks with no linked entity. That is a real limitation of the set
        // path, stated rather than hidden: the resolved set is a NECESSARY condition, and a consumer that opens
        // a specific task still calls CanAsync, which is where the linked-entity rule applies.
        [Theory]
        [InlineData(false, false)]   // bootstrap-open, no grants at all
        [InlineData(true, false)]    // an ordinary employee in a configured company
        [InlineData(true, true)]     // a supervisor
        public async Task Every_row_gets_the_same_answer_from_CanAsync_and_from_the_resolved_set(
            bool configured, bool supervisor)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 });
            host.Seed.TaskItems.AddRange(
                NewTask(CompanyOne, 10, 10),     // mine
                NewTask(CompanyOne, 11, 12),     // my report's, created by an outsider
                NewTask(CompanyOne, 12, 10),     // an outsider's, created by me
                NewTask(CompanyOne, 12, 12),     // nothing to do with me
                NewTask(CompanyTwo, 10, 10));    // my employee id, another company
            await host.Seed.SaveChangesAsync();

            if (configured)
                await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);
            if (supervisor)
                await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksSupervisor);

            var tasks = Tasks(host);
            var ctx = Ctx(10, CompanyOne);

            var scope = await tasks.ResolveScopeAsync(ctx, TasksActions.Read);
            var visible = (await host.Db.TaskItems.WithinScope(scope, ctx.EmployeeId)
                .Select(t => t.ID).ToListAsync()).ToHashSet();

            var all = await host.Db.TaskItems.AsNoTracking().Select(t => t.ID).ToListAsync();
            Assert.NotEmpty(all);

            foreach (var id in all)
            {
                bool byRecord = await tasks.CanAsync(ctx, TasksActions.Read, PermissionTarget.ForTask(id));
                bool bySet = visible.Contains(id);
                Assert.True(byRecord == bySet,
                    $"Task {id}: CanAsync said {byRecord} but the resolved set said {bySet} " +
                    $"(configured={configured}, supervisor={supervisor}, breadth={scope.Breadth})");
            }
        }

        // `manage` is company-administration only: a non-administrator resolves to nothing, and the set must
        // agree rather than falling back to "own tasks".
        [Fact]
        public async Task Manage_resolves_to_nothing_for_a_non_administrator_and_the_set_is_empty()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            host.Seed.TaskItems.Add(NewTask(CompanyOne, 10, 10));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 10, TasksRoles.TasksViewer);

            var tasks = Tasks(host);
            var ctx = Ctx(10, CompanyOne);

            var scope = await tasks.ResolveScopeAsync(ctx, TasksActions.Manage);
            Assert.True(scope.IsEmpty);
            Assert.Empty(await host.Db.TaskItems.WithinScope(scope, ctx.EmployeeId).ToListAsync());
            Assert.False(await tasks.CanAsync(ctx, TasksActions.Manage, PermissionTarget.ForTask(1)));
        }
    }
}
