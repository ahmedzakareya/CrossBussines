using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalBiomedDeviceInput
	{
		public int AssetId { get; set; } public int? RoomId { get; set; } public string? Category { get; set; } public string? RiskClass { get; set; }
		public string? SerialNo { get; set; } public string? Manufacturer { get; set; } public string? ModelName { get; set; } public bool Retired { get; set; }
	}
	public sealed class HospitalBiomedEventInput { public string? Kind { get; set; } public bool? Passed { get; set; } public string? Detail { get; set; } }
	public sealed class HospitalBiomedPmInput { public int ScheduleId { get; set; } public string? Note { get; set; } }

	public sealed record HospitalBiomedRow(HospitalBiomedDevice Device, string AssetName, string? AssetNameEn, string? AssetNo, string? Room,
		DateTime? NextPm, int? NextPmScheduleId, string? NextPmTitle, DateTime? LastSafety, bool? LastSafetyPassed, bool SafetyOverdue, bool PmOverdue, double DownHours30);
	public sealed record HospitalBiomedPage(bool Ready, IReadOnlyList<HospitalBiomedRow> Rows, int Total, int Devices, int DownNow, int SafetyOverdue, decimal? PmCompliance, decimal? Availability30,
		IReadOnlyList<(int Id, string Name)> FreeAssets, IReadOnlyList<(int Id, string Name)> Rooms);

	// ==========================================================================================
	// HOSPITAL H245 — the biomedical register over the fixed assets. Anyone in the hospital reads it; biomedical
	// engineering (`manage`) registers devices, records events and closes preventive maintenance — through the asset
	// module's own MaintenanceService (no cost, no ledger: a cost is booked in the asset screen).
	// ==========================================================================================
	public sealed class HospitalBiomedService
	{
		public const string NoTables = "Biomedical engineering is not installed yet (hospital_h245_biomed.sql).";
		private static readonly string[] PmTypes = { "Preventive", "Calibration", "Inspection" };
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IMaintenanceService _maintenance;
		public HospitalBiomedService(CrossDbContext db, IHospitalAccessService access, IMaintenanceService maintenance) { _db = db; _access = access; _maintenance = maintenance; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private Task<bool> Manage(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		public async Task<HospitalBiomedPage> PageAsync(BusinessContext ctx, string? q, string? view, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = HospitalNursingService.UtcToWard(nowUtc).Date;
			var rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.IsActive).OrderBy(r => r.Number)
				.Select(r => new { r.ID, r.Number, r.Name, r.NameEn }).ToListAsync(ct);
			var roomName = rooms.ToDictionary(r => r.ID, r => r.Number + (r.Name == null ? "" : " · " + DisplayName.Of(r.Name, r.NameEn)));
			try
			{
				var devices = await (from d in _db.Set<HospitalBiomedDevice>().AsNoTracking()
									 join a in _db.FixedAssets.AsNoTracking() on d.AssetId equals a.ID
									 where d.CompanyId == ctx.CompanyId && a.CompanyID == ctx.CompanyId
									 select new { d, a.Name, a.NameEn, a.AssetNo }).ToListAsync(ct);
				var assetIds = devices.Select(x => x.d.AssetId).ToList();
				var schedules = await _db.MaintenanceSchedules.AsNoTracking().Where(s => s.CompanyID == ctx.CompanyId && s.IsActive && assetIds.Contains(s.AssetId) && PmTypes.Contains(s.Type)).ToListAsync(ct);
				var ids = devices.Select(x => x.d.ID).ToList();
				var since = nowUtc.AddDays(-30);
				var events = await _db.Set<HospitalBiomedEvent>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && ids.Contains(e.DeviceId)).ToListAsync(ct);
				var rows = devices.Select(x =>
				{
					var mine = schedules.Where(s => s.AssetId == x.d.AssetId).OrderBy(s => s.NextDueDate).ToList();
					var next = mine.FirstOrDefault();
					var ev = events.Where(e => e.DeviceId == x.d.ID).ToList();
					var test = ev.Where(e => e.Kind == HospitalBiomedEventKind.SafetyTest).MaxBy(e => e.At);
					return new HospitalBiomedRow(x.d, x.Name, x.NameEn, x.AssetNo, x.d.RoomId is int r ? roomName.GetValueOrDefault(r) : null, next?.NextDueDate, next?.ID, next == null ? null : DisplayName.Of(next.Title, next.TitleEn),
						test?.At, test?.Passed, x.d.Status != HospitalBiomedStatus.Retired && HospitalBiomedRules.SafetyOverdue(x.d.RiskClass, test == null ? null : HospitalNursingService.UtcToWard(test.At), today),
						next != null && next.NextDueDate.Date < today, HospitalBiomedRules.DowntimeHours(ev, since, nowUtc));
				}).ToList();
				var live = rows.Where(r => r.Device.Status != HospitalBiomedStatus.Retired).ToList();
				double downAll = live.Sum(r => r.DownHours30);
				decimal? avail = live.Count == 0 ? null : HospitalBiomedRules.Availability(downAll / live.Count, since, nowUtc);

				IEnumerable<HospitalBiomedRow> list = view switch
				{
					"down" => rows.Where(r => r.Device.Status == HospitalBiomedStatus.Down),
					"overdue" => rows.Where(r => r.SafetyOverdue || r.PmOverdue),
					"retired" => rows.Where(r => r.Device.Status == HospitalBiomedStatus.Retired),
					_ => live,
				};
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					list = list.Where(r => r.AssetName.Contains(t, StringComparison.OrdinalIgnoreCase) || (r.AssetNameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.AssetNo ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
						|| (r.Device.SerialNo ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Device.Manufacturer ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Device.ModelName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Room ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
				}
				var ordered = list.OrderBy(r => r.Device.Status == HospitalBiomedStatus.Down ? 0 : r.SafetyOverdue || r.PmOverdue ? 1 : 2).ThenBy(r => r.Device.RiskClass == HospitalBiomedRisk.High ? 0 : 1).ThenBy(r => r.AssetName).ToList();
				var free = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && a.Status != "Disposed" && !assetIds.Contains(a.ID)).OrderBy(a => a.Name).Take(500)
					.Select(a => new { a.ID, a.Name, a.NameEn, a.AssetNo }).ToListAsync(ct);
				return new HospitalBiomedPage(true, ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), ordered.Count, live.Count, live.Count(r => r.Device.Status == HospitalBiomedStatus.Down),
					live.Count(r => r.SafetyOverdue), HospitalBiomedRules.PmCompliance(schedules.Where(s => live.Any(r => r.Device.AssetId == s.AssetId)).Select(s => s.NextDueDate), today), avail,
					free.Select(a => (a.ID, (a.AssetNo == null ? "" : a.AssetNo + " · ") + DisplayName.Of(a.Name, a.NameEn))).ToList(), rooms.Select(r => (r.ID, roomName[r.ID])).ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalBiomedPage(false, Array.Empty<HospitalBiomedRow>(), 0, 0, 0, 0, null, null, Array.Empty<(int, string)>(), rooms.Select(r => (r.ID, roomName[r.ID])).ToList());
			}
		}

		public async Task<HospitalResult> SaveDeviceAsync(BusinessContext ctx, HospitalBiomedDeviceInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Manage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.FixedAssets.AnyAsync(a => a.ID == i.AssetId && a.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (i.RoomId is int rid && !await _db.HospitalRooms.AnyAsync(r => r.ID == rid && r.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (HospitalBiomedRules.DeviceRefusal(i.Category, i.RiskClass, i.SerialNo, i.Manufacturer, i.ModelName) is string why) return HospitalResult.Fail(why);
			static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
			try
			{
				var d = await _db.Set<HospitalBiomedDevice>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AssetId == i.AssetId, ct);
				if (d == null) _db.Add(d = new HospitalBiomedDevice { CompanyId = ctx.CompanyId, AssetId = i.AssetId });
				d.RoomId = i.RoomId; d.Category = i.Category!; d.RiskClass = i.RiskClass!; d.SerialNo = T(i.SerialNo); d.Manufacturer = T(i.Manufacturer); d.ModelName = T(i.ModelName);
				if (i.Retired) { d.Status = HospitalBiomedStatus.Retired; d.DownSince = null; }
				else if (d.Status == HospitalBiomedStatus.Retired) d.Status = HospitalBiomedStatus.InService;
				d.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> EventAsync(BusinessContext ctx, int deviceId, HospitalBiomedEventInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Manage(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var d = await _db.Set<HospitalBiomedDevice>().FirstOrDefaultAsync(x => x.ID == deviceId && x.CompanyId == ctx.CompanyId, ct);
				if (d == null) return HospitalResult.Fail("not_found");
				if (HospitalBiomedRules.EventRefusal(d.Status, i.Kind, i.Passed, i.Detail) is string why) return HospitalResult.Fail(why);
				_db.Add(new HospitalBiomedEvent { CompanyId = ctx.CompanyId, DeviceId = d.ID, Kind = i.Kind!, At = nowUtc, Passed = i.Kind == HospitalBiomedEventKind.SafetyTest ? i.Passed : null,
					Detail = string.IsNullOrWhiteSpace(i.Detail) ? null : i.Detail.Trim()[..Math.Min(300, i.Detail.Trim().Length)], ByEmployeeId = employeeId });
				// a failed safety test takes the device out of service: it may not be used on a patient
				if (i.Kind == HospitalBiomedEventKind.Down || (i.Kind == HospitalBiomedEventKind.SafetyTest && i.Passed == false && d.Status != HospitalBiomedStatus.Down))
				{
					d.Status = HospitalBiomedStatus.Down; d.DownSince = nowUtc;
					if (i.Kind == HospitalBiomedEventKind.SafetyTest) _db.Add(new HospitalBiomedEvent { CompanyId = ctx.CompanyId, DeviceId = d.ID, Kind = HospitalBiomedEventKind.Down, At = nowUtc, Detail = "Failed the electrical-safety test", ByEmployeeId = employeeId });
				}
				if (i.Kind == HospitalBiomedEventKind.Up) { d.Status = HospitalBiomedStatus.InService; d.DownSince = null; }
				d.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Preventive maintenance done today on one of the device's schedules: the asset module records it and moves the schedule on.
		public async Task<HospitalResult> PmDoneAsync(BusinessContext ctx, int deviceId, HospitalBiomedPmInput i, int? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Manage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var d = await _db.Set<HospitalBiomedDevice>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == deviceId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (!await _db.MaintenanceSchedules.AnyAsync(s => s.ID == i.ScheduleId && s.CompanyID == ctx.CompanyId && s.AssetId == d.AssetId, ct)) return HospitalResult.Fail("not_found");
			var (ok, err) = await _maintenance.LogMaintenanceAsync(ctx.CompanyId, d.AssetId, i.ScheduleId, HospitalNursingService.UtcToWard(nowUtc).Date,
				string.IsNullOrWhiteSpace(i.Note) ? "Biomedical PM" : i.Note.Trim()[..Math.Min(300, i.Note.Trim().Length)], 0m, null, null, null, userId);
			return ok ? HospitalResult.Success(d.ID) : HospitalResult.Fail(err ?? "The change could not be saved");
		}
	}
}
