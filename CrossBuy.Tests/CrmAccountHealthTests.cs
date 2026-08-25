using System.Xml.Linq;
using CrossBuy.ViewModel.Ai;
using Xunit;
using Rules = CrossBuy.BL.Platform.Ai.CrmAccountHealthRules;

namespace CrossBuy.Tests
{
    // CRM ACCOUNT HEALTH — the honesty of "decline".
    //
    // THE FAILURE THIS FILE EXISTS TO PREVENT. A percentage is only meaningful against a baseline big
    // enough to have a trend, and two situations that look identical as ratios are completely different:
    //
    //     previous 12, recent 3   a real slowdown worth a manager's time
    //     previous 1,  recent 0   one call did not repeat. "100% decline" is noise as a statistic.
    //
    // Every decline finding therefore requires a baseline, and every finding carries the raw counts it
    // came from. Several tests below exist purely to stop that discipline being quietly relaxed into a
    // tidier-looking "health score" later.
    //
    // Still no CRM model exists in crossbuy_ai, so nothing here is a prediction and no test pretends
    // otherwise. NO TEST CONTACTS ANY AI SERVICE.
    public class CrmAccountHealthTests
    {
        private static readonly DateTime Now = new(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc);
        private const int Company = 7;

        private static Rules.Signal Acct(
            int id = 1,
            int recent = 5,
            int previous = 5,
            int? daysSinceLast = 2,
            int openFollowUps = 1,
            int openOpps = 0,
            decimal openValue = 0m,
            int pastDue = 0,
            decimal pastDueValue = 0m,
            string? owner = "Sara") => new()
        {
            AccountId = id,
            Name = $"Account {id}",
            OwnerEmployeeId = 3,
            OwnerName = owner,
            RecentActivityCount = recent,
            PreviousActivityCount = previous,
            LastActivityAt = daysSinceLast == null ? null : Now.Date.AddDays(-daysSinceLast.Value),
            OpenFollowUpCount = openFollowUps,
            OpenOpportunityCount = openOpps,
            OpenOpportunityValue = openValue,
            PastDueOpportunityCount = pastDue,
            PastDueOpportunityValue = pastDueValue,
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
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Crm", "AccountInsights.cshtml"));

        private static string Action()
        {
            var src = Controller();
            var a = src.IndexOf("public async Task<IActionResult> AccountInsights()", StringComparison.Ordinal);
            Assert.True(a >= 0, "the AccountInsights action must exist");
            var b = src.IndexOf("// ===== CRM Opportunity Insights", a, StringComparison.Ordinal);
            return b < 0 ? src[a..] : src[a..b];
        }

        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
        }

        // =========================================================================================
        // 1–5. The decline arithmetic, and its honesty
        // =========================================================================================

        [Fact]   // 1
        public void A_material_decline_against_a_real_baseline_is_flagged_with_both_counts()
        {
            var i = Assert.Single(Rules.Evaluate(Acct(previous: 12, recent: 3), Now),
                x => x.Finding == Rules.Finding.DecliningActivity);

            Assert.Equal(75, i.DeclinePercent);
            Assert.Equal(12, i.Account.PreviousActivityCount);
            Assert.Equal(3, i.Account.RecentActivityCount);
        }

        [Fact]   // 2
        public void Stable_activity_is_not_flagged()
        {
            Assert.Empty(Rules.Evaluate(Acct(previous: 8, recent: 8), Now));
            Assert.Empty(Rules.Evaluate(Acct(previous: 8, recent: 7), Now));

            // Exactly at the ratio boundary is NOT a material decline — the rule is "below half".
            Assert.False(Rules.IsMaterialDecline(previous: 8, recent: 4));
            Assert.True(Rules.IsMaterialDecline(previous: 8, recent: 3));
        }

        // THE HEADLINE HONESTY TEST. Zero and zero is not a 100% decline; it is dormancy.
        [Fact]   // 3
        public void Zero_previous_and_zero_recent_is_never_reported_as_a_decline()
        {
            var f = Rules.Evaluate(Acct(previous: 0, recent: 0, daysSinceLast: 400), Now);

            Assert.DoesNotContain(f, x => x.Finding == Rules.Finding.DecliningActivity);
            Assert.Contains(f, x => x.Finding == Rules.Finding.ActivityStopped);
            Assert.All(f, x => Assert.Null(x.DeclinePercent));
            Assert.False(Rules.IsMaterialDecline(0, 0));
        }

        // previous = 1, recent = 0. Real, but not a statistic. No percentage may be attached.
        [Fact]   // 4
        public void One_activity_that_did_not_repeat_is_stopped_activity_without_a_percentage()
        {
            var f = Rules.Evaluate(Acct(previous: 1, recent: 0, daysSinceLast: 40), Now);

            var stopped = Assert.Single(f, x => x.Finding == Rules.Finding.ActivityStopped);
            Assert.Null(stopped.DeclinePercent);
            Assert.DoesNotContain(f, x => x.Finding == Rules.Finding.DecliningActivity);
            Assert.False(Rules.IsMaterialDecline(1, 0));
        }

        [Fact]   // 5
        public void An_account_with_no_activity_ever_is_its_own_finding()
        {
            var f = Rules.Evaluate(Acct(previous: 0, recent: 0, daysSinceLast: null), Now);

            var never = Assert.Single(f, x => x.Finding == Rules.Finding.NoActivityEver);
            Assert.Null(never.DaysSinceLastActivity);
            Assert.DoesNotContain(f, x => x.Finding == Rules.Finding.ActivityStopped);
            Assert.DoesNotContain(f, x => x.Finding == Rules.Finding.DecliningActivity);
        }

        // The baseline is what separates a trend from noise. If it were ever lowered to 1, test 4 above
        // would start reporting "100% decline" — this pins the constant that prevents it.
        [Fact]
        public void The_decline_baseline_is_large_enough_to_exclude_single_data_points()
            => Assert.True(Rules.MinimumBaselineActivities >= 4);

        // =========================================================================================
        // 6–10. Commercial exposure
        // =========================================================================================

        [Fact]   // 6
        public void Open_opportunities_with_no_recent_contact_are_flagged()
        {
            var f = Rules.Evaluate(Acct(previous: 2, recent: 0, daysSinceLast: 45, openOpps: 3, openValue: 40_000m), Now);
            Assert.Contains(f, x => x.Finding == Rules.Finding.OpenOpportunitiesEngagementStopped);
        }

        [Fact]   // 7
        public void High_value_alone_never_creates_a_finding()
        {
            // Healthy engagement, enormous pipeline, everything scheduled: nothing to say.
            var f = Rules.Evaluate(Acct(previous: 8, recent: 9, openOpps: 5, openValue: 10_000_000m, openFollowUps: 4), Now);
            Assert.Empty(f);
        }

        [Fact]   // 8
        public void High_value_amplifies_a_finding_that_already_exists()
        {
            var small = Rules.Evaluate(Acct(previous: 12, recent: 2, openValue: 1_000m), Now)
                .Single(x => x.Finding == Rules.Finding.DecliningActivity);
            var large = Rules.Evaluate(Acct(previous: 12, recent: 2, openValue: Rules.HighExposureThreshold), Now)
                .Single(x => x.Finding == Rules.Finding.DecliningActivity);

            Assert.Equal(Rules.Severity.Medium, small.Severity);
            Assert.Equal(Rules.Severity.High, large.Severity);
        }

        // A Won or Lost deal is not exposure: the money has landed or the decision is taken.
        [Fact]   // 9
        public void Closed_stages_are_excluded_from_exposure_by_the_query_contract()
        {
            Assert.Contains("Won", Rules.ClosedStagesForExposure);
            Assert.Contains("Lost", Rules.ClosedStagesForExposure);

            var code = StripComments(Action());
            Assert.Contains("!closedStages.Contains(o.Stage)", code, StringComparison.Ordinal);
        }

        [Fact]   // 10
        public void Overdue_exposure_is_reported_with_its_aggregated_count_and_value()
        {
            var i = Assert.Single(
                Rules.Evaluate(Acct(previous: 6, recent: 6, openOpps: 4, openValue: 90_000m, pastDue: 2, pastDueValue: 55_000m), Now),
                x => x.Finding == Rules.Finding.OverdueOpportunityExposure);

            Assert.Equal(2, i.Account.PastDueOpportunityCount);
            Assert.Equal(55_000m, i.Account.PastDueOpportunityValue);
        }

        // =========================================================================================
        // 11–12. Deterministic ordering
        // =========================================================================================

        [Fact]   // 11 & 12
        public void The_ranking_is_total_and_input_order_independent()
        {
            var accounts = new[]
            {
                Acct(1, previous: 12, recent: 1, daysSinceLast: 20, openValue: 5_000m),
                Acct(2, previous: 0, recent: 0, daysSinceLast: null, openOpps: 2, openValue: 200_000m),
                Acct(3, previous: 6, recent: 0, daysSinceLast: 90, openOpps: 1, openValue: 10_000m),
                Acct(4, previous: 5, recent: 5, pastDue: 1, pastDueValue: 300_000m, openOpps: 1, openValue: 300_000m),
            };

            var a = Rules.Analyse(accounts, Now).Select(i => (i.Account.AccountId, i.Finding)).ToList();
            var b = Rules.Analyse(accounts.Reverse(), Now).Select(i => (i.Account.AccountId, i.Finding)).ToList();

            Assert.Equal(a, b);

            var ranked = Rules.Analyse(accounts, Now);
            Assert.True(ranked.Select(i => (int)i.Severity).SequenceEqual(
                ranked.Select(i => (int)i.Severity).OrderByDescending(x => x)),
                "severity must be non-increasing down the list");
        }

        // An account never contacted has been silent for its whole life, so it sorts as maximally silent
        // rather than as "0 days", which would put it at the bottom.
        [Fact]
        public void A_never_contacted_account_sorts_as_maximally_silent()
        {
            var ranked = Rules.Analyse(new[]
            {
                Acct(1, previous: 0, recent: 0, daysSinceLast: 60, openOpps: 1, openValue: 1_000m),
                Acct(2, previous: 0, recent: 0, daysSinceLast: null, openOpps: 1, openValue: 1_000m),
            }, Now).Where(i => i.Severity == Rules.Severity.Medium).ToList();

            Assert.Equal(2, ranked[0].Account.AccountId);
        }

        // =========================================================================================
        // 13–17. Isolation and permission
        // =========================================================================================

        [Fact]   // 13 & 29
        public void Every_root_query_is_company_predicated_and_no_default_company_is_used()
        {
            var code = StripComments(Action());

            Assert.Contains("a.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // accounts
            Assert.Contains("x.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // activities
            Assert.Contains("o.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // opportunities
            Assert.Contains("e.EmpCompanyID == scope.CompanyId", code, StringComparison.Ordinal);// employees

            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyID == 1", code, StringComparison.Ordinal);
        }

        // 14 & 15: the join that maps activities to accounts through opportunities predicates BOTH
        // sides on the company. A filter that is only correct because of the other table's predicate is
        // one refactor away from leaking another company's numbers into this company's metrics.
        [Fact]
        public void The_activity_to_account_join_predicates_both_sides_on_the_company()
        {
            var code = StripComments(Action());
            var join = code[code.IndexOf("var viaOpp", StringComparison.Ordinal)..];
            var end = join.IndexOf("ToListAsync", StringComparison.Ordinal);
            join = join[..(end > 0 ? end : join.Length)];

            Assert.Contains("x.CompanyID == scope.CompanyId", join, StringComparison.Ordinal);
            Assert.Contains("o.CompanyID == scope.CompanyId", join, StringComparison.Ordinal);
        }

        [Fact]   // 16
        public void Owner_scope_comes_from_the_crm_service_and_is_applied_on_top_of_company_scope()
        {
            var code = StripComments(Action());

            Assert.Contains("_access.VisibleOwnerIdsAsync()", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CrmUserRoles", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SalesManager", code, StringComparison.Ordinal);

            var companyAt = code.IndexOf("a.CompanyID == scope.CompanyId", StringComparison.Ordinal);
            var ownerAt = code.IndexOf("visibleOwners.Contains", StringComparison.Ordinal);
            Assert.True(companyAt >= 0 && ownerAt > companyAt, "company scope must precede owner narrowing");
        }

        [Fact]   // 17
        public void An_unresolved_company_fails_closed_without_disclosing_why()
        {
            var src = Controller();
            var code = StripComments(Action());

            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
            Assert.Contains("if (!scope.Ok)", code, StringComparison.Ordinal);
            Assert.Contains("RedirectToAction(nameof(Index))", code, StringComparison.Ordinal);
            Assert.DoesNotContain("scope.Reason", code, StringComparison.Ordinal);

            // and no company may be supplied by the caller
            Assert.Contains("AccountInsights()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("AccountInsights(int", src, StringComparison.Ordinal);
        }

        [Fact]
        public void The_action_uses_the_modules_own_permission_attribute()
        {
            var src = Controller();
            var idx = src.IndexOf("public async Task<IActionResult> AccountInsights()", StringComparison.Ordinal);
            Assert.Contains("[CrossBuy.Models.CrmPerm(\"read\")]", src[Math.Max(0, idx - 400)..idx], StringComparison.Ordinal);
        }

        // =========================================================================================
        // 18–21. Honest states
        // =========================================================================================

        [Fact]   // 18 & 19
        public void Unavailable_and_failed_are_distinct_states()
        {
            var u = AiInsightMapper.Unavailable("crm-account:data-unavailable", Company, Now);
            var f = AiInsightMapper.Failed("crm-account:unreadable-data", Company, Now);

            Assert.Equal(AiInsightState.Unavailable, u.State);
            Assert.Equal(AiInsightState.Failed, f.State);
            Assert.NotEqual(u.State, f.State);

            var src = Controller();
            Assert.Contains("crm-account:data-unavailable", src, StringComparison.Ordinal);
            Assert.Contains("crm-account:unreadable-data", src, StringComparison.Ordinal);
        }

        [Fact]   // 20
        public void Zero_accounts_analysed_is_InsufficientData_and_not_a_clean_result()
        {
            var vm = new CrmAccountHealthVm { Analysed = 0, Panel = AiInsightMapper.Map(200, 0, Company, Now) };

            Assert.Equal(AiInsightState.InsufficientData, vm.Panel.State);
            Assert.False(vm.Panel.HasResult);
            Assert.Null(vm.Panel.GeneratedAtUtc);

            var view = View();
            Assert.Contains("This is not a statement that your customer relationships are healthy", view, StringComparison.Ordinal);
        }

        [Fact]   // 21
        public void Zero_findings_over_real_accounts_is_an_honest_clean_state()
        {
            var vm = new CrmAccountHealthVm
            {
                Analysed = 40,
                Insights = Rules.Analyse(Enumerable.Range(1, 40).Select(i => Acct(i)), Now),
                Panel = AiInsightMapper.Map(200, 40, Company, Now),
            };

            Assert.True(vm.Panel.HasResult);
            Assert.Empty(vm.Insights);
            Assert.Equal(0, vm.AccountsNeedingAttention);

            // It says no CONDITION matched - not that the customers are healthy. Razor comments never
            // render, and the one in that region explains this very rule, so it is stripped before the
            // scan; otherwise documenting the rule would break the test that enforces it.
            var view = View();
            var rendered = System.Text.RegularExpressions.Regex.Replace(
                view, @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            Assert.Contains("No attention conditions detected", rendered, StringComparison.Ordinal);
            Assert.Contains("none matched a configured attention condition", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("customers are healthy", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("relationships are healthy", rendered.Replace(
                "This is not a statement that your customer relationships are healthy.", string.Empty,
                StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_view_distinguishes_all_five_states()
        {
            var view = View();

            Assert.Contains("Panel.HasResult", view, StringComparison.Ordinal);                 // available
            Assert.Contains("Model.Insights.Count == 0", view, StringComparison.Ordinal);       // clean
            Assert.Contains("AiInsightState.InsufficientData", view, StringComparison.Ordinal);
            Assert.Contains("AiInsightState.Unavailable", view, StringComparison.Ordinal);
            Assert.Contains("Assessment could not be completed", view, StringComparison.Ordinal); // failed
        }

        // 0 and "not known" must not render identically.
        [Fact]
        public void Never_contacted_renders_as_never_and_not_as_zero_days()
        {
            var view = View();
            Assert.Contains("DaysOrNever", view, StringComparison.Ordinal);
            Assert.Contains("Localizer[\"never\"]", view, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 22–24. No fake AI, no external call, no writes
        // =========================================================================================

        [Fact]   // 22
        public void The_page_uses_no_probability_or_score_language()
        {
            var rendered = System.Text.RegularExpressions.Regex.Replace(
                View(), @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            const string disclaimer = "There is no predictive model: nothing here estimates churn, health or a probability, and every finding shows the counts it came from.";
            Assert.Contains(disclaimer, rendered, StringComparison.Ordinal);
            var claims = rendered.Replace(disclaimer, string.Empty, StringComparison.Ordinal);

            foreach (var overclaim in new[]
                     {
                         "health score", "risk score", "AI score", "churn probability",
                         "confidence", "predicts", "predicted", "machine learning", "probability",
                     })
            {
                Assert.DoesNotContain(overclaim, claims, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]   // 23
        public void The_screen_performs_no_ai_egress_because_no_crm_model_exists()
        {
            var code = StripComments(Action());

            Assert.DoesNotContain("_insights.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("IAiEgressPolicy", code, StringComparison.Ordinal);
            Assert.DoesNotContain("api.openai.com", code, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenAi", code, StringComparison.Ordinal);

            // The governed AI contract still exposes only the three models that actually exist.
            // Scanned over the METHOD SIGNATURES only: the prose in that file mentions
            // "AccountingController", and a substring match on "Account" would flag the word inside it.
            var contract = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "IAiInsightsService.cs"));
            var signatures = contract.Split('\n')
                .Where(l => l.Contains("Task<AiProxyResult>", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(3, signatures.Count);
            foreach (var line in signatures)
                foreach (var crmConcept in new[] { "Account", "Crm", "Opportunity", "Lead" })
                    Assert.DoesNotContain(crmConcept, line, StringComparison.Ordinal);
        }

        [Fact]
        public void The_shipped_provider_record_still_approves_nobody()
        {
            var authority = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "AiProviderAuthority.cs"));

            Assert.Contains("RecordedCandidate = null", authority, StringComparison.Ordinal);
            Assert.Contains("RecordedApproval = null", authority, StringComparison.Ordinal);
        }

        [Fact]   // 24
        public void The_screen_writes_nothing()
        {
            var code = StripComments(Action());
            var view = View();

            foreach (var writer in new[] { "SaveChangesAsync", ".Add(", ".Update(", ".Remove(", "ExecuteUpdate", "ExecuteDelete", "NotifyAsync" })
                Assert.DoesNotContain(writer, code, StringComparison.Ordinal);

            Assert.DoesNotContain("<form", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("method=\"post\"", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HttpPost", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // 25–27. Navigation and localisation
        // =========================================================================================

        [Fact]   // 25
        public void Every_navigation_target_exists()
        {
            var view = View();
            var crm = Controller();

            Assert.Contains("Url.Action(\"AccountEditor\",\"Crm\", new { id = a.AccountId })", view, StringComparison.Ordinal);
            Assert.Contains("AccountEditor(int? id)", crm, StringComparison.Ordinal);
            Assert.Contains("Accounts()", crm, StringComparison.Ordinal);
            Assert.Contains("Activities()", crm, StringComparison.Ordinal);
            Assert.Contains("Opportunities()", crm, StringComparison.Ordinal);
            Assert.Contains("OpportunityInsights()", crm, StringComparison.Ordinal);
        }

        [Theory]   // 26
        [InlineData("en")]
        [InlineData("ar")]
        [InlineData("fr")]
        public void The_screen_is_localised(string lang)
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Crm", $"AccountInsights.{lang}.resx");
            Assert.True(File.Exists(path), $"missing {lang} resources");
            var data = XDocument.Load(path).Root!.Elements("data").ToList();
            Assert.NotEmpty(data);
            Assert.All(data, d => Assert.False(string.IsNullOrWhiteSpace(d.Element("value")?.Value)));
        }

        [Fact]
        public void Every_localiser_key_the_view_uses_is_defined()
        {
            var keys = System.Text.RegularExpressions.Regex
                .Matches(View(), "Localizer\\[\"([^\"]+)\"\\]")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            var defined = XDocument.Load(Path.Combine(
                    RepoRoot(), "CrossBuy", "Resources", "Views", "Crm", "AccountInsights.en.resx"))
                .Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);

            var missing = keys.Except(defined).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "undefined keys: " + string.Join(" | ", missing));
        }

        [Fact]   // 27
        public void The_arabic_resources_are_actually_arabic()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Crm");
            var ar = XDocument.Load(Path.Combine(dir, "AccountInsights.ar.resx")).Root!
                .Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
            var en = XDocument.Load(Path.Combine(dir, "AccountInsights.en.resx")).Root!
                .Elements("data").Count();

            Assert.Equal(en, ar.Count);
            var arabic = ar.Values.Count(v => v.Any(c => c >= '؀' && c <= 'ۿ'));
            Assert.True(arabic >= ar.Count * 0.8, $"only {arabic} of {ar.Count} values contain Arabic script");
        }

        // =========================================================================================
        // 28. Responsive / RTL structure
        // =========================================================================================

        [Fact]
        public void The_layout_is_responsive_and_direction_neutral()
        {
            var view = View();

            Assert.Contains("table-responsive", view, StringComparison.Ordinal);
            Assert.Contains("col-6 col-md-4 col-xl-2", view, StringComparison.Ordinal);
            Assert.Contains("flex-wrap", view, StringComparison.Ordinal);

            // LOGICAL spacing only — ms-*/me-* flip with direction; ml-*/mr-* would strand Arabic.
            foreach (var physical in new[] { "ml-1", "ml-2", "ml-3", "mr-1", "mr-2", "mr-3", "text-left", "text-right" })
                Assert.DoesNotContain($"\"{physical}", view, StringComparison.Ordinal);
        }

        [Fact]
        public void There_is_no_duplicated_rtl_page()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Views", "Crm");
            foreach (var n in new[] { "AccountInsights.ar.cshtml", "AccountInsightsRtl.cshtml", "AccountInsights.rtl.cshtml" })
                Assert.False(File.Exists(Path.Combine(dir, n)), $"{n} must not exist");
        }

        // =========================================================================================
        // 30. No N+1
        // =========================================================================================

        // The whole screen must cost a fixed number of round trips however many accounts exist. A query
        // inside the projection loop is the classic way this degrades silently once a tenant grows.
        [Fact]
        public void The_account_metrics_are_gathered_in_bounded_aggregate_queries()
        {
            var code = StripComments(Action());

            // Exactly five awaited database calls, none of them inside a loop.
            var awaited = System.Text.RegularExpressions.Regex.Matches(code, @"await [^;]*?(ToListAsync|ToDictionaryAsync)\(\)").Count;
            Assert.True(awaited <= 5, $"expected at most 5 database round trips, found {awaited}");

            // Counting happens in SQL, not after materialising activity rows.
            Assert.Contains("g.Count(x => x.CreatedAt != null && x.CreatedAt >= recentFrom)", code, StringComparison.Ordinal);
            Assert.Contains("GroupBy", code, StringComparison.Ordinal);

            // No query sits inside the per-account projection.
            var projection = code[code.IndexOf("var signals = accounts.Select", StringComparison.Ordinal)..];
            foreach (var q in new[] { "await ", "ToListAsync", "FirstOrDefaultAsync", "CountAsync", "_context." })
                Assert.DoesNotContain(q, projection, StringComparison.Ordinal);
        }

        [Fact]
        public void The_view_model_holds_no_data_source()
        {
            foreach (var p in typeof(CrmAccountHealthVm).GetProperties())
            {
                var n = p.PropertyType.Name;
                Assert.False(n.Contains("DbContext", StringComparison.Ordinal), $"{p.Name} is a {n}");
                Assert.False(n.Contains("HttpClient", StringComparison.Ordinal), $"{p.Name} is a {n}");
            }
        }

        // Exposure is counted once per account, however many findings that account tripped. Summing per
        // insight would double-count the same pipeline and inflate the headline number.
        [Fact]
        public void Exposed_value_counts_each_account_once()
        {
            var a = Acct(1, previous: 12, recent: 0, daysSinceLast: 60, openOpps: 2, openValue: 50_000m, pastDue: 1, pastDueValue: 20_000m, openFollowUps: 0);
            var vm = new CrmAccountHealthVm { Analysed = 1, Insights = Rules.Analyse(new[] { a }, Now) };

            Assert.True(vm.Insights.Count > 1, "this account should trip several findings");
            Assert.Equal(1, vm.AccountsNeedingAttention);
            Assert.Equal(50_000m, vm.ExposedOpenValue);
            Assert.Equal(20_000m, vm.PastDueValue);
        }

        [Fact]
        public void The_screen_is_reachable_from_the_crm_menu()
        {
            var menu = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Models", "Menu", "MainMenu.cs"));
            Assert.Contains("Action = \"AccountInsights\", Controller = \"Crm\"", menu, StringComparison.Ordinal);
        }

        // This surface must not become a second rendering of OpportunityInsights.
        [Fact]
        public void The_account_findings_are_distinct_from_the_opportunity_findings()
        {
            var accountFindings = Enum.GetNames<Rules.Finding>().ToHashSet(StringComparer.Ordinal);
            var oppFindings = Enum.GetNames<CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Finding>().ToHashSet(StringComparer.Ordinal);

            // The account surface owns the engagement-over-time question; the opportunity surface does not.
            Assert.Contains("DecliningActivity", accountFindings);
            Assert.DoesNotContain("DecliningActivity", oppFindings);

            // And it does not re-report per-deal conditions the other screen already owns.
            Assert.DoesNotContain("PastExpectedClose", accountFindings);
            Assert.DoesNotContain("ClosingSoon", accountFindings);
        }
    }
}
