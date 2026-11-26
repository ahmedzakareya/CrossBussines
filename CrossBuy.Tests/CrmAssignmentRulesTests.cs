using Xunit;
using A = CrossBuy.BL.CrmIdentity.CrmAssignmentRules;

namespace CrossBuy.Tests
{
	// CRM C2 — who gets a new lead. Pure rules, pinned here directly.
	public class CrmAssignmentRulesTests
	{
		static A.Territory T(int id, int prio, string? areas = null, string? segments = null, string? sources = null, bool active = true, params A.Member[] members) =>
			new(id, "T" + id, areas, segments, sources, prio, active, members);
		static A.Member M(int emp, DateTime? last = null, bool available = true) => new(emp, last, available);

		[Fact]
		public void An_existing_account_owner_wins_over_every_territory()
		{
			var d = A.Decide(new A.LeadFacts("الأحمدي", null, "Website", ExistingAccountOwner: 42, ExistingAccountId: 7), new[] { T(1, 1, "الأحمدي", members: M(5)) });
			Assert.Equal(42, d.OwnerEmployeeId);
			Assert.Equal(A.Why.AccountOwner, d.Reason);
		}

		[Fact]
		public void Areas_match_across_arabic_spellings_and_commas()
		{
			Assert.True(A.Meets("العاصمة، الاحمدي", "الأحمدي"));
			Assert.True(A.Meets("Capital, Ahmadi", "ahmadi"));
			Assert.False(A.Meets("العاصمة", "حولي"));
			Assert.True(A.Meets(null, "anything"));      // empty criterion = any
			Assert.False(A.Meets("العاصمة", null));      // a stated criterion needs a value
		}

		[Fact]
		public void The_lowest_priority_number_among_matching_territories_wins()
		{
			var d = A.Decide(new A.LeadFacts("حولي", "A", "Website"), new[] { T(1, 20, members: M(1)), T(2, 10, "حولي", members: M(2)) });
			Assert.Equal(2, d.OwnerEmployeeId);
			Assert.Equal(2, d.TerritoryId);
		}

		[Fact]
		public void Round_robin_gives_the_lead_to_whoever_waited_longest_never_assigned_first()
		{
			var now = new DateTime(2026, 11, 10, 9, 0, 0);
			Assert.Equal(3, A.NextInTurn(new[] { M(1, now), M(2, now.AddHours(-2)), M(3) })!.EmployeeId);
			Assert.Equal(2, A.NextInTurn(new[] { M(1, now), M(2, now.AddHours(-2)) })!.EmployeeId);
		}

		[Fact]
		public void A_territory_with_nobody_available_is_skipped_for_the_next_match()
		{
			var d = A.Decide(new A.LeadFacts("حولي", null, null), new[] { T(1, 1, "حولي", members: M(9, available: false)), T(2, 2, members: M(4)) });
			Assert.Equal(4, d.OwnerEmployeeId);
			Assert.Equal(new[] { 1 }, d.SkippedTerritories);
		}

		[Fact]
		public void Nothing_matched_means_nobody_never_an_arbitrary_person()
		{
			var none = A.Decide(new A.LeadFacts("الجهراء", null, null), new[] { T(1, 1, "حولي", members: M(1)) });
			Assert.Null(none.OwnerEmployeeId);
			Assert.Equal(A.Why.NoTerritoryMatched, none.Reason);
			var busy = A.Decide(new A.LeadFacts("حولي", null, null), new[] { T(1, 1, "حولي", members: M(1, available: false)) });
			Assert.Null(busy.OwnerEmployeeId);
			Assert.Equal(A.Why.TerritoryHasNobodyAvailable, busy.Reason);
		}

		[Fact]
		public void An_inactive_territory_takes_nothing() =>
			Assert.Null(A.Decide(new A.LeadFacts(null, null, null), new[] { T(1, 1, active: false, members: M(1)) }).OwnerEmployeeId);

		[Fact]
		public void Every_stated_criterion_must_match()
		{
			var t = T(1, 1, "حولي", "A", "Website", members: M(1));
			Assert.True(A.Matches(t, new A.LeadFacts("حولي", "A", "Website")));
			Assert.False(A.Matches(t, new A.LeadFacts("حولي", "B", "Website")));
			Assert.False(A.Matches(t, new A.LeadFacts("حولي", "A", "Referral")));
		}
	}
}
