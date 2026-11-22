using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalDrugFacts(string GenericName, string? GenericNameEn, string? Atc, string? PregnancyRisk, string? LactationRisk, bool IsAntimicrobial, string FormularyStatus);

	// ==========================================================================================
	// HOSPITAL H234 — three more checks on a medicine order, free of the database:
	//   · DUPLICATE THERAPY: another running medicine of the same therapeutic subgroup (the first five characters of the
	//     ATC code: e.g. M01AE ibuprofen with M01AB diclofenac is NOT a duplicate; M01AE ibuprofen with M01AE naproxen is).
	//   · PREGNANCY / BREASTFEEDING: the medicine's recorded risk against an active pregnancy, or a birth in the last
	//     six months (breastfeeding assumed — the order says so, it does not claim to know).
	//   · PER-KILOGRAM DOSE: a dose above the per-kg limit for the patient's recorded weight — every age, not only
	//     children. No recent weight and a per-kg limit: said, never silently skipped.
	// ==========================================================================================
	public static class HospitalMedSafetyRules
	{
		public const int LactationDays = 182;
		public const int WeightDays = 30;

		public static string? Atc4(string? atc)
		{
			var a = (atc ?? "").Trim().ToUpperInvariant();
			return a.Length >= 5 ? a[..5] : null;
		}

		/// The drug master record an ordered name means (its generic name, either language, inside the order's text).
		public static HospitalDrugFacts? Find(string ordered, IEnumerable<HospitalDrugFacts> drugs) =>
			drugs.Where(d => HospitalDrugSafetyRules.Mentions(ordered, d.GenericNameEn ?? "") || HospitalDrugSafetyRules.Mentions(ordered, d.GenericName))
				.OrderByDescending(d => (d.GenericNameEn ?? d.GenericName).Length).FirstOrDefault();

		/// The running medicines that duplicate the new one's therapeutic subgroup (never the same medicine itself).
		public static IReadOnlyList<string> Duplicates(string ordered, IEnumerable<string> running, IReadOnlyList<HospitalDrugFacts> drugs)
		{
			var me = Find(ordered, drugs); var key = Atc4(me?.Atc);
			if (me == null || key == null) return Array.Empty<string>();
			return running.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(r => (r, f: Find(r, drugs)))
				.Where(x => x.f != null && !ReferenceEquals(x.f, me) && !string.Equals(x.f.GenericName, me.GenericName, StringComparison.OrdinalIgnoreCase) && Atc4(x.f.Atc) == key)
				.Select(x => x.r).ToList();
		}

		public static bool Lactating(DateTime? lastBirthUtc, DateTime nowUtc) => lastBirthUtc is DateTime b && (nowUtc - b).TotalDays <= LactationDays;

		/// The largest single dose the weight allows, and whether the ordered amount (mg) is above it.
		public static (decimal Max, bool Over)? PerKg(decimal? orderedMg, decimal? perKgMg, decimal? kg)
		{
			if (orderedMg is not decimal mg || perKgMg is not decimal per || kg is not > 0) return null;
			var max = Math.Round(per * kg.Value, 1);
			return (max, mg > max);
		}
	}
}
