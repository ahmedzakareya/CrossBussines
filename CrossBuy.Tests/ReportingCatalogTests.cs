using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — the CATALOG is the frozen vocabulary, and its constructor is the gate.
    //
    // Every test here asserts something that would otherwise fail deep inside a render, where the error is
    // unrecognisable. The catalog validates in its CONSTRUCTOR precisely so these become DI-graph failures instead.
    public class ReportingCatalogTests
    {
        private static ReportCatalog Catalog(params ReportDefinition[] definitions) =>
            new(new IReportDefinitionProvider[] { new InlineProvider("Inline", definitions) });

        private sealed class InlineProvider : IReportDefinitionProvider
        {
            private readonly ReportDefinition[] _definitions;
            public InlineProvider(string name, ReportDefinition[] definitions)
            {
                ProviderName = name;
                _definitions = definitions;
            }
            public string ProviderName { get; }
            public IEnumerable<ReportDefinition> GetDefinitions() => _definitions;
        }

        // ================================================================================================
        // 1. THE VOCABULARY IS FROZEN AND UNIQUE
        // ================================================================================================

        [Fact]
        public void An_unregistered_report_code_throws_rather_than_returning_an_empty_report()
        {
            using var host = new ReportingTestHost();

            // An unknown code is a WIRING BUG. Returning an empty report would let a broken deployment look like a
            // report with no data — the failure mode this exception exists to prevent.
            var ex = Assert.Throws<ReportNotRegisteredException>(() => host.Catalog.GetDefinition("No.Such.Report"));
            Assert.Equal("No.Such.Report", ex.ReportCode);
        }

        [Fact]
        public void Two_providers_declaring_the_same_code_fail_at_construction_and_the_error_names_both()
        {
            var a = TestReportDefinitions.Sales();

            var ex = Assert.Throws<ReportingException>(() => new ReportCatalog(new IReportDefinitionProvider[]
            {
                new InlineProvider("ProviderOne", new[] { a }),
                new InlineProvider("ProviderTwo", new[] { a }),
            }));

            // Both provider names appear, because "duplicate code" is useless without knowing who else claimed it.
            Assert.Contains("ProviderOne", ex.Message);
            Assert.Contains("ProviderTwo", ex.Message);
        }

        [Theory]
        [InlineData("lowercase.code")]
        [InlineData("NoDot")]
        [InlineData("Has Space.Report")]
        [InlineData("Too.Many.Segments.In.This.Code.Here")]
        public void A_malformed_code_is_refused(string code)
        {
            var definition = With(TestReportDefinitions.Sales(), code: code);
            Assert.Throws<ReportingException>(() => Catalog(definition));
        }

        // ================================================================================================
        // 2. THE PERMISSION KEY IS THE ONE FIELD THAT MAY NOT BE FORGOTTEN
        // ================================================================================================

        [Fact]
        public void A_definition_with_no_permission_key_is_refused_and_says_to_use_Public_explicitly()
        {
            var definition = With(TestReportDefinitions.Sales(), permissionKey: "");

            var ex = Assert.Throws<ReportingException>(() => Catalog(definition));

            // The message must push the author toward an EXPLICIT decision. Omission must never be able to mean
            // "unrestricted" — that is the single most dangerous default a report platform could have.
            Assert.Contains("PermissionKey is required", ex.Message);
            Assert.Contains("ReportPermissions.Public", ex.Message);
        }

        // ================================================================================================
        // 3. DEFAULT QUERY INTENT MUST REFERENCE REAL, PERMITTED COLUMNS
        // ================================================================================================

        [Fact]
        public void A_default_sort_on_an_undeclared_column_is_refused()
        {
            var definition = With(TestReportDefinitions.Sales(),
                defaultSorts: new[] { ReportSort.By("NoSuchColumn") });

            var ex = Assert.Throws<ReportingException>(() => Catalog(definition));
            Assert.Contains("NoSuchColumn", ex.Message);
        }

        [Fact]
        public void A_default_grouping_on_a_column_that_is_not_marked_groupable_is_refused()
        {
            // "Item" exists but Groupable defaults to false — grouping is opt-in per column, so this must fail
            // rather than silently produce one band per row.
            var definition = With(TestReportDefinitions.Sales(),
                defaultGroupings: new[] { ReportGrouping.By("Item") });

            var ex = Assert.Throws<ReportingException>(() => Catalog(definition));
            Assert.Contains("groupable", ex.Message);
        }

        [Fact]
        public void A_default_sort_on_an_internal_column_is_refused()
        {
            // Cost is Internal. A definition that sorted by it would leak nothing directly, but it would establish
            // that internal columns are addressable in query intent — and the next step is displaying one.
            var definition = With(TestReportDefinitions.Sales(),
                defaultSorts: new[] { ReportSort.By("Cost") });

            Assert.Throws<ReportingException>(() => Catalog(definition));
        }

        [Fact]
        public void A_system_supplied_parameter_may_not_also_be_required()
        {
            var sales = TestReportDefinitions.Sales();
            var parameters = sales.Parameters.ToList();
            parameters.Add(new ReportParameterDescriptor
            {
                Key = "Bogus", TitleAr = "أ", TitleEn = "B",
                SystemSupplied = true, Required = true,
            });

            var ex = Assert.Throws<ReportingException>(() => Catalog(With(sales, parameters: parameters)));

            // Required + SystemSupplied is a contradiction: the engine fills it (so Required is meaningless) or it
            // does not (so Required is unsatisfiable). Either way the flag lies to the UI.
            Assert.Contains("SystemSupplied and Required", ex.Message);
        }

        [Fact]
        public void A_default_format_outside_the_capability_list_is_refused()
        {
            var definition = With(TestReportDefinitions.Sales(),
                capabilities: new ReportCapabilities { Formats = new[] { ReportOutputFormat.Csv } },
                defaultFormat: ReportOutputFormat.Pdf);

            Assert.Throws<ReportingException>(() => Catalog(definition));
        }

        [Fact]
        public void Both_a_bilingual_title_and_at_least_one_column_are_required()
        {
            Assert.Throws<ReportingException>(() => Catalog(With(TestReportDefinitions.Sales(), titleEn: "")));
            Assert.Throws<ReportingException>(() =>
                Catalog(With(TestReportDefinitions.Sales(), columns: Array.Empty<ReportColumn>())));
        }

        // ================================================================================================
        // 4. BROWSING
        // ================================================================================================

        [Fact]
        public void Query_filters_by_module_category_tag_and_free_text()
        {
            using var host = new ReportingTestHost();

            Assert.Contains(host.Catalog.Query(module: "Test"), d => d.Code == TestReportDefinitions.SalesCode);
            Assert.Empty(host.Catalog.Query(module: "NoSuchModule"));

            Assert.Contains(host.Catalog.Query(tag: "test"), d => d.Code == TestReportDefinitions.SalesCode);

            // Free text matches the code and BOTH titles — an Arabic-speaking user searches in Arabic.
            Assert.Contains(host.Catalog.Query(search: "Sales"), d => d.Code == TestReportDefinitions.SalesCode);
            Assert.Contains(host.Catalog.Query(search: "المبيعات"), d => d.Code == TestReportDefinitions.SalesCode);
        }

        [Fact]
        public void The_platform_ships_its_own_reports_and_they_pass_the_same_validation()
        {
            // Construction alone proves it: the catalog validates in its constructor, so if either platform
            // definition were malformed this host could not be built.
            using var host = new ReportingTestHost();

            Assert.Contains(host.Catalog.GetDefinitions(), d => d.Code == PlatformReportCodes.ReportCatalog);
            Assert.Contains(host.Catalog.GetDefinitions(), d => d.Code == PlatformReportCodes.ReportRunHistory);
            Assert.Contains(PlatformReportCodes.CategoryKey, host.Catalog.GetCategoryKeys());
        }

        [Fact]
        public void Definitions_are_returned_in_a_stable_order()
        {
            using var host = new ReportingTestHost();

            var first = host.Catalog.GetDefinitions().Select(d => d.Code).ToList();
            var second = host.Catalog.GetDefinitions().Select(d => d.Code).ToList();

            // Stable ordering matters because it drives the report browser: a menu that reshuffles between requests
            // is unusable.
            Assert.Equal(first, second);
            Assert.Equal(first.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                first.OrderBy(c => c, StringComparer.Ordinal).ToList());
        }

        // ================================================================================================
        // 5. DATA SOURCE REGISTRY
        // ================================================================================================

        [Fact]
        public void An_unregistered_data_source_key_throws_and_names_the_report_that_needed_it()
        {
            var registry = new ReportDataSourceRegistry(Array.Empty<IReportDataSource>());

            var ex = Assert.Throws<ReportDataSourceNotRegisteredException>(() =>
                registry.Resolve("Nope.Key", TestReportDefinitions.SalesCode));

            Assert.Equal("Nope.Key", ex.DataSourceKey);
            Assert.Contains(TestReportDefinitions.SalesCode, ex.Message);
        }

        [Fact]
        public void Two_data_sources_claiming_one_key_fail_at_construction()
        {
            // Last-registration-wins here would let a module silently hijack another module's reports — the exact
            // opposite of the renderer registry, where last-wins IS the substitution mechanism.
            Assert.Throws<ReportingException>(() => new ReportDataSourceRegistry(new IReportDataSource[]
            {
                TestReportDefinitions.CreateDataSource(),
                TestReportDefinitions.CreateDataSource(),
            }));
        }

        // ------------------------------------------------------------------------------------------------
        private static ReportDefinition With(ReportDefinition source,
            string? code = null, string? titleEn = null, string? permissionKey = null,
            IReadOnlyList<ReportColumn>? columns = null,
            IReadOnlyList<ReportParameterDescriptor>? parameters = null,
            IReadOnlyList<ReportSort>? defaultSorts = null,
            IReadOnlyList<ReportGrouping>? defaultGroupings = null,
            ReportCapabilities? capabilities = null,
            ReportOutputFormat? defaultFormat = null) => new()
            {
                Code = code ?? source.Code,
                Module = source.Module,
                TitleAr = source.TitleAr,
                TitleEn = titleEn ?? source.TitleEn,
                DataSourceKey = source.DataSourceKey,
                PermissionKey = permissionKey ?? source.PermissionKey,
                CategoryKey = source.CategoryKey,
                Tags = source.Tags,
                Columns = columns ?? source.Columns,
                Parameters = parameters ?? source.Parameters,
                DefaultSorts = defaultSorts ?? Array.Empty<ReportSort>(),
                DefaultGroupings = defaultGroupings ?? Array.Empty<ReportGrouping>(),
                Capabilities = capabilities ?? source.Capabilities,
                DefaultFormat = defaultFormat ?? source.DefaultFormat,
            };
    }
}
