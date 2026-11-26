using Xunit;
using R = CrossBuy.BL.CrmIdentity.CrmServiceRules;

namespace CrossBuy.Tests
{
	// CRM C8 — matching articles to a ticket, and the satisfaction summary.
	public class CrmServiceRulesTests
	{
		static readonly R.Article Oven = new(1, "الفرن لا يسخن بعد التركيب", "فرن, حرارة, سخان", "تأكد من توصيل الكهرباء ثلاثي الطور ومن ضبط منظم الحرارة.");
		static readonly R.Article Fridge = new(2, "ثلاجة العرض تصدر صوتاً عالياً", "ثلاجه, ضجيج, كمبروسر", "نظّف شبك المكثف وتأكد من استواء الأرجل.");
		static readonly R.Article Invoice = new(3, "How to get a copy of an invoice", "invoice, copy, statement", "Open the client portal and choose Invoices.");

		[Fact]
		public void Words_fold_letters_drop_the_article_and_the_filler()
		{
			Assert.Equal(new[] { "فرن", "يسخن" }, R.Words("الفرن لا يسخن؟"));
			Assert.Contains("ثلاجه", R.Words("الثلاجة"));                 // ة → ه
			Assert.Contains("طابعه", R.Words("والطابعة"));                 // و + ال
			Assert.DoesNotContain("مشكله", R.Words("عندي مشكلة"));          // filler words carry no meaning
			Assert.Empty(R.Words("  ؟ ! "));
		}

		[Fact]
		public void The_article_whose_title_or_keywords_meet_the_ticket_comes_first()
		{
			var s = R.Suggest("فرن المطعم الجديد ما يسخن أبداً", new[] { Oven, Fridge, Invoice });
			Assert.Equal(1, s[0].id);
			Assert.DoesNotContain(s, x => x.id == 2);
			Assert.Equal(3, R.Suggest("I need a copy of last month's invoice", new[] { Oven, Fridge, Invoice })[0].id);
		}

		[Fact]
		public void A_clitic_still_meets_its_word_but_a_short_word_does_not_match_everything()
		{
			Assert.Equal(2, R.Suggest("ثلاجتنا فيها ضجيج", new[] { Oven, Fridge })[0].id);   // ضجيج = keyword
			Assert.Empty(R.Suggest("مرحبا", new[] { Oven, Fridge, Invoice }));
			Assert.Empty(R.Suggest("", new[] { Oven }));
		}

		[Fact]
		public void Csat_is_the_share_of_fours_and_fives()
		{
			var c = R.Summarise(new[] { 5, 4, 3, 1, 5 });
			Assert.Equal(5, c.Answers); Assert.Equal(3.6m, c.Average); Assert.Equal(60, c.SatisfiedPercent);
			Assert.Equal(new R.Csat(0, null, null), R.Summarise(new[] { 0, 6 }));
		}
	}
}
