using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalDrgCaseStat(int DrgId, int Cases, decimal Weight, decimal AvgLos, decimal? MeanLos)
	{
		/// Average stay over the group's mean: above 1 the stays run longer than the group expects.
		public decimal? LosIndex => MeanLos is > 0 ? Math.Round(AvgLos / MeanLos.Value, 2, MidpointRounding.AwayFromZero) : null;
	}

	// ==========================================================================================
	// HOSPITAL H241 — the grouper, free of the database. A group matches when one of its ICD-10 prefixes starts the
	// principal diagnosis (case and the dot ignored), its surgical flag fits the stay, and the age is inside its band.
	// Of the matches, the longest prefix wins; on a tie the surgical-specific group, then the heavier. The CASE-MIX
	// INDEX is the mean frozen weight of the period's grouped stays.
	// ==========================================================================================
	public static class HospitalDrgRules
	{
		public static string NormalizeDx(string? dx) => (dx ?? "").Replace(".", "").Replace(" ", "").ToUpperInvariant();

		public static IReadOnlyList<string> Prefixes(string? list) =>
			(list ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeDx).Where(p => p.Length > 0).Distinct().ToList();

		public static HospitalDrg? Group(IEnumerable<HospitalDrg> groups, string? principalDx, bool surgical, int? age)
		{
			var dx = NormalizeDx(principalDx);
			if (dx.Length < 3) return null;
			return groups.Where(g => g.IsActive && (g.Surgical == null || g.Surgical == surgical)
					&& (g.MinAge == null || (age is int a1 && a1 >= g.MinAge)) && (g.MaxAge == null || (age is int a2 && a2 <= g.MaxAge)))
				.Select(g => (g, len: Prefixes(g.DxPrefixes).Where(p => dx.StartsWith(p, StringComparison.Ordinal)).Select(p => p.Length).DefaultIfEmpty(0).Max()))
				.Where(x => x.len > 0)
				.OrderByDescending(x => x.len).ThenByDescending(x => x.g.Surgical != null).ThenByDescending(x => x.g.Weight)
				.Select(x => x.g).FirstOrDefault();
		}

		public static decimal? CaseMixIndex(IEnumerable<decimal> weights)
		{
			var w = weights.Where(x => x > 0).ToList();
			return w.Count == 0 ? null : Math.Round(w.Average(), 4, MidpointRounding.AwayFromZero);
		}

		public static string? DefinitionRefusal(string? code, string? name, string? prefixes, decimal weight, int? minAge, int? maxAge, decimal? meanLos)
		{
			if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 20) return "Enter the group's code";
			if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return "Enter the group's name";
			var p = Prefixes(prefixes);
			if (p.Count == 0 || p.Any(x => x.Length < 1 || x.Length > 7 || !char.IsLetter(x[0]))) return "Enter the ICD-10 prefixes, separated by commas (for example I21,I22)";
			if (weight <= 0 || weight > 100) return "The weight is above zero and at most 100";
			if (minAge is < 0 || maxAge is > 130 || (minAge is int a && maxAge is int b && b < a)) return "The age band must end after it starts";
			return meanLos is <= 0 or > 365 ? "The mean stay is 1 to 365 days" : null;
		}
	}
}
