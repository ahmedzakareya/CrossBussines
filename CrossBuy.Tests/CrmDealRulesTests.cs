using Xunit;
using D = CrossBuy.BL.CrmIdentity.CrmDealRules;

namespace CrossBuy.Tests
{
	// CRM C3 — stage requirements and discount approval. Pure rules, pinned here directly.
	public class CrmDealRulesTests
	{
		static D.Line L(int item, decimal qty, decimal price, decimal disc) => new(item, null, qty, price, disc);

		[Fact]
		public void The_fingerprint_ignores_line_order_but_sees_every_change()
		{
			var a = D.Fingerprint(new[] { L(1, 10, 9.5m, 5), L(2, 3, 100, 0) });
			Assert.Equal(a, D.Fingerprint(new[] { L(2, 3, 100, 0), L(1, 10, 9.5m, 5) }));
			Assert.NotEqual(a, D.Fingerprint(new[] { L(1, 11, 9.5m, 5), L(2, 3, 100, 0) }));     // quantity
			Assert.NotEqual(a, D.Fingerprint(new[] { L(1, 10, 9.4m, 5), L(2, 3, 100, 0) }));     // price
			Assert.NotEqual(a, D.Fingerprint(new[] { L(1, 10, 9.5m, 6), L(2, 3, 100, 0) }));     // discount
		}

		[Fact]
		public void No_limits_configured_means_the_feature_is_off_and_no_role_means_zero()
		{
			Assert.Null(D.LimitFor(new[] { "SalesRep" }, new Dictionary<string, decimal>()));
			var limits = new Dictionary<string, decimal> { ["SalesRep"] = 7, ["SalesManager"] = 15 };
			Assert.Equal(7m, D.LimitFor(new[] { "SalesRep" }, limits));
			Assert.Equal(15m, D.LimitFor(new[] { "SalesRep", "SalesManager" }, limits));
			Assert.Equal(0m, D.LimitFor(new[] { "Marketing" }, limits));
		}

		[Fact]
		public void An_approval_counts_only_for_the_exact_lines_it_was_given_for()
		{
			var fp = D.Fingerprint(new[] { L(1, 10, 9.5m, 12) });
			var approved = new[] { new D.ApprovalFacts("Approved", 12, fp) };
			Assert.Equal(D.DiscountState.Approved, D.StateOf(12, 7, fp, approved));
			var changed = D.Fingerprint(new[] { L(1, 20, 9.5m, 12) });
			Assert.Equal(D.DiscountState.NeedsApproval, D.StateOf(12, 7, changed, approved));
			// an approval for 10% does not cover 12%
			Assert.Equal(D.DiscountState.NeedsApproval, D.StateOf(12, 7, fp, new[] { new D.ApprovalFacts("Approved", 10, fp) }));
		}

		[Fact]
		public void Within_the_limit_needs_nothing_and_pending_or_rejected_do_not_clear()
		{
			var fp = "X";
			Assert.Equal(D.DiscountState.Within, D.StateOf(5, 7, fp, Array.Empty<D.ApprovalFacts>()));
			Assert.False(D.DiscountCleared(D.StateOf(12, 7, fp, new[] { new D.ApprovalFacts("Pending", 12, fp) })));
			Assert.False(D.DiscountCleared(D.StateOf(12, 7, fp, new[] { new D.ApprovalFacts("Rejected", 12, fp) })));
			Assert.True(D.DiscountCleared(D.DiscountState.NotLimited));
		}

		[Fact]
		public void Nobody_approves_their_own_discount()
		{
			Assert.False(D.MayApprove(5, 5, 12, 50, approverIsManager: true));
			Assert.True(D.MayApprove(6, 5, 12, 15, approverIsManager: false));
			Assert.False(D.MayApprove(6, 5, 20, 15, approverIsManager: false));
			Assert.True(D.MayApprove(6, 5, 20, null, approverIsManager: true));
		}

		[Fact]
		public void Unmet_lists_exactly_what_is_missing()
		{
			var f = new D.DealFacts(26900, null, 3, new HashSet<string> { "Influencer" }, HasNextStep: true, DiscountCleared: false, HasQuotation: false, FilledFields: new HashSet<int> { 4 });
			var unmet = D.Unmet(f, new[] { new D.Requirement("Amount"), new D.Requirement("CloseDate"), new D.Requirement("DecisionMaker"), new D.Requirement("NextStep"),
				new D.Requirement("Discount"), new D.Requirement("CustomField", 4), new D.Requirement("CustomField", 9) }).Select(r => r.Kind + r.FieldId).ToList();
			Assert.Equal(new[] { "CloseDate", "DecisionMaker", "Discount", "CustomField9" }, unmet);
		}
	}
}
