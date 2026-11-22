using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalPreArrivalInput
	{
		/// Ward-local.
		public DateTime ExpectedAt { get; set; }
		public string? Unit { get; set; }
		public string? Complaint { get; set; }
		public string? AgeText { get; set; }
		public string? Sex { get; set; }
		public int? EsiEstimate { get; set; }
		public string? Pathway { get; set; }
	}

	public sealed record HospitalErPathwayView(HospitalErPathway Pathway, HospitalErVisit Visit, string? PatientName, string? PatientNameEn, string? Photo,
		IReadOnlyList<HospitalErStepState> Steps)
	{
		public HospitalErStepState? Next => Steps.FirstOrDefault(s => s.Done == null);
		public int Overdue => Steps.Count(s => s.State == HospitalErStepStatus.Overdue);
	}
	public sealed record HospitalErPathwayBoard(bool Ready, IReadOnlyList<HospitalErPathwayView> Active, IReadOnlyList<HospitalErPreArrival> Incoming);

	// ==========================================================================================
	// HOSPITAL H237 — the pathways and pre-arrivals of the emergency department. The department's own staff (`admit`)
	// activate, tick and receive; nothing here moves a patient between stages — the board's own moves do that.
	// ==========================================================================================
	public sealed class HospitalErPathwayService
	{
		public const string NoTables = "The emergency pathways are not installed yet (hospital_h237_er_pathways.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalErPathwayService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private Task<bool> StaffAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Admit, null, ct);

		public async Task<HospitalErPathwayBoard> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var rows = await (from p in _db.Set<HospitalErPathway>().AsNoTracking()
								  join v in _db.Set<HospitalErVisit>().AsNoTracking() on p.ErVisitId equals v.ID
								  where p.CompanyId == ctx.CompanyId && v.CompanyId == ctx.CompanyId && p.ClosedAt == null && v.Stage != HospitalErStage.Closed
								  select new { p, v }).ToListAsync(ct);
				var ids = rows.Select(r => r.p.ID).ToList();
				var steps = await _db.Set<HospitalErPathwayStep>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.PathwayId)).ToListAsync(ct);
				var pids = rows.Where(r => r.v.PatientId != null).Select(r => r.v.PatientId!.Value).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && pids.Contains(x.ID)).Select(x => new { x.ID, x.Name, x.NameEn, x.PhotoPath }).ToDictionaryAsync(x => x.ID, ct);
				var active = rows.Select(r =>
				{
					var pt = r.v.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					return new HospitalErPathwayView(r.p, r.v, pt?.Name ?? r.v.UnknownLabel, pt?.NameEn, pt?.PhotoPath,
						HospitalErPathwayRules.Evaluate(r.p.Kind, r.v.ArrivedAt, steps.Where(s => s.PathwayId == r.p.ID).ToList(), nowUtc));
				}).OrderByDescending(x => x.Overdue).ThenBy(x => x.Visit.ArrivedAt).ToList();
				var incoming = await _db.Set<HospitalErPreArrival>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == HospitalPreArrivalStatus.Expected && a.ExpectedAt >= nowUtc.AddHours(-6))
					.OrderBy(a => a.ExpectedAt).ToListAsync(ct);
				return new HospitalErPathwayBoard(true, active, incoming);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalErPathwayBoard(false, Array.Empty<HospitalErPathwayView>(), Array.Empty<HospitalErPreArrival>()); }
		}

		public async Task<HospitalResult> ActivateAsync(BusinessContext ctx, int visitId, string? kind, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalErPathwayKind.All.Contains(kind ?? "")) return HospitalResult.Fail("Choose the pathway");
			try
			{
				var v = await _db.Set<HospitalErVisit>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == visitId && x.CompanyId == ctx.CompanyId, ct);
				if (v == null) return HospitalResult.Fail("not_found");
				if (v.Stage == HospitalErStage.Closed) return HospitalResult.Fail("This visit is closed");
				if (await _db.Set<HospitalErPathway>().AnyAsync(p => p.CompanyId == ctx.CompanyId && p.ErVisitId == visitId && p.Kind == kind, ct)) return HospitalResult.Fail("This pathway is already active for the patient");
				var p = new HospitalErPathway { CompanyId = ctx.CompanyId, ErVisitId = v.ID, PatientId = v.PatientId, Kind = kind!, ActivatedAt = nowUtc, ActivatedByEmployeeId = employeeId };
				_db.Add(p); await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { return HospitalResult.Fail("This pathway is already active for the patient"); }
		}

		/// A step done now, or set aside as not indicated (with why). Once only.
		public async Task<HospitalResult> StepAsync(BusinessContext ctx, int pathwayId, string? code, bool notIndicated, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await _db.Set<HospitalErPathway>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == pathwayId && x.CompanyId == ctx.CompanyId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (p.ClosedAt != null) return HospitalResult.Fail("This pathway is closed");
				if (HospitalErPathwayRules.StepRefusal(p.Kind, code, notIndicated, note) is string why) return HospitalResult.Fail(why);
				if (await _db.Set<HospitalErPathwayStep>().AnyAsync(s => s.CompanyId == ctx.CompanyId && s.PathwayId == p.ID && s.Code == code, ct)) return HospitalResult.Fail("This step is already recorded");
				_db.Add(new HospitalErPathwayStep { CompanyId = ctx.CompanyId, PathwayId = p.ID, Code = code!, At = nowUtc, ByEmployeeId = employeeId, NotIndicated = notIndicated, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int pathwayId, string? note, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.Set<HospitalErPathway>().FirstOrDefaultAsync(x => x.ID == pathwayId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (p.ClosedAt != null) return HospitalResult.Fail("This pathway is closed");
			p.ClosedAt = nowUtc; p.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)];
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(p.ID);
		}

		public async Task<HospitalResult> PreAlertAsync(BusinessContext ctx, HospitalPreArrivalInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var pathway = string.IsNullOrWhiteSpace(i.Pathway) ? null : i.Pathway;
			if (HospitalErPathwayRules.PreArrivalRefusal(i.ExpectedAt, nowUtc.ToLocalTime(), i.Complaint, i.EsiEstimate, pathway) is string why) return HospitalResult.Fail(why);
			try
			{
				var a = new HospitalErPreArrival
				{
					CompanyId = ctx.CompanyId, ExpectedAt = DateTime.SpecifyKind(i.ExpectedAt, DateTimeKind.Local).ToUniversalTime(), Unit = string.IsNullOrWhiteSpace(i.Unit) ? null : i.Unit.Trim(),
					Complaint = i.Complaint!.Trim(), AgeText = string.IsNullOrWhiteSpace(i.AgeText) ? null : i.AgeText.Trim(), Sex = i.Sex is "M" or "F" ? i.Sex : null,
					EsiEstimate = i.EsiEstimate, Pathway = pathway, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(a); await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(a.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The ambulance is at the door: the emergency visit opens through the department's own arrival (by ambulance), and
		/// the pre-alerted pathway starts on it from the real door time.
		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, HospitalEmergencyService er, int preArrivalId, int? patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.Set<HospitalErPreArrival>().FirstOrDefaultAsync(x => x.ID == preArrivalId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalPreArrivalStatus.Expected) return HospitalResult.Fail("This ambulance was already received or cancelled");
			var label = patientId == null ? string.Join(" ", new[] { a.Sex == "F" ? "♀" : a.Sex == "M" ? "♂" : null, a.AgeText, a.Unit }.Where(x => !string.IsNullOrWhiteSpace(x))) : null;
			var visit = await er.ArriveAsync(ctx, new HospitalErArrivalInput { PatientId = patientId, UnknownLabel = patientId == null ? (string.IsNullOrWhiteSpace(label) ? "Ambulance" : label) : null, ArrivalMode = HospitalErArrival.Ambulance, Complaint = a.Complaint, Esi = a.EsiEstimate == 1 ? 1 : null }, employeeId, nowUtc, ct);
			if (!visit.Ok) return visit;
			a.Status = HospitalPreArrivalStatus.Arrived; a.ErVisitId = visit.Id;
			await _db.SaveChangesAsync(ct);
			if (a.Pathway != null) await ActivateAsync(ctx, visit.Id!.Value, a.Pathway, employeeId, nowUtc, ct);
			return HospitalResult.Success(visit.Id);
		}

		public async Task<HospitalResult> CancelPreAlertAsync(BusinessContext ctx, int preArrivalId, CancellationToken ct = default)
		{
			if (!await StaffAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.Set<HospitalErPreArrival>().FirstOrDefaultAsync(x => x.ID == preArrivalId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalPreArrivalStatus.Expected) return HospitalResult.Fail("This ambulance was already received or cancelled");
			a.Status = HospitalPreArrivalStatus.Cancelled; await _db.SaveChangesAsync(ct); return HospitalResult.Success(a.ID);
		}
	}
}
