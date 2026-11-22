using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalDrgInput
	{
		public int? Id { get; set; }
		public string? Code { get; set; } public string? Name { get; set; } public string? NameEn { get; set; }
		public string? DxPrefixes { get; set; } public bool? Surgical { get; set; }
		public int? MinAge { get; set; } public int? MaxAge { get; set; }
		public decimal Weight { get; set; } public decimal? MeanLos { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalDrgAssignInput { public string? PrincipalDx { get; set; } public int? DrgId { get; set; } }

	public sealed record HospitalDrgStay(int AdmissionId, int PatientId, string Name, string? NameEn, string? Photo, string? Mrn, DateTime AdmittedAt, DateTime DischargedAt,
		int LosDays, string? Diagnosis, bool Surgical, HospitalDrgAssignment? Assignment, HospitalDrg? Drg);

	public sealed record HospitalDrgPage(bool Ready, IReadOnlyList<HospitalDrgStay> Stays, int StayTotal, int ToCode, decimal? Cmi, int Coded, int Ungroupable,
		IReadOnlyList<(HospitalDrg Drg, HospitalDrgCaseStat Stat)> Top, IReadOnlyList<HospitalDrg> Groups, int GroupTotal);

	// ==========================================================================================
	// HOSPITAL H241 — DRG coding of discharged stays (the last 120 days) and the facility's group table. The coder
	// (`bill`) codes a stay; the administrator (`manage`) keeps the groups. The case-mix index covers the coded stays
	// of the last 90 days.
	// ==========================================================================================
	public sealed class HospitalDrgService
	{
		public const string NoTables = "DRG grouping is not installed yet (hospital_h241_drg.sql).";
		public const int WindowDays = 120, CmiDays = 90;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDrgService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private static int Los(DateTime from, DateTime to) => Math.Max(1, (Ward(to).Date - Ward(from).Date).Days);

		public async Task<HospitalDrgPage?> PageAsync(BusinessContext ctx, string? q, string? view, int page, int size, string? gq, int gpage, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return null;
			var since = nowUtc.AddDays(-WindowDays);
			var stays = await (from a in _db.HospitalAdmissions.AsNoTracking()
							   join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							   where a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Discharged && a.DischargedAt != null && a.DischargedAt >= since
							   select new { a.ID, a.PatientId, p.Name, p.NameEn, p.PhotoPath, p.Mrn, a.AdmittedAt, Discharged = a.DischargedAt!.Value, a.Diagnosis }).ToListAsync(ct);
			var ids = stays.Select(s => (int?)s.ID).ToList();
			var operated = (await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.AdmissionId) && s.Status == HospitalSurgeryStatus.Done)
				.Select(s => s.AdmissionId!.Value).ToListAsync(ct)).ToHashSet();
			List<HospitalDrgAssignment> assigned; List<HospitalDrg> groups;
			try
			{
				var aids = stays.Select(s => s.ID).ToList();
				assigned = await _db.Set<HospitalDrgAssignment>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && aids.Contains(x.AdmissionId)).ToListAsync(ct);
				groups = await _db.Set<HospitalDrg>().AsNoTracking().Where(g => g.CompanyId == ctx.CompanyId).OrderBy(g => g.Code).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return new HospitalDrgPage(false, Array.Empty<HospitalDrgStay>(), 0, stays.Count, null, 0, 0, Array.Empty<(HospitalDrg, HospitalDrgCaseStat)>(), Array.Empty<HospitalDrg>(), 0);
			}
			var byAdm = assigned.ToDictionary(x => x.AdmissionId);
			var byId = groups.ToDictionary(g => g.ID);
			var all = stays.Select(s =>
			{
				var asg = byAdm.GetValueOrDefault(s.ID);
				return new HospitalDrgStay(s.ID, s.PatientId, s.Name, s.NameEn, s.PhotoPath, s.Mrn, s.AdmittedAt, s.Discharged, Los(s.AdmittedAt, s.Discharged), s.Diagnosis,
					operated.Contains(s.ID), asg, asg?.DrgId is int d ? byId.GetValueOrDefault(d) : null);
			}).ToList();

			var cmiSince = nowUtc.AddDays(-CmiDays);
			var recent = all.Where(s => s.Assignment?.DrgId != null && s.DischargedAt >= cmiSince).ToList();
			var top = recent.GroupBy(s => s.Assignment!.DrgId!.Value).Where(g => byId.ContainsKey(g.Key))
				.Select(g => (byId[g.Key], new HospitalDrgCaseStat(g.Key, g.Count(), g.Sum(s => s.Assignment!.Weight), Math.Round((decimal)g.Average(s => s.LosDays), 1), byId[g.Key].MeanLos)))
				.OrderByDescending(x => x.Item2.Weight).Take(8).ToList();

			IEnumerable<HospitalDrgStay> list = view == "coded" ? all.Where(s => s.Assignment != null) : all.Where(s => s.Assignment == null);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				list = list.Where(s => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (s.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (s.Mrn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| (s.Diagnosis ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (s.Assignment?.PrincipalDx ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (s.Drg?.Code ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
			}
			var ordered = list.OrderByDescending(s => s.DischargedAt).ToList();

			IEnumerable<HospitalDrg> gl = groups;
			if (!string.IsNullOrWhiteSpace(gq))
			{
				var t = gq.Trim();
				gl = gl.Where(g => g.Code.Contains(t, StringComparison.OrdinalIgnoreCase) || g.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (g.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || g.DxPrefixes.Contains(t, StringComparison.OrdinalIgnoreCase));
			}
			var gAll = gl.ToList();
			return new HospitalDrgPage(true, ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), ordered.Count, all.Count(s => s.Assignment == null),
				HospitalDrgRules.CaseMixIndex(recent.Select(s => s.Assignment!.Weight)), recent.Count, all.Count(s => s.Assignment != null && s.Assignment.DrgId == null),
				top, gAll.Skip((Math.Max(1, gpage) - 1) * size).Take(size).ToList(), gAll.Count);
		}

		public async Task<HospitalResult> AssignAsync(BusinessContext ctx, int admissionId, HospitalDrgAssignInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			var dx = HospitalDrgRules.NormalizeDx(i.PrincipalDx);
			if (dx.Length < 3 || dx.Length > 10 || !char.IsLetter(dx[0])) return HospitalResult.Fail("Enter the principal diagnosis as an ICD-10 code");
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Discharged || a.DischargedAt == null) return HospitalResult.Fail("A stay is coded after its discharge");
			var birth = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == a.PatientId).Select(p => p.BirthDate).FirstOrDefaultAsync(ct);
			bool surgical = await _db.Set<HospitalSurgery>().AnyAsync(s => s.CompanyId == ctx.CompanyId && s.AdmissionId == a.ID && s.Status == HospitalSurgeryStatus.Done, ct);
			try
			{
				var groups = await _db.Set<HospitalDrg>().AsNoTracking().Where(g => g.CompanyId == ctx.CompanyId && g.IsActive).ToListAsync(ct);
				HospitalDrg? g = i.DrgId is int id ? groups.FirstOrDefault(x => x.ID == id) : HospitalDrgRules.Group(groups, dx, surgical, HospitalPatientService.AgeOn(birth, a.AdmittedAt));
				if (i.DrgId != null && g == null) return HospitalResult.Fail("not_found");
				var row = await _db.Set<HospitalDrgAssignment>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == a.ID, ct);
				if (row == null) _db.Add(row = new HospitalDrgAssignment { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId });
				row.PrincipalDx = dx; row.Surgical = surgical; row.DrgId = g?.ID; row.Weight = g?.Weight ?? 0; row.LosDays = Los(a.AdmittedAt, a.DischargedAt.Value);
				row.Manual = i.DrgId != null; row.AssignedAt = nowUtc; row.AssignedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		public async Task<HospitalResult> SaveGroupAsync(BusinessContext ctx, HospitalDrgInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDrgRules.DefinitionRefusal(i.Code, i.Name, i.DxPrefixes, i.Weight, i.MinAge, i.MaxAge, i.MeanLos) is string why) return HospitalResult.Fail(why);
			var code = i.Code!.Trim().ToUpperInvariant();
			try
			{
				if (await _db.Set<HospitalDrg>().AnyAsync(g => g.CompanyId == ctx.CompanyId && g.Code == code && g.ID != (i.Id ?? 0), ct)) return HospitalResult.Fail("Another group uses this code");
				HospitalDrg? row = i.Id is int id ? await _db.Set<HospitalDrg>().FirstOrDefaultAsync(g => g.ID == id && g.CompanyId == ctx.CompanyId, ct) : null;
				if (i.Id != null && row == null) return HospitalResult.Fail("not_found");
				if (row == null) _db.Add(row = new HospitalDrg { CompanyId = ctx.CompanyId });
				row.Code = code; row.Name = i.Name!.Trim(); row.NameEn = string.IsNullOrWhiteSpace(i.NameEn) ? null : i.NameEn.Trim();
				row.DxPrefixes = string.Join(",", HospitalDrgRules.Prefixes(i.DxPrefixes)); row.Surgical = i.Surgical; row.MinAge = i.MinAge; row.MaxAge = i.MaxAge;
				row.Weight = i.Weight; row.MeanLos = i.MeanLos; row.IsActive = i.IsActive; row.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another group uses this code"); }
		}
	}
}
