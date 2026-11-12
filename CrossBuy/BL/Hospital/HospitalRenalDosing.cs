using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H104 — DOSING BY THE KIDNEY (the approved prototype screen sc-im: "Metformin with eGFR 47 — review the dose
	// below 45, stop below 30 — the trend is falling"). A dose limit (H73) may carry two eGFR thresholds: review the dose
	// below one, stop the medicine below the other. The patient's eGFR is the last one STORED (the visit forms' CKD-EPI
	// 2021 calculation, or a LOINC eGFR) within a year — never guessed, and an old one is not used.
	//   * a new hospital order meets it: below "stop" it is a stopping alert (overridden only with a reason, H73);
	//     below "review" a warning; a kidney rule with no eGFR on file says it could not check;
	//   * the specialty visit shows it for the medicines prescribed in the last 180 days — and a FALLING eGFR within 5 of
	//     the review line is shown before it crosses (the prototype's 47).
	// ==========================================================================================
	public sealed record HospitalEgfr(decimal Value, DateTime At, decimal? Previous)
	{
		public bool Falling => Previous is decimal p && Value < p;
	}

	public sealed record HospitalRenalAlert(string Drug, HospitalEgfr Egfr, HospitalDoseLimit Limit, bool Stop, bool Approaching);

	public static class HospitalRenalRules
	{
		/// LOINC codes of an eGFR result (CKD-EPI 2021 without race, MDRD, CKD-EPI creatinine).
		public static readonly IReadOnlyList<string> EgfrLoinc = new[] { "98979-8", "62238-1", "33914-3", "48642-3", "48643-1" };
		public static readonly TimeSpan Valid = TimeSpan.FromDays(365);
		public const decimal Approach = 5;

		/// The kidney rules a medicine meets at this eGFR: (limit, stop?) — below "stop", or below "review".
		public static IReadOnlyList<(HospitalDoseLimit Limit, bool Stop, bool Approaching)> Hits(string drug, string? route, HospitalEgfr? egfr, IEnumerable<HospitalDoseLimit> limits)
		{
			var outp = new List<(HospitalDoseLimit, bool, bool)>();
			if (egfr == null) return outp;
			foreach (var l in Rules(drug, route, limits))
			{
				if (l.RenalStopBelow is decimal s && egfr.Value < s) outp.Add((l, true, false));
				else if (l.RenalReviewBelow is decimal r && egfr.Value < r) outp.Add((l, false, false));
				else if (l.RenalReviewBelow is decimal r2 && egfr.Falling && egfr.Value < r2 + Approach) outp.Add((l, false, true));
			}
			return outp;
		}

		/// The dose limits of this medicine (and route) that carry a kidney threshold.
		public static IEnumerable<HospitalDoseLimit> Rules(string drug, string? route, IEnumerable<HospitalDoseLimit> limits) =>
			limits.Where(l => (l.RenalReviewBelow != null || l.RenalStopBelow != null) && HospitalDrugSafetyRules.Mentions(drug, l.Drug) && (l.Route == null || route == null || l.Route == route));

		/// The patient's last stored eGFR within a year, and the one before it (for the trend).
		public static async Task<HospitalEgfr?> EgfrAsync(CrossDbContext db, int companyId, int patientId, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var since = nowUtc - Valid;
				var pts = await (from o in db.Set<HospitalClinicalObservation>().AsNoTracking().Where(o => o.CompanyId == companyId && o.PatientId == patientId && o.Value != null)
								 join c in db.Set<HospitalConcept>().AsNoTracking() on o.ConceptId equals c.ID
								 where c.Calculator == HospitalConceptCalculator.EgfrCkdEpi2021 || (c.CodeSystem == "LOINC" && EgfrLoinc.Contains(c.Code))
								 orderby o.ObservedAt descending
								 select new { o.Value, o.ObservedAt }).Take(20).ToListAsync(ct);
				if (pts.Count == 0 || pts[0].ObservedAt < since) return null;
				// the trend compares with an EARLIER DAY: one visit can store the same value more than once
				var before = pts.FirstOrDefault(x => x.ObservedAt.Date < pts[0].ObservedAt.Date);
				return new HospitalEgfr(pts[0].Value!.Value, pts[0].ObservedAt, before?.Value);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return null; }
		}

		/// The visit's kidney alerts: each medicine prescribed to the patient in the last 180 days that meets a rule.
		public static async Task<IReadOnlyList<HospitalRenalAlert>> VisitAlertsAsync(CrossDbContext db, int companyId, int patientId, DateTime nowUtc, CancellationToken ct)
		{
			var (_, limits) = await HospitalDrugSafetyService.KbAsync(db, companyId, ct);
			if (!limits.Any(l => l.RenalReviewBelow != null || l.RenalStopBelow != null)) return Array.Empty<HospitalRenalAlert>();
			var egfr = await EgfrAsync(db, companyId, patientId, nowUtc, ct);
			if (egfr == null) return Array.Empty<HospitalRenalAlert>();
			var since = nowUtc.AddDays(-180);
			var drugs = await (from l in db.Set<HospitalPrescriptionLine>().AsNoTracking()
							   join e in db.Set<HospitalEncounter>().AsNoTracking() on l.EncounterId equals e.ID
							   where e.CompanyId == companyId && e.PatientId == patientId && e.StartedAt >= since
							   select new { l.Drug, l.DrugEn }).Distinct().ToListAsync(ct);
			var outp = new List<HospitalRenalAlert>();
			// a medicine is read by both its names: a prescription may be written in Arabic, a rule in English
			foreach (var d in drugs.Where(x => !string.IsNullOrWhiteSpace(x.Drug)))
				foreach (var (limit, stop, approaching) in Hits(d.Drug + " " + d.DrugEn, null, egfr, limits))
					if (!outp.Any(a => a.Limit.ID == limit.ID)) outp.Add(new HospitalRenalAlert(d.Drug, egfr, limit, stop, approaching));
			return outp;
		}
	}
}
