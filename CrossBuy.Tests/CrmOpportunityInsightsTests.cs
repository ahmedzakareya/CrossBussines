using System.Xml.Linq;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.ViewModel.Ai;
using Xunit;
using Rules = CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules;

namespace CrossBuy.Tests
{
    // CRM OPPORTUNITY INSIGHTS — rules, honesty, isolation.
    //
    // THE CENTRAL CLAIM THESE TESTS DEFEND. There is no CRM model in this product. crossbuy_ai ships
    // journal-anomaly, cashflow and inventory models; none of them takes an opportunity, a lead or an
    // account. Nothing anywhere computes a win probability, a churn probability, a revenue forecast, a
    // confidence score or a predicted close date.
    //
    // So this screen is deterministic business rules, and it must keep saying so. An "AI score" with no
    // model behind it is the most damaging thing this surface could ship: a salesperson told "AI says
    // 23%" will act on it and never learn the number came from nowhere. A salesperson told "last
    // activity was 24 days ago" can check it in one click. Several tests below exist only to stop that
    // line being crossed later, quietly, by someone adding a plausible sentence.
    public class CrmOpportunityInsightsTests
    {
        private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        private const int Company = 7;

        private static Rules.Signal Opp(
            int id = 1,
            string stage = "Proposal",
            decimal amount = 5_000m,
            int daysOld = 10,
            int? daysSinceActivity = 1,
            int openActivities = 1,
            int? daysUntilClose = 30,
            bool noCloseDate = false,
            int? accountId = 42,
            string? owner = "Sara") => new()
        {
            OpportunityId = id,
            Title = $"Opp {id}",
            AccountId = accountId,
            AccountName = accountId == null ? null : "Acme",
            Stage = stage,
            Amount = amount,
            Probability = 50,
            ExpectedCloseDate = noCloseDate ? null : Now.Date.AddDays(daysUntilClose ?? 0),
            CreatedAt = Now.Date.AddDays(-daysOld),
            OwnerEmployeeId = 3,
            OwnerName = owner,
            LastActivityAt = daysSinceActivity == null ? null : Now.Date.AddDays(-daysSinceActivity.Value),
            OpenActivityCount = openActivities,
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Controller() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "CrmController.cs"));

        private static string View() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Crm", "OpportunityInsights.cshtml"));

        private static string Action()
        {
            var src = Controller();
            var a = src.IndexOf("public async Task<IActionResult> OpportunityInsights()", StringComparison.Ordinal);
            Assert.True(a >= 0, "the OpportunityInsights action must exist");
            var b = src.IndexOf("[HttpGet] public async Task<IActionResult> Opportunities()", a, StringComparison.Ordinal);
            return b < 0 ? src[a..] : src[a..b];
        }

        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
        }

        // =========================================================================================
        // 1. Real results — each rule fires on real evidence
        // =========================================================================================

        [Fact]
        public void A_healthy_opportunity_produces_no_findings()
        {
            var found = Rules.Evaluate(Opp(), Now);
            Assert.Empty(found);
        }

        [Fact]
        public void A_past_expected_close_date_is_high_severity_with_the_days_overdue()
        {
            var f = Rules.Evaluate(Opp(daysUntilClose: -12), Now);

            var overdue = Assert.Single(f, x => x.Finding == Rules.Finding.PastExpectedClose);
            Assert.Equal(Rules.Severity.High, overdue.Severity);
            Assert.Equal(12, overdue.EvidenceDays);
        }

        [Fact]
        public void A_stale_opportunity_reports_the_days_since_the_last_activity()
        {
            var f = Rules.Evaluate(Opp(daysSinceActivity: 45), Now);

            var stale = Assert.Single(f, x => x.Finding == Rules.Finding.StaleActivity);
            Assert.Equal(45, stale.EvidenceDays);
            Assert.Equal(Rules.Severity.Medium, stale.Severity);
        }

        // "Never contacted" and "not contacted lately" call for different actions, so they are different
        // findings rather than one blurred "stale".
        [Fact]
        public void Never_contacted_is_a_different_finding_from_gone_quiet()
        {
            var never = Rules.Evaluate(Opp(daysSinceActivity: null), Now);
            var quiet = Rules.Evaluate(Opp(daysSinceActivity: 45), Now);

            Assert.Contains(never, x => x.Finding == Rules.Finding.NoActivityEver);
            Assert.DoesNotContain(never, x => x.Finding == Rules.Finding.StaleActivity);
            Assert.Contains(quiet, x => x.Finding == Rules.Finding.StaleActivity);
            Assert.DoesNotContain(quiet, x => x.Finding == Rules.Finding.NoActivityEver);
        }

        [Fact]
        public void An_imminent_close_only_surfaces_when_nobody_is_working_it()
        {
            var worked = Rules.Evaluate(Opp(daysUntilClose: 5, openActivities: 2), Now);
            var abandoned = Rules.Evaluate(Opp(daysUntilClose: 5, openActivities: 0), Now);

            Assert.DoesNotContain(worked, x => x.Finding == Rules.Finding.ClosingSoon);
            Assert.Contains(abandoned, x => x.Finding == Rules.Finding.ClosingSoon);
        }

        [Fact]
        public void A_long_open_opportunity_reports_its_age()
        {
            var f = Rules.Evaluate(Opp(daysOld: 200), Now);
            var old = Assert.Single(f, x => x.Finding == Rules.Finding.LongOpen);
            Assert.Equal(200, old.EvidenceDays);
        }

        [Fact]
        public void A_missing_expected_close_date_is_reported_as_a_low_severity_data_gap()
        {
            var f = Rules.Evaluate(Opp(noCloseDate: true), Now);
            var gap = Assert.Single(f, x => x.Finding == Rules.Finding.NoExpectedCloseDate);
            Assert.Equal(Rules.Severity.Low, gap.Severity);
        }

        // A closed deal is finished, not neglected. Listing it would train the reader to ignore the list.
        [Theory]
        [InlineData("Won")]
        [InlineData("Lost")]
        [InlineData("won")]
        public void A_closed_opportunity_is_never_flagged_however_neglected(string stage)
        {
            var f = Rules.Evaluate(Opp(stage: stage, daysSinceActivity: 999, daysOld: 999, openActivities: 0, daysUntilClose: -999), Now);
            Assert.Empty(f);
        }

        // High value AMPLIFIES a real finding; it is never a finding by itself. A large healthy deal is
        // not a problem, and listing it as one would bury the real ones.
        [Fact]
        public void High_value_escalates_a_finding_but_is_not_a_finding_on_its_own()
        {
            var healthyBig = Rules.Evaluate(Opp(amount: 5_000_000m), Now);
            Assert.Empty(healthyBig);

            var small = Rules.Evaluate(Opp(amount: 1_000m, daysSinceActivity: 45), Now)
                .Single(x => x.Finding == Rules.Finding.StaleActivity);
            var big = Rules.Evaluate(Opp(amount: Rules.HighValueThreshold, daysSinceActivity: 45), Now)
                .Single(x => x.Finding == Rules.Finding.StaleActivity);

            Assert.Equal(Rules.Severity.Medium, small.Severity);
            Assert.Equal(Rules.Severity.High, big.Severity);
        }

        // =========================================================================================
        // 2 & 3. Empty vs insufficient — the distinction that must never blur
        // =========================================================================================

        [Fact]
        public void Zero_findings_over_real_opportunities_is_a_genuine_clean_result()
        {
            var vm = new CrmOpportunityInsightsVm
            {
                Analysed = 120,
                Insights = Rules.Analyse(Enumerable.Range(1, 120).Select(i => Opp(i)), Now),
                Panel = AiInsightMapper.Map(200, 120, Company, Now),
            };

            Assert.True(vm.Panel.HasResult);
            Assert.Empty(vm.Insights);
            Assert.Equal(120, vm.Panel.RecordsConsidered);
            Assert.Equal(0, vm.OpportunitiesWithFindings);
        }

        [Fact]
        public void Zero_opportunities_analysed_is_InsufficientData_never_a_clean_pipeline()
        {
            var vm = new CrmOpportunityInsightsVm { Analysed = 0, Panel = AiInsightMapper.Map(200, 0, Company, Now) };

            Assert.Equal(AiInsightState.InsufficientData, vm.Panel.State);
            Assert.False(vm.Panel.HasResult);
            Assert.Null(vm.Panel.GeneratedAtUtc);
        }

        [Fact]
        public void The_view_separates_the_clean_result_from_the_nothing_analysed_state()
        {
            var view = View();

            Assert.Contains("AiInsightState.InsufficientData", view, StringComparison.Ordinal);
            Assert.Contains("No opportunities need attention", view, StringComparison.Ordinal);
            Assert.Contains("This is not a statement that your pipeline is healthy", view, StringComparison.Ordinal);
            Assert.Contains("Model.Insights.Count == 0", view, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 4 & 5. Unavailable and Failed
        // =========================================================================================

        [Fact]
        public void Unreachable_data_is_Unavailable_and_unreadable_data_is_Failed()
        {
            var unavailable = AiInsightMapper.Unavailable("crm-insight:data-unavailable", Company, Now);
            var failed = AiInsightMapper.Failed("crm-insight:unreadable-data", Company, Now);

            Assert.Equal(AiInsightState.Unavailable, unavailable.State);
            Assert.Equal(AiInsightState.Failed, failed.State);
            Assert.NotEqual(unavailable.State, failed.State);
            Assert.False(unavailable.HasResult);
            Assert.False(failed.HasResult);
        }

        [Fact]
        public void The_action_handles_both_failure_modes()
        {
            var src = Controller();
            Assert.Contains("catch (Microsoft.Data.SqlClient.SqlException)", src, StringComparison.Ordinal);
            Assert.Contains("catch (InvalidOperationException)", src, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 6 & 7. Company isolation
        // =========================================================================================

        [Fact]
        public void The_action_accepts_no_company_from_the_caller_and_resolves_one()
        {
            var src = Controller();
            var code = StripComments(Action());

            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
            Assert.Contains("if (!scope.Ok)", code, StringComparison.Ordinal);
            Assert.Contains("OpportunityInsights()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("OpportunityInsights(int", src, StringComparison.Ordinal);
        }

        // Every table this action touches carries its own company predicate. A filter that is only
        // correct because of another table's predicate is one refactor away from being wrong.
        [Fact]
        public void Every_query_in_the_action_is_company_predicated()
        {
            var code = StripComments(Action());

            Assert.Contains("o.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);      // Opportunities
            Assert.Contains("a.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);      // Activities + CrmAccounts
            Assert.Contains("e.EmpCompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // Employee

            // and never the controller's hardcoded default
            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unresolved_company_refuses_without_disclosing_why()
        {
            var code = StripComments(Action());

            Assert.Contains("RedirectToAction(nameof(Index))", code, StringComparison.Ordinal);
            Assert.DoesNotContain("scope.Reason", code, StringComparison.Ordinal);
            Assert.DoesNotContain("scope.Failure", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 8. CRM permission — the real path, not copied logic
        // =========================================================================================

        [Fact]
        public void The_action_uses_the_modules_own_permission_attribute()
        {
            var src = Controller();
            var idx = src.IndexOf("public async Task<IActionResult> OpportunityInsights()", StringComparison.Ordinal);
            var header = src[Math.Max(0, idx - 400)..idx];

            Assert.Contains("[CrossBuy.Models.CrmPerm(\"read\")]", header, StringComparison.Ordinal);
        }

        // Owner scoping comes from ICrmAccessService, not from role logic re-implemented in the
        // controller, and it is applied ON TOP of the company predicate rather than instead of it.
        [Fact]
        public void Owner_scope_comes_from_the_crm_access_service_and_never_replaces_company_scope()
        {
            var code = StripComments(Action());

            Assert.Contains("_access.VisibleOwnerIdsAsync()", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CrmUserRoles", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SalesManager", code, StringComparison.Ordinal);

            // the company predicate is applied before any owner narrowing
            var companyAt = code.IndexOf("o.CompanyID == scope.CompanyId", StringComparison.Ordinal);
            var ownerAt = code.IndexOf("visibleOwners.Contains", StringComparison.Ordinal);
            Assert.True(companyAt >= 0 && ownerAt > companyAt,
                "company scope must be applied before owner narrowing");
        }

        // =========================================================================================
        // 9. Navigation targets exist
        // =========================================================================================

        [Fact]
        public void Every_navigation_target_the_view_links_to_really_exists()
        {
            var view = View();
            var crm = Controller();

            Assert.Contains("Url.Action(\"OpportunityProducts\",\"Crm\", new { id = o.OpportunityId })", view, StringComparison.Ordinal);
            Assert.Contains("Url.Action(\"AccountEditor\",\"Crm\", new { id = o.AccountId.Value })", view, StringComparison.Ordinal);

            // Asserted on the SIGNATURE, not the return type: these are declared Task<IActionResult>,
            // so a literal that includes "IActionResult " with a space matches nothing and the test
            // would have passed for the wrong reason had it been written the other way round.
            Assert.Contains("OpportunityProducts(int id)", crm, StringComparison.Ordinal);
            Assert.Contains("AccountEditor(int? id)", crm, StringComparison.Ordinal);
            Assert.Contains("Activities()", crm, StringComparison.Ordinal);
            Assert.Contains("Opportunities()", crm, StringComparison.Ordinal);
            Assert.Contains("Pipeline()", crm, StringComparison.Ordinal);
        }

        // An account link must not be rendered for an opportunity that has no account.
        [Fact]
        public void The_account_link_is_conditional_on_an_account_existing()
            => Assert.Contains("if (o.AccountId.HasValue)", View(), StringComparison.Ordinal);

        // =========================================================================================
        // 10 & 11. Nothing internal on screen
        // =========================================================================================

        [Fact]
        public void No_raw_payload_and_no_exception_text_reaches_the_page()
        {
            var code = StripComments(Action());
            var view = View();

            Assert.DoesNotContain("ex.Message", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception e", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Panel.Detail", view, StringComparison.Ordinal);
            Assert.DoesNotContain("StackTrace", view, StringComparison.Ordinal);
        }

        [Fact]
        public void Panel_details_are_fixed_tokens()
        {
            foreach (var d in new[]
                     {
                         AiInsightMapper.Unavailable("crm-insight:data-unavailable", Company, Now).Detail,
                         AiInsightMapper.Failed("crm-insight:unreadable-data", Company, Now).Detail,
                     })
            {
                Assert.NotNull(d);
                Assert.StartsWith("crm-insight:", d!, StringComparison.Ordinal);
                Assert.DoesNotContain("{", d, StringComparison.Ordinal);
                Assert.True(d!.Length <= 64);
            }
        }

        // =========================================================================================
        // 12 & 13. No provider access, no approval mutation
        // =========================================================================================

        [Fact]
        public void The_screen_touches_no_ai_provider_at_all()
        {
            foreach (var src in new[] { Action(), View(), File.ReadAllText(Path.Combine(
                         RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "CrmOpportunityRiskRules.cs")) })
            {
                Assert.DoesNotContain("api.openai.com", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAiProviderAdapter", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OPENAI_API_KEY", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAiOptions", src, StringComparison.Ordinal);
                Assert.DoesNotContain("new AiEgressApproval", src, StringComparison.Ordinal);
            }
        }

        // This surface performs NO AI egress, because there is no CRM model to send anything to. Stated
        // as a test so that adding one later is a deliberate act with a governance conversation attached.
        [Fact]
        public void The_screen_performs_no_ai_egress_because_no_crm_model_exists()
        {
            var code = StripComments(Action());

            Assert.DoesNotContain("_insights.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("IAiEgressPolicy", code, StringComparison.Ordinal);
            Assert.DoesNotContain("EvaluateAsync", code, StringComparison.Ordinal);

            // and the governed AI service still exposes only the three models that do exist
            var contract = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "IAiInsightsService.cs"));
            Assert.DoesNotContain("Crm", contract, StringComparison.Ordinal);
            Assert.DoesNotContain("Opportunity", contract, StringComparison.Ordinal);
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
        // 14. Honest wording
        // =========================================================================================

        [Fact]
        public void The_page_states_that_it_is_rules_and_not_a_model()
        {
            var view = View();

            Assert.Contains("Rule-based review list", view, StringComparison.Ordinal);
            Assert.Contains("There is no predictive model", view, StringComparison.Ordinal);
            Assert.Contains("Business rules", view, StringComparison.Ordinal);
        }

        [Fact]
        public void The_page_claims_no_capability_that_does_not_exist()
        {
            // The disclaimer legitimately names what is absent; a scan that could not tell a denial from
            // a claim would forbid the page's most important true sentence. Razor comments never render.
            var rendered = System.Text.RegularExpressions.Regex.Replace(
                View(), @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            const string disclaimer =
                "There is no predictive model: nothing here estimates a win chance, a close date or a revenue figure.";
            Assert.Contains(disclaimer, rendered, StringComparison.Ordinal);
            var claims = rendered.Replace(disclaimer, string.Empty, StringComparison.Ordinal);

            foreach (var overclaim in new[]
                     {
                         "win probability", "churn", "AI score", "confidence score",
                         "predicts", "predicted", "machine learning", "forecasts that",
                     })
            {
                Assert.DoesNotContain(overclaim, claims, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Probability IS stored on Opportunity — but a human typed it. It must never be presented as a
        // model output. The rules carry it for display only and never branch on it.
        [Fact]
        public void The_stored_probability_is_never_treated_as_a_model_output()
        {
            var rules = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "CrmOpportunityRiskRules.cs"));
            var code = StripComments(rules);

            // present on the signal for display, but no rule reads it to decide anything
            Assert.Contains("public int Probability", code, StringComparison.Ordinal);
            Assert.DoesNotContain("s.Probability >", code, StringComparison.Ordinal);
            Assert.DoesNotContain("s.Probability <", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 15. Read-only
        // =========================================================================================

        [Fact]
        public void The_screen_writes_nothing()
        {
            var code = StripComments(Action());
            var view = View();

            foreach (var writer in new[] { "SaveChangesAsync", ".Add(", ".Update(", ".Remove(", "_crm.Save", "ExecuteUpdate", "ExecuteDelete" })
                Assert.DoesNotContain(writer, code, StringComparison.Ordinal);

            Assert.DoesNotContain("<form", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("method=\"post\"", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HttpPost", Action(), StringComparison.Ordinal);
        }

        // =========================================================================================
        // 16. Deterministic ranking
        // =========================================================================================

        [Fact]
        public void The_ranking_is_total_and_repeatable()
        {
            var pipeline = new[]
            {
                Opp(1, daysSinceActivity: 40, amount: 10m),
                Opp(2, daysUntilClose: -3),
                Opp(3, daysSinceActivity: 90, amount: 10m),
                Opp(4, noCloseDate: true, openActivities: 0),
                Opp(5, daysUntilClose: -3, amount: 999_999m),
            };

            var a = Rules.Analyse(pipeline, Now).Select(i => (i.Opportunity.OpportunityId, i.Finding)).ToList();
            var b = Rules.Analyse(pipeline.Reverse(), Now).Select(i => (i.Opportunity.OpportunityId, i.Finding)).ToList();

            // Input order must not change output order — that is what "deterministic" has to mean.
            Assert.Equal(a, b);

            // Most serious first.
            var ranked = Rules.Analyse(pipeline, Now);
            Assert.Equal(Rules.Severity.High, ranked[0].Severity);
            Assert.True(ranked.Select(i => (int)i.Severity).SequenceEqual(
                ranked.Select(i => (int)i.Severity).OrderByDescending(x => x)),
                "severity must be non-increasing down the list");
        }

        [Fact]
        public void Among_equal_severity_the_longer_neglected_ranks_higher()
        {
            var ranked = Rules.Analyse(new[]
            {
                Opp(1, daysSinceActivity: 35, amount: 10m),
                Opp(2, daysSinceActivity: 120, amount: 10m),
            }, Now).Where(i => i.Finding == Rules.Finding.StaleActivity).ToList();

            Assert.Equal(2, ranked[0].Opportunity.OpportunityId);
            Assert.Equal(120, ranked[0].EvidenceDays);
        }

        // =========================================================================================
        // 17 & 18. Resources, both cultures
        // =========================================================================================

        [Theory]
        [InlineData("en")]
        [InlineData("ar")]
        [InlineData("fr")]
        public void The_screen_is_localised(string lang)
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Crm", $"OpportunityInsights.{lang}.resx");
            Assert.True(File.Exists(path), $"missing {lang} resources");

            var data = XDocument.Load(path).Root!.Elements("data").ToList();
            Assert.NotEmpty(data);
            Assert.All(data, d => Assert.False(string.IsNullOrWhiteSpace(d.Element("value")?.Value)));
        }

        // Arabic must be a real translation, not the English string copied across.
        [Fact]
        public void The_arabic_resources_are_actually_arabic()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Crm");
            var ar = XDocument.Load(Path.Combine(dir, "OpportunityInsights.ar.resx")).Root!
                .Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
            var en = XDocument.Load(Path.Combine(dir, "OpportunityInsights.en.resx")).Root!
                .Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

            Assert.Equal(en.Count, ar.Count);

            var arabic = ar.Values.Count(v => v.Any(c => c >= '؀' && c <= 'ۿ'));
            Assert.True(arabic >= ar.Count * 0.8,
                $"only {arabic} of {ar.Count} Arabic values contain Arabic script");
        }

        // Every key the view asks for must resolve, or the page silently renders its own key text.
        [Fact]
        public void Every_localiser_key_the_view_uses_is_defined()
        {
            var view = View();
            var keys = System.Text.RegularExpressions.Regex
                .Matches(view, "Localizer\\[\"([^\"]+)\"\\]")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            var defined = XDocument.Load(Path.Combine(
                    RepoRoot(), "CrossBuy", "Resources", "Views", "Crm", "OpportunityInsights.en.resx"))
                .Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);

            var missing = keys.Except(defined).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "undefined resource keys: " + string.Join(" | ", missing));
        }

        // =========================================================================================
        // 19 & 20. Wiring
        // =========================================================================================

        [Fact]
        public void The_controller_dependencies_are_registered_services()
        {
            var crm = Controller();
            Assert.Contains("IRequestCompanyResolver company", crm, StringComparison.Ordinal);
            Assert.Contains("ICrmAccessService access", crm, StringComparison.Ordinal);

            var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));
            Assert.Contains("IRequestCompanyResolver", program, StringComparison.Ordinal);
            Assert.Contains("ICrmAccessService", program, StringComparison.Ordinal);
        }

        [Fact]
        public void The_screen_is_reachable_from_the_crm_menu_and_has_a_view()
        {
            var menu = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Models", "Menu", "MainMenu.cs"));
            Assert.Contains("Action = \"OpportunityInsights\", Controller = \"Crm\"", menu, StringComparison.Ordinal);

            Assert.True(File.Exists(Path.Combine(
                RepoRoot(), "CrossBuy", "Views", "Crm", "OpportunityInsights.cshtml")));
        }

        // =========================================================================================
        // Responsive / RTL structure
        // =========================================================================================

        [Fact]
        public void The_layout_is_responsive_and_direction_neutral()
        {
            var view = View();

            // the wide table scrolls inside its own container rather than pushing the page sideways
            Assert.Contains("table-responsive", view, StringComparison.Ordinal);
            Assert.Contains("col-6 col-xl-3", view, StringComparison.Ordinal);
            Assert.Contains("flex-wrap", view, StringComparison.Ordinal);

            // LOGICAL spacing only. ms-*/me-* flip with direction; ml-*/mr-* would strand Arabic.
            foreach (var physical in new[] { "ml-1", "ml-2", "ml-3", "mr-1", "mr-2", "mr-3", "text-left", "text-right" })
                Assert.DoesNotContain($"\"{physical}", view, StringComparison.Ordinal);
        }

        // One page serves both directions. A duplicated RTL view is two pages to keep in step.
        [Fact]
        public void There_is_no_duplicated_rtl_page()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Views", "Crm");
            foreach (var name in new[] { "OpportunityInsights.ar.cshtml", "OpportunityInsightsRtl.cshtml", "OpportunityInsights.rtl.cshtml" })
                Assert.False(File.Exists(Path.Combine(dir, name)), $"{name} must not exist");
        }
    }
}
