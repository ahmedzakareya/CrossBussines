namespace CrossBuy.BL.Hospital
{
	/// <summary>
	/// HOSPITAL H269 — which of a patient's recorded allergies a drug falls under. The file keeps the allergies as free text
	/// ("بنسلين", "Penicillin (rash)", "سلفا"), and a doctor writes a drug's own name ("Amoxicillin 500 mg"), so matching
	/// the words alone missed the case that matters most: a penicillin allergy and amoxicillin share no word. Found in the
	/// owner's walk-through on 2026-11-25 — amoxicillin was prescribed to a patient allergic to penicillin with no warning.
	///
	/// Two ways a drug meets an allergy:
	///   · the allergy's own word is in the drug's words (its name, its generic, its class) — "Aspirin" in "Aspirin 81 mg";
	///   · the allergy names a FAMILY (or one of its members) and the drug is another member — penicillin → amoxicillin.
	/// Both scripts, folded the way the desk types them (alef forms, teh marbuta, the article). A match is a warning shown
	/// to the prescriber and the pharmacist; it never stops a prescription (the doctor may know the allergy is not real).
	/// </summary>
	public static class HospitalAllergyMatch
	{
		private static readonly char[] Separators = { '\n', ',', '،', ';', '؛', '·' };

		/// A family: the words that name it, and the members a drug's words are searched for. Each word 4+ letters, so a
		/// fragment cannot match by accident. Cross-family reactions (penicillin ↔ cephalosporin) are deliberately left out:
		/// they are a low, debated risk, and a warning on every cephalosporin would teach doctors to ignore the warning.
		public static readonly (string Key, string[] Names, string[] Members)[] Families =
		{
			("penicillins",
				new[] { "penicillin", "بنسلين", "بنسيلين" },
				new[] { "penicillin", "amoxicillin", "amoxycillin", "ampicillin", "amoxiclav", "augmentin", "flucloxacillin", "cloxacillin",
					"dicloxacillin", "oxacillin", "nafcillin", "piperacillin", "tazocin", "ticarcillin", "benzathine", "pivmecillinam", "temocillin",
					"بنسلين", "بنسيلين", "اموكسيسيلين", "اموكسيسلين", "اموكسيل", "امبيسيلين", "امبيسلين", "اوجمنتين", "اوغمنتين", "فلوكلوكساسيلين",
					"كلوكساسيلين", "بيبراسيلين", "تازوسين" }),
			("cephalosporins",
				new[] { "cephalosporin", "سيفالوسبورين" },
				new[] { "cephalosporin", "cefalexin", "cephalexin", "cefadroxil", "cefazolin", "cefuroxime", "cefaclor", "cefprozil", "cefixime",
					"cefdinir", "cefpodoxime", "ceftriaxone", "cefotaxime", "ceftazidime", "cefepime", "ceftaroline", "cefoxitin",
					"سيفالوسبورين", "سيفالكسين", "سيفادروكسيل", "سيفازولين", "سيفوروكسيم", "سيفاكلور", "سيفيكسيم", "سيفترياكسون", "سيفوتاكسيم",
					"سيفتازيديم", "سيفيبيم" }),
			("sulfonamides",
				new[] { "sulfa", "sulpha", "sulfonamide", "سلفا" },
				new[] { "sulfa", "sulpha", "sulfonamide", "sulfamethoxazole", "co-trimoxazole", "cotrimoxazole", "septrin", "bactrim",
					"sulfasalazine", "sulfadiazine", "سلفا", "سلفاميثوكسازول", "سبترين", "باكتريم", "سلفاسالازين" }),
			("nsaids",
				new[] { "nsaid", "مضادات التهاب", "مسكنات غير ستيرويديه" },
				new[] { "nsaid", "aspirin", "acetylsalicylic", "ibuprofen", "brufen", "diclofenac", "voltaren", "cataflam", "naproxen", "ketoprofen",
					"ketorolac", "mefenamic", "indomethacin", "piroxicam", "meloxicam", "celecoxib", "etoricoxib", "اسبرين", "ايبوبروفين",
					"بروفين", "ديكلوفيناك", "فولتارين", "كتافلام", "نابروكسين", "كيتوبروفين", "كيتورولاك", "ميفيناميك", "اندوميثاسين",
					"بيروكسيكام", "ميلوكسيكام", "سيليكوكسيب" }),
			("opioids",
				new[] { "opioid", "opiate", "افيونات", "مسكنات افيونيه" },
				new[] { "opioid", "opiate", "codeine", "morphine", "dihydrocodeine", "hydrocodone", "oxycodone", "hydromorphone", "كودايين",
					"كودين", "مورفين", "اوكسيكودون" }),
			("macrolides",
				new[] { "macrolide", "ماكروليد" },
				new[] { "macrolide", "erythromycin", "azithromycin", "clarithromycin", "zithromax", "klacid", "اريثرومايسين", "ازيثرومايسين",
					"زيثروماكس", "كلاريثرومايسين", "كلاسيد" }),
			("fluoroquinolones",
				new[] { "quinolone", "كينولون" },
				new[] { "quinolone", "ciprofloxacin", "levofloxacin", "moxifloxacin", "ofloxacin", "norfloxacin", "سيبروفلوكساسين",
					"ليفوفلوكساسين", "موكسيفلوكساسين", "اوفلوكساسين" }),
			("tetracyclines",
				new[] { "tetracycline", "تتراسيكلين" },
				new[] { "tetracycline", "doxycycline", "minocycline", "تتراسيكلين", "دوكسيسيكلين", "مينوسيكلين" }),
		};

		/// The recorded allergies (as written, the reaction in brackets kept off) that the drug's words fall under.
		/// `drugWords` is everything known about the drug: what the doctor wrote, its generic names and its class.
		public static IReadOnlyList<string> Hits(string? drugWords, string? allergies)
		{
			if (string.IsNullOrWhiteSpace(allergies) || string.IsNullOrWhiteSpace(drugWords)) return Array.Empty<string>();
			var drug = Fold(drugWords);
			var hits = new List<string>();
			foreach (var raw in allergies.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var written = raw.Split('(')[0].Trim();
				var entry = Fold(written);
				if (entry.Length < 3) continue;
				bool hit = drug.Contains(entry, StringComparison.Ordinal)
					|| Families.Any(f => NamesFamily(entry, f) && f.Members.Any(m => drug.Contains(Fold(m), StringComparison.Ordinal)));
				if (hit && !hits.Contains(written, StringComparer.OrdinalIgnoreCase)) hits.Add(written);
			}
			return hits;
		}

		/// The allergy is the family itself ("Penicillin", "حساسية البنسلين", "sulfa drugs") or one of its members
		/// ("Amoxicillin" — an allergy to one penicillin is an allergy to them all).
		private static bool NamesFamily(string entry, (string Key, string[] Names, string[] Members) f)
			=> f.Names.Concat(f.Members).Any(w => entry.Contains(Fold(w), StringComparison.Ordinal));

		/// Lower case, and the Arabic folded the way the desk types it (see HospitalAllergyText.Fold).
		public static string Fold(string s) => HospitalAllergyText.Fold(s.ToLowerInvariant());
	}
}
