using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H122/H130 — a treatment session's consumables leave its unit's store. Each unit (the dialysis unit, the
	// oncology day unit, physiotherapy) names its kit — a bundle whose bill of materials lists one session's consumables —
	// and its store (H123 settings). A completed session (a dialysis session finished, a chemotherapy cycle given, a
	// physiotherapy session attended) draws one kit through HospitalKitStock (the bill of materials, the inventory's single
	// stock writer, cost of goods), once (KitIssuedAt). A short store never undoes the session: it stays "not issued" with
	// the reason, and the kit is issued from its page once the store is stocked. The consumables are not charged — the
	// session's price (H119/H123) covers them; the stock and its cost are what was missing.
	// ==========================================================================================
	/// The kit card a page shows: the unit's kit with what its store holds; for one session (SessionId), whether its kit left.
	public sealed record HospitalSessionKitCard(string Unit, HospitalKitView Kit, int? SessionId, bool Completed, bool CanManage);

	/// One session's kit in a list row: left (when), can leave now, or not yet.
	public sealed record HospitalSessionKitCell(string Unit, int Id, DateTime? IssuedAt, bool Completed, bool HasKit);

	public static class HospitalSessionKitRules
	{
		/// The stock movements' source per unit (each found again by its session).
		public static string MovementSource(string unit) => unit switch
		{
			HospitalKitUnits.Dialysis => "HospitalDialysis",
			HospitalKitUnits.Chemo => "HospitalChemo",
			HospitalKitUnits.Physio => "HospitalRehab",
			_ => throw new ArgumentOutOfRangeException(nameof(unit)),
		};

		/// The unit's own page (its settings live there, at #unit-settings).
		public static string UnitAction(string unit) => unit switch { HospitalKitUnits.Chemo => "Oncology", HospitalKitUnits.Physio => "Rehab", _ => "Dialysis" };

		/// Why this session's kit cannot leave the store now, or null. `completed` = finished / given / attended.
		public static string? IssueRefusal(bool completed, DateTime? issuedAt, bool hasKit, bool hasStore, int lines)
		{
			if (issuedAt != null) return "This session's kit has already left the store";
			if (!completed) return "The kit is issued when the session is finished";
			if (!hasKit) return "The unit has no kit set";
			if (!hasStore) return "Choose the unit's store first";
			if (lines == 0) return "The kit item has no components (Inventory › the item's BOM)";
			return null;
		}
	}

	public sealed class HospitalSessionKitService
	{
		public const string NoTables = "The unit kit tables are not created yet (hospital_h122_unit_kits.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IBomExplosionService _bom;
		private readonly IStockService _stock;

		public HospitalSessionKitService(CrossDbContext db, IHospitalAccessService access, IBomExplosionService bom, IStockService stock)
		{ _db = db; _access = access; _bom = bom; _stock = stock; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The unit's kit as a page shows it, with what the store holds and — for one session — whether its kit left.
		public async Task<HospitalKitView> KitAsync(BusinessContext ctx, string unit, DateTime? issuedAt, CancellationToken ct = default)
		{
			try
			{
				var set = await _db.Set<HospitalUnitKit>().AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == ctx.CompanyId && k.Unit == unit, ct);
				var store = set?.StoreWarehouseId;
				var storeName = await HospitalKitStock.StoreNameAsync(_db, ctx.CompanyId, store, ct);
				if (set?.KitItemId is not int kid) return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), store, storeName, issuedAt, true);
				var kit = await _db.Items.AsNoTracking().Where(i => i.ID == kid && i.CompanyID == ctx.CompanyId).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
				var lines = await HospitalKitStock.LinesAsync(_db, _bom, ctx.CompanyId, kid, store, ct);
				return new HospitalKitView(kid, kit?.Name, kit?.NameEn ?? kit?.Name, lines, store, storeName, issuedAt, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalKitView(null, null, null, Array.Empty<HospitalKitLine>(), null, null, null, false); }
		}

		/// The session as the kit needs it: whether it is completed and when its kit left (null when there is no such session).
		private async Task<(bool Completed, DateTime? IssuedAt)?> SessionAsync(BusinessContext ctx, string unit, int id, CancellationToken ct)
		{
			int cid = ctx.CompanyId;
			return unit switch
			{
				HospitalKitUnits.Dialysis => await _db.Set<HospitalDialysisSession>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == cid)
					.Select(x => new { C = x.Status == HospitalDialysisSessionStatus.Done, x.KitIssuedAt }).FirstOrDefaultAsync(ct) is { } d ? (d.C, d.KitIssuedAt) : null,
				HospitalKitUnits.Chemo => await _db.Set<HospitalChemoCycle>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == cid)
					.Select(x => new { C = x.Status == HospitalChemoCycleStatus.Given, x.KitIssuedAt }).FirstOrDefaultAsync(ct) is { } c ? (c.C, c.KitIssuedAt) : null,
				HospitalKitUnits.Physio => await _db.Set<HospitalRehabSession>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == cid)
					.Select(x => new { C = x.Attendance == HospitalRehabAttendance.Attended, x.KitIssuedAt }).FirstOrDefaultAsync(ct) is { } p ? (p.C, p.KitIssuedAt) : null,
				_ => null,
			};
		}

		/// Nothing issues a kit twice: the completed session is claimed first, under the transaction.
		private Task<int> ClaimAsync(BusinessContext ctx, string unit, int id, DateTime nowUtc, CancellationToken ct)
		{
			int cid = ctx.CompanyId;
			return unit switch
			{
				HospitalKitUnits.Dialysis => _db.Set<HospitalDialysisSession>().Where(x => x.ID == id && x.CompanyId == cid && x.KitIssuedAt == null && x.Status == HospitalDialysisSessionStatus.Done)
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.KitIssuedAt, nowUtc), ct),
				HospitalKitUnits.Chemo => _db.Set<HospitalChemoCycle>().Where(x => x.ID == id && x.CompanyId == cid && x.KitIssuedAt == null && x.Status == HospitalChemoCycleStatus.Given)
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.KitIssuedAt, nowUtc), ct),
				_ => _db.Set<HospitalRehabSession>().Where(x => x.ID == id && x.CompanyId == cid && x.KitIssuedAt == null && x.Attendance == HospitalRehabAttendance.Attended)
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.KitIssuedAt, nowUtc), ct),
			};
		}

		/// A completed session's kit leaves its unit's store: every component, or none (one transaction), once.
		/// Who treats (`admit` / `consult`) or the store's keepers (`pharmacy` / `manage`) may issue it.
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, string unit, int sessionId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!HospitalKitUnits.WithKit.Contains(unit)) return HospitalResult.Fail("not_found");
			if (!await Can(ctx, HospitalActions.Admit, ct) && !await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Pharmacy, ct) && !await Can(ctx, HospitalActions.Manage, ct))
				return HospitalResult.Fail("forbidden");
			try
			{
				if (await SessionAsync(ctx, unit, sessionId, ct) is not { } s) return HospitalResult.Fail("not_found");
				var kit = await KitAsync(ctx, unit, s.IssuedAt, ct);
				if (!kit.Ready) return HospitalResult.Fail(NoTables);
				if (HospitalSessionKitRules.IssueRefusal(s.Completed, s.IssuedAt, kit.KitItemId != null, kit.StoreWarehouseId != null, kit.Lines.Count) is string why) return HospitalResult.Fail(why);
				var (noCogs, cogs) = await HospitalKitStock.CostAccountsAsync(_db, ctx.CompanyId, kit.Lines, ct);
				if (noCogs != null) return noCogs;
				if (HospitalKitStock.ShortRefusal(kit.Lines, 1) is { } shortOf) return shortOf;

				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				if (await ClaimAsync(ctx, unit, sessionId, nowUtc, ct) != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This session's kit has already left the store"); }
				if (await HospitalKitStock.PostAsync(_stock, ctx.CompanyId, kit.Lines, cogs, kit.StoreWarehouseId!.Value, 1, HospitalSessionKitRules.MovementSource(unit), sessionId, unit + " kit #" + sessionId, userId, nowUtc) is { } refused)
				{ await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return refused; }
				await tx.CommitAsync();
				return HospitalResult.Success(sessionId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
