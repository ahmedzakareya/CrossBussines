using Xunit;
using F = CrossBuy.BL.CrmIdentity.CrmForecastRules;

namespace CrossBuy.Tests
{
	// CRM C5 — the forecast arithmetic: periods, categories, the roll-up and who may sign off.
	public class CrmForecastRulesTests
	{
		[Fact]
		public void Periods_parse_months_and_quarters_and_refuse_anything_else()
		{
			var m = F.Parse("2026-11")!;
			Assert.Equal(1, m.Months); Assert.Equal(new DateTime(2026, 11, 1), m.Start); Assert.Equal(new DateTime(2026, 12, 1), m.End);
			Assert.Equal("2026-12", m.Next.Key); Assert.Equal("2026-10", m.Previous.Key);
			var q = F.Parse("2026-Q4")!;
			Assert.True(q.IsQuarter); Assert.Equal(new[] { "2026-10", "2026-11", "2026-12" }, q.MonthKeys);
			Assert.Equal("2027-Q1", q.Next.Key); Assert.Equal("2026-Q3", q.Previous.Key);
			Assert.Equal("2025-12", F.Parse("2026-01")!.Previous.Key);
			foreach (var bad in new[] { null, "", "2026-13", "2026-00", "2026-Q5", "26-11", "2026/11", "abcd-ef" }) Assert.Null(F.Parse(bad));
		}

		[Fact]
		public void A_closed_deal_ignores_the_chosen_category_and_an_open_one_defaults_from_probability()
		{
			Assert.Equal("Won", F.CategoryOf("Won", 100, "Pipeline"));
			Assert.Equal("Lost", F.CategoryOf("Lost", 0, "Commit"));
			Assert.Equal("Commit", F.CategoryOf("Negotiation", 75, null));
			Assert.Equal("BestCase", F.CategoryOf("Proposal", 50, null));
			Assert.Equal("Pipeline", F.CategoryOf("Qualification", 25, null));
			Assert.Equal("Omitted", F.CategoryOf("Negotiation", 90, "Omitted"));    // the owner's choice wins over probability
			Assert.Equal("Pipeline", F.CategoryOf("Prospecting", 10, "Nonsense"));  // an unknown word is no choice
		}

		[Fact]
		public void The_rollup_counts_each_category_once_and_lost_or_omitted_count_for_nothing()
		{
			var r = F.Roll(100_000, new[] { new F.Deal(1, 40_000, "Won"), new F.Deal(1, 30_000, "Commit"), new F.Deal(1, 20_000, "BestCase"),
				new F.Deal(1, 50_000, "Pipeline"), new F.Deal(1, 99_000, "Lost"), new F.Deal(1, 70_000, "Omitted") });
			Assert.Equal(40_000, r.Won); Assert.Equal(70_000, r.Forecast); Assert.Equal(90_000, r.Upside);
			Assert.Equal(60_000, r.Gap); Assert.Equal(40, r.Attainment); Assert.Equal(70, r.ForecastPercent);
			Assert.Equal(1.7m, r.Coverage);                                       // (30+20+50) / 60
		}

		[Fact]
		public void No_quota_means_no_percentages_and_a_met_quota_has_no_gap()
		{
			var none = F.Roll(0, new[] { new F.Deal(1, 10, "Won") });
			Assert.Null(none.Attainment); Assert.Null(none.ForecastPercent); Assert.Null(none.Coverage);
			var met = F.Roll(100, new[] { new F.Deal(1, 150, "Won"), new F.Deal(1, 10, "Pipeline") });
			Assert.Equal(0, met.Gap); Assert.Equal(150, met.Attainment); Assert.Null(met.Coverage);
			Assert.Equal(new F.Rollup(100, 150, 0, 0, 10), F.Rollup.Zero + met);
		}

		[Fact]
		public void Sign_off_only_by_someone_else_in_whose_team_the_person_is_and_only_once()
		{
			var team = new HashSet<int> { 5, 17, 18 };
			Assert.Null(F.ReviewRefusal(5, 17, team, "Submitted"));
			Assert.NotNull(F.ReviewRefusal(5, 5, team, "Submitted"));        // own forecast
			Assert.NotNull(F.ReviewRefusal(5, 30, team, "Submitted"));       // not in the team
			Assert.NotNull(F.ReviewRefusal(5, 17, team, "Approved"));        // already decided
		}
	}
}
