namespace CrossBuy.BL.CrmIdentity
{
	/// CRM C8 — which article answers a ticket, and what a satisfaction score adds up to. No database.
	/// Shadow-tested in CrmServiceRulesTests.
	public static class CrmServiceRules
	{
		// words that say nothing about the problem
		static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
		{
			"في", "من", "على", "الى", "عن", "مع", "او", "ثم", "هل", "لا", "ما", "لم", "لن", "قد", "كيف", "متى", "لماذا", "هذا", "هذه", "ذلك", "تلك", "التي", "الذي",
			"انا", "نحن", "انت", "عند", "عندي", "عندنا", "يوجد", "مشكله", "مشكلة", "ارجو", "الرجاء", "شكرا", "لو", "سمحت", "بعد", "قبل", "كل", "اي",
			"the", "and", "for", "with", "not", "can", "how", "why", "what", "when", "this", "that", "have", "has", "our", "your", "you", "are", "was", "please", "problem", "issue",
		};

		/// The words of a text, folded (Arabic letter forms, diacritics, case) and stripped of the definite article,
		/// so "الطابعة" and "طابعه" meet. Two-letter words carry too little to match on.
		public static List<string> Words(string? text)
		{
			var list = new List<string>();
			var folded = CrmDuplicateRules.Fold(text);
			var buf = new System.Text.StringBuilder();
			foreach (var ch in folded + " ")
			{
				if (char.IsLetterOrDigit(ch)) { buf.Append(ch); continue; }
				if (buf.Length == 0) continue;
				var w = buf.ToString(); buf.Clear();
				if (w.Length > 4 && (w.StartsWith("وال", StringComparison.Ordinal) || w.StartsWith("بال", StringComparison.Ordinal) || w.StartsWith("لل", StringComparison.Ordinal) && w.Length > 4)) w = w.StartsWith("لل") ? w[2..] : w[3..];
				else if (w.Length > 3 && w.StartsWith("ال", StringComparison.Ordinal)) w = w[2..];
				if (w.Length < 3 || Stop.Contains(w)) continue;
				list.Add(w);
			}
			return list.Distinct().ToList();
		}

		/// One word of the question against one word of the article: equal, or one contains the other when the shorter
		/// has four letters or more — enough for Arabic clitics ("طابعتي", "وطابعه") without "ال" matching everything.
		static bool Meets(string q, string a) => q == a || (Math.Min(q.Length, a.Length) >= 4 && (a.Contains(q, StringComparison.Ordinal) || q.Contains(a, StringComparison.Ordinal)));

		public sealed record Article(int Id, string Title, string? Keywords, string Body);

		/// How well an article answers this text: a word of the title or the keywords is worth 3, of the body 1, each
		/// word of the question counted once.
		public static int Score(IReadOnlyList<string> question, Article a)
		{
			var title = Words(a.Title); var keys = Words(a.Keywords); var body = Words(a.Body);
			var score = 0;
			foreach (var q in question)
			{
				if (title.Any(t => Meets(q, t)) || keys.Any(k => Meets(q, k))) score += 3;
				else if (body.Any(b => Meets(q, b))) score += 1;
			}
			return score;
		}

		/// The best few articles for this text; nothing below a single title/keyword hit is offered.
		public static List<(int id, int score)> Suggest(string? text, IEnumerable<Article> articles, int take = 3)
		{
			var q = Words(text);
			if (q.Count == 0) return new();
			return articles.Select(a => (a.Id, Score(q, a))).Where(x => x.Item2 >= 3).OrderByDescending(x => x.Item2).ThenByDescending(x => x.Id).Take(take).ToList();
		}

		/// CSAT the usual way: the share of answers that are 4 or 5, with the plain average beside it.
		public sealed record Csat(int Answers, decimal? Average, int? SatisfiedPercent);
		public static Csat Summarise(IEnumerable<int> scores)
		{
			var s = scores.Where(x => x is >= 1 and <= 5).ToList();
			if (s.Count == 0) return new Csat(0, null, null);
			return new Csat(s.Count, Math.Round((decimal)s.Average(), 1), (int)Math.Round(s.Count(x => x >= 4) * 100.0 / s.Count));
		}
	}
}
