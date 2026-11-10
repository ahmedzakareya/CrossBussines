using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H85 — antenatal care. The due date and the gestational age come from the dates (Naegele: LMP + 280 days,
	// or the ultrasound's due date when there is one); each visit's warnings come from its measures; the list shows every
	// active pregnancy with its weeks, its due date, its last warnings and whether the next visit was missed.
	// ==========================================================================================
	public sealed class HospitalPregnancyInput
	{
		public int PatientId { get; set; }
		public DateTime? Lmp { get; set; }
		public DateTime? UltrasoundEdd { get; set; }
		public int Gravida { get; set; }
		public int Para { get; set; }
		public int Abortions { get; set; }
		public string? Risks { get; set; }
	}

	public sealed class HospitalAntenatalVisitInput
	{
		public int PregnancyId { get; set; }
		public DateTime? VisitDate { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal? FundalHeightCm { get; set; }
		public int? FetalHeartRate { get; set; }
		public string? UrineProtein { get; set; }
		public string? Note { get; set; }
		public DateTime? NextVisitOn { get; set; }
	}

	public sealed class HospitalPregnancyEndInput
	{
		public string? Status { get; set; }
		public int? BirthId { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalAntenatalWarning
	{
		public const string Hypertension = "Hypertension";
		public const string SevereHypertension = "SevereHypertension";
		public const string PreEclampsia = "PreEclampsia";
		public const string FetalHeart = "FetalHeart";
		public const string FundalHeight = "FundalHeight";
	}

	public sealed record HospitalPregnancyRow(HospitalPregnancy Pregnancy, string Name, string NameEn, string Mrn, string? Phone, DateTime Edd, int GaDays,
		HospitalAntenatalVisit? LastVisit, IReadOnlyList<string> Warnings, bool VisitMissed);
	public sealed record HospitalPregnancyPage(HospitalPregnancy Pregnancy, string Name, string NameEn, string Mrn, string? Photo, string? BloodGroup, DateTime Edd, int GaDays,
		IReadOnlyList<(HospitalAntenatalVisit Visit, int GaDays, IReadOnlyList<string> Warnings)> Visits, IReadOnlyList<(int Id, string No, DateTime BornAt)> Births,
		IReadOnlyDictionary<int, string> People);

	public static class HospitalAntenatalRules
	{
		/// The due date: the ultrasound's when there is one, else Naegele's rule (LMP + 280 days).
		public static DateTime Edd(DateTime? lmp, DateTime? ultrasoundEdd) => (ultrasoundEdd ?? lmp!.Value.AddDays(280)).Date;

		/// Gestational age in days on a date, counted back from the due date (so an ultrasound date corrects it too).
		public static int GaDays(DateTime edd, DateTime on) => (on.Date - edd.AddDays(-280)).Days;

		public static string Ga(int days) => $"{days / 7}+{days % 7}";

		public static int Trimester(int gaDays) => gaDays < 14 * 7 ? 1 : gaDays < 28 * 7 ? 2 : 3;

		public static string? PregnancyRefusal(HospitalPregnancyInput i, string sex, DateTime today)
		{
			if (sex != HospitalPatientSex.Female) return "A pregnancy is opened on a female patient's file";
			if (i.Lmp == null && i.UltrasoundEdd == null) return "Write the last menstrual period or the ultrasound due date";
			if (i.Lmp != null && (i.Lmp.Value.Date > today.Date || i.Lmp.Value.Date < today.Date.AddDays(-310))) return "The last menstrual period must be within the last 44 weeks";
			if (i.UltrasoundEdd != null && (i.UltrasoundEdd.Value.Date < today.Date.AddDays(-30) || i.UltrasoundEdd.Value.Date > today.Date.AddDays(290))) return "The ultrasound due date is out of range";
			if (i.Gravida is < 1 or > 30 || i.Para < 0 || i.Abortions < 0 || i.Para + i.Abortions >= i.Gravida) return "Gravida counts this pregnancy: births and losses before it are fewer";
			return null;
		}

		public static string? VisitRefusal(HospitalAntenatalVisitInput i, DateTime edd, DateTime today)
		{
			var d = (i.VisitDate ?? today).Date;
			if (d > today.Date) return "The date is in the future";
			if (d < edd.AddDays(-280)) return "The visit cannot be before the pregnancy";
			if ((i.BpSystolic == null) != (i.BpDiastolic == null)) return "Write both blood pressure numbers";
			if (i.BpSystolic is int s && i.BpDiastolic is int di && (s is < 50 or > 260 || di is < 30 or > 160 || di >= s)) return "The blood pressure is not plausible";
			if (i.FetalHeartRate is < 50 or > 240) return "The fetal heart rate is not plausible";
			if (i.FundalHeightCm is < 5 or > 50) return "The fundal height is not plausible";
			if (i.WeightKg is < 25 or > 250) return "The weight is not plausible";
			if (!string.IsNullOrEmpty(i.UrineProtein) && !HospitalUrineProtein.All.Contains(i.UrineProtein)) return "Choose the urine protein";
			if (i.NextVisitOn != null && i.NextVisitOn.Value.Date <= d) return "The next visit comes after this one";
			bool any = i.BpSystolic != null || i.WeightKg != null || i.FundalHeightCm != null || i.FetalHeartRate != null || !string.IsNullOrEmpty(i.UrineProtein) || !string.IsNullOrWhiteSpace(i.Note);
			return any ? null : "Write at least one measure or a note";
		}

		/// The warnings of a visit, from its measures and the gestational age that day.
		public static IReadOnlyList<string> Warnings(HospitalAntenatalVisit v, int gaDays)
		{
			var w = new List<string>();
			bool severe = v.BpSystolic >= 160 || v.BpDiastolic >= 110;
			bool high = v.BpSystolic >= 140 || v.BpDiastolic >= 90;
			if (severe) w.Add(HospitalAntenatalWarning.SevereHypertension);
			else if (high) w.Add(HospitalAntenatalWarning.Hypertension);
			// pre-eclampsia: new hypertension after 20 weeks with significant proteinuria
			if (high && gaDays >= 20 * 7 && HospitalUrineProtein.Significant(v.UrineProtein)) w.Add(HospitalAntenatalWarning.PreEclampsia);
			if (v.FetalHeartRate is int f && (f < 110 || f > 160)) w.Add(HospitalAntenatalWarning.FetalHeart);
			// from 24 weeks the fundal height in cm is about the weeks; more than 3 cm off is worth a scan
			if (v.FundalHeightCm is decimal fh && gaDays >= 24 * 7 && Math.Abs(fh - gaDays / 7) > 3) w.Add(HospitalAntenatalWarning.FundalHeight);
			return w;
		}

		public static string? EndRefusal(HospitalPregnancy p, string? status, string? note)
		{
			if (p.Status != HospitalPregnancyStatus.Active) return "This pregnancy is already closed";
			if (status is not (HospitalPregnancyStatus.Delivered or HospitalPregnancyStatus.Loss)) return "Choose delivered or lost";
			if (status == HospitalPregnancyStatus.Loss && string.IsNullOrWhiteSpace(note)) return "Write what happened";
			return null;
		}
	}

	public sealed class HospitalAntenatalService
	{
		public const string NoTables = "Antenatal care is not installed yet (hospital_h85_antenatal.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalAntenatalService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalPregnancy> Pregnancies(BusinessContext ctx) => _db.Set<HospitalPregnancy>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalAntenatalVisit> Visits(BusinessContext ctx) => _db.Set<HospitalAntenatalVisit>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<bool> ClinicalAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);

		public async Task<HospitalResult> OpenAsync(BusinessContext ctx, HospitalPregnancyInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await ClinicalAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == input.PatientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.Sex }).FirstOrDefaultAsync(ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (HospitalAntenatalRules.PregnancyRefusal(input, p.Sex, Today(nowUtc)) is string why) return HospitalResult.Fail(why);
			try
			{
				if (await Pregnancies(ctx).AnyAsync(x => x.PatientId == p.ID && x.Status == HospitalPregnancyStatus.Active, ct)) return HospitalResult.Fail("This patient already has an active pregnancy");
				var nos = await Pregnancies(ctx).Select(x => x.PregnancyNo).ToListAsync(ct);
				var row = new HospitalPregnancy
				{
					CompanyId = ctx.CompanyId, PregnancyNo = HospitalDentalPracticeRules.NextNo(nos, "PG-", 6), PatientId = p.ID, Lmp = input.Lmp?.Date, UltrasoundEdd = input.UltrasoundEdd?.Date,
					Gravida = input.Gravida, Para = input.Para, Abortions = input.Abortions, Risks = Clip(input.Risks, 500), Status = HospitalPregnancyStatus.Active,
					CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AddVisitAsync(BusinessContext ctx, HospitalAntenatalVisitInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await ClinicalAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await Pregnancies(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PregnancyId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (p.Status != HospitalPregnancyStatus.Active) return HospitalResult.Fail("This pregnancy is already closed");
				var today = Today(nowUtc);
				if (HospitalAntenatalRules.VisitRefusal(input, HospitalAntenatalRules.Edd(p.Lmp, p.UltrasoundEdd), today) is string why) return HospitalResult.Fail(why);
				var row = new HospitalAntenatalVisit
				{
					CompanyId = ctx.CompanyId, PregnancyId = p.ID, PatientId = p.PatientId, VisitDate = (input.VisitDate ?? today).Date, BpSystolic = input.BpSystolic, BpDiastolic = input.BpDiastolic,
					WeightKg = input.WeightKg, FundalHeightCm = input.FundalHeightCm, FetalHeartRate = input.FetalHeartRate, UrineProtein = string.IsNullOrEmpty(input.UrineProtein) ? null : input.UrineProtein,
					Note = Clip(input.Note, 500), NextVisitOn = input.NextVisitOn?.Date, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Closes a pregnancy as delivered (optionally pointing at the mother's H58 birth) or as a loss with what happened.
		public async Task<HospitalResult> EndAsync(BusinessContext ctx, int id, HospitalPregnancyEndInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await Pregnancies(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (HospitalAntenatalRules.EndRefusal(p, input.Status, input.Note) is string why) return HospitalResult.Fail(why);
				if (input.BirthId is int bid && !await _db.Set<HospitalBirth>().AnyAsync(b => b.ID == bid && b.CompanyId == ctx.CompanyId && b.MotherPatientId == p.PatientId && b.VoidedAt == null, ct))
					return HospitalResult.Fail("That birth is not this mother's");
				p.Status = input.Status!; p.BirthId = input.Status == HospitalPregnancyStatus.Delivered ? input.BirthId : null; p.EndedAt = nowUtc; p.EndNote = Clip(input.Note, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The active pregnancies: weeks, due date, the last visit's warnings and a missed next visit; searched and paged.
		public async Task<(IReadOnlyList<HospitalPregnancyRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Today(nowUtc);
			try
			{
				var query = from x in Pregnancies(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID
							where x.Status == HospitalPregnancyStatus.Active
							select new { x, p.Name, p.NameEn, p.Mrn, p.Phone };
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(r => r.x.PregnancyNo.Contains(s) || r.Name.Contains(s) || (r.NameEn != null && r.NameEn.Contains(s)) || r.Mrn.Contains(s) || (r.Phone != null && r.Phone.Contains(s))); }
				var all = await query.ToListAsync(ct);
				var ids = all.Select(r => r.x.ID).ToList();
				var visits = await Visits(ctx).AsNoTracking().Where(v => ids.Contains(v.PregnancyId)).ToListAsync(ct);
				var rows = all.Select(r =>
				{
					var edd = HospitalAntenatalRules.Edd(r.x.Lmp, r.x.UltrasoundEdd);
					var last = visits.Where(v => v.PregnancyId == r.x.ID).OrderByDescending(v => v.VisitDate).ThenByDescending(v => v.ID).FirstOrDefault();
					var warn = last == null ? Array.Empty<string>() : HospitalAntenatalRules.Warnings(last, HospitalAntenatalRules.GaDays(edd, last.VisitDate));
					bool missed = last?.NextVisitOn is DateTime n && n < today;
					return new HospitalPregnancyRow(r.x, r.Name, r.NameEn ?? "", r.Mrn, r.Phone, edd, HospitalAntenatalRules.GaDays(edd, today), last, warn, missed);
				});
				rows = filter switch
				{
					"warning" => rows.Where(r => r.Warnings.Count > 0),
					"missed" => rows.Where(r => r.VisitMissed),
					"term" => rows.Where(r => r.GaDays >= 37 * 7),
					_ => rows,
				};
				var list = rows.OrderByDescending(r => r.Warnings.Count > 0).ThenByDescending(r => r.VisitMissed).ThenBy(r => r.Edd).ToList();
				pageSize = Math.Clamp(pageSize, 5, 100); page = Math.Max(1, page);
				return (list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalPregnancyRow>(), 0, false); }
		}

		public async Task<HospitalPregnancyPage?> PageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var x = await Pregnancies(ctx).AsNoTracking().FirstOrDefaultAsync(r => r.ID == id, ct);
				if (x == null) return null;
				var p = await _db.HospitalPatients.AsNoTracking().Where(r => r.ID == x.PatientId).Select(r => new { r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.BloodGroup }).FirstAsync(ct);
				var edd = HospitalAntenatalRules.Edd(x.Lmp, x.UltrasoundEdd);
				var vs = await Visits(ctx).AsNoTracking().Where(v => v.PregnancyId == id).OrderBy(v => v.VisitDate).ThenBy(v => v.ID).ToListAsync(ct);
				var births = (await _db.Set<HospitalBirth>().AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.MotherPatientId == x.PatientId && b.VoidedAt == null)
					.OrderByDescending(b => b.BornAt).Select(b => new { b.ID, b.BirthNo, b.BornAt }).ToListAsync(ct)).Select(b => (b.ID, b.BirthNo, b.BornAt)).ToList();
				var eids = vs.Where(v => v.CreatedByEmployeeId != null).Select(v => v.CreatedByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalPregnancyPage(x, p.Name, p.NameEn ?? "", p.Mrn, p.PhotoPath, p.BloodGroup, edd, HospitalAntenatalRules.GaDays(edd, Today(nowUtc)),
					vs.Select(v => { int ga = HospitalAntenatalRules.GaDays(edd, v.VisitDate); return (v, ga, HospitalAntenatalRules.Warnings(v, ga)); }).ToList(), births, people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
