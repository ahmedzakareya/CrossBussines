using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// Text normalisation and item scoring, in ONE place.
	//
	// WHY NORMALISING ONLY ONE SIDE IS WORSE THAN NOT NORMALISING AT ALL. The first version folded the
	// QUESTION (ة→ه, diacritics stripped) and then searched the stored names, which are not folded.
	// "مياه معدنية" became "مياه معدنيه" and matched a catalogue that still reads "معدنية" — never,
	// for any item. A comparison is only meaningful when both sides went through the same function,
	// and that is what this class exists to guarantee.
	//
	// IT RUNS IN MEMORY ON PURPOSE. Folding inside SQL would mean a per-row function and no index, and
	// the catalogue here is 237 items — small enough that loading code, name and English name costs
	// less than the LIKE chain it replaces, while allowing scoring that SQL could not express.
	// =================================================================================================
	public static class AskSearch
	{
		/// Arabic is written several ways for the same word. Fold both sides of every comparison.
		public static string Normalise(string? s)
		{
			if (string.IsNullOrWhiteSpace(s)) return string.Empty;
			var sb = new StringBuilder(s.Length);
			foreach (var ch in s)
			{
				if (ch >= 'ً' && ch <= 'ْ') continue;   // tashkeel
				if (ch == 'ـ') continue;                      // tatweel
				sb.Append(ch switch
				{
					'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',   // أ إ آ ٱ -> ا
					'ى' => 'ي',                                        // ى -> ي
					'ة' => 'ه',                                        // ة -> ه
					'ؤ' => 'و',                                        // ؤ -> و
					'ئ' => 'ي',                                        // ئ -> ي
					// Arabic-Indic digits read as their Latin equivalents, so "٦٠٠" finds "600".
					>= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
					>= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
					_ => char.ToLowerInvariant(ch),
				});
			}
			return sb.ToString();
		}

		public static string[] Tokens(string? s) =>
			Normalise(s).Split(new[]
					{
						' ', '\t', '\n', '\r', '-', '_', '/', '\\', '،', ',', '.',
						// Punctuation separates words; it is never a word of its own. The first list
						// omitted the Arabic question mark, so "؟" survived tokenisation as a search
						// term that can never match anything — and one unmatchable token drops an
						// otherwise complete match from 400+ into the "did you mean" band, below the bar.
						'؟', '?', '!', ':', ';', '؛', '(', ')', '[', ']', '\"', '\'',
					},
					StringSplitOptions.RemoveEmptyEntries)
				.Where(t => t.Length > 0)
				.ToArray();

		/// One candidate and how well it answers the query.
		public readonly record struct Scored<T>(T Item, int Score);

		/// Rank candidates against a query. Higher is better; 0 means no relation at all.
		///
		/// The ladder is deliberate rather than a single Contains: a person typing an exact code
		/// means THAT item, and should not be handed five partial matches first.
		///   1000  the code, exactly
		///    600  the code or a name begins with the query
		///    400  every query word appears somewhere
		///    300  every query word appears OR is a keystroke or two from a word that does
		///  1..299 some words appear — enough to offer as "did you mean", never as an answer
		public static int Score(string query, params string?[] fields)
		{
			// BOTH SIDES IN THE SAME SHAPE, or the comparison is about the punctuation rather than
			// about the words. Comparing a raw normalised query against a raw normalised field let a
			// trailing "؟" demote a prefix match from 600 to 412 -- the verdict survived, but the
			// ORDER did not, so a less apt item could outrank the right one because of a keystroke
			// that carries no meaning. Tokenising both sides removes the punctuation from both.
			var qTokens = Tokens(query);
			if (qTokens.Length == 0) return 0;
			var q = string.Join(' ', qTokens);

			var normFields = fields.Where(f => !string.IsNullOrWhiteSpace(f))
				.Select(f => string.Join(' ', Tokens(f))).Where(f => f.Length > 0).ToArray();
			if (normFields.Length == 0) return 0;

			if (normFields.Any(f => f == q)) return 1000;
			if (normFields.Any(f => f.StartsWith(q, StringComparison.Ordinal))) return 600;

			var hits = qTokens.Count(t => normFields.Any(f => f.Contains(t, StringComparison.Ordinal)));
			if (hits == qTokens.Length) return 400 + Math.Min(99, q.Length);

			// A TYPO IS NOT A DIFFERENT QUESTION. "برجر داج" is one keystroke from "برجر دجاج", and
			// refusing it teaches people that the assistant is brittle rather than that they
			// mistyped. Scored in its own band BELOW a literal all-words match, so a caller can
			// still tell "this is what you wrote" from "this is what I think you meant" — and so a
			// near match never outranks an exact one when both exist.
			var fieldTokens = normFields.SelectMany(Tokens).Distinct().ToArray();
			var near = qTokens.Count(t => normFields.Any(f => f.Contains(t, StringComparison.Ordinal))
				|| fieldTokens.Any(ft => NearEnough(t, ft)));
			if (near == qTokens.Length) return NearThreshold + Math.Min(49, q.Length);

			if (hits == 0) return 0;

			// A PARTIAL MATCH IS NOT AN ANSWER. Scored below both floors so a caller can tell them
			// apart and offer them as a question rather than present them as a result.
			return (int)Math.Round(hits * 299.0 / qTokens.Length);
		}

		/// Is `typed` plausibly a misspelling of `stored`?
		///
		/// Deliberately mean about short words: at three or four letters, one edit away is a
		/// DIFFERENT Arabic word far more often than it is a slip, and a matcher that cannot tell
		/// those apart answers confidently about the wrong thing. Below three letters, never.
		public static bool NearEnough(string typed, string stored)
		{
			if (typed.Length < 3 || stored.Length < 3) return false;
			if (Math.Abs(typed.Length - stored.Length) > 2) return false;
			var budget = Math.Max(typed.Length, stored.Length) <= 5 ? 1 : 2;
			return Distance(typed, stored, budget) <= budget;
		}

		/// Levenshtein, abandoned as soon as it exceeds `max`. Nobody needs the true distance
		/// between unrelated words, and bounding it keeps a whole-catalogue scan cheap.
		public static int Distance(string a, string b, int max)
		{
			if (a == b) return 0;
			if (Math.Abs(a.Length - b.Length) > max) return max + 1;
			var prev = new int[b.Length + 1];
			var cur = new int[b.Length + 1];
			for (var j = 0; j <= b.Length; j++) prev[j] = j;
			for (var i = 1; i <= a.Length; i++)
			{
				cur[0] = i;
				var rowBest = cur[0];
				for (var j = 1; j <= b.Length; j++)
				{
					var cost = a[i - 1] == b[j - 1] ? 0 : 1;
					cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
					if (cur[j] < rowBest) rowBest = cur[j];
				}
				// Every later row is at least this good, so once a whole row is over budget the
				// final answer is too.
				if (rowBest > max) return max + 1;
				(prev, cur) = (cur, prev);
			}
			return prev[b.Length];
		}

		/// DOES THIS CANDIDATE'S NAME APPEAR INSIDE THE QUESTION?
		///
		/// The other direction, and it is the one that survives an unexpected wording.
		///
		/// Everything above asks "does the query match this name", which works while the query IS
		/// the name. It stops working the moment the parameter is extracted by subtraction — strip
		/// the keywords, strip the stopwords, hope what is left is a name — because an unanticipated
		/// phrasing leaves residue. "بكام اشترينا لابتوب Dell" yielded "اشترينا لابتوب Dell", which
		/// matches two of its three words against "لابتوب Dell" and lands below the bar. The intent
		/// was understood perfectly; the item was lost to a word nobody thought to list.
		///
		/// Asking the question the other way round needs no list at all: EVERY word of the stored
		/// name must appear somewhere in what the person typed. Extra words in the question cost
		/// nothing, because they are not being matched against anything.
		///
		/// It stays precise because the requirement is ALL of the name's words, not some. That is
		/// also what disambiguates: "ميه معدنيه فيه كام" contains every word of "مياه معدنية" and
		/// only half of "مياه معدنية 600 مل", so the shorter name wins on evidence rather than on
		/// a tie-break.
		public static int NameInQuestion(string question, params string?[] fields)
		{
			var qTokens = Tokens(question);
			if (qTokens.Length == 0) return 0;
			var qSet = new HashSet<string>(qTokens, StringComparer.Ordinal);

			var best = 0;
			foreach (var f in fields)
			{
				if (string.IsNullOrWhiteSpace(f)) continue;
				var nameTokens = Tokens(f);
				if (nameTokens.Length == 0) continue;

				// A ONE-WORD NAME MUST BE A REAL WORD. "مل" or "kg" turns up inside half the
				// sentences anyone types, and a name that short matching a long question is a
				// coincidence rather than a reference.
				if (nameTokens.Length == 1 && nameTokens[0].Length < 4) continue;

				var present = nameTokens.Count(t =>
					qSet.Contains(t) || qTokens.Any(q => NearEnough(q, t)));
				if (present != nameTokens.Length) continue;

				// Scored just inside the answerable band, and by the NAME's length rather than the
				// question's: a longer name found whole is stronger evidence, while a longer
				// question is merely a longer question.
				var joined = string.Join(' ', nameTokens);
				best = Math.Max(best, AnswerThreshold + Math.Min(99, joined.Length));
			}
			return best;
		}

		/// The floor at which a match may be ANSWERED rather than merely suggested.
		public const int AnswerThreshold = 400;

		/// The floor at which a match may be OFFERED as a correction — answered only when it is the
		/// single candidate in this band, and always with the correction stated.
		public const int NearThreshold = 300;

		/// WHAT TO DO WITH A RANKED LIST, in one place so every handler decides it the same way.
		///
		/// Three outcomes, and the middle one is the point:
		///   - something matched literally -> answer it;
		///   - nothing did, but exactly ONE candidate is a keystroke away -> answer THAT, and the
		///     caller must say which, because an unannounced correction is a wrong answer that
		///     looks right;
		///   - several are close, or none is -> answer nothing and offer them.
		/// `ranked` must already be ordered by score, highest first.
		public static (List<T> Answer, bool Corrected, List<T> Offer) Resolve<T>(
			IReadOnlyList<Scored<T>> ranked, int max)
		{
			var answer = ranked.Where(x => x.Score >= AnswerThreshold).Take(max)
				.Select(x => x.Item).ToList();
			if (answer.Count > 0) return (answer, false, new List<T>());

			var close = ranked.Where(x => x.Score >= NearThreshold).ToList();
			if (close.Count == 1) return (new List<T> { close[0].Item }, true, new List<T>());

			var offer = (close.Count > 0 ? close : ranked.Where(x => x.Score > 0).ToList())
				.Take(3).Select(x => x.Item).ToList();
			return (new List<T>(), false, offer);
		}
	}
}
