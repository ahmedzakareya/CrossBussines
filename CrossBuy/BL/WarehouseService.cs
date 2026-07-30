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
		Task<(bool ok, string? error)> SaveBinLocationAsync(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive);
		Task<(bool ok, string? error)> DeleteBinLocationAsync(int id);
	}

	public class WarehouseService : IWarehouseService
	{
		private readonly CrossDbContext _context;
		public WarehouseService(CrossDbContext context) { _context = context; }

		public async Task<List<Warehouse>> GetWarehousesAsync(int companyId) =>
			await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == companyId).OrderBy(w => w.Code).ToListAsync();

		public async Task<(bool ok, string? error)> CreateWarehouseAsync(int companyId, Warehouse w, string? userId)
		{
			if (string.IsNullOrWhiteSpace(w.Code) || string.IsNullOrWhiteSpace(w.Name)) return (false, "الكود والاسم مطلوبان");
			if (await _context.Warehouses.AnyAsync(x => x.CompanyID == companyId && x.Code == w.Code)) return (false, "كود المخزن مستخدم من قبل");
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
		public async Task<List<BinLocation>> GetBinLocationsAsync(int warehouseId) =>
			await _context.BinLocations.AsNoTracking().Where(b => b.WarehouseId == warehouseId).OrderBy(b => b.Code).ToListAsync();

		public async Task<(bool ok, string? error)> SaveBinLocationAsync(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(code)) return (false, "الكود مطلوب");
			locationType = locationType == "Rack" ? "Rack" : (locationType == "Bin" ? "Bin" : "Section");
			if (locationType == "Section") parentId = null;   // sections sit directly under the warehouse
			else
			{
				if (parentId == null) return (false, "الرف يجب أن يتبع سيكشن");
				var parent = await _context.BinLocations.AsNoTracking().FirstOrDefaultAsync(b => b.ID == parentId && b.WarehouseId == warehouseId);
				if (parent == null) return (false, "السيكشن الأب غير موجود");
				if (parent.LocationType != "Section") return (false, "الرف يجب أن يتبع سيكشن مباشرةً (مستويان فقط)");
			}
			if (await _context.BinLocations.AnyAsync(b => b.WarehouseId == warehouseId && b.Code == code && b.ID != id)) return (false, "الكود مستخدم داخل نفس المخزن");

			if (id > 0)
			{
				var ex = await _context.BinLocations.FirstOrDefaultAsync(b => b.ID == id && b.WarehouseId == warehouseId);
				if (ex == null) return (false, "الموقع غير موجود");
				ex.Code = code.Trim(); ex.Name = name?.Trim(); ex.LocationType = locationType; ex.ParentId = parentId; ex.IsActive = isActive;
			}
			else
			{
				_context.BinLocations.Add(new BinLocation { WarehouseId = warehouseId, Code = code.Trim(), Name = name?.Trim(), LocationType = locationType, ParentId = parentId, IsActive = isActive });
			}
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteBinLocationAsync(int id)
		{
			var ex = await _context.BinLocations.FirstOrDefaultAsync(b => b.ID == id);
			if (ex == null) return (false, "الموقع غير موجود");
			if (await _context.BinLocations.AnyAsync(b => b.ParentId == id)) return (false, "لا يمكن الحذف: توجد رفوف تحت هذا السيكشن");
			if (await _context.ItemWarehouseSettings.AnyAsync(s => s.DefaultSectionId == id || s.DefaultBinLocationId == id)) return (false, "لا يمكن الحذف: الموقع مستخدم كموقع افتراضي لأصناف");
			if (await _context.StockMovements.AnyAsync(m => m.BinLocationId == id)) return (false, "لا يمكن الحذف: توجد حركات مرتبطة بهذا الموقع");
			_context.BinLocations.Remove(ex);
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
