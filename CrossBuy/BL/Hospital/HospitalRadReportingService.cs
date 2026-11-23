using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalRadTemplateInput
	{
		public int? Id { get; set; } public string? Code { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public string? Modality { get; set; }
		public List<HospitalRadSectionInput> Sections { get; set; } = new(); public bool Active { get; set; } = true;
	}
	public sealed class HospitalRadSectionInput { public string? Heading { get; set; } public string? HeadingEn { get; set; } public bool Required { get; set; } }
	public sealed class HospitalRadPeerInput { public int Score { get; set; } public bool Significant { get; set; } public string? Note { get; set; } }

	public sealed record HospitalRadTemplatePage(bool Ready, IReadOnlyList<HospitalRadTemplate> Rows, int Total);
	public sealed record HospitalRadPeerRow(HospitalRadOrder Order, string PatientName, string? PatientNameEn, string? Photo, string? Mrn, HospitalFace? Radiologist, HospitalRadPeerReview? Mine);
	public sealed record HospitalRadPeerStat(int EmployeeId, HospitalFace? Face, int Reviewed, decimal Discrepancy, decimal Significant);
	public sealed record HospitalRadPeerPage(bool Ready, IReadOnlyList<HospitalRadPeerRow> Rows, int Total, IReadOnlyList<HospitalRadPeerStat> Stats);
	public sealed record HospitalRadSkeleton(int TemplateId, string Arabic, string English);

	// ==========================================================================================
	// HOSPITAL H259 — report templates (the administrator, `manage`), applying one to a study and the peer-review
	// worklist (radiologists, `rad-report`). The sign check of required sections is in HospitalRadiologyService.SignAsync.
	// ==========================================================================================
	public sealed class HospitalRadReportingService
	{
		public const string NoTables = "Structured reporting is not installed yet (hospital_h259_rad_reporting.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRadReportingService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		public async Task<HospitalRadTemplatePage?> TemplatesAsync(BusinessContext ctx, string? q, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			try
			{
				var query = _db.Set<HospitalRadTemplate>().AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(t => t.Code.Contains(s) || t.Name.Contains(s) || t.NameEn.Contains(s)); }
				int total = await query.CountAsync(ct);
				var rows = await query.OrderByDescending(t => t.Active).ThenBy(t => t.Code).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				return new HospitalRadTemplatePage(true, rows, total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalRadTemplatePage(false, Array.Empty<HospitalRadTemplate>(), 0); }
		}

		public async Task<HospitalResult> SaveTemplateAsync(BusinessContext ctx, HospitalRadTemplateInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var sections = i.Sections.Where(s => !string.IsNullOrWhiteSpace(s.Heading) || !string.IsNullOrWhiteSpace(s.HeadingEn))
				.Select(s => new HospitalRadSection((s.Heading ?? "").Trim(), (s.HeadingEn ?? "").Trim(), s.Required)).ToList();
			if (sections.Any(s => s.Heading.Length == 0 || s.HeadingEn.Length == 0)) return HospitalResult.Fail("Each section has an Arabic and an English heading");
			var modality = string.IsNullOrWhiteSpace(i.Modality) ? null : i.Modality;
			if (HospitalRadReportingRules.Refusal(i.Code, i.Name, i.NameEn, modality, sections) is string why) return HospitalResult.Fail(why);
			var code = i.Code!.Trim().ToUpperInvariant();
			try
			{
				if (await _db.Set<HospitalRadTemplate>().AnyAsync(t => t.CompanyId == ctx.CompanyId && t.Code == code && t.ID != (i.Id ?? 0), ct)) return HospitalResult.Fail("Another template uses this code");
				HospitalRadTemplate? t = i.Id is int id ? await _db.Set<HospitalRadTemplate>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct) : null;
				if (i.Id != null && t == null) return HospitalResult.Fail("not_found");
				if (t == null) _db.Add(t = new HospitalRadTemplate { CompanyId = ctx.CompanyId });
				t.Code = code; t.Name = i.Name!.Trim(); t.NameEn = i.NameEn!.Trim(); t.Modality = modality; t.Sections = HospitalRadReportingRules.Serialize(sections); t.Active = i.Active; t.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(t.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Adds the standard templates the company does not have yet (by code). Returns how many were added.
		public async Task<HospitalResult> SeedStandardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var have = await _db.Set<HospitalRadTemplate>().Where(t => t.CompanyId == ctx.CompanyId).Select(t => t.Code).ToListAsync(ct);
				int n = 0;
				foreach (var s in HospitalRadReportingRules.Standard.Where(s => !have.Contains(s.Code)))
				{
					_db.Add(new HospitalRadTemplate { CompanyId = ctx.CompanyId, Code = s.Code, Name = s.Name, NameEn = s.NameEn, Modality = s.Modality, Sections = HospitalRadReportingRules.Serialize(s.Sections), Active = true, UpdatedAt = nowUtc });
					n++;
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(n);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The active templates a study of this modality can use (for the report editor). Empty when not installed.
		public async Task<IReadOnlyList<HospitalRadTemplate>> ForModalityAsync(BusinessContext ctx, string modality, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalRadTemplate>().AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && t.Active && (t.Modality == null || t.Modality == modality)).OrderBy(t => t.Code).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalRadTemplate>(); }
		}

		public async Task<int?> UsedTemplateAsync(BusinessContext ctx, int orderId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalRadTemplateUse>().AsNoTracking().Where(u => u.CompanyId == ctx.CompanyId && u.OrderId == orderId).Select(u => (int?)u.TemplateId).FirstOrDefaultAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The radiologist applies a template to a study being reported: the study now uses it (its required sections
		/// must be written before signing), and the headings are returned for the editor.
		public async Task<(HospitalResult Result, HospitalRadSkeleton? Skeleton)> ApplyAsync(BusinessContext ctx, int orderId, int templateId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var o = await _db.HospitalRadOrders.AsNoTracking().FirstOrDefaultAsync(r => r.ID == orderId && r.CompanyId == ctx.CompanyId, ct);
			if (o == null) return (HospitalResult.Fail("not_found"), null);
			if (o.Status != HospitalRadStatus.Reporting) return (HospitalResult.Fail("A template is chosen while the report is written"), null);
			try
			{
				var t = await _db.Set<HospitalRadTemplate>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == templateId && x.CompanyId == ctx.CompanyId && x.Active, ct);
				if (t == null || (t.Modality != null && t.Modality != o.Modality)) return (HospitalResult.Fail("This template is not for this study"), null);
				var use = await _db.Set<HospitalRadTemplateUse>().FirstOrDefaultAsync(u => u.CompanyId == ctx.CompanyId && u.OrderId == orderId, ct);
				if (use == null) _db.Add(use = new HospitalRadTemplateUse { CompanyId = ctx.CompanyId, OrderId = orderId });
				use.TemplateId = t.ID; use.At = nowUtc; use.ByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				var s = HospitalRadReportingRules.Parse(t.Sections);
				return (HospitalResult.Success(t.ID), new HospitalRadSkeleton(t.ID, HospitalRadReportingRules.Skeleton(s, false), HospitalRadReportingRules.Skeleton(s, true)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return (HospitalResult.Fail(NoTables), null); }
		}

		/// The required sections the report leaves empty, when the study uses a template (empty otherwise).
		public static async Task<IReadOnlyList<HospitalRadSection>> MissingAsync(CrossDbContext db, int companyId, int orderId, string? findings, string? findingsEn, CancellationToken ct = default)
		{
			try
			{
				var t = await (from u in db.Set<HospitalRadTemplateUse>().AsNoTracking()
							   join x in db.Set<HospitalRadTemplate>().AsNoTracking() on u.TemplateId equals x.ID
							   where u.CompanyId == companyId && u.OrderId == orderId
							   select x.Sections).FirstOrDefaultAsync(ct);
				return t == null ? Array.Empty<HospitalRadSection>() : HospitalRadReportingRules.Missing(HospitalRadReportingRules.Parse(t), findings, findingsEn);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalRadSection>(); }
		}

		// ---------------- peer review ----------------

		/// The reviewer's queue: reports signed by OTHER radiologists in the last 90 days, in a stable random sample
		/// order for the day ("mine" = the ones I already scored), with each radiologist's rates over 12 months.
		public async Task<HospitalRadPeerPage?> PeerAsync(BusinessContext ctx, string? q, string? view, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct)) return null;
			int me = ctx.EmployeeId ?? 0;
			try
			{
				var since = nowUtc.AddDays(-90);
				var reviewedByMe = _db.Set<HospitalRadPeerReview>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.ReviewerEmployeeId == me).Select(r => r.OrderId);
				var query = from o in _db.HospitalRadOrders.AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							where o.CompanyId == ctx.CompanyId && o.Status == HospitalRadStatus.Signed && o.SignedByEmployeeId != null && o.SignedByEmployeeId != me && o.SignedAt >= since
							select new { o, p.Name, p.NameEn, p.PhotoPath, p.Mrn };
				query = view == "mine" ? query.Where(x => reviewedByMe.Contains(x.o.ID)) : query.Where(x => !reviewedByMe.Contains(x.o.ID));
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s) || x.o.AccessionNo.Contains(s) || x.o.Name.Contains(s) || x.o.NameEn.Contains(s)); }
				int total = await query.CountAsync(ct);
				var all = await query.Select(x => x.o.ID).ToListAsync(ct);
				var day = nowUtc.Date;
				var ids = (view == "mine" ? all.OrderByDescending(i => i) : all.OrderBy(i => HospitalRadReportingRules.SampleKey(i, day))).Skip((Math.Max(1, page) - 1) * size).Take(size).ToList();
				var rows = await query.Where(x => ids.Contains(x.o.ID)).ToListAsync(ct);
				var mine = await _db.Set<HospitalRadPeerReview>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.ReviewerEmployeeId == me && ids.Contains(r.OrderId)).ToDictionaryAsync(r => r.OrderId, ct);
				var yearAgo = nowUtc.AddYears(-1);
				var reviews = await _db.Set<HospitalRadPeerReview>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.At >= yearAgo).Select(r => new { r.RadiologistEmployeeId, r.Score, r.Significant }).ToListAsync(ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(r => r.o.SignedByEmployeeId).Concat(reviews.Select(r => (int?)r.RadiologistEmployeeId)), ct);
				var stats = reviews.GroupBy(r => r.RadiologistEmployeeId).Select(g =>
				{
					var (n, d, s) = HospitalRadReportingRules.Rates(g.Select(r => (r.Score, r.Significant)));
					return new HospitalRadPeerStat(g.Key, faces.GetValueOrDefault(g.Key), n, d, s);
				}).OrderByDescending(s => s.Discrepancy).ThenByDescending(s => s.Reviewed).ToList();
				var ordered = ids.Select(i => rows.First(r => r.o.ID == i)).Select(r => new HospitalRadPeerRow(r.o, r.Name, r.NameEn, r.PhotoPath, r.Mrn, faces.GetValueOrDefault(r.o.SignedByEmployeeId!.Value), mine.GetValueOrDefault(r.o.ID))).ToList();
				return new HospitalRadPeerPage(true, ordered, total, stats);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalRadPeerPage(false, Array.Empty<HospitalRadPeerRow>(), 0, Array.Empty<HospitalRadPeerStat>()); }
		}

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int orderId, HospitalRadPeerInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct)) return HospitalResult.Fail("forbidden");
			var o = await _db.HospitalRadOrders.AsNoTracking().FirstOrDefaultAsync(r => r.ID == orderId && r.CompanyId == ctx.CompanyId, ct);
			if (o == null) return HospitalResult.Fail("not_found");
			if (o.Status != HospitalRadStatus.Signed) return HospitalResult.Fail("This report is not signed");
			if (HospitalRadReportingRules.ReviewRefusal(i.Score, i.Significant, i.Note, o.SignedByEmployeeId, ctx.EmployeeId) is string why) return HospitalResult.Fail(why);
			try
			{
				var r = await _db.Set<HospitalRadPeerReview>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.OrderId == orderId && x.ReviewerEmployeeId == ctx.EmployeeId!.Value, ct);
				if (r == null) _db.Add(r = new HospitalRadPeerReview { CompanyId = ctx.CompanyId, OrderId = orderId, ReviewerEmployeeId = ctx.EmployeeId!.Value, RadiologistEmployeeId = o.SignedByEmployeeId!.Value });
				r.Score = i.Score; r.Significant = i.Significant; r.Note = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim()[..Math.Min(500, i.Note.Trim().Length)]; r.At = nowUtc;
				try { await _db.SaveChangesAsync(ct); }
				catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
