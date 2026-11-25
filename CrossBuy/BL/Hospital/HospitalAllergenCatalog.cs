using System.Text.Json;

namespace CrossBuy.BL.Hospital
{
	/// One allergy as the form sends it and the file lists it: a catalogue code (or null with free text), what happened,
	/// how bad. Code "NKA" alone = no known allergy.
	public sealed class HospitalAllergyItem
	{
		public string? Code { get; set; }
		public string? Text { get; set; }
		public string? Reaction { get; set; }
		public string? Severity { get; set; }
	}

	public sealed record HospitalAllergen(string Code, string Kind, string Ar, string En, string Fr);

	/// <summary>
	/// HOSPITAL H270 — the allergens a desk picks from, and the rules of the coded list. Each drug allergen's Arabic name
	/// is a word HospitalAllergyMatch knows as its family, so the text written from the list always meets the drugs of that
	/// family (a test holds every drug entry to it). The reactions are the words HospitalAllergyText translates, so the
	/// text reads in English and French too.
	/// </summary>
	public static class HospitalAllergenCatalog
	{
		public const string NoKnownAllergy = "NKA";
		public const string Drug = "Drug", Food = "Food", Environment = "Environment";
		public const string Mild = "Mild", Moderate = "Moderate", Severe = "Severe";
		public static readonly IReadOnlyList<string> Severities = new[] { Mild, Moderate, Severe };

		public static readonly IReadOnlyList<HospitalAllergen> All = new HospitalAllergen[]
		{
			new("DRUG-PEN", Drug, "بنسلين", "Penicillin", "Pénicilline"),
			new("DRUG-CEPH", Drug, "سيفالوسبورينات", "Cephalosporins", "Céphalosporines"),
			new("DRUG-SULFA", Drug, "سلفا", "Sulfa drugs", "Sulfamides"),
			new("DRUG-ASA", Drug, "أسبرين", "Aspirin", "Aspirine"),
			new("DRUG-NSAID", Drug, "مضادات الالتهاب غير الستيرويدية", "NSAIDs", "AINS"),
			new("DRUG-OPIOID", Drug, "المسكنات الأفيونية", "Opioids", "Opioïdes"),
			new("DRUG-MACRO", Drug, "ماكروليدات", "Macrolides", "Macrolides"),
			new("DRUG-QUIN", Drug, "كينولونات", "Quinolones", "Quinolones"),
			new("DRUG-TETRA", Drug, "تتراسيكلين", "Tetracyclines", "Tétracyclines"),
			new("DRUG-IODINE", Drug, "صبغة اليود", "Iodine contrast", "Produit de contraste iodé"),
			new("FOOD-PEANUT", Food, "فول سوداني", "Peanuts", "Arachides"),
			new("FOOD-NUTS", Food, "مكسرات", "Tree nuts", "Fruits à coque"),
			new("FOOD-EGG", Food, "بيض", "Eggs", "Œufs"),
			new("FOOD-MILK", Food, "حليب", "Milk", "Lait"),
			new("FOOD-SEAFOOD", Food, "مأكولات بحرية", "Seafood", "Fruits de mer"),
			new("FOOD-FISH", Food, "سمك", "Fish", "Poisson"),
			new("FOOD-WHEAT", Food, "قمح", "Wheat", "Blé"),
			new("FOOD-SESAME", Food, "سمسم", "Sesame", "Sésame"),
			new("ENV-LATEX", Environment, "لاتكس", "Latex", "Latex"),
			new("ENV-BEE", Environment, "لدغة نحل", "Bee sting", "Piqûre d'abeille"),
			new("ENV-DUST", Environment, "عثة غبار المنزل", "House dust mite", "Acariens"),
			new("ENV-POLLEN", Environment, "حبوب اللقاح", "Pollen", "Pollen"),
		};

		/// The reactions, each with the Arabic word HospitalAllergyText translates.
		public static readonly IReadOnlyList<(string Code, string Ar, string En, string Fr)> Reactions = new[]
		{
			("Rash", "طفح جلدي", "Rash", "Éruption cutanée"),
			("Hives", "شري", "Hives", "Urticaire"),
			("Itching", "حكة", "Itching", "Démangeaisons"),
			("Swelling", "تورم", "Swelling", "Œdème"),
			("Dyspnoea", "ضيق تنفس", "Shortness of breath", "Dyspnée"),
			("Anaphylaxis", "صدمة تحسسية", "Anaphylaxis", "Anaphylaxie"),
			("Nausea", "غثيان", "Nausea", "Nausées"),
		};

		private static readonly Dictionary<string, HospitalAllergen> ByCode = All.ToDictionary(a => a.Code);

		public static HospitalAllergen? Find(string? code) => code != null && ByCode.TryGetValue(code, out var a) ? a : null;

		/// The catalogue entry an Arabic name stands for (folded), for HospitalAllergyText's English/French display.
		public static HospitalAllergen? ByArabic(string folded) => All.FirstOrDefault(a => HospitalAllergyText.Fold(a.Ar) == folded);

		public static string Label(HospitalAllergen a, string lang) => lang == "ar" ? a.Ar : lang == "fr" ? a.Fr : a.En;

		public static List<HospitalAllergyItem>? Parse(string? json)
		{
			if (string.IsNullOrWhiteSpace(json)) return null;
			try { return JsonSerializer.Deserialize<List<HospitalAllergyItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch (JsonException) { return null; }
		}

		/// Cleans the list in place; a refusal, or null when it is sound.
		public static string? Refusal(List<HospitalAllergyItem> items)
		{
			foreach (var i in items)
			{
				i.Code = string.IsNullOrWhiteSpace(i.Code) ? null : i.Code.Trim();
				i.Text = string.IsNullOrWhiteSpace(i.Text) ? null : i.Text.Trim();
				i.Reaction = string.IsNullOrWhiteSpace(i.Reaction) ? null : i.Reaction.Trim();
				i.Severity = string.IsNullOrWhiteSpace(i.Severity) ? null : i.Severity.Trim();
				if (i.Code != null) i.Text = null;
			}
			if (items.Any(i => i.Code == null && i.Text == null && (i.Reaction != null || i.Severity != null)))
				return "Choose the allergy in every row, or remove the row";   // H270: a half-filled row is never dropped silently
			items.RemoveAll(i => i.Code == null && i.Text == null);
			if (items.Any(i => i.Code == NoKnownAllergy))
			{
				if (items.Count > 1) return "No known allergy cannot sit beside an allergy";
				items[0].Reaction = null; items[0].Severity = null;
				return null;
			}
			foreach (var i in items)
			{
				if (i.Code != null && Find(i.Code) == null) return "Unknown allergen";
				if (i.Text != null && (i.Text.Length < 2 || i.Text.Length > 100)) return "An allergy not in the list is 2 to 100 letters";
				if (i.Reaction != null && !Reactions.Any(r => r.Code == i.Reaction)) return "Unknown reaction";
				if (i.Severity != null && !Severities.Contains(i.Severity)) return "Unknown severity";
			}
			if (items.Select(Key).Distinct().Count() != items.Count) return "The same allergy is listed twice";
			return null;
		}

		/// What makes two entries the same allergy: the code, or the folded free text.
		public static string Key(HospitalAllergyItem i) => i.Code ?? "t:" + HospitalAllergyMatch.Fold(i.Text ?? "");

		/// The text the file prints: one line per allergy, "بنسلين (طفح جلدي)"; null for none or no known allergy.
		public static string? Describe(IEnumerable<HospitalAllergyItem> items)
		{
			var lines = items.Where(i => i.Code != NoKnownAllergy).Select(Line).Where(l => l.Length > 0).ToList();
			if (lines.Count == 0) return null;
			var text = string.Join("\n", lines);
			return text.Length <= 1000 ? text : text[..1000];
		}

		public static string Line(HospitalAllergyItem i)
		{
			var name = Find(i.Code)?.Ar ?? i.Text ?? "";
			var r = Reactions.FirstOrDefault(x => x.Code == i.Reaction);
			return name.Length == 0 ? "" : r.Code == null ? name : name + " (" + r.Ar + ")";
		}

		/// The list a text written before H270 (or by the kiosk, a merge) stands for: an entry the catalogue knows by its
		/// Arabic or English name becomes coded, with its reaction when the words are known; anything else stays free text.
		public static List<HospitalAllergyItem> FromText(string? text)
		{
			var items = new List<HospitalAllergyItem>();
			if (string.IsNullOrWhiteSpace(text)) return items;
			foreach (var raw in text.Split(new[] { '\n', ',', '،', ';', '؛', '·' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				int open = raw.IndexOf('(');
				var main = (open >= 0 ? raw[..open] : raw).Trim();
				var inside = open >= 0 ? raw[(open + 1)..].TrimEnd(')').Trim() : "";
				if (main.Length == 0) continue;
				var folded = HospitalAllergyMatch.Fold(main);
				var hit = All.FirstOrDefault(a => HospitalAllergyMatch.Fold(a.Ar) == folded || HospitalAllergyMatch.Fold(a.En) == folded);
				var reaction = inside.Length == 0 ? null : Reactions.FirstOrDefault(r => HospitalAllergyText.Fold(r.Ar) == HospitalAllergyText.Fold(inside) || r.En.Equals(inside, StringComparison.OrdinalIgnoreCase)).Code;
				var item = hit != null
					? new HospitalAllergyItem { Code = hit.Code, Reaction = reaction }
					: new HospitalAllergyItem { Text = (reaction == null && inside.Length > 0 ? raw : main) is var t && t.Length > 100 ? t[..100] : t, Reaction = reaction };
				if (!items.Any(x => Key(x) == Key(item))) items.Add(item);
			}
			return items;
		}
	}
}
