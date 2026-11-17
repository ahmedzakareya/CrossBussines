using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H158 — the desk's handoffs (see HospitalDeskHandoffs.cs).
	//   · WHAT IS WAITING FOR THIS PATIENT: the desk sees what the hospital already has for them — today's clinic visit,
	//     a test or a scan ordered and not yet done, an operation planned today, a stay in progress — and sends the
	//     person on against THAT item (one open handoff per item), or, for a referral, records who referred them.
	//   · AN URGENT CALL needs no identified patient; it is sent to the emergency team at once.
	//   · A VISITOR asking about a patient is answered under the disclosure rule: a confidential file is never located;
	//     otherwise the place is given and the inquiry (who asked, their relation) is recorded.
	//   · THE RECEIVING TEAM accepts, marks arrived, or says why it cannot take them; the desk sees each handoff's age and
	//     chases one not accepted in time (a reminder is counted).
	// ==========================================================================================

	public sealed class HospitalHandoffInput
	{
		public int? PatientId { get; set; }
		public string? Reason { get; set; }
		public string? Destination { get; set; }
		public string? LinkKind { get; set; }
		public int? LinkId { get; set; }
		public string? ReferralFrom { get; set; }
		public string? VisitorName { get; set; }
		public string? VisitorRelation { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalHandoffRules
	{
		/// Minutes a handoff may wait for the receiving team before the desk chases it (an urgent call: two).
		public static int AcceptWithin(string reason) => reason == HospitalArrivalReason.Urgent ? 2 : 10;
		/// Minutes an accepted handoff may take before the person is expected there.
		public const int ArriveWithin = 30;

		public static bool Overdue(HospitalDeskHandoff h, DateTime nowUtc) => h.Status switch
		{
			HospitalHandoffStatus.Sent => (nowUtc - h.SentAt).TotalMinutes > AcceptWithin(h.Reason),
			HospitalHandoffStatus.Accepted => h.AcceptedAt is DateTime a && (nowUtc - a).TotalMinutes > ArriveWithin,
			_ => false,
		};

		/// Where a reason goes unless the desk chooses (a referral names its own destination).
		public static string? DefaultDestination(string reason) => reason switch
		{
			HospitalArrivalReason.Clinic => HospitalHandoffDestination.Clinic,
			HospitalArrivalReason.Lab => HospitalHandoffDestination.Lab,
			HospitalArrivalReason.Imaging => HospitalHandoffDestination.Imaging,
			HospitalArrivalReason.Admission => HospitalHandoffDestination.Ward,
			HospitalArrivalReason.Operation => HospitalHandoffDestination.Theatre,
			HospitalArrivalReason.Urgent => HospitalHandoffDestination.Emergency,
			HospitalArrivalReason.Admin => HospitalHandoffDestination.Billing,
			HospitalArrivalReason.Visitor => HospitalHandoffDestination.Desk,
			_ => null,
		};

		public static string? Refuse(HospitalHandoffInput i)
		{
			if (!HospitalArrivalReason.All.Contains(i.Reason ?? "")) return "Choose why they came";
			if (i.PatientId == null && i.Reason != HospitalArrivalReason.Urgent) return "Open the patient's file first";
			if (i.Reason == HospitalArrivalReason.Referral && string.IsNullOrWhiteSpace(i.ReferralFrom)) return "Write who referred the patient";
			if (i.Reason == HospitalArrivalReason.Visitor && (string.IsNullOrWhiteSpace(i.VisitorName) || string.IsNullOrWhiteSpace(i.VisitorRelation))) return "Write the visitor's name and relation to the patient";
			var dest = i.Destination ?? DefaultDestination(i.Reason!);
			if (!HospitalHandoffDestination.All.Contains(dest ?? "")) return "Choose where to send them";
			if (i.Reason is HospitalArrivalReason.Lab or HospitalArrivalReason.Imaging or HospitalArrivalReason.Operation && i.LinkId == null) return "Choose the request they came for";
			return null;
		}
	}

	/// Something the hospital already has for the patient that the desk can send them to.
	public sealed record HospitalArrivalOption(string Reason, string LinkKind, int LinkId, string Title, string? TitleEn, string? When, string Destination);
	public sealed record HospitalHandoffRow(HospitalDeskHandoff H, string? PatientName, string? PatientNameEn, string? Mrn, string? SentBy, string? AcceptedBy, bool Overdue, int AgeMinutes);
	public sealed record HospitalHandoffSent(int Id, IReadOnlyList<HospitalWhere>? Where, bool Confidential);

	public sealed class HospitalDeskHandoffService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalWhereaboutsService _where;
		public HospitalDeskHandoffService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; _where = new HospitalWhereaboutsService(db, access); }

		private IQueryable<HospitalDeskHandoff> Rows(BusinessContext ctx) => _db.Set<HospitalDeskHandoff>().Where(h => h.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;

		/// What the hospital already has waiting for this patient, by the reason it answers.
		public async Task<IReadOnlyList<HospitalArrivalOption>> OptionsAsync(BusinessContext ctx, int patientId, DateTime todayLocal, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var list = new List<HospitalArrivalOption>();
			var appts = await (from a in _db.HospitalAppointments.AsNoTracking()
							   join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
							   where a.CompanyId == cid && a.PatientId == patientId && a.AppointmentDate == todayLocal
									 && (a.Status == HospitalAppointmentStatus.Booked || a.Status == HospitalAppointmentStatus.Arrived)
							   select new { a.ID, a.StartMinute, d.Name, d.NameEn }).ToListAsync(ct);
			foreach (var a in appts)
				list.Add(new HospitalArrivalOption(HospitalArrivalReason.Clinic, HospitalHandoffLink.Appointment, a.ID, a.Name, a.NameEn, (a.StartMinute / 60).ToString("00") + ":" + (a.StartMinute % 60).ToString("00"), HospitalHandoffDestination.Clinic));
			try
			{
				var labs = await _db.Set<HospitalLabOrder>().AsNoTracking().Where(o => o.CompanyId == cid && o.PatientId == patientId && o.Status == HospitalLabStatus.Ordered)
					.Select(o => new { o.ID, o.Name, o.NameEn, o.AccessionNo }).ToListAsync(ct);
				list.AddRange(labs.Select(o => new HospitalArrivalOption(HospitalArrivalReason.Lab, HospitalHandoffLink.LabOrder, o.ID, o.Name, o.NameEn, o.AccessionNo, HospitalHandoffDestination.Lab)));
				var scans = await _db.Set<HospitalRadOrder>().AsNoTracking().Where(o => o.CompanyId == cid && o.PatientId == patientId && o.Status == HospitalRadStatus.Scheduled)
					.Select(o => new { o.ID, o.Name, o.NameEn, o.AccessionNo }).ToListAsync(ct);
				list.AddRange(scans.Select(o => new HospitalArrivalOption(HospitalArrivalReason.Imaging, HospitalHandoffLink.RadOrder, o.ID, o.Name, o.NameEn, o.AccessionNo, HospitalHandoffDestination.Imaging)));
				var cases = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == cid && s.PatientId == patientId && s.CaseDate == todayLocal && s.Status == HospitalSurgeryStatus.Planned)
					.Select(s => new { s.ID, s.Name, s.NameEn, s.StartMinute }).ToListAsync(ct);
				list.AddRange(cases.Select(s => new HospitalArrivalOption(HospitalArrivalReason.Operation, HospitalHandoffLink.Surgery, s.ID, s.Name, s.NameEn, (s.StartMinute / 60).ToString("00") + ":" + (s.StartMinute % 60).ToString("00"), HospitalHandoffDestination.Theatre)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var stay = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == cid && a.PatientId == patientId && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			if (stay is int st) list.Add(new HospitalArrivalOption(HospitalArrivalReason.Admission, HospitalHandoffLink.Admission, st, "", null, null, HospitalHandoffDestination.Ward));
			return list;
		}

		public async Task<(HospitalResult Result, HospitalHandoffSent? Sent)> SendAsync(BusinessContext ctx, HospitalHandoffInput input, int? employeeId, DateTime nowUtc, DateTime todayLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var why = HospitalHandoffRules.Refuse(input);
			if (why != null) return (HospitalResult.Fail(why), null);
			if (input.PatientId is int pid && !await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId, ct)) return (HospitalResult.Fail("not_found"), null);
			string? detail = null, detailEn = null;
			if (input.LinkId is int lid && input.PatientId is int p2)
			{
				var opt = (await OptionsAsync(ctx, p2, todayLocal, ct)).FirstOrDefault(o => o.LinkKind == input.LinkKind && o.LinkId == lid);
				if (opt == null) return (HospitalResult.Fail("This request is not open for the patient"), null);
				if (await Rows(ctx).AnyAsync(h => h.LinkKind == opt.LinkKind && h.LinkId == lid && (h.Status == HospitalHandoffStatus.Sent || h.Status == HospitalHandoffStatus.Accepted), ct))
					return (HospitalResult.Fail("The patient was already sent for this request"), null);
				detail = opt.Title; detailEn = opt.TitleEn;
			}
			string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
			var issued = await Rows(ctx).Select(h => h.HandoffNo).ToListAsync(ct);
			int max = 0; foreach (var n in issued) if (n.StartsWith("HO-") && int.TryParse(n.AsSpan(3), out var v) && v > max) max = v;
			bool visitor = input.Reason == HospitalArrivalReason.Visitor;
			var row = new HospitalDeskHandoff
			{
				CompanyId = ctx.CompanyId, HandoffNo = "HO-" + (max + 1).ToString("000000"), PatientId = input.PatientId, Reason = input.Reason!,
				Destination = input.Destination ?? HospitalHandoffRules.DefaultDestination(input.Reason!)!, LinkKind = input.LinkId == null ? null : input.LinkKind, LinkId = input.LinkId,
				Detail = Clip(detail, 200), DetailEn = Clip(detailEn, 200), ReferralFrom = Clip(input.ReferralFrom, 200), VisitorName = Clip(input.VisitorName, 120), VisitorRelation = Clip(input.VisitorRelation, 60),
				Note = Clip(input.Note, 300), Status = visitor ? HospitalHandoffStatus.Closed : HospitalHandoffStatus.Sent, SentAt = nowUtc, SentByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			if (!visitor) return (HospitalResult.Success(row.ID), new HospitalHandoffSent(row.ID, null, false));
			// a visitor is answered under the disclosure rule: a confidential file is never located
			var (where, restricted) = await _where.ForDeskAsync(ctx, new[] { input.PatientId!.Value }, nowUtc.ToLocalTime(), ct);
			bool secret = restricted.Contains(input.PatientId.Value);
			return (HospitalResult.Success(row.ID), new HospitalHandoffSent(row.ID, secret ? null : where.GetValueOrDefault(input.PatientId.Value) ?? Array.Empty<HospitalWhere>(), secret));
		}

		private async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, string to, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			// taking the person is the receiving (clinical) team's act, never the desk's — the desk only follows and chases
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return HospitalResult.Fail("forbidden");
			var h = await Rows(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (h == null) return HospitalResult.Fail("not_found");
			bool ok = (h.Status, to) switch
			{
				(HospitalHandoffStatus.Sent, HospitalHandoffStatus.Accepted) => true,
				(HospitalHandoffStatus.Sent or HospitalHandoffStatus.Accepted, HospitalHandoffStatus.Arrived) => true,
				(HospitalHandoffStatus.Sent or HospitalHandoffStatus.Accepted, HospitalHandoffStatus.Failed) => true,
				_ => false,
			};
			if (!ok) return HospitalResult.Fail("This handoff has moved on already");
			if (to == HospitalHandoffStatus.Failed && string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why the team cannot take the patient");
			h.Status = to;
			if (to == HospitalHandoffStatus.Accepted) { h.AcceptedAt = nowUtc; h.AcceptedByEmployeeId = employeeId; }
			if (to == HospitalHandoffStatus.Arrived) { h.ArrivedAt = nowUtc; h.ArrivedByEmployeeId = employeeId; h.AcceptedAt ??= nowUtc; h.AcceptedByEmployeeId ??= employeeId; }
			if (to == HospitalHandoffStatus.Failed) { h.FailedAt = nowUtc; h.FailedByEmployeeId = employeeId; h.FailReason = reason!.Trim()[..Math.Min(300, reason.Trim().Length)]; }
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(h.ID);
		}

		public Task<HospitalResult> AcceptAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default) => MoveAsync(ctx, id, HospitalHandoffStatus.Accepted, null, employeeId, nowUtc, ct);
		public Task<HospitalResult> ArrivedAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default) => MoveAsync(ctx, id, HospitalHandoffStatus.Arrived, null, employeeId, nowUtc, ct);
		public Task<HospitalResult> FailAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default) => MoveAsync(ctx, id, HospitalHandoffStatus.Failed, reason, employeeId, nowUtc, ct);

		/// The desk chases a handoff nobody took: counted, timed.
		public async Task<HospitalResult> RemindAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var h = await Rows(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (h == null) return HospitalResult.Fail("not_found");
			if (!HospitalHandoffStatus.Open(h.Status)) return HospitalResult.Fail("This handoff has moved on already");
			h.RemindedAt = nowUtc; h.Reminders++;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(h.ID);
		}

		public async Task<(int Open, int Overdue)> CountsAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var open = await Rows(ctx).AsNoTracking().Where(h => h.Status == HospitalHandoffStatus.Sent || h.Status == HospitalHandoffStatus.Accepted).ToListAsync(ct);
				return (open.Count, open.Count(h => HospitalHandoffRules.Overdue(h, nowUtc)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (0, 0); }
		}

		/// The handoffs, newest first: `open` = still with nobody or on the way; `destination` narrows to one team.
		public async Task<(IReadOnlyList<HospitalHandoffRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? destination, bool openOnly, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			IQueryable<HospitalDeskHandoff> rows;
			try { rows = Rows(ctx).AsNoTracking(); await rows.AnyAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalHandoffRow>(), 0); }
			if (openOnly) rows = rows.Where(h => h.Status == HospitalHandoffStatus.Sent || h.Status == HospitalHandoffStatus.Accepted);
			else rows = rows.Where(h => h.SentAt >= nowUtc.AddDays(-1) || h.Status == HospitalHandoffStatus.Sent || h.Status == HospitalHandoffStatus.Accepted);
			if (HospitalHandoffDestination.All.Contains(destination ?? "")) rows = rows.Where(h => h.Destination == destination);
			var joined = from h in rows
						 join p0 in _db.HospitalPatients.AsNoTracking() on h.PatientId equals p0.ID into pj
						 from p in pj.DefaultIfEmpty()
						 select new { h, Name = (string?)p.Name, NameEn = (string?)p.NameEn, Mrn = (string?)p.Mrn };
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				joined = joined.Where(x => x.h.HandoffNo.Contains(s) || (x.Name ?? "").Contains(s) || (x.NameEn ?? "").Contains(s) || (x.Mrn ?? "").Contains(s) || (x.h.Detail ?? "").Contains(s));
			}
			int total = await joined.CountAsync(ct);
			var page1 = await joined.OrderByDescending(x => x.h.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var emp = page1.SelectMany(x => new[] { x.h.SentByEmployeeId, x.h.AcceptedByEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && emp.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			return (page1.Select(x => new HospitalHandoffRow(x.h, x.Name, x.NameEn, x.Mrn,
				x.h.SentByEmployeeId is int sb ? names.GetValueOrDefault(sb) : null, x.h.AcceptedByEmployeeId is int ab ? names.GetValueOrDefault(ab) : null,
				HospitalHandoffRules.Overdue(x.h, nowUtc), (int)Math.Max(0, (nowUtc - (x.h.AcceptedAt ?? x.h.SentAt)).TotalMinutes))).ToList(), total);
		}
	}
}
