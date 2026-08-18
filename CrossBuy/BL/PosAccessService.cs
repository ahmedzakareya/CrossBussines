using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
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

		// The POS catalog/accounts company. HM-1: POS catalog and cash accounts live under company 1 while
		// branches sit under companies 65–79, so this is NOT the branch's own company and must not be
		// "corrected" to it — doing so would change POS accounting, which Stage 1 explicitly does not touch.
		//
		// STAGE 1 BATCH A: the value is no longer a silent field default. ResolveByUserIdAsync now sets it
		// EXPLICITLY from PosCompanyPolicy.CatalogCompanyId, so the constant has one named home with the
		// reason attached instead of appearing as `= 1` on a DTO. Classified as a deliberate single-company
		// compatibility setting, not a defect.
		public int CompanyId { get; set; } = PosCompanyPolicy.CatalogCompanyId;

		// The company the BRANCH belongs to. Added in Stage 1 because it is the honest tenancy answer for a
		// POS session, and it is what the session blob and the BusinessContext need — as distinct from the
		// catalog company above. Null when the branch has no company row.
		public int? BranchCompanyId { get; set; }

		public List<string> Roles { get; set; } = new();
	}

	// The one place the POS catalog-company constant is stated, with its justification.
	public static class PosCompanyPolicy
	{
		// HM-1 / PosSetupService: POS catalog items and cash/GL accounts are maintained under company 1,
		// while operating branches belong to companies 65–79. Changing this changes POS accounting.
		// Stage 1 contains it; it does not remove it. Removal requires the POS catalog to be made
		// per-company, which is a business change with GL consequences.
		public const int CatalogCompanyId = 1;
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

	public class PosAccessService : IPosAccessService, IModuleAccessService
	{
		private readonly CrossDbContext _db;
		public PosAccessService(CrossDbContext db) { _db = db; }

		public string Scope => EntityRegistry.ScopePos;

		// The POS vocabulary is PREDICATES over assigned roles, not an action table like the other three
		// modules. It is exposed here in the canonical form so the same names are usable from a workflow step
		// or an AI retrieval, without pretending POS has read/post/pay/manage semantics it does not have.
		public IReadOnlyCollection<string> Actions { get; } = new[] { "view", "sell", "order", "kitchen", "manage" };

		// ---------------------------------------------------------------------------------------------
		// Canonical, session-free. POS was ALREADY session-free — it takes a userId — so this method is a
		// re-expression of the existing predicates in the shared contract, not a change of policy.
		// ---------------------------------------------------------------------------------------------
		public async Task<bool> CanAsync(
			BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action)) return false;
			if (string.IsNullOrEmpty(context.UserId)) return false;   // POS identity is the AspNetUser id

			var pos = await ResolveByUserIdAsync(context.UserId);
			if (pos == null) return false;                            // no POS role assigned ⇒ no access

			// Branch isolation: a cashier's rights exist at their OWN branch. When the caller names a branch,
			// it must be that branch.
			if (target?.BranchId is > 0 && target.BranchId.Value != pos.BranchId) return false;

			return action switch
			{
				"view" => true,                        // holding any POS role is enough to view
				"sell" => CanSell(pos.Roles),
				"order" => CanOrder(pos.Roles),
				"kitchen" => IsKitchen(pos.Roles),
				"manage" => IsManager(pos.Roles),
				_ => false,
			};
		}

		public async Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (string.IsNullOrEmpty(context.UserId)) return Array.Empty<string>();
			var pos = await ResolveByUserIdAsync(context.UserId);
			return pos?.Roles ?? (IReadOnlyList<string>)Array.Empty<string>();
		}

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
			var branch = await _db.Branches.AsNoTracking().Where(b => b.ID == branchId)
				.Select(b => new { b.Name, b.CompanyID }).FirstOrDefaultAsync();
			return new PosLoginContext
			{
				EmployeeId = emp.ID,
				EmployeeName = emp.FullName ?? ("#" + emp.ID),
				EmployeeNameEn = emp.FullNameEn,
				EmployeePhoto = string.IsNullOrWhiteSpace(emp.ProfileImage) ? null : emp.ProfileImage,
				BranchId = branchId,
				BranchName = branch?.Name ?? branchId.ToString(),
				// Explicit, with the policy named — see PosCompanyPolicy.
				CompanyId = PosCompanyPolicy.CatalogCompanyId,
				// The branch's real company, so a POS session can state its tenancy honestly.
				BranchCompanyId = branch?.CompanyID,
				Roles = roles,
			};
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
