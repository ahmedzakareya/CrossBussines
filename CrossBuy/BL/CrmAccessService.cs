using Microsoft.Extensions.Logging;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// CRM RBAC + data scope. Roles: SalesManager (team, manage), SalesRep (own records),
	/// Marketing (leads/campaigns), CrmViewer (read-only). Dormant until ≥1 CRM role is assigned (bootstrap-open).
	///
	/// STAGE 1 BATCH A: policy moved to CanAsync(BusinessContext, …). Legacy signatures preserved, so all 37
	/// [CrossBuy.Models.CrmPerm] usages and CrmController's VisibleOwnerIdsAsync call are untouched.
	/// `const int CompanyId = 1` removed — it also scoped record OWNERSHIP to company 1's role rows.
	///
	/// This module is the only one with a real record-owner rule, so it is the one where PermissionTarget
	/// earns its place: CanAsync(context, action, PermissionTarget.ForOwner(ownerId)) answers "may this
	/// employee touch a record owned by that employee" — the question the Business Workspace and AI Context
	/// will ask for every CRM record.
	public interface ICrmAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);                 // read | edit | manage
		/// Owner-id whitelist the current user may SEE; null = unrestricted (viewer/marketing/unconfigured).
		Task<HashSet<int>?> VisibleOwnerIdsAsync();
		/// A manager's team = self + ALL subordinate employees in the full Hierarchicals subtree (every level/branch),
		/// INTERSECTED with the manager's own company (Stage 1 Batch C.1 — see TeamOwnerIdsAsync).
		Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId);
		Task<string> RoleLabelAsync(bool isAr);
	}

	public class CrmAccessService : ICrmAccessService, IModuleAccessService
	{
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		private readonly IBusinessContextAccessor _context;
		private readonly IOrgHierarchy _org;

		// The platform bootstrap authority, exactly as AccountingAccessService consumes it. This service
		// does NOT query BootstrapAccessPolicies itself, for the same reason it does not query role rows
		// itself: one reader evaluates NeverBootstrapOpen, expiry and company scope, and a second copy of
		// that logic here is a second place for it to be wrong.
		private readonly CrossBuy.BL.Platform.IBootstrapAccessPolicyReader _policies;
		private readonly ILogger<CrmAccessService> _log;

		public CrmAccessService(
			CrossDbContext db, IHttpContextAccessor http, IBusinessContextAccessor context, IOrgHierarchy org,
			CrossBuy.BL.Platform.IBootstrapAccessPolicyReader policies, ILogger<CrmAccessService> log)
		{ _db = db; _http = http; _context = context; _org = org; _policies = policies; _log = log; }

		public string Scope => EntityRegistry.ScopeCrm;

		public IReadOnlyCollection<string> Actions { get; } = new[] { "read", "edit", "manage" };

		// ---------------------------------------------------------------------------------------------
		// Canonical, session-free.
		// ---------------------------------------------------------------------------------------------
		public async Task<bool> CanAsync(
			BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action)) return false;

			// A COMPANY WITH ROLES CONFIGURED BEHAVES EXACTLY AS BEFORE — the roles decide.
			if (!await AnyRoleConfiguredAsync(context.CompanyId, cancellationToken))
			{
				// A COMPANY WITH NO ROLES DOES NOT BECOME OPEN.
				//
				// This used to `return true`, which meant "nobody has configured CRM yet" was indistinguishable
				// from "everybody may do everything", and it read the ABSENCE of authority as authority. The
				// action is now decided by the platform reader, which evaluates NeverBootstrapOpen FIRST - so
				// Crm/manage can never be bootstrap-open however the table is populated - then requires an
				// explicit, active, in-date policy row for THIS company, THIS scope and THIS action.
				//
				// No policy, an expired one, or one written for another company: refused.
				var decision = await _policies.ResolveDecisionAsync(context, Scope, action, cancellationToken);

				if (decision.IsBootstrap)
					_log.LogInformation(
						"CRM authorization: company {Company} action {Action} allowed={Allowed} source={Source} " +
						"reason={Reason} policy={PolicyId} — COMPATIBILITY, not role authorization.",
						decision.CompanyID, decision.Action, decision.IsAllowed,
						decision.DecisionSource, decision.ReasonCode, decision.BootstrapPolicyId);

				if (!decision.IsAllowed) return false;

				// Authority resolved without roles, so it cannot say WHOSE records this caller may see.
				// Own records only - never the unrestricted set, because widening on an unresolved authority
				// is the failure mode this whole change exists to remove.
				if (target?.OwnerEmployeeId is > 0 && target.OwnerEmployeeId != context.EmployeeId) return false;
				return true;
			}

			var roles = await RolesAsync(context, cancellationToken);
			bool mgr = roles.Contains("SalesManager");
			bool rep = roles.Contains("SalesRep");
			bool mkt = roles.Contains("Marketing");

			bool allowed = action switch
			{
				// Read stays open WITHIN a company that has configured CRM - any authenticated employee of
				// that company may view, which is the Accounting precedent. It is no longer unconditional:
				// an unconfigured company never reaches this switch at all.
				"read" => true,
				"edit" => mgr || rep || mkt,        // CrmViewer (or no role) → read-only
				"manage" => mgr,                    // CRM role assignment, pipeline config
				_ => false
			};
			if (!allowed) return false;

			// Row-level ownership, applied on top of the action grant when the caller names an owner.
			if (target?.OwnerEmployeeId is > 0)
			{
				var visible = await VisibleOwnerIdsAsync(context, cancellationToken);
				if (visible != null && !visible.Contains(target.OwnerEmployeeId.Value)) return false;
			}
			return true;
		}

		public async Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (context.EmployeeId is not > 0) return Array.Empty<string>();
			return await _db.CrmUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == context.CompanyId && r.EmployeeId == context.EmployeeId.Value)
				.Select(r => r.Role)
				.ToListAsync(cancellationToken);
		}

		/// Session-free owner scope. null = unrestricted.
		public async Task<HashSet<int>?> VisibleOwnerIdsAsync(BusinessContext context, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			// NOT null. null means "unrestricted", and an unconfigured company is precisely the case where
			// this service cannot establish whose records the caller may see. Own records only; a caller
			// with no resolved employee sees nothing.
			if (!await AnyRoleConfiguredAsync(context.CompanyId, cancellationToken))
				return context.EmployeeId is > 0 ? new HashSet<int> { context.EmployeeId.Value } : new HashSet<int>();

			var roles = await RolesAsync(context, cancellationToken);

			// SalesManager → own + full team subtree (all levels/branches), intersected with THIS context's company
			if (roles.Contains("SalesManager"))
				return context.EmployeeId is > 0
					? await TeamOwnerIdsAsync(context.CompanyId, context.EmployeeId.Value, cancellationToken)
					: new HashSet<int>();

			// Marketing / CrmViewer → see all (unchanged)
			if (roles.Contains("Marketing") || roles.Contains("CrmViewer")) return null;

			// SalesRep (or unknown) → only their own records
			return context.EmployeeId is > 0 ? new HashSet<int> { context.EmployeeId.Value } : new HashSet<int>();
		}

		private Task<bool> AnyRoleConfiguredAsync(int companyId, CancellationToken cancellationToken)
			=> _db.CrmUserRoles.AnyAsync(r => r.CompanyID == companyId, cancellationToken);

		// self + every subordinate employee in the FULL subtree beneath the manager's org node, INTERSECTED
		// with `companyId`.
		//
		// STAGE 1 BATCH C.1 — THE LEAK THIS CLOSES. The walk used to end at the `team.Add` above and return
		// whatever the tree contained. `Hierarchical` carries NO CompanyID column (it is one shared org tree,
		// which is why Batch B's global filters deliberately skip it), so on a multi-company install any node
		// grafted under this manager's subtree — another company's employee, a shared position node with
		// children from two companies — became a "team member". `VisibleOwnerIdsAsync` then handed that id set
		// to CRM as an owner whitelist, and a SalesManager read leads, opportunities and accounts OWNED BY
		// ANOTHER COMPANY'S employees. Nothing else in the CRM path re-checked the owner's company, so the
		// tree was the whole control.
		//
		// The walk is not reimplemented here: `IOrgHierarchy` already performs exactly this traversal with the
		// same cycle guard, intersects the result against Employee.EmpCompanyID, and LOGS a warning naming the
		// count it dropped — so a mis-grafted tree becomes an operational signal instead of a silent widening.
		// This method is now the CRM-shaped view of that one implementation.
		public async Task<HashSet<int>> TeamOwnerIdsAsync(
			int companyId, int managerEmployeeId, CancellationToken cancellationToken = default)
		{
			if (companyId <= 0 || managerEmployeeId <= 0) return new HashSet<int>();
			var team = await _org.DirectAndIndirectReportsAsync(companyId, managerEmployeeId, cancellationToken);
			return team.ToHashSet();
		}

		// The company-less overload the legacy interface exposes. The company is resolved from the manager's
		// OWN Employee row — never assumed, and never defaulted to 1: an employee row with no company yields
		// an empty set (see nothing), because "which company is this manager in" has no safe guess.
		public async Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId, CancellationToken cancellationToken = default)
		{
			if (managerEmployeeId <= 0) return new HashSet<int>();
			var companyId = await _db.Employee.AsNoTracking()
				.Where(e => e.ID == managerEmployeeId)
				.Select(e => (int?)e.EmpCompanyID)
				.FirstOrDefaultAsync(cancellationToken);
			if (companyId is not > 0) return new HashSet<int>();
			return await TeamOwnerIdsAsync(companyId.Value, managerEmployeeId, cancellationToken);
		}

		// ---------------------------------------------------------------------------------------------
		// Legacy surface — signatures unchanged.
		// ---------------------------------------------------------------------------------------------

		/// Legacy, synchronous, session-based. No longer part of the permission path.
		public int? CurrentEmployeeId()
		{
			var json = _http.HttpContext?.Session.GetString("Employee");
			if (string.IsNullOrEmpty(json)) return null;
			try { return JsonSerializer.Deserialize<EmployeeViewModel>(json)?.ID; } catch { return null; }
		}

		public async Task<List<string>> MyRolesAsync()
		{
			var context = await _context.TryGetCurrentAsync();
			if (context == null) return new();
			return (await RolesAsync(context)).ToList();
		}

		public async Task<bool> CanAsync(string action)
		{
			var context = await _context.TryGetCurrentAsync();
			if (context == null) return false;   // unresolved identity ⇒ deny
			return await CanAsync(context, action);
		}

		public async Task<HashSet<int>?> VisibleOwnerIdsAsync()
		{
			var context = await _context.TryGetCurrentAsync();
			// An unresolved identity sees NOTHING, rather than the "null = unrestricted" answer an
			// unresolved caller used to get by way of the company-1 context.
			if (context == null) return new HashSet<int>();
			return await VisibleOwnerIdsAsync(context);
		}

		public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId)
			=> TeamOwnerIdsAsync(managerEmployeeId, CancellationToken.None);

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
			return string.Join(", ", roles.Select(name));
		}
	}
}