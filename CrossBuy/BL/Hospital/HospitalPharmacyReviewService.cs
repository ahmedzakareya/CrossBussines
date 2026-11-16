using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H136 — the pharmacist's review (see HospitalPharmacyReview.cs).
	//   · THE CLINICAL PANEL: what the review weighs, beside the prescription. It shows the patient's age and sex, the
	//     last recorded weight, the last eGFR with its trend (the H104 renal reader), the recorded allergies, and whether
	//     they are admitted now.
	//   · PRIORITY: the pharmacist may raise or lower a prescription's priority while it is still with the pharmacy.
	//   · REJECT: refuse a prescription, with the reason. It never reaches dispensing.
	//   · INTERVENTIONS: logged against the prescription, and against one of its lines when it is about one medicine. The
	//     prescriber's answer (accepted / declined) closes each.
	// ==========================================================================================

	public sealed record HospitalRxClinical(int? AgeYears, string Sex, decimal? WeightKg, DateTime? WeightAt, HospitalEgfr? Egfr, string? Allergies, bool Admitted);

	public sealed record HospitalInterventionView(HospitalPharmacyIntervention I, string? RecordedBy, string? RecordedByEn, string? RecordedByPhoto);

	public sealed class HospitalInterventionInput
	{
		public int? LineId { get; set; }
		public string? Category { get; set; }
		public string? Severity { get; set; }
		public string? Problem { get; set; }
		public string? Recommendation { get; set; }
		public int? Minutes { get; set; }
	}

	public static class HospitalReviewRules
	{
		/// Minutes the prescription has waited (or waited) for the pharmacist's review.
		public static int WaitingMinutes(DateTime prescribedAt, DateTime? reviewedAt, DateTime nowUtc)
			=> Math.Max(0, (int)Math.Floor(((reviewedAt ?? nowUtc) - prescribedAt).TotalMinutes));

		/// Past its priority's target: still unreviewed and over it, or reviewed after it.
		public static bool IsLate(string priority, DateTime prescribedAt, DateTime? reviewedAt, DateTime nowUtc)
			=> WaitingMinutes(prescribedAt, reviewedAt, nowUtc) > HospitalRxPriority.TargetMinutes(priority);

		public static HospitalResult? Validate(HospitalInterventionInput i)
		{
			i.Problem = Clean(i.Problem); i.Recommendation = Clean(i.Recommendation);
			if (!HospitalInterventionCategory.All.Contains(i.Category ?? "")) return HospitalResult.Fail("Choose the kind of problem");
			if (!HospitalInterventionSeverity.All.Contains(i.Severity ?? "")) return HospitalResult.Fail("Choose how serious it is");
			if (i.Problem == null) return HospitalResult.Fail("Write the problem");
			if (i.Recommendation == null) return HospitalResult.Fail("Write what you recommend");
			if (i.Minutes is int m && (m < 0 || m > 600)) return HospitalResult.Fail("The minutes spent are 0 to 600");
			return null;
		}

		/// The share of answered interventions the prescribers accepted, rounded; null when none was answered.
		public static int? AcceptedPct(int accepted, int declined) => accepted + declined == 0 ? null : (int)Math.Round(100m * accepted / (accepted + declined), MidpointRounding.AwayFromZero);

		public static int? AgeYears(DateTime? birth, DateTime today)
		{
			if (birth is not DateTime b) return null;
			int a = today.Year - b.Year; if (b.Date > today.AddYears(-a)) a--;
			return a < 0 ? null : a;
		}

		private static string? Clean(string? s) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > 400 ? s[..400] : s; }
	}

	public sealed class HospitalPharmacyReviewService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalPharmacyReviewService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Pharmacist(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<HospitalRxClinical> ClinicalAsync(BusinessContext ctx, HospitalPatient p, DateTime nowUtc, CancellationToken ct = default)
		{
			var w = await _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == p.ID && e.WeightKg != null)
				.OrderByDescending(e => e.CreatedAt).Select(e => new { e.WeightKg, e.CreatedAt }).FirstOrDefaultAsync(ct);
			var egfr = await HospitalRenalRules.EgfrAsync(_db, ctx.CompanyId, p.ID, nowUtc, ct);
			bool admitted = await _db.HospitalAdmissions.AsNoTracking().AnyAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == p.ID && a.Status == HospitalAdmissionStatus.Active, ct);
			return new HospitalRxClinical(HospitalReviewRules.AgeYears(p.BirthDate, nowUtc.Date), p.Sex, w?.WeightKg, w?.CreatedAt, egfr, string.IsNullOrWhiteSpace(p.Allergies) ? null : p.Allergies, admitted);
		}

		public async Task<IReadOnlyList<HospitalInterventionView>> InterventionsAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			List<HospitalPharmacyIntervention> rows;
			try { rows = await _db.Set<HospitalPharmacyIntervention>().AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId && i.DispenseId == dispenseId).OrderByDescending(i => i.RecordedAt).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalInterventionView>(); }   // the slice not yet applied
			var emp = rows.Where(r => r.RecordedByEmployeeId != null).Select(r => r.RecordedByEmployeeId!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => emp.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => (e.FullName, e.FullNameEn), ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, emp, ct);
			return rows.Select(r =>
			{
				var by = r.RecordedByEmployeeId is int b && people.TryGetValue(b, out var x) ? x : default;
				return new HospitalInterventionView(r, by.FullName, by.FullNameEn, r.RecordedByEmployeeId is int b2 ? photos.GetValueOrDefault(b2) : null);
			}).ToList();
		}

		/// This month's interventions and the share the prescribers accepted (for the pharmacy's figures).
		public static async Task<(int Count, int? AcceptedPct)> MonthAsync(CrossDbContext db, int companyId, DateTime nowUtc, CancellationToken ct)
		{
			var start = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
			try
			{
				var o = await db.Set<HospitalPharmacyIntervention>().AsNoTracking().Where(i => i.CompanyId == companyId && i.RecordedAt >= start).Select(i => i.Outcome).ToListAsync(ct);
				return (o.Count, HospitalReviewRules.AcceptedPct(o.Count(x => x == HospitalInterventionOutcome.Accepted), o.Count(x => x == HospitalInterventionOutcome.Declined)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (0, null); }
		}

		// ====================================================================================
		// WRITES
		// ====================================================================================

		private static bool WithThePharmacy(HospitalDispense d) => d.Status == HospitalDispenseStatus.New || d.Status == HospitalDispenseStatus.Verified;

		public async Task<HospitalResult> SetPriorityAsync(BusinessContext ctx, int id, string? priority, CancellationToken ct = default)
		{
			if (!await Pharmacist(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalRxPriority.All.Contains(priority ?? "")) return HospitalResult.Fail("Choose the priority");
			var d = await _db.HospitalDispenses.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (!WithThePharmacy(d)) return HospitalResult.Fail("This prescription has left the pharmacy");
			d.Priority = priority;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(d.ID);
		}

		public async Task<HospitalResult> RejectAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Pharmacist(ctx, ct)) return HospitalResult.Fail("forbidden");
			reason = reason?.Trim();
			if (string.IsNullOrEmpty(reason)) return HospitalResult.Fail("Write why the prescription is refused");
			var d = await _db.HospitalDispenses.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (!WithThePharmacy(d)) return HospitalResult.Fail("This prescription has left the pharmacy");
			d.Status = HospitalDispenseStatus.Rejected; d.RejectedAt = nowUtc; d.RejectedByEmployeeId = employeeId; d.RejectReason = reason.Length > 400 ? reason[..400] : reason;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(d.ID);
		}

		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, int dispenseId, HospitalInterventionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Pharmacist(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalReviewRules.Validate(input);
			if (refusal != null) return refusal;
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dispenseId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			string? drug = null;
			if (input.LineId is int lid)
			{
				drug = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.ID == lid && l.DispenseId == d.ID && l.CompanyId == ctx.CompanyId).Select(l => l.Drug).FirstOrDefaultAsync(ct);
				if (drug == null) return HospitalResult.Fail("not_found");   // a line of another prescription
			}
			var i = new HospitalPharmacyIntervention
			{
				CompanyId = ctx.CompanyId, DispenseId = d.ID, PatientId = d.PatientId, LineId = input.LineId, Drug = drug is { Length: > 200 } ? drug[..200] : drug,
				Category = input.Category!, Severity = input.Severity!, Problem = input.Problem!, Recommendation = input.Recommendation!, Minutes = input.Minutes,
				RecordedAt = nowUtc, RecordedByEmployeeId = employeeId,
			};
			_db.Add(i);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(i.ID);
		}

		/// The prescriber's answer to an intervention: accepted or declined. Answered once.
		public async Task<HospitalResult> OutcomeAsync(BusinessContext ctx, int interventionId, string? outcome, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Pharmacist(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (outcome is not (HospitalInterventionOutcome.Accepted or HospitalInterventionOutcome.Declined)) return HospitalResult.Fail("not_found");
			var i = await _db.Set<HospitalPharmacyIntervention>().FirstOrDefaultAsync(x => x.ID == interventionId && x.CompanyId == ctx.CompanyId, ct);
			if (i == null) return HospitalResult.Fail("not_found");
			if (i.Outcome != HospitalInterventionOutcome.Pending) return HospitalResult.Fail("The prescriber's answer is already recorded");
			i.Outcome = outcome; i.OutcomeAt = nowUtc; i.OutcomeByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(i.ID);
		}
	}
}
