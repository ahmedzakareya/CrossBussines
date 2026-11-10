using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H55 — the discharge summary (HospitalDischargeSummary). The stay's facts are read each time from the stay
	// itself; the doctor writes the rest and signs (`consult`), after which nothing changes. Anyone who reads the
	// hospital may read it (`read`), and it prints through Report Studio (Hospital.DischargeSummary).
	// ==========================================================================================

	public sealed class HospitalDischargeInput
	{
		public string? FinalDiagnosis { get; set; } public string? Course { get; set; } public string? Procedures { get; set; } public string? Condition { get; set; }
		public string? Medications { get; set; } public string? Instructions { get; set; } public DateTime? FollowUpOn { get; set; } public string? FollowUpWith { get; set; }
	}

	public sealed record HospitalDischargeOperation(string Name, string NameEn, DateTime Date, string? Surgeon, string? SurgeonEn);

	/// The stay as the summary shows it, and the summary when one has been started.
	public sealed record HospitalDischargeView(HospitalAdmission Admission, HospitalPatient Patient, string? Ward, string? WardEn, string? Room, string? Bed,
		string? Attending, string? AttendingEn, decimal StayDays, IReadOnlyList<HospitalDischargeOperation> Operations, HospitalDischargeSummary? Summary,
		string? SignedBy, string? SignedByEn);

	public static class HospitalDischargeRules
	{
		/// What signing needs: the final diagnosis, the condition at discharge, and a follow-up date not in the past.
		public static string? SignRefusal(HospitalDischargeInput s, DateTime todayWard)
		{
			if (string.IsNullOrWhiteSpace(s.FinalDiagnosis)) return "Write the final diagnosis before signing";
			if (!HospitalDischargeCondition.All.Contains(s.Condition ?? "")) return "Choose the condition at discharge before signing";
			if (s.FollowUpOn is { } f && f.Date < todayWard.Date) return "The follow-up date is in the past";
			return null;
		}

		/// What any save refuses: an unknown condition, a text longer than its column.
		public static string? SaveRefusal(HospitalDischargeInput s)
		{
			if (s.Condition != null && s.Condition != "" && !HospitalDischargeCondition.All.Contains(s.Condition)) return "Choose the condition at discharge";
			if ((s.FinalDiagnosis?.Length ?? 0) > 1000 || (s.Course?.Length ?? 0) > 4000 || (s.Procedures?.Length ?? 0) > 2000 || (s.Medications?.Length ?? 0) > 2000
				|| (s.Instructions?.Length ?? 0) > 2000 || (s.FollowUpWith?.Length ?? 0) > 200) return "A text is too long";
			return null;
		}

		/// Length of stay in days (to now while the patient is still in).
		public static decimal StayDays(DateTime admittedUtc, DateTime? dischargedUtc, DateTime nowUtc) =>
			Math.Round((decimal)((dischargedUtc ?? nowUtc) - admittedUtc).TotalDays, 1);

		/// The operations of the stay as the summary's "procedures" starts from (the doctor may edit it).
		public static string ProceduresText(IEnumerable<HospitalDischargeOperation> ops, bool arabic) =>
			string.Join(Environment.NewLine, ops.Select(o => o.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " — " + (arabic ? o.Name : (string.IsNullOrWhiteSpace(o.NameEn) ? o.Name : o.NameEn))));
	}

	public sealed class HospitalDischargeService
	{
		public const string NoTables = "The discharge summary is not installed yet (hospital_h55_discharge_summary.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDischargeService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalDischargeSummary> Summaries(BusinessContext ctx) => _db.Set<HospitalDischargeSummary>().Where(s => s.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// The stay and its summary; null when the admission is not this company's. `Ready` false = no H55 table.
		public async Task<(HospitalDischargeView? View, bool Ready)> GetAsync(BusinessContext ctx, int admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return (null, true);
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == a.PatientId && x.CompanyId == ctx.CompanyId, ct);
			// the last bed of the stay: where the patient was discharged from (or is now)
			var stay = await _db.HospitalBedStays.AsNoTracking().Where(s => s.AdmissionId == a.ID && s.CompanyId == ctx.CompanyId).OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
			string? ward = null, wardEn = null, room = null, bed = null;
			if (stay != null)
			{
				var r = await _db.HospitalRooms.AsNoTracking().FirstOrDefaultAsync(x => x.ID == stay.RoomId && x.CompanyId == ctx.CompanyId, ct);
				room = r?.Number;
				bed = await _db.HospitalBeds.AsNoTracking().Where(x => x.ID == stay.BedId && x.CompanyId == ctx.CompanyId).Select(x => x.Code).FirstOrDefaultAsync(ct);
				if (r?.WardId is int wid && await _db.HospitalWards.AsNoTracking().FirstOrDefaultAsync(w => w.ID == wid && w.CompanyId == ctx.CompanyId, ct) is { } w) { ward = w.Name; wardEn = w.NameEn ?? w.Name; }
			}
			var ops = await (from s in _db.Set<HospitalSurgery>().AsNoTracking()
							 join d in _db.HospitalPractitioners.AsNoTracking() on s.SurgeonId equals d.ID into dj from d in dj.DefaultIfEmpty()
							 where s.CompanyId == ctx.CompanyId && s.AdmissionId == a.ID && s.Status == HospitalSurgeryStatus.Done
							 orderby s.CaseDate
							 select new HospitalDischargeOperation(s.Name, s.NameEn, s.CaseDate, d == null ? null : d.Name, d == null ? null : d.NameEn)).ToListAsync(ct);
			var names = await NamesAsync(ctx, new[] { a.AttendingEmployeeId }, ct);
			try
			{
				var sum = await Summaries(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.AdmissionId == a.ID, ct);
				var signer = sum?.SignedByEmployeeId is int sid ? (await NamesAsync(ctx, new int?[] { sid }, ct)).GetValueOrDefault(sid) : default;
				var att = a.AttendingEmployeeId is int aid ? names.GetValueOrDefault(aid) : default;
				return (new HospitalDischargeView(a, p, ward, wardEn, room, bed, att.Name, att.NameEn, HospitalDischargeRules.StayDays(a.AdmittedAt, a.DischargedAt, nowUtc), ops, sum, signer.Name, signer.NameEn), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return (new HospitalDischargeView(a, p, ward, wardEn, room, bed, null, null, HospitalDischargeRules.StayDays(a.AdmittedAt, a.DischargedAt, nowUtc), ops, null, null, null), false);
			}
		}

		private async Task<Dictionary<int, (string? Name, string? NameEn)>> NamesAsync(BusinessContext ctx, IEnumerable<int?> ids, CancellationToken ct)
		{
			var list = ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			return (await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct))
				.ToDictionary(e => e.ID, e => ((string?)e.FullName, (string?)(e.FullNameEn ?? e.FullName)));
		}

		/// Saves the draft (creating it the first time). A signed summary is final.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int admissionId, HospitalDischargeInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDischargeRules.SaveRefusal(input) is string why) return HospitalResult.Fail(why);
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			try
			{
				var s = await Summaries(ctx).FirstOrDefaultAsync(x => x.AdmissionId == admissionId, ct);
				if (s?.Status == HospitalDischargeSummaryStatus.Signed) return HospitalResult.Fail("This summary is signed and cannot change");
				if (s == null) { s = new HospitalDischargeSummary { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId }; _db.Add(s); }
				s.FinalDiagnosis = Clip(input.FinalDiagnosis, 1000); s.Course = Clip(input.Course, 4000); s.Procedures = Clip(input.Procedures, 2000);
				s.Condition = string.IsNullOrWhiteSpace(input.Condition) ? null : input.Condition; s.Medications = Clip(input.Medications, 2000);
				s.Instructions = Clip(input.Instructions, 2000); s.FollowUpOn = input.FollowUpOn?.Date; s.FollowUpWith = Clip(input.FollowUpWith, 200);
				s.UpdatedAt = nowUtc; s.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Saves what is on the screen and signs it: from then on the summary is final.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int admissionId, HospitalDischargeInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDischargeRules.SignRefusal(input, HospitalNursingService.UtcToWard(nowUtc)) is string why) return HospitalResult.Fail(why);
			var saved = await SaveAsync(ctx, admissionId, input, employeeId, nowUtc, ct);
			if (!saved.Ok) return saved;
			int n = await Summaries(ctx).Where(s => s.ID == saved.Id && s.Status == HospitalDischargeSummaryStatus.Draft)
				.ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, HospitalDischargeSummaryStatus.Signed).SetProperty(s => s.SignedAt, nowUtc).SetProperty(s => s.SignedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(saved.Id!.Value) : HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
		}
	}
}
