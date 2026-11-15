using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// COMPOSING A QUERY WITHOUT A MODEL — the fast, exact half.
	//
	// WHY THIS EXISTS. The language model composes correctly: all nine test questions mapped to the
	// right grouping and measure, including "اقل الاصناف مبيعا" as ascending and "بعنا كام اجمالا"
	// as ungrouped. It also took a median of twelve seconds, which is not a chat box; it is a form
	// submission that looks broken. For the TAIL of a question nobody anticipated that is a fair
	// price, because the alternative is failing. For "ايه الايراد لكل شهر" it is twelve seconds
	// spent on a question that a word match answers exactly.
	//
	// So the same shape as the intent chain: cheap and exact first, expensive and general second.
	// This resolves the question only when the words are unambiguous and returns null otherwise —
	// it never guesses, because a guess here costs the same as the model's answer and arrives
	// without the model's judgement.
	//
	// THE VOCABULARY IS THE SURFACE'S, NOT THIS CLASS'S. Every word comes from a column's name,
	// label or declared synonyms, so a surface added later brings its own words and this code does
	// not grow. That is the difference between a composer and a second place to hard-code domain
	// knowledge.
	// =================================================================================================
	public sealed class AskRuleComposer : IAskQueryComposer
	{
		/// Words that reverse the ordering. Everything else is "the biggest first", which is what
		/// almost every business question wants and what a reader assumes when nothing says otherwise.
		private static readonly string[] Ascending =
		{
			"اقل", "أقل", "اصغر", "أصغر", "ادنى", "أدنى", "اضعف", "أضعف", "الاقل", "الأقل",
			"lowest", "least", "smallest", "worst", "bottom", "fewest", "ascending",
		};

		/// Words that ask HOW SOMETHING CHANGED rather than what it is.
		///
		/// Note the overlap with Recency on purpose: "اللي فات" appears in both, because "اخر فاتورة"
		/// (the last invoice) and "مقارنة بالشهر اللي فات" (compared with last month) share it. The
		/// two are told apart by what ELSE is in the sentence — Recency only wins when no measure
		/// word is present, and a comparison always has one.
		private static readonly string[] Comparison =
		{
			"مقارنة", "مقارنه", "بالمقارنة", "مقارنا", "مقابل", "قارن", "قارنلي",
			"عن الشهر اللي فات", "عن الشهر الماضي", "عن الشهر السابق", "من الشهر اللي فات",
			"زاد ولا قل", "زادت ولا قلت", "طلع ولا نزل", "احسن ولا اوحش", "الفرق عن",
			"نسبة التغير", "التغير", "تغير عن", "زيادة ولا نقص",
			"compared to", "compared with", "versus", " vs ", "vs last", "against last",
			"month over month", "change from", "change versus", "up or down",
			"better or worse", "growth", "decline",
		};

		/// Words that ask for a RECORD rather than a total: "اخر فاتورة", "the latest order". When
		/// one of these is present and no measure word is, the question wants rows.
		private static readonly string[] Recency =
		{
			"اخر", "آخر", "احدث", "أحدث", "اخيرة", "الاخيرة", "الاخير", "اخرها", "اللي فات",
			"last", "latest", "most recent", "recent", "newest",
		};

		/// The column a record query orders by: a date if the surface has one, otherwise nothing and
		/// the question is handed on. Ordering is required precisely so a record list cannot be
		/// unordered — an unordered "last one" is not an answer.
		private static AskColumn? Newest(AskSurface surface) =>
			surface.Columns.FirstOrDefault(c => c.Kind == AskColumnKind.Dimension
				&& (c.Name == "date" || c.Name.EndsWith("date", StringComparison.OrdinalIgnoreCase)));

		public Task<AskComposedQuery?> ComposeAsync(
			string question, AskSurface surface, CancellationToken ct = default)
		{
			if (surface == null || string.IsNullOrWhiteSpace(question))
				return Task.FromResult<AskComposedQuery?>(null);

			var measure = Best(question, surface.Columns.Where(c => c.Kind == AskColumnKind.Measure));
			var dimension = Best(question, surface.Columns.Where(c => c.Kind == AskColumnKind.Dimension));

			var folded0 = AskSearch.Normalise(question);

			// A COMPARISON IS NEVER A RECORD LIST, and the two share words.
			//
			// "حركة المخزون مقارنة بالشهر اللي فات" contains "اللي فات", which is in Recency, and
			// matched no measure once the bare word "الحركات" was taken off the measure — so it
			// became "the last 5 movements". A list of five rows is not an answer to "how does it
			// compare". A comparison word present anywhere in the sentence settles it: the asker
			// wants two periods weighed, not recent rows.
			var asksComparison = surface.Column("month") != null
				&& Comparison.Any(c => folded0.Contains(AskSearch.Normalise(c), StringComparison.Ordinal));

			// A RECORD, NOT A TOTAL. "اخر فاتورة بيع" names no measure and asks for rows; answering
			// it with a sum of anything would be answering a different question.
			if (measure == null && !asksComparison
				&& Recency.Any(w => folded0.Contains(AskSearch.Normalise(w), StringComparison.Ordinal)))
			{
				var newest = Newest(surface);
				if (newest != null)
					return Task.FromResult<AskComposedQuery?>(new AskComposedQuery(
						GroupBy: null,
						Measure: "",                       // no measure: this is a record query
						Descending: true,
						Limit: LimitIn(question) == 10 ? 5 : LimitIn(question),
						Engine: "rules:record",
						Raw: $"record | order {newest.Name} desc | {LimitIn(question)}"));
			}

			// A DIMENSION WITH NO MEASURE IS STILL A QUESTION. "مين اكبر زباين عندنا" names
			// customers and no measure, and obviously means by the surface primary one. Declining
			// it sent the question to the model, which on this machine meant seconds, and then to
			// the default query, which answered about ITEMS - a different question entirely.
			//
			// The surface declares its columns in order, so the first measure is the primary one.
			// This is an assumption, so the caller states it: see the answer note.
			if (measure == null && dimension != null)
				measure = surface.Columns.FirstOrDefault(c => c.Kind == AskColumnKind.Measure);

			// Neither a measure nor a dimension means this vocabulary does not cover the question.
			// Handing it on is correct: the model may recognise a wording nobody declared.
			if (measure == null) return Task.FromResult<AskComposedQuery?>(null);

			var folded = AskSearch.Normalise(question);
			var descending = !Ascending.Any(a =>
				folded.Contains(AskSearch.Normalise(a), StringComparison.Ordinal));

			// A COMPARISON IS ONLY CLAIMED WHEN THE SURFACE CAN HONOUR IT. Asking a surface with no
			// month dimension how something changed has no answer, and setting the flag anyway would
			// produce a confident table with an empty comparison column.
			var compare = asksComparison;

			// THE MONTH IS THE AXIS OF A COMPARISON, NOT A GROUPING OF IT.
			//
			// "المبيعات مقارنة بالشهر اللي فات" contains "الشهر", so the dimension matcher chose
			// `month` — and the answer came back as revenue grouped by month, which is the ordinary
			// answer wearing the question's words. Comparing months while grouped BY month compares
			// each period with itself. When a comparison is asked and the dimension chosen is the
			// time axis, the grouping is dropped: one total now against one total before, which is
			// exactly what the question asks.

			var groupBy = dimension?.Name;
			if (compare && string.Equals(groupBy, "month", StringComparison.OrdinalIgnoreCase))
				groupBy = null;

			return Task.FromResult<AskComposedQuery?>(new AskComposedQuery(
				GroupBy: groupBy,
				Measure: measure.Name,
				Descending: descending,
				Limit: LimitIn(question),
				Engine: "rules",
				Raw: $"{dimension?.Name ?? "none"} | {measure.Name} | {(descending ? "desc" : "asc")} | {LimitIn(question)}"
					+ (compare ? " | compare" : ""),
				Compare: compare));
		}

		/// The column whose vocabulary the question matches most specifically, or null when nothing
		/// matches. Longest match wins: "قيمة مخزون" is a more specific claim than "مخزون", and the
		/// longer phrase is the one the asker chose.
		private static AskColumn? Best(string question, IEnumerable<AskColumn> columns)
		{
			var folded = AskSearch.Normalise(question);
			AskColumn? best = null;
			var bestLength = 0;

			foreach (var col in columns)
			{
				foreach (var word in Vocabulary(col))
				{
					var w = AskSearch.Normalise(word);
					if (w.Length == 0) continue;

					// WHOLE WORDS ONLY. A substring match makes "عدد" fire inside "معدنية" and
					// "cost" inside "costume", which is how a matcher starts answering confidently
					// about the wrong column.
					if (!ContainsWord(folded, w)) continue;

					if (w.Length > bestLength)
					{
						best = col;
						bestLength = w.Length;
					}
				}
			}
			return best;
		}

		private static IEnumerable<string> Vocabulary(AskColumn col)
		{
			yield return col.Name;
			if (!string.IsNullOrWhiteSpace(col.Label.Ar)) yield return col.Label.Ar;
			if (!string.IsNullOrWhiteSpace(col.Label.En)) yield return col.Label.En;
			foreach (var s in col.Synonyms) yield return s;
		}

		/// ARABIC GLUES ITS PARTICLES ON, so a strict boundary is the wrong boundary.
		///
		/// "ارباح" is declared as a synonym of the margin measure and "الارباح كام" still composed
		/// nothing, because the ل of the definite article sits immediately before the word and a
		/// letter-boundary test reads that as the middle of some longer word. The question then fell
		/// through to the surface's default and answered REVENUE by item — the right surface, the
		/// wrong measure, and no sign on screen that anything had been missed.
		///
		/// Listing "الارباح" beside "ارباح" would have fixed that one word and left the next fifty.
		/// These proclitics are a closed set in Arabic, so the boundary itself is what should know
		/// about them: ال، وال، بال، فال، كال، لل، and the single-letter و ب ف ك ل.
		private static readonly string[] Proclitics =
			{ "وبال", "فبال", "وال", "بال", "فال", "كال", "لل", "ال", "و", "ب", "ف", "ك", "ل" };

		/// A word boundary check that works for a phrase as well as a single word.
		private static bool ContainsWord(string haystack, string needle)
		{
			var from = 0;
			while (true)
			{
				var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
				if (at < 0) return false;
				var beforeOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
				// ...or everything between this word and the real boundary is one of those particles.
				if (!beforeOk)
					foreach (var p in Proclitics)
					{
						if (at < p.Length) continue;
						if (string.CompareOrdinal(haystack, at - p.Length, p, 0, p.Length) != 0) continue;
						var before = at - p.Length;
						if (before == 0 || !char.IsLetterOrDigit(haystack[before - 1])) { beforeOk = true; break; }
					}
				var afterAt = at + needle.Length;
				var afterOk = afterAt >= haystack.Length || !char.IsLetterOrDigit(haystack[afterAt]);
				if (beforeOk && afterOk) return true;
				from = at + 1;
			}
		}

		/// A row count the asker named, snapped to what the surface layer allows. Arabic-Indic
		/// digits are folded to Latin by Normalise before this sees them.
		private static int LimitIn(string question)
		{
			var folded = AskSearch.Normalise(question);
			var digits = new string(folded.Where(char.IsDigit).ToArray());
			if (digits.Length is > 0 and <= 3 && int.TryParse(digits, NumberStyles.None,
					CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= AskQueryValidator.MaxLimit)
				return n;

			// Spelled out, in the few sizes anyone actually asks for.
			if (folded.Contains("عشرين", StringComparison.Ordinal)
				|| folded.Contains("twenty", StringComparison.Ordinal)) return 20;
			if (folded.Contains("خمسه", StringComparison.Ordinal)
				|| folded.Contains("five", StringComparison.Ordinal)) return 5;
			return 10;
		}
	}

	// =================================================================================================
	// RULES FIRST, MODEL SECOND — and the model is only reached when the rules decline.
	//
	// The composers fail in opposite directions, which is what makes the order worth having: the
	// rules are exact where the surface's own vocabulary covers the wording and silent everywhere
	// else; the model generalises to a wording nobody declared and costs seconds. Asking the cheap
	// one first means the common question is instant and the unusual one is still answered.
	// =================================================================================================
	public sealed class ChainedQueryComposer : IAskQueryComposer
	{
		private readonly IReadOnlyList<IAskQueryComposer> _links;

		public ChainedQueryComposer(IEnumerable<IAskQueryComposer> links)
			=> _links = (links ?? throw new ArgumentNullException(nameof(links)))
				.Where(l => l is not ChainedQueryComposer)
				.ToList();

		public async Task<AskComposedQuery?> ComposeAsync(
			string question, AskSurface surface, CancellationToken ct = default)
		{
			foreach (var link in _links)
			{
				var composed = await link.ComposeAsync(question, surface, ct);
				if (composed != null) return composed;
			}
			return null;
		}
	}
}
