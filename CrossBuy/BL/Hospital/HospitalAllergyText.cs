using System.Globalization;
using System.Text;

namespace CrossBuy.BL.Hospital
{
	/// <summary>
	/// A patient's allergies as the reader's language shows them. The file keeps ONE free-text field, usually typed in
	/// Arabic at the desk ("البنسلين (طفح جلدي)"), so an English or French screen showed Arabic in the middle of the banner.
	///
	/// This translates for DISPLAY ONLY, and only what it fully understands: an entry becomes English/French when its
	/// substance AND every word of its reaction are known; anything else is shown exactly as written. A half-translated
	/// allergy ("Penicillin (طفح شديد)") would be worse than the original, and a clinical warning must never lose words.
	/// The stored text, the allergy matching in pharmacy and radiology, and the Arabic screen are untouched.
	/// </summary>
	public static class HospitalAllergyText
	{
		private static readonly char[] Separators = { '\n', ',', '،', ';', '؛', '·' };

		// Substances, keyed by the folded Arabic spelling (no article, alef/teh-marbuta/yeh folded).
		private static readonly Dictionary<string, (string En, string Fr)> Substances = new()
		{
			["بنسلين"] = ("Penicillin", "Pénicilline"),
			["اموكسيسيلين"] = ("Amoxicillin", "Amoxicilline"),
			["اسبرين"] = ("Aspirin", "Aspirine"),
			["سلفا"] = ("Sulfa drugs", "Sulfamides"),
			["مضادات التهاب"] = ("NSAIDs", "AINS"),
			["ايبوبروفين"] = ("Ibuprofen", "Ibuprofène"),
			["كودايين"] = ("Codeine", "Codéine"),
			["مورفين"] = ("Morphine", "Morphine"),
			["صبغه يود"] = ("Iodine contrast", "Produit de contraste iodé"),
			["يود"] = ("Iodine", "Iode"),
			["لاتكس"] = ("Latex", "Latex"),
			["فول سوداني"] = ("Peanuts", "Arachides"),
			["مكسرات"] = ("Nuts", "Fruits à coque"),
			["بيض"] = ("Eggs", "Œufs"),
			["حليب"] = ("Milk", "Lait"),
			["ماكولات بحريه"] = ("Seafood", "Fruits de mer"),
			["سمك"] = ("Fish", "Poisson"),
			["قمح"] = ("Wheat", "Blé"),
			["لدغه نحل"] = ("Bee sting", "Piqûre d'abeille"),
		};

		// Reactions (inside the brackets), the same folding.
		private static readonly Dictionary<string, (string En, string Fr)> Reactions = new()
		{
			["طفح جلدي"] = ("rash", "éruption cutanée"),
			["طفح"] = ("rash", "éruption"),
			["ضيق تنفس"] = ("shortness of breath", "dyspnée"),
			["صدمه تحسسيه"] = ("anaphylaxis", "anaphylaxie"),
			["حكه"] = ("itching", "démangeaisons"),
			["تورم"] = ("swelling", "œdème"),
			["غثيان"] = ("nausea", "nausées"),
			["شري"] = ("hives", "urticaire"),
		};

		/// The allergies for the current UI language: Arabic as written; English/French term by term where fully known.
		public static string Display(string? text) => Display(text, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

		public static string Display(string? text, string lang)
		{
			if (string.IsNullOrWhiteSpace(text)) return "";
			if (lang == "ar") return text.Trim();
			bool fr = lang == "fr";
			var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(p => Translate(p, fr) ?? p)
				.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			return string.Join(", ", parts);
		}

		/// One entry, "substance (reaction)"; null when any of it is unknown (the caller keeps the original).
		public static string? Translate(string entry, bool fr)
		{
			if (!HasArabic(entry)) return entry;
			string main = entry, reaction = "";
			int open = entry.IndexOf('(');
			if (open >= 0)
			{
				int close = entry.IndexOf(')', open);
				main = entry[..open];
				reaction = close > open ? entry[(open + 1)..close] : entry[(open + 1)..];
			}
			if (!Substances.TryGetValue(Fold(main), out var s)) return null;
			string name = fr ? s.Fr : s.En;
			if (string.IsNullOrWhiteSpace(reaction)) return name;
			var r = TranslateReaction(reaction, fr);
			return r == null ? null : name + " (" + r + ")";
		}

		// "طفح جلدي وضيق تنفس": each piece joined by و must be known.
		private static string? TranslateReaction(string reaction, bool fr)
		{
			var pieces = new List<string>();
			foreach (var raw in Fold(reaction).Split(" و", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var piece = raw.StartsWith('و') && !Reactions.ContainsKey(raw) ? raw[1..] : raw;
				if (!Reactions.TryGetValue(piece, out var t)) return null;
				pieces.Add(fr ? t.Fr : t.En);
			}
			return pieces.Count == 0 ? null : string.Join(fr ? " et " : " and ", pieces);
		}

		private static bool HasArabic(string s) => s.Any(c => c >= '؀' && c <= 'ۿ');

		/// Folds the spellings a desk types for one word: diacritics and tatweel off, alef forms and teh marbuta and
		/// alef maqsura unified, and the article dropped from every word ("الفول السوداني" = "فول سوداني").
		public static string Fold(string s)
		{
			var sb = new StringBuilder(s.Length);
			foreach (var c in s.Trim())
			{
				if (c is >= 'ً' and <= 'ٟ' or 'ـ') continue;
				sb.Append(c switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', _ => c });
			}
			var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Select(w => w.Length > 3 && w.StartsWith("ال") ? w[2..] : w);
			return string.Join(' ', words);
		}
	}
}
