using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H179 — the signed dental treatment session, and the informed consent of a plan phase.
	//   · A session record gathers the patient's plan work completed and not yet recorded, with the anaesthetic, the
	//     materials (and lots), complications and post-op instructions; signed by the dentist when written (`consult`).
	//   · The plan's completed work that no session records yet is listed, so nothing done goes unrecorded.
	//   · A phase's treatment is started only under a signed procedure consent for that phase — informed consent, apart
	//     from the patient's acceptance of the phase's price; diagnostic and preventive work needs none.
	// ==========================================================================================
	public sealed class HospitalDentalSessionInput
	{
		public List<int> LineIds { get; set; } = new();
		public string? Anaesthetic { get; set; }
		public string? AnaestheticAmount { get; set; }
		public string? AnaestheticSite { get; set; }
		public string? Materials { get; set; }
		public string? Complications { get; set; }
		public string? PostOpInstructions { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalSessionLine(int LineId, int? Tooth, string? Surfaces, string Procedure, string ProcedureEn, DateTime? CompletedAt);
	public sealed record HospitalDentalSessionRow(HospitalDentalSession Session, string Dentist, IReadOnlyList<HospitalSessionLine> Lines);

	public static class HospitalDentalSessionRules
	{
		public const string NoPhaseConsent = "Take the patient's signed consent for this phase's treatment first";

		/// Work that needs informed consent: everything but diagnostic and preventive.
		public static bool NeedsConsent(string category) => category is not (HospitalDentalCategory.Diagnostic or HospitalDentalCategory.Preventive);

		/// The consents that cover a phase: a procedure consent, given, signed, not withdrawn.
		public static IQueryable<HospitalConsent> CoveringPhase(IQueryable<HospitalConsent> all, int companyId, int phaseId) =>
			all.Where(c => c.CompanyId == companyId && c.DentalPhaseId == phaseId && c.Kind == HospitalConsentKind.Procedure
				&& c.Decision == HospitalConsentDecision.Given && c.WithdrawnAt == null && c.SignatureDocumentId != null);

		public static string? Refusal(HospitalDentalSessionInput i, int found)
		{
			if (i.LineIds.Count == 0) return "Choose the work done in this session";
			if (found != i.LineIds.Distinct().Count()) return "Only this patient's completed work not yet in a session is recorded";
			if (string.IsNullOrWhiteSpace(i.Anaesthetic)) return "Write the anaesthetic given, or \"none\"";
			if (string.IsNullOrWhiteSpace(i.PostOpInstructions)) return "Write the instructions given to the patient";
			return null;
		}
	}

	public sealed class HospitalDentalSessionService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDentalSessionService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// The patient's completed plan work that no session records yet.
		public async Task<IReadOnlyList<HospitalSessionLine>> UnrecordedAsync(BusinessContext ctx, int patientId, CancellationToken ct = default) =>
			await LinesAsync(ctx, patientId, l => l.SessionId == null, ct);

		private async Task<IReadOnlyList<HospitalSessionLine>> LinesAsync(BusinessContext ctx, int patientId, System.Linq.Expressions.Expression<Func<HospitalDentalPlanLine, bool>> filter, CancellationToken ct)
		{
			var q = from l in _db.Set<HospitalDentalPlanLine>().AsNoTracking().Where(filter)
					join p in _db.Set<HospitalDentalPlan>().AsNoTracking() on l.PlanId equals p.ID
					join s in _db.Set<HospitalService>().AsNoTracking() on l.ServiceId equals s.ID
					where l.CompanyId == ctx.CompanyId && p.PatientId == patientId && l.Status == HospitalDentalLineStatus.Completed
					orderby l.CompletedAt
					select new HospitalSessionLine(l.ID, l.Tooth, l.Surfaces, s.Name, s.NameEn, l.CompletedAt);
			return await q.ToListAsync(ct);
		}

		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int patientId, HospitalDentalSessionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var ids = input.LineIds.Distinct().ToList();
			var found = (await UnrecordedAsync(ctx, patientId, ct)).Count(l => ids.Contains(l.LineId));
			if (HospitalDentalSessionRules.Refusal(input, found) is string why) return HospitalResult.Fail(why);
			var doctor = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == me && p.IsActive).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct);
			await using var tx = _db.Database.IsRelational() ? await ScopedTx.BeginOrJoinAsync(_db) : null;
			var nos = await _db.Set<HospitalDentalSession>().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.SessionNo).ToListAsync(ct);
			var row = new HospitalDentalSession
			{
				CompanyId = ctx.CompanyId, SessionNo = HospitalDentalPracticeRules.NextNo(nos, "DS-", 6), PatientId = patientId, PractitionerId = doctor, SessionAt = nowUtc,
				Anaesthetic = Clip(input.Anaesthetic, 100), AnaestheticAmount = Clip(input.AnaestheticAmount, 40), AnaestheticSite = Clip(input.AnaestheticSite, 100),
				Materials = Clip(input.Materials, 500), Complications = Clip(input.Complications, 500), PostOpInstructions = Clip(input.PostOpInstructions, 500),
				Note = Clip(input.Note, 1000), SignedAt = nowUtc, SignedByEmployeeId = me,
			};
			_db.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			int n = await _db.Set<HospitalDentalPlanLine>().Where(l => l.CompanyId == ctx.CompanyId && ids.Contains(l.ID) && l.SessionId == null)
				.ExecuteUpdateAsync(u => u.SetProperty(l => l.SessionId, row.ID), ct);
			if (n != ids.Count) { if (tx != null) await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			if (tx != null) await tx.CommitAsync();
			return HospitalResult.Success(row.ID);
		}

		public async Task<IReadOnlyList<HospitalDentalSessionRow>> SessionsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try
			{
				var ss = await _db.Set<HospitalDentalSession>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.PatientId == patientId).OrderByDescending(s => s.SessionAt).Take(50).ToListAsync(ct);
				var sids = ss.Select(s => s.ID).ToList();
				var lines = await LinesAsync(ctx, patientId, l => l.SessionId != null && sids.Contains(l.SessionId.Value), ct);
				var lineSession = await _db.Set<HospitalDentalPlanLine>().AsNoTracking().Where(l => l.SessionId != null && sids.Contains(l.SessionId.Value)).Select(l => new { l.ID, l.SessionId }).ToListAsync(ct);
				var eids = ss.Select(s => s.SignedByEmployeeId).Distinct().ToList();
				var names = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				return ss.Select(s => new HospitalDentalSessionRow(s, names.FirstOrDefault(n => n.ID == s.SignedByEmployeeId) is { } n ? DisplayName.Of(n.FullName ?? "", n.FullNameEn) : "—",
					lines.Where(l => lineSession.Any(x => x.ID == l.LineId && x.SessionId == s.ID)).ToList())).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalDentalSessionRow>(); }
		}

		/// Each phase of the plan: whether a signed procedure consent covers it (consent id), or null.
		public async Task<IReadOnlyDictionary<int, HospitalConsent>> PhaseConsentsAsync(BusinessContext ctx, IReadOnlyList<int> phaseIds, CancellationToken ct = default)
		{
			try
			{
				var cs = await _db.Set<HospitalConsent>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.DentalPhaseId != null && phaseIds.Contains(c.DentalPhaseId.Value)
					&& c.Kind == HospitalConsentKind.Procedure && c.Decision == HospitalConsentDecision.Given && c.WithdrawnAt == null && c.SignatureDocumentId != null).ToListAsync(ct);
				return cs.GroupBy(c => c.DentalPhaseId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.SignedAt).First());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new Dictionary<int, HospitalConsent>(); }
		}
	}
}
