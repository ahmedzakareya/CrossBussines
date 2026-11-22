using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalQcMaterialInput
	{
		public int AnalyteId { get; set; }
		public string? Lot { get; set; }
		public int Level { get; set; } = 2;
		public decimal Mean { get; set; }
		public decimal Sd { get; set; }
		public DateTime? ExpiresOn { get; set; }
	}

	public sealed record HospitalQcPoint(int RunId, DateTime At, decimal Value, decimal Z, string Status, string? Violations, string? Action);
	public sealed record HospitalQcMaterialRow(HospitalLabQcMaterial Material, string Analyte, string AnalyteEn, string? Unit, string Test, string TestEn,
		IReadOnlyList<HospitalQcPoint> Points)
	{
		public string? LatestStatus => Points.Count == 0 ? null : Points[^1].Status;
	}
	public sealed record HospitalQcPage(bool Ready, IReadOnlyList<HospitalQcMaterialRow> Rows, int Total, int OutOfControl,
		IReadOnlyList<(int Id, string Name, string NameEn, string Test)> Analytes);

	// ==========================================================================================
	// HOSPITAL H232 — quality control at the bench. Materials are set by the lab specialist (`lab-verify`) or the
	// administrator; runs are recorded by the bench (`lab`). A rejected run stops patient results for its test until an
	// accepted run of the same material — enforced in HospitalLabService.SaveResultsAsync through OutOfControlAsync.
	// ==========================================================================================
	public sealed class HospitalLabQcService
	{
		public const string NoTables = "Quality control is not installed yet (hospital_h232_lab_qc.sql).";
		public const int ChartPoints = 20;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalLabQcService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The analytes of a test whose control is out (their names), for the bench's refusal. Empty: in control.
		public static async Task<IReadOnlyList<string>> OutOfControlAsync(CrossDbContext db, int companyId, int serviceId, CancellationToken ct = default)
		{
			try
			{
				var analytes = await db.HospitalLabAnalytes.AsNoTracking().Where(a => a.CompanyId == companyId && a.ServiceId == serviceId && a.IsActive)
					.Select(a => new { a.ID, a.NameEn }).ToListAsync(ct);
				var ids = analytes.Select(a => a.ID).ToList();
				var mats = await db.Set<HospitalLabQcMaterial>().AsNoTracking().Where(m => m.CompanyId == companyId && m.IsActive && ids.Contains(m.AnalyteId))
					.Select(m => new { m.ID, m.AnalyteId }).ToListAsync(ct);
				if (mats.Count == 0) return Array.Empty<string>();
				var matIds = mats.Select(m => m.ID).ToList();
				var latest = (await db.Set<HospitalLabQcRun>().AsNoTracking().Where(r => r.CompanyId == companyId && matIds.Contains(r.MaterialId))
					.Select(r => new { r.ID, r.MaterialId, r.Status, r.At }).ToListAsync(ct))
					.GroupBy(r => r.MaterialId).Select(g => g.OrderByDescending(r => r.At).ThenByDescending(r => r.ID).First()).ToList();
				var bad = latest.Where(l => l.Status == HospitalQcStatus.Rejected).Select(l => mats.First(m => m.ID == l.MaterialId).AnalyteId).ToHashSet();
				return analytes.Where(a => bad.Contains(a.ID)).Select(a => a.NameEn).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<string>(); }
		}

		public async Task<HospitalQcPage> PageAsync(BusinessContext ctx, string? q, int page, int size, CancellationToken ct = default)
		{
			var analytes = await (from a in _db.HospitalLabAnalytes.AsNoTracking()
								  join s in _db.HospitalServices.AsNoTracking() on a.ServiceId equals s.ID
								  where a.CompanyId == ctx.CompanyId && s.CompanyId == ctx.CompanyId && a.IsActive && a.Unit != null
								  orderby s.Code, a.SortOrder
								  select new { a.ID, a.Name, a.NameEn, a.Unit, Test = s.Name, TestEn = s.NameEn }).ToListAsync(ct);
			var picker = analytes.Select(a => (a.ID, a.Name, a.NameEn, DisplayName.Of(a.Test, a.TestEn))).ToList();
			try
			{
				var mats = await _db.Set<HospitalLabQcMaterial>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.IsActive).ToListAsync(ct);
				var byId = analytes.ToDictionary(a => a.ID);
				var list = mats.Where(m => byId.ContainsKey(m.AnalyteId)).Select(m => (m, a: byId[m.AnalyteId])).ToList();
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					list = list.Where(x => x.a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || x.a.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase)
						|| x.m.Lot.Contains(t, StringComparison.OrdinalIgnoreCase) || (x.a.TestEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
				}
				var ids = mats.Select(m => m.ID).ToList();
				var runs = await _db.Set<HospitalLabQcRun>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.MaterialId)).ToListAsync(ct);
				var rows = list.Select(x =>
				{
					var pts = runs.Where(r => r.MaterialId == x.m.ID).OrderBy(r => r.At).ThenBy(r => r.ID).TakeLast(ChartPoints)
						.Select(r => new HospitalQcPoint(r.ID, r.At, r.Value, HospitalQcRules.Z(r.Value, x.m.Mean, x.m.Sd), r.Status, r.Violations, r.Action)).ToList();
					return new HospitalQcMaterialRow(x.m, x.a.Name, x.a.NameEn, x.a.Unit, x.a.Test, x.a.TestEn, pts);
				}).OrderByDescending(r => r.LatestStatus == HospitalQcStatus.Rejected).ThenBy(r => r.TestEn).ThenBy(r => r.AnalyteEn).ThenBy(r => r.Material.Level).ToList();
				int outCount = rows.Count(r => r.LatestStatus == HospitalQcStatus.Rejected);
				return new HospitalQcPage(true, rows.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), rows.Count, outCount, picker);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalQcPage(false, Array.Empty<HospitalQcMaterialRow>(), 0, 0, picker); }
		}

		public async Task<HospitalResult> SaveMaterialAsync(BusinessContext ctx, HospitalQcMaterialInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.LabVerify, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalQcRules.MaterialRefusal(input.Lot, input.Level, input.Mean, input.Sd, input.ExpiresOn, nowUtc.ToLocalTime()) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalLabAnalytes.AnyAsync(a => a.ID == input.AnalyteId && a.CompanyId == ctx.CompanyId && a.IsActive, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var lot = input.Lot!.Trim();
				if (await _db.Set<HospitalLabQcMaterial>().AnyAsync(m => m.CompanyId == ctx.CompanyId && m.AnalyteId == input.AnalyteId && m.Lot == lot && m.Level == input.Level, ct))
					return HospitalResult.Fail("This lot and level are already set for this analyte");
				var m = new HospitalLabQcMaterial
				{
					CompanyId = ctx.CompanyId, AnalyteId = input.AnalyteId, Lot = lot, Level = input.Level, Mean = Math.Round(input.Mean, 4), Sd = Math.Round(input.Sd, 4),
					ExpiresOn = input.ExpiresOn?.Date, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(m);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A lot taken out of use (finished or replaced): its runs stay; it no longer gates the test.
		public async Task<HospitalResult> RetireMaterialAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.LabVerify, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var m = await _db.Set<HospitalLabQcMaterial>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (m == null) return HospitalResult.Fail("not_found");
				m.IsActive = false; await _db.SaveChangesAsync(ct); return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A control measured: judged by the Westgard rules against the material's last runs. A rejected run needs the
		/// corrective action written once it is known (a later accepted run is what brings the test back).
		public async Task<(HospitalResult Result, string? Status, IReadOnlyList<string>? Violations)> RecordRunAsync(BusinessContext ctx, int materialId, decimal value, string? action, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Lab, null, ct)) return (HospitalResult.Fail("forbidden"), null, null);
			if ((action ?? "").Trim().Length > 300) return (HospitalResult.Fail("The action is 300 characters at most"), null, null);
			try
			{
				var m = await _db.Set<HospitalLabQcMaterial>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == materialId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
				if (m == null) return (HospitalResult.Fail("not_found"), null, null);
				if (m.ExpiresOn is DateTime e && e.Date < nowUtc.ToLocalTime().Date) return (HospitalResult.Fail("This lot has expired"), null, null);
				var prev = await _db.Set<HospitalLabQcRun>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.MaterialId == m.ID)
					.OrderByDescending(r => r.At).ThenByDescending(r => r.ID).Take(9).Select(r => r.Value).ToListAsync(ct);
				var (status, violations) = HospitalQcRules.Judge(HospitalQcRules.Z(value, m.Mean, m.Sd), prev.Select(v => HospitalQcRules.Z(v, m.Mean, m.Sd)).ToList());
				var run = new HospitalLabQcRun
				{
					CompanyId = ctx.CompanyId, MaterialId = m.ID, Value = Math.Round(value, 4), At = nowUtc, ByEmployeeId = employeeId, Status = status,
					Violations = violations.Count == 0 ? null : string.Join(",", violations), Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
				};
				_db.Add(run);
				await _db.SaveChangesAsync(ct);
				return (HospitalResult.Success(run.ID), status, violations);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (HospitalResult.Fail(NoTables), null, null); }
		}
	}
}
