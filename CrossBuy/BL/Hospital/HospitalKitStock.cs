using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H122 — a kit leaving a unit's store, in ONE place. A kit is an inventory bundle whose bill of materials
	// lists the consumables; IBomExplosionService (the ONE place a recipe becomes quantities) explodes it, and each
	// component leaves the unit's store through IStockService.PostMovementAsync (the inventory's single writer), costed to
	// the component category's cost-of-goods account. The theatre's procedure kit (H37) and the dialysis session's kit
	// (H122) both draw through here; each unit keeps only its own "once" claim and its own refusals.
	// ==========================================================================================
	public static class HospitalKitStock
	{
		/// "WH-01 · name" for a store, in the reader's language.
		public static async Task<string?> StoreNameAsync(CrossDbContext db, int companyId, int? store, CancellationToken ct) =>
			store is int w ? await db.Warehouses.AsNoTracking().Where(x => x.ID == w && x.CompanyID == companyId)
				.Select(x => new { x.Code, x.Name, x.NameEn }).FirstOrDefaultAsync(ct) is { } r ? r.Code + " · " + DisplayName.Of(r.Name, r.NameEn ?? r.Name) : null
			: null;

		/// One kit's components, with what the store holds of each.
		public static async Task<IReadOnlyList<HospitalKitLine>> LinesAsync(CrossDbContext db, IBomExplosionService bom, int companyId, int kitItemId, int? store, CancellationToken ct)
		{
			var x = await bom.ExplodeAsync(companyId, kitItemId, 1, new BomExplosionOptions { ConvertToBaseUoM = true }, ct);
			var ids = x.Lines.Select(l => l.ComponentItemId).Distinct().ToList();
			var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == companyId).ToDictionaryAsync(i => i.ID, ct);
			var onHand = store is int sw
				? await db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.WarehouseId == sw && ids.Contains(b.ItemId)).GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(v => v.QtyOnHand) }).ToDictionaryAsync(v => v.Key, v => v.Q, ct)
				: new Dictionary<int, decimal>();
			return x.Lines.Where(l => items.ContainsKey(l.ComponentItemId)).Select(l => new HospitalKitLine(l.ComponentItemId, items[l.ComponentItemId].ItemCode, items[l.ComponentItemId].Name,
				items[l.ComponentItemId].NameEn ?? items[l.ComponentItemId].Name, l.Quantity, onHand.GetValueOrDefault(l.ComponentItemId))).ToList();
		}

		/// The composite inventory items a unit may use as a kit (a bundle with a bill of materials).
		public static async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn)>> KitItemsAsync(CrossDbContext db, int companyId, CancellationToken ct)
		{
			var withBom = db.ItemComponents.Where(c => c.CompanyID == companyId).Select(c => c.ParentItemId);
			var rows = await db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive && i.IsComposite && withBom.Contains(i.ID))
				.OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, NameEn = i.NameEn ?? i.Name }).ToListAsync(ct);
			return rows.Select(r => (r.ID, r.ItemCode, r.Name, r.NameEn)).ToList();
		}

		/// Each component's cost-of-goods account, or the refusal naming the first component whose category has none.
		public static async Task<(HospitalResult? Refusal, IReadOnlyDictionary<int, int> Cogs)> CostAccountsAsync(CrossDbContext db, int companyId, IReadOnlyList<HospitalKitLine> lines, CancellationToken ct)
		{
			var ids = lines.Select(l => l.ItemId).ToList();
			var cats = await db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == companyId).ToDictionaryAsync(i => i.ID, i => i.ItemCategoryId, ct);
			var catIds = cats.Values.Distinct().ToList();
			var cogs = await db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CogsAccountId, ct);
			if (lines.FirstOrDefault(l => !cats.ContainsKey(l.ItemId) || cogs.GetValueOrDefault(cats[l.ItemId]) == null) is { } none)
				return (HospitalResult.Fail("Map the cost-of-goods account of {0}'s inventory category first", DisplayName.Of(none.Name, none.NameEn)), new Dictionary<int, int>());
			return (null, lines.Select(l => l.ItemId).Distinct().ToDictionary(i => i, i => cogs[cats[i]]!.Value));
		}

		/// The first component the store holds too little of, in the reader's language — checked before the stock writer,
		/// whose own refusal is English by design ("Insufficient stock: available 0, required 1", found live).
		public static HospitalResult? ShortRefusal(IReadOnlyList<HospitalKitLine> lines, int kits)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			return lines.FirstOrDefault(l => l.InStore < HospitalTheatreKitRules.Need(l.Qty, kits)) is { } s
				? HospitalResult.Fail("Not enough {0} in the store: {1} of {2}", DisplayName.Of(s.Name, s.NameEn), s.InStore.ToString("0.###", inv), HospitalTheatreKitRules.Need(s.Qty, kits).ToString("0.###", inv))
				: null;
		}

		/// Every component leaves the store, inside the CALLER's transaction (the caller claims "once" first and rolls back
		/// on a refusal). Null when all left; otherwise the refusal naming the component the store could not give.
		public static async Task<HospitalResult?> PostAsync(IStockService stock, int companyId, IReadOnlyList<HospitalKitLine> lines, IReadOnlyDictionary<int, int> cogs, int warehouseId,
			int kits, string sourceType, int sourceId, string notes, string? userId, DateTime nowUtc)
		{
			int n = 0;
			foreach (var l in lines)
			{
				var (ok, error, _) = await stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = nowUtc, ItemId = l.ItemId, WarehouseId = warehouseId, Direction = -1, Qty = HospitalTheatreKitRules.Need(l.Qty, kits),
					SourceType = sourceType, SourceId = sourceId, SourceLineId = ++n, PostToGl = true, CounterAccountOverride = cogs[l.ItemId], Notes = notes,
				}, userId);
				if (!ok) return HospitalResult.Fail("{0}: {1}", DisplayName.Of(l.Name, l.NameEn), error ?? "");
			}
			return null;
		}
	}
}
