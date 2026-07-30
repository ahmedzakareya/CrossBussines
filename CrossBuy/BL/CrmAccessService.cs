using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// CRM RBAC + data scope. Roles: SalesManager (team, manage), SalesRep (own records),
	/// Marketing (leads/campaigns), CrmViewer (read-only). Dormant until ≥1 CRM role is assigned (bootstrap-open).
	public interface ICrmAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);                 // read | edit | manage
		/// Owner-id whitelist the current user may SEE; null = unrestricted (viewer/marketing/unconfigured).
		Task<HashSet<int>?> VisibleOwnerIdsAsync();
		/// A manager's team = self + ALL subordinate employees in the full Hierarchicals subtree (every level/branch).
		Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId);
		Task<string> RoleLabelAsync(bool isAr);
	}

	public class CrmAccessService : ICrmAccessService
	{
		private const int CompanyId = 1;
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		public CrmAccessService(CrossDbContext db, IHttpContextAccessor http) { _db = db; _http = http; }

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
			return await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == CompanyId && r.EmployeeId == emp).Select(r => r.Role).ToListAsync();
		}

		private Task<bool> AnyRoleConfiguredAsync() => _db.CrmUserRoles.AnyAsync(r => r.CompanyID == CompanyId);

		public async Task<bool> CanAsync(string action)
		{
			if (!await AnyRoleConfiguredAsync()) return true;   // not configured → open (bootstrap)
			var roles = await MyRolesAsync();
			bool mgr = roles.Contains("SalesManager");
			bool rep = roles.Contains("SalesRep");
			bool mkt = roles.Contains("Marketing");
			return action switch
			{
				"read" => true,
				"edit" => mgr || rep || mkt,        // CrmViewer (or no role) → read-only
				"manage" => mgr,                    // CRM role assignment, pipeline config
				_ => false
			};
		}

		public async Task<HashSet<int>?> VisibleOwnerIdsAsync()
		{
			if (!await AnyRoleConfiguredAsync()) return null;   // open
			var roles = await MyRolesAsync();
			// SalesManager → own + full team subtree (all levels/branches) via Hierarchicals
			if (roles.Contains("SalesManager"))
			{
				var mgr = CurrentEmployeeId();
				return mgr.HasValue ? await TeamOwnerIdsAsync(mgr.Value) : new HashSet<int>();
			}
			// Marketing / CrmViewer → see all (unchanged)
			if (roles.Contains("Marketing") || roles.Contains("CrmViewer")) return null;
			// SalesRep (or unknown) → only their own records
			var me = CurrentEmployeeId();
			return me.HasValue ? new HashSet<int> { me.Value } : new HashSet<int>();
		}

		// self + every subordinate employee in the FULL subtree beneath the manager's org node.
		// Tree convention (same as People/Structure): employee node (H_Type=5) → child position nodes → child employee nodes → …
		public async Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId)
		{
			var team = new HashSet<int> { managerEmployeeId };
			var all = await _db.Hierarchicals.AsNoTracking()
				.Select(h => new { h.H_ID, h.H_Parent, h.H_Type, h.H_ObjectID }).ToListAsync();
			var myNode = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == managerEmployeeId);
			if (myNode == null) return team;   // manager not placed in the org tree → sees only own
			var childrenByParent = all.Where(h => h.H_Parent.HasValue)
				.GroupBy(h => h.H_Parent!.Value).ToDictionary(g => g.Key, g => g.ToList());
			var stack = new Stack<int>(); stack.Push(myNode.H_ID);
			var visited = new HashSet<int>();
			while (stack.Count > 0)
			{
				var nodeId = stack.Pop();
				if (!visited.Add(nodeId)) continue;   // cycle guard
				if (!childrenByParent.TryGetValue(nodeId, out var kids)) continue;
				foreach (var k in kids)
				{
					if (k.H_Type == 5 && k.H_ObjectID.HasValue) team.Add(k.H_ObjectID.Value);
					stack.Push(k.H_ID);
				}
			}
			return team;
		}

		public async Task<string> RoleLabelAsync(bool isAr)
		{
			var roles = await MyRolesAsync();
			if (roles.Count == 0) return isAr ? "قارئ" : "Viewer";
			string name(string r) => r switch
			{
				"SalesManager" => isAr ? "مدير مبيعات" : "Sales manager",
				"SalesRep" => isAr ? "مندوب مبيعات" : "Sales rep",
				"Marketing" => isAr ? "تسويق" : "Marketing",
				"CrmViewer" => isAr ? "قارئ CRM" : "CRM viewer",
				_ => r
			};
			return string.Join("، ", roles.Select(name));
		}
	}
}
