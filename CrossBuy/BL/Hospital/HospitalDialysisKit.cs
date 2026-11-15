using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H122 — a dialysis session's consumables leave the dialysis unit's store. The unit names its kit (a bundle:
	// the dialyser, the blood lines, the needles, the saline, the heparin…) and its store; a finished session draws one
	// kit through HospitalKitStock (the bill of materials, the inventory's single stock writer, cost of goods), once
	// (KitIssuedAt). A short store never undoes the session: it stays "not issued" with the reason, and the kit is issued
	// from the session's page once the store is stocked. The consumables are not charged — the session's price (H119)
	// covers them; the stock and its cost are what was missing.
	// ==========================================================================================
	public static class HospitalDialysisKitRules
	{
		public const string MovementSource = "HospitalDialysis";

		/// Why this session's kit cannot leave the store now, or null.
		public static string? IssueRefusal(string sessionStatus, DateTime? issuedAt, bool hasKit, bool hasStore, int lines)
		{
			if (issuedAt != null) return "This session's kit has already left the store";
			if (sessionStatus != HospitalDialysisSessionStatus.Done) return "The kit is issued when the session is finished";
			if (!hasKit) return "The dialysis unit has no kit set";
			if (!hasStore) return "Choose the dialysis unit's store first";
			if (lines == 0) return "The kit item has no components (Inventory › the item's BOM)";
			return null;
		}
	}

	public sealed class HospitalDialysisKitService
	{
		public const string NoTables = "The unit kit tables are not created yet (hospital_h122_unit_kits.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IBomExplosionService _bom;
		private readonly IStockService _stock;

		public HospitalDialysisKitService(CrossDbContext db, IHospitalAccessService access, IBomExplosionService bom, IStockService stock)
		{ _db = db; _access = access; _bom = bom; _stock = stock; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		private Task<HospitalUnitKit?> SettingAsync(BusinessContext ctx, CancellationToken ct) =>
			_db.Set<HospitalUnitKit>().AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == ctx.CompanyId && k.Unit == HospitalKitUnits.Dialysis, ct);

		/// The unit's kit as a session's page shows it, with what the store holds and whether this session's kit left.
		public async Task<HospitalKitView> KitAsync(BusinessContext ctx, DateTime? issuedAt, CancellationToken ct = default)
		{
			try
			{
				var set = await SettingAsync(ctx, ct);
				var store = set?.StoreWarehouseId;
				var storeName = await HospitalKitStock.StoreNameAsync(_db, ctx.CompanyId, store, ct);
				if (set?.KitItemId is not int kid) return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), store, storeName, issuedAt, true);
				var kit = await _db.Items.AsNoTracking().Where(i => i.ID == kid && i.CompanyID == ctx.CompanyId).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
				var lines = await HospitalKitStock.LinesAsync(_db, _bom, ctx.CompanyId, kid, store, ct);
				return new HospitalKitView(kid, kit?.Name, kit?.NameEn ?? kit?.Name, lines, store, storeName, issuedAt, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), null, null, null, false); }
		}

		public Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn)>> KitItemsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> HospitalKitStock.KitItemsAsync(_db, ctx.CompanyId, ct);

		/// The unit's kit and store (`manage`); a null clears that one.
		public async Task<HospitalResult> SetAsync(BusinessContext ctx, int? kitItemId, int? warehouseId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (kitItemId is int k && !(await KitItemsAsync(ctx, ct)).Any(x => x.Id == k)) return HospitalResult.Fail("Choose a kit item that has a bill of materials");
			if (warehouseId is int w && !await _db.Warehouses.AnyAsync(x => x.ID == w && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = await _db.Set<HospitalUnitKit>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.Unit == HospitalKitUnits.Dialysis, ct)
					?? _db.Set<HospitalUnitKit>().Add(new HospitalUnitKit { CompanyId = ctx.CompanyId, Unit = HospitalKitUnits.Dialysis }).Entity;
				row.KitItemId = kitItemId; row.StoreWarehouseId = warehouseId; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// A finished session's kit leaves the dialysis store: every component, or none (one transaction), once.
		/// Who treats (`admit` / `consult`) or the store's keepers (`pharmacy` / `manage`) may issue it.
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, int sessionId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct) && !await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Pharmacy, ct) && !await Can(ctx, HospitalActions.Manage, ct))
				return HospitalResult.Fail("forbidden");
			try
			{
				var s = await _db.Set<HospitalDialysisSession>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == sessionId && x.CompanyId == ctx.CompanyId, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				var kit = await KitAsync(ctx, s.KitIssuedAt, ct);
				if (!kit.Ready) return HospitalResult.Fail(NoTables);
				if (HospitalDialysisKitRules.IssueRefusal(s.Status, s.KitIssuedAt, kit.KitItemId != null, kit.StoreWarehouseId != null, kit.Lines.Count) is string why) return HospitalResult.Fail(why);
				var (noCogs, cogs) = await HospitalKitStock.CostAccountsAsync(_db, ctx.CompanyId, kit.Lines, ct);
				if (noCogs != null) return noCogs;
				if (HospitalKitStock.ShortRefusal(kit.Lines, 1) is { } shortOf) return shortOf;

				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				// nothing issues a kit twice: the session is claimed first, under the transaction
				int claimed = await _db.Set<HospitalDialysisSession>().Where(x => x.ID == sessionId && x.CompanyId == ctx.CompanyId && x.KitIssuedAt == null && x.Status == HospitalDialysisSessionStatus.Done)
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.KitIssuedAt, nowUtc), ct);
				if (claimed != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This session's kit has already left the store"); }
				if (await HospitalKitStock.PostAsync(_stock, ctx.CompanyId, kit.Lines, cogs, kit.StoreWarehouseId!.Value, 1, HospitalDialysisKitRules.MovementSource, s.ID, "Dialysis kit #" + s.ID, userId, nowUtc) is { } refused)
				{ await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return refused; }
				await tx.CommitAsync();
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
