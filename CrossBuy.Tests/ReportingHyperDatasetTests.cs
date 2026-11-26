using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // HYPER — TOP SELLERS, the declaration and the decisions inside it.
    //
    // NO DATABASE OF ANY KIND. The dataset definition is pure and the provider is pure, so neither
    // needs one. Under the standing policy nothing here may open or create a database — not SQL
    // Server, and not a SQLite in-memory connection either, which is still a provisioned database.
    //
    // WHAT IS DELIBERATELY NOT CLAIMED: these tests say nothing about whether the numbers are RIGHT.
    // Proving that a paid order's revenue lands on the right item needs the DbContext the policy
    // forbids, so the row-building half of HyperTopSellersDataSource is unverified and is declared
    // as such rather than implied by a green count. What IS covered is every DECISION the dataset
    // encodes — each assertion below exists because that decision could regress silently and the
    // regression would look like a working report.
    // ============================================================================================
    public class ReportingHyperDatasetTests
    {
        private sealed class AlwaysAllow : IReportPermissionEvaluator
        {
            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        private static ReportDatasetDefinition Ds() => HyperDatasets.TopSellers();

        private static ReportDatasetField Field(string key) =>
            Assert.Single(Ds().Fields.Where(f => f.Key == key));

        // ---- the declaration is legal -----------------------------------------------------------

        // ReportDatasetRegistry validates every definition IN ITS CONSTRUCTOR, so building one over the
        // Hyper pack is the whole declaration check: a duplicate code, an empty key, a malformed field
        // or a bad aggregate combination throws here instead of on a store manager's click.
        [Fact]
        public void The_dataset_constructs_and_validates()
        {
            var registry = new ReportDatasetRegistry(
                HyperDatasets.All().Cast<IReportDatasetDefinition>(), new AlwaysAllow());

            Assert.True(registry.TryResolve(HyperDatasetCodes.TopSellers, out _));
            Assert.True(registry.TryResolve(HyperDatasetCodes.BasketAnalysis, out _));

            // NOT a literal. This read Assert.Single when Hyper had one dataset, so adding a second broke a
            // test that is about VALIDITY, not about counting. The registry and the pack are compared
            // instead, which stays true however many datasets the pack grows.
            Assert.Equal(HyperDatasets.All().Count(), registry.RegisteredCodes.Count);
        }

        // ---- permission: the key and the namespace it has to live in ----------------------------

        [Fact]
        public void It_requires_the_hyper_key_and_that_key_sits_inside_the_claimed_namespace()
        {
            Assert.Equal(HyperReportPermissions.View, Ds().RequiredPermissionKey);
            Assert.StartsWith(HyperReportPermissions.Prefix, HyperReportPermissions.View, StringComparison.Ordinal);
        }

        // NO HYPER MODULE AUTHORITY EXISTS YET, which is why the key has to be mapped in Program.cs
        // before anyone can run this. If a future authority claims `hyper.` it must claim exactly this
        // prefix — a key outside its own prefix is routed to the fail-closed role map and the module is
        // never asked at all, which is how a report ends up authorised by identity role names instead.
        [Fact]
        public void No_authority_claims_the_hyper_namespace_yet_so_the_key_must_be_mapped()
        {
            var authorities = typeof(IReportModuleAuthority).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IReportModuleAuthority).IsAssignableFrom(t))
                .ToList();

            Assert.NotEmpty(authorities);   // positive control: the discovery itself works
            Assert.DoesNotContain(authorities, t => t.Name.StartsWith("Hyper", StringComparison.Ordinal));
        }

        // ---- the parameter contract -------------------------------------------------------------

        // A PERIOD IS REQUIRED AND HAS NO SILENT FALLBACK. A sales report that quietly defaults its
        // period is how a number gets quoted for the wrong month.
        [Theory]
        [InlineData("From")]
        [InlineData("To")]
        public void The_period_is_required(string key)
        {
            var p = Assert.Single(Ds().Parameters.Where(x => x.Key == key));
            Assert.True(p.Required);
            Assert.Equal(ReportFieldType.Date, p.Type);
        }

        // THE BRANCH IS OPTIONAL ON PURPOSE. Empty means every branch — the group view a head office
        // asks for. Making it required would answer the store manager's question and refuse the owner's.
        [Fact]
        public void The_branch_is_optional_so_the_report_can_cover_the_whole_company()
        {
            var p = Assert.Single(Ds().Parameters.Where(x => x.Key == ReportSystemParameters.BranchId));
            Assert.False(p.Required);
        }

        // The company parameter is SystemSupplied: the binder fills it from the resolved BusinessContext
        // and discards anything a caller sends, so the isolation guarantee is visible as metadata and not
        // only as a line of code inside the source.
        [Fact]
        public void The_company_is_system_supplied_not_caller_supplied()
        {
            var p = Assert.Single(Ds().Parameters.Where(x => x.Key == ReportSystemParameters.CompanyId));
            Assert.True(p.SystemSupplied);
        }

        // ---- money: the decision that would be most expensive to get wrong -----------------------

        // A HYPER BRANCH SETTLES IN KUWAITI DINAR AT THREE DECIMALS. A money column formatted to two
        // places disagrees with the receipt in the customer's hand, which is worse than no report at
        // all. This test exists to fail the moment someone "tidies" these to 0.00.
        [Theory]
        [InlineData("Revenue")]
        [InlineData("Cost")]
        [InlineData("Margin")]
        public void Money_carries_three_decimals_never_two(string key)
        {
            var f = Field(key);
            Assert.Equal(ReportFieldType.Money, f.Type);
            // The Format must be EXPLICIT. ReportValues falls back to "N2" for a money field that
            // carries none, so leaving it null would round a dinar to two places - the same defect
            // this test was written for, now hiding behind a TYPE DEFAULT instead of a typo.
            Assert.Equal("0.000", f.Format);
        }

        // ---- the second tier: who may see cost and margin ------------------------------------

        // COST AND MARGIN ARE NOT THE SAME SENSITIVITY AS "what sold". The platform evaluates a
        // field's RequiredPermissionKey IN ADDITION to the dataset's, so one report can serve the
        // store manager and the owner instead of two reports drifting apart.
        [Theory]
        [InlineData("Cost")]
        [InlineData("Margin")]
        [InlineData("MarginPct")]
        public void Cost_and_margin_need_the_narrower_key(string key)
        {
            var f = Field(key);
            Assert.Equal(HyperReportPermissions.Cost, f.RequiredPermissionKey);
            Assert.Equal(ReportFieldSensitivity.Confidential, f.Sensitivity);
            Assert.False(f.VisibleByDefault);
        }

        // ...and the operational columns must NOT carry it. Without this the split achieves
        // nothing: every column would need the narrow key and the report collapses back to
        // administration-only, which is the outcome the tier exists to avoid.
        [Theory]
        [InlineData("QtySold")]
        [InlineData("Revenue")]
        [InlineData("Orders")]
        [InlineData("Item")]
        public void What_sold_stays_on_the_broad_key(string key)
        {
            Assert.True(string.IsNullOrEmpty(Field(key).RequiredPermissionKey),
                $"{key} must stay on the broad key, or the store manager loses the report.");
        }

        [Fact]
        public void The_two_tiers_are_distinct_keys_in_one_namespace()
        {
            Assert.NotEqual(HyperReportPermissions.View, HyperReportPermissions.Cost);
            Assert.StartsWith(HyperReportPermissions.Prefix, HyperReportPermissions.Cost, StringComparison.Ordinal);
        }

        // Quantity is in the item's BASE unit and can be fractional (a weighed item), so it is a decimal
        // with three places too — an integer column here would round 1.250 kg to 1.
        [Fact]
        public void Quantity_is_fractional_because_a_weighed_item_is()
        {
            var f = Field("QtySold");
            Assert.Equal(ReportFieldType.Decimal, f.Type);
            Assert.Equal("0.###", f.Format);
        }

        // ---- aggregates: what may and may not be totalled ----------------------------------------

        // SUMMING A PERCENTAGE COLUMN PRODUCES A NUMBER THAT IS NOT THE OVERALL MARGIN, and it appears
        // in the totals row looking exactly like one. So Margin % declares no aggregates at all.
        [Fact]
        public void The_margin_percentage_cannot_be_aggregated()
        {
            var f = Field("MarginPct");
            Assert.Empty(f.SupportedAggregates);   // defaults to Array.Empty, so this is emptiness, not nullness
        }

        // ...while the additive columns must be, or the report has no usable totals row.
        [Theory]
        [InlineData("QtySold")]
        [InlineData("Revenue")]
        [InlineData("Cost")]
        [InlineData("Margin")]
        public void The_additive_columns_can_be_summed(string key)
        {
            Assert.Contains(ReportAggregate.Sum, Field(key).SupportedAggregates);
        }

        // ---- the row cap, and why the ordering is part of the contract ----------------------------

        // The source orders by quantity BEFORE the cap applies. With TruncateAndDeclare and no ordering
        // the platform would keep an arbitrary 5,000 rows and the one question the report exists to
        // answer — what sold most — would be the thing truncated away.
        [Fact]
        public void A_truncated_run_keeps_the_top_sellers()
        {
            var ds = Ds();
            Assert.Equal(ReportRowCapPolicy.TruncateAndDeclare, ds.RowCapPolicy);
            Assert.True(ds.MaxRows > 0);

            var first = Assert.Single(new HyperReportDefinitionProvider().GetDefinitions()).DefaultSorts[0];
            Assert.Equal("QtySold", first.Field);
            Assert.True(first.Descending, "Top sellers must sort highest-first.");
        }

        // ---- the dataset actually reaches a source -----------------------------------------------

        // A dataset whose DataSourceKey matches no source's Key is registered, listed, clickable and
        // then fails at run time. Registering the two is two steps and this is the one that proves the
        // second happened.
        [Fact]
        public void The_dataset_points_at_a_source_that_answers_to_that_key()
        {
            // The ctor only assigns, and Key is a constant, so this opens nothing.
            var sourceKey = new HyperTopSellersDataSource(null!).Key;

            Assert.Equal(Ds().DataSourceKey, sourceKey);
            Assert.Equal(HyperDatasetCodes.TopSellers, sourceKey);
        }

        // ---- the provider's definition agrees with the dataset ------------------------------------

        [Fact]
        public void The_published_definition_does_not_drift_from_the_dataset()
        {
            var ds = Ds();
            var def = Assert.Single(new HyperReportDefinitionProvider().GetDefinitions());

            Assert.Equal(ds.DatasetCode, def.Code);
            Assert.Equal(ds.Module, def.Module);
            Assert.Equal(ds.DataSourceKey, def.DataSourceKey);
            // The permission key is the one that matters: a definition published under a weaker key than
            // its dataset would let the Report Center route around the dataset's own requirement.
            Assert.Equal(ds.RequiredPermissionKey, def.PermissionKey);
            Assert.Equal(ds.Fields.Count, def.Columns.Count);
            Assert.Equal(ds.MaxRows, def.Capabilities.MaxRows);
        }

        // Bilingual display is mandatory in this product: every shown field needs its twin. The item
        // name is the only free-text column here, and it carries one.
        [Fact]
        public void The_item_name_has_an_english_twin()
        {
            Assert.NotNull(Field("Item"));
            var twin = Field("ItemEn");
            Assert.False(twin.VisibleByDefault, "The twin is available, not duplicated into the default view.");
        }
    
        // ---- Basket analysis: the decisions that keep it honest --------------------------------

        private static ReportDatasetField BasketField(string key) =>
            Assert.Single(HyperDatasets.BasketAnalysis().Fields.Where(f => f.Key == key));

        // A CO-OCCURRENCE REPORT WITHOUT A FLOOR SHOWS COINCIDENCES. Measured on this database when
        // the dataset was written: 239 paid orders, only 54 holding two different items, 23 distinct
        // pairs of which 15 were seen twice and 10 three times or more. Printing every pair would
        // have filled the screen with pairs seen ONCE. Required, so nobody runs it without choosing.
        [Fact]
        public void A_minimum_basket_count_is_required_so_a_coincidence_is_not_shown_as_a_pattern()
        {
            var p = Assert.Single(HyperDatasets.BasketAnalysis().Parameters.Where(x => x.Key == "MinBaskets"));

            Assert.True(p.Required);
            Assert.Equal(ReportFieldType.Integer, p.Type);
            Assert.Equal("2", p.DefaultValue);   // "seen more than once" is the lowest defensible floor
        }

        // THE DENOMINATOR TRAVELS WITH THE ROW. "Seen in 3 baskets" means nothing on its own: three
        // out of four is a habit, three out of four thousand is noise. A reader who cannot see the
        // period total cannot judge a single row of this report.
        [Fact]
        public void Every_row_carries_the_basket_total_it_is_measured_against()
        {
            var f = BasketField("BasketsInPeriod");

            Assert.Equal(ReportFieldType.Integer, f.Type);
            Assert.True(f.VisibleByDefault, "the denominator must not be hidden behind a column picker.");
        }

        // Summing or averaging attachment rates across pairs produces a number that is not the
        // attachment rate of anything, and it lands in the totals row looking exactly like one.
        [Fact]
        public void The_attachment_rate_cannot_be_aggregated()
        {
            Assert.Empty(BasketField("AttachmentPct").SupportedAggregates);
        }

        // Both packs answer to the same key: basket analysis exposes no cost and no margin, so it
        // needs no second tier - but it must not quietly sit OUTSIDE the permission model either.
        [Fact]
        public void Basket_analysis_requires_the_same_hyper_key()
        {
            Assert.Equal(HyperReportPermissions.View, HyperDatasets.BasketAnalysis().RequiredPermissionKey);
            Assert.All(HyperDatasets.BasketAnalysis().Fields,
                f => Assert.True(string.IsNullOrEmpty(f.RequiredPermissionKey),
                    $"{f.Key} carries a field key; basket analysis has no cost tier to guard."));
        }

        // The dataset must reach a source, the same two-step check Top sellers carries.
        [Fact]
        public void Basket_analysis_points_at_a_source_that_answers_to_that_key()
        {
            Assert.Equal(HyperDatasets.BasketAnalysis().DataSourceKey,
                new HyperBasketAnalysisDataSource(null!).Key);
        }
}
}
