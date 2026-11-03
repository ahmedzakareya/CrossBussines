using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H12 — the estimate rules, pure: no database, no clock, no access. An estimate prices a package
	// (its fixed price, its included days), the stay beyond those days at the room class's daily rate, and any
	// extra service; the payer's and the patient's shares come from the ONE split rule of H11
	// (HospitalInsuranceRules.Split); the deposit asked is the patient's expected share plus a margin.
	// The tests pin them (CrossBuy.Tests/HospitalEstimateRulesTests, database-free).
	// ==========================================================================================

	/// One priced line of an estimate before the split. Kind: package | stay | service.
	public sealed record HospitalEstimateDraftLine(string Kind, int? ServiceId, string Code, string Category, string Description, string DescriptionEn,
		decimal Quantity, decimal UnitPrice, decimal Amount, bool Covered);

	public sealed record HospitalEstimateTotals(decimal Gross, decimal PayerShare, decimal PatientShare, decimal Deposit, IReadOnlyList<decimal> PayerPerLine);

	public static class HospitalEstimateRules
	{
		public const string KindPackage = "package", KindStay = "stay", KindService = "service";

		/// The nights billed outside the package: what the stay exceeds the included days by (never negative).
		public static int ExtraNights(int expectedNights, int includedNights) => Math.Max(0, expectedNights - Math.Max(0, includedNights));

		/// The lines of an estimate. With a package: the package at its fixed price, then the nights beyond its
		/// included ones. Without: every night of the stay. Extra services follow either way. Covered is the
		/// payer's list covering the line (an uncovered line is the patient's in full, as on a claim).
		public static IReadOnlyList<HospitalEstimateDraftLine> Lines(HospitalPackage? package, bool packageCovered, string? roomCode, decimal nightlyRate,
			int expectedNights, IEnumerable<HospitalEstimateDraftLine> services, int decimals)
		{
			decimal R(decimal v) => Math.Round(v, decimals, MidpointRounding.AwayFromZero);
			var list = new List<HospitalEstimateDraftLine>();
			int nights = Math.Max(0, expectedNights);
			if (package != null)
			{
				list.Add(new(KindPackage, package.ServiceId, package.Code, HospitalServiceCategories.Procedure, package.Name, package.NameEn, 1, package.Price, R(package.Price), packageCovered));
				nights = ExtraNights(expectedNights, package.IncludedNights);
			}
			if (nights > 0 && nightlyRate > 0)
				list.Add(new(KindStay, null, "ACC-" + (roomCode ?? "").ToUpperInvariant(), HospitalServiceCategories.Accommodation, "إقامة", "Accommodation",
					nights, nightlyRate, R(nights * nightlyRate), true));
			list.AddRange(services.Select(s => s with { Kind = KindService, Amount = R(s.Quantity * s.UnitPrice) }));
			return list;
		}

		/// The split of an estimate, by the same rule a claim uses, and the deposit: the patient's expected share
		/// plus `marginPct` (rounded up to the currency's last decimal, so the deposit never falls short).
		public static HospitalEstimateTotals Totals(IReadOnlyList<HospitalEstimateDraftLine> lines, decimal copayPct, decimal copayCap, bool insured, decimal marginPct, int decimals)
		{
			var claimable = lines.Select(l => new HospitalClaimableLine(null, l.Code, l.Category, l.Amount, insured && l.Covered)).ToList();
			var split = HospitalInsuranceRules.Split(claimable, insured ? copayPct : 0, insured ? copayCap : 0, decimals);
			decimal factor = (decimal)Math.Pow(10, decimals);
			decimal deposit = Math.Ceiling(split.PatientShare * (1 + Math.Max(0, marginPct) / 100m) * factor) / factor;
			return new HospitalEstimateTotals(split.Gross, split.InsurerShare, split.PatientShare, deposit, split.InsurerPerLine);
		}

		/// An estimate stays valid for this many days from its issue; after that it is re-priced, not honoured.
		public const int ValidDays = 30;

		public static bool IsExpired(DateTime issuedAt, DateTime day) => day.Date > issuedAt.Date.AddDays(ValidDays);
	}
}
