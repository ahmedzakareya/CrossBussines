using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public interface IWarehouseService
	{
		Task<List<Warehouse>> GetWarehousesAsync(int companyId);
		Task<(bool ok, string? error)> CreateWarehouseAsync(int companyId, Warehouse w, string? userId);
		Task<(bool ok, string? error)> UpdateWarehouseAsync(int companyId, int id, Warehouse w, string? userId);
		// warehouse internal subdivision — Section / Rack (pure locational; never valued)
		Task<List<BinLocation>> GetBinLocationsAsync(int warehouseId);
		// What is actually BEHIND each rack, so the warehouse plan can say something instead of only
		// drawing shapes. Keyed by rack BinLocation.ID; a rack with nothing assigned is simply absent.
		Task<Dictionary<int, RackLoad>> GetRackLoadAsync(int warehouseId, int companyId);
		// nameEn is OPTIONAL and trails the name it belongs beside: a section or rack carries both
		// names or just the Arabic one.
		Task<(bool ok, string? error)> SaveBinLocationAsync(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive, string? nameEn = null);
		Task<(bool ok, string? error)> DeleteBinLocationAsync(int id);
	}

	// ============================================================================================
	// WHAT A RACK HOLDS — and, just as importantly, what this CANNOT say.
	//
	// `Items` is the number of items whose DefaultBinLocationId is this rack. That is real and
	// complete: every ItemWarehouseSetting row in this database carries one.
	//
	// `InStock`/`Qty`/`Value` are the balances of THOSE items — read from StockBalance, which is
	// keyed on (item, warehouse) and NOT on the rack. StockMovement has a BinLocationId column and
	// it is null on every row in this database, so the ledger does not know which rack anything
	// physically moved into. This is therefore the stock of the items ASSIGNED here, not a count of
	// what is standing in the rack, and every caller must label it that way. Promoting it to
	// "occupancy" would be a number somebody reorders against.
	// ============================================================================================
	// `Measured` is the difference between "this rack holds 40" and "the items that live on this
	// rack hold 40 somewhere in the warehouse". Both are useful; presenting the second as the
	// first is the lie this whole feature exists to stop telling, so the flag travels with the
	// numbers rather than being inferred by a caller.
	public sealed record RackLoad(int Items, int InStock, decimal Qty, decimal Value, bool Measured = false);

	public class WarehouseService : IWarehouseService
	{
		private readonly CrossDbContext _context;
		public WarehouseService(CrossDbContext context) { _context = context; }

		public async Task<List<Warehouse>> GetWarehousesAsync(int companyId) =>
			await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == companyId).OrderBy(w => w.Code).ToListAsync();

		public async Task<(bool ok, string? error)> CreateWarehouseAsync(int companyId, Warehouse w, string? userId)
		{
			if (string.IsNullOrWhiteSpace(w.Code) || string.IsNullOrWhiteSpace(w.Name)) return (false, "Code and name are required");
			if (await _context.Warehouses.AnyAsync(x => x.CompanyID == companyId && x.Code == w.Code)) return (false, "That warehouse code is already in use");
			w.CompanyID = companyId; w.IsActive = true; w.CreatedBy = userId; w.CreatedAt = DateTime.UtcNow;
			_context.Warehouses.Add(w);
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> UpdateWarehouseAsync(int companyId, int id, Warehouse w, string? userId)
		{
			var ex = await _context.Warehouses.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (ex == null) return (false, "المخزن غير موجود");
			if (await _context.Warehouses.AnyAsync(x => x.CompanyID == companyId && x.Code == w.Code && x.ID != id)) return (false, "كود المخزن مستخدم من قبل");
			ex.Code = w.Code; ex.Name = w.Name; ex.NameEn = w.NameEn; ex.WarehouseType = w.WarehouseType;
			ex.BranchHierarchicalId = w.BranchHierarchicalId; ex.KeeperEmployeeId = w.KeeperEmployeeId;
			ex.AllowNegativeStock = w.AllowNegativeStock; ex.IsActive = w.IsActive;
			ex.ModifiedBy = userId; ex.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// ---- Section / Rack (BinLocation tree under a warehouse) ----
		//
		// THE COMPANY COMES FROM THE WAREHOUSE, because BinLocation has no CompanyID of its own
		// and does not need one: a section exists only under a warehouse, and Warehouse is a
		// company-filtered pilot entity, so the relationship already answers the question. Adding a
		// column would carry the same fact twice and let the two disagree.
		//
		// WHY THIS READ IS A SECURITY BOUNDARY AND NOT A CONVENIENCE. warehouseId arrives from the
		// request at InventoryController.SaveItemLocations, which writes ItemWarehouseSetting rows
		// keyed on it - and ItemWarehouseSetting has no CompanyID and no query filter, so nothing
		// downstream can catch a foreign warehouse. The section lookup is what stands between the
		// request and that write: a row without a section is skipped, and a row with one cannot
		// reach the write unless its section came out of THIS dictionary. Scoping here closes the
		// path for every caller at once rather than at one call site.
		//
		// The filter honours a legitimate cross-company bypass exactly as every pilot entity does,
		// so system and administrative scopes are unaffected.
		public async Task<List<BinLocation>> GetBinLocationsAsync(int warehouseId) =>
			await _context.BinLocations.AsNoTracking()
				.Where(b => b.WarehouseId == warehouseId
					&& _context.Warehouses.Any(w => w.ID == b.WarehouseId))
				.OrderBy(b => b.Code).ToListAsync();

		// TENANCY, the same way GetBinLocationsAsync does it. ItemWarehouseSetting has no CompanyID
		// and no query filter, so it is scoped through Warehouses — which IS filtered — rather than
		// trusted. StockBalance has a CompanyID but no filter either, so it carries an explicit
		// predicate against the company the caller resolved. Neither is left to a global.
		public async Task<Dictionary<int, RackLoad>> GetRackLoadAsync(int warehouseId, int companyId)
		{
			var rows = await (
				from s in _context.ItemWarehouseSettings.AsNoTracking()
				where s.WarehouseId == warehouseId
				      && s.DefaultBinLocationId != null
				      && _context.Warehouses.Any(w => w.ID == s.WarehouseId)
				join b in _context.StockBalances.AsNoTracking()
						on new { s.ItemId, s.WarehouseId } equals new { b.ItemId, b.WarehouseId } into joined
				from b in joined.DefaultIfEmpty()
				where b == null || b.CompanyID == companyId
				select new
				{
					RackId = s.DefaultBinLocationId!.Value,
					Qty = b == null ? 0m : b.QtyOnHand,
					Value = b == null ? 0m : b.TotalValue,
				}).ToListAsync();

			// ---- WHAT THE RACK ACTUALLY HOLDS, when anybody has told us ------------------------
			//
			// BinStock is the real thing: a quantity recorded against THIS rack by a posted movement.
			// It was empty for the whole history of this database, which is why the assignment figure
			// above had to stand in for it. Now that movements record a location, a rack that has been
			// written to can speak for itself, and only a rack that has not falls back to the proxy.
			//
			// PER RACK, not per warehouse: locations are switched on one rack at a time as goods move
			// through them, so a warehouse spends a long time with some racks measured and some not.
			// A single warehouse-wide switch would make the whole map lie until the last rack caught up.
			var measured = await _context.BinStocks.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.WarehouseId == warehouseId)
				.GroupBy(b => b.BinLocationId)
				.Select(g => new { RackId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Items = g.Count(x => x.QtyOnHand > 0) })
				.ToListAsync();
			var measuredByRack = measured.ToDictionary(m => m.RackId, m => m);

			return rows
				.GroupBy(r => r.RackId)
				.ToDictionary(g => g.Key, g =>
				{
					int assigned = g.Count();
					if (measuredByRack.TryGetValue(g.Key, out var m))
						// The VALUE stays the assigned items’ value: BinStock is quantity only and never carries
						// value or GL, which is the invariant the whole locational dimension is built on.
						return new RackLoad(assigned, Math.Min(m.Items, assigned), m.Qty, g.Sum(r => r.Value), Measured: true);

					return new RackLoad(assigned, g.Count(r => r.Qty > 0), g.Sum(r => r.Qty), g.Sum(r => r.Value));
				});
		}

		public async Task<(bool ok, string? error)> SaveBinLocationAsync(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive, string? nameEn = null)
		{
			if (string.IsNullOrWhiteSpace(code)) return (false, "Code is required");
			locationType = locationType == "Rack" ? "Rack" : (locationType == "Bin" ? "Bin" : "Section");
			if (locationType == "Section") parentId = null;   // sections sit directly under the warehouse
			else
			{
				if (parentId == null) return (false, "A shelf must belong to a section");
				var parent = await _context.BinLocations.AsNoTracking().FirstOrDefaultAsync(b => b.ID == parentId && b.WarehouseId == warehouseId);
				if (parent == null) return (false, "The parent section was not found");
				if (parent.LocationType != "Section") return (false, "A shelf must belong directly to a section (only two levels)");
			}
			if (await _context.BinLocations.AnyAsync(b => b.WarehouseId == warehouseId && b.Code == code && b.ID != id)) return (false, "That code is already used within the same warehouse");

			if (id > 0)
			{
				var ex = await _context.BinLocations.FirstOrDefaultAsync(b => b.ID == id && b.WarehouseId == warehouseId);
				if (ex == null) return (false, "الموقع غير موجود");
				ex.Code = code.Trim(); ex.Name = name?.Trim();
				ex.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
				ex.LocationType = locationType; ex.ParentId = parentId; ex.IsActive = isActive;
			}
			else
			{
				_context.BinLocations.Add(new BinLocation
				{
					WarehouseId = warehouseId, Code = code.Trim(), Name = name?.Trim(),
					NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(),
					LocationType = locationType, ParentId = parentId, IsActive = isActive,
				});
			}
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteBinLocationAsync(int id)
		{
			var ex = await _context.BinLocations.FirstOrDefaultAsync(b => b.ID == id);
			if (ex == null) return (false, "Location not found");
			if (await _context.BinLocations.AnyAsync(b => b.ParentId == id)) return (false, "Cannot delete: this section still has shelves under it");
			if (await _context.ItemWarehouseSettings.AnyAsync(s => s.DefaultSectionId == id || s.DefaultBinLocationId == id)) return (false, "Cannot delete: the location is used as the default location for some items");
			if (await _context.StockMovements.AnyAsync(m => m.BinLocationId == id)) return (false, "Cannot delete: there are movements linked to this location");
			_context.BinLocations.Remove(ex);
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
