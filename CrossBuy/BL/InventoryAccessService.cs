using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.Extensions.Logging;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// Inventory RBAC. Roles: InventoryManager (full), WarehouseKeeper (his branch only),
	/// PurchasingOfficer (PO/receipts), InventoryAuditor (read-only). A user with no inventory role is read-only.
	///
	/// STAGE 1 BATCH A: policy moved to CanAsync(BusinessContext, …). Legacy signatures preserved, so all 74
	/// [InvPerm] usages and the 8 InventoryController call sites are untouched. `const int CompanyId = 1` removed —
	/// it also meant CanUseWarehouseAsync only ever matched warehouses in company 1.
	public interface IInventoryAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);                 // read | doc | purchase | manage
		Task<bool> CanUseWarehouseAsync(int warehouseId);
		Task<string> RoleLabelAsync(bool isAr);
	}

	public class InventoryAccessService : IInventoryAccessService, IModuleAccessService
	{
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		private readonly IBusinessContextAccessor _context;

		// Stage 2A Batch B — B6. The ONE read path for bootstrap policy, plus a logger.
		private readonly CrossBuy.BL.Platform.IBootstrapAccessPolicyReader _policies;
		private readonly ILogger<InventoryAccessService> _log;

		public InventoryAccessService(
			CrossDbContext db, IHttpContextAccessor http, IBusinessContextAccessor context,
			CrossBuy.BL.Platform.IBootstrapAccessPolicyReader policies, ILogger<InventoryAccessService> log)
		{ _db = db; _http = http; _context = context; _policies = policies; _log = log; }

		/// <summary>The action code the warehouse-scope path authorizes. Never-Bootstrap-Open; no seed row.</summary>
		internal const string WarehouseAccessAction = "warehouse-access";

		public string Scope => EntityRegistry.ScopeInventory;

		public IReadOnlyCollection<string> Actions { get; } = new[] { "read", "doc", "purchase", "manage" };

		// ---------------------------------------------------------------------------------------------
		// Canonical, session-free.
		// ---------------------------------------------------------------------------------------------
		public async Task<bool> CanAsync(
			BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
			=> (await DecideAsync(context, action, target, cancellationToken)).IsAllowed;

		// =============================================================================================
		// Stage 2A Batch B — B6 SITE 2: Mechanism A replaced at the MODULE level.
		//
		// WHAT WAS HERE BEFORE:
		//
		//     if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;   // BEFORE the switch
		//
		// It granted `doc`, `purchase` and `manage` — every stock mutation and role assignment — to any
		// authenticated employee of an unconfigured company, because it ran before the action was examined.
		//
		// SCOPE OF THIS CHANGE: the permission SOURCE only. No stock query, cost filter or row-level rule
		// changed. Inventory.read still exposes stock costs with no branch or warehouse filter — recorded as an
		// unresolved business decision, deliberately NOT addressed here.
		// =============================================================================================
		public async Task<AuthorizationDecision> DecideAsync(
			BusinessContext context, string action, PermissionTarget? target = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			int companyId = context.CompanyId;

			if (companyId <= 0)
				return Log(AuthorizationDecision.Deny(
					companyId, Scope, action ?? "", AuthorizationReasonCodes.CompanyUnresolved));

			if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action))
				return Log(AuthorizationDecision.Deny(
					companyId, Scope, action ?? "", AuthorizationReasonCodes.UnknownAction));

			// A TARGET NAMING ANOTHER COMPANY IS REFUSED BEFORE ANY ROLE IS READ.
			//
			// ModuleAccessServiceBase applies this gate for the services that derive from it. This one
			// implements IModuleAccessService directly - the Accounting and CRM shape - so the gate has to be
			// stated here or it is simply absent. PlatformPermissionProvider also checks it, but that only
			// covers callers arriving through the platform: a direct caller passing a foreign WarehouseId
			// would otherwise be answered by an InventoryManager grant that never looks at the warehouse row.
			if (target?.CompanyId is > 0 && target.CompanyId.Value != companyId)
				return Log(AuthorizationDecision.Deny(
					companyId, Scope, action, AuthorizationReasonCodes.CompanyMismatch));

			// ---- configured roles decide first: a configured company behaves EXACTLY as before ----
			if (await AnyRoleConfiguredAsync(companyId, cancellationToken))
			{
				var roles = await RolesAsync(context, cancellationToken);
				bool mgr = roles.Contains("InventoryManager");
				bool keeper = roles.Contains("WarehouseKeeper");
				bool purch = roles.Contains("PurchasingOfficer");

				bool allowed = action switch
				{
					"read" => true,                       // any authenticated user in this company may view
					"manage" => mgr,                      // master data / settings / roles
					"doc" => mgr || keeper,               // stock documents (movement/transfer/count/assembly/landed/sales)
					"purchase" => mgr || purch,           // purchase orders / receipts
					_ => false
				};

				if (!allowed)
					return Log(AuthorizationDecision.Deny(
						companyId, Scope, action, AuthorizationReasonCodes.RoleNotHeld));

				// Row-level: when the caller names a warehouse, the keeper's branch scope applies ON TOP of the
				// action grant. Unchanged by B6 — the branch/warehouse exactness is the property this must not
				// widen, and it is asserted by its own tests.
				if (target?.WarehouseId is > 0 && action is "doc" or "purchase")
				{
					bool inScope = await CanUseWarehouseAsync(context, target.WarehouseId.Value, cancellationToken);
					return Log(inScope
						? AuthorizationDecision.Allow(
							AuthorizationDecisionSources.LegacyRole, companyId, Scope, action,
							AuthorizationReasonCodes.RoleHeld, roles, branchId: target.WarehouseId)
						: AuthorizationDecision.Deny(
							companyId, Scope, action, AuthorizationReasonCodes.RoleNotHeld, target.WarehouseId));
				}

				return Log(AuthorizationDecision.Allow(
					AuthorizationDecisionSources.LegacyRole, companyId, Scope, action,
					AuthorizationReasonCodes.RoleHeld, roles));
			}

			// ---- no role configured: the action decides. doc/purchase/manage are Never; read needs a policy. ----
			return Log(await _policies.ResolveDecisionAsync(context, Scope, action, cancellationToken));
		}

		/// <summary>Structured, safe. Carries provenance and NO stock cost, quantity or valuation.</summary>
		private AuthorizationDecision Log(AuthorizationDecision decision)
		{
			if (decision.IsBootstrap)
				_log.LogInformation(
					"Inventory authorization: company {Company} action {Action} allowed={Allowed} source={Source} " +
					"reason={Reason} policy={PolicyId} branch={Branch} — COMPATIBILITY, not role authorization.",
					decision.CompanyID, decision.Action, decision.IsAllowed, decision.DecisionSource,
					decision.ReasonCode, decision.BootstrapPolicyId, decision.ScopeBranchId);
			else
				_log.LogDebug(
					"Inventory authorization: company {Company} action {Action} allowed={Allowed} source={Source} " +
					"reason={Reason} branch={Branch}.",
					decision.CompanyID, decision.Action, decision.IsAllowed, decision.DecisionSource,
					decision.ReasonCode, decision.ScopeBranchId);

			return decision;
		}

		public async Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (context.EmployeeId is not > 0) return Array.Empty<string>();
			return await _db.InventoryUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == context.CompanyId && r.EmployeeId == context.EmployeeId.Value)
				.Select(r => r.Role)
				.ToListAsync(cancellationToken);
		}

		/// Session-free warehouse scope check. The warehouse must belong to the CONTEXT's company — a keeper
		/// cannot reach a warehouse in another company even if a branch id happened to match.
		public async Task<bool> CanUseWarehouseAsync(
			BusinessContext context, int warehouseId, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			// =========================================================================================
			// Stage 2A Batch B — B6 SITE 3: the warehouse-scope bootstrap, CLOSED. This conversion is
			// deliberately NARROWING and is approved as such.
			//
			// WHAT WAS HERE BEFORE:
			//
			//     if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;  // "not configured yet → open"
			//
			// An unconfigured company received access to EVERY warehouse — the exact opposite of what this path
			// exists to enforce. `warehouse-access` is Never-Bootstrap-Open precisely because a compatibility
			// allowance here does not merely open a read: it WIDENS an exact branch/warehouse restriction to
			// company scope, which is the one thing the keeper scope is for.
			//
			// So there is no seed row for it, and no policy state can open it. The reader is asked anyway rather
			// than hard-coding a denial, so the refusal carries a decision source and reason code like every
			// other decision — and so a future decision to introduce a warehouse policy would flow through the
			// same path instead of needing this branch rewritten.
			// =========================================================================================
			if (!await AnyRoleConfiguredAsync(context.CompanyId, cancellationToken))
			{
				var decision = await _policies.ResolveDecisionAsync(
					context, Scope, WarehouseAccessAction, cancellationToken);

				_log.LogInformation(
					"Inventory warehouse scope: company {Company} warehouse {Warehouse} allowed={Allowed} " +
					"source={Source} reason={Reason} — no inventory role configured; warehouse access is " +
					"Never-Bootstrap-Open and cannot be opened by policy.",
					context.CompanyId, warehouseId, decision.IsAllowed,
					decision.DecisionSource, decision.ReasonCode);

				return decision.IsAllowed;
			}

			if (context.EmployeeId is not > 0) return false;

			var roles = await _db.InventoryUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == context.CompanyId && r.EmployeeId == context.EmployeeId.Value)
				.Select(r => new { r.Role, r.ScopeBranchId })
				.ToListAsync(cancellationToken);

			if (roles.Any(r => r.Role == "InventoryManager")) return true;

			var keeperScopes = roles.Where(r => r.Role == "WarehouseKeeper").Select(r => r.ScopeBranchId).ToList();
			if (keeperScopes.Count == 0) return false;            // not a keeper → no doc rights anyway
			if (keeperScopes.Any(s => s == null)) return true;    // unscoped keeper

			var wh = await _db.Warehouses.AsNoTracking()
				.FirstOrDefaultAsync(w => w.ID == warehouseId && w.CompanyID == context.CompanyId, cancellationToken);
			return wh != null && keeperScopes.Contains(wh.BranchHierarchicalId);
		}

		// RBAC is dormant until at least one role is assigned in this company (avoids lockout / lets you configure it)
		private Task<bool> AnyRoleConfiguredAsync(int companyId, CancellationToken cancellationToken)
			=> _db.InventoryUserRoles.AnyAsync(r => r.CompanyID == companyId, cancellationToken);

		// ---------------------------------------------------------------------------------------------
		// Legacy surface — signatures unchanged.
		// ---------------------------------------------------------------------------------------------

		/// Legacy, synchronous, session-based. No longer part of the permission path — see the note in
		/// AccountingAccessService.CurrentEmployeeId.
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
			if (context == null) return false;   // unresolved identity ⇒ deny (was: evaluated against company 1)
			return await CanAsync(context, action);
		}

		public async Task<bool> CanUseWarehouseAsync(int warehouseId)
		{
			var context = await _context.TryGetCurrentAsync();
			if (context == null) return false;
			return await CanUseWarehouseAsync(context, warehouseId);
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
			return string.Join(", ", roles.Distinct().Select(name));
		}
	}
}