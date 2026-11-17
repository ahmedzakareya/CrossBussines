using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H108 — the AF stroke score (the approved prototype screen sc-im: a read-only field, "No AF — not scored").
	// It is never typed: the score is read from the patient's age and from the CODED conditions on file — the active
	// problem list and every diagnosis of the patient's visits (ICD-10). Without atrial fibrillation (I48) there is no
	// score: it answers an AF question only.
	// H172 — CHA₂DS₂-VA, ESC 2024 (rule "af-stroke" of the clinical register, H171). The 2024 guidelines dropped the sex
	// category of CHA₂DS₂-VASc: it adds nothing once the other factors are counted and confused the thresholds.
	//   C heart failure 1 · H hypertension 1 · A₂ age ≥ 75 2 · D diabetes 1 · S₂ stroke / TIA / thromboembolism 2 ·
	//   V vascular disease 1 · A age 65–74 1 (0–8).
	//   ESC 2024: 0 — no anticoagulant; 1 — consider one; 2 or more — recommended.
	// ==========================================================================================
	public sealed record HospitalChadsItem(string Key, int Points);
	public sealed record HospitalChadsVasc(bool Af, int? Score, IReadOnlyList<HospitalChadsItem> Items, string? Advice);

	public static class HospitalChadsRules
	{
		private static bool Starts(IEnumerable<string> codes, params string[] prefixes) =>
			codes.Any(c => prefixes.Any(p => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

		public const int Max = 8;

		/// The score from age and the coded conditions; no AF, no score.
		public static HospitalChadsVasc Score(int? ageYears, IEnumerable<string> icd)
		{
			var codes = icd.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
			bool af = Starts(codes, "I48");
			if (!af) return new HospitalChadsVasc(false, null, Array.Empty<HospitalChadsItem>(), null);
			var items = new List<HospitalChadsItem>();
			if (Starts(codes, "I50", "I42", "I11.0", "I13.0", "I13.2")) items.Add(new("chf", 1));
			if (Starts(codes, "I10", "I11", "I12", "I13", "I15")) items.Add(new("htn", 1));
			if (ageYears >= 75) items.Add(new("age75", 2));
			else if (ageYears >= 65) items.Add(new("age65", 1));
			if (Starts(codes, "E10", "E11", "E12", "E13", "E14")) items.Add(new("dm", 1));
			if (Starts(codes, "I63", "I64", "G45", "I74", "Z86.73")) items.Add(new("stroke", 2));
			if (Starts(codes, "I21", "I22", "I25.2", "I70", "I73.9")) items.Add(new("vasc", 1));
			int score = items.Sum(x => x.Points);
			string advice = score <= 0 ? "none" : score == 1 ? "consider" : "recommended";
			return new HospitalChadsVasc(true, score, items, advice);
		}

		/// The patient's coded conditions: the active problem list and every visit diagnosis.
		public static async Task<HospitalChadsVasc> ForPatientAsync(CrossDbContext db, int companyId, HospitalPatient p, DateTime nowUtc, CancellationToken ct)
		{
			var codes = new List<string>();
			try
			{
				codes.AddRange(await db.Set<HospitalProblem>().AsNoTracking().Where(x => x.CompanyId == companyId && x.PatientId == p.ID && x.Status == HospitalProblemStatus.Active && x.Code != null).Select(x => x.Code!).ToListAsync(ct));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
			codes.AddRange(await (from d in db.Set<HospitalEncounterDiagnosis>().AsNoTracking()
								  join e in db.Set<HospitalEncounter>().AsNoTracking() on d.EncounterId equals e.ID
								  where e.CompanyId == companyId && e.PatientId == p.ID
								  select d.Code).ToListAsync(ct));
			return Score(HospitalPatientService.AgeOn(p.BirthDate, nowUtc), codes);
		}
	}
}
