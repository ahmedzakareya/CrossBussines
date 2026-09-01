using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — TEMPLATES: the four scopes (components 29–32) as ONE precedence walk, plus
    // versioning immutability, sharing and ownership.
    public class ReportingTemplateScopeTests
    {
        private const int Team = 40;

        private static ReportLayout Layout(params string[] visibleColumns) => new()
        {
            VisibleColumns = visibleColumns.Length > 0 ? visibleColumns : new[] { "Branch", "Amount" },
        };

        // Platform templates are created by DEPLOYMENT, not by a tenant, so a test seeds them directly — which is
        // also the assertion that SaveAsync refuses to (see below).
        private static async Task<int> SeedPlatformTemplateAsync(ReportingTestHost host, string name)
        {
            var template = new ReportTemplate
            {
                CompanyID = 0,
                ReportCode = TestReportDefinitions.SalesCode,
                Name = name,
                Scope = ReportTemplateScope.Platform,
                IsDefault = true,
                CreatedAt = host.Clock.LocalNow,
            };
            host.Db.ReportTemplates.Add(template);
            await host.Db.SaveChangesAsync();

            host.Db.ReportTemplateVersions.Add(new ReportTemplateVersion
            {
                CompanyID = 0,
                TemplateId = template.Id,
                VersionNo = 1,
                LayoutJson = ReportLayoutJson.Serialize(Layout("Branch", "Item")),
                ContentHash = "platform",
                IsPublished = true,
                PublishedAt = host.Clock.LocalNow,
            });
            template.CurrentVersionNo = 1;
            await host.Db.SaveChangesAsync();
            return template.Id;
        }

        // ================================================================================================
        // 1. THE PRECEDENCE WALK — Personal > Team > Company > Platform > definition defaults
        // ================================================================================================

        [Fact]
        public async Task With_no_template_at_all_the_definition_defaults_are_used()
        {
            using var host = new ReportingTestHost();

            var resolution = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), null, null, host.Ctx);

            Assert.Equal(ReportTemplateSource.DefinitionDefault, resolution.Source);
            Assert.Null(resolution.Template);
        }

        [Fact]
        public async Task Resolution_walks_Personal_then_Team_then_Company_then_Platform()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);
            host.SeedEmployee(7, companyId: 1, departmentId: Team);

            await SeedPlatformTemplateAsync(host, "platform layout");

            async Task<ReportTemplateSource> ResolveSourceAsync()
            {
                // A FRESH resolution each time, from the database — never asserted from the entity that wrote it.
                var resolution = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), null, null, host.Ctx);
                return resolution.Source;
            }

            // Platform is the only one present.
            Assert.Equal(ReportTemplateSource.Platform, await ResolveSourceAsync());

            var company = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "company layout",
                Scope = ReportTemplateScope.Company, Layout = Layout(), IsDefault = true,
            }, host.Ctx);
            Assert.True(company.Success);
            Assert.Equal(ReportTemplateSource.Company, await ResolveSourceAsync());

            var team = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "team layout",
                Scope = ReportTemplateScope.Team, TeamId = Team, Layout = Layout(), IsDefault = true,
            }, host.Ctx);
            Assert.True(team.Success);
            Assert.Equal(ReportTemplateSource.Team, await ResolveSourceAsync());

            var personal = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "my layout",
                Scope = ReportTemplateScope.Personal, Layout = Layout(), IsDefault = true,
            }, host.Ctx);
            Assert.True(personal.Success);

            // Personal wins over everything — the enum's numeric values ARE the precedence.
            Assert.Equal(ReportTemplateSource.Personal, await ResolveSourceAsync());
        }

        [Fact]
        public async Task Another_employees_personal_template_is_never_resolved_for_me()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);

            var theirs = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            var saved = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "employee 8 private",
                Scope = ReportTemplateScope.Personal, Layout = Layout(), IsDefault = true,
            }, theirs);
            Assert.True(saved.Success);

            // Nobody else's private layout — not even a colleague with full module rights.
            var mine = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), null, null, host.Ctx);
            Assert.Equal(ReportTemplateSource.DefinitionDefault, mine.Source);

            // ...and naming it explicitly is refused too, degrading to the default rather than failing the report.
            var explicitAttempt = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(),
                saved.TemplateId, null, host.Ctx);
            Assert.Equal(ReportTemplateSource.DefinitionDefault, explicitAttempt.Source);
            Assert.Contains(explicitAttempt.Diagnostics, d => d.Code == ReportTemplateService.CodeTemplateDenied);
        }

        [Fact]
        public async Task A_team_template_resolves_only_for_members_of_that_team()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);
            host.SeedEmployee(7, companyId: 1, departmentId: Team);
            host.SeedEmployee(8, companyId: 1, departmentId: 99);

            await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "team layout",
                Scope = ReportTemplateScope.Team, TeamId = Team, Layout = Layout(), IsDefault = true,
            }, host.Ctx);

            Assert.Equal(ReportTemplateSource.Team,
                (await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), null, null, host.Ctx)).Source);

            var otherTeam = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            // Not a member → the template simply does not resolve. Never an error, and never a fallback to
            // "all teams".
            Assert.Equal(ReportTemplateSource.DefinitionDefault,
                (await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), null, null, otherTeam)).Source);
        }

        [Fact]
        public async Task A_template_from_another_company_is_refused()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);

            var theirCompany = new BusinessContext
            {
                CompanyId = 2, EmployeeId = 7, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            var saved = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "company 2 layout",
                Scope = ReportTemplateScope.Company, Layout = Layout(),
            }, theirCompany);

            var template = await host.Db.ReportTemplates.AsNoTracking()
                .SingleAsync(t => t.Id == saved.TemplateId);

            var decision = await host.Authorization.AuthorizeTemplateAsync(TestReportDefinitions.Sales(),
                template, ReportAccessLevel.Run, host.Ctx);

            Assert.False(decision.Allowed);
            Assert.Equal(ReportAuthorizationService.CodeTemplateOtherCompany, decision.ReasonCode);
        }

        [Fact]
        public async Task A_missing_or_deleted_template_degrades_to_the_default_with_a_warning()
        {
            using var host = new ReportingTestHost();

            var resolution = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), 12345, null, host.Ctx);

            // A stale bookmark must not break a report.
            Assert.Equal(ReportTemplateSource.DefinitionDefault, resolution.Source);
            Assert.Contains(resolution.Diagnostics, d => d.Code == ReportTemplateService.CodeTemplateNotFound);
        }

        // ================================================================================================
        // 2. PLATFORM TEMPLATES ARE READ-ONLY; "EDITING" ONE IS A FORK
        // ================================================================================================

        [Fact]
        public async Task A_tenant_cannot_create_a_platform_template()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            var result = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "sneaky",
                Scope = ReportTemplateScope.Platform, Layout = Layout(),
            }, host.Ctx);

            // Not even an administrator: allowing it would let one company publish a layout every other company
            // resolves.
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportTemplateService.CodeTemplateScopeInvalid);
        }

        [Fact]
        public async Task A_platform_template_is_runnable_by_all_and_editable_by_nobody()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");
            var platformId = await SeedPlatformTemplateAsync(host, "shipped layout");

            var template = await host.Db.ReportTemplates.AsNoTracking().SingleAsync(t => t.Id == platformId);

            Assert.True((await host.Authorization.AuthorizeTemplateAsync(TestReportDefinitions.Sales(),
                template, ReportAccessLevel.Run, host.Ctx)).Allowed);

            // Even for an administrator acting in a tenant: platform content changes by deployment.
            var edit = await host.Authorization.AuthorizeTemplateAsync(TestReportDefinitions.Sales(),
                template, ReportAccessLevel.Edit, host.Ctx);
            Assert.False(edit.Allowed);
            Assert.Equal(ReportAuthorizationService.CodeScopeNotAllowed, edit.ReasonCode);
        }

        [Fact]
        public async Task Forking_a_platform_template_copies_its_layout_into_an_editable_scope()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);
            var platformId = await SeedPlatformTemplateAsync(host, "shipped layout");

            var fork = await host.Templates.ForkAsync(platformId, ReportTemplateScope.Personal, "my copy", host.Ctx);

            Assert.True(fork.Success);
            Assert.Equal(1, fork.VersionNo);   // a fork is an ordinary create, so it starts at v1

            var forked = await host.Db.ReportTemplates.AsNoTracking().SingleAsync(t => t.Id == fork.TemplateId);
            Assert.Equal(ReportTemplateScope.Personal, forked.Scope);
            Assert.Equal(1, forked.CompanyID);
            Assert.Equal(7, forked.OwnerEmpId);

            // The layout came across intact.
            var version = await host.Db.ReportTemplateVersions.AsNoTracking()
                .SingleAsync(v => v.TemplateId == fork.TemplateId && v.VersionNo == 1);
            var layout = ReportLayoutJson.Deserialize(version.LayoutJson);
            Assert.Equal(new[] { "Branch", "Item" }, layout!.VisibleColumns);
            Assert.Contains("Forked from", version.ChangeNote);
        }

        // ================================================================================================
        // 3. VERSIONING IS IMMUTABILITY
        // ================================================================================================

        [Fact]
        public async Task Editing_appends_a_version_and_never_rewrites_one()
        {
            using var host = new ReportingTestHost();

            var first = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "v1",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch", "Amount"),
            }, host.Ctx);
            Assert.Equal(1, first.VersionNo);

            var second = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                Id = first.TemplateId,
                ReportCode = TestReportDefinitions.SalesCode, Name = "v2",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch", "Item", "Amount"),
                ChangeNote = "added Item",
            }, host.Ctx);
            Assert.Equal(2, second.VersionNo);
            Assert.False(second.Unchanged);

            // v1 is still there, still readable, and still the ORIGINAL layout — which is what makes an archived
            // artifact from v1 reproducible.
            var v1 = await host.Db.ReportTemplateVersions.AsNoTracking()
                .SingleAsync(v => v.TemplateId == first.TemplateId && v.VersionNo == 1);
            Assert.Equal(new[] { "Branch", "Amount" }, ReportLayoutJson.Deserialize(v1.LayoutJson)!.VisibleColumns);

            var versions = await host.Templates.ListVersionsAsync(first.TemplateId, host.Ctx);
            Assert.Equal(2, versions.Count);
            Assert.True(versions.Single(v => v.VersionNo == 2).IsCurrent);
        }

        [Fact]
        public async Task Saving_an_identical_layout_creates_no_new_version()
        {
            using var host = new ReportingTestHost();

            var input = new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "same",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch", "Amount"),
            };

            var first = await host.Templates.SaveAsync(input, host.Ctx);
            var again = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                Id = first.TemplateId, ReportCode = input.ReportCode, Name = input.Name,
                Scope = input.Scope, Layout = input.Layout,
            }, host.Ctx);

            // Idempotent save: a UI that autosaves would otherwise grow a version per keystroke and make the
            // history useless.
            Assert.True(again.Unchanged);
            Assert.Equal(1, again.VersionNo);
            Assert.Single(await host.Db.ReportTemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == first.TemplateId).ToListAsync());
        }

        [Fact]
        public async Task Rollback_moves_the_pointer_and_appends_nothing()
        {
            using var host = new ReportingTestHost();

            var first = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "t",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch"),
            }, host.Ctx);

            await host.Templates.SaveAsync(new ReportTemplateInput
            {
                Id = first.TemplateId, ReportCode = TestReportDefinitions.SalesCode, Name = "t",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch", "Item", "Amount"),
            }, host.Ctx);

            var rollback = await host.Templates.RollbackAsync(first.TemplateId, 1, host.Ctx);

            Assert.True(rollback.Success);

            // Still exactly two versions — history is not rewritten, and no "v3 that is a copy of v1" appears.
            Assert.Equal(2, (await host.Templates.ListVersionsAsync(first.TemplateId, host.Ctx)).Count);

            var resolution = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(),
                first.TemplateId, null, host.Ctx);
            Assert.Equal(1, resolution.VersionNo);
            Assert.Equal(new[] { "Branch" }, resolution.Layout.VisibleColumns);
        }

        [Fact]
        public async Task An_older_version_can_be_replayed_exactly()
        {
            using var host = new ReportingTestHost();

            var first = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "t",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Branch"),
            }, host.Ctx);

            await host.Templates.SaveAsync(new ReportTemplateInput
            {
                Id = first.TemplateId, ReportCode = TestReportDefinitions.SalesCode, Name = "t",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Item", "Amount"),
            }, host.Ctx);

            // Asking for v1 explicitly — the mechanism that makes an archived March PDF explainable in December.
            var replay = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(), first.TemplateId, 1,
                host.Ctx);
            Assert.Equal(new[] { "Branch" }, replay.Layout.VisibleColumns);
        }

        [Fact]
        public async Task A_corrupt_stored_layout_degrades_to_the_definition_default_rather_than_failing()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "t",
                Scope = ReportTemplateScope.Personal, Layout = Layout(),
            }, host.Ctx);

            var version = await host.Db.ReportTemplateVersions
                .SingleAsync(v => v.TemplateId == saved.TemplateId && v.VersionNo == 1);
            version.LayoutJson = "{ this is not json";
            await host.Db.SaveChangesAsync();

            var resolution = await host.Templates.ResolveAsync(TestReportDefinitions.Sales(),
                saved.TemplateId, null, host.Ctx);

            // One bad row must not take a report offline, and the warning names the row so it can be fixed.
            Assert.Equal(ReportTemplateSource.DefinitionDefault, resolution.Source);
            Assert.Contains(resolution.Diagnostics, d => d.Code == ReportTemplateService.CodeLayoutCorrupt);
        }

        // ================================================================================================
        // 4. A TEMPLATE MAY ONLY CHOOSE AMONG WHAT THE DEFINITION DECLARES
        // ================================================================================================

        [Fact]
        public async Task A_layout_with_an_unusable_filter_is_refused_at_save_time()
        {
            using var host = new ReportingTestHost();

            var result = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "bad filter",
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout { Filters = new[] { ReportFilter.Eq("NoSuchColumn", "x") } },
            }, host.Ctx);

            // Storing it would move the failure to render time, where it looks like a report bug rather than a
            // save mistake.
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportTemplateService.CodeTemplateInvalidLayout);
        }

        [Fact]
        public async Task A_layout_may_not_bake_a_system_supplied_parameter()
        {
            using var host = new ReportingTestHost();

            var result = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "sneaky company",
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout
                {
                    Parameters = new Dictionary<string, string?> { [ReportSystemParameters.CompanyId] = "99" },
                },
            }, host.Ctx);

            // A template that could bake CompanyId would be a STORED cross-tenant read, waiting for someone to
            // edit the row.
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics,
                d => d.Severity == ReportDiagnosticSeverity.Error && d.Field == ReportSystemParameters.CompanyId);
        }

        [Fact]
        public async Task An_unusable_sort_in_a_layout_is_only_a_warning_and_the_template_still_saves()
        {
            using var host = new ReportingTestHost();

            var result = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "stale sort",
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout { Sorts = new[] { ReportSort.By("Removed") } },
            }, host.Ctx);

            Assert.True(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Severity == ReportDiagnosticSeverity.Warning);
        }

        [Fact]
        public async Task At_most_one_default_exists_per_scope_group()
        {
            using var host = new ReportingTestHost();

            var a = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "a",
                Scope = ReportTemplateScope.Personal, Layout = Layout(), IsDefault = true,
            }, host.Ctx);

            var b = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "b",
                Scope = ReportTemplateScope.Personal, Layout = Layout("Item"), IsDefault = true,
            }, host.Ctx);

            var templates = await host.Db.ReportTemplates.AsNoTracking()
                .Where(t => t.ReportCode == TestReportDefinitions.SalesCode
                            && t.Scope == ReportTemplateScope.Personal)
                .ToListAsync();

            Assert.Single(templates.Where(t => t.IsDefault));
            Assert.Equal(b.TemplateId, templates.Single(t => t.IsDefault).Id);
            Assert.NotEqual(a.TemplateId, b.TemplateId);
        }

        // ================================================================================================
        // 5. SHARING RAISES, NEVER OPENS
        // ================================================================================================

        [Fact]
        public async Task A_share_cannot_grant_access_the_module_permission_denies()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            await host.Library.ShareAsync(new ReportShareInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                PrincipalType = ReportPrincipalType.Company,
                AccessLevel = ReportAccessLevel.Manage,
            }, host.Ctx);

            // A company-wide Manage grant exists. The gate still shuts on someone without the module permission —
            // the share is not even consulted.
            var outsider = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Warehouse" }, Source = BusinessContextSource.Test,
            };

            var decision = await host.Authorization.AuthorizeReportAsync(TestReportDefinitions.Sales(),
                ReportAccessLevel.View, outsider);

            Assert.False(decision.Allowed);
            Assert.Equal(ReportAuthorizationService.CodeNoModulePermission, decision.ReasonCode);
        }

        [Fact]
        public async Task A_share_raises_the_level_of_someone_who_already_passed_the_gate()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            var target = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            // Base level from the module permission is Run.
            var before = await host.Authorization.AuthorizeReportAsync(TestReportDefinitions.Sales(),
                ReportAccessLevel.Edit, target);
            Assert.False(before.Allowed);

            await host.Library.ShareAsync(new ReportShareInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                PrincipalType = ReportPrincipalType.Employee,
                PrincipalKey = "8",
                AccessLevel = ReportAccessLevel.Edit,
            }, host.Ctx);

            var after = await host.Authorization.AuthorizeReportAsync(TestReportDefinitions.Sales(),
                ReportAccessLevel.Edit, target);
            Assert.True(after.Allowed);
            Assert.Equal(ReportAccessSource.Share, after.Source);
        }

        [Fact]
        public async Task An_expired_share_grants_nothing_and_expiry_is_evaluated_at_read_time()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            host.Db.ReportShares.Add(new ReportShare
            {
                CompanyID = 1,
                ReportCode = TestReportDefinitions.SalesCode,
                PrincipalType = ReportPrincipalType.Employee,
                PrincipalKey = "8",
                AccessLevel = ReportAccessLevel.Manage,
                ExpiresAt = ReportingTestHost.FixedNow.AddDays(-1),
                CreatedAt = ReportingTestHost.FixedNow,
            });
            await host.Db.SaveChangesAsync();

            var target = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            // An expiry that depended on a sweeper would keep working when the sweeper stopped.
            var decision = await host.Authorization.AuthorizeReportAsync(TestReportDefinitions.Sales(),
                ReportAccessLevel.Edit, target);
            Assert.False(decision.Allowed);
        }

        [Fact]
        public async Task Nobody_can_grant_more_than_they_hold()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Reports");

            // "Reports" gives Run, and sharing requires Manage — so this must fail rather than mint privilege.
            var ex = await Assert.ThrowsAsync<ReportingException>(() => host.Library.ShareAsync(
                new ReportShareInput
                {
                    ReportCode = TestReportDefinitions.SalesCode,
                    PrincipalType = ReportPrincipalType.Company,
                    AccessLevel = ReportAccessLevel.Manage,
                }, host.Ctx));

            Assert.Contains("Manage", ex.Message);
        }

        [Fact]
        public async Task Re_granting_updates_the_one_row_rather_than_stacking_grants()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            var input = new ReportShareInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                PrincipalType = ReportPrincipalType.Employee,
                PrincipalKey = "8",
                AccessLevel = ReportAccessLevel.Run,
            };

            var first = await host.Library.ShareAsync(input, host.Ctx);
            var second = await host.Library.ShareAsync(new ReportShareInput
            {
                ReportCode = input.ReportCode, PrincipalType = input.PrincipalType,
                PrincipalKey = input.PrincipalKey, AccessLevel = ReportAccessLevel.Edit,
            }, host.Ctx);

            // Two live grants to one principal would make "what does this person have?" a question with two
            // answers.
            Assert.Equal(first, second);
            var share = await host.Db.ReportShares.AsNoTracking().SingleAsync();
            Assert.Equal(ReportAccessLevel.Edit, share.AccessLevel);
        }

        // ================================================================================================
        // 6. OWNERSHIP
        // ================================================================================================

        [Fact]
        public async Task Ownership_transfers_only_to_an_active_employee_of_the_same_company()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);
            host.SeedEmployee(8, companyId: 1);
            host.SeedEmployee(9, companyId: 2);            // another tenant
            host.SeedEmployee(10, companyId: 1, active: false);

            var saved = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "mine",
                Scope = ReportTemplateScope.Personal, Layout = Layout(),
            }, host.Ctx);

            // Another tenant's employee, and an inactive one, would both park the template somewhere unreachable.
            Assert.False(await host.Library.TransferOwnershipAsync(saved.TemplateId, 9, host.Ctx));
            Assert.False(await host.Library.TransferOwnershipAsync(saved.TemplateId, 10, host.Ctx));

            Assert.True(await host.Library.TransferOwnershipAsync(saved.TemplateId, 8, host.Ctx));

            var template = await host.Db.ReportTemplates.AsNoTracking().SingleAsync(t => t.Id == saved.TemplateId);
            Assert.Equal(8, template.OwnerEmpId);
        }

        // ================================================================================================
        // 7. FAVOURITES, CATEGORIES, TAGS
        // ================================================================================================

        [Fact]
        public async Task Favouriting_is_idempotent_and_hides_reports_whose_permission_was_revoked()
        {
            using var host = new ReportingTestHost();

            var first = await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            var again = await host.Library.AddFavoriteAsync(TestReportDefinitions.SalesCode, null, host.Ctx);
            Assert.Equal(first, again);

            Assert.Single(await host.Library.GetFavoritesAsync(host.Ctx));

            // A pinned link the user cannot open is worse than no link.
            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);
            Assert.Empty(await host.Library.GetFavoritesAsync(host.Ctx));
        }

        [Fact]
        public async Task Platform_categories_are_materialised_from_the_catalog_and_the_sync_is_idempotent()
        {
            using var host = new ReportingTestHost();

            var firstRun = await host.Library.SyncPlatformCategoriesAsync();
            var secondRun = await host.Library.SyncPlatformCategoriesAsync();

            Assert.True(firstRun > 0);
            Assert.Equal(0, secondRun);   // additive only, never renames or duplicates

            var tree = await host.Library.GetCategoryTreeAsync(host.Ctx);
            Assert.Contains(tree, c => c.Key == PlatformReportCodes.CategoryKey && c.IsSystem);
        }

        [Fact]
        public async Task Tags_are_deduplicated_case_insensitively()
        {
            using var host = new ReportingTestHost();

            var a = await host.Library.EnsureTagAsync("Month-End", null, "primary", host.Ctx);
            var b = await host.Library.EnsureTagAsync("month-end", null, "danger", host.Ctx);

            // Without this a tag list becomes a list of near-duplicates within a week.
            Assert.Equal(a, b);
            Assert.Single(await host.Library.GetTagsAsync(host.Ctx));

            Assert.True(await host.Library.TagAsync(TestReportDefinitions.SalesCode, null, a, host.Ctx));
            Assert.Contains(TestReportDefinitions.SalesCode,
                await host.Library.GetReportCodesByTagAsync(a, host.Ctx));
        }

        [Fact]
        public async Task Deleting_a_template_is_a_soft_delete_so_archive_rows_are_not_orphaned()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Templates.SaveAsync(new ReportTemplateInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "doomed",
                Scope = ReportTemplateScope.Personal, Layout = Layout(),
            }, host.Ctx);

            Assert.True(await host.Templates.DeleteAsync(saved.TemplateId, host.Ctx));

            // The row is still there — an archived artifact references (TemplateId, VersionNo), and hard-deleting
            // would make an archived document unexplainable.
            var template = await host.Db.ReportTemplates.AsNoTracking().SingleAsync(t => t.Id == saved.TemplateId);
            Assert.NotNull(template.DeletedAt);

            // ...but it no longer resolves.
            Assert.Empty(await host.Templates.ListAsync(TestReportDefinitions.SalesCode, host.Ctx));
        }
    }
}
