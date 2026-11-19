using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H203 — referrals. A doctor asks:
	//   · INTERNAL — another department of this hospital (and optionally one of its doctors) to see the patient: the
	//     department accepts it, then answers with its reply; or declines it with a reason;
	//   · EXTERNAL — another facility: the referral is sent; it closes when that facility's reply is recorded.
	// Each states its question (required) and the clinical summary, and its urgency sets the time it is due by — an
	// emergency in 2 hours, urgent in 24 hours, routine in 14 days. An open referral past that time is OVERDUE on the
	// list. The one who asked may cancel an open referral, with the reason. A referral is never edited after it is sent:
	// what was asked stays what was asked.
	// ==========================================================================================
	public sealed class HospitalReferralInput
	{
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string? Kind { get; set; }
		public int? ToDepartmentId { get; set; }
		public int? ToPractitionerId { get; set; }
		public string? ToFacility { get; set; }
		public string? ToSpecialty { get; set; }
		public string? Urgency { get; set; }
		public string? Reason { get; set; }
		public string? Summary { get; set; }
	}

	public sealed record HospitalReferralRow(HospitalReferral Referral, string Patient, string PatientEn, string Mrn, string? From, string? FromEn,
		string? ToDepartment, string? ToDepartmentEn, string? ToDoctor, string? ToDoctorEn, bool Overdue);

	public static class HospitalReferralRules
	{
		public static DateTime DueAt(string urgency, DateTime createdUtc) => urgency switch
		{
			HospitalReferralUrgency.Emergency => createdUtc.AddHours(2),
			HospitalReferralUrgency.Urgent => createdUtc.AddHours(24),
			_ => createdUtc.AddDays(14),
		};

		public static bool Overdue(HospitalReferral r, DateTime nowUtc) => HospitalReferralStatus.Open(r.Status) && nowUtc > r.DueAt;

		public static string? CreateRefusal(HospitalReferralInput i, int? fromPractitionerId)
		{
			if (string.IsNullOrEmpty(i.Kind) || !HospitalReferralKind.All.Contains(i.Kind)) return "Choose an internal or an external referral";
			if (string.IsNullOrEmpty(i.Urgency) || !HospitalReferralUrgency.All.Contains(i.Urgency)) return "Choose the urgency";
			if (string.IsNullOrWhiteSpace(i.Reason)) return "Write the question for the doctor you refer to";
			if (i.Reason.Trim().Length > 1000 || (i.Summary?.Trim().Length ?? 0) > 2000) return "The text is too long";
			if (i.Kind == HospitalReferralKind.Internal)
			{
				if (i.ToDepartmentId == null) return "Choose the department you refer to";
				if (i.ToPractitionerId != null && i.ToPractitionerId == fromPractitionerId) return "A doctor cannot refer a patient to themself";
			}
			else if (string.IsNullOrWhiteSpace(i.ToFacility)) return "Name the facility you refer to";
			return null;
		}

		/// Why the referral cannot make that move now (to: Accepted, Declined, Completed, Cancelled), or null.
		public static string? MoveRefusal(HospitalReferral r, string to, string? text)
		{
			bool internalRef = r.Kind == HospitalReferralKind.Internal;
			switch (to)
			{
				case HospitalReferralStatus.Accepted:
					if (!internalRef) return "Only a referral within the hospital is accepted here";
					return r.Status == HospitalReferralStatus.Sent ? null : "This referral is no longer waiting";
				case HospitalReferralStatus.Declined:
					if (!internalRef) return "Only a referral within the hospital is declined here";
					if (r.Status != HospitalReferralStatus.Sent) return "This referral is no longer waiting";
					return string.IsNullOrWhiteSpace(text) ? "Write why the referral is declined" : null;
				case HospitalReferralStatus.Completed:
					if (!HospitalReferralStatus.Open(r.Status)) return "This referral is closed";
					if (internalRef && r.Status != HospitalReferralStatus.Accepted) return "Accept the referral before answering it";
					return string.IsNullOrWhiteSpace(text) ? (internalRef ? "Write the reply to the referring doctor" : "Record what the facility wrote back") : null;
				case HospitalReferralStatus.Cancelled:
					if (!HospitalReferralStatus.Open(r.Status)) return "This referral is closed";
					return string.IsNullOrWhiteSpace(text) ? "Write why the referral is cancelled" : null;
				default:
					return "not_found";
			}
		}
	}

	public sealed class HospitalReferralService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalReferralService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalReferral> Referrals(BusinessContext ctx) => _db.Set<HospitalReferral>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		/// The signed-in employee's practitioner (the referring doctor), if they are one.
		private async Task<HospitalPractitioner?> MeAsync(BusinessContext ctx, CancellationToken ct) =>
			ctx.EmployeeId is int e ? await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == e, ct) : null;

		public async Task<HospitalResult> CreateAsync(BusinessContext ctx, HospitalReferralInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var me = await MeAsync(ctx, ct);
			if (HospitalReferralRules.CreateRefusal(input, me?.ID) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.EncounterId is int enc && !await _db.HospitalEncounters.AnyAsync(e => e.ID == enc && e.CompanyId == ctx.CompanyId && e.PatientId == input.PatientId, ct))
				return HospitalResult.Fail("The visit is not this patient's");
			bool internalRef = input.Kind == HospitalReferralKind.Internal;
			if (internalRef)
			{
				if (!await _db.HospitalDepartments.AnyAsync(d => d.ID == input.ToDepartmentId && d.CompanyId == ctx.CompanyId && d.IsActive, ct)) return HospitalResult.Fail("Choose the department you refer to");
				if (input.ToPractitionerId is int tp && !await _db.HospitalPractitioners.AnyAsync(p => p.ID == tp && p.CompanyId == ctx.CompanyId && p.DepartmentId == input.ToDepartmentId, ct))
					return HospitalResult.Fail("The doctor is not of that department");
			}
			var nos = await Referrals(ctx).Where(r => r.ReferralNo.StartsWith("REF-")).Select(r => r.ReferralNo).ToListAsync(ct);
			int n = 0; foreach (var no in nos) if (int.TryParse(no.AsSpan(4), out var v) && v > n) n = v;
			var r = new HospitalReferral
			{
				CompanyId = ctx.CompanyId, ReferralNo = "REF-" + (n + 1).ToString("000000"), PatientId = input.PatientId, EncounterId = input.EncounterId,
				Kind = input.Kind!, FromPractitionerId = me?.ID, FromDepartmentId = me?.DepartmentId,
				ToDepartmentId = internalRef ? input.ToDepartmentId : null, ToPractitionerId = internalRef ? input.ToPractitionerId : null,
				ToFacility = internalRef ? null : Clip(input.ToFacility, 200), ToSpecialty = internalRef ? null : Clip(input.ToSpecialty, 100),
				Urgency = input.Urgency!, Reason = Clip(input.Reason, 1000)!, Summary = Clip(input.Summary, 2000),
				Status = HospitalReferralStatus.Sent, DueAt = HospitalReferralRules.DueAt(input.Urgency!, nowUtc), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Add(r);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		/// Accept, decline (reason), answer (reply) or cancel (reason) — only the first move from a state lands.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, string to, string? text, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Referrals(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalReferralRules.MoveRefusal(r, to, text) is string why) return HospitalResult.Fail(why);
			var q = Referrals(ctx).Where(x => x.ID == id && x.Status == r.Status);
			string? t = Clip(text, to == HospitalReferralStatus.Completed ? 2000 : 500);
			int n = to switch
			{
				HospitalReferralStatus.Accepted => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.AcceptedAt, nowUtc).SetProperty(x => x.AcceptedByEmployeeId, employeeId), ct),
				HospitalReferralStatus.Completed => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.Reply, t).SetProperty(x => x.ClosedAt, nowUtc).SetProperty(x => x.ClosedByEmployeeId, employeeId), ct),
				_ => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.CloseReason, t).SetProperty(x => x.ClosedAt, nowUtc).SetProperty(x => x.ClosedByEmployeeId, employeeId), ct),
			};
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("Someone else changed this referral: open it again");
		}

		public async Task<(IReadOnlyList<HospitalReferralRow> Rows, int Total, int Open, int Overdue, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, string? kind, int? toDepartment,
			bool overdueOnly, int? patientId, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalReferralRow>(), 0, 0, 0, true);
			try
			{
				var query = from r in Referrals(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							join f0 in _db.HospitalPractitioners.AsNoTracking() on r.FromPractitionerId equals f0.ID into ff
							from f in ff.DefaultIfEmpty()
							join d0 in _db.HospitalDepartments.AsNoTracking() on r.ToDepartmentId equals d0.ID into dd
							from d in dd.DefaultIfEmpty()
							join t0 in _db.HospitalPractitioners.AsNoTracking() on r.ToPractitionerId equals t0.ID into tt
							from t in tt.DefaultIfEmpty()
							where p.CompanyId == ctx.CompanyId
							select new { r, p, f, d, t };
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.r.Status == status);
				if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.r.Kind == kind);
				if (toDepartment is int dep) query = query.Where(x => x.r.ToDepartmentId == dep);
				if (patientId is int pid) query = query.Where(x => x.r.PatientId == pid);
				if (overdueOnly) query = query.Where(x => (x.r.Status == HospitalReferralStatus.Sent || x.r.Status == HospitalReferralStatus.Accepted) && x.r.DueAt < nowUtc);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var s = q.Trim();
					query = query.Where(x => x.r.ReferralNo.Contains(s) || x.p.Name.Contains(s) || x.p.NameEn.Contains(s) || x.p.Mrn.Contains(s) || (x.r.ToFacility != null && x.r.ToFacility.Contains(s)));
				}
				int total = await query.CountAsync(ct);
				var open = Referrals(ctx).Where(r => r.Status == HospitalReferralStatus.Sent || r.Status == HospitalReferralStatus.Accepted);
				int openN = await open.CountAsync(ct), overdueN = await open.CountAsync(r => r.DueAt < nowUtc, ct);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.r.Status == HospitalReferralStatus.Sent || x.r.Status == HospitalReferralStatus.Accepted).ThenBy(x => x.r.DueAt).ThenByDescending(x => x.r.ID)
					.Skip((page - 1) * size).Take(size).ToListAsync(ct);
				return (rows.Select(x => new HospitalReferralRow(x.r, x.p.Name, x.p.NameEn, x.p.Mrn, x.f?.Name, x.f?.NameEn, x.d?.Name, x.d?.NameEn, x.t?.Name, x.t?.NameEn,
					HospitalReferralRules.Overdue(x.r, nowUtc))).ToList(), total, openN, overdueN, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return (Array.Empty<HospitalReferralRow>(), 0, 0, 0, false); }
		}

		public async Task<IReadOnlyList<(int Id, string Name, string? NameEn)>> DepartmentsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.ID, d.Name, d.NameEn }).ToListAsync(ct))
				.Select(d => (d.ID, d.Name, d.NameEn)).ToList();

		public async Task<IReadOnlyList<(int Id, string Name, string NameEn, int? DepartmentId)>> DoctorsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.DepartmentId != null).OrderBy(p => p.Name).Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId }).ToListAsync(ct))
				.Select(p => (p.ID, p.Name, p.NameEn, p.DepartmentId)).ToList();
	}
}
