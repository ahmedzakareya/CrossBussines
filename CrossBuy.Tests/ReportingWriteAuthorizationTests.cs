using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // Reporting Platform — R3 PHASE 1: THE SEVEN WRITES.
    //
    // The endpoints themselves are parked in ReportsCenterWriteEndpoints.cs.pending because CBA001 blocks
    // them until TAB 1 declares IReportAuthorizationService an authority. THESE TESTS ARE NOT PARKED, and
    // that is the point: the authorization they rely on lives in the SERVICE layer, which is compiled,
    // registered and reachable today. Testing it here rather than through HTTP means activation is a
    // rename against already-green tests, not a fresh test-writing exercise under time pressure.
    //
    // Phase 1 names ten scenarios. Eight were already proven by ReportingTemplateScopeTests when the
    // template engine was built, and duplicating them here would be two places to maintain one rule:
    //
    //   personal ownership    → Another_employees_personal_template_is_never_resolved_for_me
    //   team scope            → A_team_template_resolves_only_for_members_of_that_team
    //   platform scope        → A_tenant_cannot_create_a_platform_template
    //                           A_platform_template_is_runnable_by_all_and_editable_by_nobody
    //   unauthorized write    → A_share_cannot_grant_access_the_module_permission_denies
    //                           Nobody_can_grant_more_than_they_hold
    //   cross-company write   → A_template_from_another_company_is_refused
    //   deletion constraints  → Deleting_a_template_is_a_soft_delete_so_archive_rows_are_not_orphaned
    //   favourite idempotency → Favouriting_is_idempotent_and_hides_reports_whose_permission_was_revoked
    //
    // What was NOT covered, and is added below: COMPANY SCOPE as a write boundary, STALE VERSION, and
    // REORDER ISOLATION. Plus one test that pins the CBA001 gate itself, so the parked state cannot drift
    // out of sync with reality unnoticed.
    // =============================================================================================
    public class ReportingWriteAuthorizationTests
    {
        private const int Team = 40;
        private const int Colleague = 8;

        private static ReportLayout Layout(params string[] columns) => new()
        {
            VisibleColumns = columns.Length > 0 ? columns : new[] { "Branch", "Amount" },
        };

        private static ReportTemplateInput Input(string name, ReportTemplateScope scope, int id = 0,
            int? teamId = null) => new()
            {
                Id = id,
                ReportCode = TestReportDefinitions.SalesCode,
                Name = name,
                Scope = scope,
                TeamId = teamId,
                Layout = Layout(),
            };

        // ============================================================================================
        // COMPANY SCOPE — the gap between "everyone may run it" and "everyone may change it"
        // ============================================================================================

        // A Company template is the company standard: everybody who passed the report gate RUNS it. That is
        // exactly why editing it must not follow from running it — otherwise one person redefines the number
        // the whole company reads, and the permission that allowed it was "may view sales".
        [Fact]
        public async Task A_company_template_is_runnable_by_all_but_editable_only_by_its_owner()
        {
            using var host = new ReportingTestHost();

            var created = await host.Templates.SaveAsync(
                Input("Company standard", ReportTemplateScope.Company), host.Ctx);
            Assert.True(created.Success);

            var template = await host.Db.ReportTemplates.AsNoTracking()
                .SingleAsync(t => t.Id == created.TemplateId);
            var definition = host.Catalog.GetDefinition(TestReportDefinitions.SalesCode);

            // The OWNER holds Manage.
            var owner = await host.Authorization.AuthorizeTemplateAsync(
                definition, template, ReportAccessLevel.Manage, host.Ctx);
            Assert.True(owner.Allowed);

            // A COLLEAGUE in the same company, holding the same module permission, may RUN it…
            var colleagueContext = new BusinessContext
            {
                CompanyId = host.Ctx.CompanyId,
                EmployeeId = Colleague,
                UserId = "colleague",
                Roles = host.Ctx.Roles,
                Source = BusinessContextSource.Test,
            };

            var run = await host.Authorization.AuthorizeTemplateAsync(
                definition, template, ReportAccessLevel.Run, colleagueContext);
            Assert.True(run.Allowed);

            // …and may NOT edit it. Running and changing are different rights over the same row.
            var edit = await host.Authorization.AuthorizeTemplateAsync(
                definition, template, ReportAccessLevel.Edit, colleagueContext);
            Assert.False(edit.Allowed);
            Assert.Equal(ReportAuthorizationService.CodeInsufficientLevel, edit.ReasonCode);
        }

        // ============================================================================================
        // STALE VERSION — the concurrency check the write endpoint performs
        // ============================================================================================

        // TWO PEOPLE, ONE COMPANY LAYOUT. Both open v1; both save. Without a version check the second save
        // silently discards the first person's work and nobody is told — a last-writer-wins overwrite that
        // looks exactly like the first edit never happened.
        //
        // The endpoint's ExpectedVersionNo check is what turns that into a visible 409. This test proves the
        // FACT the check depends on: an append bumps the version, so a caller holding the old number can
        // detect it. (The endpoint's own comparison is three lines over this fact.)
        [Fact]
        public async Task A_second_save_appends_a_new_version_so_a_stale_writer_is_detectable()
        {
            using var host = new ReportingTestHost();

            var created = await host.Templates.SaveAsync(
                Input("Shared layout", ReportTemplateScope.Company), host.Ctx);
            Assert.True(created.Success);
            Assert.Equal(1, created.VersionNo);

            // Person A saves a real change.
            var second = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                Id = created.TemplateId,
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "Shared layout",
                Scope = ReportTemplateScope.Company,
                Layout = Layout("Branch"),
            }, host.Ctx);

            Assert.True(second.Success);
            Assert.Equal(2, second.VersionNo);
            Assert.False(second.Unchanged);

            // Person B is still holding v1. The version they can observe now disagrees, which is precisely
            // what SaveLayout compares against before writing.
            var versions = await host.Templates.ListVersionsAsync(created.TemplateId, host.Ctx);
            Assert.Equal(2, versions.Max(v => v.VersionNo));
            Assert.NotEqual(1, versions.Max(v => v.VersionNo));

            // AND THE OLD VERSION IS STILL THERE. A stale-write conflict is only recoverable if the version
            // the loser was editing has not been rewritten — history is appended, never edited.
            Assert.Contains(versions, v => v.VersionNo == 1);
        }

        // ============================================================================================
        // REORDER ISOLATION
        // ============================================================================================

        // Favourites are PER EMPLOYEE. Reorder takes a list of row ids from the client, which is the shape
        // that invites the attack: send somebody else's ids and reorder — or merely confirm the existence of
        // — their bar. The service resolves ids within the caller's own set, so a foreign id matches nothing.
        [Fact]
        public async Task Reordering_favourites_cannot_touch_another_employees_rows()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(Colleague, companyId: host.Ctx.CompanyId);

            var colleagueContext = new BusinessContext
            {
                CompanyId = host.Ctx.CompanyId,
                EmployeeId = Colleague,
                UserId = "colleague",
                Roles = host.Ctx.Roles,
                Source = BusinessContextSource.Test,
            };

            var mine = await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            var theirs = await host.Library.AddFavoriteAsync(
                TestReportDefinitions.SalesCode, null, colleagueContext);

            Assert.NotEqual(mine, theirs);

            var beforeSort = await host.Db.ReportFavorites.AsNoTracking()
                .Where(f => f.Id == theirs).Select(f => f.SortOrder).SingleAsync();

            // I submit THEIR id first, then mine — the shape of a crafted reorder.
            await host.Library.ReorderFavoritesAsync(new[] { theirs, mine }, host.Ctx);

            var afterSort = await host.Db.ReportFavorites.AsNoTracking()
                .Where(f => f.Id == theirs).Select(f => f.SortOrder).SingleAsync();

            // Their row is untouched.
            Assert.Equal(beforeSort, afterSort);

            // And their list still resolves for them, unchanged.
            var theirFavorites = await host.Library.GetFavoritesAsync(colleagueContext);
            Assert.Single(theirFavorites);
            Assert.Equal(theirs, theirFavorites[0].Id);
        }

        // A favourite is per-user state, but the thing it POINTS AT is a report. Pinning one the caller may
        // not see would confirm the code exists and leave a row referencing it — which is why the endpoint
        // gates on the report before writing, and why the library hides a favourite whose permission is gone.
        [Fact]
        public async Task A_favourite_of_a_report_the_caller_cannot_see_is_never_returned()
        {
            using var host = new ReportingTestHost();

            await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            Assert.Single(await host.Library.GetFavoritesAsync(host.Ctx));

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            Assert.Empty(await host.Library.GetFavoritesAsync(host.Ctx));
        }

        // ============================================================================================
        // AN UNRESOLVED COMPANY WRITES NOTHING
        // ============================================================================================

        // The platform rule, applied to the write path: fail closed, never default to a company. Every one of
        // the seven endpoints begins with the same ResolveAsync guard; this proves the service beneath it
        // refuses too, so the guard is defence in depth rather than the only thing standing there.
        [Fact]
        public async Task A_write_with_no_resolved_company_is_refused_by_the_service_itself()
        {
            using var host = new ReportingTestHost();

            var unresolved = new BusinessContext
            {
                CompanyId = 0,
                EmployeeId = 7,
                UserId = "nobody",
                Roles = new[] { "Reports" },
                Source = BusinessContextSource.Test,
            };

            var result = await host.Templates.SaveAsync(
                Input("Should not exist", ReportTemplateScope.Personal), unresolved);

            Assert.False(result.Success);
            Assert.Empty(await host.Db.ReportTemplates.AsNoTracking().ToListAsync());
        }

        // ============================================================================================
        // THE GATE ITSELF
        // ============================================================================================

        // A TEST OVER A CONSTANT, which needs justifying.
        //
        // ReportingWriteSurface.IsActivated drives whether the Viewer renders Save / Fork / Favourite as live
        // controls or as disabled ones with a reason. The flag and the endpoint file must agree: a flag set
        // true without the endpoints gives buttons that 404, and endpoints without the flag gives a working
        // API nobody can reach from the product.
        //
        // The endpoints are now ACTIVE — TAB 1 declared IReportAuthorizationService an authority, the parked
        // file was restored, and the three endpoints the analyzer still flagged were genuinely gated rather
        // than exempted. This test survived that transition unchanged in intent: it asserts the two halves
        // are CONSISTENT, which was true before activation and is true after.
        [Fact]
        public void The_write_surface_flag_and_the_endpoint_file_agree()
        {
            var root = RepoRoot();
            var live = Path.Combine(root, "CrossBuy", "Controllers", "Api", "ReportsCenterWriteEndpoints.cs");
            var parked = live + ".pending";

            bool endpointsCompiled = File.Exists(live);
            bool endpointsParked = File.Exists(parked);

            Assert.True(endpointsCompiled || endpointsParked,
                "The write endpoints must exist either as .cs (activated) or as .cs.pending (held back). " +
                "Neither file is present, so the seven writes have been lost rather than deferred.");

            Assert.False(endpointsCompiled && endpointsParked,
                "Both the compiled and the parked copy exist. One of them is dead code that will drift.");

            Assert.Equal(endpointsCompiled, ReportingWriteSurface.IsActivated);
        }

        // NO PENDING PRODUCTION CODE ANYWHERE. A `.pending` file is invisible to the build, to a *.cs search
        // and to every analyzer — which is exactly why one is useful for parking work, and exactly why one
        // left behind rots unnoticed. The parking mechanism was legitimate; leaving it in place after the
        // block cleared would not be.
        [Fact]
        public void No_pending_production_code_remains_in_the_repository()
        {
            var root = RepoRoot();

            var pending = Directory
                .EnumerateFiles(root, "*.pending", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();

            Assert.True(pending.Count == 0,
                "Pending production code must be activated or removed, never left: " + string.Join(", ", pending));
        }

        // Three probes, and it FAILS LOUDLY rather than skipping. A path test that silently no-ops when it
        // cannot find the repo is a test that reports success for having done nothing — the same reasoning the
        // Communication schema-parity test records.
        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
                return fromEnvironment;

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root (no CrossBuy.sln above " + AppContext.BaseDirectory +
                "). Set CROSSBUY_REPO_ROOT.");
        }
    }
}
