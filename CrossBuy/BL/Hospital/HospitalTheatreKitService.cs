using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H37 — the procedure kit consumed from inventory (the prototype's "BOM explosion to consume the procedure
	// kit from inventory", #/or). A procedure names its kit (an inventory BUNDLE item whose bill of materials lists the
	// consumables); the theatre's setting names the store. When a case closes, the kit is exploded by
	// IBomExplosionService — the ONE place a recipe becomes quantities — and each component leaves the theatre store
	// through IStockService.PostMovementAsync (the inventory's single writer), costed to the component category's
	// cost-of-goods account, once (KitIssuedAt). A short store never blocks closing a case: the kit then stays
	// "not issued", with the reason, until the store is stocked and it is issued from the case card.
	// ==========================================================================================
	public sealed record HospitalKitLine(int ItemId, string Code, string Name, string NameEn, decimal Qty, decimal InStore);
	public sealed record HospitalKitView(int? KitItemId, string? KitName, string? KitNameEn, IReadOnlyList<HospitalKitLine> Lines, int? StoreWarehouseId, string? StoreName,
		DateTime? IssuedAt, bool Ready);

	public static class HospitalTheatreKitRules
	{
		public const string MovementSource = "HospitalSurgery";

		/// Why this case's kit cannot leave the store now, or null.
		public static string? IssueRefusal(string caseStatus, DateTime? issuedAt, bool hasKit, bool hasStore, int lines)
		{
			if (issuedAt != null) return "This case's kit has already left the store";
			if (caseStatus != HospitalSurgeryStatus.Done) return "The kit is issued when the case is closed";
			if (!hasKit) return "This procedure has no kit set";
			if (!hasStore) return "Choose the theatre store first";
			if (lines == 0) return "The kit item has no components (Inventory › the item's BOM)";
			return null;
		}

		/// The kit's quantities: each component's quantity times the kits used, rounded as the BOM service rounds.
		public static decimal Need(decimal perKit, int kits) => Math.Round(perKit * kits, 4, MidpointRounding.AwayFromZero);
	}

	public sealed class HospitalTheatreKitService
	{
		public const string NoTables = "The theatre kit tables are not created yet (hospital_h37_theatre_kits.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IBomExplosionService _bom;
		private readonly IStockService _stock;

		public HospitalTheatreKitService(CrossDbContext db, IHospitalAccessService access, IBomExplosionService bom, IStockService stock)
		{ _db = db; _access = access; _bom = bom; _stock = stock; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		public async Task<int?> StoreAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalTheatreSetting>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.StoreWarehouseId).FirstOrDefaultAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return null; }
		}

		/// A case's kit as the card shows it: the kit, its components with what the theatre store holds, and whether it left.
		public async Task<HospitalKitView> KitAsync(BusinessContext ctx, HospitalSurgery s, CancellationToken ct = default)
		{
			try
			{
				var kitId = await _db.Set<HospitalProcedureKit>().AsNoTracking().Where(k => k.CompanyId == ctx.CompanyId && k.ServiceId == s.ServiceId).Select(k => (int?)k.KitItemId).FirstOrDefaultAsync(ct);
				var store = await StoreAsync(ctx, ct);
				var storeName = store is int w ? await _db.Warehouses.AsNoTracking().Where(x => x.ID == w && x.CompanyID == ctx.CompanyId).Select(x => x.Code + " · " + (DisplayName.IsArabic ? x.Name : x.NameEn)).FirstOrDefaultAsync(ct) : null;
				if (kitId is not int kid) return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), store, storeName, s.KitIssuedAt, true);
				var kit = await _db.Items.AsNoTracking().Where(i => i.ID == kid && i.CompanyID == ctx.CompanyId).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
				var bom = await _bom.ExplodeAsync(ctx.CompanyId, kid, 1, new BomExplosionOptions { ConvertToBaseUoM = true }, ct);
				var ids = bom.Lines.Select(l => l.ComponentItemId).Distinct().ToList();
				var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == ctx.CompanyId).ToDictionaryAsync(i => i.ID, ct);
				var onHand = store is int sw
					? await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == sw && ids.Contains(b.ItemId)).GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct)
					: new Dictionary<int, decimal>();
				var lines = bom.Lines.Where(l => items.ContainsKey(l.ComponentItemId)).Select(l => new HospitalKitLine(l.ComponentItemId, items[l.ComponentItemId].ItemCode, items[l.ComponentItemId].Name,
					items[l.ComponentItemId].NameEn ?? items[l.ComponentItemId].Name, l.Quantity, onHand.GetValueOrDefault(l.ComponentItemId))).ToList();
				return new HospitalKitView(kid, kit?.Name, kit?.NameEn ?? kit?.Name, lines, store, storeName, s.KitIssuedAt, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 208 or 207) { return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), null, null, null, false); }
		}

		/// The composite inventory items a procedure may use as its kit (a bundle with a bill of materials).
		public async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn)>> KitItemsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var withBom = _db.ItemComponents.Where(c => c.CompanyID == ctx.CompanyId).Select(c => c.ParentItemId);
			var rows = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && i.IsComposite && withBom.Contains(i.ID))
				.OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, NameEn = i.NameEn ?? i.Name }).ToListAsync(ct);
			return rows.Select(r => (r.ID, r.ItemCode, r.Name, r.NameEn)).ToList();
		}

		/// A procedure's kit (`manage`); null clears it.
		public async Task<HospitalResult> SetKitAsync(BusinessContext ctx, int serviceId, int? kitItemId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == serviceId && s.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (kitItemId is int k && !(await KitItemsAsync(ctx, ct)).Any(x => x.Id == k)) return HospitalResult.Fail("Choose a kit item that has a bill of materials");
			try
			{
				var row = await _db.Set<HospitalProcedureKit>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ServiceId == serviceId, ct);
				if (kitItemId is not int kit) { if (row != null) _db.Remove(row); }
				else
				{
					row ??= _db.Set<HospitalProcedureKit>().Add(new HospitalProcedureKit { CompanyId = ctx.CompanyId, ServiceId = serviceId }).Entity;
					row.KitItemId = kit; row.UpdatedAt = nowUtc;
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(serviceId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The theatre store (`manage`).
		public async Task<HospitalResult> SetStoreAsync(BusinessContext ctx, int? warehouseId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (warehouseId is int w && !await _db.Warehouses.AnyAsync(x => x.ID == w && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = await _db.Set<HospitalTheatreSetting>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct)
					?? _db.Set<HospitalTheatreSetting>().Add(new HospitalTheatreSetting { CompanyId = ctx.CompanyId }).Entity;
				row.StoreWarehouseId = warehouseId; row.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The closed case's kit leaves the theatre store: every component, or none (one transaction), once.
		/// `consult` (who closes the case) or `pharmacy` / `manage` (the store) may issue it.
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, int surgeryId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Pharmacy, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var s = await _db.Set<HospitalSurgery>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			var kit = await KitAsync(ctx, s, ct);
			if (!kit.Ready) return HospitalResult.Fail(NoTables);
			if (HospitalTheatreKitRules.IssueRefusal(s.Status, s.KitIssuedAt, kit.KitItemId != null, kit.StoreWarehouseId != null, kit.Lines.Count) is string why) return HospitalResult.Fail(why);
			var ids = kit.Lines.Select(l => l.ItemId).ToList();
			var cats = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == ctx.CompanyId).ToDictionaryAsync(i => i.ID, i => i.ItemCategoryId, ct);
			var catIds = cats.Values.Distinct().ToList();
			var cogs = await _db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CogsAccountId, ct);
			if (kit.Lines.FirstOrDefault(l => cogs.GetValueOrDefault(cats[l.ItemId]) == null) is { } noCogs)
				return HospitalResult.Fail("Map the cost-of-goods account of {0}'s inventory category first", DisplayName.Of(noCogs.Name, noCogs.NameEn));

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// nothing issues a kit twice: the case is claimed first, under the transaction
			int claimed = await _db.Set<HospitalSurgery>().Where(x => x.ID == surgeryId && x.CompanyId == ctx.CompanyId && x.KitIssuedAt == null)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.KitIssuedAt, nowUtc), ct);
			if (claimed != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This case's kit has already left the store"); }
			int n = 0;
			foreach (var l in kit.Lines)
			{
				var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
				{
					Date = nowUtc, ItemId = l.ItemId, WarehouseId = kit.StoreWarehouseId!.Value, Direction = -1, Qty = HospitalTheatreKitRules.Need(l.Qty, 1),
					SourceType = HospitalTheatreKitRules.MovementSource, SourceId = s.ID, SourceLineId = ++n, PostToGl = true, CounterAccountOverride = cogs[cats[l.ItemId]],
					Notes = "Theatre kit " + s.CaseNo,
				}, userId);
				if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("{0}: {1}", DisplayName.Of(l.Name, l.NameEn), error ?? ""); }
			}
			await tx.CommitAsync();
			return HospitalResult.Success(s.ID);
		}
	}
}
