using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H77 — inpatient consultation requests. A doctor asks a specialty; a doctor of that specialty (or the one
	// named) accepts and answers; the asker or an administrator can cancel while it is unanswered. Who may answer is
	// decided from the practitioner register (the signed-in employee's practitioner and its department), never from a
	// field the caller sends.
	// ==========================================================================================
	public sealed class HospitalConsultRequestInput
	{
		public int AdmissionId { get; set; }
		public int DepartmentId { get; set; }
		public int? PractitionerId { get; set; }
		public string? Urgency { get; set; }
		public string? Question { get; set; }
	}

	public sealed class HospitalConsultAnswerInput
	{
		public string? Findings { get; set; }
		public string? Recommendations { get; set; }
		public bool FollowUp { get; set; }
	}

	public sealed record HospitalConsultRow(HospitalConsultRequest Request, string Name, string NameEn, string Mrn, string? Photo, string Department, string DepartmentEn,
		string? Practitioner, string? PractitionerEn, string? Bed, string? Requester, string? Answerer, bool Overdue, bool CanAnswer, bool CanCancel,
		string? PractitionerPhoto = null, string? RequesterPhoto = null, string? AnswererPhoto = null);

	/// What the nursing station shows beside a patient: the open requests and the answers not older than two days.
	public sealed record HospitalConsultBadge(int Id, string RequestNo, string Department, string DepartmentEn, string Status, string Urgency, bool Overdue);

	public static class HospitalConsultRequestRules
	{
		public static string? RequestRefusal(HospitalConsultRequestInput i)
		{
			if (i.DepartmentId <= 0) return "Choose the specialty";
			if (!HospitalConsultUrgency.All.Contains(i.Urgency ?? "")) return "Choose how urgent it is";
			if (string.IsNullOrWhiteSpace(i.Question)) return "Write the question for the consultant";
			if (i.Question.Trim().Length > 1000) return "The question is too long";
			return null;
		}

		public static string? AnswerRefusal(HospitalConsultRequest r, HospitalConsultAnswerInput a)
		{
			if (!HospitalConsultRequestStatus.Open(r.Status)) return "This consultation is closed";
			if (string.IsNullOrWhiteSpace(a.Recommendations)) return "Write the recommendations";
			if ((a.Findings?.Trim().Length ?? 0) > 2000 || a.Recommendations.Trim().Length > 2000) return "The answer is too long";
			return null;
		}

		/// Overdue: still open after its due time. An accepted request is still overdue until it is answered.
		public static bool Overdue(HospitalConsultRequest r, DateTime nowUtc) => HospitalConsultRequestStatus.Open(r.Status) && nowUtc > r.DueAt;

		/// Who answers: a doctor of the specialty (when no one is named) or the named doctor; an administrator always.
		public static bool MayAnswer(HospitalConsultRequest r, bool admin, IReadOnlyCollection<(int PractitionerId, int? DepartmentId)> mine) =>
			admin || (r.PractitionerId is int named ? mine.Any(m => m.PractitionerId == named) : mine.Any(m => m.DepartmentId == r.DepartmentId));
	}

	public sealed class HospitalConsultRequestService
	{
		public const string NoTables = "Consultation requests are not installed yet (hospital_h77_consult_requests.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalConsultRequestService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalConsultRequest> Requests(BusinessContext ctx) => _db.Set<HospitalConsultRequest>().Where(x => x.CompanyId == ctx.CompanyId);

		/// The signed-in employee's practitioner records (one employee may sit in more than one department).
		private async Task<List<(int PractitionerId, int? DepartmentId)>> MineAsync(BusinessContext ctx, CancellationToken ct)
		{
			if (ctx.EmployeeId is not int me) return new();
			return (await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == me)
				.Select(p => new { p.ID, p.DepartmentId }).ToListAsync(ct)).Select(p => (p.ID, p.DepartmentId)).ToList();
		}

		/// The open requests and recent answers of these admissions (the nursing station's badges). Empty when not installed.
		public static async Task<IReadOnlyDictionary<int, IReadOnlyList<HospitalConsultBadge>>> BadgesAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> admissionIds, DateTime nowUtc, CancellationToken ct)
		{
			var result = new Dictionary<int, IReadOnlyList<HospitalConsultBadge>>();
			if (admissionIds.Count == 0) return result;
			try
			{
				var since = nowUtc.AddDays(-2);
				var rows = await (from r in db.Set<HospitalConsultRequest>().AsNoTracking()
								  join d in db.HospitalDepartments.AsNoTracking() on r.DepartmentId equals d.ID
								  where r.CompanyId == companyId && admissionIds.Contains(r.AdmissionId)
									&& (r.Status == HospitalConsultRequestStatus.Requested || r.Status == HospitalConsultRequestStatus.Accepted || (r.Status == HospitalConsultRequestStatus.Answered && r.AnsweredAt >= since))
								  orderby r.RequestedAt
								  select new { r, d.Name, d.NameEn }).ToListAsync(ct);
				foreach (var g in rows.GroupBy(x => x.r.AdmissionId))
					result[g.Key] = g.Select(x => new HospitalConsultBadge(x.r.ID, x.r.RequestNo, x.Name, x.NameEn ?? x.Name, x.r.Status, x.r.Urgency, HospitalConsultRequestRules.Overdue(x.r, nowUtc))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return result;
		}

		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, HospitalConsultRequestInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalConsultRequestRules.RequestRefusal(input) is string why) return HospitalResult.Fail(why);
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			if (!await _db.HospitalDepartments.AnyAsync(d => d.ID == input.DepartmentId && d.CompanyId == ctx.CompanyId && d.IsActive, ct)) return HospitalResult.Fail("Choose the specialty");
			if (input.PractitionerId is int pid && !await _db.HospitalPractitioners.AnyAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId && p.DepartmentId == input.DepartmentId, ct))
				return HospitalResult.Fail("This doctor is not in the chosen specialty");
			try
			{
				if (await Requests(ctx).AnyAsync(r => r.AdmissionId == adm.ID && r.DepartmentId == input.DepartmentId
					&& (r.Status == HospitalConsultRequestStatus.Requested || r.Status == HospitalConsultRequestStatus.Accepted), ct))
					return HospitalResult.Fail("This specialty already has an open request for the patient");
				var nos = await Requests(ctx).Select(r => r.RequestNo).ToListAsync(ct);
				var row = new HospitalConsultRequest
				{
					CompanyId = ctx.CompanyId, RequestNo = HospitalDentalPracticeRules.NextNo(nos, "CR-", 6), AdmissionId = adm.ID, PatientId = adm.PatientId,
					DepartmentId = input.DepartmentId, PractitionerId = input.PractitionerId, Urgency = input.Urgency!, Question = input.Question!.Trim(),
					Status = HospitalConsultRequestStatus.Requested, RequestedAt = nowUtc, RequestedByEmployeeId = employeeId, DueAt = nowUtc + HospitalConsultUrgency.Window(input.Urgency!),
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private async Task<(HospitalConsultRequest? Row, string? Refusal)> AnswerableAsync(BusinessContext ctx, int id, CancellationToken ct)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return (null, "forbidden");
			var r = await Requests(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return (null, "not_found");
			bool admin = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			if (!HospitalConsultRequestRules.MayAnswer(r, admin, await MineAsync(ctx, ct))) return (null, "Only a doctor of the specialty asked can answer");
			return (r, null);
		}

		public async Task<HospitalResult> AcceptAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (r, why) = await AnswerableAsync(ctx, id, ct);
				if (r == null) return HospitalResult.Fail(why!);
				if (r.Status != HospitalConsultRequestStatus.Requested) return HospitalResult.Fail("This consultation is already accepted or closed");
				r.Status = HospitalConsultRequestStatus.Accepted; r.AcceptedAt = nowUtc; r.AcceptedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The answer closes the request; answering accepts it too when no one did first.
		public async Task<HospitalResult> AnswerAsync(BusinessContext ctx, int id, HospitalConsultAnswerInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (r, why) = await AnswerableAsync(ctx, id, ct);
				if (r == null) return HospitalResult.Fail(why!);
				if (HospitalConsultRequestRules.AnswerRefusal(r, input) is string no) return HospitalResult.Fail(no);
				if (r.AcceptedAt == null) { r.AcceptedAt = nowUtc; r.AcceptedByEmployeeId = employeeId; }
				r.Status = HospitalConsultRequestStatus.Answered; r.AnsweredAt = nowUtc; r.AnsweredByEmployeeId = employeeId;
				r.Findings = Clip(input.Findings, 2000); r.Recommendations = Clip(input.Recommendations, 2000); r.FollowUp = input.FollowUp;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The asker (or an administrator) withdraws a request no one has answered.
		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var r = await Requests(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (r == null) return HospitalResult.Fail("not_found");
				bool admin = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
				if (!admin && r.RequestedByEmployeeId != employeeId) return HospitalResult.Fail("Only the doctor who asked can cancel it");
				if (!HospitalConsultRequestStatus.Open(r.Status)) return HospitalResult.Fail("This consultation is closed");
				if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why it is cancelled");
				r.Status = HospitalConsultRequestStatus.Cancelled; r.CancelledAt = nowUtc; r.CancelledByEmployeeId = employeeId; r.CancelReason = Clip(reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The register. Scope "mine" = asked of my specialties or of me; "asked" = what I asked; anything else = all.
		/// Open ones first, the most urgent and the longest overdue at the top; searched by number, patient or file.
		public async Task<(IReadOnlyList<HospitalConsultRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? scope, string? status, string? q, int? admissionId, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var mine = await MineAsync(ctx, ct);
				bool admin = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
				var myDeps = mine.Where(m => m.DepartmentId != null).Select(m => m.DepartmentId!.Value).Distinct().ToList();
				var myIds = mine.Select(m => m.PractitionerId).ToList();
				var query = from r in Requests(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							join d in _db.HospitalDepartments.AsNoTracking() on r.DepartmentId equals d.ID
							join pr in _db.HospitalPractitioners.AsNoTracking() on r.PractitionerId equals pr.ID into prj
							from pr in prj.DefaultIfEmpty()
							select new { r, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Dep = d.Name, DepEn = d.NameEn, Pr = pr == null ? null : pr.Name, PrEn = pr == null ? null : pr.NameEn, PrEmp = pr == null ? null : pr.EmployeeId };
				if (scope == "mine") query = query.Where(x => x.r.PractitionerId != null ? myIds.Contains(x.r.PractitionerId.Value) : myDeps.Contains(x.r.DepartmentId));
				else if (scope == "asked") query = query.Where(x => x.r.RequestedByEmployeeId == ctx.EmployeeId);
				if (admissionId is int aid) query = query.Where(x => x.r.AdmissionId == aid);
				if (status == "open") query = query.Where(x => x.r.Status == HospitalConsultRequestStatus.Requested || x.r.Status == HospitalConsultRequestStatus.Accepted);
				else if (HospitalConsultRequestStatus.All.Contains(status ?? "")) query = query.Where(x => x.r.Status == status);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var s = q.Trim();
					query = query.Where(x => x.r.RequestNo.Contains(s) || x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)) || x.Mrn.Contains(s));
				}
				int total = await query.CountAsync(ct);
				pageSize = Math.Clamp(pageSize, 5, 100); page = Math.Max(1, page);
				var rows = await query
					.OrderBy(x => x.r.Status == HospitalConsultRequestStatus.Requested || x.r.Status == HospitalConsultRequestStatus.Accepted ? 0 : 1)
					.ThenBy(x => x.r.Status == HospitalConsultRequestStatus.Requested || x.r.Status == HospitalConsultRequestStatus.Accepted ? x.r.DueAt : DateTime.MaxValue)
					.ThenByDescending(x => x.r.RequestedAt)
					.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var admIds = rows.Select(x => x.r.AdmissionId).Distinct().ToList();
				var beds = await (from s in _db.HospitalBedStays.AsNoTracking()
								  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
								  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && admIds.Contains(s.AdmissionId)
								  select new { s.AdmissionId, b.Code }).ToListAsync(ct);
				var ids = rows.SelectMany(x => new[] { x.r.RequestedByEmployeeId, x.r.AnsweredByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				string? Who(int? id) => id is int i && people.TryGetValue(i, out var n) ? n : null;
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, ids.Concat(rows.Where(x => x.PrEmp != null).Select(x => x.PrEmp!.Value)), ct);
				string? Face(int? id) => id is int i ? photos.GetValueOrDefault(i) : null;
				return (rows.Select(x => new HospitalConsultRow(x.r, x.Name, x.NameEn ?? "", x.Mrn, x.PhotoPath, x.Dep, x.DepEn ?? x.Dep, x.Pr, x.PrEn,
					beds.FirstOrDefault(b => b.AdmissionId == x.r.AdmissionId)?.Code, Who(x.r.RequestedByEmployeeId), Who(x.r.AnsweredByEmployeeId),
					HospitalConsultRequestRules.Overdue(x.r, nowUtc),
					HospitalConsultRequestStatus.Open(x.r.Status) && HospitalConsultRequestRules.MayAnswer(x.r, admin, mine),
					HospitalConsultRequestStatus.Open(x.r.Status) && (admin || x.r.RequestedByEmployeeId == ctx.EmployeeId),
					Face(x.PrEmp), Face(x.r.RequestedByEmployeeId), Face(x.r.AnsweredByEmployeeId))).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalConsultRow>(), 0, false); }
		}

		/// The specialties and their doctors, for the request window.
		public async Task<IReadOnlyList<(int Id, string Name, string NameEn, IReadOnlyList<(int Id, string Name, string NameEn)> Doctors)>> SpecialtiesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var deps = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).Select(d => new { d.ID, d.Name, d.NameEn }).ToListAsync(ct);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.DepartmentId != null).OrderBy(p => p.Name)
				.Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId }).ToListAsync(ct);
			return deps.Select(d => (d.ID, d.Name, d.NameEn ?? d.Name, (IReadOnlyList<(int, string, string)>)docs.Where(p => p.DepartmentId == d.ID).Select(p => (p.ID, p.Name, p.NameEn)).ToList())).ToList();
		}
	}
}
