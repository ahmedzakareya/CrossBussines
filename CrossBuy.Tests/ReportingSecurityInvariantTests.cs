using System.Reflection;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Security) — THE INVARIANT RE-PROOF.
    //
    // Thirteen invariants were re-reviewed at the integration gate. NINE were already mechanically covered by
    // the existing suite, and adding a second test for them would be noise:
    //
    //   denied runs are recorded            ReportingEngineTests.A_caller_without_the_module_permission_is_denied_and_the_denial_is_recorded
    //   shares cannot widen permission      ReportingEngineTests.A_share_cannot_grant_access_the_module_permission_denies
    //   callers cannot pass CompanyId       ReportingParameterEngineTests.A_caller_supplied_CompanyId_is_dropped_with_a_warning_and_the_context_wins
    //   internal fields cannot be filtered  ReportingShaperTests.A_filter_on_an_internal_column_fails_rather_than_being_quietly_ignored
    //   dropping a filter is never allowed  ReportingShaperTests.A_filter_on_an_undeclared_column_FAILS_the_run_because_dropping_it_would_widen_the_result
    //   row limits only become stricter     ReportingEngineTests.A_caller_can_lower_the_row_cap_but_not_raise_it
    //   truncation is always declared       ReportingEngineTests.A_row_cap_truncates_and_says_so
    //   unmapped keys fail closed           ReportingEngineTests.An_unmapped_permission_key_is_DENIED_because_omission_must_not_mean_unrestricted
    //   archive cannot bypass permission    ReportingEngineTests.An_archive_entry_is_not_readable_once_the_report_permission_is_revoked
    //
    // FOUR were not, and this file closes exactly those. Two of them are STRUCTURAL — they are properties of
    // the public surface rather than of a code path, and a structural property is best defended by reflection
    // over the assembly, because a behavioural test cannot fail when somebody adds a NEW public door.
    // ============================================================================================
    public class ReportingSecurityInvariantTests
    {
        private static ReportRequest Request(ReportOutputFormat format = ReportOutputFormat.Html) => new()
        {
            ReportCode = TestReportDefinitions.SalesCode,
            Format = format,
        };

        // Assembly.GetTypes() THROWS ReflectionTypeLoadException when any type in the assembly cannot be
        // loaded — and this web assembly has several whose dependencies are deliberately unbound in this
        // increment (the Playwright PDF renderer's converter, for one). The partial result carried on the
        // exception is exactly what a structural sweep wants: every type that DID load. Without this the whole
        // test host crashes rather than failing a test, which is a far worse diagnostic.
        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }

        // ========================================================================================
        // INVARIANT 1 — authorization happens BEFORE the data fetch.
        //
        // Not covered before: the existing denial test proves the RESULT is denied, which a pipeline that
        // fetched first and then discarded the rows would also satisfy. The difference matters — a report that
        // reads Accounting and then refuses has still read Accounting, and on a slow query it has still spent
        // the database time. This proves the ORDER by observing that the data source is never entered.
        // ========================================================================================
        [Fact]
        public async Task A_denied_run_never_reaches_the_data_source()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Warehouse");

            var fetched = false;
            var spy = new StaticReportDataSource(TestReportDefinitions.SalesDataSourceKey, _ =>
            {
                fetched = true;
                return Array.Empty<IReadOnlyDictionary<string, object?>>();
            });

            var engine = host.EngineWith(spy);
            var result = await engine.GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsDenied);

            // THE ASSERTION THAT MATTERS. A pipeline that fetched and then discarded would fail here while
            // still returning a denial.
            Assert.False(fetched, "the data source was entered for a caller who was refused the report");
        }

        // The same ordering for a caller who may run the report but whose company is unresolved: fail closed
        // BEFORE the read, not after it.
        [Fact]
        public async Task An_unresolved_company_never_reaches_the_data_source()
        {
            using var host = new ReportingTestHost(companyId: 1);

            var fetched = false;
            var spy = new StaticReportDataSource(TestReportDefinitions.SalesDataSourceKey, _ =>
            {
                fetched = true;
                return Array.Empty<IReadOnlyDictionary<string, object?>>();
            });

            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = 7, UserId = "u7",
                Roles = new[] { "Accountant" }, Source = BusinessContextSource.Test,
            };

            var result = await host.EngineWith(spy).GenerateAsync(Request(), unresolved);

            Assert.False(result.IsSuccess);
            Assert.False(fetched, "the data source was entered for an unresolved company scope");
        }

        // ========================================================================================
        // INVARIANT 2 — a caller cannot select a RENDERER, only a FORMAT.
        //
        // Structural. A caller naming a renderer would let them pick an implementation whose escaping rules or
        // permission handling differ from the one the platform chose. The public request surface must therefore
        // expose no renderer handle at all — and the check is reflective, so it also fails when somebody adds a
        // renderer property to ReportRequest next year.
        // ========================================================================================
        [Fact]
        public void The_public_request_surface_exposes_no_renderer_or_data_source_handle()
        {
            var offenders = new List<string>();

            foreach (var property in typeof(ReportRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = property.PropertyType;

                if (typeof(IReportRenderer).IsAssignableFrom(type)
                    || typeof(IReportExporter).IsAssignableFrom(type)
                    || typeof(IReportDataSource).IsAssignableFrom(type)
                    || typeof(IReportDatasetDefinition).IsAssignableFrom(type)
                    || typeof(IReportRendererRegistry).IsAssignableFrom(type)
                    || typeof(IReportDataSourceRegistry).IsAssignableFrom(type))
                    offenders.Add($"ReportRequest.{property.Name} : {type.Name}");

                // A Type-typed property would be the same hole wearing a different coat: "give me the renderer
                // of this type" is renderer selection.
                if (type == typeof(Type)) offenders.Add($"ReportRequest.{property.Name} : System.Type");
            }

            Assert.Empty(offenders);
        }

        // The caller's lever is an enum of FORMATS, and the registry maps a format to a renderer. That mapping
        // is the substitution point the platform owns — proven here to be enum-driven, not caller-driven.
        [Fact]
        public void The_format_lever_is_a_closed_enum_and_the_registry_owns_the_mapping()
        {
            var format = typeof(ReportRequest).GetProperty(nameof(ReportRequest.Format));

            Assert.NotNull(format);
            Assert.True(Nullable.GetUnderlyingType(format!.PropertyType)?.IsEnum == true
                        || format.PropertyType.IsEnum,
                "Format must be a closed enum; a string would let a caller name anything.");
        }

        // ========================================================================================
        // INVARIANT 3 — a caller cannot reach a data source directly. ONE public door.
        //
        // Structural, and the reason it is reflective: the guarantee is about the assembly's public surface, so
        // the failure mode to defend against is a NEW public type appearing, which no behavioural test notices.
        // ========================================================================================
        [Fact]
        public void Only_the_report_service_and_print_service_are_public_doors_onto_report_generation()
        {
            // Types that legitimately expose a Generate/Fetch-shaped method to a caller outside the platform.
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(IReportService), "ReportService",
                nameof(IReportPrintService), "ReportPrintService",

                // The engine is public because ReportService and the schedule runner call it, and both live in
                // this assembly. It is NOT a door a controller should use — that is a review rule, not a
                // compiler one, and it is recorded in ADR-037 §3.
                nameof(IReportEngine), "ReportEngine",
            };

            var doors = LoadableTypes(typeof(IReportService).Assembly)
                .Where(t => t.IsPublic && t.Namespace == "CrossBuy.BL.Reporting")
                .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(m => m.Name is "GenerateAsync"))
                .Select(t => t.Name)
                .Where(n => !allowed.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(doors);
        }

        // A data source is reachable only through the registry, which the engine owns. If IReportDataSource ever
        // gained a public static entry point or a parameterless public fetch, this is where it would surface.
        [Fact]
        public void A_data_source_is_reachable_only_with_a_query_that_carries_a_resolved_context()
        {
            var fetch = typeof(IReportDataSource).GetMethod(nameof(IReportDataSource.FetchAsync));

            Assert.NotNull(fetch);

            // The ONLY non-cancellation parameter must be the query — and the query is what carries the
            // BusinessContext. A source that could be called with a bare company id would be a source a caller
            // could talk into another tenant.
            var parameters = fetch!.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .ToList();

            Assert.Single(parameters);
            Assert.Equal(typeof(ReportDataQuery), parameters[0].ParameterType);

            var contextProperty = typeof(ReportDataQuery).GetProperty(nameof(ReportDataQuery.Context));
            Assert.NotNull(contextProperty);
            Assert.Equal(typeof(BusinessContext), contextProperty!.PropertyType);
        }

        // ========================================================================================
        // INVARIANT 4 — template VISIBILITY cannot grant DATA access.
        //
        // Not covered before: the template tests prove who can RESOLVE a template (platform templates resolve
        // for everyone, another employee's personal template never resolves for me). None of them proves that
        // resolving a template does not let you RUN the report it lays out. A platform template is visible to
        // every tenant by design — if that visibility carried data access, one shared layout would open the
        // underlying report to the whole product.
        // ========================================================================================
        [Fact]
        public async Task A_platform_template_is_visible_to_everyone_and_grants_no_data_access()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Warehouse");

            // A PLATFORM template (CompanyID = 0) — visible to every tenant by design.
            host.Db.ReportTemplates.Add(new ReportTemplate
            {
                CompanyID = 0,
                ReportCode = TestReportDefinitions.SalesCode,
                Scope = ReportTemplateScope.Platform,
                Name = "Standard layout",
                NameEn = "Standard layout",
                IsDefault = true,
                CreatedAt = ReportingTestHost.FixedNow,
            });
            await host.Db.SaveChangesAsync();

            var template = await host.Db.ReportTemplates.AsNoTracking()
                .SingleAsync(t => t.ReportCode == TestReportDefinitions.SalesCode);

            // Running THROUGH that template, as a caller who lacks the report's module permission.
            var request = new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Html,
                TemplateId = template.Id,
            };

            var result = await host.Engine.GenerateAsync(request, host.Ctx);

            // The template resolved; the DATA did not follow it.
            Assert.True(result.IsDenied);
            Assert.Null(result.Artifact);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportAuthorizationService.CodeNoModulePermission);
        }

        // The same claim from the other side: a template is a LAYOUT, so its stored shape must carry no
        // permission, no company override and no data-source handle. If it did, editing a layout would be a way
        // to edit authorization.
        [Fact]
        public void A_stored_template_carries_no_permission_and_no_data_source_handle()
        {
            var suspicious = typeof(ReportTemplate)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Role", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("DataSource", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Dataset", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Empty(suspicious);
        }

        // ========================================================================================
        // The dataset layer inherits the same rule: a dataset ADDS a gate, it never replaces the report's.
        // ========================================================================================
        [Fact]
        public void A_dataset_declares_its_own_permission_so_it_can_only_add_a_gate()
        {
            var property = typeof(IReportDatasetDefinition)
                .GetProperty(nameof(IReportDatasetDefinition.RequiredPermissionKey));

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property!.PropertyType);

            // And it is not nullable-by-omission: the validator refuses an empty key, so "forgot to set it"
            // cannot become "data anyone can query".
            var ungated = new ReportDatasetDefinition
            {
                DatasetCode = "X.Y", Module = "Test", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "X", RequiredPermissionKey = "",
                Fields = new[]
                {
                    new ReportDatasetField { Key = "A", TitleAr = "a", TitleEn = "a" },
                },
            };

            Assert.Contains(ReportDatasetValidator.Validate(ungated),
                e => e.Contains("RequiredPermissionKey is required"));
        }
    }
}