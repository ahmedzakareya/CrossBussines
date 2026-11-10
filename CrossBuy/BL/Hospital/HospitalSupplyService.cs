using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H32 — ward requisitions to stores. The decisions are HospitalSupplyRules' (no database). The ward desk
	// (`admit`) writes and sends a request; the store (`pharmacy`) approves each quantity, picks and dispatches —
	// dispatching posts the inventory module's own transfer (StockService.TransferAsync, called as it is). Units and
	// pars are set up with `manage`.
	// ==========================================================================================

	public sealed class HospitalSupplyUnitInput { public int? Id { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string NameEn { get; set; } = ""; public int? WardId { get; set; } public int StoreWarehouseId { get; set; } public int SourceWarehouseId { get; set; } public bool IsActive { get; set; } = true; }
	public sealed class HospitalSupplyParInput { public int ItemId { get; set; } public decimal ParQty { get; set; } }
	public sealed class HospitalRequisitionInput { public int UnitId { get; set; } public string Priority { get; set; } = HospitalRequisitionPriority.Routine; public string? Note { get; set; } public bool FromPar { get; set; } = true; }
	public sealed class HospitalRequisitionLineInput { public int ItemId { get; set; } public decimal Qty { get; set; } }
	public sealed class HospitalRequisitionMoveInput { public string To { get; set; } = ""; public string? Reason { get; set; } }

	public sealed record HospitalSupplyItemRow(int ItemId, string Code, string Name, string NameEn, decimal Par, decimal OnWard, decimal StoreStock);
	public sealed record HospitalRequisitionLineRow(HospitalRequisitionLine Line, string Code, string Name, string NameEn, decimal Par, decimal OnWard, decimal StoreStock);
	public sealed record HospitalRequisitionRow(HospitalRequisition Req, HospitalSupplyUnit Unit, string? RequestedBy, string? RequestedByPhoto, int Lines);
	public sealed record HospitalRequisitionView(HospitalRequisitionRow Row, IReadOnlyList<HospitalRequisitionLineRow> Lines, string? TransferNo);
	public sealed record HospitalSupplyBoard(IReadOnlyList<HospitalRequisitionRow> Requests, int Today, double? AverageHours, int BelowPar);

	public static class HospitalSupplyRules
	{
		/// The prototype's suggested quantity: the unit's par less what its store holds now (never below zero).
		public static decimal Suggest(decimal par, decimal onWard) => Math.Max(0, par - onWard);

		/// H36: may this person do this duty on this unit? The administrator always; a person named for the duty; and,
		/// while NOBODY is named for it on the unit, whoever holds the hospital role it defaults to (admit / pharmacy).
		public static bool MayAct(bool manage, bool named, bool anyoneNamed, bool holdsDefaultRole) => manage || named || (!anyoneNamed && holdsDefaultRole);

		/// The hospital role a duty falls back to while the unit names nobody for it.
		public static string DefaultRole(string duty) => duty == HospitalSupplyDuty.Approve ? HospitalActions.Pharmacy : HospitalActions.Admit;

		public static string? UnitRefusal(string? code, string? name, int store, int source)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return "Enter the unit's code and name";
			if (store <= 0 || source <= 0) return "Choose the unit's store and the store it draws from";
			if (store == source) return "The unit's store and its source are two warehouses";
			return null;
		}

		public static string? ParRefusal(decimal qty) => qty is <= 0 or > 1_000_000 ? "A par is more than zero" : null;

		/// Why a line cannot be added or changed, or null: only on a draft, a positive quantity.
		public static string? LineRefusal(string status, decimal qty)
		{
			if (status != HospitalRequisitionStatus.Draft) return "Only a draft request is changed";
			if (qty is <= 0 or > 1_000_000) return "A quantity is more than zero";
			return null;
		}

		/// Why the store cannot approve this quantity, or null: while submitted, 0 to what was asked.
		public static string? ApproveRefusal(string status, decimal requested, decimal approved)
		{
			if (status != HospitalRequisitionStatus.Submitted) return "Quantities are approved on a submitted request";
			if (approved < 0) return "The approved quantity is 0 or more";
			if (approved > requested) return "The store gives at most what was asked";
			return null;
		}

		/// Why a request cannot make this move, or null.
		public static string? MoveRefusal(string from, string to, int lines, bool everyLineApproved, bool anyApproved, string? reason)
		{
			switch (from, to)
			{
				case (HospitalRequisitionStatus.Draft, HospitalRequisitionStatus.Submitted):
					return lines == 0 ? "Add at least one item" : null;
				case (HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Approved):
					if (!everyLineApproved) return "Approve a quantity on every line (0 for none)";
					return anyApproved ? null : "Approve at least one quantity, or cancel the request";
				case (HospitalRequisitionStatus.Approved, HospitalRequisitionStatus.Picking):
				case (HospitalRequisitionStatus.Picking, HospitalRequisitionStatus.Dispatched):
					return null;
				case (HospitalRequisitionStatus.Draft or HospitalRequisitionStatus.Submitted or HospitalRequisitionStatus.Approved or HospitalRequisitionStatus.Picking, HospitalRequisitionStatus.Cancelled):
					return string.IsNullOrWhiteSpace(reason) ? "Write why the request is cancelled" : null;
				default:
					return "This request cannot make that move";
			}
		}

		/// The store's average time from a request sent to it dispatched, in hours (one decimal), or null when none.
		public static double? AverageHours(IEnumerable<(DateTime? Submitted, DateTime? Dispatched)> done)
		{
			var spans = done.Where(d => d.Submitted != null && d.Dispatched != null && d.Dispatched >= d.Submitted).Select(d => (d.Dispatched!.Value - d.Submitted!.Value).TotalHours).ToList();
			return spans.Count == 0 ? null : Math.Round(spans.Average(), 1);
		}
	}

	public sealed class HospitalSupplyService
	{
		public const string NoTables = "The supply tables are not created yet (hospital_h32_supply.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;

		public HospitalSupplyService(CrossDbContext db, IHospitalAccessService access, IStockService stock) { _db = db; _access = access; _stock = stock; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalSupplyUnitPerson> People(BusinessContext ctx) => _db.Set<HospitalSupplyUnitPerson>().Where(p => p.CompanyId == ctx.CompanyId);

		/// H36: the unit's setting decides who asks and who gives (see HospitalSupplyRules.MayAct).
		public async Task<bool> MayAsync(BusinessContext ctx, int unitId, string duty, CancellationToken ct = default)
		{
			if (await Can(ctx, HospitalActions.Manage, ct)) return true;
			List<int> named;
			try { named = await People(ctx).AsNoTracking().Where(p => p.UnitId == unitId && p.Duty == duty).Select(p => p.EmployeeId).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { named = new(); }   // H36 not applied: the roles decide
			bool me = ctx.EmployeeId is int e && named.Contains(e);
			return HospitalSupplyRules.MayAct(false, me, named.Count > 0, !me && named.Count == 0 && await Can(ctx, HospitalSupplyRules.DefaultRole(duty), ct));
		}

		private async Task<int?> UnitOfAsync(BusinessContext ctx, int reqId, CancellationToken ct) =>
			await Reqs(ctx).AsNoTracking().Where(x => x.ID == reqId).Select(x => (int?)x.UnitId).FirstOrDefaultAsync(ct);

		// ---------------- H36: the unit's people (`manage`) ----------------

		public sealed record PersonRow(int Id, int EmployeeId, string Duty, string Name, string NameEn, string? Photo);

		public async Task<IReadOnlyList<PersonRow>> PeopleAsync(BusinessContext ctx, int unitId, CancellationToken ct = default)
		{
			try
			{
				var rows = await People(ctx).AsNoTracking().Where(p => p.UnitId == unitId).OrderBy(p => p.Duty).ThenBy(p => p.ID).ToListAsync(ct);
				var ids = rows.Select(r => r.EmployeeId).Distinct().ToList();
				var who = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				var photos = await EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);
				return rows.Where(r => who.ContainsKey(r.EmployeeId))
					.Select(r => new PersonRow(r.ID, r.EmployeeId, r.Duty, who[r.EmployeeId].FullName ?? "", who[r.EmployeeId].FullNameEn ?? who[r.EmployeeId].FullName ?? "", photos.GetValueOrDefault(r.EmployeeId))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<PersonRow>(); }
		}

		public async Task<HospitalResult> AddPersonAsync(BusinessContext ctx, int unitId, int employeeId, string duty, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalSupplyDuty.All.Contains(duty)) return HospitalResult.Fail("Choose the duty");
			if (!await Units(ctx).AnyAsync(u => u.ID == unitId, ct) || !await _db.Employee.AnyAsync(e => e.ID == employeeId && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct))
				return HospitalResult.Fail("not_found");
			try
			{
				if (await People(ctx).AnyAsync(p => p.UnitId == unitId && p.EmployeeId == employeeId && p.Duty == duty, ct)) return HospitalResult.Fail("This person already has this duty on the unit");
				var row = new HospitalSupplyUnitPerson { CompanyId = ctx.CompanyId, UnitId = unitId, EmployeeId = employeeId, Duty = duty, CreatedAt = nowUtc };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemovePersonAsync(BusinessContext ctx, int personId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				int n = await People(ctx).Where(p => p.ID == personId).ExecuteDeleteAsync(ct);
				return n == 1 ? HospitalResult.Success(personId) : HospitalResult.Fail("not_found");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalResult.Fail(NoTables); }
		}
		private IQueryable<HospitalSupplyUnit> Units(BusinessContext ctx) => _db.Set<HospitalSupplyUnit>().Where(u => u.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalRequisition> Reqs(BusinessContext ctx) => _db.Set<HospitalRequisition>().Where(r => r.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalRequisitionLine> Lines(BusinessContext ctx) => _db.Set<HospitalRequisitionLine>().Where(l => l.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		private async Task<Dictionary<int, decimal>> OnHandAsync(BusinessContext ctx, int warehouseId, IReadOnlyCollection<int> itemIds, CancellationToken ct) =>
			await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == warehouseId && itemIds.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);

		// ---------------- setup (`manage`) ----------------

		public async Task<IReadOnlyList<HospitalSupplyUnit>> UnitsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await Units(ctx).AsNoTracking().OrderBy(u => u.Code).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalSupplyUnit>(); }
		}

		public async Task<HospitalResult> SaveUnitAsync(BusinessContext ctx, HospitalSupplyUnitInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalSupplyRules.UnitRefusal(input.Code, input.Name, input.StoreWarehouseId, input.SourceWarehouseId) is string why) return HospitalResult.Fail(why);
			var whs = new[] { input.StoreWarehouseId, input.SourceWarehouseId };
			if (await _db.Warehouses.CountAsync(w => w.CompanyID == ctx.CompanyId && whs.Contains(w.ID), ct) != 2) return HospitalResult.Fail("not_found");
			try
			{
				var u = input.Id is int id ? await Units(ctx).FirstOrDefaultAsync(x => x.ID == id, ct) : null;
				if (input.Id != null && u == null) return HospitalResult.Fail("not_found");
				if (await Units(ctx).AnyAsync(x => x.Code == input.Code.Trim() && x.ID != (input.Id ?? 0), ct)) return HospitalResult.Fail("Another unit has this code");
				u ??= _db.Set<HospitalSupplyUnit>().Add(new HospitalSupplyUnit { CompanyId = ctx.CompanyId }).Entity;
				u.Code = input.Code.Trim(); u.Name = input.Name.Trim(); u.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? input.Name.Trim() : input.NameEn.Trim();
				u.WardId = input.WardId; u.StoreWarehouseId = input.StoreWarehouseId; u.SourceWarehouseId = input.SourceWarehouseId; u.IsActive = input.IsActive;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(u.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// A unit's items: its pars, with what its store holds and what the source store holds.
		public async Task<IReadOnlyList<HospitalSupplyItemRow>> ParsAsync(BusinessContext ctx, int unitId, CancellationToken ct = default)
		{
			var u = await Units(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == unitId, ct);
			if (u == null) return Array.Empty<HospitalSupplyItemRow>();
			var pars = await (from p in _db.Set<HospitalSupplyPar>().AsNoTracking()
							  join i in _db.Items.AsNoTracking() on p.ItemId equals i.ID
							  where p.CompanyId == ctx.CompanyId && p.UnitId == unitId && i.CompanyID == ctx.CompanyId
							  orderby i.Name
							  select new { p.ItemId, Code = i.ItemCode, i.Name, NameEn = i.NameEn ?? i.Name, p.ParQty }).ToListAsync(ct);
			var ids = pars.Select(p => p.ItemId).ToList();
			var ward = await OnHandAsync(ctx, u.StoreWarehouseId, ids, ct);
			var store = await OnHandAsync(ctx, u.SourceWarehouseId, ids, ct);
			return pars.Select(p => new HospitalSupplyItemRow(p.ItemId, p.Code, p.Name, p.NameEn, p.ParQty, ward.GetValueOrDefault(p.ItemId), store.GetValueOrDefault(p.ItemId))).ToList();
		}

		public async Task<HospitalResult> SaveParAsync(BusinessContext ctx, int unitId, HospitalSupplyParInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalSupplyRules.ParRefusal(input.ParQty) is string why) return HospitalResult.Fail(why);
			if (!await Units(ctx).AnyAsync(u => u.ID == unitId, ct) || !await _db.Items.AnyAsync(i => i.ID == input.ItemId && i.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var p = await _db.Set<HospitalSupplyPar>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.UnitId == unitId && x.ItemId == input.ItemId, ct);
			if (p == null) _db.Add(p = new HospitalSupplyPar { CompanyId = ctx.CompanyId, UnitId = unitId, ItemId = input.ItemId });
			p.ParQty = input.ParQty;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(p.ID);
		}

		public async Task<HospitalResult> RemoveParAsync(BusinessContext ctx, int unitId, int itemId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			int n = await _db.Set<HospitalSupplyPar>().Where(x => x.CompanyId == ctx.CompanyId && x.UnitId == unitId && x.ItemId == itemId).ExecuteDeleteAsync(ct);
			return n == 1 ? HospitalResult.Success(itemId) : HospitalResult.Fail("not_found");
		}

		/// Stock items by code or name, for a line or a par.
		public async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn)>> SearchItemsAsync(BusinessContext ctx, string? q, CancellationToken ct = default)
		{
			var s = (q ?? "").Trim();
			var rows = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && i.ItemType == "Stockable"
					&& (s == "" || i.ItemCode.Contains(s) || i.Barcode == s || i.Name.Contains(s) || (i.NameEn != null && i.NameEn.Contains(s))))
				.OrderBy(i => i.Name).Take(30).Select(i => new { i.ID, Code = i.ItemCode, i.Name, NameEn = i.NameEn ?? i.Name }).ToListAsync(ct);
			return rows.Select(r => (r.ID, r.Code, r.Name, r.NameEn)).ToList();
		}

		// ---------------- the board ----------------

		public async Task<(HospitalSupplyBoard? Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime todayUtc, CancellationToken ct = default)
		{
			try
			{
				var reqs = await Reqs(ctx).AsNoTracking().OrderByDescending(r => r.ID).Take(100).ToListAsync(ct);
				var unitIds = reqs.Select(r => r.UnitId).Distinct().ToList();
				var units = await Units(ctx).AsNoTracking().ToDictionaryAsync(u => u.ID, ct);
				var reqIds = reqs.Select(r => r.ID).ToList();
				var counts = await Lines(ctx).AsNoTracking().Where(l => reqIds.Contains(l.RequisitionId)).GroupBy(l => l.RequisitionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var people = await PeopleAsync(ctx, reqs.Select(r => r.RequestedByEmployeeId), ct);
				var rows = reqs.Where(r => units.ContainsKey(r.UnitId)).Select(r => Row(r, units[r.UnitId], people, counts.GetValueOrDefault(r.ID))).ToList();
				var avg = HospitalSupplyRules.AverageHours(reqs.Where(r => r.Status == HospitalRequisitionStatus.Dispatched).Select(r => (r.SubmittedAt, r.DispatchedAt)));
				// items below par across every active unit
				int below = 0;
				foreach (var u in units.Values.Where(u => u.IsActive))
					below += (await ParsAsync(ctx, u.ID, ct)).Count(p => p.OnWard < p.Par);
				return (new HospitalSupplyBoard(rows, reqs.Count(r => r.CreatedAt.Date == todayUtc.Date), avg, below), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (null, false); }
		}

		private async Task<Dictionary<int, (string Name, string? Photo)>> PeopleAsync(BusinessContext ctx, IEnumerable<int?> employeeIds, CancellationToken ct)
		{
			var ids = employeeIds.OfType<int>().Distinct().ToList();
			if (ids.Count == 0) return new();
			var names = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);
			return names.ToDictionary(n => n.ID, n => (DisplayName.Of(n.FullName, n.FullNameEn ?? n.FullName), photos.GetValueOrDefault(n.ID)));
		}

		private static HospitalRequisitionRow Row(HospitalRequisition r, HospitalSupplyUnit u, Dictionary<int, (string Name, string? Photo)> people, int lines)
		{
			var who = r.RequestedByEmployeeId is int e && people.TryGetValue(e, out var p) ? p : ((string Name, string? Photo)?)null;
			return new HospitalRequisitionRow(r, u, who?.Name, who?.Photo, lines);
		}

		public async Task<HospitalRequisitionView?> RequisitionAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var r = await Reqs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return null;
			var u = await Units(ctx).AsNoTracking().FirstAsync(x => x.ID == r.UnitId, ct);
			var lines = await (from l in Lines(ctx).AsNoTracking()
							   join i in _db.Items.AsNoTracking() on l.ItemId equals i.ID
							   where l.RequisitionId == id && i.CompanyID == ctx.CompanyId
							   orderby i.Name
							   select new { l, Code = i.ItemCode, i.Name, NameEn = i.NameEn ?? i.Name }).ToListAsync(ct);
			var ids = lines.Select(x => x.l.ItemId).ToList();
			var pars = await _db.Set<HospitalSupplyPar>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.UnitId == u.ID && ids.Contains(p.ItemId)).ToDictionaryAsync(p => p.ItemId, p => p.ParQty, ct);
			var ward = await OnHandAsync(ctx, u.StoreWarehouseId, ids, ct);
			var store = await OnHandAsync(ctx, u.SourceWarehouseId, ids, ct);
			var people = await PeopleAsync(ctx, new[] { r.RequestedByEmployeeId }, ct);
			string? trNo = r.StockTransferId is int t ? await _db.StockTransfers.AsNoTracking().Where(s => s.ID == t && s.CompanyID == ctx.CompanyId).Select(s => s.TransferNo).FirstOrDefaultAsync(ct) : null;
			return new HospitalRequisitionView(Row(r, u, people, lines.Count),
				lines.Select(x => new HospitalRequisitionLineRow(x.l, x.Code, x.Name, x.NameEn, pars.GetValueOrDefault(x.l.ItemId), ward.GetValueOrDefault(x.l.ItemId), store.GetValueOrDefault(x.l.ItemId))).ToList(), trNo);
		}

		// ---------------- the ward desk (`admit`) ----------------

		/// A new draft for a unit; from its pars by default (every item below par, at par − on ward).
		public async Task<HospitalResult> CreateAsync(BusinessContext ctx, HospitalRequisitionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await MayAsync(ctx, input.UnitId, HospitalSupplyDuty.Request, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalRequisitionPriority.All.Contains(input.Priority)) return HospitalResult.Fail("Choose the priority");
			try
			{
				var u = await Units(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.UnitId && x.IsActive, ct);
				if (u == null) return HospitalResult.Fail("not_found");
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				var nums = await Reqs(ctx).Select(r => r.ReqNo).ToListAsync(ct);
				int max = nums.Select(n => int.TryParse(n.AsSpan(4), out var v) ? v : 0).DefaultIfEmpty(0).Max();
				var r = new HospitalRequisition { CompanyId = ctx.CompanyId, ReqNo = "REQ-" + (max + 1).ToString("000000"), UnitId = u.ID, Priority = input.Priority, Status = HospitalRequisitionStatus.Draft,
					Note = Clip(input.Note, 500), CreatedAt = nowUtc, RequestedByEmployeeId = employeeId };
				_db.Add(r);
				await _db.SaveChangesAsync(ct);
				if (input.FromPar)
					foreach (var p in await ParsAsync(ctx, u.ID, ct))
						if (HospitalSupplyRules.Suggest(p.Par, p.OnWard) is var q && q > 0)
							_db.Add(new HospitalRequisitionLine { CompanyId = ctx.CompanyId, RequisitionId = r.ID, ItemId = p.ItemId, RequestedQty = q });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SetLineAsync(BusinessContext ctx, int reqId, HospitalRequisitionLineInput input, CancellationToken ct = default)
		{
			if (await UnitOfAsync(ctx, reqId, ct) is not int unit) return HospitalResult.Fail("not_found");
			if (!await MayAsync(ctx, unit, HospitalSupplyDuty.Request, ct)) return HospitalResult.Fail("forbidden");
			var r = await Reqs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == reqId, ct);
			if (r == null || !await _db.Items.AnyAsync(i => i.ID == input.ItemId && i.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (HospitalSupplyRules.LineRefusal(r.Status, input.Qty) is string why) return HospitalResult.Fail(why);
			var l = await Lines(ctx).FirstOrDefaultAsync(x => x.RequisitionId == reqId && x.ItemId == input.ItemId, ct);
			if (l == null) _db.Add(l = new HospitalRequisitionLine { CompanyId = ctx.CompanyId, RequisitionId = reqId, ItemId = input.ItemId });
			l.RequestedQty = input.Qty;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(l.ID);
		}

		public async Task<HospitalResult> RemoveLineAsync(BusinessContext ctx, int reqId, int itemId, CancellationToken ct = default)
		{
			if (await UnitOfAsync(ctx, reqId, ct) is not int unit) return HospitalResult.Fail("not_found");
			if (!await MayAsync(ctx, unit, HospitalSupplyDuty.Request, ct)) return HospitalResult.Fail("forbidden");
			var r = await Reqs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == reqId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalRequisitionStatus.Draft) return HospitalResult.Fail("Only a draft request is changed");
			int n = await Lines(ctx).Where(x => x.RequisitionId == reqId && x.ItemId == itemId).ExecuteDeleteAsync(ct);
			return n == 1 ? HospitalResult.Success(itemId) : HospitalResult.Fail("not_found");
		}

		// ---------------- the store (`pharmacy`) ----------------

		public async Task<HospitalResult> ApproveLineAsync(BusinessContext ctx, int reqId, int itemId, decimal qty, CancellationToken ct = default)
		{
			if (await UnitOfAsync(ctx, reqId, ct) is not int unit) return HospitalResult.Fail("not_found");
			if (!await MayAsync(ctx, unit, HospitalSupplyDuty.Approve, ct)) return HospitalResult.Fail("forbidden");
			var r = await Reqs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == reqId, ct);
			var l = await Lines(ctx).FirstOrDefaultAsync(x => x.RequisitionId == reqId && x.ItemId == itemId, ct);
			if (r == null || l == null) return HospitalResult.Fail("not_found");
			if (HospitalSupplyRules.ApproveRefusal(r.Status, l.RequestedQty, qty) is string why) return HospitalResult.Fail(why);
			l.ApprovedQty = qty;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(l.ID);
		}

		/// Moves a request. The desk submits and cancels its own draft (`admit`); every other move is the store's
		/// (`pharmacy`). Dispatched posts the inventory transfer of the approved quantities in the same transaction.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int reqId, HospitalRequisitionMoveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var r = await Reqs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == reqId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			bool desk = input.To == HospitalRequisitionStatus.Submitted || (input.To == HospitalRequisitionStatus.Cancelled && r.Status == HospitalRequisitionStatus.Draft);
			if (!await MayAsync(ctx, r.UnitId, desk ? HospitalSupplyDuty.Request : HospitalSupplyDuty.Approve, ct)) return HospitalResult.Fail("forbidden");
			var lines = await Lines(ctx).AsNoTracking().Where(l => l.RequisitionId == reqId).ToListAsync(ct);
			if (HospitalSupplyRules.MoveRefusal(r.Status, input.To, lines.Count, lines.All(l => l.ApprovedQty != null), lines.Any(l => l.ApprovedQty > 0), input.Reason) is string why) return HospitalResult.Fail(why);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			int? transferId = null;
			if (input.To == HospitalRequisitionStatus.Dispatched)
			{
				var u = await Units(ctx).AsNoTracking().FirstAsync(x => x.ID == r.UnitId, ct);
				var give = lines.Where(l => l.ApprovedQty > 0).Select(l => new TransferLineInput { ItemId = l.ItemId, Qty = l.ApprovedQty!.Value }).ToList();
				var (ok, err, tr) = await _stock.TransferAsync(ctx.CompanyId, u.SourceWarehouseId, u.StoreWarehouseId, nowUtc.ToLocalTime(), r.ReqNo + " — " + DisplayName.Of(u.Name, u.NameEn), give, null);
				if (!ok || tr == null) { await tx.RollbackAsync(); return HospitalResult.Fail(err ?? "The change could not be saved"); }
				transferId = tr.ID;
			}
			var q = Reqs(ctx).Where(x => x.ID == reqId && x.Status == r.Status);   // nothing moves a request twice
			int n = input.To switch
			{
				HospitalRequisitionStatus.Submitted => await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.To).SetProperty(x => x.SubmittedAt, nowUtc), ct),
				HospitalRequisitionStatus.Approved => await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.To).SetProperty(x => x.ApprovedAt, nowUtc).SetProperty(x => x.ApprovedByEmployeeId, employeeId), ct),
				HospitalRequisitionStatus.Dispatched => await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.To).SetProperty(x => x.DispatchedAt, nowUtc).SetProperty(x => x.DispatchedByEmployeeId, employeeId).SetProperty(x => x.StockTransferId, transferId), ct),
				HospitalRequisitionStatus.Cancelled => await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.To).SetProperty(x => x.CancelReason, Clip(input.Reason, 300)), ct),
				_ => await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.To), ct),
			};
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Someone else changed this request: open it again"); }
			await tx.CommitAsync();
			return HospitalResult.Success(reqId);
		}
	}
}
