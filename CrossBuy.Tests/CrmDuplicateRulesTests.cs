using CrossBuy.BL.CrmIdentity;
using Xunit;
using R = CrossBuy.BL.CrmIdentity.CrmDuplicateRules;

namespace CrossBuy.Tests
{
	// CRM C1 — the duplicate rules are pure (no database), so they are pinned here directly.
	public class CrmDuplicateRulesTests
	{
		static R.Candidate C(int id, string name, string? phone = null, string? email = null, string? tax = null, int? account = null, string? en = null) =>
			new(id, name, en, phone, email, tax, account);

		[Fact]
		public void Arabic_spellings_fold_to_one_form()
		{
			Assert.Equal(R.Fold("أحمد إبراهيم"), R.Fold("احمد ابراهيم"));
			Assert.Equal(R.Fold("مستشفى"), R.Fold("مستشفي"));            // alef maqsura
			Assert.Equal(R.Fold("الــمــدرســة"), R.Fold("المدرسه"));   // tatweel and ta marbuta
		}

		[Fact]
		public void Legal_form_and_article_carry_no_identity()
		{
			Assert.Equal(string.Join(' ', R.NameTokens("شركة الخليج للمقاولات")), string.Join(' ', R.NameTokens("الخليج للمقاولات")));
			Assert.Equal(string.Join(' ', R.NameTokens("Nova Retail W.L.L")), string.Join(' ', R.NameTokens("Nova Retail")));
		}

		[Theory]
		[InlineData("+965 9900 1122")]
		[InlineData("00965-9900-1122")]
		[InlineData("99001122")]
		[InlineData("٩٩٠٠١١٢٢")]
		public void One_phone_number_in_any_spelling(string phone) => Assert.Equal("99001122", R.PhoneKey(phone));

		[Fact]
		public void A_short_number_is_not_a_phone_key() => Assert.Null(R.PhoneKey("1234"));

		[Fact]
		public void Same_name_after_legal_form_is_a_duplicate()
		{
			var m = R.Compare(C(1, "Nova Retail"), C(2, "Nova Retail W.L.L"));
			Assert.NotNull(m);
			Assert.Contains(R.Reason.SameName, m!.Reasons);
		}

		[Fact]
		public void Same_phone_and_similar_name_scores_high()
		{
			var m = R.Compare(C(1, "مجموعة مطاعم الديوان", "+965 2245 1100"), C(2, "مطاعم الديوان", "22451100"));
			Assert.NotNull(m);
			Assert.Contains(R.Reason.SamePhone, m!.Reasons);
			Assert.True(m.Score >= 80);
		}

		[Fact]
		public void A_shared_tax_number_alone_is_enough()
		{
			var m = R.Compare(C(1, "Alpha", tax: "310245567"), C(2, "Beta", tax: "310-245-567"));
			Assert.NotNull(m);
			Assert.Contains(R.Reason.SameTaxNumber, m!.Reasons);
		}

		[Fact]
		public void A_free_mailbox_domain_is_not_evidence()
		{
			Assert.Null(R.DomainKey("someone@gmail.com"));
			Assert.Null(R.Compare(C(1, "Alpha Trading", email: "a@gmail.com"), C(2, "Omega Foods", email: "b@gmail.com")));
		}

		[Fact]
		public void A_company_domain_plus_a_similar_name_is_a_duplicate()
		{
			var m = R.Compare(C(1, "Nova Retail", email: "sales@novaretail.kw"), C(2, "Nova Retail Group", email: "info@novaretail.kw"));
			Assert.NotNull(m);
			Assert.Contains(R.Reason.SameEmailDomain, m!.Reasons);
		}

		// Measured on CrossBuyDB2: 5 of 42 customer names were destroyed to "???? ?????" by a non-Unicode write.
		// Distinct customers that share only that wreckage must never look like one customer.
		[Fact]
		public void Corrupted_question_mark_names_are_never_a_match()
		{
			Assert.Empty(R.NameTokens("???? ?????"));
			Assert.Null(R.Compare(C(1, "???? ?????"), C(2, "???? ?????")));
			Assert.Empty(R.FindPairs(new[] { C(1, "???? ?????"), C(2, "???? ?????"), C(3, "?????") }));
		}

		[Fact]
		public void Two_people_sharing_a_name_in_different_accounts_are_not_proposed()
		{
			Assert.Null(R.Compare(C(1, "Mohammad Ali", account: 10), C(2, "Mohammad Ali", account: 20)));
			Assert.NotNull(R.Compare(C(1, "Mohammad Ali", account: 10), C(2, "Mohammad Ali", account: 10)));
		}

		[Fact]
		public void A_dismissed_pair_is_not_proposed_again()
		{
			var all = new[] { C(1, "Nova Retail"), C(2, "Nova Retail W.L.L"), C(3, "Pearl Hotel", "99887766"), C(4, "Pearl Hotel Kuwait", "99887766") };
			Assert.Equal(2, R.FindPairs(all).Count);
			var left = R.FindPairs(all, new HashSet<(int, int)> { (1, 2) });
			Assert.Single(left);
			Assert.Equal((3, 4), (left[0].LowId, left[0].HighId));
		}

		// A mobile already on file is enough to WARN while typing, but not enough to PROPOSE a merge.
		[Fact]
		public void A_shared_mobile_alone_warns_but_is_not_a_merge_proposal()
		{
			var a = C(1, "بدر المطيري", "+965 9900 1122"); var b = C(2, "فهد الصباح", "99001122", account: 7);
			Assert.Null(R.Compare(a, b));
			var e = R.Evidence(a, b);
			Assert.NotNull(e);
			Assert.True(R.SharesIdentifier(e!));
			Assert.False(R.SharesIdentifier(R.Evidence(C(1, "Nova Retail"), C(2, "Nova Retail"))!));
		}

		[Fact]
		public void Different_businesses_sharing_one_generic_word_are_not_duplicates() =>
			Assert.Null(R.Compare(C(1, "Gulf Contracting"), C(2, "Gulf Hospital")));
	}
}
