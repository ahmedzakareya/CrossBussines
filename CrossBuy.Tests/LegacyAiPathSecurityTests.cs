using CrossBuy.BL.Platform.Ai;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 2 — a RATCHET around the LEGACY (pull) AI path.
    //
    // ============================================================================================
    // THE FINDING, stated plainly so nobody has to re-derive it.
    //
    // CrossBuy has TWO AI paths, and only one of them is governed by the Increment 1/2 boundary:
    //
    //   NEW (push):  BusinessEvent -> grant -> AiProjectionConsumer -> AiProjections -> AiProjectionReader
    //                Company comes from the event, then from the authenticated user. Re-authorized per read.
    //
    //   LEGACY (pull): AccountingController.AiInsights -> IAiInsightsService -> external Python service.
    //                Company comes from `private const int DefaultCompanyId = 1`.
    //
    // AccountingController.AiInsights is [SessionValidation] + [HttpGet] — authenticated and reachable —
    // and passes DefaultCompanyId (= 1) to all three insight calls.
    //
    // WHY THAT IS A REAL EXPOSURE AND NOT MERELY A FUNCTIONAL BREAK. AiInsightsService reads thirteen
    // tables. Six are Stage-1 pilot entities carrying a global company filter, so for a company-2 caller
    // the filter (CompanyID = 2) and the literal predicate (CompanyID = 1) intersect to ZERO rows — a
    // broken screen, not a leak. But SEVEN are NOT filtered:
    //
    //     StockBalance, StockMovement, ItemWarehouseSetting, Account, Payment, Receipt, Vendor
    //
    // For those, `WHERE CompanyID = 1` is the ONLY company predicate, so an authenticated user of ANY
    // company receives COMPANY 1's stock balances, stock movements, reorder settings, accounts, payments,
    // receipts and vendors — and that data is then forwarded to an external service.
    //
    // This test does NOT fix that. Fixing it is controller remediation (CORRECTION-005), which this
    // increment is explicitly forbidden from broadening into, and it belongs to that wave's owner. What
    // this test does is make the debt MONOTONIC and impossible to forget, and pin the two facts that
    // matter: the legacy path does NOT use the new secure boundary, and the new boundary does not
    // inherit its defect.
    // ============================================================================================
    public class LegacyAiPathSecurityTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        // REMEDIATED. This test previously PINNED the defect — it asserted that AiInsights still passed
        // DefaultCompanyId, precisely so a fix could not happen silently without the report being updated.
        // It fired when the fix landed, which is the ratchet working as designed, and it now asserts the
        // repaired state instead.
        //
        // The constant itself still EXISTS in AccountingController for ~190 other, unrelated actions, so
        // the controller correctly remains on the CORRECTION-005 12-controller ratchet. What changed is
        // that the AI path no longer reaches it. Detailed guards live in LegacyAiInsightsEndpointTests.
        [Fact]
        public void The_legacy_ai_insights_endpoint_no_longer_resolves_its_company_from_a_constant()
        {
            var src = Read("CrossBuy", "Controllers", "AccountingController.cs");

            Assert.DoesNotContain("ScanJournalAnomaliesAsync(DefaultCompanyId)", src, StringComparison.Ordinal);
            Assert.DoesNotContain("ForecastCashflowAsync(DefaultCompanyId", src, StringComparison.Ordinal);
            Assert.DoesNotContain("AnalyzeInventoryAsync(DefaultCompanyId", src, StringComparison.Ordinal);

            // The constant remains for the rest of the controller — this increment was bounded to the AI
            // security path and did NOT expand into the full CORRECTION-005 wave.
            Assert.Contains("const int DefaultCompanyId = 1", src, StringComparison.Ordinal);
        }

        // The tables that make it an exposure rather than an empty screen. If a future wave adds company
        // filters for these, this test fails and the severity in the report must be re-assessed — which
        // is the point: the classification must not go stale.
        [Fact]
        public void The_unfiltered_tables_that_make_the_legacy_path_leak_are_still_unfiltered()
        {
            var filters = Read("CrossBuy", "BL", "Platform", "CompanyQueryFilters.cs");

            foreach (var entity in new[] { "StockBalance", "StockMovement", "ItemWarehouseSetting",
                                           "Account", "Payment", "Receipt", "Vendor" })
                Assert.DoesNotContain($"Entity<{entity}>()", filters, StringComparison.Ordinal);

            // Control: the filtered ones really are filtered, so this test is measuring something.
            foreach (var entity in new[] { "JournalEntry", "SalesInvoice", "Item", "Customer" })
                Assert.Contains($"Entity<{entity}>()", filters, StringComparison.Ordinal);
        }

        // The honest boundary statement: the legacy path does NOT route through the new secure retrieval
        // layer, and this increment does not pretend otherwise.
        [Fact]
        public void The_legacy_pull_path_does_not_use_the_new_secure_retrieval_boundary()
        {
            var insights = Read("CrossBuy", "BL", "AiInsightsService.cs");

            Assert.DoesNotContain("IAiProjectionReader", insights, StringComparison.Ordinal);
            Assert.DoesNotContain("AiRetrievalRequest", insights, StringComparison.Ordinal);
            Assert.DoesNotContain("AiProjection", insights, StringComparison.Ordinal);
        }

        // ...and the converse, which is the part that keeps THIS increment safe: the new boundary shares
        // nothing with the legacy path. No constant, no proxy, no external service.
        [Fact]
        public void The_new_retrieval_boundary_shares_nothing_with_the_legacy_path()
        {
            foreach (var file in new[] { "AiProjectionReader.cs", "AiProjectionRevocationService.cs",
                                         "AiProjectionConsumer.cs", "AiProjectionShapes.cs" })
            {
                var src = Read("CrossBuy", "BL", "Platform", "Ai", file);
                Assert.DoesNotContain("DefaultCompanyId", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiService", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiInsightsService", src, StringComparison.Ordinal);
                Assert.DoesNotContain("crossbuy_ai", src, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("HttpClient", src, StringComparison.Ordinal);
            }
        }

        // No AI file anywhere may hardcode company 1 as a data source.
        [Fact]
        public void No_new_ai_file_resolves_a_company_from_a_constant()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "BL", "Platform", "Ai");
            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var src = File.ReadAllText(file);
                Assert.DoesNotContain("const int DefaultCompanyId", src, StringComparison.Ordinal);
                Assert.DoesNotContain("CompanyId = 1;", src, StringComparison.Ordinal);
                Assert.DoesNotContain("CompanyID = 1;", src, StringComparison.Ordinal);
            }
        }

        // The shape registry and the builders must agree on the approved field list. They are declared
        // separately on purpose (write-time vs read-time, and a shape can be retired for reading), so a
        // test — not a comment — is what stops them drifting.
        [Fact]
        public void Every_retrievable_shape_has_a_builder_and_a_grant()
        {
            var shapes = new AiProjectionShapeRegistry();
            var grants = new AiConsumerGrants();
            var builders = new IAiProjectionBuilder[]
            {
                new TaskLifecycleProjectionBuilder(), new CalendarSchedulingProjectionBuilder(),
            };

            foreach (var shape in shapes.All)
            {
                Assert.True(
                    builders.Any(b => b.ProjectionType == shape.ProjectionType && b.ProjectionVersion == shape.Version),
                    $"Retrievable shape '{shape.ProjectionType}' v{shape.Version} has no builder.");
                Assert.True(
                    grants.All.Any(g => g.ProjectionType == shape.ProjectionType && g.ProjectionVersion == shape.Version),
                    $"Retrievable shape '{shape.ProjectionType}' v{shape.Version} has no grant.");
            }
        }
    }
}
