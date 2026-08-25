using System.Reflection;
using CrossBuy.ViewModel.Ai;
using Xunit;

namespace CrossBuy.Tests
{
    // INVENTORY AI RISK INSIGHTS — product expansion wave 1.
    //
    // WHAT THIS SCREEN MAY AND MAY NOT CLAIM. crossbuy_ai/app/ml/inventory.py is deterministic rules
    // plus simple statistics over on-hand quantity, value and outbound demand. It classifies four
    // situations — slow/dead, out-of-stock-with-demand, below-reorder-point, low-days-of-cover — and
    // computes average daily usage, days-of-cover and a suggested reorder quantity.
    //
    // IT IS NOT A FORECASTER. It predicts no future demand, detects no anomalous individual movement,
    // and produces no confidence interval. Several tests below exist purely to stop the screen from
    // growing a claim the model cannot support, because that is the failure mode nobody notices: a
    // plausible sentence attributed to a model that never said it.
    //
    // NO TEST HERE CONTACTS ANY AI SERVICE, local or external.
    public class InventoryRiskInsightsTests
    {
        private const int Company = 7;
        private static readonly DateTime Now = new(2026, 8, 24, 11, 0, 0, DateTimeKind.Utc);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Controller() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "InventoryController.cs"));

        private static string View() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Inventory", "RiskInsights.cshtml"));

        private static string Action()
        {
            var src = Controller();
            var a = src.IndexOf("public async Task<IActionResult> RiskInsights()", StringComparison.Ordinal);
            Assert.True(a >= 0, "the RiskInsights action must exist");
            var b = src.IndexOf("[HttpGet] public async Task<IActionResult> StockMovements", a, StringComparison.Ordinal);
            return b < 0 ? src[a..] : src[a..b];
        }

        /// <summary>Strips comments so a source scan measures code, not prose.</summary>
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
        }

        private static InventoryItem Item(int id, string cls, decimal onHand, decimal value, string sev = "high") => new()
        {
            ItemId = id,
            Code = $"ITM-{id}",
            Name = $"Item {id}",
            OnHand = onHand,
            Value = value,
            Class = cls,
            Severity = sev,
            Reasons = new List<AiReason> { new() { En = "Below reorder point", Ar = "تحت نقطة إعادة الطلب" } },
        };

        // =========================================================================================
        // A real result
        // =========================================================================================

        [Fact]
        public void A_real_result_reports_its_flags_grouped_by_class()
        {
            var vm = new InventoryRiskVm
            {
                CompanyId = Company,
                WindowDays = 90,
                Result = new InventoryResult
                {
                    ItemsAnalyzed = 900,
                    FlaggedCount = 4,
                    Summary = new InventorySummary { SlowMoving = 1, Reorder = 2, StockoutRisk = 1, DeadStockValue = 1234.50m },
                    Flagged = new List<InventoryItem>
                    {
                        Item(1, "stockout", 0m, 0m),
                        Item(2, "reorder", 3m, 90m),
                        Item(3, "reorder", 1m, 40m),
                        Item(4, "slow", 500m, 1234.50m, "low"),
                    },
                },
                Panel = AiInsightMapper.Map(200, 900, Company, Now),
            };

            Assert.True(vm.Panel.HasResult);
            Assert.Equal(900, vm.Panel.RecordsConsidered);
            Assert.Equal(4, vm.Flagged.Count);
            Assert.Single(vm.OfClass("stockout"));
            Assert.Equal(2, vm.OfClass("reorder").Count());
            Assert.Single(vm.OfClass("slow"));
            Assert.Equal(90, vm.WindowDays);
            Assert.Equal(Company, vm.CompanyId);
            Assert.Equal(Now, vm.Panel.GeneratedAtUtc);
        }

        // A clean result is only clean if something was examined. This is the pairing the screen must
        // keep straight, and the reason ItemsAnalyzed (not FlaggedCount) drives the state.
        [Fact]
        public void Zero_flags_over_real_items_is_a_genuine_clean_result()
        {
            var vm = new InventoryRiskVm
            {
                Result = new InventoryResult { ItemsAnalyzed = 900, FlaggedCount = 0 },
                Panel = AiInsightMapper.Map(200, 900, Company, Now),
            };

            Assert.True(vm.Panel.HasResult);
            Assert.Empty(vm.Flagged);
            Assert.Equal(900, vm.Panel.RecordsConsidered);
        }

        // =========================================================================================
        // Insufficient data — the state that must never read as "all good"
        // =========================================================================================

        [Fact]
        public void Zero_items_analysed_is_InsufficientData_and_never_a_clean_bill_of_health()
        {
            var vm = new InventoryRiskVm
            {
                Result = new InventoryResult { ItemsAnalyzed = 0, FlaggedCount = 0 },
                Panel = AiInsightMapper.Map(200, 0, Company, Now),
            };

            Assert.Equal(AiInsightState.InsufficientData, vm.Panel.State);
            Assert.False(vm.Panel.HasResult);
            Assert.Null(vm.Panel.GeneratedAtUtc);
        }

        // The state is decided by ITEMS ANALYSED, not by the flag count. If this ever flipped to
        // FlaggedCount, "0 flags" would render as InsufficientData for a healthy 900-item catalogue and
        // as a clean result for an empty one — both backwards.
        [Fact]
        public void The_state_is_driven_by_items_analysed_not_by_the_flag_count()
        {
            var code = StripComments(Action());

            Assert.Contains("parsed.ItemsAnalyzed", code, StringComparison.Ordinal);
            Assert.DoesNotContain("parsed.FlaggedCount", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // Unavailable and Failed
        // =========================================================================================

        [Theory]
        [InlineData(503)]
        [InlineData(500)]
        [InlineData(403)]   // the egress boundary refused
        public void An_unreachable_or_refused_model_is_Unavailable(int status)
        {
            var vm = new InventoryRiskVm { Panel = AiInsightMapper.Map(status, null, Company, Now) };

            Assert.Equal(AiInsightState.Unavailable, vm.Panel.State);
            Assert.False(vm.Panel.HasResult);
            Assert.Empty(vm.Flagged);       // no result means no rows, not an empty "all clear" table
        }

        [Fact]
        public void An_unreadable_response_is_Failed_and_distinct_from_Unavailable()
        {
            var failed = AiInsightMapper.Failed("ai-insight:unreadable-response", Company, Now);

            Assert.Equal(AiInsightState.Failed, failed.State);
            Assert.NotEqual(AiInsightState.Unavailable, failed.State);
            Assert.False(failed.HasResult);
        }

        [Fact]
        public void The_action_handles_a_missing_model_without_taking_inventory_down()
        {
            var src = Controller();

            Assert.Contains("catch (HttpRequestException)", src, StringComparison.Ordinal);
            Assert.Contains("catch (TaskCanceledException)", src, StringComparison.Ordinal);
            Assert.Contains("catch (JsonException)", src, StringComparison.Ordinal);
        }

        [Fact]
        public void The_view_renders_all_four_states_explicitly()
        {
            var view = View();

            Assert.Contains("AiInsightState.InsufficientData", view, StringComparison.Ordinal);
            Assert.Contains("AiInsightState.Unavailable", view, StringComparison.Ordinal);
            Assert.Contains("Panel.HasResult", view, StringComparison.Ordinal);
            Assert.Contains("No inventory risks were flagged", view, StringComparison.Ordinal);
            Assert.Contains("No stocked items were available to analyse", view, StringComparison.Ordinal);
        }

        // =========================================================================================
        // Company isolation — server-derived only
        // =========================================================================================

        [Fact]
        public void The_action_accepts_no_company_from_the_caller_and_resolves_one()
        {
            var src = Controller();
            var code = StripComments(Action());

            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
            Assert.Contains("if (!scope.Ok)", code, StringComparison.Ordinal);
            Assert.Contains("scope.CompanyId", code, StringComparison.Ordinal);

            // No parameters at all — there is nothing a caller could offer to be validated or coerced.
            Assert.Contains("RiskInsights()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("RiskInsights(int", src, StringComparison.Ordinal);
            Assert.DoesNotContain("RiskInsights(int? companyId", src, StringComparison.Ordinal);
        }

        // The rest of this controller still uses a DefaultCompanyId constant. This screen must not
        // inherit that: it is the one action here whose company is resolved.
        [Fact]
        public void The_action_does_not_use_the_controllers_hardcoded_default_company()
        {
            var code = StripComments(Action());
            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_resolved_company_travels_onto_the_panel_and_the_page()
        {
            var a = AiInsightMapper.Map(200, 10, companyId: 1, Now);
            var b = AiInsightMapper.Map(200, 10, companyId: 65, Now);

            Assert.Equal(1, a.CompanyId);
            Assert.Equal(65, b.CompanyId);

            var code = StripComments(Action());
            Assert.Contains("CompanyId = scope.CompanyId", code, StringComparison.Ordinal);

            // and the page states the scope it is showing
            Assert.Contains("Model.CompanyId", View(), StringComparison.Ordinal);
        }

        // The refusal path must not name another company — "your company is 2, the record is 1" tells a
        // caller a record exists where they cannot see it.
        [Fact]
        public void The_refusal_does_not_disclose_the_resolver_reason()
        {
            var code = StripComments(Action());

            Assert.DoesNotContain("scope.Reason", code, StringComparison.Ordinal);
            Assert.Contains("RedirectToAction(nameof(Index))", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // Navigation only — no mutation
        // =========================================================================================

        [Fact]
        public void Each_flagged_row_links_to_the_item_and_its_stock_movements()
        {
            var view = View();

            Assert.Contains("Url.Action(\"EditItem\",\"Inventory\", new { id = it.ItemId })", view, StringComparison.Ordinal);
            Assert.Contains("Url.Action(\"StockMovements\",\"Inventory\", new { itemId = it.ItemId })", view, StringComparison.Ordinal);
        }

        // Those two targets must actually exist, or the links are dead.
        [Fact]
        public void The_navigation_targets_exist_on_the_controller()
        {
            var src = Controller();

            Assert.Contains("public async Task<IActionResult> EditItem(int id)", src, StringComparison.Ordinal);
            Assert.Contains("StockMovements(int? itemId, int? warehouseId)", src, StringComparison.Ordinal);
        }

        [Fact]
        public void The_screen_is_read_only_and_mutates_no_inventory()
        {
            var code = StripComments(Action());
            var view = View();

            // The action is a GET and posts nothing.
            Assert.Contains("[HttpGet]", StripComments(Controller())[
                ..StripComments(Controller()).IndexOf("public async Task<IActionResult> RiskInsights()", StringComparison.Ordinal)][^40..],
                StringComparison.Ordinal);

            foreach (var writer in new[] { "SaveChangesAsync", "_stock.", "PostMovement", "_approvals.", "Add(", "Update(", "Remove(" })
                Assert.DoesNotContain(writer, code, StringComparison.Ordinal);

            // No form, no POST, no mutating control anywhere on the page.
            Assert.DoesNotContain("<form", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("method=\"post\"", view, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================================================
        // No internals on screen
        // =========================================================================================

        [Fact]
        public void No_raw_service_json_and_no_exception_text_reaches_the_page()
        {
            var code = StripComments(Action());
            var view = View();

            // The payload is parsed and never assigned anywhere the view can read it.
            Assert.Contains("JsonSerializer.Deserialize<InventoryResult>", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ex.Message", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Detail = response", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ServiceMessage", code, StringComparison.Ordinal);

            // The view renders no free-form service text at all.
            Assert.DoesNotContain("Panel.Detail", view, StringComparison.Ordinal);
            Assert.DoesNotContain("response.Json", view, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_panel_detail_is_a_fixed_token()
        {
            foreach (var d in new[]
                     {
                         AiInsightMapper.Map(503, null, Company, Now).Detail,
                         AiInsightMapper.Failed("ai-insight:unreadable-response", Company, Now).Detail,
                         AiInsightMapper.Unavailable("ai-insight:service-timeout", Company, Now).Detail,
                     })
            {
                Assert.NotNull(d);
                Assert.StartsWith("ai-insight:", d!, StringComparison.Ordinal);
                Assert.DoesNotContain("{", d, StringComparison.Ordinal);
                Assert.True(d.Length <= 64);
            }
        }

        // =========================================================================================
        // Local only: zero OpenAI, and no approval state touched
        // =========================================================================================

        [Fact]
        public void The_inventory_risk_path_reaches_no_external_provider()
        {
            foreach (var src in new[] { Controller(), View() })
            {
                Assert.DoesNotContain("api.openai.com", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAiProviderAdapter", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OPENAI_API_KEY", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAiOptions", src, StringComparison.Ordinal);
            }
        }

        // The screen asks the governed service; it never mints or inspects an approval itself, and it
        // cannot: AiEgressApproval's constructor is internal to the product assembly.
        [Fact]
        public void The_action_goes_through_the_governed_insights_service_only()
        {
            var code = StripComments(Action());

            Assert.Contains("_insights.AnalyzeInventoryAsync", code, StringComparison.Ordinal);
            Assert.DoesNotContain("new AiEgressApproval", code, StringComparison.Ordinal);
            Assert.DoesNotContain("IAiEgressPolicy", code, StringComparison.Ordinal);
            Assert.DoesNotContain("_ai.PostAsync", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_shipped_provider_record_still_approves_nobody()
        {
            var authority = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "AiProviderAuthority.cs"));

            Assert.Contains("RecordedCandidate = null", authority, StringComparison.Ordinal);
            Assert.Contains("RecordedApproval = null", authority, StringComparison.Ordinal);
        }

        // =========================================================================================
        // The screen must not overclaim the model
        // =========================================================================================

        // The model is rules plus statistics. The page says so, in the reader's language, rather than
        // letting "AI" imply a prediction.
        [Fact]
        public void The_page_states_that_this_is_not_a_forecast()
        {
            var view = View();

            Assert.Contains("This is not a demand forecast", view, StringComparison.Ordinal);
            Assert.Contains("fixed rules and simple statistics", view, StringComparison.Ordinal);

            // and it shows the window the classification was made over, so it can be judged
            Assert.Contains("Demand window", view, StringComparison.Ordinal);
            Assert.Contains("Model.WindowDays", view, StringComparison.Ordinal);
        }

        // Words the current model cannot support must not appear as claims on the page.
        [Fact]
        public void The_page_claims_no_capability_the_model_does_not_have()
        {
            var view = View();

            // The DISCLAIMER is removed before scanning. It legitimately contains "prediction" because
            // it DENIES one, and a scan that cannot tell a denial from a claim would force the page to
            // stop saying the most important true thing on it.
            const string disclaimer =
                "This is not a demand forecast and carries no prediction of future sales.";
            Assert.Contains(disclaimer, view, StringComparison.Ordinal);
            // Razor comments never render, so they are not claims either. Strip them, then strip the
            // disclaimer, and what remains is text a user can actually read on the page.
            var rendered = System.Text.RegularExpressions.Regex.Replace(
                view, @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            var claims = rendered.Replace(disclaimer, string.Empty, StringComparison.Ordinal);

            foreach (var overclaim in new[]
                     {
                         "predict", "forecast", "confidence interval", "probability",
                         "expected demand", "will run out", "estimated future",
                     })
            {
                Assert.DoesNotContain(overclaim, claims, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Reasons come from the model, in the model's own words, and are only selected by culture.
        [Fact]
        public void Reasons_are_the_models_own_and_are_not_generated_in_the_view()
        {
            var view = View();

            Assert.Contains("it.Reasons", view, StringComparison.Ordinal);
            Assert.Contains("R(r)", view, StringComparison.Ordinal);
        }

        [Fact]
        public void The_view_model_exposes_no_data_source_of_its_own()
        {
            var t = typeof(InventoryRiskVm);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var n = p.PropertyType.Name;
                Assert.False(n.Contains("DbContext", StringComparison.Ordinal), $"{p.Name} is a {n}");
                Assert.False(n.Contains("HttpClient", StringComparison.Ordinal), $"{p.Name} is a {n}");
            }
        }

        // =========================================================================================
        // Discoverability
        // =========================================================================================

        [Fact]
        public void The_inventory_dashboard_links_to_the_risk_screen()
        {
            var index = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Inventory", "Index.cshtml"));
            Assert.Contains("Url.Action(\"RiskInsights\",\"Inventory\")", index, StringComparison.Ordinal);
        }

        [Fact]
        public void The_screen_is_localised_in_all_three_cultures()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Inventory");
            foreach (var lang in new[] { "en", "ar", "fr" })
            {
                var path = Path.Combine(dir, $"RiskInsights.{lang}.resx");
                Assert.True(File.Exists(path), $"missing {lang} resources");
                var doc = System.Xml.Linq.XDocument.Load(path);
                Assert.NotEmpty(doc.Root!.Elements("data"));
            }
        }
    }
}
