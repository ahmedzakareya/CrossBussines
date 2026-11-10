using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H66 — equipment work orders. Anyone who reads the hospital reports; the administrator (`manage`) assigns
	// and may act on any order; the assigned technician starts and closes their own. A registered asset's repair is logged
	// through Accounting's IMaintenanceService (its record, and its cost posted when a pay-from account is given).
	// ==========================================================================================

	public sealed class HospitalWorkOrderInput { public int? AssetId { get; set; } public string? Equipment { get; set; } public int? RoomId { get; set; } public string? Problem { get; set; } public string? Priority { get; set; } }
	public sealed class HospitalWorkOrderMoveInput
	{
		public string? To { get; set; } public int? TechnicianEmployeeId { get; set; } public string? Resolution { get; set; } public decimal? Cost { get; set; }
		public string? Vendor { get; set; } public int? PayFromAccountId { get; set; } public string? Reason { get; set; }
	}

	public sealed record HospitalWorkOrderRow(HospitalWorkOrder Order, string Device, string? DeviceEn, string? AssetNo, string? Room, string Reporter, string ReporterEn,
		string? Technician, string? TechnicianEn, string? TechnicianPhoto);

	public static class HospitalWorkOrderRules
	{
		public static string? ReportRefusal(HospitalWorkOrderInput i)
		{
			if (i.AssetId is not > 0 && string.IsNullOrWhiteSpace(i.Equipment)) return "Choose the device, or describe it";
			if (string.IsNullOrWhiteSpace(i.Problem)) return "Describe the problem";
			if (!HospitalWorkOrderPriority.All.Contains(i.Priority ?? "")) return "Choose how urgent it is";
			if ((i.Problem?.Length ?? 0) > 1000 || (i.Equipment?.Length ?? 0) > 200) return "A text is too long";
			return null;
		}

		/// Why the order cannot take this step (null = it can). `manager` acts on any order; the technician on his own.
		public static string? MoveRefusal(HospitalWorkOrder o, HospitalWorkOrderMoveInput m, int me, bool manager)
		{
			bool mine = o.TechnicianEmployeeId == me;
			switch (o.Status, m.To)
			{
				case (HospitalWorkOrderStatus.Open or HospitalWorkOrderStatus.Assigned, HospitalWorkOrderStatus.Assigned):
					if (!manager) return "forbidden";
					return m.TechnicianEmployeeId is > 0 ? null : "Choose the technician";
				case (HospitalWorkOrderStatus.Assigned, HospitalWorkOrderStatus.InProgress):
					return manager || mine ? null : "Only the assigned technician starts this order";
				case (HospitalWorkOrderStatus.InProgress, HospitalWorkOrderStatus.Done):
					if (!manager && !mine) return "Only the assigned technician closes this order";
					if (string.IsNullOrWhiteSpace(m.Resolution)) return "Write what was done";
					if (m.Cost is < 0) return "A cost cannot be negative";
					if (m.Cost is > 0 && m.PayFromAccountId is > 0 && o.AssetId == null) return "Only a registered asset's repair is posted";
					return null;
				case (HospitalWorkOrderStatus.Open or HospitalWorkOrderStatus.Assigned, HospitalWorkOrderStatus.Cancelled):
					if (!manager) return "forbidden";
					return string.IsNullOrWhiteSpace(m.Reason) ? "Write why it is cancelled" : null;
				default:
					return "This order cannot take that step from where it is";
			}
		}

		/// Hours from the report to the repair (null while it is not done).
		public static double? HoursToFix(HospitalWorkOrder o) => o.DoneAt is DateTime d ? Math.Round((d - o.ReportedAt).TotalHours, 1) : null;
	}

	public sealed class HospitalWorkOrderService
	{
		public const string NoTables = "Work orders are not installed yet (hospital_h66_work_orders.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IMaintenanceService _maintenance;
		public HospitalWorkOrderService(CrossDbContext db, IHospitalAccessService access, IMaintenanceService maintenance) { _db = db; _access = access; _maintenance = maintenance; }
		private IQueryable<HospitalWorkOrder> Orders(BusinessContext ctx) => _db.Set<HospitalWorkOrder>().Where(o => o.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<HospitalResult> ReportAsync(BusinessContext ctx, HospitalWorkOrderInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (HospitalWorkOrderRules.ReportRefusal(input) is string why) return HospitalResult.Fail(why);
			if (input.AssetId is int a && !await _db.FixedAssets.AnyAsync(x => x.ID == a && x.CompanyID == ctx.CompanyId && x.Status != "Disposed", ct)) return HospitalResult.Fail("not_found");
			if (input.RoomId is int r && !await _db.HospitalRooms.AnyAsync(x => x.ID == r && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Orders(ctx).Select(o => o.OrderNo).ToListAsync(ct);
					var row = new HospitalWorkOrder
					{
						CompanyId = ctx.CompanyId, OrderNo = HospitalDentalPracticeRules.NextNo(nos, "WO-", 6), AssetId = input.AssetId is > 0 ? input.AssetId : null,
						Equipment = input.AssetId is > 0 ? null : Clip(input.Equipment, 200), RoomId = input.RoomId, Problem = Clip(input.Problem, 1000)!, Priority = input.Priority!,
						ReportedAt = nowUtc, ReportedByEmployeeId = me,
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
				}
				return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, HospitalWorkOrderMoveInput m, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			bool manager = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			HospitalWorkOrder? o;
			try { o = await Orders(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			if (o == null) return HospitalResult.Fail("not_found");
			if (HospitalWorkOrderRules.MoveRefusal(o, m, me, manager) is string why) return HospitalResult.Fail(why);
			if (m.To == HospitalWorkOrderStatus.Assigned && !await _db.Employee.AnyAsync(e => e.ID == m.TechnicianEmployeeId && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("Choose the technician");
			if (m.PayFromAccountId is int pay && !await _db.Accounts.AnyAsync(a => a.ID == pay && a.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var q = Orders(ctx).Where(x => x.ID == id && x.Status == o.Status);   // nothing moves an order twice
			int n = m.To switch
			{
				HospitalWorkOrderStatus.Assigned => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, m.To).SetProperty(x => x.TechnicianEmployeeId, m.TechnicianEmployeeId).SetProperty(x => x.AssignedAt, nowUtc), ct),
				HospitalWorkOrderStatus.InProgress => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, m.To).SetProperty(x => x.StartedAt, nowUtc), ct),
				HospitalWorkOrderStatus.Done => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, m.To).SetProperty(x => x.DoneAt, nowUtc).SetProperty(x => x.Resolution, Clip(m.Resolution, 1000))
					.SetProperty(x => x.Cost, m.Cost).SetProperty(x => x.Vendor, Clip(m.Vendor, 200)).SetProperty(x => x.LoggedToAsset, o.AssetId != null), ct),
				_ => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalWorkOrderStatus.Cancelled).SetProperty(x => x.CancelReason, Clip(m.Reason, 300)), ct),
			};
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Someone else changed this order: open it again"); }
			if (m.To == HospitalWorkOrderStatus.Done && o.AssetId is int asset)
			{
				// the one maintenance history: Accounting's record of the repair (and its cost, when it is paid from an account)
				var (ok, err) = await _maintenance.LogMaintenanceAsync(ctx.CompanyId, asset, null, HospitalNursingService.UtcToWard(nowUtc).Date, o.OrderNo + " — " + Clip(m.Resolution, 400),
					m.Cost ?? 0, Clip(m.Vendor, 200), m.Cost is > 0 ? m.PayFromAccountId : null, null, null);
				if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The repair could not be logged on the asset: {0}", err ?? ""); }
			}
			await tx.CommitAsync();
			return HospitalResult.Success(id);
		}

		/// The board's list: active orders first (urgent first, oldest first), then the closed (newest first); searched by
		/// number, device, room or problem; one page and the total.
		public async Task<(IReadOnlyList<HospitalWorkOrderRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? state, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			var raw = await Orders(ctx).AsNoTracking().ToListAsync(ct);
			var assetIds = raw.Where(o => o.AssetId != null).Select(o => o.AssetId!.Value).Distinct().ToList();
			var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && assetIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => new { a.Name, a.NameEn, a.AssetNo }, ct);
			var roomIds = raw.Where(o => o.RoomId != null).Select(o => o.RoomId!.Value).Distinct().ToList();
			var rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && roomIds.Contains(r.ID)).ToDictionaryAsync(r => r.ID, r => r.Number, ct);
			int Rank(string p) => p == HospitalWorkOrderPriority.Urgent ? 0 : p == HospitalWorkOrderPriority.High ? 1 : 2;
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var hit = raw.Where(o => (state switch { "active" => HospitalWorkOrderStatus.Active(o.Status), "closed" => !HospitalWorkOrderStatus.Active(o.Status), _ => true }))
				.Select(o => (o, Dev: o.AssetId is int a && assets.TryGetValue(a, out var x) ? (x.Name, x.NameEn, x.AssetNo) : (o.Equipment ?? "", (string?)null, (string?)null),
					Room: o.RoomId is int r ? rooms.GetValueOrDefault(r) : null))
				.Where(t => terms.All(s => t.o.OrderNo.Contains(s, StringComparison.OrdinalIgnoreCase) || t.Dev.Item1.Contains(s, StringComparison.OrdinalIgnoreCase)
					|| (t.Dev.Item2 ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || (t.Dev.Item3 ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)
					|| (t.Room ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || t.o.Problem.Contains(s, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(t => HospitalWorkOrderStatus.Active(t.o.Status) ? 0 : 1).ThenBy(t => HospitalWorkOrderStatus.Active(t.o.Status) ? Rank(t.o.Priority) : 0)
				.ThenBy(t => HospitalWorkOrderStatus.Active(t.o.Status) ? t.o.ReportedAt : DateTime.MaxValue).ThenByDescending(t => t.o.DoneAt ?? t.o.ReportedAt).ToList();
			var pageRows = hit.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var eids = pageRows.SelectMany(t => new int?[] { t.o.ReportedByEmployeeId, t.o.TechnicianEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
			(string, string) Who(int? id) => id is int i && people.TryGetValue(i, out var e) ? (e.FullName ?? "", e.FullNameEn ?? e.FullName ?? "") : ("", "");
			return (pageRows.Select(t =>
			{
				var (rep, repEn) = Who(t.o.ReportedByEmployeeId); var (tech, techEn) = Who(t.o.TechnicianEmployeeId);
				return new HospitalWorkOrderRow(t.o, t.Dev.Item1, t.Dev.Item2, t.Dev.Item3, t.Room, rep, repEn, t.o.TechnicianEmployeeId == null ? null : tech,
					t.o.TechnicianEmployeeId == null ? null : techEn, t.o.TechnicianEmployeeId is int ti ? photos.GetValueOrDefault(ti) : null);
			}).ToList(), hit.Count);
		}

		/// The board's figures: open, urgent open, and the average hours to fix over the last 30 days.
		public async Task<(int Open, int UrgentOpen, double? AvgHours, int Done30, bool Ready)> FiguresAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var active = await Orders(ctx).AsNoTracking().Where(o => o.Status == HospitalWorkOrderStatus.Open || o.Status == HospitalWorkOrderStatus.Assigned || o.Status == HospitalWorkOrderStatus.InProgress)
					.Select(o => o.Priority).ToListAsync(ct);
				var since = nowUtc.AddDays(-30);
				var done = await Orders(ctx).AsNoTracking().Where(o => o.Status == HospitalWorkOrderStatus.Done && o.DoneAt >= since).ToListAsync(ct);
				double? avg = done.Count == 0 ? null : Math.Round(done.Average(o => HospitalWorkOrderRules.HoursToFix(o)!.Value), 1);
				return (active.Count, active.Count(p => p == HospitalWorkOrderPriority.Urgent), avg, done.Count, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (0, 0, null, 0, false); }
		}
	}
}
