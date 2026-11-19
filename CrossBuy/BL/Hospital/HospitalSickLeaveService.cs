using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H56 — sick leave certificates (HospitalSickLeave). A doctor (`consult`) issues; anyone who reads the
	// hospital sees the patient's certificates; a wrong one is voided with a reason by a doctor or the administrator.
	// The issuing doctor is the issuing employee's practitioner record (when they have one) — never chosen.
	// ==========================================================================================

	public sealed class HospitalSickLeaveInput
	{
		public DateTime? FromDate { get; set; } public DateTime? ToDate { get; set; } public string? Kind { get; set; }
		public string? Diagnosis { get; set; } public bool ShowDiagnosis { get; set; } public string? Note { get; set; } public int? EncounterId { get; set; }
	}

	public sealed record HospitalSickLeaveRow(HospitalSickLeave Leave, string? Doctor, string? DoctorEn, string? VoidedBy, string? VoidedByEn);

	public static class HospitalSickLeaveRules
	{
		/// The longest one certificate may cover, and how far back it may start (a certificate is not a back-dated excuse).
		public const int MaxDays = 30, MaxBackDays = 3;

		public static int Days(DateTime from, DateTime to) => (int)(to.Date - from.Date).TotalDays + 1;

		public static string? IssueRefusal(HospitalSickLeaveInput i, DateTime todayWard)
		{
			if (i.FromDate is not { } f || i.ToDate is not { } t) return "Give the first and the last day";
			if (t.Date < f.Date) return "The last day is before the first";
			if (f.Date < todayWard.Date.AddDays(-MaxBackDays)) return "A sick leave cannot start more than {0} days ago";
			if (Days(f, t) > MaxDays) return "One certificate covers at most {0} days";
			if (!HospitalSickLeaveKind.All.Contains(i.Kind ?? "")) return "Choose rest, light duty or in hospital";
			if (i.ShowDiagnosis && string.IsNullOrWhiteSpace(i.Diagnosis)) return "Write the diagnosis to print it";
			if ((i.Diagnosis?.Length ?? 0) > 500 || (i.Note?.Length ?? 0) > 500) return "A text is too long";
			return null;
		}

		// ---- H208 (Kuwait, MOH charter of January 2026): a certificate only for a patient SEEN in person, never back-dated
		// before that visit; a QR anyone can verify; the Ministry's authentication recorded on it. -----------------------
		public const int SeenWithinHours = 24;
		public const string NotSeen = "A sick leave is issued only to a patient seen in person: there is no visit in the last 24 hours";
		public const string BeforeVisit = "A sick leave cannot start before the visit it is issued at";

		/// Why the certificate cannot be issued at this visit (visitStartUtc null = no visit in the window), or null.
		public static string? SeenRefusal(DateTime? visitStartUtc, DateTime fromDate, DateTime nowUtc)
		{
			if (visitStartUtc is not DateTime v || nowUtc - v > TimeSpan.FromHours(SeenWithinHours)) return NotSeen;
			return fromDate.Date < HospitalNursingService.UtcToWard(v).Date ? BeforeVisit : null;
		}

		/// The QR's code: 10 characters from an alphabet without 0/O/1/I (read aloud or typed from the paper without doubt).
		public static string NewVerifyCode()
		{
			const string A = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
			var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
			return new string(b.Select(x => A[x % A.Length]).ToArray());
		}

		public static string? MohRefusal(HospitalSickLeave l, string? reference)
		{
			if (l.VoidedAt != null) return "A void certificate is not authenticated";
			if (l.MohAuthAt != null) return "The Ministry's authentication is already recorded";
			if (string.IsNullOrWhiteSpace(reference)) return "Write the Ministry's authentication reference";
			return reference.Trim().Length > 60 ? "The reference is too long" : null;
		}
	}

	/// H208: what the public verification page shows — enough to know the paper is genuine, nothing of the illness.
	public sealed record HospitalSickLeaveCheck(string LeaveNo, string Facility, string? FacilityEn, string Initials, DateTime From, DateTime To, int Days, DateTime IssuedAt,
		bool Void, DateTime? MohAuthAt, string? MohAuthRef);

	public sealed class HospitalSickLeaveService
	{
		public const string NoTables = "Sick leave is not installed yet (hospital_h56_sick_leave.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSickLeaveService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalSickLeave> Leaves(BusinessContext ctx) => _db.Set<HospitalSickLeave>().Where(s => s.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// The patient's certificates, newest first. `Ready` false = no H56 table.
		public async Task<(IReadOnlyList<HospitalSickLeaveRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try
			{
				var rows = await Leaves(ctx).AsNoTracking().Where(s => s.PatientId == patientId).OrderByDescending(s => s.ID).ToListAsync(ct);
				var dids = rows.Where(r => r.PractitionerId != null).Select(r => r.PractitionerId!.Value).Distinct().ToList();
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && dids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				var eids = rows.Where(r => r.VoidedByEmployeeId != null).Select(r => r.VoidedByEmployeeId!.Value).Concat(rows.Select(r => r.IssuedByEmployeeId)).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				return (rows.Select(r =>
				{
					var d = r.PractitionerId is int pid ? docs.GetValueOrDefault(pid) : null;
					var issuer = people.GetValueOrDefault(r.IssuedByEmployeeId);
					var v = r.VoidedByEmployeeId is int vid ? people.GetValueOrDefault(vid) : null;
					return new HospitalSickLeaveRow(r, d?.Name ?? issuer?.FullName, d?.NameEn ?? issuer?.FullNameEn ?? issuer?.FullName, v?.FullName, v?.FullNameEn ?? v?.FullName);
				}).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalSickLeaveRow>(), false); }
		}

		/// Issues a numbered certificate; final from here (a mistake is voided, not edited).
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, int patientId, HospitalSickLeaveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (HospitalSickLeaveRules.IssueRefusal(input, HospitalNursingService.UtcToWard(nowUtc)) is string why)
				return HospitalResult.Fail(why, why.Contains("{0}") ? (why.Contains("ago") ? HospitalSickLeaveRules.MaxBackDays : HospitalSickLeaveRules.MaxDays).ToString() : "");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.EncounterId is int eid && !await _db.HospitalEncounters.AnyAsync(e => e.ID == eid && e.CompanyId == ctx.CompanyId && e.PatientId == patientId, ct)) return HospitalResult.Fail("not_found");
			// H208: the visit it is issued at — the one named, else the patient's latest in the last 24 hours; none = refused
			var since = nowUtc.AddHours(-HospitalSickLeaveRules.SeenWithinHours);
			var visit = await _db.HospitalEncounters.AsNoTracking()
				.Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == patientId && (input.EncounterId == null ? e.StartedAt >= since : e.ID == input.EncounterId))
				.OrderByDescending(e => e.StartedAt).Select(e => new { e.ID, e.StartedAt }).FirstOrDefaultAsync(ct);
			if (HospitalSickLeaveRules.SeenRefusal(visit?.StartedAt, input.FromDate!.Value, nowUtc) is string unseen) return HospitalResult.Fail(unseen);
			input.EncounterId = visit!.ID;
			var doctor = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == me && p.IsActive).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct);
			try
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Leaves(ctx).Select(s => s.LeaveNo).ToListAsync(ct);
					var row = new HospitalSickLeave
					{
						CompanyId = ctx.CompanyId, LeaveNo = HospitalDentalPracticeRules.NextNo(nos, "SL-", 6), PatientId = patientId, EncounterId = input.EncounterId, PractitionerId = doctor,
						FromDate = input.FromDate!.Value.Date, ToDate = input.ToDate!.Value.Date, Days = HospitalSickLeaveRules.Days(input.FromDate.Value, input.ToDate.Value),
						Kind = input.Kind!, Diagnosis = Clip(input.Diagnosis, 500), ShowDiagnosis = input.ShowDiagnosis, Note = Clip(input.Note, 500), IssuedAt = nowUtc, IssuedByEmployeeId = me,
						VerifyCode = HospitalSickLeaveRules.NewVerifyCode(),   // H208
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // the number was taken a moment ago: the next one
				}
				return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// H208: the Ministry of Health's authentication of the certificate (the patient has it authenticated through the
		/// Ministry; the desk records its reference). Once, and never on a void certificate.
		public async Task<HospitalResult> MohAuthAsync(BusinessContext ctx, int id, string? reference, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Read, ct)) return HospitalResult.Fail("forbidden");
			var l = await Leaves(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			if (HospitalSickLeaveRules.MohRefusal(l, reference) is string why) return HospitalResult.Fail(why);
			l.MohAuthRef = reference!.Trim(); l.MohAuthAt = nowUtc; l.MohAuthByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(l.ID);
		}

		/// H208: the public check behind the QR — by its code alone (any company: the code is unique), minimal data only.
		public static async Task<HospitalSickLeaveCheck?> CheckAsync(CrossDbContext db, string? code, CancellationToken ct = default)
		{
			code = code?.Trim().ToUpperInvariant();
			if (string.IsNullOrEmpty(code) || code.Length > 16) return null;
			var l = await db.Set<HospitalSickLeave>().AsNoTracking().FirstOrDefaultAsync(x => x.VerifyCode == code, ct);
			if (l == null) return null;
			var p = await db.HospitalPatients.AsNoTracking().Where(x => x.ID == l.PatientId && x.CompanyId == l.CompanyId).Select(x => new { x.Name, x.NameEn }).FirstOrDefaultAsync(ct);
			var c = await db.Companies.AsNoTracking().Where(x => x.CompanyID == l.CompanyId).Select(x => new { Ar = x.ComoanyNameAr ?? x.CompanyName, En = x.CompanyName }).FirstOrDefaultAsync(ct);
			return new HospitalSickLeaveCheck(l.LeaveNo, c?.Ar ?? "", c?.En, Initials(p?.NameEn ?? p?.Name), l.FromDate, l.ToDate, l.Days, l.IssuedAt,
				l.VoidedAt != null, l.MohAuthAt, l.MohAuthRef);
		}

		/// The patient's initials only (the page proves the paper, not who is ill): "Mahmoud Saeed Osman" → "M. S. O."
		public static string Initials(string? name) =>
			string.Join(" ", (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).Take(4).Select(w => char.ToUpperInvariant(w[0]) + "."));

		/// Voids a certificate with its reason (a doctor or the administrator). It stays on the file, marked void.
		public async Task<HospitalResult> VoidAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write the reason");
			try
			{
				int n = await Leaves(ctx).Where(s => s.ID == id && s.VoidedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.VoidedAt, nowUtc)
					.SetProperty(s => s.VoidedByEmployeeId, employeeId).SetProperty(s => s.VoidReason, Clip(reason, 300)), ct);
				return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This certificate is already void");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return HospitalResult.Fail(NoTables); }
		}
	}
}
