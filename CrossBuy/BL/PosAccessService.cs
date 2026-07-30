using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// POS-2: resolves and ENFORCES cashier access over the existing BranchUserRole assignments.
	// A user reaches /pos only if their Employee is linked to a branch AND has ≥1 POS role there.
	public class PosLoginContext
	{
		public int EmployeeId { get; set; }
		public string EmployeeName { get; set; } = "";
		public string? EmployeeNameEn { get; set; }
		public string? EmployeePhoto { get; set; }
		public int BranchId { get; set; }
		public string BranchName { get; set; } = "";
		public int CompanyId { get; set; } = 1;   // catalog/accounts company (branches sit under 65–79; POS data under 1)
		public List<string> Roles { get; set; } = new();
	}

	public interface IPosAccessService
	{
		Task<PosLoginContext?> ResolveByUserIdAsync(string userId);   // null → no POS access
		bool CanSell(IEnumerable<string> roles);                      // pay/settle allowed? (cashier/manager)
		bool IsManager(IEnumerable<string> roles);
		bool IsKitchen(IEnumerable<string> roles);                    // RC-3a: can advance KDS line status (kitchen or manager)
		bool IsWaiter(IEnumerable<string> roles);                     // RC-3d: is a waiter
		bool CanOrder(IEnumerable<string> roles);                     // RC-3d: open/add/send/table ops (waiter/cashier/manager — NOT kitchen-only)
		// HM-1-أ: the ONE place that whitelists which activities each cashier lane serves. Both lanes (restaurant + hyper)
		// and every future system read this. Returns (allowed, noActivity) where noActivity = the branch has no activity set.
		(bool allowed, bool noActivity) IsActivityAllowedForLane(string? activityPresetCode, string lane);
	}

	public class PosAccessService : IPosAccessService
	{
		private readonly CrossDbContext _db;
		public PosAccessService(CrossDbContext db) { _db = db; }

		public async Task<PosLoginContext?> ResolveByUserIdAsync(string userId)
		{
			if (string.IsNullOrEmpty(userId)) return null;
			var emp = await _db.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId && e.BranchID != null);
			if (emp == null || emp.BranchID == null) return null;
			int branchId = emp.BranchID.Value;
			var roles = await _db.BranchUserRoles.AsNoTracking()
				.Where(r => r.BranchId == branchId && r.EmployeeId == emp.ID && r.IsActive)
				.Select(r => r.PosRole).Distinct().ToListAsync();
			if (roles.Count == 0) return null;   // assigned no POS role → no access
			var branchName = await _db.Branches.AsNoTracking().Where(b => b.ID == branchId).Select(b => b.Name).FirstOrDefaultAsync() ?? branchId.ToString();
			return new PosLoginContext { EmployeeId = emp.ID, EmployeeName = emp.FullName ?? ("#" + emp.ID), EmployeeNameEn = emp.FullNameEn, EmployeePhoto = string.IsNullOrWhiteSpace(emp.ProfileImage) ? null : emp.ProfileImage, BranchId = branchId, BranchName = branchName, Roles = roles };
		}

		public bool CanSell(IEnumerable<string> roles) => roles.Contains("pos-cashier") || roles.Contains("pos-manager");
		public bool IsManager(IEnumerable<string> roles) => roles.Contains("pos-manager");
		public bool IsKitchen(IEnumerable<string> roles) => roles.Contains("pos-kitchen") || roles.Contains("pos-manager");
		public bool IsWaiter(IEnumerable<string> roles) => roles.Contains("pos-waiter");
		public bool CanOrder(IEnumerable<string> roles) => roles.Contains("pos-waiter") || roles.Contains("pos-cashier") || roles.Contains("pos-manager");

		// HM-1-أ: EXPLICIT WHITELIST (not blacklist), so a new activity/system never leaks in by default.
		//  - restaurant lane serves { Restaurant, Cafe } + no-activity (NULL) branches (backward compat with existing
		//    branches that predate the activity field; they operate as restaurants today).
		//  - hyper lane serves { Hyper } ONLY.
		//  - anything else (e.g. Retail, or a future code) is denied until a lane explicitly claims it.
		public (bool allowed, bool noActivity) IsActivityAllowedForLane(string? activityPresetCode, string lane)
		{
			var c = string.IsNullOrWhiteSpace(activityPresetCode) ? null : activityPresetCode.Trim();
			bool noActivity = c == null;
			if (string.Equals(lane, "hyper", StringComparison.OrdinalIgnoreCase))
				return (c == "Hyper", noActivity);
			// restaurant (default lane)
			return (c == "Restaurant" || c == "Cafe" || noActivity, noActivity);
		}
	}
}
