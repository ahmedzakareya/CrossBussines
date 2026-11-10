using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H29 — the theatre schedule. The decisions are HospitalTheatreRules' (no database); the service reads
	// the day (the theatres, their cases with the patient, the surgeon and the anaesthetist, each with a photo) and
	// writes bookings, moves and the checklist. `book` books and cancels; `consult` (the clinical team) starts,
	// closes and ticks the checklist. Closing charges the procedure to the patient's bill, once.
	// ==========================================================================================

	public sealed class HospitalSurgeryInput
	{
		public int PatientId { get; set; }
		public int RoomId { get; set; }
		public int ServiceId { get; set; }
		public int SurgeonId { get; set; }
		public int? AnaesthetistId { get; set; }
		public DateTime Date { get; set; }
		public int StartMinute { get; set; }
		public int DurationMinutes { get; set; } = 60;
		public string? Note { get; set; }
	}

	public sealed class HospitalSurgeryMoveInput { public string To { get; set; } = ""; public string? Reason { get; set; } }

	/// A case as the schedule shows it: the patient and the two doctors, each with their photo, and its ticked checks.
	public sealed record HospitalTheatreCase(HospitalSurgery Case, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		HospitalPortalDoctorRef? Surgeon, HospitalPortalDoctorRef? Anaesthetist, IReadOnlyList<string> Checks);

	public sealed record HospitalTheatreRoom(int Id, string Number, string Name, string NameEn);

	public sealed record HospitalTheatreDay(DateTime Day, IReadOnlyList<HospitalTheatreRoom> Rooms, IReadOnlyList<HospitalTheatreCase> Cases, int Utilisation, int OnTime, int FirstCases);

	public static class HospitalTheatreRules
	{
		/// The theatres' working session the prototype's timeline draws, and utilisation is measured against.
		public const int DayStart = 8 * 60, DayEnd = 16 * 60;
		/// A first case that starts within this many minutes of its booked time started on time.
		public const int OnTimeGrace = 15;

		public static bool Overlaps(int s1, int d1, int s2, int d2) => s1 < s2 + d2 && s2 < s1 + d1;

		/// Why a case cannot be booked so, or null (the time itself; the clashes are ClashRefusal's).
		public static string? BookingRefusal(DateTime day, int start, int duration, DateTime today, bool surgeonOperates, bool? anaesthetistAnaesthetises)
		{
			if (day.Date < today.Date) return "An operation cannot be booked in the past";
			if (duration is < 15 or > 720 || duration % 5 != 0) return "An operation is 15 minutes to 12 hours, in steps of 5";
			if (start is < 0 or >= 1440 || start + duration > 1440) return "The operation must end the same day";
			if (!surgeonOperates) return "The surgeon needs the surgery privilege";
			if (anaesthetistAnaesthetises == false) return "The anaesthetist needs the anaesthesia privilege";
			return null;
		}

		/// Why it clashes with the day's other cases that still hold their people and room, or null.
		public static string? ClashRefusal(int roomId, int surgeonId, int? anaesthetistId, int start, int duration,
			IEnumerable<(int RoomId, int SurgeonId, int? AnaesthetistId, int Start, int Duration)> others)
		{
			var hits = others.Where(o => Overlaps(start, duration, o.Start, o.Duration)).ToList();
			if (hits.Any(o => o.RoomId == roomId)) return "The theatre is booked at that time";
			if (hits.Any(o => o.SurgeonId == surgeonId || o.AnaesthetistId == surgeonId)) return "The surgeon is in another operation at that time";
			if (anaesthetistId is int a && hits.Any(o => o.AnaesthetistId == a || o.SurgeonId == a)) return "The anaesthetist is in another operation at that time";
			return null;
		}

		/// Why a case cannot make this move, or null. Starting needs the checklist's before-induction items and a free
		/// theatre; closing needs every item; only a planned case is cancelled, and with a reason.
		public static string? MoveRefusal(string from, string to, IReadOnlyCollection<string> checks, bool theatreBusy, string? reason)
		{
			switch (from, to)
			{
				case (HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.InProgress):
					if (HospitalSurgeryChecklist.BeforeStart.Any(i => !checks.Contains(i))) return "Complete the checklist before induction";
					if (theatreBusy) return "Another operation is in progress in this theatre";
					return null;
				case (HospitalSurgeryStatus.InProgress, HospitalSurgeryStatus.Done):
					return HospitalSurgeryChecklist.All.Any(i => !checks.Contains(i)) ? "Complete the checklist before the patient leaves the theatre" : null;
				case (HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.Cancelled):
					return string.IsNullOrWhiteSpace(reason) ? "Write why the operation is cancelled" : null;
				default:
					return "This operation cannot make that move";
			}
		}

		/// Why this checklist item cannot be ticked or unticked now, or null.
		public static string? CheckRefusal(string status, string item)
		{
			if (!HospitalSurgeryChecklist.All.Contains(item)) return "not_found";
			return HospitalSurgeryStatus.Holds(status) ? null : "The checklist closes with the operation";
		}

		/// Booked minutes inside the working session, over what the theatres offer, as a whole percent.
		public static int Utilisation(IEnumerable<(int Start, int Duration, string Status)> cases, int theatres)
		{
			if (theatres <= 0) return 0;
			int used = cases.Where(c => c.Status != HospitalSurgeryStatus.Cancelled)
				.Sum(c => Math.Max(0, Math.Min(c.Start + c.Duration, DayEnd) - Math.Max(c.Start, DayStart)));
			return (int)Math.Round(used * 100.0 / (theatres * (DayEnd - DayStart)));
		}

		/// Of the theatres whose first case has started, how many started it on time (within the grace).
		public static (int OnTime, int Started) FirstCaseOnTime(IEnumerable<(int RoomId, int Start, string Status, DateTime? StartedAt)> cases)
		{
			var firsts = cases.Where(c => c.Status != HospitalSurgeryStatus.Cancelled).GroupBy(c => c.RoomId)
				.Select(g => g.OrderBy(c => c.Start).First()).Where(c => c.StartedAt != null).ToList();
			return (firsts.Count(c => c.StartedAt!.Value.Hour * 60 + c.StartedAt.Value.Minute <= c.Start + OnTimeGrace), firsts.Count);
		}
	}

	public sealed class HospitalTheatreService
	{
		public const string NoTables = "The theatre tables are not created yet (hospital_h29_theatre.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;

		public HospitalTheatreService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) { _db = db; _access = access; _billing = billing; }

		/// The refusal when a case has no sterile set issued to it (owner 2026-11-08).
		public const string NoSterileSet = "Issue a sterile instrument set to this operation from the sterile supply first";

		/// Whether the case may start as far as the sterile supply goes: a set has been issued to it, or the hospital
		/// registers no instrument sets at all (it does not run its sterile supply through the system).
		public async Task<bool> HasSterileSetAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default)
		{
			try
			{
				if (!await _db.Set<HospitalInstrumentSet>().AnyAsync(s => s.CompanyId == ctx.CompanyId && s.IsActive, ct)) return true;
				return await _db.Set<HospitalSetEvent>().AnyAsync(e => e.CompanyId == ctx.CompanyId && e.SurgeryId == surgeryId && e.Kind == HospitalSetEventKind.Issued, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return true; }   // no sterile supply tables
		}

		/// The sets issued to the case (their codes), for the case card to show.
		public async Task<List<string>> IssuedSetsAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default)
		{
			try
			{
				var ids = await _db.Set<HospitalSetEvent>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.SurgeryId == surgeryId && e.Kind == HospitalSetEventKind.Issued).Select(e => e.SetId).Distinct().ToListAsync(ct);
				return await _db.Set<HospitalInstrumentSet>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.ID)).OrderBy(s => s.Code).Select(s => s.Code).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new List<string>(); }
		}

		/// H60: whether the case has a signed procedure consent in force.
		public Task<bool> HasConsentAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default) =>
			HospitalConsentRules.CoveringCase(_db.Set<HospitalConsent>(), ctx.CompanyId, surgeryId).AnyAsync(ct);

		/// H60: the number of the consent that covers the case (the newest), for the checklist to show.
		public Task<string?> ConsentNoAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default) =>
			HospitalConsentRules.CoveringCase(_db.Set<HospitalConsent>(), ctx.CompanyId, surgeryId).OrderByDescending(c => c.ID).Select(c => (string?)c.ConsentNo).FirstOrDefaultAsync(ct);

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalSurgery> Cases(BusinessContext ctx) => _db.Set<HospitalSurgery>().Where(s => s.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<IReadOnlyList<HospitalTheatreRoom>> TheatresAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.IsActive && r.Kind == HospitalRoomKinds.Theatre).OrderBy(r => r.Number).ToListAsync(ct))
				.Select(r => new HospitalTheatreRoom(r.ID, r.Number, r.Name ?? r.Number, r.NameEn ?? r.Name ?? r.Number)).ToList();

		/// The practitioners with their photo (one call, so every picker and card shows the same faces).
		public async Task<IReadOnlyList<(HospitalPortalDoctorRef Doctor, string Privileges)>> TeamAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(p => p.EmployeeId != null).Select(p => p.EmployeeId!.Value), ct);
			return rows.Select(p => (new HospitalPortalDoctorRef(p.ID, p.Name, p.NameEn ?? p.Name, p.Title, p.TitleEn, p.EmployeeId is int e ? photos.GetValueOrDefault(e) : null), p.Privileges ?? "")).ToList();
		}

		/// A day in the theatres: every theatre, its cases (none cancelled), and the prototype's figures.
		public async Task<(HospitalTheatreDay? Day, bool Ready)> DayAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			var rooms = await TheatresAsync(ctx, ct);
			List<HospitalSurgery> cases; List<HospitalSurgeryCheck> checks;
			try
			{
				cases = await Cases(ctx).AsNoTracking().Where(s => s.CaseDate == day.Date).OrderBy(s => s.StartMinute).ToListAsync(ct);
				var ids = cases.Select(c => c.ID).ToList();
				checks = await _db.Set<HospitalSurgeryCheck>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && ids.Contains(c.SurgeryId)).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (new HospitalTheatreDay(day.Date, rooms, Array.Empty<HospitalTheatreCase>(), 0, 0, 0), false); }
			var patIds = cases.Select(c => c.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var team = (await TeamAsync(ctx, ct)).ToDictionary(t => t.Doctor.Id, t => t.Doctor);
			var shown = cases.Where(c => pats.ContainsKey(c.PatientId)).Select(c =>
			{
				var p = pats[c.PatientId];
				return new HospitalTheatreCase(c, p.Name, p.NameEn ?? p.Name, p.Mrn, p.PhotoPath, team.GetValueOrDefault(c.SurgeonId),
					c.AnaesthetistId is int a ? team.GetValueOrDefault(a) : null, checks.Where(k => k.SurgeryId == c.ID).Select(k => k.Item).ToList());
			}).ToList();
			var live = shown.Where(c => c.Case.Status != HospitalSurgeryStatus.Cancelled).ToList();
			var (onTime, started) = HospitalTheatreRules.FirstCaseOnTime(live.Select(c => (c.Case.RoomId, c.Case.StartMinute, c.Case.Status, c.Case.StartedAt)));
			return (new HospitalTheatreDay(day.Date, rooms, shown, HospitalTheatreRules.Utilisation(live.Select(c => (c.Case.StartMinute, c.Case.DurationMinutes, c.Case.Status)), rooms.Count), onTime, started), true);
		}

		public async Task<HospitalResult> BookAsync(BusinessContext ctx, HospitalSurgeryInput input, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			var room = await _db.HospitalRooms.AsNoTracking().FirstOrDefaultAsync(r => r.ID == input.RoomId && r.CompanyId == ctx.CompanyId && r.IsActive && r.Kind == HospitalRoomKinds.Theatre, ct);
			var service = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId && s.IsActive, ct);
			var surgeon = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.SurgeonId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			var anaes = input.AnaesthetistId is int aid ? await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == aid && p.CompanyId == ctx.CompanyId && p.IsActive, ct) : null;
			if (patient == null || room == null || service == null || surgeon == null || (input.AnaesthetistId != null && anaes == null)) return HospitalResult.Fail("not_found");
			static bool Has(HospitalPractitioner p, string priv) => (p.Privileges ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(priv);
			if (HospitalTheatreRules.BookingRefusal(input.Date, input.StartMinute, input.DurationMinutes, nowLocal, Has(surgeon, "surg"), anaes == null ? null : Has(anaes, "anes")) is string why) return HospitalResult.Fail(why);
			if (input.AnaesthetistId == input.SurgeonId) return HospitalResult.Fail("The surgeon and the anaesthetist are two people");
			// as the clinics: an expired licence stops new bookings until the renewed one is entered
			if (!HospitalOutpatientService.LicenseValidOn(surgeon, input.Date)) return HospitalResult.Fail("The surgeon's professional licence has expired");
			if (anaes != null && !HospitalOutpatientService.LicenseValidOn(anaes, input.Date)) return HospitalResult.Fail("The anaesthetist's professional licence has expired");
			var day = input.Date.Date;
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db, System.Data.IsolationLevel.Serializable);
				var others = await Cases(ctx).AsNoTracking().Where(s => s.CaseDate == day && (s.Status == HospitalSurgeryStatus.Planned || s.Status == HospitalSurgeryStatus.InProgress))
					.Select(s => new { s.RoomId, s.SurgeonId, s.AnaesthetistId, s.StartMinute, s.DurationMinutes }).ToListAsync(ct);
				if (HospitalTheatreRules.ClashRefusal(room.ID, surgeon.ID, anaes?.ID, input.StartMinute, input.DurationMinutes,
					others.Select(o => (o.RoomId, o.SurgeonId, o.AnaesthetistId, o.StartMinute, o.DurationMinutes))) is string clash) { await tx.RollbackAsync(); return HospitalResult.Fail(clash); }
				var admission = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patient.ID && a.Status == HospitalAdmissionStatus.Active)
					.OrderByDescending(a => a.AdmittedAt).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
				var nums = await Cases(ctx).Select(s => s.CaseNo).ToListAsync(ct);
				int max = nums.Select(n => int.TryParse(n.AsSpan(3), out var v) ? v : 0).DefaultIfEmpty(0).Max();
				var row = new HospitalSurgery
				{
					CompanyId = ctx.CompanyId, CaseNo = "OR-" + (max + 1).ToString("000000"), PatientId = patient.ID, AdmissionId = admission, RoomId = room.ID, ServiceId = service.ID,
					Code = service.Code, Name = service.Name, NameEn = service.NameEn ?? service.Name, SurgeonId = surgeon.ID, AnaesthetistId = anaes?.ID, CaseDate = day,
					StartMinute = input.StartMinute, DurationMinutes = input.DurationMinutes, Status = HospitalSurgeryStatus.Planned, Note = Clip(input.Note, 500),
					CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Ticks (on) or unticks (off) one checklist item, while the case is open.
		public async Task<HospitalResult> CheckAsync(BusinessContext ctx, int surgeryId, string item, bool on, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.ID == surgeryId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (HospitalTheatreRules.CheckRefusal(c.Status, item) is string why) return HospitalResult.Fail(why);
			if (on && item == HospitalSurgeryChecklist.Consent && !await HasConsentAsync(ctx, surgeryId, ct)) return HospitalResult.Fail(HospitalConsentRules.NoCaseConsent);
			var set = _db.Set<HospitalSurgeryCheck>();
			if (!on)
			{
				await set.Where(k => k.CompanyId == ctx.CompanyId && k.SurgeryId == surgeryId && k.Item == item).ExecuteDeleteAsync(ct);
				return HospitalResult.Success(surgeryId);
			}
			if (await set.AnyAsync(k => k.CompanyId == ctx.CompanyId && k.SurgeryId == surgeryId && k.Item == item, ct)) return HospitalResult.Success(surgeryId);
			set.Add(new HospitalSurgeryCheck { CompanyId = ctx.CompanyId, SurgeryId = surgeryId, Item = item, CheckedAt = nowUtc, CheckedByEmployeeId = employeeId });
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // ticked by someone else at the same moment: it is ticked
			return HospitalResult.Success(surgeryId);
		}

		/// Starts, closes or cancels a case. Closing charges the procedure to the patient's bill in the same transaction.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int surgeryId, HospitalSurgeryMoveInput input, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			var action = input.To == HospitalSurgeryStatus.Cancelled ? HospitalActions.Book : HospitalActions.Consult;
			if (!await Can(ctx, action, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.ID == surgeryId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			var checks = await _db.Set<HospitalSurgeryCheck>().AsNoTracking().Where(k => k.CompanyId == ctx.CompanyId && k.SurgeryId == surgeryId).Select(k => k.Item).ToListAsync(ct);
			bool busy = input.To == HospitalSurgeryStatus.InProgress && await Cases(ctx).AnyAsync(s => s.RoomId == c.RoomId && s.Status == HospitalSurgeryStatus.InProgress && s.ID != surgeryId, ct);
			if (HospitalTheatreRules.MoveRefusal(c.Status, input.To, checks, busy, input.Reason) is string why) return HospitalResult.Fail(why);
			// H60: a tick made before consents existed (or a consent withdrawn since) does not start an operation
			if (input.To == HospitalSurgeryStatus.InProgress && !await HasConsentAsync(ctx, surgeryId, ct)) return HospitalResult.Fail(HospitalConsentRules.NoCaseConsent);
			if (input.To == HospitalSurgeryStatus.InProgress && !await HasSterileSetAsync(ctx, surgeryId, ct)) return HospitalResult.Fail(NoSterileSet);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var q = Cases(ctx).Where(s => s.ID == surgeryId && s.Status == c.Status);   // nothing moves a case twice
			int n = input.To switch
			{
				HospitalSurgeryStatus.InProgress => await q.ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, input.To).SetProperty(s => s.StartedAt, nowLocal).SetProperty(s => s.UpdatedAt, DateTime.UtcNow).SetProperty(s => s.UpdatedByEmployeeId, employeeId), ct),
				HospitalSurgeryStatus.Done => await q.ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, input.To).SetProperty(s => s.EndedAt, nowLocal).SetProperty(s => s.UpdatedAt, DateTime.UtcNow).SetProperty(s => s.UpdatedByEmployeeId, employeeId), ct),
				_ => await q.ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, input.To).SetProperty(s => s.CancelReason, Clip(input.Reason, 300)).SetProperty(s => s.UpdatedAt, DateTime.UtcNow).SetProperty(s => s.UpdatedByEmployeeId, employeeId), ct),
			};
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Someone else changed this operation: open it again"); }
			if (input.To == HospitalSurgeryStatus.Done && c.BillLineId == null)
			{
				// the stay's bill when admitted, otherwise the patient's own open bill
				HospitalBill? bill = null;
				if (c.AdmissionId is int adm && (await _billing.EnsureAdmissionBillAsync(ctx, adm, employeeId, nowLocal, ct)).Id is int billId)
					bill = await _db.HospitalBills.FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId && b.Status == HospitalBillStatus.Open, ct);
				bill ??= await _billing.BillForDispenseAsync(ctx, c.PatientId, null, employeeId, nowLocal, ct);
				var line = await _billing.AddServiceChargeAsync(ctx, bill, c.ServiceId, employeeId, nowLocal, ct);
				await _db.SaveChangesAsync(ct);
				if (line != null) await Cases(ctx).Where(s => s.ID == surgeryId).ExecuteUpdateAsync(u => u.SetProperty(s => s.BillLineId, line.ID), ct);
			}
			await tx.CommitAsync();
			return HospitalResult.Success(surgeryId);
		}
	}
}
