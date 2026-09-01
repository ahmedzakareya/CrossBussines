using CrossBuy.BL.Reporting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Dataset) — the reusable DATASET LAYER.
    //
    // Every test here pins a rule that would otherwise fail at RUN time, on a user's screen, with a message
    // about an internal key. The validator's whole purpose is to move those failures to graph construction, so
    // the tests are mostly "does the validator actually catch this".
    //
    // NO PRODUCTION MODULE IS CONNECTED. Every dataset below is built in the test.
    // ============================================================================================
    public class ReportingDatasetTests
    {
        private const string Perm = "accounting.reports.view";
        private const string CostPerm = "accounting.reports.cost";

        private static ReportDatasetField Field(
            string key,
            ReportFieldType type = ReportFieldType.String,
            ReportFieldSensitivity sensitivity = ReportFieldSensitivity.Normal,
            string? requiredPermission = null,
            bool groupable = false,
            string? expression = null,
            params string[] dependsOn) => new()
            {
                Key = key,
                TitleAr = key,
                TitleEn = key,
                Type = type,
                Sensitivity = sensitivity,
                RequiredPermissionKey = requiredPermission,
                Groupable = groupable,
                Expression = expression,
                DependsOn = dependsOn,
                Filterable = expression == null,
                Sortable = expression == null,
            };

        private static ReportDatasetDefinition Dataset(
            params ReportDatasetField[] fields) => new()
            {
                DatasetCode = "Test.Sales.Lines",
                Module = "Accounting",
                TitleAr = "خطوط المبيعات",
                TitleEn = "Sales lines",
                DataSourceKey = "Test.Sales",
                RequiredPermissionKey = Perm,
                Fields = fields.Length > 0 ? fields : new[] { Field("Id", ReportFieldType.Integer) },
            };

        // ========================================================================================
        // The dataset is a CONTRACT: the validator refuses anything that could not work
        // ========================================================================================
        [Fact]
        public void A_well_formed_dataset_validates_clean()
        {
            var dataset = Dataset(
                Field("CustomerName", groupable: true),
                Field("Amount", ReportFieldType.Money),
                Field("Cost", ReportFieldType.Money, ReportFieldSensitivity.Confidential, CostPerm));

            Assert.Empty(ReportDatasetValidator.Validate(dataset));
        }

        // "Sensitive but ungated" is the combination that LOOKS safe and is not: the field is marked
        // Confidential, everyone assumes it is protected, and nothing checks anything.
        [Theory]
        [InlineData(ReportFieldSensitivity.Confidential)]
        [InlineData(ReportFieldSensitivity.Restricted)]
        public void A_sensitive_field_with_no_permission_key_is_refused(ReportFieldSensitivity sensitivity)
        {
            var dataset = Dataset(Field("Cost", ReportFieldType.Money, sensitivity, requiredPermission: null));

            var errors = ReportDatasetValidator.Validate(dataset);

            Assert.Contains(errors, e => e.Contains("declares no RequiredPermissionKey"));
        }

        // A source cannot push down a column it never produced, and the shaper computes calculated fields AFTER
        // filtering — so a "filterable" calculated field would silently filter on nulls.
        [Fact]
        public void A_calculated_field_cannot_be_filterable_or_sortable()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[]
                {
                    Field("Net", ReportFieldType.Money),
                    new ReportDatasetField
                    {
                        Key = "WithVat", TitleAr = "v", TitleEn = "v", Type = ReportFieldType.Money,
                        Expression = "Net * 1.15", DependsOn = new[] { "Net" },
                        Filterable = true, Sortable = true,          // the mistake
                    },
                },
            };

            var errors = ReportDatasetValidator.Validate(dataset);

            Assert.Contains(errors, e => e.Contains("cannot be Filterable or Sortable"));
        }

        // Without DependsOn the source would not know which underlying fields to fetch, so the expression would
        // evaluate against nulls and produce a plausible wrong number.
        [Fact]
        public void A_calculated_field_must_declare_what_it_reads()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[]
                {
                    new ReportDatasetField
                    {
                        Key = "WithVat", TitleAr = "v", TitleEn = "v", Type = ReportFieldType.Money,
                        Expression = "Net * 1.15", Filterable = false, Sortable = false,
                    },
                },
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("declares no DependsOn"));
        }

        [Fact]
        public void A_calculated_field_depending_on_an_unknown_field_is_refused()
        {
            var dataset = Dataset(
                Field("Net", ReportFieldType.Money),
                Field("WithVat", ReportFieldType.Money, expression: "Gross * 1.15", dependsOn: "Gross"));

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("unknown field 'Gross'"));
        }

        // A self-referential expression would loop the evaluator. The detector is iterative precisely because a
        // recursive walk would stack-overflow on the input it exists to catch.
        [Fact]
        public void A_dependency_cycle_between_calculated_fields_is_detected()
        {
            var dataset = Dataset(
                Field("A", ReportFieldType.Decimal, expression: "B + 1", dependsOn: "B"),
                Field("B", ReportFieldType.Decimal, expression: "A + 1", dependsOn: "A"));

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("dependency cycle"));
        }

        [Fact]
        public void A_self_referencing_calculated_field_is_detected()
        {
            var dataset = Dataset(Field("A", ReportFieldType.Decimal, expression: "A + 1", dependsOn: "A"));

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("dependency cycle"));
        }

        [Fact]
        public void Duplicate_field_keys_are_refused()
        {
            var dataset = Dataset(Field("Amount", ReportFieldType.Money), Field("Amount", ReportFieldType.Money));

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("duplicate field key"));
        }

        [Fact]
        public void A_dataset_with_no_permission_key_is_refused()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = "",
                Fields = new[] { Field("Id", ReportFieldType.Integer) },
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset),
                e => e.Contains("RequiredPermissionKey is required"));
        }

        [Fact]
        public void An_entity_ref_field_must_name_the_registry_code_it_points_at()
        {
            var dataset = Dataset(Field("CustomerId", ReportFieldType.EntityRef));

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("names no LookupEntityCode"));
        }

        // A drill-down level that is not Groupable cannot band — the level would render as one giant group.
        [Fact]
        public void A_drill_down_level_that_is_not_groupable_is_refused()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[] { Field("Region", groupable: false), Field("Amount", ReportFieldType.Money) },
                DrillDowns = new[]
                {
                    new ReportDatasetDrillDown
                    { Key = "geo", TitleAr = "g", TitleEn = "g", Levels = new[] { "Region" } },
                },
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("is not Groupable"));
        }

        [Fact]
        public void A_drill_through_mapping_from_an_unknown_field_is_refused()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[] { Field("CustomerName") },
                DrillThroughTargets = new[]
                {
                    new ReportDatasetDrillTarget
                    {
                        Key = "statement", TitleAr = "s", TitleEn = "s",
                        TargetReportCode = "Accounting.CustomerStatement",
                        ParameterMap = new Dictionary<string, string> { ["customerId"] = "CustomerId" },
                    },
                },
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("unknown field 'CustomerId'"));
        }

        // SystemSupplied means the ENGINE provides it and a request-supplied value is ignored. Required with no
        // default is therefore unsatisfiable by anyone.
        [Fact]
        public void A_system_supplied_required_parameter_with_no_default_is_refused()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[] { Field("Id", ReportFieldType.Integer) },
                Parameters = new[]
                {
                    new ReportParameterDescriptor
                    { Key = "AsOf", TitleAr = "a", TitleEn = "a", SystemSupplied = true, Required = true },
                },
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset), e => e.Contains("SystemSupplied and Required"));
        }

        // ========================================================================================
        // Field-level visibility — the point of the sensitivity classification
        // ========================================================================================
        [Fact]
        public void Sensitivity_never_hides_a_field_from_everyone_including_an_administrator()
        {
            var field = Field("InternalRowKey", ReportFieldType.String, ReportFieldSensitivity.Never);

            // Even with every permission in the world.
            var everything = new HashSet<string>(StringComparer.Ordinal) { Perm, CostPerm, "reporting.administer" };

            Assert.False(ReportDatasetDefinition.IsFieldVisible(field, everything));
        }

        [Fact]
        public void A_confidential_field_is_visible_only_with_its_own_permission()
        {
            var field = Field("Cost", ReportFieldType.Money, ReportFieldSensitivity.Confidential, CostPerm);

            Assert.False(ReportDatasetDefinition.IsFieldVisible(field, new HashSet<string>(StringComparer.Ordinal) { Perm }));
            Assert.True(ReportDatasetDefinition.IsFieldVisible(field, new HashSet<string>(StringComparer.Ordinal) { Perm, CostPerm }));
        }

        [Fact]
        public void Visible_fields_filters_the_set_the_caller_may_actually_see()
        {
            var dataset = Dataset(
                Field("CustomerName"),
                Field("Amount", ReportFieldType.Money),
                Field("Cost", ReportFieldType.Money, ReportFieldSensitivity.Confidential, CostPerm),
                Field("RowKey", ReportFieldType.String, ReportFieldSensitivity.Never));

            var ordinary = dataset.VisibleFields(new HashSet<string>(StringComparer.Ordinal) { Perm })
                .Select(f => f.Key).ToList();

            Assert.Equal(new[] { "CustomerName", "Amount" }, ordinary);
        }

        // The DATA layer's "Never" and the RENDERING layer's "Internal" must agree by construction, not by two
        // flags somebody keeps in step by hand — an export that includes a column the screen hides is the
        // classic reporting leak.
        [Fact]
        public void A_never_field_projects_to_an_internal_column()
        {
            var column = Field("RowKey", ReportFieldType.String, ReportFieldSensitivity.Never).ToColumn();

            Assert.True(column.Internal);
        }

        // A calculated field must not arrive at the renderer claiming to be filterable, whatever the author set.
        [Fact]
        public void A_calculated_field_projects_to_a_non_filterable_column()
        {
            var column = Field("WithVat", ReportFieldType.Money, expression: "Net * 1.15", dependsOn: "Net")
                .ToColumn();

            Assert.False(column.Filterable);
            Assert.False(column.Sortable);
        }

        // ========================================================================================
        // Operator surface
        // ========================================================================================
        [Fact]
        public void Text_fields_offer_contains_and_numeric_fields_offer_between()
        {
            Assert.Contains(ReportFilterOperator.Contains, ReportDatasetOperators.For(ReportFieldType.String));
            Assert.DoesNotContain(ReportFilterOperator.Between, ReportDatasetOperators.For(ReportFieldType.String));

            Assert.Contains(ReportFilterOperator.Between, ReportDatasetOperators.For(ReportFieldType.Money));
            Assert.DoesNotContain(ReportFilterOperator.Contains, ReportDatasetOperators.For(ReportFieldType.Money));
        }

        // A substring match against an entity id is never what the user meant, and offering it invites a report
        // that half-works.
        [Fact]
        public void An_entity_ref_field_does_not_offer_substring_matching()
        {
            var ops = ReportDatasetOperators.For(ReportFieldType.EntityRef);

            Assert.Contains(ReportFilterOperator.Equals, ops);
            Assert.Contains(ReportFilterOperator.In, ops);
            Assert.DoesNotContain(ReportFilterOperator.Contains, ops);
            Assert.DoesNotContain(ReportFilterOperator.StartsWith, ops);
        }

        [Fact]
        public void A_field_may_narrow_the_operator_set_but_not_widen_it()
        {
            var narrowed = new ReportDatasetField
            {
                Key = "Code", TitleAr = "c", TitleEn = "c", Type = ReportFieldType.String,
                SupportedOperators = new[] { ReportFilterOperator.Equals },
            };

            Assert.True(ReportDatasetOperators.IsAllowed(narrowed, ReportFilterOperator.Equals));
            Assert.False(ReportDatasetOperators.IsAllowed(narrowed, ReportFilterOperator.Contains));

            // Widening is caught by the validator, not silently honoured.
            var widened = Dataset(new ReportDatasetField
            {
                Key = "Amount", TitleAr = "a", TitleEn = "a", Type = ReportFieldType.Money,
                SupportedOperators = new[] { ReportFilterOperator.Contains },   // meaningless for Money
            });

            Assert.Contains(ReportDatasetValidator.Validate(widened),
                e => e.Contains("not valid for Money"));
        }

        // ========================================================================================
        // Registry
        // ========================================================================================
        private sealed class StubEvaluator : IReportPermissionEvaluator
        {
            private readonly HashSet<string> _held;
            public StubEvaluator(params string[] held) => _held = new HashSet<string>(held, StringComparer.Ordinal);

            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(_held.Contains(permissionKey));
        }

        private static BusinessContext Ctx(int companyId = 1) => new()
        {
            CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(),
        };

        // Two modules claiming one dataset code would let one silently hijack the other's permission key.
        [Fact]
        public void Two_datasets_claiming_one_code_fail_at_graph_construction()
        {
            var a = Dataset(Field("Id", ReportFieldType.Integer));
            var b = Dataset(Field("Id", ReportFieldType.Integer));

            var ex = Assert.Throws<ReportingException>(() =>
                new ReportDatasetRegistry(new IReportDatasetDefinition[] { a, b }, new StubEvaluator()));

            Assert.Contains("Duplicate dataset code", ex.Message);
        }

        // Validation at construction, not at first use.
        [Fact]
        public void An_invalid_dataset_fails_at_graph_construction()
        {
            var bad = Dataset(Field("Cost", ReportFieldType.Money, ReportFieldSensitivity.Confidential));

            var ex = Assert.Throws<ReportingException>(() =>
                new ReportDatasetRegistry(new IReportDatasetDefinition[] { bad }, new StubEvaluator()));

            Assert.Contains("RequiredPermissionKey", ex.Message);
        }

        [Fact]
        public void An_unknown_dataset_code_throws_a_named_exception()
        {
            var registry = new ReportDatasetRegistry(Array.Empty<IReportDatasetDefinition>(), new StubEvaluator());

            var ex = Assert.Throws<ReportDatasetNotRegisteredException>(() => registry.Resolve("Nope.Nothing"));
            Assert.Equal("Nope.Nothing", ex.DatasetCode);

            Assert.False(registry.TryResolve("Nope.Nothing", out var found));
            Assert.Null(found);
        }

        // The platform ships NO dataset in this increment. An empty registry is the honest state, not a broken
        // one — this test pins that "no production data source is connected" is checkable, not just asserted.
        [Fact]
        public async Task An_empty_registry_offers_nothing_to_the_studio()
        {
            var registry = new ReportDatasetRegistry(Array.Empty<IReportDatasetDefinition>(), new StubEvaluator(Perm));

            Assert.Empty(registry.RegisteredCodes);
            Assert.Empty(await registry.ListForStudioAsync(Ctx()));
        }

        [Fact]
        public async Task The_studio_list_excludes_a_dataset_the_caller_may_not_use()
        {
            var registry = new ReportDatasetRegistry(
                new IReportDatasetDefinition[] { Dataset(Field("Id", ReportFieldType.Integer)) },
                new StubEvaluator());   // holds nothing

            Assert.Empty(await registry.ListForStudioAsync(Ctx()));
        }

        [Fact]
        public async Task The_studio_list_includes_a_permitted_dataset()
        {
            var registry = new ReportDatasetRegistry(
                new IReportDatasetDefinition[] { Dataset(Field("Id", ReportFieldType.Integer)) },
                new StubEvaluator(Perm));

            var listed = await registry.ListForStudioAsync(Ctx());

            Assert.Single(listed);
            Assert.Equal("Test.Sales.Lines", listed[0].DatasetCode);
        }

        // A dataset withheld from the builder must still RESOLVE, so existing saved reports over it keep working.
        [Fact]
        public async Task A_dataset_withheld_from_the_studio_still_resolves_for_saved_reports()
        {
            var hidden = new ReportDatasetDefinition
            {
                DatasetCode = "Test.Hidden", Module = "Accounting", TitleAr = "h", TitleEn = "h",
                DataSourceKey = "Test.H", RequiredPermissionKey = Perm,
                Fields = new[] { Field("Id", ReportFieldType.Integer) },
                AvailableInStudio = false,
            };

            var registry = new ReportDatasetRegistry(new IReportDatasetDefinition[] { hidden }, new StubEvaluator(Perm));

            Assert.Empty(await registry.ListForStudioAsync(Ctx()));
            Assert.Equal("Test.Hidden", registry.Resolve("Test.Hidden").DatasetCode);
        }

        // Deprecation is exactly this pair: invisible to the builder, still resolvable for what already exists.
        [Fact]
        public async Task A_deprecated_dataset_leaves_the_studio_but_keeps_resolving()
        {
            var deprecated = new ReportDatasetDefinition
            {
                DatasetCode = "Test.Old", Module = "Accounting", TitleAr = "o", TitleEn = "o",
                DataSourceKey = "Test.O", RequiredPermissionKey = Perm,
                Fields = new[] { Field("Id", ReportFieldType.Integer) },
                DeprecationNoticeEn = "Use Test.New instead.",
                DeprecationNoticeAr = "استخدم Test.New بدلاً منه.",
                SupersededByDatasetCode = "Test.New",
            };

            var registry = new ReportDatasetRegistry(new IReportDatasetDefinition[] { deprecated }, new StubEvaluator(Perm));

            Assert.Empty(await registry.ListForStudioAsync(Ctx()));
            Assert.True(registry.TryResolve("Test.Old", out _));
        }

        // Fail closed on an unresolved tenant, matching the platform rule that an unresolved company scope reads
        // no company-scoped data.
        [Fact]
        public async Task An_unresolved_company_gets_no_datasets()
        {
            var registry = new ReportDatasetRegistry(
                new IReportDatasetDefinition[] { Dataset(Field("Id", ReportFieldType.Integer)) },
                new StubEvaluator(Perm));

            Assert.Empty(await registry.ListForStudioAsync(Ctx(companyId: 0)));
        }

        // ========================================================================================
        // Versioning
        // ========================================================================================
        [Fact]
        public void A_dataset_version_renders_as_major_dot_minor()
        {
            Assert.Equal("2.5", new ReportDatasetVersion { Major = 2, Minor = 5 }.ToString());
            Assert.Equal("1.1", new ReportDatasetVersion().ToString());
        }

        [Fact]
        public void Naming_a_superseding_dataset_without_deprecating_is_refused()
        {
            var dataset = new ReportDatasetDefinition
            {
                DatasetCode = "Test.X", Module = "Accounting", TitleAr = "x", TitleEn = "x",
                DataSourceKey = "Test.X", RequiredPermissionKey = Perm,
                Fields = new[] { Field("Id", ReportFieldType.Integer) },
                SupersededByDatasetCode = "Test.Y",       // but no notice
            };

            Assert.Contains(ReportDatasetValidator.Validate(dataset),
                e => e.Contains("names a superseding dataset but carries no deprecation notice"));
        }
    }
}
