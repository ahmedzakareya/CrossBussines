using Xunit;

namespace CrossBuy.Tests
{
    // LEGACY AI PATH — structural guards on the REPAIRED endpoint and its service.
    //
    // The isolation behaviour is proven at query level in LegacyAiInsightsIsolationTests. What these pin
    // is that the repair cannot be quietly undone: the constant must not come back, the trusted resolver
    // must stay, and the refusal must stay ahead of the egress.
    public class LegacyAiInsightsEndpointTests
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

        // The body of the AiInsights action ONLY, extracted by brace matching.
        //
        // An earlier version of this helper searched for the next attribute block instead, and silently
        // over-ran into the FOLLOWING action — which does use DefaultCompanyId, so the guard reported a
        // violation that was not in this method. Brace matching is exact: the body ends where the method
        // ends, whatever follows it.
        private static string AiInsightsBody()
        {
            var src = Read("CrossBuy", "Controllers", "AccountingController.cs");
            int start = src.IndexOf("public async Task<IActionResult> AiInsights()", StringComparison.Ordinal);
            Assert.True(start > 0, "AiInsights action not found — it may have been renamed.");

            int open = src.IndexOf('{', start);
            Assert.True(open > 0, "AiInsights has no body");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
            }
            Assert.Fail("AiInsights body is unbalanced");
            return "";
        }

        // THE core regression guard. If anyone reintroduces the constant on this path, this fails.
        [Fact]
        public void The_ai_insights_action_no_longer_passes_a_hardcoded_company()
        {
            var body = AiInsightsBody();

            Assert.DoesNotContain("DefaultCompanyId", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ScanJournalAnomaliesAsync(1", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ForecastCashflowAsync(1", body, StringComparison.Ordinal);
            Assert.DoesNotContain("AnalyzeInventoryAsync(1", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_ai_insights_action_resolves_its_company_from_the_trusted_resolver()
        {
            var body = AiInsightsBody();

            Assert.Contains("_company.ResolveAsync()", body, StringComparison.Ordinal);
            Assert.Contains("scope.CompanyId", body, StringComparison.Ordinal);
            // Fail closed, and the refusal must come BEFORE the three service calls.
            Assert.Contains("if (!scope.Ok)", body, StringComparison.Ordinal);
            Assert.True(
                body.IndexOf("if (!scope.Ok)", StringComparison.Ordinal)
                    < body.IndexOf("ScanJournalAnomaliesAsync", StringComparison.Ordinal),
                "The company refusal must run BEFORE any insight call, or data is gathered before it is authorized.");
        }

        // The action must not accept a company from the caller. Proven on the SIGNATURE, so the claim does
        // not depend on a validation routine somebody could later bypass.
        [Fact]
        public void The_ai_insights_action_accepts_no_company_parameter()
        {
            var body = AiInsightsBody();
            int sig = body.IndexOf('(');
            int close = body.IndexOf(')', sig);
            var parameters = body[(sig + 1)..close];
            Assert.True(string.IsNullOrWhiteSpace(parameters),
                $"AiInsights must take no parameters so no company can be supplied; found: '{parameters}'");
        }

        // The service-side guard, and its ORDER relative to the first query. A guard that ran after the
        // first read would still let a company-less request touch the database.
        [Fact]
        public void Every_insight_method_guards_its_company_before_touching_data()
        {
            var src = Read("CrossBuy", "BL", "AiInsightsService.cs");

            Assert.Contains("private static void RequireCompany(int companyId)", src, StringComparison.Ordinal);
            Assert.Contains("throw new ArgumentOutOfRangeException", src, StringComparison.Ordinal);

            foreach (var method in new[]
            {
                "ScanJournalAnomaliesAsync(int companyId",
                "ForecastCashflowAsync(int companyId",
                "AnalyzeInventoryAsync(int companyId",
            })
            {
                int start = src.IndexOf(method, StringComparison.Ordinal);
                Assert.True(start > 0, $"{method} not found");

                int guard = src.IndexOf("RequireCompany(companyId);", start, StringComparison.Ordinal);
                int firstQuery = src.IndexOf("_context.", start, StringComparison.Ordinal);

                // Increment 3 routed every outbound call through SendAsync, which evaluates the central
                // egress policy and only then reaches _ai.PostAsync. The egress point to check is
                // therefore SendAsync — checking for _ai.PostAsync alone would now find the shared
                // chokepoint far away in the file and silently stop testing these methods.
                int firstEgress = src.IndexOf("SendAsync(", start, StringComparison.Ordinal);

                Assert.True(guard > 0 && guard < firstQuery,
                    $"{method}: RequireCompany must run before the first query.");
                Assert.True(firstEgress > 0 && guard < firstEgress,
                    $"{method}: RequireCompany must run before anything is sent to the AI service.");
            }
        }

        // ItemWarehouseSetting has no company column, so it is scoped through its item. If someone removes
        // that scoping the query silently reads every company again — exactly the state this repaired.
        [Fact]
        public void Item_warehouse_settings_are_scoped_through_the_company_scoped_item_set()
        {
            var src = Read("CrossBuy", "BL", "AiInsightsService.cs");

            int q = src.IndexOf("_context.ItemWarehouseSettings", StringComparison.Ordinal);
            Assert.True(q > 0, "the ItemWarehouseSettings query was not found");

            var window = src[q..Math.Min(src.Length, q + 400)];
            Assert.Contains("itemIds.Contains(s.ItemId)", window, StringComparison.Ordinal);
        }

        // Every source in this service must carry an explicit company intent — defence in depth, kept even
        // for entities that already have a Stage-1 global filter.
        [Fact]
        public void Every_company_bearing_source_keeps_an_explicit_company_predicate()
        {
            var src = Read("CrossBuy", "BL", "AiInsightsService.cs");

            foreach (var (dbSet, predicate) in new[]
            {
                ("_context.JournalEntries", "e.CompanyID == companyId"),
                ("_context.Accounts", "a.CompanyID == companyId"),
                ("_context.Customers", "c.CompanyID == companyId"),
                ("_context.SalesInvoices", "i.CompanyID == companyId"),
                ("_context.Receipts", "r.CompanyID == companyId"),
                ("_context.Vendors", "v.CompanyID == companyId"),
                ("_context.PurchaseInvoices", "i.CompanyID == companyId"),
                ("_context.Payments", "p.CompanyID == companyId"),
                ("_context.Items", "i.CompanyID == companyId"),
                ("_context.StockBalances", "b.CompanyID == companyId"),
                ("_context.StockMovements", "m.CompanyID == companyId"),
            })
            {
                Assert.True(src.Contains(dbSet, StringComparison.Ordinal),
                    $"{dbSet} is no longer queried — update this guard deliberately.");
                Assert.True(src.Contains(predicate, StringComparison.Ordinal),
                    $"{dbSet} lost its explicit company predicate ('{predicate}').");
            }
        }

        // No AI-reachable code may reintroduce a company constant.
        [Fact]
        public void The_insights_service_hardcodes_no_company()
        {
            var src = Read("CrossBuy", "BL", "AiInsightsService.cs");
            Assert.DoesNotContain("CompanyID == 1", src, StringComparison.Ordinal);
            Assert.DoesNotContain("companyId = 1", src, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultCompanyId", src, StringComparison.Ordinal);
        }

        // §12 — the legacy pull path is repaired, NOT migrated. Stating it as a test keeps the report
        // honest: nothing here claims the Python path now uses the projection boundary.
        [Fact]
        public void The_legacy_path_is_repaired_but_still_independent_of_the_new_ai_boundary()
        {
            var src = Read("CrossBuy", "BL", "AiInsightsService.cs");
            Assert.DoesNotContain("IAiProjectionReader", src, StringComparison.Ordinal);
            Assert.DoesNotContain("AiProjection", src, StringComparison.Ordinal);

            // ...and the new boundary still shares nothing with it.
            foreach (var file in new[] { "AiProjectionReader.cs", "AiProjectionConsumer.cs" })
            {
                var ai = Read("CrossBuy", "BL", "Platform", "Ai", file);
                Assert.DoesNotContain("IAiInsightsService", ai, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiService", ai, StringComparison.Ordinal);
            }
        }
    }
}
