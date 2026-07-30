using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// Inventory RBAC. Roles: InventoryManager (full), WarehouseKeeper (his branch only),
	/// PurchasingOfficer (PO/receipts), InventoryAuditor (read-only). A user with no inventory role is read-only.
	public interface IInventoryAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);                 // read | doc | purchase | manage
		Task<bool> CanUseWarehouseAsync(int warehouseId);
		Task<string> RoleLabelAsync(bool isAr);
	}

	public class InventoryAccessService : IInventoryAccessService
	{
		private const int CompanyId = 1;
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		public InventoryAccessService(CrossDbContext db, IHttpContextAccessor http) { _db = db; _http = http; }

		public int? CurrentEmployeeId()
		{
			var json = _http.HttpContext?.Session.GetString("Employee");
			if (string.IsNullOrEmpty(json)) return null;
			try { return JsonSerializer.Deserialize<EmployeeViewModel>(json)?.ID; } catch { return null; }
		}

		public async Task<List<string>> MyRolesAsync()
		{
			var emp = CurrentEmployeeId();
			if (emp == null) return new();
			return await _db.InventoryUserRoles.AsNoTracking().Where(r => r.CompanyID == CompanyId && r.EmployeeId == emp).Select(r => r.Role).ToListAsync();
		}

		// RBAC is dormant until at least one role is assigned company-wide (avoids lockout / lets you configure it)
		private Task<bool> AnyRoleConfiguredAsync() => _db.InventoryUserRoles.AnyAsync(r => r.CompanyID == CompanyId);

		public async Task<bool> CanAsync(string action)
		{
			if (!await AnyRoleConfiguredAsync()) return true;   // not configured yet → open
			var roles = await MyRolesAsync();
			bool mgr = roles.Contains("InventoryManager");
			bool keeper = roles.Contains("WarehouseKeeper");
			bool purch = roles.Contains("PurchasingOfficer");
			return action switch
			{
				"read" => true,                       // any authenticated user may view
				"manage" => mgr,                      // master data / settings / roles
				"doc" => mgr || keeper,               // stock documents (movement/transfer/count/assembly/landed/sales)
				"purchase" => mgr || purch,           // purchase orders / receipts
				_ => false
			};
		}

		public async Task<bool> CanUseWarehouseAsync(int warehouseId)
		{
			if (!await AnyRoleConfiguredAsync()) return true;   // not configured yet → open
			var emp = CurrentEmployeeId();
			if (emp == null) return false;
			var roles = await _db.InventoryUserRoles.AsNoTracking().Where(r => r.CompanyID == CompanyId && r.EmployeeId == emp).ToListAsync();
			if (roles.Any(r => r.Role == "InventoryManager")) return true;
			var keeperScopes = roles.Where(r => r.Role == "WarehouseKeeper").Select(r => r.ScopeBranchId).ToList();
			if (keeperScopes.Count == 0) return false;            // not a keeper → no doc rights anyway
			if (keeperScopes.Any(s => s == null)) return true;    // unscoped keeper
			var wh = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.ID == warehouseId && w.CompanyID == CompanyId);
			return wh != null && keeperScopes.Contains(wh.BranchHierarchicalId);
		}

		public async Task<string> RoleLabelAsync(bool isAr)
		{
			var roles = await MyRolesAsync();
			if (roles.Count == 0) return isAr ? "قارئ" : "Viewer";
			string name(string r) => r switch
			{
				"InventoryManager" => isAr ? "مدير المخزون" : "Inventory Manager",
				"WarehouseKeeper" => isAr ? "أمين مخزن" : "Warehouse Keeper",
				"PurchasingOfficer" => isAr ? "مسؤول مشتريات" : "Purchasing Officer",
				"InventoryAuditor" => isAr ? "مراجع مخزون" : "Inventory Auditor",
				_ => r
			};
			return string.Join("، ", roles.Distinct().Select(name));
		}
	}
}
