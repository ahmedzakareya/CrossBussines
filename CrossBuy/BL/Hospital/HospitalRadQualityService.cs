using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalRadCategoryInput { public string? System { get; set; } public string? Code { get; set; } public string? Recommendation { get; set; } public DateTime? DueDate { get; set; } }
	public sealed class HospitalRadFollowUpCloseInput { public bool Done { get; set; } public string? AccessionNo { get; set; } public string? Note { get; set; } }
	public sealed class HospitalRadDoseInput { public string? Region { get; set; } public decimal? CtdiVol { get; set; } public decimal? Dlp { get; set; } public decimal? Dap { get; set; } }

	public sealed record HospitalRadQualityPanel(bool Ready, HospitalRadFollowUp? FollowUp, HospitalRadDose? Dose, decimal CumulativeMsv12, int Exams12, string? AnsweredBy);
	public sealed record HospitalRadFollowUpRow(HospitalRadFollowUp F, string AccessionNo, string Exam, string ExamEn, string PatientName, string? PatientNameEn, string? Photo, string? Mrn, bool Overdue);
	public sealed record HospitalRadFollowUpPage(bool Ready, IReadOnlyList<HospitalRadFollowUpRow> Rows, int Total, int Open, int Overdue);

	// ==========================================================================================
	// HOSPITAL H243 — the structured category and follow-up of a study (the radiologist, `rad-report`), its radiation
	// dose (the radiographer, `rad`), and the follow-up worklist (anyone in radiology). The rules are
	// HospitalRadQualityRules'.
	// ==========================================================================================
	public sealed class HospitalRadQualityService
	{
		public const string NoTables = "Radiology follow-up and dose are not installed yet (hospital_h243_rad_quality.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRadQualityService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalRadOrder> Orders(BusinessContext ctx) => _db.Set<HospitalRadOrder>().Where(o => o.CompanyId == ctx.CompanyId);

		public async Task<HospitalRadQualityPanel> PanelAsync(BusinessContext ctx, int orderId, int patientId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var f = await _db.Set<HospitalRadFollowUp>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.OrderId == orderId, ct);
				var d = await _db.Set<HospitalRadDose>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.OrderId == orderId, ct);
				var since = nowUtc.AddMonths(-12);
				var year = await _db.Set<HospitalRadDose>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == patientId && x.At >= since).Select(x => x.EffectiveMsv).ToListAsync(ct);
				string? by = f?.AnsweredByOrderId is int a ? await Orders(ctx).Where(o => o.ID == a).Select(o => o.AccessionNo).FirstOrDefaultAsync(ct) : null;
				return new HospitalRadQualityPanel(true, f, d, year.Sum(x => x ?? 0), year.Count, by);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalRadQualityPanel(false, null, null, 0, 0, null); }
		}

		public async Task<HospitalResult> CategorizeAsync(BusinessContext ctx, int orderId, HospitalRadCategoryInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct)) return HospitalResult.Fail("forbidden");
			var o = await Orders(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId, ct);
			if (o == null) return HospitalResult.Fail("not_found");
			if (o.Status is not (HospitalRadStatus.Reporting or HospitalRadStatus.Signed)) return HospitalResult.Fail("The category is given once the scan is done");
			var c = HospitalRadQualityRules.Find(i.System, i.Code);
			if (c == null) return HospitalResult.Fail("Choose the category");
			var today = Ward(nowUtc).Date;
			var due = c.FollowUpMonths == null ? null : i.DueDate?.Date ?? HospitalRadQualityRules.DueDate(c, today);
			if (due is DateTime dd && (dd < today || dd > today.AddYears(3))) return HospitalResult.Fail("The follow-up is due within the next three years");
			try
			{
				var f = await _db.Set<HospitalRadFollowUp>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.OrderId == o.ID, ct);
				if (f != null && f.Status != HospitalRadFollowUpStatus.Open) return HospitalResult.Fail("This follow-up is already closed");
				if (f == null) _db.Add(f = new HospitalRadFollowUp { CompanyId = ctx.CompanyId, OrderId = o.ID, PatientId = o.PatientId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId });
				f.System = c.System; f.Category = c.Code; f.DueDate = due;
				f.Recommendation = string.IsNullOrWhiteSpace(i.Recommendation) ? c.Recommendation : i.Recommendation.Trim()[..Math.Min(300, i.Recommendation.Trim().Length)];
				// a category that asks for nothing records the category and closes at once
				f.Status = due == null ? HospitalRadFollowUpStatus.Done : HospitalRadFollowUpStatus.Open; f.ClosedAt = due == null ? nowUtc : null;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(f.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CloseFollowUpAsync(BusinessContext ctx, int followUpId, HospitalRadFollowUpCloseInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Rad, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var f = await _db.Set<HospitalRadFollowUp>().FirstOrDefaultAsync(x => x.ID == followUpId && x.CompanyId == ctx.CompanyId, ct);
				if (f == null) return HospitalResult.Fail("not_found");
				if (f.Status != HospitalRadFollowUpStatus.Open) return HospitalResult.Fail("This follow-up is already closed");
				if (i.Done)
				{
					var acc = (i.AccessionNo ?? "").Trim();
					var later = await Orders(ctx).AsNoTracking().FirstOrDefaultAsync(o => o.AccessionNo == acc, ct);
					if (later == null || later.PatientId != f.PatientId || later.ID == f.OrderId) return HospitalResult.Fail("Enter the accession number of the patient's later study");
					f.AnsweredByOrderId = later.ID; f.Status = HospitalRadFollowUpStatus.Done;
				}
				else
				{
					if (string.IsNullOrWhiteSpace(i.Note)) return HospitalResult.Fail("Write why the follow-up is closed");
					f.Status = HospitalRadFollowUpStatus.Closed;
				}
				if (!string.IsNullOrWhiteSpace(i.Note)) f.Note = i.Note.Trim()[..Math.Min(300, i.Note.Trim().Length)];
				f.ClosedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(f.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RecordDoseAsync(BusinessContext ctx, int orderId, HospitalRadDoseInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Rad, null, ct)) return HospitalResult.Fail("forbidden");
			var o = await Orders(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId, ct);
			if (o == null) return HospitalResult.Fail("not_found");
			if (o.ScanStartedAt == null) return HospitalResult.Fail("The dose is recorded once the scan has started");
			if (HospitalRadQualityRules.DoseRefusal(o.Modality, i.Region, i.CtdiVol, i.Dlp, i.Dap) is string why) return HospitalResult.Fail(why);
			try
			{
				var d = await _db.Set<HospitalRadDose>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.OrderId == o.ID, ct);
				if (d == null) _db.Add(d = new HospitalRadDose { CompanyId = ctx.CompanyId, OrderId = o.ID, PatientId = o.PatientId });
				d.Region = i.Region!; d.CtdiVol = i.CtdiVol; d.Dlp = i.Dlp; d.Dap = i.Dap;
				d.EffectiveMsv = HospitalRadQualityRules.EffectiveMsv(i.Region!, i.Dlp); d.AboveReference = HospitalRadQualityRules.AboveReference(i.Region!, i.CtdiVol, i.Dlp);
				d.At = o.ScanStartedAt.Value; d.ByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalRadFollowUpPage> FollowUpsAsync(BusinessContext ctx, string? q, string? view, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Ward(nowUtc).Date;
			try
			{
				var baseQ = from f in _db.Set<HospitalRadFollowUp>().AsNoTracking()
							join o in Orders(ctx).AsNoTracking() on f.OrderId equals o.ID
							join p in _db.HospitalPatients.AsNoTracking() on f.PatientId equals p.ID
							where f.CompanyId == ctx.CompanyId && f.DueDate != null
							select new { f, o.AccessionNo, o.Name, o.NameEn, PName = p.Name, PNameEn = p.NameEn, p.PhotoPath, p.Mrn };
				int open = await baseQ.CountAsync(x => x.f.Status == HospitalRadFollowUpStatus.Open, ct);
				int overdue = await baseQ.CountAsync(x => x.f.Status == HospitalRadFollowUpStatus.Open && x.f.DueDate < today, ct);
				var qq = view switch
				{
					"overdue" => baseQ.Where(x => x.f.Status == HospitalRadFollowUpStatus.Open && x.f.DueDate < today),
					"closed" => baseQ.Where(x => x.f.Status != HospitalRadFollowUpStatus.Open),
					_ => baseQ.Where(x => x.f.Status == HospitalRadFollowUpStatus.Open),
				};
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					qq = qq.Where(x => x.PName.Contains(t) || (x.PNameEn ?? "").Contains(t) || x.Mrn.Contains(t) || x.AccessionNo.Contains(t) || x.f.System.Contains(t) || x.Name.Contains(t) || x.NameEn.Contains(t));
				}
				int total = await qq.CountAsync(ct);
				var rows = await qq.OrderBy(x => x.f.DueDate).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				return new HospitalRadFollowUpPage(true, rows.Select(x => new HospitalRadFollowUpRow(x.f, x.AccessionNo, x.Name, x.NameEn, x.PName, x.PNameEn, x.PhotoPath, x.Mrn, HospitalRadQualityRules.Overdue(x.f, today))).ToList(), total, open, overdue);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalRadFollowUpPage(false, Array.Empty<HospitalRadFollowUpRow>(), 0, 0, 0); }
		}
	}
}
