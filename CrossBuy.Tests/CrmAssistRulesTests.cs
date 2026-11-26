using Xunit;
using A = CrossBuy.BL.CrmIdentity.CrmAssistRules;

namespace CrossBuy.Tests
{
	// CRM C9 — the assistant's rules: a lead score that explains itself, and the next action on a deal.
	public class CrmAssistRulesTests
	{
		static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
		static A.LeadSignals L(int fit = 0, bool booked = false, int? inboundDaysAgo = null, int forms = 0, bool optIn = false, int done = 0, int? touchDaysAgo = null, int createdDaysAgo = 2, bool converted = false)
			=> new(fit, booked, inboundDaysAgo is int i ? Now.AddDays(-i) : null, forms, optIn, done, touchDaysAgo is int t ? Now.AddDays(-t) : null, Now.AddDays(-createdDaysAgo), converted);

		[Fact]
		public void Behaviour_lifts_a_lead_and_every_point_has_a_reason()
		{
			var s = A.ScoreLead(L(fit: 20, booked: true, inboundDaysAgo: 1, forms: 1, optIn: true), Now);
			Assert.Equal(80, s.Score); Assert.Equal("Hot", s.Band);
			Assert.Equal(s.Score, s.Reasons.Sum(r => r.Points));
			Assert.Equal(new[] { "booked", "fit", "messagedRecently", "webForm", "optIn" }, s.Reasons.Select(r => r.Code));
		}

		[Fact]
		public void Silence_cools_a_lead_and_the_score_never_leaves_0_to_100()
		{
			var cold = A.ScoreLead(L(fit: 30, createdDaysAgo: 90), Now);
			Assert.Equal(5, cold.Score); Assert.Equal("Cold", cold.Band);
			Assert.Contains(cold.Reasons, r => r.Code == "silent" && r.Points == -25 && r.Evidence == 90);
			Assert.Equal(0, A.ScoreLead(L(createdDaysAgo: 400), Now).Score);
			Assert.Equal(100, A.ScoreLead(L(fit: 99, booked: true, inboundDaysAgo: 0, forms: 5, optIn: true, done: 9), Now).Score);
			// a fresh touch resets the silence even when the lead is old
			Assert.DoesNotContain(A.ScoreLead(L(fit: 30, createdDaysAgo: 90, touchDaysAgo: 3), Now).Reasons, r => r.Code == "silent");
		}

		[Fact]
		public void Fit_is_capped_so_behaviour_still_decides()
		{
			var s = A.ScoreLead(L(fit: 95), Now);
			Assert.Equal(40, s.Score); Assert.Equal("Warm", s.Band);
			Assert.Equal(95, s.Reasons.Single(r => r.Code == "fit").Evidence);
		}

		static A.DealFacts D(string stage = "Proposal", DateTime? close = null, int quiet = 2, bool task = true, string[]? unmet = null, bool pend = false, bool rej = false,
			bool dm = true, int inbound = 0, bool quote = false, int prob = 50, decimal amount = 10000)
			=> new(stage, amount, close, quiet, task, unmet ?? Array.Empty<string>(), pend, rej, dm, inbound, quote, prob);

		[Fact]
		public void A_waiting_customer_comes_before_everything()
		{
			var a = A.NextActions(D(inbound: 2, quiet: 30, task: false, close: new DateTime(2026, 11, 1), dm: false), Now);
			Assert.Equal("answerCustomer", a[0].Code);
			Assert.Equal("fixCloseDate", a[1].Code); Assert.Equal("10", a[1].Param);
			Assert.Contains(a, x => x.Code == "reconnect" && x.Param == "30");
		}

		[Fact]
		public void Discount_and_quote_and_closing()
		{
			Assert.Equal("reviseDiscount", A.NextActions(D(rej: true, pend: true), Now)[0].Code);
			Assert.Equal("chaseApproval", A.NextActions(D(pend: true), Now)[0].Code);
			Assert.Equal("sendQuote", A.NextActions(D(close: new DateTime(2026, 11, 15), prob: 75), Now)[0].Code);
			Assert.DoesNotContain(A.NextActions(D(close: new DateTime(2026, 11, 15), prob: 75, quote: true), Now), x => x.Code == "sendQuote");
			Assert.Equal("setCloseDate", A.NextActions(D(), Now).Single().Code);
		}

		[Fact]
		public void Closed_deals_need_nothing_and_urgency_is_the_top_weight()
		{
			Assert.Empty(A.NextActions(D(stage: "Won", inbound: 3), Now));
			Assert.Equal(0, A.Urgency(new List<A.Action>()));
			Assert.Equal(100, A.Urgency(A.NextActions(D(inbound: 1), Now)));
			Assert.Equal("المبلغ، الموعد", A.NextActions(D(unmet: new[] { "المبلغ", "الموعد" }, close: new DateTime(2026, 12, 30)), Now).Single(x => x.Code == "completeStage").Param);
		}
	}
}
