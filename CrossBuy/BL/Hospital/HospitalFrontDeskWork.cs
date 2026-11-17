using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H141 — the desk's work, as the approved prototype shows it under the board (design-output/hospital-prototype
	// #/frontdesk): each tab lists what waits, not only how many.
	//   · CONFIRM TOMORROW: tomorrow's booked appointments with the phone, the reminder SMS's state (H70 sends it on its own)
	//     and what the patient said when the desk called (confirmed, no answer, wants to move).
	//   · RESCHEDULE: a booked appointment moved to another free slot (same or another doctor, any day from today). The
	//     outpatient service books the new one and cancels the old in one transaction; a patient who asked to move is
	//     confirmed on the new one.
	//   · THE QUEUES: portal booking requests, waitlist offers, self-intake forms, pending pre-authorisations, unidentified
	//     patients and possible duplicate files — the oldest first, each opening its own screen where the act lives.
	// ==========================================================================================

	public sealed record HospitalDeskConfirmRow(HospitalAppointmentView A, string Doctor, string? DoctorEn, string? DoctorPhoto, string? Department, string? DepartmentEn,
		string? Phone, HospitalDeskConfirmation? Answer, string? Reminder);

	/// One waiting item of a work queue. Line/LineEn is data (a department, a doctor, a payer, a description), never prose.
	public sealed record HospitalDeskWorkItem(string Kind, int? PatientId, string Name, string? NameEn, string? Photo, string? Mrn, string? Code,
		string? Line, string? LineEn, DateTime? At, DateTime? Due, bool Urgent, decimal? Amount, string? Extra);

	public static class HospitalFrontDeskWorkRules
	{
		public static readonly IReadOnlyList<string> Kinds = new[] { "confirm", "portal", "waitlist", "intake", "preauth", "unidentified", "dup" };

		/// The answer to record on a call: a known status; a "no answer" counts one more call.
		public static string? Refuse(string? status, string? appointmentStatus)
		{
			if (!HospitalConfirmStatus.IsKnown(status)) return "Unknown answer";
			if (appointmentStatus != HospitalAppointmentStatus.Booked) return "Only a booked appointment is confirmed";
			return null;
		}

		/// A move needs a booked appointment and a day from today on.
		public static string? RefuseMove(string? appointmentStatus, DateTime newDay, DateTime today)
		{
			if (appointmentStatus != HospitalAppointmentStatus.Booked) return "Only a booked appointment can be moved";
			if (newDay.Date < today.Date) return "An appointment cannot be booked in the past";
			return null;
		}

		/// The slot picked must be one of the doctor's free slots that day; a slot taken meanwhile is refused, never moved.
		public static bool SlotFree(IReadOnlyList<int> free, int minute) => free.Contains(minute);
	}

	public sealed class HospitalFrontDeskWorkService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalOutpatientService _op;
		private readonly HospitalPortalBookingService _portal;

		public HospitalFrontDeskWorkService(CrossDbContext db, IHospitalAccessService access, HospitalOutpatientService op, HospitalPortalBookingService portal)
		{ _db = db; _access = access; _op = op; _portal = portal; }

		private Task<bool> CanBook(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Book, null, ct);

		private static async Task<T> Safe<T>(Func<Task<T>> f, T empty)
		{
			try { return await f(); } catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return empty; }
		}

		// ====================================================================================
		// CONFIRM TOMORROW
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDeskConfirmRow>> ConfirmListAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var appts = (await _op.DayAsync(ctx, day.Date, ct)).Where(a => a.Status == HospitalAppointmentStatus.Booked).OrderBy(a => a.StartMinute).ToList();
			if (appts.Count == 0) return Array.Empty<HospitalDeskConfirmRow>();
			var docs = (await _op.PractitionersAsync(ctx, ct)).ToDictionary(p => p.Row.ID);
			var pids = appts.Select(a => a.PatientId).Distinct().ToList();
			var phones = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == cid && pids.Contains(p.ID)).Select(p => new { p.ID, p.Phone }).ToDictionaryAsync(p => p.ID, p => p.Phone, ct);
			var ids = appts.Select(a => a.Id).ToList();
			var answers = await Safe(() => _db.Set<HospitalDeskConfirmation>().AsNoTracking().Where(c => c.CompanyId == cid && ids.Contains(c.AppointmentId)).ToDictionaryAsync(c => c.AppointmentId, ct), new Dictionary<int, HospitalDeskConfirmation>());
			var keys = ids.Select(i => $"appt:{i}:reminder").ToList();
			var sms = await Safe(async () => (await _db.Set<HospitalSmsMessage>().AsNoTracking().Where(m => m.CompanyId == cid && keys.Contains(m.DedupKey)).Select(m => new { m.SourceId, m.Status }).ToListAsync(ct))
				.Where(m => m.SourceId != null).GroupBy(m => m.SourceId!.Value).ToDictionary(g => g.Key, g => g.First().Status), new Dictionary<int, string>());
			return appts.Select(a =>
			{
				var d = docs.GetValueOrDefault(a.PractitionerId);
				return new HospitalDeskConfirmRow(a, d?.Row.Name ?? "—", d?.Row.NameEn, d?.PhotoPath, d?.Department?.Name, d?.Department?.NameEn,
					phones.GetValueOrDefault(a.PatientId), answers.GetValueOrDefault(a.Id), sms.GetValueOrDefault(a.Id));
			}).ToList();
		}

		/// How many of the day's booked appointments nobody has confirmed yet.
		public async Task<int> UnconfirmedAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId; var d = day.Date;
			var ids = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == cid && a.AppointmentDate == d && a.Status == HospitalAppointmentStatus.Booked).Select(a => a.ID).ToListAsync(ct);
			if (ids.Count == 0) return 0;
			int done = await Safe(() => _db.Set<HospitalDeskConfirmation>().CountAsync(c => c.CompanyId == cid && ids.Contains(c.AppointmentId) && c.Status == HospitalConfirmStatus.Confirmed, ct), 0);
			return ids.Count - done;
		}

		public async Task<HospitalResult> ConfirmAsync(BusinessContext ctx, int appointmentId, string? status, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanBook(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.HospitalAppointments.AsNoTracking().Where(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.Status }).FirstOrDefaultAsync(ct);
			if (a == null) return HospitalResult.Fail("not_found");
			var why = HospitalFrontDeskWorkRules.Refuse(status, a.Status);
			if (why != null) return HospitalResult.Fail(why);
			note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			if (note?.Length > 300) note = note[..300];
			var set = _db.Set<HospitalDeskConfirmation>();
			var row = await set.FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.AppointmentId == a.ID, ct);
			if (row == null) { row = new HospitalDeskConfirmation { CompanyId = ctx.CompanyId, AppointmentId = a.ID }; set.Add(row); }
			row.Status = status!; row.Note = note ?? row.Note; row.Calls += 1; row.LastAt = nowUtc; row.ByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		// ====================================================================================
		// RESCHEDULE
		// ====================================================================================

		/// A doctor's free slots on a day (from now on when the day is today).
		public async Task<IReadOnlyList<int>> SlotsAsync(BusinessContext ctx, int practitionerId, DateTime day, DateTime nowLocal, CancellationToken ct = default)
			=> day.Date < nowLocal.Date ? Array.Empty<int>() : (await _portal.StaffFreeSlotsAsync(ctx, practitionerId, day.Date, nowLocal, ct)).Where(m => day.Date > nowLocal.Date || m >= nowLocal.Hour * 60 + nowLocal.Minute - 10).OrderBy(m => m).ToList();

		/// Moves a booked appointment: the new one is booked and the old cancelled together, or neither.
		public async Task<HospitalResult> RescheduleAsync(BusinessContext ctx, int appointmentId, int practitionerId, DateTime day, int startMinute, int? employeeId, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanBook(ctx, ct)) return HospitalResult.Fail("forbidden");
			var old = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (old == null) return HospitalResult.Fail("not_found");
			var why = HospitalFrontDeskWorkRules.RefuseMove(old.Status, day, nowLocal.Date);
			if (why != null) return HospitalResult.Fail(why);
			if (practitionerId == old.PractitionerId && day.Date == old.AppointmentDate.Date && startMinute == old.StartMinute) return HospitalResult.Fail("Pick another time");
			var free = await SlotsAsync(ctx, practitionerId, day, nowLocal, ct);
			if (!HospitalFrontDeskWorkRules.SlotFree(free, startMinute)) return HospitalResult.Fail("This slot is no longer free");
			bool wanted = await Safe(() => _db.Set<HospitalDeskConfirmation>().AnyAsync(c => c.CompanyId == ctx.CompanyId && c.AppointmentId == old.ID && c.Status == HospitalConfirmStatus.WantsToMove, ct), false);

			// H161: the old one is cancelled FIRST, so a move to the same hour with another doctor does not collide with the
			// patient's own appointment; a booking that then fails rolls the cancellation back with it.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var cancelled = await _op.MoveAsync(ctx, old.ID, HospitalAppointmentStatus.Cancelled, employeeId, nowUtc, ct);
			if (!cancelled.Ok) { await tx.RollbackAsync(); return cancelled; }
			var booked = await _op.BookAsync(ctx, new HospitalBookingInput { PatientId = old.PatientId, PractitionerId = practitionerId, Date = day.Date, StartMinute = startMinute, VisitType = old.VisitType, Note = old.Note },
				employeeId, nowLocal.Date, ct);
			if (!booked.Ok) { await tx.RollbackAsync(); return booked; }
			if (wanted)
			{
				_db.Set<HospitalDeskConfirmation>().Add(new HospitalDeskConfirmation { CompanyId = ctx.CompanyId, AppointmentId = booked.Id!.Value, Status = HospitalConfirmStatus.Confirmed, Note = "Moved at the patient's request", Calls = 1, LastAt = nowUtc, ByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
			}
			await tx.CommitAsync();
			return HospitalResult.Success(booked.Id);
		}

		// ====================================================================================
		// THE QUEUES
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDeskWorkItem>> WorkAsync(BusinessContext ctx, string kind, DateTime nowUtc, int take = 8, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var empty = (IReadOnlyList<HospitalDeskWorkItem>)Array.Empty<HospitalDeskWorkItem>();
			async Task<Dictionary<int, HospitalPatient>> Patients(IEnumerable<int> ids)
			{
				var list = ids.Distinct().ToList();
				return await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == cid && list.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			}
			switch (kind)
			{
				case "portal":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var rows = await _db.Set<HospitalPortalRequest>().AsNoTracking().Where(r => r.CompanyId == cid && r.Status == HospitalPortalRequestStatus.Open).OrderBy(r => r.CreatedAt).Take(take).ToListAsync(ct);
						var accIds = rows.Select(r => r.AccountId).ToList();
						var accounts = await _db.Set<HospitalPortalAccount>().AsNoTracking().Where(a => a.CompanyId == cid && accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, ct);
						var depIds = rows.Select(r => r.DepartmentId).ToList();
						var deps = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == cid && depIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
						var pts = await Patients(rows.Where(r => r.PatientId != null).Select(r => r.PatientId!.Value));
						return rows.Select(r =>
						{
							var p = r.PatientId is int pid ? pts.GetValueOrDefault(pid) : null; var acc = accounts.GetValueOrDefault(r.AccountId); var d = deps.GetValueOrDefault(r.DepartmentId);
							return new HospitalDeskWorkItem(kind, p?.ID, p?.Name ?? acc?.Name ?? r.RequestNo, p?.NameEn ?? acc?.NameEn, p?.PhotoPath, p?.Mrn, r.RequestNo, d?.Name, d?.NameEn, r.CreatedAt, null, false, null, r.TimeOfDay);
						}).ToList();
					}, empty);
				case "waitlist":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var rows = await _db.Set<HospitalPortalOffer>().AsNoTracking().Where(o => o.CompanyId == cid && o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt > nowUtc).OrderBy(o => o.ExpiresAt).Take(take).ToListAsync(ct);
						var pts = await Patients(rows.Select(r => r.PatientId));
						var docIds = rows.Select(r => r.PractitionerId).ToList();
						var docs = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == cid && docIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
						return rows.Select(r =>
						{
							var p = pts.GetValueOrDefault(r.PatientId); var d = docs.GetValueOrDefault(r.PractitionerId);
							return new HospitalDeskWorkItem(kind, p?.ID, p?.Name ?? "—", p?.NameEn, p?.PhotoPath, p?.Mrn, (r.StartMinute / 60).ToString("00") + ":" + (r.StartMinute % 60).ToString("00"),
								d?.Name, d?.NameEn, r.OfferDate, r.ExpiresAt, false, null, null);
						}).ToList();
					}, empty);
				case "intake":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var rows = await _db.Set<HospitalSelfIntake>().AsNoTracking().Where(i => i.CompanyId == cid && i.Status == HospitalIntakeStatus.Submitted)
							.OrderByDescending(i => i.Urgent).ThenBy(i => i.SubmittedAt).Take(take).ToListAsync(ct);
						return rows.Select(r => new HospitalDeskWorkItem(kind, r.PatientId, r.Name ?? r.NameEn ?? r.IntakeNo, r.NameEn, null, null, r.TicketNo, r.Reason, null, r.SubmittedAt, null, r.Urgent, r.MatchScore, r.RedFlags)).ToList();
					}, empty);
				case "preauth":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var rows = await _db.Set<HospitalPreauth>().AsNoTracking().Where(p => p.CompanyId == cid && p.Status == HospitalPreauthStatus.Pending).OrderBy(p => p.RequestedAt).Take(take).ToListAsync(ct);
						var pts = await Patients(rows.Select(r => r.PatientId));
						var payIds = rows.Select(r => r.PayerId).ToList();
						var payers = await _db.Set<HospitalPayer>().AsNoTracking().Where(y => y.CompanyId == cid && payIds.Contains(y.ID)).ToDictionaryAsync(y => y.ID, ct);
						return rows.Select(r =>
						{
							var p = pts.GetValueOrDefault(r.PatientId); var y = payers.GetValueOrDefault(r.PayerId);
							return new HospitalDeskWorkItem(kind, p?.ID, p?.Name ?? "—", p?.NameEn, p?.PhotoPath, p?.Mrn, r.RequestNo, y?.Name, y?.NameEn, r.RequestedAt, null, false, r.Amount, null);
						}).ToList();
					}, empty);
				case "unidentified":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var rows = await _db.Set<HospitalUnidentified>().AsNoTracking().Where(u => u.CompanyId == cid && u.IdentifiedAt == null && u.MergedIntoPatientId == null).OrderBy(u => u.CreatedAt).Take(take).ToListAsync(ct);
						var pts = await Patients(rows.Select(r => r.PatientId));
						return rows.Select(r =>
						{
							var p = pts.GetValueOrDefault(r.PatientId);
							return new HospitalDeskWorkItem(kind, p?.ID, p?.Name ?? "—", p?.NameEn, p?.PhotoPath, p?.Mrn, r.EstimatedAge.ToString(), r.Description, null, r.CreatedAt, null, r.MedicoLegal, null, r.BroughtBy);
						}).ToList();
					}, empty);
				case "dup":
					return await Safe<IReadOnlyList<HospitalDeskWorkItem>>(async () =>
					{
						var pairs = await new HospitalPatientMergeService(_db, _access).CandidatesAsync(ctx, 2000, ct);
						return pairs.Take(take).Select(x => new HospitalDeskWorkItem(kind, x.A.ID, x.A.Name, x.A.NameEn, x.A.PhotoPath, x.A.Mrn, x.B.Mrn, x.B.Name, x.B.NameEn, null, null, false, null, string.Join(",", x.Reasons))).ToList();
					}, empty);
				default:
					return empty;
			}
		}
	}
}
