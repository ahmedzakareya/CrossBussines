using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Rules = CrossBuy.BL.Platform.Ai.CrmAccountHealthRules;

namespace CrossBuy.Tests
{
    // THE QUERY SHAPES BEHIND ACCOUNT INSIGHTS, EXECUTED.
    //
    // WHY THIS FILE EXISTS SEPARATELY FROM THE RULES TESTS. The rules are a pure function and are tested
    // as one. The queries that FEED them are the other half, and they carry a failure mode no source
    // scan can see: a LINQ shape that compiles perfectly and then throws at runtime because EF cannot
    // translate it. Grouped aggregates over a join are exactly where that happens.
    //
    // The authenticated page could not be opened during this increment (no UAT credential is knowable
    // from the repository), so without these tests an untranslatable query would have shipped invisibly.
    // Each test below executes the SAME shape the controller uses against a real relational provider,
    // and asserts BOTH that it translates and that it computes the right numbers.
    //
    // COMPANY ISOLATION IS EXERCISED WITH DATA, not asserted from source: rows belonging to another
    // company are inserted deliberately, and every expectation is written so that leaking them would
    // change the answer.
    public class CrmAccountHealthQueryTests
    {
        private const int Mine = 1;
        private const int Theirs = 65;
        private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime RecentFrom = Now.Date.AddDays(-Rules.RecentWindowDays);
        private static readonly DateTime PreviousFrom = RecentFrom.AddDays(-Rules.PreviousWindowDays);

        // SEEDED PER COMPANY, IN THAT COMPANY'S OWN SCOPE. The platform's CompanyWriteGuardInterceptor
        // refuses an insert whose CompanyID does not match the writing scope - "a company id from a
        // request, a view model or a route may not redirect a write". That control is correct and is not
        // worked around here: each company's rows are written through a scope bound to that company,
        // which is exactly how the application itself would write them.
        private static void Seed(PlatformTestHost host)
        {
            var mine = host.Request(Mine);
            mine.Db.CrmAccounts.AddRange(
                new CrmAccount { ID = 1, CompanyID = Mine, Name = "Mine A", OwnerEmployeeId = 10 },
                new CrmAccount { ID = 2, CompanyID = Mine, Name = "Mine B", OwnerEmployeeId = 10 });

            mine.Db.Opportunities.AddRange(
                // account 1: two open (one past due), one Won that must NOT count as exposure
                new Opportunity { ID = 100, CompanyID = Mine, AccountId = 1, Title = "Open", Stage = "Proposal", Amount = 30_000m, ExpectedCloseDate = Now.Date.AddDays(10) },
                new Opportunity { ID = 101, CompanyID = Mine, AccountId = 1, Title = "Late", Stage = "Negotiation", Amount = 20_000m, ExpectedCloseDate = Now.Date.AddDays(-5) },
                new Opportunity { ID = 102, CompanyID = Mine, AccountId = 1, Title = "Won", Stage = "Won", Amount = 999_000m, ExpectedCloseDate = Now.Date.AddDays(-40) });

            mine.Db.Activities.AddRange(
                // account 1, direct: 1 recent, 5 previous -> a real decline with a real baseline
                Act(200, Mine, "Account", 1, Now.Date.AddDays(-3), done: false),
                Act(201, Mine, "Account", 1, PreviousFrom.AddDays(2)),
                Act(202, Mine, "Account", 1, PreviousFrom.AddDays(3)),
                Act(203, Mine, "Account", 1, PreviousFrom.AddDays(4)),
                Act(204, Mine, "Account", 1, PreviousFrom.AddDays(5)),
                Act(205, Mine, "Account", 1, PreviousFrom.AddDays(6)),
                // account 1, via its opportunity - engagement with the account
                OppAct(206, Mine, 100, Now.Date.AddDays(-2)));
            mine.Db.SaveChanges();

            var theirs = host.Request(Theirs);
            theirs.Db.CrmAccounts.Add(new CrmAccount { ID = 3, CompanyID = Theirs, Name = "Theirs", OwnerEmployeeId = 99 });
            theirs.Db.Opportunities.Add(
                new Opportunity { ID = 103, CompanyID = Theirs, AccountId = 3, Title = "Foreign", Stage = "Proposal", Amount = 500_000m, ExpectedCloseDate = Now.Date.AddDays(-9) });
            theirs.Db.Activities.AddRange(
                // A FOREIGN activity pointing at MY account id. The company predicate must exclude it;
                // if it ever leaked, the recent count below would be 2 instead of 1.
                Act(207, Theirs, "Account", 1, Now.Date.AddDays(-1)),
                OppAct(208, Theirs, 103, Now.Date.AddDays(-1)));
            theirs.Db.SaveChanges();
        }

        private static Activity Act(int id, int company, string entityType, int entityId, DateTime at, bool done = true) => new()
        {
            ID = id, CompanyID = company, Type = "Call", Subject = "s",
            EntityType = entityType, EntityId = entityId, CreatedAt = at, Done = done,
        };

        private static Activity OppAct(int id, int company, int opportunityId, DateTime at, bool done = true) => new()
        {
            ID = id, CompanyID = company, Type = "Call", Subject = "s",
            OpportunityId = opportunityId, CreatedAt = at, Done = done,
        };

        // ---- the three shapes the controller runs, reproduced exactly ----

        private static async Task<List<(int AccountId, int Recent, int Previous, DateTime? LastAt, int Open)>> DirectAsync(
            CrossBuy.Models.Context.CrossDbContext db, int company, List<int> ids)
        {
            var rows = await db.Activities.AsNoTracking()
                .Where(x => x.CompanyID == company && x.EntityType == "Account" && x.EntityId != null && ids.Contains(x.EntityId.Value))
                .GroupBy(x => x.EntityId!.Value)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Recent = g.Count(x => x.CreatedAt != null && x.CreatedAt >= RecentFrom),
                    Previous = g.Count(x => x.CreatedAt != null && x.CreatedAt >= PreviousFrom && x.CreatedAt < RecentFrom),
                    LastAt = g.Max(x => x.CreatedAt),
                    Open = g.Count(x => !x.Done),
                })
                .ToListAsync();
            return rows.Select(r => (r.AccountId, r.Recent, r.Previous, r.LastAt, r.Open)).ToList();
        }

        private static async Task<List<(int AccountId, int Recent, int Previous)>> ViaOpportunityAsync(
            CrossBuy.Models.Context.CrossDbContext db, int company, List<int> ids)
        {
            var rows = await (
                from act in db.Activities.AsNoTracking().Where(x => x.CompanyID == company && x.OpportunityId != null)
                join opp in db.Opportunities.AsNoTracking().Where(o => o.CompanyID == company && o.AccountId != null)
                    on act.OpportunityId equals opp.ID
                where ids.Contains(opp.AccountId!.Value)
                group act by opp.AccountId!.Value into g
                select new
                {
                    AccountId = g.Key,
                    Recent = g.Count(x => x.CreatedAt != null && x.CreatedAt >= RecentFrom),
                    Previous = g.Count(x => x.CreatedAt != null && x.CreatedAt >= PreviousFrom && x.CreatedAt < RecentFrom),
                }).ToListAsync();
            return rows.Select(r => (r.AccountId, r.Recent, r.Previous)).ToList();
        }

        private static async Task<List<(int AccountId, int OpenCount, decimal OpenValue, int PastDueCount, decimal PastDueValue)>> ExposureAsync(
            CrossBuy.Models.Context.CrossDbContext db, int company, List<int> ids)
        {
            var closed = Rules.ClosedStagesForExposure;
            var today = Now.Date;
            var rows = await db.Opportunities.AsNoTracking()
                .Where(o => o.CompanyID == company && o.AccountId != null && ids.Contains(o.AccountId.Value)
                            && !closed.Contains(o.Stage))
                .GroupBy(o => o.AccountId!.Value)
                .Select(g => new
                {
                    AccountId = g.Key,
                    OpenCount = g.Count(),
                    OpenValue = g.Sum(x => (decimal?)x.Amount) ?? 0m,
                    PastDueCount = g.Count(x => x.ExpectedCloseDate != null && x.ExpectedCloseDate < today),
                    PastDueValue = g.Sum(x => x.ExpectedCloseDate != null && x.ExpectedCloseDate < today ? (decimal?)x.Amount : 0m) ?? 0m,
                })
                .ToListAsync();
            return rows.Select(r => (r.AccountId, r.OpenCount, r.OpenValue, r.PastDueCount, r.PastDueValue)).ToList();
        }

        // =========================================================================================

        [Fact]
        public async Task Every_aggregate_query_translates_to_sql_and_counts_correctly()
        {
            using var host = new PlatformTestHost();
            Seed(host);

            var ids = new List<int> { 1, 2 };
            using var db = host.AllCompanies();

            var direct = await DirectAsync(db, Mine, ids);
            var d1 = Assert.Single(direct, r => r.AccountId == 1);

            // 1 recent, 5 previous. The foreign-company row aimed at account 1 is excluded, so a leak
            // would show 2 recent and this assertion would fail.
            Assert.Equal(1, d1.Recent);
            Assert.Equal(5, d1.Previous);
            Assert.Equal(1, d1.Open);
            Assert.Equal(Now.Date.AddDays(-3), d1.LastAt);
        }

        [Fact]
        public async Task Activity_reaching_an_account_through_its_opportunity_is_counted()
        {
            using var host = new PlatformTestHost();
            Seed(host);
            using var db = host.AllCompanies();

            var via = await ViaOpportunityAsync(db, Mine, new List<int> { 1, 2 });
            var v1 = Assert.Single(via, r => r.AccountId == 1);

            Assert.Equal(1, v1.Recent);
            Assert.Equal(0, v1.Previous);

            // The foreign opportunity's activity belongs to account 3 in another company and must not
            // appear at all for this company's id list.
            Assert.DoesNotContain(via, r => r.AccountId == 3);
        }

        [Fact]
        public async Task Exposure_excludes_closed_deals_and_aggregates_overdue_separately()
        {
            using var host = new PlatformTestHost();
            Seed(host);
            using var db = host.AllCompanies();

            var e = Assert.Single(await ExposureAsync(db, Mine, new List<int> { 1, 2 }), r => r.AccountId == 1);

            // Two open deals only. The 999,000 Won deal must not inflate exposure.
            Assert.Equal(2, e.OpenCount);
            Assert.Equal(50_000m, e.OpenValue);

            // One of them is past its expected close date.
            Assert.Equal(1, e.PastDueCount);
            Assert.Equal(20_000m, e.PastDueValue);
        }

        // A foreign company's numbers must be invisible, not merely filtered later.
        [Fact]
        public async Task No_foreign_company_row_can_reach_this_companys_metrics()
        {
            using var host = new PlatformTestHost();
            Seed(host);
            using var db = host.AllCompanies();

            var ids = new List<int> { 1, 2, 3 };   // deliberately INCLUDING the foreign account id

            var direct = await DirectAsync(db, Mine, ids);
            var via = await ViaOpportunityAsync(db, Mine, ids);
            var exposure = await ExposureAsync(db, Mine, ids);

            // Even asked for account 3 explicitly, the company predicate yields nothing for it.
            Assert.DoesNotContain(direct, r => r.AccountId == 3);
            Assert.DoesNotContain(via, r => r.AccountId == 3);
            Assert.DoesNotContain(exposure, r => r.AccountId == 3);

            // And no foreign value leaked into the totals that DO come back.
            Assert.All(exposure, r => Assert.True(r.OpenValue < 500_000m));
        }

        // The whole pipeline, end to end: query shapes into the rules, on real relational data.
        [Fact]
        public async Task The_queries_and_the_rules_agree_on_a_real_decline()
        {
            using var host = new PlatformTestHost();
            Seed(host);
            using var db = host.AllCompanies();

            var ids = new List<int> { 1, 2 };
            var direct = (await DirectAsync(db, Mine, ids)).ToDictionary(r => r.AccountId);
            var via = (await ViaOpportunityAsync(db, Mine, ids)).ToDictionary(r => r.AccountId);
            var exposure = (await ExposureAsync(db, Mine, ids)).ToDictionary(r => r.AccountId);

            direct.TryGetValue(1, out var d);
            via.TryGetValue(1, out var v);
            exposure.TryGetValue(1, out var x);

            var signal = new Rules.Signal
            {
                AccountId = 1,
                Name = "Mine A",
                RecentActivityCount = d.Recent + v.Recent,       // 1 + 1 = 2
                PreviousActivityCount = d.Previous + v.Previous, // 5 + 0 = 5
                LastActivityAt = d.LastAt,
                OpenFollowUpCount = d.Open,
                OpenOpportunityCount = x.OpenCount,
                OpenOpportunityValue = x.OpenValue,
                PastDueOpportunityCount = x.PastDueCount,
                PastDueOpportunityValue = x.PastDueValue,
            };

            Assert.Equal(2, signal.RecentActivityCount);
            Assert.Equal(5, signal.PreviousActivityCount);

            var findings = Rules.Evaluate(signal, Now);

            // 5 -> 2 clears the baseline and is below half, so it is a real, reportable decline.
            var decline = Assert.Single(findings, f => f.Finding == Rules.Finding.DecliningActivity);
            Assert.Equal(60, decline.DeclinePercent);

            // and the overdue deal is reported separately, from the exposure query.
            Assert.Contains(findings, f => f.Finding == Rules.Finding.OverdueOpportunityExposure);
        }

        // An account with rows in neither aggregate must come through as "never contacted", not as a
        // zero-activity decline. This is the join-miss case that silently produces wrong findings.
        [Fact]
        public async Task An_account_with_no_activity_rows_produces_the_never_contacted_signal()
        {
            using var host = new PlatformTestHost();
            Seed(host);
            using var db = host.AllCompanies();

            var direct = (await DirectAsync(db, Mine, new List<int> { 1, 2 })).ToDictionary(r => r.AccountId);
            var via = (await ViaOpportunityAsync(db, Mine, new List<int> { 1, 2 })).ToDictionary(r => r.AccountId);

            Assert.False(direct.ContainsKey(2));
            Assert.False(via.ContainsKey(2));

            var signal = new Rules.Signal { AccountId = 2, Name = "Mine B", LastActivityAt = null };
            var f = Rules.Evaluate(signal, Now);

            Assert.Contains(f, x => x.Finding == Rules.Finding.NoActivityEver);
            Assert.DoesNotContain(f, x => x.Finding == Rules.Finding.DecliningActivity);
        }
    }
}
