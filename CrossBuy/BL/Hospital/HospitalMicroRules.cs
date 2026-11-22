using System.Globalization;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalMicroPanelLine(int AntibioticId, string Code, string Name, int Tier, int SortOrder);
	public sealed record HospitalMicroAstLine(int AntibioticId, string Code, string Name, string Result, decimal? Mic);
	public sealed record HospitalMicroIsolateSummary(string Organism, string? Quantity, string? Mdro, IReadOnlyList<HospitalMicroAstLine> Reported);

	// ==========================================================================================
	// HOSPITAL H230 — the microbiology rules, free of the database.
	// ==========================================================================================
	public static class HospitalMicroRules
	{
		public static readonly IReadOnlyList<string> Sources = new[] { "Blood", "Urine", "Sputum", "Wound", "CSF", "Stool", "Swab", "Fluid", "Other" };

		// Antibiotic codes the resistance rules read (the dictionary's own codes; see the standard seed).
		private static readonly string[] Antistaph = { "OXA", "FOX" };
		private static readonly string[] Carbapenems = { "MEM", "IPM", "ETP" };
		private static readonly string[] ThirdGenCeph = { "CRO", "CTX", "CAZ" };

		/// Cascade (selective) reporting: tier 1 is always reported; tier 2 only when the isolate resists a tested
		/// tier-1 agent. An antibiotic outside the group's panel is reported (the bench tested it on purpose).
		public static IReadOnlySet<int> Reported(IReadOnlyList<HospitalMicroPanelLine> panel, IReadOnlyList<HospitalMicroAstLine> results)
		{
			var tier = panel.ToDictionary(p => p.AntibioticId, p => p.Tier);
			bool resistsFirstLine = results.Any(r => tier.GetValueOrDefault(r.AntibioticId, 1) == 1 && r.Result == HospitalMicroSir.Resistant);
			return results.Where(r => tier.GetValueOrDefault(r.AntibioticId, 1) == 1 || resistsFirstLine).Select(r => r.AntibioticId).ToHashSet();
		}

		/// The multidrug-resistant organism an isolate is, from its group and results (null: none).
		public static string? Mdro(string group, bool isAureus, IReadOnlyList<HospitalMicroAstLine> results)
		{
			bool R(params string[] codes) => results.Any(r => r.Result == HospitalMicroSir.Resistant && codes.Contains(r.Code, StringComparer.OrdinalIgnoreCase));
			return group switch
			{
				HospitalMicroGroup.Staphylococci when isAureus && R(Antistaph) => HospitalMdro.Mrsa,
				HospitalMicroGroup.Enterococci when R("VAN") => HospitalMdro.Vre,
				HospitalMicroGroup.Enterobacterales when R(Carbapenems) => HospitalMdro.Cre,
				HospitalMicroGroup.Enterobacterales when R(ThirdGenCeph) => HospitalMdro.Esbl,   // phenotypic screen; confirmed by the bench
				HospitalMicroGroup.Acinetobacter when R("MEM", "IPM") => HospitalMdro.Crab,
				HospitalMicroGroup.Pseudomonas when R("MEM", "IPM") => HospitalMdro.Crpa,
				_ => null,
			};
		}

		public static string? PreliminaryRefusal(string? text)
		{
			var t = (text ?? "").Trim();
			if (t.Length == 0) return "Write the preliminary report";
			return t.Length > 500 ? "A preliminary report is 500 characters at most" : null;
		}

		/// Whether the bench may complete the culture: growth decided; growth has isolates, each with an organism,
		/// and every isolate of a group with a panel has its susceptibility (fungi and "other" may go without).
		public static string? CompleteRefusal(string growth, IReadOnlyList<(string Group, int Results)> isolates)
		{
			if (growth == HospitalMicroGrowth.Pending) return "Record whether there is growth";
			if (growth != HospitalMicroGrowth.Growth) return isolates.Count > 0 ? "Remove the isolates: there is no significant growth" : null;
			if (isolates.Count == 0) return "Add the organism that grew";
			if (isolates.Count > 6) return "A culture has 6 isolates at most";
			return isolates.Any(i => i.Results == 0 && i.Group is not (HospitalMicroGroup.Fungi or HospitalMicroGroup.Other))
				? "Enter the susceptibility of every isolate" : null;
		}

		/// A blood culture with growth is a critical result the moment it is known (a positive bottle).
		public static bool Critical(string source, string growth) => source == "Blood" && growth == HospitalMicroGrowth.Growth;

		public static string Hours(DateTime startedUtc, DateTime nowUtc) => Math.Max(0, (int)Math.Floor((nowUtc - startedUtc).TotalHours)).ToString(CultureInfo.InvariantCulture);

		/// The report's text, written into the order's result row: what grew, how much, the resistance flag and the
		/// reported susceptibilities — S first, then I, then R, each in the panel's order. Organism names are the
		/// scientific (Latin) names a microbiology report always carries.
		public static string Summary(string growth, IReadOnlyList<HospitalMicroIsolateSummary> isolates, DateTime startedUtc, DateTime nowUtc, string? comment)
		{
			string text = growth switch
			{
				HospitalMicroGrowth.NoGrowth => "No growth after " + Hours(startedUtc, nowUtc) + " h.",
				HospitalMicroGrowth.Contaminated => "Mixed growth of doubtful significance (probable contamination). Please send a new specimen.",
				_ => string.Join(" | ", isolates.Select((i, n) =>
				{
					var parts = new List<string> { (n + 1).ToString(CultureInfo.InvariantCulture) + ") " + i.Organism + (string.IsNullOrWhiteSpace(i.Quantity) ? "" : " " + i.Quantity!.Trim()) };
					if (i.Mdro != null) parts[0] += " [" + i.Mdro + "]";
					foreach (var (code, label) in new[] { (HospitalMicroSir.Susceptible, "S"), (HospitalMicroSir.Intermediate, "I"), (HospitalMicroSir.Resistant, "R") })
					{
						var names = i.Reported.Where(r => r.Result == code).Select(r => r.Name + (r.Mic is decimal m ? " (MIC " + m.ToString("0.###", CultureInfo.InvariantCulture) + ")" : "")).ToList();
						if (names.Count > 0) parts.Add(label + ": " + string.Join(", ", names));
					}
					return string.Join("; ", parts);
				})),
			};
			if (!string.IsNullOrWhiteSpace(comment)) text += " — " + comment.Trim();
			return text.Length > 2000 ? text[..1997] + "..." : text;
		}

		public static string? QuantityRefusal(string? q) => (q ?? "").Trim().Length > 60 ? "The quantity is 60 characters at most" : null;

		public static decimal? Mic(string? raw)
		{
			var s = (raw ?? "").Trim().TrimStart('<', '>', '=', '≤', '≥').Trim();
			return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0 && d < 100000 ? Math.Round(d, 3) : null;
		}
	}
}
