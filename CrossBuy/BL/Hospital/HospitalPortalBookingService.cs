using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H24 — the patient portal's booking engine.
	//
	// PORTAL SIDE (an account, never an employee — H25 resolves the account from the sign-in):
	//   · who the account books for (its own file, its family when allowed), the free slots of a clinic or a doctor,
	//     booking, cancelling before the cut-off, sending and withdrawing a request — every decision through
	//     HospitalPortalPolicy and the clinic's H23 rule.
	//   · A booking writes the H4 appointment row itself (H4's BookAsync refuses anyone who is not an employee) under
	//     H4's own slot rules and its one-booking-per-slot index, so the front desk and the portal cannot double-book;
	//     a HospitalPortalBooking row says it came from the portal.
	// CALL-CENTRE SIDE (`book`): the open requests; booking one (through H4's BookAsync, as staff) or declining it.
	// FRONT DESK (`register`): verifying an account against a patient file; linking family.
	// ==========================================================================================

	public sealed class HospitalPortalBookInput
	{
		public int PatientId { get; set; }
		public int DepartmentId { get; set; }
		public string VisitType { get; set; } = HospitalVisitTypes.New;
		/// Null in a "first free doctor" clinic: the engine takes the first doctor free at that time.
		public int? PractitionerId { get; set; }
		public DateTime Date { get; set; }
		public int StartMinute { get; set; }
		public string? Referral { get; set; }
		/// H48: "email me if an earlier slot frees up" (kept only while the portal's waitlist is on).
		public bool Waitlist { get; set; }
	}

	public sealed class HospitalPortalRequestInput
	{
		public int? PatientId { get; set; }
		public int DepartmentId { get; set; }
		public string VisitType { get; set; } = HospitalVisitTypes.New;
		public string? PreferredDays { get; set; }
		public string? TimeOfDay { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalRequestBookInput
	{
		public int PractitionerId { get; set; }
		public DateTime Date { get; set; }
		public int StartMinute { get; set; }
	}

	public sealed record HospitalPortalPerson(int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Relation);

	public sealed record HospitalPortalSlot(int PractitionerId, DateTime Date, int StartMinute, string State = HospitalPortalPolicy.SlotFree);

	public sealed record HospitalPortalDay(DateTime Date, bool Works, int Free, int Booked);

	public sealed record HospitalPortalVisit(int AppointmentId, HospitalPortalPerson Person, DateTime Date, int StartMinute, string VisitType, string Status, int PractitionerId,
		string Doctor, string DoctorEn, string? Title, string? TitleEn, string? DoctorPhoto, string Clinic, string ClinicEn, bool FromPortal);

	public sealed record HospitalPortalRequestRow(HospitalPortalRequest Request, string DepartmentName, string DepartmentNameEn, string WhoName, string WhoNameEn, string? Mrn, string? Photo,
		string? Phone, bool Verified, int? PatientId);

	public sealed class HospitalPortalBookingService
	{
		public const string Self = "Self";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPortalRulesService _rules;

		public HospitalPortalBookingService(CrossDbContext db, IHospitalAccessService access)
		{ _db = db; _access = access; _rules = new HospitalPortalRulesService(db, access); }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalPortalAccount> Accounts(BusinessContext ctx) => _db.Set<HospitalPortalAccount>().Where(a => a.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		// ====================================================================================
		// PORTAL SIDE
		// ====================================================================================

		/// The files this account books for: its own (when verified), then its family when family booking is on.
		public async Task<IReadOnlyList<HospitalPortalPerson>> PeopleAsync(BusinessContext ctx, int accountId, CancellationToken ct = default)
		{
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null || acc.Status != HospitalPortalAccountStatus.Verified) return Array.Empty<HospitalPortalPerson>();
			var s = await _rules.SettingsAsync(ctx, ct);
			var links = s.FamilyBooking ? await _db.Set<HospitalPortalFamilyLink>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId).ToListAsync(ct) : new();
			var ids = links.Select(l => l.PatientId).Prepend(acc.PatientId!.Value).ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && ids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			return ids.Where(pats.ContainsKey).Select(id =>
			{
				var p = pats[id];
				return new HospitalPortalPerson(id, p.Name, p.NameEn, p.Mrn, p.PhotoPath, id == acc.PatientId ? Self : links.First(l => l.PatientId == id).Relation);
			}).ToList();
		}

		/// The doctors of this patient in a clinic: those who finished a visit with them there.
		private async Task<IReadOnlyList<int>> OwnDoctorsAsync(BusinessContext ctx, int patientId, int departmentId, CancellationToken ct) =>
			await (from a in _db.HospitalAppointments.AsNoTracking() join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
				   where a.CompanyId == ctx.CompanyId && a.PatientId == patientId && d.DepartmentId == departmentId && a.Status == HospitalAppointmentStatus.Done
				   select a.PractitionerId).Distinct().ToListAsync(ct);

		/// Everything a booking needs checked before any slot is looked at: the account, the patient, the clinic's
		/// offer of this visit, and the doctors it may be with.
		private async Task<(string? Refusal, HospitalPortalSettings S, HospitalPortalClinic? C, IReadOnlyList<int> Doctors)> GateAsync(BusinessContext ctx, int accountId, int patientId, int departmentId,
			string visitType, CancellationToken ct)
		{
			var s = await _rules.SettingsAsync(ctx, ct);
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return ("not_found", s, null, Array.Empty<int>());
			if (HospitalPortalPolicy.AccountRefusal(acc.Status, isRequest: false) is string ar) return (ar, s, null, Array.Empty<int>());
			var family = await _db.Set<HospitalPortalFamilyLink>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId).Select(l => l.PatientId).ToListAsync(ct);
			if (!HospitalPortalPolicy.MayBookFor(acc.PatientId, family, s.FamilyBooking, patientId)) return ("not_found", s, null, Array.Empty<int>());
			var c = (await _rules.ClinicsAsync(ctx, ct)).FirstOrDefault(x => x.DepartmentId == departmentId);
			if (c == null) return ("not_found", s, null, Array.Empty<int>());
			if (c.Mode == HospitalPortalModes.Request) return ("This clinic takes requests: send one and the call centre will book it", s, c, Array.Empty<int>());
			var own = await OwnDoctorsAsync(ctx, patientId, departmentId, ct);
			if (HospitalPortalPolicy.OfferRefusal(c, visitType, own.Count > 0) is string or) return (or, s, c, Array.Empty<int>());
			var docs = (await _rules.DoctorsAsync(ctx, ct)).Select(d => new HospitalPortalDoctor(d.PractitionerId, d.DepartmentId, d.Online, d.NewPatients));
			var allowed = HospitalPortalPolicy.DoctorsFor(c, visitType, own, docs);
			return (allowed.Count == 0 ? "No doctor of this clinic can be booked online for this visit" : null, s, c, allowed);
		}

		/// The doctors this patient may book for this visit in this clinic, or why none (the same gate booking uses).
		public async Task<(string? Refusal, string? Mode, IReadOnlyList<int> Doctors)> AllowedDoctorsAsync(BusinessContext ctx, int accountId, int patientId, int departmentId, string visitType, CancellationToken ct = default)
		{
			var (why, _, c, allowed) = await GateAsync(ctx, accountId, patientId, departmentId, visitType, ct);
			return (why, c?.Mode, allowed);
		}

		/// The visits of the account's people (the last half year and everything ahead), newest ahead first.
		public async Task<IReadOnlyList<HospitalPortalVisit>> VisitsAsync(BusinessContext ctx, int accountId, DateTime nowLocal, CancellationToken ct = default)
		{
			var people = await PeopleAsync(ctx, accountId, ct);
			var ids = people.Select(p => p.PatientId).ToList();
			var since = nowLocal.Date.AddDays(-180);
			var rows = await (from a in _db.HospitalAppointments.AsNoTracking() join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
							  where a.CompanyId == ctx.CompanyId && ids.Contains(a.PatientId) && a.AppointmentDate >= since
							  select new { a, d }).ToListAsync(ct);
			var deptIds = rows.Select(r => r.d.DepartmentId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var depts = await _db.HospitalDepartments.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && deptIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(r => r.d.EmployeeId != null).Select(r => r.d.EmployeeId!.Value).Distinct(), ct);
			var apptIds = rows.Select(r => r.a.ID).ToList();
			var fromPortal = (await _db.Set<HospitalPortalBooking>().AsNoTracking().Where(b => apptIds.Contains(b.AppointmentId)).Select(b => b.AppointmentId).ToListAsync(ct)).ToHashSet();
			return rows.Select(r =>
			{
				var p = people.First(x => x.PatientId == r.a.PatientId); var dp = r.d.DepartmentId is int di ? depts.GetValueOrDefault(di) : null;
				return new HospitalPortalVisit(r.a.ID, p, r.a.AppointmentDate.Date, r.a.StartMinute, r.a.VisitType, r.a.Status, r.d.ID, r.d.Name, r.d.NameEn, r.d.Title, r.d.TitleEn,
					r.d.EmployeeId is int e ? photos.GetValueOrDefault(e) : null, dp?.Name ?? "", dp?.NameEn ?? dp?.Name ?? "", fromPortal.Contains(r.a.ID));
			}).OrderBy(v => v.Date < nowLocal.Date).ThenBy(v => v.Date < nowLocal.Date ? -v.Date.Ticks : v.Date.Ticks).ThenBy(v => v.StartMinute).ToList();
		}

		/// The account's own requests, newest first.
		public async Task<IReadOnlyList<HospitalPortalRequestRow>> MyRequestsAsync(BusinessContext ctx, int accountId, CancellationToken ct = default) =>
			(await RequestsForAsync(ctx, _db.Set<HospitalPortalRequest>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.AccountId == accountId), ct)).OrderByDescending(r => r.Request.CreatedAt).ToList();

		/// The doctors whose sessions a request for slots reads: the one chosen, or (first free doctor) all allowed.
		private static List<int> SlotDoctors(HospitalPortalClinic c, IReadOnlyList<int> allowed, int? practitionerId) =>
			practitionerId is int pid && c.Mode != HospitalPortalModes.Next ? (allowed.Contains(pid) ? new List<int> { pid } : new List<int>()) : allowed.ToList();

		/// The WHOLE session of a doctor (or, first free doctor, of the clinic's allowed doctors) on one day, every time
		/// with its state: free, booked by another patient, or unavailable (past / inside the minimum notice). With several
		/// doctors a time is free when any of them is free then (that doctor is the one booked), booked when all are.
		public async Task<(string? Refusal, IReadOnlyList<HospitalPortalSlot> Slots)> SlotsAsync(BusinessContext ctx, int accountId, int patientId, int departmentId, string visitType,
			int? practitionerId, DateTime day, DateTime nowLocal, CancellationToken ct = default)
		{
			var (why, s, c, allowed) = await GateAsync(ctx, accountId, patientId, departmentId, visitType, ct);
			if (why != null) return (why, Array.Empty<HospitalPortalSlot>());
			var ids = SlotDoctors(c!, allowed, practitionerId);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ID)).OrderBy(p => p.ID).ToListAsync(ct);
			var date = day.Date;
			var taken = (await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == date && ids.Contains(a.PractitionerId) && a.Status != HospitalAppointmentStatus.Cancelled)
				.Select(a => new { a.PractitionerId, a.StartMinute }).ToListAsync(ct))
				.Concat((await HospitalPortalWaitlistService.HeldAsync(_db, ctx.CompanyId, ids, date, date, DateTime.UtcNow, ct)).Select(h => new { h.PractitionerId, StartMinute = h.Minute })).ToList();   // H48: held for an offer
			var sch = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, docs, date, date, ct);
			var all = docs.SelectMany(d => HospitalPortalPolicy.DaySlots(sch[d.ID], date, taken.Where(t => t.PractitionerId == d.ID).Select(t => t.StartMinute).ToList(), s, nowLocal)
				.Select(x => new HospitalPortalSlot(d.ID, date, x.Minute, x.State))).ToList();
			var merged = all.GroupBy(x => x.StartMinute).Select(g =>
				g.FirstOrDefault(x => x.State == HospitalPortalPolicy.SlotFree)
				?? (g.All(x => x.State == HospitalPortalPolicy.SlotBooked) ? g.First() : g.First(x => x.State != HospitalPortalPolicy.SlotBooked)));
			return (null, merged.OrderBy(x => x.StartMinute).ToList());
		}

		/// The day strip: for each day from `from`, whether the doctor(s) work it and how many times are free.
		public async Task<(string? Refusal, IReadOnlyList<HospitalPortalDay> Days)> DaysAsync(BusinessContext ctx, int accountId, int patientId, int departmentId, string visitType,
			int? practitionerId, DateTime from, int count, DateTime nowLocal, CancellationToken ct = default)
		{
			var (why, s, c, allowed) = await GateAsync(ctx, accountId, patientId, departmentId, visitType, ct);
			if (why != null) return (why, Array.Empty<HospitalPortalDay>());
			var ids = SlotDoctors(c!, allowed, practitionerId);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ID)).ToListAsync(ct);
			DateTime first = from.Date, last = first.AddDays(Math.Clamp(count, 1, 62) - 1);
			var taken = (await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate >= first && a.AppointmentDate <= last && ids.Contains(a.PractitionerId) && a.Status != HospitalAppointmentStatus.Cancelled)
				.Select(a => new { a.PractitionerId, a.AppointmentDate, a.StartMinute }).ToListAsync(ct))
				.Concat((await HospitalPortalWaitlistService.HeldAsync(_db, ctx.CompanyId, ids, first, last, DateTime.UtcNow, ct)).Select(h => new { h.PractitionerId, AppointmentDate = h.Day, StartMinute = h.Minute })).ToList();   // H48
			var sch = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, docs, first, last, ct);
			var list = new List<HospitalPortalDay>();
			for (var d = first; d <= last; d = d.AddDays(1))
			{
				bool works = docs.Any(p => HospitalPortalPolicy.WorksOn(sch[p.ID], d, s, nowLocal));
				var states = docs.SelectMany(p => HospitalPortalPolicy.DaySlots(sch[p.ID], d, taken.Where(t => t.PractitionerId == p.ID && t.AppointmentDate == d).Select(t => t.StartMinute).ToList(), s, nowLocal)).ToList();
				int free = states.Where(x => x.State == HospitalPortalPolicy.SlotFree).Select(x => x.Minute).Distinct().Count();
				int booked = states.Count(x => x.State == HospitalPortalPolicy.SlotBooked);
				list.Add(new HospitalPortalDay(d, works, free, booked));
			}
			return (null, list);
		}

		/// Books from the portal: every rule again (the screen may be stale), then the H4 row and the portal row.
		public async Task<HospitalResult> BookAsync(BusinessContext ctx, int accountId, HospitalPortalBookInput input, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!HospitalVisitTypes.IsKnown(input.VisitType)) return HospitalResult.Fail("Unknown visit type");
			var (why, s, c, allowed) = await GateAsync(ctx, accountId, input.PatientId, input.DepartmentId, input.VisitType, ct);
			if (why != null) return HospitalResult.Fail(why);
			if (await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.DeceasedAt != null, ct)) return HospitalResult.Fail("not_found");   // H59: a deceased file is not booked
			var day = input.Date.Date;
			var open = await _db.HospitalAppointments.AsNoTracking().CountAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == input.PatientId && a.Status == HospitalAppointmentStatus.Booked && a.AppointmentDate >= nowLocal.Date, ct);
			if (HospitalPortalPolicy.BookingRefusal(s, c!, input.VisitType, day.AddMinutes(input.StartMinute), nowLocal, open, input.Referral) is string br) return HospitalResult.Fail(br);

			// the doctor: the one chosen (when the clinic lets the patient choose), else the first allowed doctor free then
			var candidates = c!.Mode == HospitalPortalModes.Next || input.PractitionerId == null ? allowed.ToList()
				: allowed.Contains(input.PractitionerId.Value) ? new List<int> { input.PractitionerId.Value } : new List<int>();
			if (candidates.Count == 0) return HospitalResult.Fail("This doctor cannot be booked online for this visit");
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && candidates.Contains(p.ID)).OrderBy(p => p.ID).ToListAsync(ct);
			var live = _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == day && a.Status != HospitalAppointmentStatus.Cancelled);
			var taken = (await live.Where(a => candidates.Contains(a.PractitionerId)).Select(a => new { a.PractitionerId, a.StartMinute }).ToListAsync(ct))
				.Concat((await HospitalPortalWaitlistService.HeldAsync(_db, ctx.CompanyId, candidates, day, day, nowUtc, ct)).Select(h => new { h.PractitionerId, StartMinute = h.Minute })).ToList();   // H48: a time held for an offer
			var sch = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, docs, day, day, ct);
			var doc = docs.FirstOrDefault(d => HospitalPortalPolicy.FreeSlots(sch[d.ID], day, taken.Where(t => t.PractitionerId == d.ID).Select(t => t.StartMinute).ToList(), s, nowLocal).Contains(input.StartMinute));
			if (doc == null) return HospitalResult.Fail("That time is no longer free");
			int duration = sch[doc.ID].DurationAt(day, input.StartMinute) ?? doc.SlotMinutes;   // the session's minutes per patient
			int end = input.StartMinute + duration;
			if (await live.AnyAsync(a => a.PatientId == input.PatientId && a.StartMinute < end && input.StartMinute < a.StartMinute + a.DurationMinutes, ct))
				return HospitalResult.Fail("The patient already has an appointment at that time");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var appt = new HospitalAppointment
			{
				CompanyId = ctx.CompanyId, PatientId = input.PatientId, PractitionerId = doc.ID, AppointmentDate = day, StartMinute = input.StartMinute, DurationMinutes = duration,
				VisitType = input.VisitType, Status = HospitalAppointmentStatus.Booked, Note = "Portal", CreatedAt = nowUtc,
			};
			_db.HospitalAppointments.Add(appt);
			try
			{
				await _db.SaveChangesAsync(ct);   // the one-booking-per-slot index refuses a slot taken a moment ago
				_db.Add(new HospitalPortalBooking { CompanyId = ctx.CompanyId, AppointmentId = appt.ID, AccountId = accountId, PatientId = input.PatientId, Referral = Clip(input.Referral, 60), BookedAt = nowUtc, Waitlist = input.Waitlist && s.Waitlist });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(appt.ID);
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("That time is no longer free"); }
		}

		/// Cancels a booking of the account's people, until the online cut-off.
		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int accountId, int appointmentId, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (acc.Status == HospitalPortalAccountStatus.Disabled) return HospitalResult.Fail("This portal account is disabled");
			var s = await _rules.SettingsAsync(ctx, ct);
			var family = await _db.Set<HospitalPortalFamilyLink>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId).Select(l => l.PatientId).ToListAsync(ct);
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null || !HospitalPortalPolicy.MayBookFor(acc.PatientId, family, s.FamilyBooking, a.PatientId)) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAppointmentStatus.Booked) return HospitalResult.Fail("Only a booked appointment can be cancelled");
			if (!HospitalPortalPolicy.CanChangeOnline(s, a.AppointmentDate.Date.AddMinutes(a.StartMinute), nowLocal)) return HospitalResult.Fail("It is too close to the appointment to change it online: call us");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			int n = await _db.HospitalAppointments.Where(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId && x.Status == HospitalAppointmentStatus.Booked)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalAppointmentStatus.Cancelled).SetProperty(x => x.UpdatedAt, nowUtc), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Only a booked appointment can be cancelled"); }
			await _db.Set<HospitalPortalBooking>().Where(b => b.AppointmentId == appointmentId && b.CompanyId == ctx.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CancelledAt, nowUtc), ct);
			await tx.CommitAsync();
			return HospitalResult.Success(appointmentId);
		}

		/// A request for the call centre (a "request" clinic, or an account not verified yet).
		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, int accountId, HospitalPortalRequestInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (HospitalPortalPolicy.AccountRefusal(acc.Status, isRequest: true) is string ar) return HospitalResult.Fail(ar);
			if (!HospitalVisitTypes.IsKnown(input.VisitType)) return HospitalResult.Fail("Unknown visit type");
			if (HospitalPortalPolicy.RequestRefusal(input.TimeOfDay, input.PreferredDays) is string rr) return HospitalResult.Fail(rr);
			var s = await _rules.SettingsAsync(ctx, ct);
			int? patientId = input.PatientId;
			if (acc.Status == HospitalPortalAccountStatus.Unverified) patientId = null;   // no file yet: the request is for the account's person
			else
			{
				var family = await _db.Set<HospitalPortalFamilyLink>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId).Select(l => l.PatientId).ToListAsync(ct);
				patientId ??= acc.PatientId;
				if (!HospitalPortalPolicy.MayBookFor(acc.PatientId, family, s.FamilyBooking, patientId!.Value)) return HospitalResult.Fail("not_found");
			}
			var c = (await _rules.ClinicsAsync(ctx, ct)).FirstOrDefault(x => x.DepartmentId == input.DepartmentId);
			if (c == null) return HospitalResult.Fail("not_found");
			if (c.Mode == HospitalPortalModes.Off) return HospitalResult.Fail("This clinic is booked by phone");
			int open = await _db.Set<HospitalPortalRequest>().CountAsync(r => r.CompanyId == ctx.CompanyId && r.AccountId == accountId && r.Status == HospitalPortalRequestStatus.Open, ct);
			if (open >= s.MaxActive) return HospitalResult.Fail("You have reached the limit of open bookings");
			var nos = await _db.Set<HospitalPortalRequest>().Where(r => r.CompanyId == ctx.CompanyId).Select(r => r.RequestNo).ToListAsync(ct);
			var row = new HospitalPortalRequest
			{
				CompanyId = ctx.CompanyId, RequestNo = HospitalDentalPracticeRules.NextNo(nos, "REQ-", 6), AccountId = accountId, PatientId = patientId, DepartmentId = c.DepartmentId,
				VisitType = input.VisitType, PreferredDays = Clip(input.PreferredDays, 120), TimeOfDay = input.TimeOfDay!, Note = Clip(input.Note, 400), CreatedAt = nowUtc,
			};
			_db.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> WithdrawAsync(BusinessContext ctx, int accountId, int requestId, DateTime nowUtc, CancellationToken ct = default)
		{
			int n = await _db.Set<HospitalPortalRequest>().Where(r => r.ID == requestId && r.CompanyId == ctx.CompanyId && r.AccountId == accountId && r.Status == HospitalPortalRequestStatus.Open)
				.ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, HospitalPortalRequestStatus.Withdrawn).SetProperty(r => r.HandledAt, nowUtc), ct);
			return n == 1 ? HospitalResult.Success(requestId) : HospitalResult.Fail("Only an open request can be withdrawn");
		}

		// ====================================================================================
		// CALL CENTRE AND FRONT DESK (staff)
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalPortalRequestRow>> RequestsAsync(BusinessContext ctx, string? status, CancellationToken ct = default)
		{
			var q = _db.Set<HospitalPortalRequest>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId);
			q = status == null ? q.Where(r => r.Status == HospitalPortalRequestStatus.Open) : q.Where(r => r.Status == status);
			return await RequestsForAsync(ctx, q.OrderBy(r => r.CreatedAt).Take(300), ct);
		}

		/// Request rows with whom they are for, their clinic and the person's photo (the call centre's list and the patient's own).
		private async Task<IReadOnlyList<HospitalPortalRequestRow>> RequestsForAsync(BusinessContext ctx, IQueryable<HospitalPortalRequest> query, CancellationToken ct)
		{
			var rows = await query.ToListAsync(ct);
			var accIds = rows.Select(r => r.AccountId).Distinct().ToList();
			var accs = await Accounts(ctx).AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, ct);
			var patIds = rows.Select(r => r.PatientId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var depts = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.ID, ct);
			return rows.Select(r =>
			{
				var a = accs.GetValueOrDefault(r.AccountId); var p = r.PatientId is int pid ? pats.GetValueOrDefault(pid) : null; var d = depts.GetValueOrDefault(r.DepartmentId);
				return new HospitalPortalRequestRow(r, d?.Name ?? "", d?.NameEn ?? d?.Name ?? "", p?.Name ?? a?.Name ?? "", p?.NameEn ?? a?.NameEn ?? a?.Name ?? "", p?.Mrn, p?.PhotoPath,
					a?.Phone ?? p?.Phone, a?.Status == HospitalPortalAccountStatus.Verified, r.PatientId);
			}).ToList();
		}

		/// A doctor's free times on a day for the call centre (`book`): H4's own slots, no portal notice or window.
		public async Task<IReadOnlyList<int>> StaffFreeSlotsAsync(BusinessContext ctx, int practitionerId, DateTime day, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return Array.Empty<int>();
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == practitionerId && p.CompanyId == ctx.CompanyId, ct);
			if (doc == null) return Array.Empty<int>();
			var date = day.Date;
			var taken = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PractitionerId == practitionerId && a.AppointmentDate == date && a.Status != HospitalAppointmentStatus.Cancelled)
				.Select(a => a.StartMinute).ToListAsync(ct);
			var sch = await HospitalScheduleLoader.OneAsync(_db, ctx.CompanyId, doc, date, date, ct);
			return HospitalPortalPolicy.FreeSlots(sch, date, taken, new HospitalPortalSettings { LeadHours = 0, WindowDays = 365 }, nowLocal);
		}

		/// The call centre books a request: through H4's own BookAsync, as staff, then the request points at it.
		/// H28: a request for the dentistry clinic is booked on the dentist's CHAIR (the chair schedule's booking: the
		/// chair and the dentist free for the procedure's length, and the dental visit row), not as a bare H4 slot —
		/// otherwise the chair schedule would never see it and the chair could be taken twice.
		public async Task<HospitalResult> BookRequestAsync(BusinessContext ctx, HospitalOutpatientService outpatient, int requestId, HospitalRequestBookInput input, int? employeeId, DateTime today, DateTime nowUtc, CancellationToken ct = default,
			HospitalDentalPracticeService? dental = null)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var r = await _db.Set<HospitalPortalRequest>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == requestId && x.CompanyId == ctx.CompanyId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalPortalRequestStatus.Open) return HospitalResult.Fail("This request is already handled");
			if (r.PatientId is not int pid) return HospitalResult.Fail("Verify the account against a patient file first");
			var code = await _db.HospitalDepartments.AsNoTracking().Where(d => d.ID == r.DepartmentId && d.CompanyId == ctx.CompanyId).Select(d => d.Code).FirstOrDefaultAsync(ct);
			int? chairId = null;
			if (HospitalPortalPolicy.IsDental(code))
			{
				if (dental == null) return HospitalResult.Fail("not_found");
				chairId = await _db.Set<HospitalDentalChair>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.IsActive && c.PractitionerId == input.PractitionerId)
					.OrderBy(c => c.SortOrder).Select(c => (int?)c.ID).FirstOrDefaultAsync(ct);
				if (chairId == null) return HospitalResult.Fail("This dentist has no chair: give them one on the chair schedule first");
			}
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var booked = chairId is int chair
				? await dental!.BookAsync(ctx, new HospitalChairBookingInput { PatientId = pid, ChairId = chair, PractitionerId = input.PractitionerId, Date = input.Date, StartMinute = input.StartMinute, VisitType = r.VisitType, Note = r.RequestNo }, employeeId, today, ct)
				: await outpatient.BookAsync(ctx, new HospitalBookingInput { PatientId = pid, PractitionerId = input.PractitionerId, Date = input.Date, StartMinute = input.StartMinute, VisitType = r.VisitType, Note = r.RequestNo }, employeeId, today, ct);
			if (!booked.Ok) { await tx.RollbackAsync(); return booked; }
			int n = await _db.Set<HospitalPortalRequest>().Where(x => x.ID == requestId && x.Status == HospitalPortalRequestStatus.Open).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalPortalRequestStatus.Booked).SetProperty(x => x.AppointmentId, booked.Id).SetProperty(x => x.HandledAt, nowUtc).SetProperty(x => x.HandledByEmployeeId, employeeId), ct);
			if (n != 1) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("This request is already handled"); }
			_db.Add(new HospitalPortalBooking { CompanyId = ctx.CompanyId, AppointmentId = booked.Id!.Value, AccountId = r.AccountId, PatientId = pid, BookedAt = nowUtc });
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(booked.Id);
		}

		public async Task<HospitalResult> DeclineRequestAsync(BusinessContext ctx, int requestId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why it is declined");
			if (!await _db.Set<HospitalPortalRequest>().AnyAsync(x => x.ID == requestId && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var why = Clip(reason, 300);
			int n = await _db.Set<HospitalPortalRequest>().Where(x => x.ID == requestId && x.CompanyId == ctx.CompanyId && x.Status == HospitalPortalRequestStatus.Open).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalPortalRequestStatus.Declined).SetProperty(x => x.DeclineReason, why).SetProperty(x => x.HandledAt, nowUtc).SetProperty(x => x.HandledByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(requestId) : HospitalResult.Fail("This request is already handled");
		}

		/// The front desk matches an account to a patient file: it becomes verified, and its open requests are for that file.
		public async Task<HospitalResult> VerifyAccountAsync(BusinessContext ctx, int accountId, int patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Register, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct)) return HospitalResult.Fail("not_found");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				int n = await Accounts(ctx).Where(a => a.ID == accountId && a.Status == HospitalPortalAccountStatus.Unverified).ExecuteUpdateAsync(u => u
					.SetProperty(a => a.PatientId, patientId).SetProperty(a => a.Status, HospitalPortalAccountStatus.Verified).SetProperty(a => a.VerifiedAt, nowUtc).SetProperty(a => a.VerifiedByEmployeeId, employeeId), ct);
				if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Only an unverified account is verified"); }
				await _db.Set<HospitalPortalRequest>().Where(r => r.CompanyId == ctx.CompanyId && r.AccountId == accountId && r.PatientId == null && r.Status == HospitalPortalRequestStatus.Open)
					.ExecuteUpdateAsync(u => u.SetProperty(r => r.PatientId, patientId), ct);
				await tx.CommitAsync();
				return HospitalResult.Success(accountId);
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("This patient file already has a portal account"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("This patient file already has a portal account"); }
		}

		/// The front desk gives a patient file its portal account, verified at once (the desk saw the person). The
		/// sign-in itself is set up when the patient first signs in (H25).
		public async Task<HospitalResult> CreateForPatientAsync(BusinessContext ctx, int patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Register, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (await Accounts(ctx).AnyAsync(a => a.PatientId == patientId, ct)) return HospitalResult.Fail("This patient file already has a portal account");
			var row = new HospitalPortalAccount
			{
				CompanyId = ctx.CompanyId, PatientId = patientId, Status = HospitalPortalAccountStatus.Verified, Name = p.Name, NameEn = p.NameEn, Phone = p.Phone, NationalId = p.NationalId,
				BirthDate = p.BirthDate, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId, VerifiedAt = nowUtc, VerifiedByEmployeeId = employeeId,
			};
			_db.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This patient file already has a portal account"); }
			return HospitalResult.Success(row.ID);
		}

		/// The front desk links a family member's file to an account (they book for it when family booking is on).
		public async Task<HospitalResult> LinkFamilyAsync(BusinessContext ctx, int accountId, int patientId, string? relation, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Register, ct)) return HospitalResult.Fail("forbidden");
			if (relation == null || !HospitalPortalRelations.All.Contains(relation)) return HospitalResult.Fail("Choose the relation");
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null || !await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct)) return HospitalResult.Fail("not_found");
			if (acc.PatientId == patientId) return HospitalResult.Fail("This is the account's own file");
			_db.Add(new HospitalPortalFamilyLink { CompanyId = ctx.CompanyId, AccountId = accountId, PatientId = patientId, Relation = relation, AddedAt = nowUtc, AddedByEmployeeId = employeeId });
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This file is already linked to the account"); }
			return HospitalResult.Success(accountId);
		}
	}
}
