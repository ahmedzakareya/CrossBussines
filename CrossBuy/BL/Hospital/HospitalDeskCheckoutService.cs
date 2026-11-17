using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H160 — checkout at the desk (see HospitalDeskCheckouts.cs).
	//   · AWAITING: today's visits whose consultation is finished (the appointment is Done) and the desk has not closed.
	//   · THE SHEET: what the visit left for the desk, administrative only — the money still owed on the visit's bill, the
	//     follow-up the doctor advised (in days) and whether a later appointment is already booked, the department the
	//     patient was referred to, how many tests, scans and medicines are still waiting, a sick-leave paper issued.
	//     No diagnosis, result or note: the desk directs the patient, it does not read the file.
	//   · CLOSE: once per visit; with money owed, an advised follow-up not booked, or orders waiting, the desk's word
	//     (why it is closed anyway) is required. What was open is kept on the row.
	// ==========================================================================================

	public sealed record HospitalCheckoutSheet(HospitalAppointment Appointment, string PatientName, string? PatientNameEn, string Mrn, string Doctor, string? DoctorEn,
		int? EncounterId, decimal Owed, IReadOnlyList<(int Id, string BillNo)> Bills, int? FollowUpDays, DateTime? FollowUpBy, DateTime? NextBooked,
		string? ReferredTo, string? ReferredToEn, int Tests, int Scans, int Medicines, int SickLeaveDays, HospitalDeskCheckout? Done)
	{
		public bool FollowUpOpen => FollowUpDays is int && NextBooked == null;
		public int OpenOrders => Tests + Scans + Medicines;
		public bool Unresolved => Owed > 0 || FollowUpOpen || OpenOrders > 0;
	}

	public sealed record HospitalCheckoutRow(int AppointmentId, int PatientId, string PatientName, string? PatientNameEn, string Mrn, string Doctor, string? DoctorEn, int StartMinute, DateTime? DoneAt);

	public static class HospitalCheckoutRules
	{
		public static string? Refuse(HospitalCheckoutSheet s, string? note)
		{
			if (s.Done != null) return "This visit is checked out already";
			if (s.Appointment.Status != HospitalAppointmentStatus.Done) return "The consultation is not finished yet";
			if (s.Unresolved && string.IsNullOrWhiteSpace(note)) return "Something is still open: write why the visit is closed anyway";
			return null;
		}
	}

	public sealed class HospitalDeskCheckoutService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		public HospitalDeskCheckoutService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) { _db = db; _access = access; _billing = billing; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;

		private async Task<HashSet<int>> ClosedAsync(BusinessContext ctx, IReadOnlyCollection<int> appts, CancellationToken ct)
		{
			try { return (await _db.Set<HospitalDeskCheckout>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && appts.Contains(c.AppointmentId)).Select(c => c.AppointmentId).ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HashSet<int>(); }
		}

		/// Today's finished consultations the desk has not closed, oldest first.
		public async Task<IReadOnlyList<HospitalCheckoutRow>> AwaitingAsync(BusinessContext ctx, DateTime todayLocal, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId; var day = todayLocal.Date;
			var done = await (from a in _db.HospitalAppointments.AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							  join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
							  where a.CompanyId == cid && a.AppointmentDate == day && a.Status == HospitalAppointmentStatus.Done
							  select new { a.ID, a.PatientId, p.Name, p.NameEn, p.Mrn, Doc = d.Name, DocEn = d.NameEn, a.StartMinute, a.FinishedAt }).ToListAsync(ct);
			if (done.Count == 0) return Array.Empty<HospitalCheckoutRow>();
			var ids = done.Select(x => x.ID).ToList();
			var closed = await ClosedAsync(ctx, ids, ct);
			return done.Where(x => !closed.Contains(x.ID))
				.Select(x => new HospitalCheckoutRow(x.ID, x.PatientId, x.Name, x.NameEn, x.Mrn, x.Doc, x.DocEn, x.StartMinute, x.FinishedAt))
				.OrderBy(x => x.DoneAt ?? DateTime.MaxValue).ThenBy(x => x.StartMinute).ToList();
		}

		public async Task<HospitalCheckoutSheet?> SheetAsync(BusinessContext ctx, int appointmentId, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == cid, ct);
			if (a == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == a.PatientId && x.CompanyId == cid, ct);
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(x => x.ID == a.PractitionerId && x.CompanyId == cid, ct);
			var enc = await _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.CompanyId == cid && e.AppointmentId == a.ID).OrderByDescending(e => e.ID).FirstOrDefaultAsync(ct);
			decimal owed = 0; var bills = new List<(int, string)>();
			int tests = 0, scans = 0, meds = 0, leave = 0; string? refAr = null, refEn = null;
			if (enc != null)
			{
				var billIds = await _db.Set<HospitalBill>().AsNoTracking().Where(b => b.CompanyId == cid && b.EncounterId == enc.ID).Select(b => b.ID).ToListAsync(ct);
				if (billIds.Count > 0)
				{
					var rows = (await _billing.BillsAsync(ctx, null, p.ID, nowUtc, ct)).Where(b => billIds.Contains(b.Id)).ToList();
					owed = rows.Sum(b => Math.Max(0, b.Total - b.Paid));
					bills = rows.Where(b => b.Total - b.Paid > 0).Select(b => (b.Id, b.BillNo)).ToList();
				}
				tests = await _db.Set<HospitalLabOrder>().CountAsync(o => o.CompanyId == cid && o.EncounterId == enc.ID && o.Status == HospitalLabStatus.Ordered, ct);
				scans = await _db.Set<HospitalRadOrder>().CountAsync(o => o.CompanyId == cid && o.EncounterId == enc.ID && o.Status == HospitalRadStatus.Scheduled, ct);
				meds = await _db.Set<HospitalDispense>().CountAsync(d => d.CompanyId == cid && d.EncounterId == enc.ID && (d.Status == HospitalDispenseStatus.New || d.Status == HospitalDispenseStatus.Verified), ct);
				leave = await _db.Set<HospitalSickLeave>().Where(s => s.CompanyId == cid && s.EncounterId == enc.ID && s.VoidedAt == null).SumAsync(s => (int?)s.Days, ct) ?? 0;
				if (enc.ReferToDepartmentId is int dep)
				{
					var d = await _db.Set<HospitalDepartment>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == dep && x.CompanyId == cid, ct);
					refAr = d?.Name; refEn = d?.NameEn;
				}
			}
			var next = await _db.HospitalAppointments.AsNoTracking()
				.Where(x => x.CompanyId == cid && x.PatientId == p.ID && x.ID != a.ID && x.AppointmentDate > a.AppointmentDate && x.Status == HospitalAppointmentStatus.Booked)
				.OrderBy(x => x.AppointmentDate).Select(x => (DateTime?)x.AppointmentDate).FirstOrDefaultAsync(ct);
			HospitalDeskCheckout? done = null;
			try { done = await _db.Set<HospitalDeskCheckout>().AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == cid && c.AppointmentId == a.ID, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			int? fu = enc?.FollowUpDays is int f && f > 0 ? f : null;
			return new HospitalCheckoutSheet(a, p.Name, p.NameEn, p.Mrn, doc?.Name ?? "", doc?.NameEn, enc?.ID, owed, bills, fu, fu is int n ? a.AppointmentDate.Date.AddDays(n) : null, next,
				refAr, refEn, tests, scans, meds, leave, done);
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int appointmentId, string? note, int? employeeId, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var s = await SheetAsync(ctx, appointmentId, todayLocal, nowUtc, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			var why = HospitalCheckoutRules.Refuse(s, note);
			if (why != null) return HospitalResult.Fail(why);
			var row = new HospitalDeskCheckout
			{
				CompanyId = ctx.CompanyId, AppointmentId = s.Appointment.ID, PatientId = s.Appointment.PatientId, EncounterId = s.EncounterId, CompletedAt = nowUtc, CompletedByEmployeeId = employeeId,
				OwedAtClose = s.Owed, FollowUpDays = s.FollowUpDays, FollowUpBooked = s.NextBooked != null, OpenOrders = s.OpenOrders,
				Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)],
			};
			_db.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { return HospitalResult.Fail("This visit is checked out already"); }   // two desks at once: the unique index keeps one
			return HospitalResult.Success(row.ID);
		}
	}
}
