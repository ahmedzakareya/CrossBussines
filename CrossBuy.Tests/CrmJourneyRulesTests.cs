using Xunit;
using J = CrossBuy.BL.CrmIdentity.CrmJourneyRules;

namespace CrossBuy.Tests
{
	// CRM C7 — segment rules and journey paths.
	public class CrmJourneyRulesTests
	{
		static readonly DateTime Today = new(2026, 11, 10);
		static J.Facts F(string? ind = "مطاعم", bool cust = true, DateTime? last = null, decimal s12 = 0, bool open = false, string? wa = null, bool email = true)
			=> new(1, 10, ind, 17, cust, last, s12, open, null, wa, null, email, true, true);

		[Fact]
		public void All_needs_every_condition_and_Any_needs_one()
		{
			var rules = new[] { new J.Rule("industry", "مطاعم"), new J.Rule("boughtWithin", "90") };
			Assert.True(J.Matches(rules, true, F(last: Today.AddDays(-30)), Today));
			Assert.False(J.Matches(rules, true, F(last: Today.AddDays(-120)), Today));
			Assert.True(J.Matches(rules, false, F(last: Today.AddDays(-120)), Today));
			Assert.False(J.Matches(rules, false, F(ind: "فنادق", last: null), Today));
			Assert.False(J.Matches(Array.Empty<J.Rule>(), true, F(), Today));     // no rule matches nobody, never everybody
		}

		[Fact]
		public void Lapsed_means_a_customer_whose_last_invoice_is_older_than_the_days_or_who_never_bought()
		{
			var r = new[] { new J.Rule("notBoughtFor", "90") };
			Assert.True(J.Matches(r, true, F(last: Today.AddDays(-91)), Today));
			Assert.True(J.Matches(r, true, F(last: null), Today));
			Assert.False(J.Matches(r, true, F(last: Today.AddDays(-10)), Today));
			Assert.False(J.Matches(r, true, F(cust: false, last: null), Today));   // a prospect has not lapsed
		}

		[Fact]
		public void Consent_and_amount_and_yes_no_conditions()
		{
			Assert.True(J.Matches(new[] { new J.Rule("waOptIn", "yes") }, true, F(wa: "OptIn"), Today));
			Assert.False(J.Matches(new[] { new J.Rule("waOptIn", "yes") }, true, F(wa: "OptOut"), Today));
			Assert.True(J.Matches(new[] { new J.Rule("waOptIn", "no") }, true, F(wa: null), Today));
			Assert.True(J.Matches(new[] { new J.Rule("sales12m", "5000") }, true, F(s12: 5000), Today));
			Assert.False(J.Matches(new[] { new J.Rule("sales12m", "5000") }, true, F(s12: 4999.99m), Today));
			Assert.True(J.Matches(new[] { new J.Rule("industry", " مطاعم ") }, true, F(), Today));
		}

		[Fact]
		public void Rules_are_validated_by_their_kind()
		{
			Assert.Null(J.RulesRefusal(new[] { new J.Rule("industry", "x"), new J.Rule("boughtWithin", "30"), new J.Rule("waOptIn", "yes"), new J.Rule("sales12m", "1000.5"), new J.Rule("owner", "17") }));
			Assert.NotNull(J.RulesRefusal(Array.Empty<J.Rule>()));
			Assert.NotNull(J.RulesRefusal(new[] { new J.Rule("nonsense", "1") }));
			Assert.NotNull(J.RulesRefusal(new[] { new J.Rule("boughtWithin", "0") }));
			Assert.NotNull(J.RulesRefusal(new[] { new J.Rule("waOptIn", "maybe") }));
			Assert.NotNull(J.RulesRefusal(new[] { new J.Rule("industry", " ") }));
		}

		static List<J.Node> Tree()
		{
			// whatsapp → wait 3 → bought? yes: [thanks e-mail]  no: [call task, wait 2] → final e-mail
			var t = new List<J.Node> {
				new() { Type = "whatsapp", Template = "offer" },
				new() { Type = "wait", Days = 3 },
				new() { Type = "branch", Cond = "bought", Yes = { new() { Type = "email", Subject = "شكراً", Body = "b" } }, No = { new() { Type = "task", Subject = "اتصل" }, new() { Type = "wait", Days = 2 } } },
				new() { Type = "email", Subject = "آخر", Body = "b" } };
			J.AssignIds(t);
			return t;
		}

		[Fact]
		public void Paths_walk_beside_then_back_out_of_a_branch()
		{
			var t = Tree();
			Assert.Equal(new[] { "n1", "n2", "n3", "n4", "n5", "n6", "n7" }, new[] { t[0].Id, t[1].Id, t[2].Id, t[2].Yes[0].Id, t[2].No[0].Id, t[2].No[1].Id, t[3].Id });
			Assert.Equal("n1", J.First(t));
			Assert.Equal("n3", J.After(t, "n2"));
			Assert.Equal("n4", J.Enter(t, "n3", true));
			Assert.Equal("n5", J.Enter(t, "n3", false));
			Assert.Equal("n7", J.After(t, "n4"));     // end of the yes path → after the branch
			Assert.Equal("n6", J.After(t, "n5"));
			Assert.Equal("n7", J.After(t, "n6"));
			Assert.Null(J.After(t, "n7"));            // the end
			Assert.Null(J.After(t, "n99"));           // a node removed by an edit finishes the walk
		}

		[Fact]
		public void An_empty_path_goes_straight_past_the_branch_and_ids_survive_an_edit()
		{
			var t = Tree();
			t[2].Yes.Clear();
			Assert.Equal("n7", J.Enter(t, "n3", true));
			t.Insert(1, new J.Node { Type = "task", Subject = "جديد" });
			J.AssignIds(t);
			Assert.Equal("n8", t[1].Id);              // the new node is numbered after the highest
			Assert.Equal("n2", t[2].Id);              // the old ones keep theirs: people waiting there stay there
		}

		[Fact]
		public void Trees_are_validated()
		{
			Assert.Null(J.TreeRefusal(Tree()));
			Assert.NotNull(J.TreeRefusal(new List<J.Node>()));
			Assert.NotNull(J.TreeRefusal(new List<J.Node> { new() { Type = "wait", Days = 0 } }));
			Assert.NotNull(J.TreeRefusal(new List<J.Node> { new() { Type = "branch", Cond = "whatever" } }));
			Assert.NotNull(J.TreeRefusal(new List<J.Node> { new() { Type = "email", Subject = "s" } }));
			Assert.NotNull(J.TreeRefusal(new List<J.Node> { new() { Type = "task", Subject = "s", Yes = { new() { Type = "task", Subject = "x" } } } }));
			var deep = new J.Node { Type = "branch", Cond = "bought" }; var cur = deep;
			for (var i = 0; i < 3; i++) { var next = new J.Node { Type = "branch", Cond = "bought" }; cur.Yes.Add(next); cur = next; }
			Assert.NotNull(J.TreeRefusal(new List<J.Node> { deep }));
		}
	}
}
