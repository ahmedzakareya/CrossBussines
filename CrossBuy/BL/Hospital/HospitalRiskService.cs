using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalRiskPatient(int AdmissionId, int PatientId, string Name, string? NameEn, string? Photo, string? Mrn, int LosDays, bool Acute, int Charlson, int EdVisits, int Lace);
	public sealed record HospitalRiskPage(int Validated, int Readmitted, decimal? Auc, IReadOnlyList<HospitalRiskBand> Calibration, decimal? Psi, string Drift, int Baseline, int Recent,
		IReadOnlyList<HospitalRiskPatient> Inpatients, int InpatientTotal, int HighRisk);

	// ==========================================================================================
	// HOSPITAL H253 — scoring LACE from the record and measuring it on the hospital's own discharges (the rules are
	// HospitalRiskRules'). Clinical reading (`clinical-read`). Nothing is stored: every figure is recomputed from the
	// record, so the validation can never be out of date.
	// ==========================================================================================
	public sealed class HospitalRiskService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRiskService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private static int Los(DateTime from, DateTime to) => Math.Max(0, (Ward(to).Date - Ward(from).Date).Days);

		public async Task<HospitalRiskPage?> PageAsync(BusinessContext ctx, string? q, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			var since = nowUtc.AddDays(-455);
			var adms = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && (a.Status == HospitalAdmissionStatus.Active || a.AdmittedAt >= since.AddDays(-30)))
				.Select(a => new { a.ID, a.PatientId, a.AdmittedAt, a.DischargedAt, a.Status }).ToListAsync(ct);
			var pids = adms.Select(a => a.PatientId).Distinct().ToList();
			var er = await _db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && v.PatientId != null && pids.Contains(v.PatientId!.Value) && v.ArrivedAt >= since.AddDays(-190))
				.Select(v => new { PatientId = v.PatientId!.Value, v.ArrivedAt, v.Outcome }).ToListAsync(ct);
			var dx = await (from d in _db.Set<HospitalEncounterDiagnosis>().AsNoTracking()
							join e in _db.Set<HospitalEncounter>().AsNoTracking() on d.EncounterId equals e.ID
							where e.CompanyId == ctx.CompanyId && pids.Contains(e.PatientId)
							select new { e.PatientId, d.Code, e.StartedAt }).ToListAsync(ct);
			List<(int PatientId, string Code)> drg;
			try { drg = (await _db.Set<HospitalDrgAssignment>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && pids.Contains(x.PatientId)).Select(x => new { x.PatientId, x.PrincipalDx }).ToListAsync(ct)).Select(x => (x.PatientId, x.PrincipalDx)).ToList(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { drg = new(); }

			int Score(int patientId, DateTime admittedAt, DateTime endAt, out bool acute, out int charlson, out int edVisits, out int los)
			{
				los = Los(admittedAt, endAt);
				acute = er.Any(v => v.PatientId == patientId && v.Outcome == HospitalErOutcome.Admitted && v.ArrivedAt <= admittedAt && v.ArrivedAt >= admittedAt.AddHours(-24));
				charlson = HospitalReadmissionRules.CharlsonScore(dx.Where(d => d.PatientId == patientId && d.StartedAt <= endAt).Select(d => d.Code).Concat(drg.Where(d => d.PatientId == patientId).Select(d => d.Code)));
				edVisits = er.Count(v => v.PatientId == patientId && v.ArrivedAt < admittedAt.AddHours(-24) && v.ArrivedAt >= admittedAt.AddMonths(-6));
				return HospitalReadmissionRules.Lace(los, acute, charlson, edVisits);
			}

			// validation: discharges whose 30 days are over
			var cases = new List<(DateTime Discharged, HospitalRiskCase Case)>();
			foreach (var a in adms.Where(a => a.DischargedAt is DateTime d && d >= since && d <= nowUtc.AddDays(-30)))
			{
				var d = a.DischargedAt!.Value;
				int s = Score(a.PatientId, a.AdmittedAt, d, out _, out _, out _, out _);
				bool readmitted = adms.Any(b => b.PatientId == a.PatientId && b.ID != a.ID && b.AdmittedAt > d && b.AdmittedAt <= d.AddDays(30));
				cases.Add((d, new HospitalRiskCase(s, readmitted)));
			}
			var all = cases.Select(c => c.Case).ToList();
			var recentCut = nowUtc.AddDays(-120);   // discharged in the last 90 days whose 30 days are over
			var recent = cases.Where(c => c.Discharged >= recentCut).Select(c => c.Case.Score).ToList();
			var baseline = cases.Where(c => c.Discharged < recentCut).Select(c => c.Case.Score).ToList();
			var psi = HospitalReadmissionRules.Psi(baseline, recent);

			// the inpatients now, scored on their stay so far
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
			var now = adms.Where(a => a.Status == HospitalAdmissionStatus.Active && pats.ContainsKey(a.PatientId)).Select(a =>
			{
				int s = Score(a.PatientId, a.AdmittedAt, nowUtc, out var acute, out var ch, out var ed, out var los);
				var p = pats[a.PatientId];
				return new HospitalRiskPatient(a.ID, a.PatientId, p.Name, p.NameEn, p.PhotoPath, p.Mrn, los, acute, ch, ed, s);
			}).ToList();
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); now = now.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (r.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Mrn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList(); }
			var ordered = now.OrderByDescending(r => r.Lace).ToList();
			return new HospitalRiskPage(all.Count, all.Count(c => c.Outcome), HospitalReadmissionRules.Auc(all), HospitalReadmissionRules.Calibration(all), psi, HospitalReadmissionRules.Drift(psi), baseline.Count, recent.Count,
				ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), ordered.Count, ordered.Count(r => r.Lace >= HospitalReadmissionRules.HighRisk));
		}
	}
}
