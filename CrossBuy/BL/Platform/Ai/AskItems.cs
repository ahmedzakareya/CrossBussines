using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// ITEM LOOKUP, IN ONE PLACE.
	//
	// Three questions take an item name — stock, cost, movements — and each one had its own copy of
	// "rank the catalogue, decide what counts as a match, build a did-you-mean". Three copies is
	// three chances for them to disagree about what a word means, and two of them to miss a fix.
	//
	// WHY A SUGGESTION IS A QUESTION AND NOT A RESULT. The near misses come back as text the person
	// can SEND, not as rows presented as the answer. Clicking one asks it, and it then goes through
	// the whole gate again like anything else typed. The assistant never acts on its own guess; it
	// only saves the typing.
	// =================================================================================================
	public static class AskItems
	{
		/// The few fields a lookup needs. Named to match the entity so handlers read the same way
		/// whether they hold a Hit or an Item.
		public readonly record struct Hit(int ID, string ItemCode, string? Name, string? NameEn)
		{
			public string Display(bool arabic) =>
				(arabic ? Name : (NameEn ?? Name)) ?? ItemCode;

			/// What goes back into the parameter next turn: the STORED spelling, which scores an
			/// exact match, not whatever the person typed.
			public string Subject => string.IsNullOrWhiteSpace(Name) ? ItemCode : Name!;
		}

		/// The phrasings a suggestion is offered in. Each must be a question THIS matcher can
		/// actually answer — offering one that gets refused is worse than offering nothing.
		public static string StockQuestion(string name, bool ar) =>
			ar ? "كام رصيد " + name : "stock of " + name;

		public static string ValueQuestion(string name, bool ar) =>
			ar ? "كام تكلفة " + name : "cost of " + name;

		public static string MovementsQuestion(string name, bool ar) =>
			ar ? "اخر حركات " + name : "last movements of " + name;

		public static List<AskSuggestion> Offer(
			IEnumerable<Hit> hits, bool arabic, Func<string, bool, string> phrasing) =>
			hits.Select(h => h.Display(arabic))
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(n => new AskSuggestion { Label = n, Question = phrasing(n, arabic) })
				.ToList();

		/// Said out loud whenever the answer is about a different spelling from the one typed.
		public static AskText Corrected(Hit h) =>
			AskText.Of("فهمت أنك تقصد: " + h.Display(true), "Taken to mean: " + h.Display(false));

		/// A NAME MADE OF QUESTION MARKS IS NOT A NAME.
		///
		/// Five of this company's customers have their Arabic name stored as literal "?" characters
		/// — a non-Unicode write that destroyed the text on the way in, not a rendering problem, so
		/// the original letters are gone. Their English names survived.
		///
		/// Showing "???? ?????" beside a figure of 1,249,774 tells the reader nothing and looks like
		/// a bug in the assistant rather than a defect in the row. Falling back to the other language
		/// shows a real name. It does NOT hide the problem: the corruption is counted by the data
		/// readiness check, where a fix can be decided rather than guessed at here, because the
		/// letters cannot be recovered from what is stored.
		public static string? Readable(string? preferred, string? fallback)
		{
			if (!string.IsNullOrWhiteSpace(preferred))
			{
				var meaningful = preferred.Count(c => c != '?' && !char.IsWhiteSpace(c));
				if (meaningful > 0) return preferred;
			}
			return string.IsNullOrWhiteSpace(fallback) ? preferred : fallback;
		}

		public static AskSubject SubjectOf(Hit h, bool arabic) => new()
		{
			Kind = "item",
			Value = h.Subject,
			Display = h.Display(arabic),
		};

		/// Rank a catalogue against what was typed, highest first.
		///
		/// BOTH DIRECTIONS, AND THE STRONGER EVIDENCE WINS. The extracted parameter is matched
		/// against each name as before; and, when the original question is available, each name is
		/// also looked for INSIDE the question. The second direction needs no list of words to
		/// strip, so it survives a wording nobody anticipated - which is the case where the first
		/// one quietly fails while the intent was understood perfectly.
		public static List<AskSearch.Scored<Hit>> Rank<T>(
			IEnumerable<T> catalogue, string query, Func<T, Hit> toHit, string? question = null)
		{
			var askedWhole = !string.IsNullOrWhiteSpace(question);
			return catalogue.Select(toHit)
				.Select(h => new AskSearch.Scored<Hit>(h, Math.Max(
					AskSearch.Score(query, h.ItemCode, h.Name, h.NameEn),
					askedWhole ? AskSearch.NameInQuestion(question!, h.ItemCode, h.Name, h.NameEn) : 0)))
				.Where(x => x.Score > 0)
				.OrderByDescending(x => x.Score)
				.ThenBy(x => x.Item.ItemCode, StringComparer.Ordinal)
				.ToList();
		}
	}
}
