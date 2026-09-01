using System.Globalization;
using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — the SHAPER.
    //
    // Pure and deterministic, so every rule here is asserted without a database. The most important thing this
    // file proves is the ASYMMETRY: an unusable sort or grouping is dropped with a warning, but an unusable FILTER
    // fails the run — because dropping a filter WIDENS the result set.
    public class ReportingShaperTests
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly ReportDefinition Definition = TestReportDefinitions.Sales();
        private static readonly ReportDataShaper Shaper = new();

        private static ReportDataSet Data(bool truncated = false,
            IReadOnlyList<ReportFilter>? appliedFilters = null,
            IReadOnlyList<ReportSort>? appliedSorts = null)
        {
            var builder = new ReportDataSetBuilder(Definition.Columns);
            foreach (var row in TestReportDefinitions.Rows()) builder.AddRow(row);
            return builder.Build(truncated, appliedFilters: appliedFilters, appliedSorts: appliedSorts);
        }

        private static ReportShapeResult Shape(ReportShapeRequest request, ReportDataSet? data = null) =>
            Shaper.Shape(Definition, data ?? Data(), request, Invariant);

        // ================================================================================================
        // 1. THE ASYMMETRY — the single most important rule in the shaper
        // ================================================================================================

        [Fact]
        public void A_filter_on_an_undeclared_column_FAILS_the_run_because_dropping_it_would_widen_the_result()
        {
            var result = Shape(new ReportShapeRequest
            {
                Filters = new[] { ReportFilter.Eq("NoSuchColumn", "x") },
            });

            Assert.False(result.IsValid);
            Assert.Null(result.View);   // no rows are handed on — that is the point
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeUnknownFilterField);
        }

        [Fact]
        public void A_filter_on_an_internal_column_fails_rather_than_being_quietly_ignored()
        {
            // Cost is Internal and therefore not Filterable in effect. Ignoring the filter would show the caller
            // MORE rows than the layout asked for.
            var result = Shape(new ReportShapeRequest
            {
                Filters = new[] { ReportFilter.Eq("Cost", "10") },
            });

            Assert.False(result.IsValid);
        }

        [Fact]
        public void An_unusable_SORT_is_dropped_with_only_a_warning_so_a_stale_layout_still_renders()
        {
            var result = Shape(new ReportShapeRequest
            {
                Sorts = new[] { ReportSort.By("NoSuchColumn") },
            });

            Assert.True(result.IsValid);
            Assert.Equal(6, result.View!.RowCount);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeSortDropped);
        }

        [Fact]
        public void An_unusable_GROUPING_is_dropped_with_only_a_warning()
        {
            var result = Shape(new ReportShapeRequest
            {
                Groupings = new[] { ReportGrouping.By("Item") },   // exists, but not Groupable
            });

            Assert.True(result.IsValid);
            Assert.False(result.View!.IsGrouped);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeGroupingDropped);
        }

        // ================================================================================================
        // 2. INTERNAL COLUMNS NEVER SURFACE
        // ================================================================================================

        [Fact]
        public void An_internal_column_named_by_a_layout_is_dropped_from_the_view()
        {
            var result = Shape(new ReportShapeRequest
            {
                VisibleColumns = new[] { "Branch", "Amount", "Cost" },
            });

            Assert.True(result.IsValid);
            Assert.DoesNotContain(result.View!.Columns, c => c.Key == "Cost");
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeColumnDropped && d.Field == "Cost");
        }

        [Fact]
        public void A_layout_naming_only_removed_columns_falls_back_to_the_definition_default()
        {
            var result = Shape(new ReportShapeRequest
            {
                VisibleColumns = new[] { "Gone", "AlsoGone" },
            });

            Assert.True(result.IsValid);
            Assert.NotEmpty(result.View!.Columns);
            Assert.DoesNotContain(result.View!.Columns, c => c.Internal);
        }

        // ================================================================================================
        // 3. FILTERS
        // ================================================================================================

        [Fact]
        public void Between_is_inclusive_on_both_ends()
        {
            // A user typing "2 May to 4 May" means both endpoints. A half-open range silently drops the last day —
            // the most common date-filter defect there is.
            var result = Shape(new ReportShapeRequest
            {
                Filters = new[] { ReportFilter.Between("SaleDate", "2026-05-02", "2026-05-04") },
            });

            Assert.Equal(3, result.View!.RowCount);
        }

        [Fact]
        public void An_empty_IN_list_matches_nothing_rather_than_everything()
        {
            var result = Shape(new ReportShapeRequest
            {
                Filters = new[] { ReportFilter.In("Branch") },
            });

            // Returning ALL rows here would be a spectacular leak, and it is what a naive implementation does.
            Assert.True(result.IsValid);
            Assert.Equal(0, result.View!.RowCount);
        }

        [Fact]
        public void Ordering_comparisons_exclude_nulls_rather_than_treating_them_as_zero()
        {
            var builder = new ReportDataSetBuilder(Definition.Columns);
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = "N", ["Amount"] = 100m });
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = "N", ["Amount"] = null });

            var result = Shaper.Shape(Definition, builder.Build(), new ReportShapeRequest
            {
                Filters = new[]
                {
                    new ReportFilter
                    {
                        Field = "Amount", Operator = ReportFilterOperator.GreaterThan, Values = new[] { "50" },
                    },
                },
            }, Invariant);

            // "unknown > 50" is not true, and it is not false-because-zero either. Excluding it is the only answer
            // that does not invent data.
            Assert.Equal(1, result.View!.RowCount);
        }

        [Fact]
        public void A_filter_the_data_source_already_pushed_down_is_not_applied_a_second_time()
        {
            var filter = ReportFilter.Eq("Branch", "North");

            // The source declares it applied the filter but returns all six rows (a source that lied). The shaper
            // must TRUST the declaration and skip re-filtering — that is what makes push-down possible at all.
            var result = Shape(new ReportShapeRequest { Filters = new[] { filter } },
                Data(appliedFilters: new[] { filter }));

            Assert.Equal(6, result.View!.RowCount);
        }

        [Fact]
        public void A_filter_matched_structurally_not_by_reference_so_a_deserialized_template_still_matches()
        {
            var requested = ReportFilter.Eq("Branch", "North");
            var rebuiltByTheSource = ReportFilter.Eq("Branch", "North");   // different instance, same content

            var result = Shape(new ReportShapeRequest { Filters = new[] { requested } },
                Data(appliedFilters: new[] { rebuiltByTheSource }));

            Assert.Equal(6, result.View!.RowCount);
        }

        // ================================================================================================
        // 4. GROUPING AND AGGREGATION
        // ================================================================================================

        [Fact]
        public void Grouping_bands_by_key_not_by_runs_of_equal_keys()
        {
            var result = Shape(new ReportShapeRequest
            {
                Groupings = new[] { ReportGrouping.By("Branch") },
                // Deliberately sorted by something else. If the shaper did not sort by the grouping key FIRST,
                // it would produce one band per RUN of equal keys instead of one band per key.
                Sorts = new[] { ReportSort.By("Item") },
            });

            Assert.True(result.View!.IsGrouped);
            Assert.Equal(2, result.View!.Groups.Count);
            Assert.Equal(new[] { "North", "South" }, result.View!.Groups.Select(g => g.KeyText));
        }

        [Fact]
        public void Nested_grouping_puts_detail_rows_only_at_the_deepest_level()
        {
            var result = Shape(new ReportShapeRequest
            {
                Groupings = new[] { ReportGrouping.By("Branch"), ReportGrouping.By("Category") },
            });

            var north = result.View!.Groups.Single(g => g.KeyText == "North");

            // An intermediate node holding its children's rows would double every subtotal a naive renderer
            // computed by walking the tree.
            Assert.Empty(north.Rows);
            Assert.Equal(2, north.Children.Count);
            Assert.All(north.Children, child => Assert.NotEmpty(child.Rows));
            Assert.Equal(3, north.RowCount);
        }

        [Fact]
        public void Subtotals_and_grand_totals_add_up()
        {
            var result = Shape(new ReportShapeRequest
            {
                Groupings = new[] { ReportGrouping.By("Branch") },
            });

            var view = result.View!;
            var north = view.Groups.Single(g => g.KeyText == "North");
            var south = view.Groups.Single(g => g.KeyText == "South");

            var northAmount = north.Subtotals.Single(s => s.ColumnKey == "Amount").Value;
            var southAmount = south.Subtotals.Single(s => s.ColumnKey == "Amount").Value;
            var grandAmount = view.GrandTotalFor("Amount")!.Value;

            Assert.Equal(81.250m, northAmount);       // 25.500 + 40.000 + 15.750
            Assert.Equal(49.300m, southAmount);       // 17.850 + 9.450 + 22.000
            Assert.Equal(northAmount + southAmount, grandAmount);

            // Qty too, so the assertion is not passing for one lucky column.
            Assert.Equal(55m, view.GrandTotalFor("Qty")!.Value);
        }

        [Fact]
        public void Average_is_taken_over_rows_that_have_a_value_not_over_all_rows()
        {
            var definition = TestReportDefinitions.Sales();
            var averaged = new ReportDefinition
            {
                Code = definition.Code, Module = definition.Module,
                TitleAr = definition.TitleAr, TitleEn = definition.TitleEn,
                DataSourceKey = definition.DataSourceKey, PermissionKey = definition.PermissionKey,
                Columns = new[]
                {
                    new ReportColumn { Key = "Branch", TitleAr = "ف", TitleEn = "Branch", Groupable = true },
                    new ReportColumn
                    {
                        Key = "Amount", TitleAr = "م", TitleEn = "Amount",
                        Type = ReportFieldType.Money, Aggregate = ReportAggregate.Average,
                    },
                },
            };

            var builder = new ReportDataSetBuilder(averaged.Columns);
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = "N", ["Amount"] = 100m });
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = "N", ["Amount"] = 200m });
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = "N", ["Amount"] = null });

            var result = Shaper.Shape(averaged, builder.Build(), new ReportShapeRequest(), Invariant);

            // 150, not 100. A null is "not measured"; treating it as zero drags every average toward zero.
            var average = result.View!.GrandTotalFor("Amount")!;
            Assert.Equal(150m, average.Value);
            Assert.Equal(2, average.Count);
        }

        // ================================================================================================
        // 5. SORTING
        // ================================================================================================

        [Fact]
        public void Multi_key_sorting_applies_keys_in_order_with_per_key_direction()
        {
            var result = Shape(new ReportShapeRequest
            {
                Sorts = new[] { ReportSort.By("Branch"), ReportSort.By("Amount", descending: true) },
            });

            var rows = result.View!.Rows;
            Assert.Equal("North", rows[0]["Branch"]);
            Assert.Equal(40.000m, rows[0]["Amount"]);   // biggest North amount first
            Assert.Equal("South", rows[3]["Branch"]);
            Assert.Equal(22.000m, rows[3]["Amount"]);
        }

        [Fact]
        public void A_source_that_already_sorted_in_the_requested_order_is_not_re_sorted()
        {
            var sorts = new[] { ReportSort.By("Branch") };

            var result = Shape(new ReportShapeRequest { Sorts = sorts }, Data(appliedSorts: sorts));

            // The rows come back in the source's order untouched — which is the observable effect of not re-sorting.
            Assert.Equal(TestReportDefinitions.Rows().Select(r => r["Item"]),
                result.View!.Rows.Select(r => r["Item"]));
        }

        // ================================================================================================
        // 6. TRUNCATION IS ALWAYS DECLARED
        // ================================================================================================

        [Fact]
        public void A_row_cap_truncates_and_says_so()
        {
            var result = Shape(new ReportShapeRequest { MaxRows = 2 });

            Assert.Equal(2, result.View!.RowCount);
            Assert.True(result.View!.Truncated);

            // A truncated report that does not say so is a WRONG report.
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeTruncated);
        }

        [Fact]
        public void Truncation_declared_by_the_data_source_is_carried_through()
        {
            var result = Shape(new ReportShapeRequest(), Data(truncated: true));

            Assert.True(result.View!.Truncated);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportDataShaper.CodeTruncated);
        }
    }
}
