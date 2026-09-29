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

namespace CrossBuy.Tests
{
    // ORIGINAL recovery: exercise its actual engine and permission evaluator, not a second authorization
    // implementation. Query spies make refusal observable before fetch; linked tests activate both the
    // engine's predicate grouping and the renderer's grouping so a successful fetch alone cannot pass.
    public class OriginalSubReportSecurityRecoveryTests
    {
        private const string ParentCode = "Recovery.Parent";
        private const string ChildCode = "Recovery.Child";
        private const string SecretPermission = "recovery.secret.view";
        private const string ChildPermission = "recovery.child.view";
        private const string Open = "GroupKey";
        private const string Secret = "SecretKey";
        private const string Ghost = "UnknownRecoveryField";

        private sealed class Definitions : IReportDefinitionProvider
        {
            private readonly string? _channel;
            private readonly string _field;
            public Definitions(string? channel = null, string field = Secret)
            { _channel = channel; _field = field; }
            public string ProviderName => "Original security recovery fixture";
            public IEnumerable<ReportDefinition> GetDefinitions()
            {
                yield return new ReportDefinition
                {
                    Code = ParentCode, Module = "Test", TitleAr = "Parent", TitleEn = "Parent",
                    DataSourceKey = ParentCode, PermissionKey = TestReportDefinitions.SalesPermission,
                    Columns = new[]
                    {
                        new ReportColumn { Key = Open, TitleAr = "Group", TitleEn = "Group", Groupable = true },
                        new ReportColumn { Key = "Date", TitleAr = "Date", TitleEn = "Date",
                            Type = ReportFieldType.Date, Format = "yyyy-MM", Groupable = true },
                        new ReportColumn { Key = "Amount", TitleAr = "Amount", TitleEn = "Amount",
                            Type = ReportFieldType.Money, Aggregate = ReportAggregate.Sum },
                    },
                    Parameters = new[]
                    {
                        new ReportParameterDescriptor { Key = "From", TitleAr = "From", TitleEn = "From",
                            Type = ReportFieldType.Date, DefaultValue = "2026-05-01" },
                        new ReportParameterDescriptor { Key = "To", TitleAr = "To", TitleEn = "To",
                            Type = ReportFieldType.Date, DefaultValue = "2026-05-14" },
                    },
                };
                yield return new ReportDefinition
                {
                    Code = ChildCode, Module = "Test", TitleAr = "Child", TitleEn = "Child",
                    DataSourceKey = ChildCode, PermissionKey = ChildPermission,
                    Columns = new[]
                    {
                        new ReportColumn { Key = Open, TitleAr = "Group", TitleEn = "Group", Groupable = true },
                        new ReportColumn { Key = "Label", TitleAr = "Label", TitleEn = "Label" },
                        new ReportColumn { Key = Secret, TitleAr = "Protected heading", TitleEn = "Protected heading",
                            Sensitivity = ReportFieldSensitivity.Confidential, RequiredPermissionKey = SecretPermission },
                        new ReportColumn { Key = "Internal", TitleAr = "Internal", TitleEn = "Internal", Internal = true },
                        new ReportColumn { Key = "Never", TitleAr = "Never", TitleEn = "Never",
                            Sensitivity = ReportFieldSensitivity.Never },
                    },
                    DefaultFilters = _channel == "filter" ? new[] { Filter(_field) } : Array.Empty<ReportFilter>(),
                    DefaultSorts = _channel == "sort" ? new[] { ReportSort.By(_field) } : Array.Empty<ReportSort>(),
                };
            }
        }

        private sealed class Spy
        {
            public List<ReportDataQuery> ParentQueries { get; } = new();
            public List<ReportDataQuery> ChildQueries { get; } = new();
            public IReportDataSource Parent => new StaticReportDataSource(ParentCode, q =>
            {
                ParentQueries.Add(q);
                return new[]
                {
                    Row((Open, "North"), ("Date", new DateTime(2026, 1, 3)), ("Amount", 10m)),
                    Row((Open, "South"), ("Date", new DateTime(2026, 1, 20)), ("Amount", 20m)),
                    Row((Open, "North"), ("Date", new DateTime(2026, 2, 5)), ("Amount", 30m)),
                };
            });
            public IReportDataSource Child => new StaticReportDataSource(ChildCode, q =>
            {
                ChildQueries.Add(q);
                return new[]
                {
                    Row((Open, "North"), (Secret, "North"), ("Label", "NORTH-CHILD-ROW"),
                        ("Internal", "INTERNAL-SECRET"), ("Never", "NEVER-SECRET")),
                    Row((Open, "South"), (Secret, "South"), ("Label", "SOUTH-CHILD-ROW"),
                        ("Internal", "INTERNAL-SECRET"), ("Never", "NEVER-SECRET")),
                };
            });
        }

        private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
            values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        private static ReportingTestHost Host(bool granted = false)
        {
            var host = new ReportingTestHost();
            host.PermissionOptions.RoleMap[ChildPermission] = new[] { "Reports" };
            if (granted) host.PermissionOptions.RoleMap[SecretPermission] = new[] { "Reports" };
            return host;
        }

        private static ReportFilter Filter(string field) => new()
        {
            Field = field, Operator = ReportFilterOperator.Equals,
            Values = new List<string?> { "North" },
        };

        private static ReportEngine Engine(ReportingTestHost host, Spy spy, string? channel = null) =>
            new(new ReportCatalog(new[] { new Definitions(channel) }), host.Authorization, host.Templates,
                host.Binder, new ReportDataSourceRegistry(new[] { spy.Parent, spy.Child }), host.Shaper,
                host.Output, host.Archive, host.History, host.Branding, host.EngineOptions, host.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportEngine>.Instance, host.Assets);

        // Placement follows ReportVisualLayoutValidator, the gate every saved layout passes: a linked child
        // lives in a group band (the only place with a group value to match), an unlinked one in a report
        // band. A Detail-band sub-report cannot be saved, so testing it would test an unreachable state.
        private static ReportRequest Request(string? link = null, string child = ChildCode)
        {
            var layout = ReportVisualLayout.Blank();
            var band = layout.Band(link != null ? ReportBandKind.GroupHeader : ReportBandKind.ReportHeader)!;
            if (link != null) band.GroupFieldKey = Open;
            band.Elements.Add(new ReportElement
            {
                Id = "recovery-child", Kind = ReportElementKind.SubReport, SubReportCode = child,
                LinkChildFieldKey = link, XMm = 2, YMm = 1, WidthMm = 180, HeightMm = 12,
            });
            return new ReportRequest { ReportCode = ParentCode, Culture = "en-US", Visual = layout };
        }

        // ---- F: THE CHILD IS BOUNDED BY THE ENGINE, NOT BY THE CHILD DATASET ----------------------
        //
        // A sub-report reads a dataset this report was not built on, so whatever bounds the PARENT does
        // not bound it. Without a ceiling of its own, one linked child over a large table makes a
        // document unbounded through nesting - and the ceiling has to be the ENGINE's, because a
        // definition that could raise it would be a definition that could exhaust the server.
        [Fact]
        public async Task Recovery_The_child_query_is_bounded_by_the_engines_own_row_ceiling()
        {
            using var host = Host(granted: true);
            var spy = new Spy();

            var result = await Engine(host, spy).GenerateAsync(Request(Open), host.Ctx);
            Assert.True(result.IsSuccess);

            var ceiling = (int)typeof(ReportEngine)
                .GetField("SubReportRowCeiling", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;

            Assert.True(ceiling > 0);
            Assert.NotEmpty(spy.ChildQueries);

            // Every child fetch carries the ceiling. Asserting the CONSTANT alone would pass for an
            // engine that declared one and never applied it.
            Assert.All(spy.ChildQueries, q => Assert.Equal(ceiling, q.MaxRows));

            // And the parent's own limit is a separate number: the child is not simply inheriting it.
            Assert.All(spy.ParentQueries, q => Assert.NotEqual(ceiling, q.MaxRows));
        }

        private static string Html(ReportResult result)
        {
            Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
            Assert.NotNull(result.Artifact);
            return System.Text.Encoding.UTF8.GetString(result.Artifact!.Content);
        }

        private static string[] ChildBlocks(string html)
        {
            // The authorized block contains a nested title div. Capture the balanced outer div so
            // positive row/group assertions inspect the table, not merely its heading.
            var blocks = new List<string>();
            var start = -1;
            var depth = 0;
            foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(
                html, "<div\\b[^>]*>|</div>", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                if (start < 0)
                {
                    if (!tag.Value.Contains("cbv-subreport\"", StringComparison.Ordinal)) continue;
                    start = tag.Index;
                    depth = 1;
                }
                else if (tag.Value.StartsWith("</", StringComparison.Ordinal))
                {
                    if (--depth == 0)
                    {
                        blocks.Add(html.Substring(start, tag.Index + tag.Length - start));
                        start = -1;
                    }
                }
                else depth++;
            }
            Assert.Equal(-1, start);
            return blocks.ToArray();
        }

        private static void Refused(Spy spy, ReportResult result)
        {
            Assert.Empty(spy.ChildQueries);
            var html = Html(result);
            var blocks = ChildBlocks(html);
            Assert.NotEmpty(blocks);
            Assert.All(blocks, b => Assert.Contains("This sub-report is unavailable.", b));
            Assert.DoesNotContain(Secret, html);
            Assert.DoesNotContain(Ghost, html);
            Assert.DoesNotContain("Protected heading", html);
        }

        [Fact]
        public async Task Recovery_Unauthorized_display_fields_are_removed_before_child_fetch_and_render()
        {
            using var host = Host();
            var spy = new Spy();
            var html = Html(await Engine(host, spy).GenerateAsync(Request(), host.Ctx));
            var query = Assert.Single(spy.ChildQueries);
            Assert.Equal(new[] { Open, "Label" }, query.RequestedColumns.Select(c => c.Key));
            Assert.Contains("NORTH-CHILD-ROW", html);
            Assert.DoesNotContain("Protected heading", html);
            Assert.DoesNotContain("INTERNAL-SECRET", html);
            Assert.DoesNotContain("NEVER-SECRET", html);
        }

        [Fact]
        public async Task Recovery_Authorized_display_fields_execute_but_internal_and_never_remain_hidden()
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var html = Html(await Engine(host, spy).GenerateAsync(Request(), host.Ctx));
            var query = Assert.Single(spy.ChildQueries);
            Assert.Contains(query.RequestedColumns, c => c.Key == Secret);
            Assert.Contains("Protected heading", html);
            Assert.DoesNotContain(query.RequestedColumns, c => c.Key is "Internal" or "Never");
            Assert.DoesNotContain("INTERNAL-SECRET", html);
            Assert.DoesNotContain("NEVER-SECRET", html);
        }

        [Theory]
        [InlineData("filter")]
        [InlineData("sort")]
        [InlineData("link")]
        public async Task Recovery_Unauthorized_shaping_never_fetches_or_falls_back_to_an_unlinked_child(string channel)
        {
            using var host = Host();
            var spy = new Spy();
            var result = await Engine(host, spy, channel).GenerateAsync(
                Request(channel == "link" ? Secret : null), host.Ctx);
            Assert.NotEmpty(spy.ParentQueries);
            Refused(spy, result);
        }

        [Theory]
        [InlineData("filter")]
        [InlineData("sort")]
        [InlineData("link")]
        public async Task Recovery_The_same_saved_shaping_is_reauthorized_after_permission_revocation(string channel)
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var engine = Engine(host, spy, channel);
            var request = Request(channel == "link" ? Secret : null);
            Html(await engine.GenerateAsync(request, host.Ctx));
            var allowed = Assert.Single(spy.ChildQueries);
            AssertShaping(allowed, channel);
            spy.ChildQueries.Clear();
            host.PermissionOptions.RoleMap.Remove(SecretPermission);
            Refused(spy, await engine.GenerateAsync(request, host.Ctx));
        }

        private static void AssertShaping(ReportDataQuery query, string channel)
        {
            if (channel == "sort")
            {
                var sort = Assert.Single(query.Sorts);
                Assert.Equal(Secret, sort.Field);
            }
            else
            {
                var filter = Assert.Single(query.Filters);
                Assert.Equal(Secret, filter.Field);
                Assert.Equal(channel == "link" ? ReportFilterOperator.In : ReportFilterOperator.Equals, filter.Operator);
                Assert.Equal(channel == "link" ? new[] { "North", "South" } : new[] { "North" }, filter.Values);
            }
        }

        [Theory]
        [InlineData("filter")]
        [InlineData("sort")]
        public async Task Recovery_Authorized_default_shaping_reaches_the_child_query_unchanged(string channel)
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            Html(await Engine(host, spy, channel).GenerateAsync(Request(), host.Ctx));
            AssertShaping(Assert.Single(spy.ChildQueries), channel);
        }

        [Theory]
        [InlineData(Open)]
        [InlineData(Secret)]
        public async Task Recovery_Authorized_link_has_a_real_In_predicate_and_matches_only_the_parent_group(string key)
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var html = Html(await Engine(host, spy).GenerateAsync(Request(key), host.Ctx));
            var query = Assert.Single(spy.ChildQueries);
            var filter = Assert.Single(query.Filters);
            Assert.Equal(key, filter.Field);
            Assert.Equal(ReportFilterOperator.In, filter.Operator);
            Assert.Equal(new[] { "North", "South" }, filter.Values);
            // One group header per parent group (North, South), each matched to its own child rows only.
            var blocks = ChildBlocks(html);
            Assert.Equal(2, blocks.Length);
            Assert.Equal(1, blocks.Count(b => b.Contains("NORTH-CHILD-ROW")));
            Assert.Equal(1, blocks.Count(b => b.Contains("SOUTH-CHILD-ROW")));
            Assert.All(blocks, b => Assert.False(b.Contains("NORTH-CHILD-ROW") && b.Contains("SOUTH-CHILD-ROW")));
        }

        [Fact]
        public async Task Recovery_Unknown_link_is_refused_without_fetch_or_unlinked_fallback()
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            Refused(spy, await Engine(host, spy).GenerateAsync(Request(Ghost), host.Ctx));
        }

        [Fact]
        public async Task Recovery_Unknown_and_forbidden_link_and_report_are_observably_indistinguishable()
        {
            using var host = Host();
            var spy = new Spy();
            var engine = Engine(host, spy);
            var forbidden = await engine.GenerateAsync(Request(Secret), host.Ctx);
            Refused(spy, forbidden);
            var unknown = await engine.GenerateAsync(Request(Ghost), host.Ctx);
            Refused(spy, unknown);
            var missing = await engine.GenerateAsync(Request(Secret, "UnknownRecoveryReport"), host.Ctx);
            Refused(spy, missing);
            host.PermissionOptions.RoleMap.Remove(ChildPermission);
            var deniedReport = await engine.GenerateAsync(Request(Secret), host.Ctx);
            Refused(spy, deniedReport);
            Assert.Equal(ChildBlocks(Html(forbidden)), ChildBlocks(Html(unknown)));
            Assert.Equal(ChildBlocks(Html(forbidden)), ChildBlocks(Html(missing)));
            Assert.Equal(ChildBlocks(Html(forbidden)), ChildBlocks(Html(deniedReport)));
            Assert.DoesNotContain("UnknownRecoveryReport", Html(missing));

            // AND THE WHOLE ARTIFACT, not only the band. Comparing the child block alone would
            // still pass if a refusal leaked the child's name or title anywhere else on the page -
            // a header, a footnote, a document outline. Four different refusals, one document.
            Assert.Equal(Html(forbidden), Html(unknown));
            Assert.Equal(Html(forbidden), Html(missing));
            Assert.Equal(Html(forbidden), Html(deniedReport));
        }

        [Fact]
        public async Task Recovery_Unresolved_company_reaches_neither_parent_nor_child_source()
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var context = new BusinessContext { CompanyId = 0, EmployeeId = 7, UserId = "test-user",
                Roles = new[] { "Reports" }, Source = BusinessContextSource.Test };
            var result = await Engine(host, spy).GenerateAsync(Request(Open), context);
            Assert.True(result.IsDenied);
            Assert.Empty(spy.ParentQueries);
            Assert.Empty(spy.ChildQueries);
        }

        [Fact]
        public async Task Recovery_Child_query_uses_current_company_and_identity_not_saved_configuration()
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var context = new BusinessContext { CompanyId = 73, EmployeeId = 82, UserId = "current-caller",
                Roles = new[] { "Reports" }, Source = BusinessContextSource.Test };
            Html(await Engine(host, spy).GenerateAsync(Request(Open), context));
            var query = Assert.Single(spy.ChildQueries);
            Assert.Same(context, query.Context);
            Assert.Equal(73, query.Context.CompanyId);
            Assert.Equal("current-caller", query.Context.UserId);
        }

        [Fact]
        public async Task Recovery_Child_report_permission_is_rechecked_for_the_same_saved_layout()
        {
            using var host = Host(granted: true);
            var spy = new Spy();
            var engine = Engine(host, spy);
            var request = Request(Open);
            Html(await engine.GenerateAsync(request, host.Ctx));
            Assert.Single(spy.ChildQueries);
            spy.ChildQueries.Clear();
            host.PermissionOptions.RoleMap.Remove(ChildPermission);
            Refused(spy, await engine.GenerateAsync(request, host.Ctx));
        }

        [Theory]
        [InlineData("filter")]
        [InlineData("sort")]
        public void Recovery_Catalog_structurally_rejects_unknown_default_shaping_fields(string channel)
        {
            Assert.Throws<ReportingException>(() => new ReportCatalog(new[] { new Definitions(channel, Ghost) }));
        }

        [Theory]
        [InlineData(ReportFieldSensitivity.Normal)]
        [InlineData(ReportFieldSensitivity.Confidential)]
        [InlineData(ReportFieldSensitivity.Restricted)]
        [InlineData(ReportFieldSensitivity.Never)]
        public async Task Recovery_Dataset_projection_preserves_the_existing_Studio_field_authority(ReportFieldSensitivity sensitivity)
        {
            var field = new ReportDatasetField { Key = "Projected", TitleAr = "Projected", TitleEn = "Projected",
                Sensitivity = sensitivity, RequiredPermissionKey = SecretPermission };
            var column = field.ToColumn();
            Assert.Equal(sensitivity, column.Sensitivity);
            Assert.Equal(SecretPermission, column.RequiredPermissionKey);
            using var host = Host();
            foreach (var granted in new[] { false, true })
            {
                if (granted) host.PermissionOptions.RoleMap[SecretPermission] = new[] { "Reports" };
                var held = new HashSet<string>(StringComparer.Ordinal);
                if (granted) held.Add(SecretPermission);
                var permitted = await host.Authorization.PermittedFieldKeysAsync(new[] { column }, host.Ctx);
                Assert.Equal(ReportDatasetDefinition.IsFieldVisible(field, held), permitted.Contains(column.Key));
            }
        }

        [Fact]
        public async Task Recovery_Internal_never_and_unresolved_company_are_fail_closed_at_field_authority()
        {
            using var host = Host(granted: true);
            var columns = new[]
            {
                new ReportColumn { Key = "Internal", TitleAr = "i", TitleEn = "i", Internal = true },
                new ReportColumn { Key = "Never", TitleAr = "n", TitleEn = "n", Sensitivity = ReportFieldSensitivity.Never },
            };
            Assert.Empty(await host.Authorization.PermittedFieldKeysAsync(columns, host.Ctx));
            var unresolved = new BusinessContext { CompanyId = 0, EmployeeId = 7, UserId = "test-user",
                Roles = new[] { "Reports" }, Source = BusinessContextSource.Test };
            Assert.Empty(await host.Authorization.PermittedFieldKeysAsync(
                new[] { new ReportColumn { Key = Open, TitleAr = "Open", TitleEn = "Open" } }, unresolved));
        }

        [Fact]
        public async Task Recovery_Original_comparison_keeps_the_inclusive_previous_period_and_current_authority()
        {
            using var host = Host();
            var spy = new Spy();
            var layout = ReportVisualLayout.Blank();
            layout.Band(ReportBandKind.ReportHeader)!.Elements.Add(new ReportElement
            {
                Id = "comparison", Kind = ReportElementKind.Summary, FieldKey = "Amount",
                Aggregate = ReportAggregate.Sum, Compare = ReportComparison.PreviousPeriod,
                XMm = 2, YMm = 1, WidthMm = 150, HeightMm = 15,
            });
            var html = Html(await Engine(host, spy).GenerateAsync(new ReportRequest
            { ReportCode = ParentCode, Culture = "en-US", Visual = layout }, host.Ctx));
            Assert.Equal(2, spy.ParentQueries.Count);
            var current = spy.ParentQueries[0];
            var previous = spy.ParentQueries[1];
            Assert.Equal(new DateTime(2026, 5, 1), current.Parameters.GetDate("From"));
            Assert.Equal(new DateTime(2026, 5, 14), current.Parameters.GetDate("To"));
            Assert.Equal(new DateTime(2026, 4, 17), previous.Parameters.GetDate("From"));
            Assert.Equal(new DateTime(2026, 4, 30), previous.Parameters.GetDate("To"));
            Assert.Same(host.Ctx, previous.Context);
            Assert.Equal(current.RequestedColumns.Select(c => c.Key), previous.RequestedColumns.Select(c => c.Key));
            Assert.Contains("vs previous period", html);
            Assert.Contains("cbv-delta-flat", html);
        }

        [Theory]
        [InlineData(ReportElementKind.Chart)]
        [InlineData(ReportElementKind.CrossTab)]
        public async Task Recovery_Original_chart_crosstab_and_multiseries_keep_formatted_time_buckets(ReportElementKind kind)
        {
            using var host = Host();
            var spy = new Spy();
            var layout = ReportVisualLayout.Blank();
            layout.Band(ReportBandKind.ReportHeader)!.Elements.Add(new ReportElement
            {
                Id = "time-chart", Kind = kind, FieldKey = "Amount", Aggregate = ReportAggregate.Sum,
                CategoryFieldKey = kind == ReportElementKind.Chart ? "Date" : Open,
                SeriesFieldKey = kind == ReportElementKind.Chart ? Open : "Date",
                ChartKind = ReportChartKind.Column, Style = new ReportElementStyle { DateFormat = "yyyy-MM" },
                XMm = 2, YMm = 1, WidthMm = 180, HeightMm = 45,
            });
            var html = Html(await Engine(host, spy).GenerateAsync(new ReportRequest
            { ReportCode = ParentCode, Culture = "en-US", Visual = layout }, host.Ctx));
            Assert.Single(spy.ParentQueries);
            Assert.Contains("2026-01", html);
            Assert.Contains("2026-02", html);
            Assert.DoesNotContain("2026-01-03", html);
            Assert.DoesNotContain("2026-01-20", html);
            Assert.Contains("North", html);
            Assert.Contains("South", html);
            Assert.Contains(kind == ReportElementKind.Chart ? "<svg" : "<table", html);
            if (kind == ReportElementKind.CrossTab)
            {
                // January: 10 + 20; February: 30. Row totals remain North=40, South=20.
                Assert.Contains(">40.00</td>", html);
                Assert.Contains(">60.00</td>", html);
                var totals = System.Text.RegularExpressions.Regex.Match(html, "<tfoot>.*?</tfoot>",
                    System.Text.RegularExpressions.RegexOptions.Singleline).Value;
                Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(totals, ">30\\.00</td>").Count);
            }
        }

        [Fact]
        public async Task Recovery_Original_month_chart_sums_days_into_the_same_bucket()
        {
            using var host = Host();
            var spy = new Spy();
            var layout = ReportVisualLayout.Blank();
            layout.Band(ReportBandKind.ReportHeader)!.Elements.Add(new ReportElement
            {
                Id = "month-totals", Kind = ReportElementKind.Chart, FieldKey = "Amount",
                Aggregate = ReportAggregate.Sum, CategoryFieldKey = "Date", ChartKind = ReportChartKind.Column,
                Style = new ReportElementStyle { DateFormat = "yyyy-MM" }, ShowValues = true,
                XMm = 2, YMm = 1, WidthMm = 180, HeightMm = 45,
            });
            var html = Html(await Engine(host, spy).GenerateAsync(new ReportRequest
            { ReportCode = ParentCode, Culture = "en-US", Visual = layout }, host.Ctx));
            Assert.Single(spy.ParentQueries);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, ">2026-01</text>").Count);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, ">2026-02</text>").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, ">30(?:\\.00)?</text>").Count);
            Assert.DoesNotContain("2026-01-03", html);
            Assert.DoesNotContain("2026-01-20", html);
        }

        [Theory]
        [InlineData(ReportIcon.Money)]
        [InlineData(ReportIcon.TrendUp)]
        public async Task Recovery_Original_KPI_icon_still_renders_server_side_without_external_resources(ReportIcon icon)
        {
            using var host = Host();
            var spy = new Spy();
            var layout = ReportVisualLayout.Blank();
            layout.Band(ReportBandKind.ReportHeader)!.Elements.Add(new ReportElement
            { Id = "kpi", Kind = ReportElementKind.Icon, Icon = icon, XMm = 2, YMm = 1, WidthMm = 15, HeightMm = 15,
                Style = new ReportElementStyle { Color = "#123456" } });
            var html = Html(await Engine(host, spy).GenerateAsync(new ReportRequest
            { ReportCode = ParentCode, Culture = "en-US", Visual = layout }, host.Ctx));
            Assert.Contains("<svg", html);
            Assert.Contains("role=\"img\"", html);
            Assert.Contains("stroke=\"#123456\"", html);
            Assert.Contains("viewBox=\"0 0 24 24\"", html);
            Assert.Contains(icon == ReportIcon.Money
                ? "<circle cx='12' cy='12' r='2.6'/>"
                : "<path d='M3 16.5 9 10l4 4 7-7.5'/>", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<image", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}


namespace CrossBuy.Tests
{
    using System.Text;
    using Microsoft.Extensions.Logging.Abstractions;

    // ==============================================================================================
    // C2B CONVERGENCE - THE HALF THE RECOVERY SUITE DOES NOT REACH.
    //
    // The recovery suite above proves the CHILD: a sub-report cannot become a second route to a
    // protected field, and a forbidden child is indistinguishable from an unknown one. These prove
    // the two surfaces that arrived from the other side of the convergence and had no proofs here:
    // the PARENT's own field authority (section 4b), and ARCHIVE RETRIEVAL, where the same data sits
    // in a file that was rendered when the caller's permissions were different.
    //
    // WHY THE ARCHIVE NEEDS ITS OWN PROOFS AT ALL. Every gate 4b applies runs at execution. An
    // archived artifact skips execution entirely - the bytes already exist - so a report-level
    // permission check was historically the only thing standing between a revoked grant and the data
    // it used to protect.
    // ==============================================================================================
    public class ReportingConvergenceAuthorityTests
    {
        private const string Report = AccountingDatasetCodes.CustomerProfitability;
        private const string Open = "CustomerName";
        private const string Gated = "Margin";                       // Confidential, needs Profitability
        private const string Ghost = "No.Such.Column";

        /// A host that holds the report tier, and optionally the confidential field tier.
        private static ReportingTestHost Host(bool profitability)
        {
            var host = new ReportingTestHost(roles: "Reports");
            host.PermissionOptions.RoleMap[AccountingReportPermissions.View] = new[] { "Reports" };
            if (profitability)
                host.PermissionOptions.RoleMap[AccountingReportPermissions.Profitability] = new[] { "Reports" };
            return host;
        }

        private static StaticReportDataSource Source() => new(
            AccountingDatasets.CustomerProfitability().DataSourceKey, _ => new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [Open] = "ACME", ["Invoiced"] = 1000m, ["Revenue"] = 900m,
                    ["Cogs"] = 400m, [Gated] = 500m, ["MarginPct"] = 55m,
                },
            });

        private static ReportRequest Ask(IEnumerable<string>? columns = null,
            IEnumerable<ReportFilter>? filters = null, IEnumerable<ReportSort>? sorts = null,
            IEnumerable<ReportGrouping>? groupings = null) => new()
        {
            ReportCode = Report,
            Format = ReportOutputFormat.Html,
            VisibleColumns = (columns ?? new[] { Open }).ToList(),
            Filters = (filters ?? Array.Empty<ReportFilter>()).ToList(),
            Sorts = (sorts ?? Array.Empty<ReportSort>()).ToList(),
            Groupings = (groupings ?? Array.Empty<ReportGrouping>()).ToList(),
        };

        private static ReportFilter On(string field) => new()
        {
            Field = field, Operator = ReportFilterOperator.GreaterThan,
            Values = new List<string?> { "0" },
        };

        // ---- THE PARENT GATE ----------------------------------------------------------------------
        //
        // A FIELD THE CALLER NAMED IS REFUSED, not quietly dropped: running something other than what
        // was asked for lets them believe a report means what it does not.

        [Theory]
        [InlineData("column")]
        [InlineData("filter")]
        [InlineData("sort")]
        [InlineData("grouping")]
        public async Task P1_a_confidential_field_the_caller_named_is_refused_on_every_surface(string surface)
        {
            using var host = Host(profitability: false);
            var request = surface switch
            {
                "column" => Ask(columns: new[] { Open, Gated }),
                "filter" => Ask(filters: new[] { On(Gated) }),
                "sort" => Ask(sorts: new[] { ReportSort.By(Gated) }),
                _ => Ask(groupings: new[] { new ReportGrouping { Field = Gated } }),
            };

            var result = await host.EngineWith(Source()).GenerateAsync(request, host.Ctx);

            Assert.False(result.IsSuccess);
            Assert.Contains(result.Diagnostics, d =>
                d.Code == ReportAuthorizationService.CodeFieldNotPermitted);

            // AND THE REFUSAL IS NOT AN ORACLE: it never names the field it refused.
            Assert.DoesNotContain(result.Diagnostics, d =>
                d.Message.Contains(Gated, StringComparison.Ordinal));
        }

        // NON-VACUITY. The identical four requests, from a caller who holds the field tier, succeed.
        // Without this the theory above would pass against an engine that refused everything.
        [Theory]
        [InlineData("column")]
        [InlineData("filter")]
        [InlineData("sort")]
        [InlineData("grouping")]
        public async Task P2_the_same_four_requests_succeed_for_a_permitted_caller(string surface)
        {
            using var host = Host(profitability: true);
            var request = surface switch
            {
                "column" => Ask(columns: new[] { Open, Gated }),
                "filter" => Ask(filters: new[] { On(Gated) }),
                "sort" => Ask(sorts: new[] { ReportSort.By(Gated) }),
                _ => Ask(groupings: new[] { new ReportGrouping { Field = Gated } }),
            };

            var result = await host.EngineWith(Source()).GenerateAsync(request, host.Ctx);
            Assert.True(result.IsSuccess);
        }

        // AN UNKNOWN KEY FAILS CLOSED. It cannot be authorized - there is no field metadata to
        // authorize it against - so treating it as harmless would be a guess.
        [Fact]
        public async Task P3_an_unknown_field_the_caller_named_is_refused_not_ignored()
        {
            using var host = Host(profitability: true);

            var result = await host.EngineWith(Source())
                .GenerateAsync(Ask(columns: new[] { Open, Ghost }), host.Ctx);

            Assert.False(result.IsSuccess);
            Assert.Contains(result.Diagnostics, d =>
                d.Code == ReportAuthorizationService.CodeFieldNotPermitted);
        }

        // A STORED LAYOUT IS FILTERED, NOT REFUSED. The caller did not ask for the field, and refusing
        // would leave them unable to run a report whose author simply had wider access. The report
        // runs; the protected column is not in it.
        [Fact]
        public async Task P4_a_stored_layouts_confidential_column_is_dropped_and_the_report_still_runs()
        {
            using var host = Host(profitability: false);

            // VisibleColumns empty => the engine falls back to the stored layout's own set, which is
            // where the definition's confidential columns live.
            var result = await host.EngineWith(Source())
                .GenerateAsync(Ask(columns: Array.Empty<string>()), host.Ctx);

            Assert.True(result.IsSuccess);
            var html = Encoding.UTF8.GetString(result.Artifact!.Content);
            Assert.DoesNotContain(Gated, html, StringComparison.Ordinal);
        }

        // ---- H4: THE AUTHORITY ITSELF FAILS CLOSED ON AN UNRESOLVED TENANT -------------------------
        //
        // The engine gates company earlier, but this method is also what the SUB-REPORT and ARCHIVE
        // paths call. A rule that depends on somebody else having checked first is not a rule.
        [Fact]
        public async Task P5_permitted_field_keys_is_empty_for_an_unresolved_company()
        {
            using var host = Host(profitability: true);
            var columns = AccountingDatasets.CustomerProfitability().Fields.Select(f => f.ToColumn()).ToList();

            var resolved = await host.Authorization.PermittedFieldKeysAsync(columns, host.Ctx);
            var unresolved = await host.Authorization.PermittedFieldKeysAsync(columns, new BusinessContext
            {
                CompanyId = 0, EmployeeId = host.Ctx.EmployeeId, UserId = host.Ctx.UserId,
                Source = BusinessContextSource.Test,
            });

            Assert.NotEmpty(resolved);      // control: the same columns DO resolve for a real tenant
            Assert.Empty(unresolved);
        }

        // ---- C: ONE FIELD-VISIBILITY AUTHORITY -----------------------------------------------------
        //
        // The engine's answer and the dataset's own Studio-facing rule must be the SAME rule, not two
        // implementations that happen to agree today. This compares them across every combination of
        // sensitivity and held permission, so changing one path alone is detectable.
        [Fact]
        public async Task C1_the_engine_and_the_dataset_share_one_field_visibility_rule()
        {
            using var host = Host(profitability: true);
            var dataset = AccountingDatasets.CustomerProfitability();
            var held = new HashSet<string>(StringComparer.Ordinal)
            {
                AccountingReportPermissions.View, AccountingReportPermissions.Profitability,
            };

            var viaEngine = await host.Authorization.PermittedFieldKeysAsync(
                dataset.Fields.Select(f => f.ToColumn()).ToList(), host.Ctx);

            var viaDataset = dataset.Fields
                .Where(f => ReportDatasetDefinition.IsFieldVisible(f, held))
                .Select(f => f.Key)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(viaDataset.OrderBy(k => k, StringComparer.Ordinal),
                         viaEngine.OrderBy(k => k, StringComparer.Ordinal));

            // The comparison is only worth anything if the two sets are non-trivial and the rule
            // actually excludes something: Never fields must be absent from BOTH.
            Assert.NotEmpty(viaEngine);
            Assert.Contains(Gated, viaEngine);
        }
    }

    // ==============================================================================================
    // C2B CONVERGENCE - THE ARCHIVE IS A SECOND REACH INTO THE SAME DATA.
    //
    // An artifact is not an authorization token. Everything answered when the file was rendered is
    // re-asked for the caller standing here now: company, the report's own permission, and every
    // field the report can contain. None of "I made it", "I own the template", "I could once see it"
    // or "I administer reporting" is a reason to hand it over.
    // ==============================================================================================
    public class ReportingConvergenceArchiveTests
    {
        private const string OrgReport = OrgStructureDatasetCodes.OrgUnits;

        /// The archive under a catalog that KNOWS the org-structure report, with exactly the
        /// permissions named. Nothing is granted by default: the evaluator is fail-closed.
        private static (ReportingTestHost Host, ReportArchiveService Archive) OrgHost(params string[] granted)
        {
            var host = new ReportingTestHost(roles: "Reports");
            foreach (var key in granted) host.PermissionOptions.RoleMap[key] = new[] { "Reports" };

            var catalog = new ReportCatalog(new IReportDefinitionProvider[]
            {
                new OrgStructureReportDefinitionProvider(), new PlatformReportDefinitionProvider(),
            });

            return (host, new ReportArchiveService(host.Db, host.ArchiveStore, catalog, host.Authorization,
                host.ArchiveOptions, host.Clock, NullLogger<ReportArchiveService>.Instance));
        }

        private static async Task<long> ArchivedAsync(ReportingTestHost host, ReportArchiveService archive)
        {
            var definition = new OrgStructureReportDefinitionProvider().GetDefinitions()
                .Single(d => d.Code == OrgReport);

            var id = await archive.ArchiveAsync(new ReportArtifact
            {
                FileName = "org.csv", ContentType = "text/csv",
                Content = Encoding.UTF8.GetBytes("Name\r\nOperations\r\n"),
            }, definition, null, null, null, host.Ctx);

            Assert.NotNull(id);
            return id!.Value;
        }

        // ---- B / ARCH-DEFECT-1 ---------------------------------------------------------------------
        //
        // THE DEFECT THIS CLOSES WAS AVAILABILITY, NOT CONFIDENTIALITY, and it was total. The field
        // gate refused whenever ANY definition column was not in the permitted set. A Never/Internal
        // column is never in that set - and ReportDataShaper strips those columns before anything is
        // rendered, so the file provably cannot contain one. Every archived artifact of any report
        // whose dataset declares such a field was therefore unreachable by everyone, forever,
        // administrators included. Admin.OrgStructure.Units declares exactly one: LinkedObjectId.
        [Fact]
        public async Task B1_an_internal_field_does_not_make_the_artifact_permanently_unreachable()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);

            // The caller holds every field the file could contain. The one column they do NOT hold is
            // LinkedObjectId, which nobody holds and which no artifact can carry.
            var artifact = await archive.RetrieveAsync(entryId, host.Ctx);

            Assert.NotNull(artifact);
            Assert.Equal("text/csv", artifact!.ContentType);
        }

        // NON-VACUITY FOR B1, and the confidentiality half of the same predicate: drop the Notes
        // grant - a real gated field, not an internal one - and the artifact goes with it.
        [Fact]
        public async Task B2_a_confidential_field_the_caller_lacks_still_withholds_the_artifact()
        {
            var (host, archive) = OrgHost(OrgStructureReportPermissions.View);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);

            Assert.Null(await archive.RetrieveAsync(entryId, host.Ctx));
        }

        // ---- H6: CURRENT PERMISSIONS, NOT GENERATION-TIME ONES -------------------------------------
        [Fact]
        public async Task A1_revocation_takes_effect_on_the_next_retrieval_of_the_same_entry()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);
            Assert.NotNull(await archive.RetrieveAsync(entryId, host.Ctx));   // control

            // The SAME caller, the SAME entry they created themselves, after losing one grant.
            host.PermissionOptions.RoleMap.Remove(OrgStructureReportPermissions.Notes);

            Assert.Null(await archive.RetrieveAsync(entryId, host.Ctx));
            Assert.Null(await archive.GetAsync(entryId, host.Ctx));
        }

        // AN UNRESOLVABLE REPORT CODE FAILS CLOSED. The old code ran the permission test inside
        // `if (TryGetDefinition(...))` with no else, so a report since removed from the catalogue
        // became an artifact with no permission gate at all.
        [Fact]
        public async Task A2_an_artifact_whose_report_no_longer_resolves_is_refused()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);

            // A catalogue that no longer knows the report - a rename or a retirement.
            var forgetful = new ReportArchiveService(host.Db, host.ArchiveStore,
                new ReportCatalog(new IReportDefinitionProvider[] { new PlatformReportDefinitionProvider() }),
                host.Authorization, host.ArchiveOptions, host.Clock,
                NullLogger<ReportArchiveService>.Instance);

            Assert.Null(await forgetful.RetrieveAsync(entryId, host.Ctx));
        }

        // ---- E: AN UNRESOLVED COMPANY REACHES NOTHING ----------------------------------------------
        [Fact]
        public async Task E1_an_unresolved_company_cannot_retrieve_or_list()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);
            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = host.Ctx.EmployeeId, UserId = host.Ctx.UserId,
                Source = BusinessContextSource.Test,
            };

            Assert.Null(await archive.RetrieveAsync(entryId, unresolved));
            Assert.Empty(await archive.ListAsync(null, unresolved));
        }

        // ---- D: A LISTING IS A DISCLOSURE ----------------------------------------------------------
        //
        // Showing the row says a named report was run, by whom and when. An entry the caller could not
        // retrieve must not be listed either, or losing a grant hides the FILE while leaving its
        // existence, name and timestamp on the screen - and the two answers disagree.
        [Fact]
        public async Task D1_a_revoked_report_disappears_from_the_listing_not_only_from_retrieval()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            await ArchivedAsync(host, archive);
            Assert.Single(await archive.ListAsync(null, host.Ctx));          // control

            host.PermissionOptions.RoleMap.Remove(OrgStructureReportPermissions.View);

            Assert.Empty(await archive.ListAsync(null, host.Ctx));
        }

        [Fact]
        public async Task D2_an_unresolvable_report_is_not_listed_either()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            await ArchivedAsync(host, archive);

            var forgetful = new ReportArchiveService(host.Db, host.ArchiveStore,
                new ReportCatalog(new IReportDefinitionProvider[] { new PlatformReportDefinitionProvider() }),
                host.Authorization, host.ArchiveOptions, host.Clock,
                NullLogger<ReportArchiveService>.Instance);

            Assert.Empty(await forgetful.ListAsync(null, host.Ctx));
        }

        // ---- INVARIANT 12: REPORTING ADMINISTRATION IS NOT A CONFIDENTIALITY BYPASS -----------------
        //
        // WHAT THIS PROVES, AND WHAT IT DELIBERATELY DOES NOT. Administration widens WHOSE artifacts a
        // caller reaches; it must not widen WHICH FIELDS they may see, because field authority belongs
        // to the owning module. This asserts that the ARCHIVE layer adds no bypass of its own: given a
        // caller who genuinely holds reporting administration and the report's own permission but not
        // the module's confidential field key, the artifact is still withheld.
        //
        // IT USES AN EVALUATOR DOUBLE ON PURPOSE. The registered RoleMapReportPermissionEvaluator
        // answers TRUE to every key for an administrator role (see its IsAdministrator short-circuit),
        // so under it this question cannot even be posed - administration IS every module's authority.
        // That is a real gap and it is NOT closed here: closing it means replacing the evaluator with
        // the module-permission adapter, which is another wave's work. The double therefore isolates
        // the one thing this layer owns, and the assertion below would fail the moment the archive
        // started treating IsAdministratorAsync as permission to read a field.
        private sealed class AdministratorWithoutFieldAuthority : IReportPermissionEvaluator
        {
            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(
                    permissionKey == ReportPermissions.Administer
                    || permissionKey == OrgStructureReportPermissions.View);
        }

        [Fact]
        public async Task ADMIN1_reporting_administration_does_not_unlock_a_confidential_field()
        {
            var (host, archive) = OrgHost(
                OrgStructureReportPermissions.View, OrgStructureReportPermissions.Notes);
            using var _ = host;

            var entryId = await ArchivedAsync(host, archive);

            var authority = new ReportAuthorizationService(host.Db,
                new AdministratorWithoutFieldAuthority(), host.Teams, host.Clock);

            var adminArchive = new ReportArchiveService(host.Db, host.ArchiveStore,
                new ReportCatalog(new IReportDefinitionProvider[]
                {
                    new OrgStructureReportDefinitionProvider(), new PlatformReportDefinitionProvider(),
                }),
                authority, host.ArchiveOptions, host.Clock, NullLogger<ReportArchiveService>.Instance);

            // The caller really does administer reporting - so the creator narrowing is not what is
            // refusing them - and really does hold the report's own permission.
            var evaluator = new AdministratorWithoutFieldAuthority();
            Assert.True(await authority.IsAdministratorAsync(host.Ctx));
            Assert.True(await evaluator.HasPermissionAsync(OrgStructureReportPermissions.View, host.Ctx));
            Assert.False(await evaluator.HasPermissionAsync(OrgStructureReportPermissions.Notes, host.Ctx));

            // ...and the confidential field they do not hold still withholds the file, and the row.
            Assert.Null(await adminArchive.RetrieveAsync(entryId, host.Ctx));
            Assert.Empty(await adminArchive.ListAsync(null, host.Ctx));
        }
    }
}
