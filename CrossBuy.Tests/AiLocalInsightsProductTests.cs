using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel.Ai;
using Xunit;

namespace CrossBuy.Tests
{
    // LOCAL AI PRODUCTIZATION — the AI Insights screen's honesty rules.
    //
    // THE DEFECT THIS FILE EXISTS FOR. The page carried ONE `ServiceDown` boolean for three independent
    // models, and rendered "No anomalies found" whenever the anomaly list was empty. Those two facts
    // combined into the worst thing an insights screen can do:
    //
    //     "we examined 500 entries and found nothing wrong"   -> reassuring, and true
    //     "we examined nothing, so we found nothing"          -> says NOTHING about your books
    //
    // Both printed the same sentence. A clean bill of health that is really an absence of data is not a
    // cosmetic problem; it is a reader drawing a conclusion the system never supported. InsufficientData
    // is therefore its own state, and these tests are what stop it collapsing back into "Ok".
    //
    // NO TEST HERE CONTACTS ANY AI SERVICE — local or external. The mapper is a pure function and the
    // provider assertions are structural.
    public class AiLocalInsightsProductTests
    {
        private const int Company = 7;
        private static readonly DateTime Now = new(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc);

        // =========================================================================================
        // The four states
        // =========================================================================================

        [Fact]
        public void A_result_over_real_records_is_Ok_and_carries_its_provenance()
        {
            var p = AiInsightMapper.Map(status: 200, recordsConsidered: 412, Company, Now);

            Assert.Equal(AiInsightState.Ok, p.State);
            Assert.True(p.HasResult);
            Assert.Equal(412, p.RecordsConsidered);
            Assert.Equal(Company, p.CompanyId);
            Assert.Equal(AiInsightSources.LocalMl, p.Source);
            Assert.Equal(Now, p.GeneratedAtUtc);
            Assert.Null(p.Detail);
        }

        // THE HEADLINE TEST. Zero records examined must never read as a clean result.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void A_model_that_examined_nothing_is_InsufficientData_and_never_Ok(int considered)
        {
            var p = AiInsightMapper.Map(200, considered, Company, Now);

            Assert.Equal(AiInsightState.InsufficientData, p.State);
            Assert.False(p.HasResult);

            // No timestamp: dating an absence would present it as an answer.
            Assert.Null(p.GeneratedAtUtc);
        }

        [Theory]
        [InlineData(500)]   // upstream error
        [InlineData(502)]   // gateway / service not running
        [InlineData(403)]   // the egress boundary refused
        [InlineData(0)]     // no response at all
        public void A_non_200_response_is_Unavailable_not_a_verdict(int status)
        {
            var p = AiInsightMapper.Map(status, recordsConsidered: null, Company, Now);

            Assert.Equal(AiInsightState.Unavailable, p.State);
            Assert.False(p.HasResult);
            Assert.Null(p.GeneratedAtUtc);
        }

        // A 200 whose body could not be understood is NOT the same as a service being down: an operator
        // can restart a stopped service and can do nothing about a malformed payload.
        [Fact]
        public void A_200_with_an_unreadable_payload_is_Failed_and_distinct_from_Unavailable()
        {
            var failed = AiInsightMapper.Map(200, recordsConsidered: null, Company, Now);

            Assert.Equal(AiInsightState.Failed, failed.State);
            Assert.NotEqual(AiInsightState.Unavailable, failed.State);
            Assert.False(failed.HasResult);
        }

        // =========================================================================================
        // No internals on a user's screen
        // =========================================================================================

        // The previous implementation assigned the raw upstream JSON and, on the catch path,
        // `ex.Message` straight into the view model. Both are service internals, and the JSON is built
        // from the company's own ledger rows.
        [Fact]
        public void No_panel_detail_can_carry_an_upstream_payload_or_an_exception_message()
        {
            var details = new[]
            {
                AiInsightMapper.Map(500, null, Company, Now).Detail,
                AiInsightMapper.Map(200, null, Company, Now).Detail,
                AiInsightMapper.Map(200, 0, Company, Now).Detail,
                AiInsightMapper.Unavailable("ai-insight:service-timeout", Company, Now).Detail,
            };

            foreach (var d in details)
            {
                Assert.NotNull(d);

                // A stable machine token, not prose and not a payload: no JSON, no stack frame, no path.
                Assert.StartsWith("ai-insight:", d!, StringComparison.Ordinal);
                Assert.DoesNotContain("{", d, StringComparison.Ordinal);
                Assert.DoesNotContain("Exception", d, StringComparison.Ordinal);
                Assert.DoesNotContain("\\", d, StringComparison.Ordinal);
                Assert.True(d.Length <= 64, "a deny detail should be a token, not a narrative");
            }
        }

        [Fact]
        public void The_controller_never_renders_a_service_payload_or_an_exception_into_the_page()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));
            var action = Between(src, "public async Task<IActionResult> AiInsights()", "// ===== segregation of duties helpers");

            // CODE ONLY. The comments in that region deliberately describe the leak that was removed,
            // and a scan that could not tell prose from code would forbid documenting the defect.
            var code = StripComments(action);

            // The two exact assignments that used to leak.
            Assert.DoesNotContain("ServiceMessage", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ex.Message", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Detail = response", code, StringComparison.Ordinal);

            // The payload is parsed, never assigned to anything the view can render.
            Assert.Contains("JsonSerializer.Deserialize<T>(response.Json", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // The page states are all reachable and rendered
        // =========================================================================================

        [Fact]
        public void The_view_renders_every_state_and_states_the_data_scope_and_source()
        {
            var view = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Accounting", "AiInsights.cshtml"));

            foreach (var required in new[]
                     {
                         "AiInsightState.InsufficientData",   // the state that must not be silent
                         "Data scope",                        // which company
                         "Analysis source",                   // which engine
                         "Generated",                         // when
                         "No data leaves this installation",  // local-only, stated to the reader
                     })
            {
                Assert.Contains(required, view, StringComparison.Ordinal);
            }

            // The leaking render is gone.
            Assert.DoesNotContain("Model.ServiceMessage", view, StringComparison.Ordinal);
        }

        // A failing capability must not blank the two that worked.
        [Fact]
        public void One_failing_capability_does_not_hide_the_others()
        {
            var vm = new AiInsightsVm
            {
                CompanyId = Company,
                AnomalyPanel = AiInsightMapper.Map(200, 120, Company, Now),      // Ok
                CashflowPanel = AiInsightMapper.Map(503, null, Company, Now),    // Unavailable
                InventoryPanel = AiInsightMapper.Map(200, 0, Company, Now),      // InsufficientData
            };

            Assert.True(vm.AnomalyPanel.HasResult);
            Assert.False(vm.CashflowPanel.HasResult);
            Assert.False(vm.InventoryPanel.HasResult);

            // The whole-page empty state is reserved for "nothing at all worked".
            Assert.False(vm.AllUnavailable);
        }

        [Fact]
        public void The_page_empty_state_appears_only_when_nothing_produced_a_result()
        {
            var allDown = new AiInsightsVm
            {
                AnomalyPanel = AiInsightMapper.Map(503, null, Company, Now),
                CashflowPanel = AiInsightMapper.Map(503, null, Company, Now),
                InventoryPanel = AiInsightMapper.Map(503, null, Company, Now),
            };
            Assert.True(allDown.AllUnavailable);

            // Insufficient data is still "not a result", so it also counts toward the empty state — but
            // it is labelled differently on the page, which is the point of keeping the states apart.
            var noData = new AiInsightsVm
            {
                AnomalyPanel = AiInsightMapper.Map(200, 0, Company, Now),
                CashflowPanel = AiInsightMapper.Map(200, 0, Company, Now),
                InventoryPanel = AiInsightMapper.Map(200, 0, Company, Now),
            };
            Assert.True(noData.AllUnavailable);
            Assert.All(new[] { noData.AnomalyPanel, noData.CashflowPanel, noData.InventoryPanel },
                p => Assert.Equal(AiInsightState.InsufficientData, p.State));
        }

        // =========================================================================================
        // Local-only: the external provider is not on this path at all
        // =========================================================================================

        [Fact]
        public void The_insights_page_reaches_no_external_provider()
        {
            foreach (var file in new[]
                     {
                         Path.Combine("CrossBuy", "ViewModel", "Ai", "AiInsightsModels.cs"),
                         Path.Combine("CrossBuy", "Views", "Accounting", "AiInsights.cshtml"),
                         Path.Combine("CrossBuy", "BL", "AiInsightsService.cs"),
                     })
            {
                var src = File.ReadAllText(Path.Combine(RepoRoot(), file));

                Assert.DoesNotContain("api.openai.com", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAiProviderAdapter", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OPENAI_API_KEY", src, StringComparison.Ordinal);
            }
        }

        // The source constant is what the page shows the reader. If a future change routed this screen
        // through a hosted model, this test is what makes that a deliberate edit rather than a drift.
        [Fact]
        public void The_declared_analysis_source_is_the_on_premises_model()
            => Assert.Equal("local-ml", AiInsightSources.LocalMl);

        // Every insight still leaves through the ONE governed boundary, with a purpose and a
        // classification. This is the property that keeps the screen inside AI governance rather than
        // beside it.
        [Fact]
        public void Every_insight_call_still_passes_through_the_egress_policy()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "AiInsightsService.cs"));

            Assert.Contains("IAiEgressPolicy", src, StringComparison.Ordinal);
            Assert.Contains("_egress.EvaluateAsync", src, StringComparison.Ordinal);

            // The adapter is only ever handed a token the policy minted.
            Assert.Contains("decision.Approval", src, StringComparison.Ordinal);
            Assert.Contains("if (!decision.Allowed", src, StringComparison.Ordinal);
        }

        // The insights page must not become a second, ungoverned reader of module tables. The controller
        // action asks the service; it does not query.
        [Fact]
        public void The_controller_action_reads_no_module_table_directly()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));
            var action = Between(src, "public async Task<IActionResult> AiInsights()", "// ===== segregation of duties helpers");

            var code = StripComments(action);
            foreach (var table in new[]
                     {
                         "_context.JournalEntries", "_context.Items", "_context.Customers",
                         "_context.Vendors", "_context.StockBalances", "_context.SalesInvoices",
                     })
            {
                Assert.DoesNotContain(table, code, StringComparison.Ordinal);
            }
        }

        // =========================================================================================
        // Graceful degradation: a missing local service must not take the application with it
        // =========================================================================================

        [Fact]
        public void A_missing_local_service_is_handled_and_never_thrown_to_the_user()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));

            // The three ways an absent or slow local service actually surfaces.
            Assert.Contains("catch (HttpRequestException)", src, StringComparison.Ordinal);
            Assert.Contains("catch (TaskCanceledException)", src, StringComparison.Ordinal);
            Assert.Contains("catch (JsonException)", src, StringComparison.Ordinal);
        }

        [Fact]
        public void The_mapper_is_pure_and_holds_no_service_or_context()
        {
            var t = typeof(AiInsightMapper);
            Assert.True(t.IsAbstract && t.IsSealed, "the mapper is static, so it holds no state");

            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            {
                var n = f.FieldType.Name;
                Assert.False(n.Contains("DbContext", StringComparison.Ordinal), $"{f.Name} is a {n}");
                Assert.False(n.Contains("HttpClient", StringComparison.Ordinal), $"{f.Name} is a {n}");
            }

            // The clock is a parameter, not a call: the same inputs always give the same panel.
            var map = t.GetMethod(nameof(AiInsightMapper.Map))!;
            Assert.Contains(map.GetParameters(), p => p.ParameterType == typeof(DateTime));
        }

        // =========================================================================================
        // Company scope travels with the result
        // =========================================================================================

        [Fact]
        public void A_panel_is_stamped_with_the_company_it_was_scoped_to()
        {
            var a = AiInsightMapper.Map(200, 5, companyId: 1, Now);
            var b = AiInsightMapper.Map(200, 5, companyId: 65, Now);

            Assert.Equal(1, a.CompanyId);
            Assert.Equal(65, b.CompanyId);
            Assert.NotEqual(a.CompanyId, b.CompanyId);
        }

        // The action takes no company parameter and resolves one from the trusted source. Re-asserted
        // here beside the new code, so a future edit that adds a caller-supplied company trips over it.
        [Fact]
        public void The_action_still_resolves_its_company_and_accepts_none_from_the_caller()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));
            var action = Between(src, "public async Task<IActionResult> AiInsights()", "// ===== segregation of duties helpers");

            Assert.Contains("_company.ResolveAsync()", action, StringComparison.Ordinal);
            Assert.Contains("if (!scope.Ok)", action, StringComparison.Ordinal);
            Assert.Contains("scope.CompanyId", action, StringComparison.Ordinal);
            Assert.DoesNotContain("AiInsights(int", src, StringComparison.Ordinal);
        }

        // =========================================================================================
        // helpers
        // =========================================================================================

        /// <summary>Removes block and line comments so a source scan measures code, not prose.</summary>
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
        }

        private static string Between(string src, string start, string end)
        {
            var a = src.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, $"anchor not found: {start}");
            var b = src.IndexOf(end, a, StringComparison.Ordinal);
            return b < 0 ? src[a..] : src[a..b];
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
