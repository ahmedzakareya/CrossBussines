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
	/// Accounting RBAC. Roles: ChiefAccountant (full + approve + close), Accountant (post journals/invoices),
	/// Cashier (receipts/payments/bank only), Auditor (read-only). No role configured for the company → open (bootstrap).
	///
	/// STAGE 1 BATCH A: the policy moved to CanAsync(BusinessContext, …) — the canonical, session-free method on
	/// IModuleAccessService. Every legacy member below still exists with its original signature and delegates to it,
	/// so [CrossBuy.Models.AccPerm] (55 usages) and the 8 AccountingController call sites are untouched.
	///
	/// Two defects were removed in the process:
	///   * `private const int CompanyId = 1` — roles were read for company 1 whatever company the user belonged to.
	///     On a second company that made the module either wrongly open (company 1 has no roles) or wrongly closed
	///     (company 1 has roles the user does not).
	///   * the employee came from Session["Employee"], so the module could not answer a question about anyone
	///     except the current browser user — which is why the outbox dispatcher's per-recipient check was theatre.
	public interface IAccountingAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);            // read | post | pay | manage | currency-override
		Task<string> RoleLabelAsync(bool isAr);
	}

	// The two period-control actions, named once so a caller cannot misspell a permission into a silent
	// deny. The other five accounting actions predate this and remain bare strings; they are not renamed
	// here because that would touch every existing call site for no behavioural gain.
	public static class AccountingActions
	{
		public const string PeriodClose = "period-close";     // Open -> SoftClosed -> Closed
		public const string PeriodReopen = "period-reopen";   // Closed/SoftClosed -> Open, with a recorded reason
	}

	public class AccountingAccessService : IAccountingAccessService, IModuleAccessService
	{
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		private readonly IBusinessContextAccessor _context;

		// Stage 2A Batch B — B6. The bootstrap policy reader and a logger. The reader is the ONE read path for
		// bootstrap policy; this service does not query BootstrapAccessPolicies itself, for the same reason it
		// does not query PlatformRoleAssignments itself.
		private readonly CrossBuy.BL.Platform.IBootstrapAccessPolicyReader _policies;
		private readonly ILogger<AccountingAccessService> _log;

		public AccountingAccessService(
			CrossDbContext db, IHttpContextAccessor http, IBusinessContextAccessor context,
			CrossBuy.BL.Platform.IBootstrapAccessPolicyReader policies, ILogger<AccountingAccessService> log)
		{ _db = db; _http = http; _context = context; _policies = policies; _log = log; }

		public string Scope => EntityRegistry.ScopeAccounting;

		public IReadOnlyCollection<string> Actions { get; } =
			new[] { "read", "post", "pay", "manage", "currency-override",
				AccountingActions.PeriodClose, AccountingActions.PeriodReopen };

		// ---------------------------------------------------------------------------------------------
		// Canonical, session-free. This is the implementation; everything else delegates here.
		// ---------------------------------------------------------------------------------------------
		public async Task<bool> CanAsync(
			BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
			=> (await DecideAsync(context, action, target, cancellationToken)).IsAllowed;

		// =============================================================================================
		// Stage 2A Batch B — B6 SITE 1: Mechanism A replaced with an action-aware, explicit decision.
		//
		// WHAT WAS HERE BEFORE:
		//
		//     if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;   // BEFORE the switch
		//
		// One line, before the action switch, that granted EVERY accounting action — post, pay, manage,
		// currency-override — to any authenticated employee of a company that had not configured roles yet.
		// It could not exclude an action because it ran before the action was looked at, and nothing in any
		// log distinguished its allow from a real role's allow.
		//
		// WHAT REPLACES IT: the action is classified FIRST, then Never is evaluated, then an explicit policy.
		// Roles are still evaluated first when they exist, so a configured company's behaviour is untouched.
		//
		// SCOPE OF THIS CHANGE: the permission SOURCE only. No query filter, no row-level rule and no returned
		// data changed. Accounting.read still exposes ledger balances with no branch filter — that exposure is
		// recorded as an unresolved business decision in the compatibility-read matrix and is deliberately NOT
		// addressed here.
		// =============================================================================================
		public async Task<AuthorizationDecision> DecideAsync(
			BusinessContext context, string action, PermissionTarget? target = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			int companyId = context.CompanyId;

			// ---- fail closed: no company means no decision. There is no fallback to company 1. ----
			if (companyId <= 0)
				return Log(AuthorizationDecision.Deny(
					companyId, Scope, action ?? "", AuthorizationReasonCodes.CompanyUnresolved));

			// ---- unknown action denies, and never falls through to read ----
			if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action))
				return Log(AuthorizationDecision.Deny(
					companyId, Scope, action ?? "", AuthorizationReasonCodes.UnknownAction));

			// ---- configured roles decide first, so a configured company behaves EXACTLY as before ----
			if (await AnyRoleConfiguredAsync(companyId, cancellationToken))
			{
				var roles = await RolesAsync(context, cancellationToken);
				bool chief = roles.Contains("ChiefAccountant");
				bool acct = roles.Contains("Accountant");
				bool cashier = roles.Contains("Cashier");

				bool allowed = action switch
				{
					"read" => true,                          // any authenticated user in this company may view
					"post" => chief || acct,                 // journals, sales/purchase invoices
					"pay" => chief || acct || cashier,        // receipts, payments, bank/cash transfers
					"manage" => chief,                       // period close, year-end, posting rules, roles, approvals
					"currency-override" => chief || acct,    // issue a document in a currency other than the branch's
					// Period control. SEPARATE from "manage" deliberately: manage gates AssignAccRole, so binding the
					// period close to it would force every person who may close a month to also hold the power to grant
					// accounting roles - more privilege than the job needs, and the wrong kind.
					AccountingActions.PeriodClose => chief || acct,
					// Reopen is strictly stronger than close: it RE-ADMITS posting to a period that was sealed.
					AccountingActions.PeriodReopen => chief,
					_ => false                               // Auditor (or unknown role) → read-only
				};

				return Log(allowed
					? AuthorizationDecision.Allow(
						AuthorizationDecisionSources.LegacyRole, companyId, Scope, action,
						AuthorizationReasonCodes.RoleHeld, roles)
					: AuthorizationDecision.Deny(
						companyId, Scope, action, AuthorizationReasonCodes.RoleNotHeld));
			}

			// ---- no role configured: the action decides, not the module ----
			//
			// The reader evaluates NeverBootstrapOpen BEFORE it queries a policy, so post/pay/manage/
			// currency-override deny here even if somebody inserts a permitting row by hand. `read` is allowed
			// only because the B3 seed wrote an explicit compatibility policy for it.
			return Log(await _policies.ResolveDecisionAsync(context, Scope, action, cancellationToken));
		}

		/// <summary>
		/// Structured, safe. Carries the decision's provenance and NO financial data — no balance, no account
		/// value, no amount. A bootstrap allow is logged at Information because it is temporary compatibility and
		/// not the security destination; a quiet one is indistinguishable from a working policy (RISK-041).
		/// </summary>
		private AuthorizationDecision Log(AuthorizationDecision decision)
		{
			if (decision.IsBootstrap)
				_log.LogInformation(
					"Accounting authorization: company {Company} action {Action} allowed={Allowed} " +
					"source={Source} reason={Reason} policy={PolicyId} — COMPATIBILITY, not role authorization.",
					decision.CompanyID, decision.Action, decision.IsAllowed,
					decision.DecisionSource, decision.ReasonCode, decision.BootstrapPolicyId);
			else
				_log.LogDebug(
					"Accounting authorization: company {Company} action {Action} allowed={Allowed} " +
					"source={Source} reason={Reason}.",
					decision.CompanyID, decision.Action, decision.IsAllowed,
					decision.DecisionSource, decision.ReasonCode);

			return decision;
		}

		public async Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);
			if (context.EmployeeId is not > 0) return Array.Empty<string>();
			return await _db.AccountingUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == context.CompanyId && r.EmployeeId == context.EmployeeId.Value)
				.Select(r => r.Role)
				.ToListAsync(cancellationToken);
		}

		// Dormant until at least one accounting role is assigned in this company (avoids lockout while configuring).
		private Task<bool> AnyRoleConfiguredAsync(int companyId, CancellationToken cancellationToken)
			=> _db.AccountingUserRoles.AnyAsync(r => r.CompanyID == companyId, cancellationToken);

		// ---------------------------------------------------------------------------------------------
		// Legacy surface — signatures unchanged, now backed by the canonical method.
		// ---------------------------------------------------------------------------------------------

		/// Legacy, SYNCHRONOUS, session-based. Kept because 26 call sites (mostly views and controller display
		/// code) use it and cannot await. It is NOT part of the permission path any more: CanAsync no longer
		/// consults it. Where a session exists this returns the same id the BusinessContext resolves, because the
		/// session blob is the context's own first source.
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
			// No resolvable identity or company ⇒ DENY. Before Stage 1 this path produced a context for
			// company 1 and evaluated against it; now an unresolved request cannot borrow another company's
			// authorization.
			if (context == null) return false;
			return await CanAsync(context, action);
		}

		public async Task<string> RoleLabelAsync(bool isAr)
		{
			var roles = await MyRolesAsync();
			if (roles.Count == 0) return isAr ? "قارئ" : "Viewer";
			string name(string r) => r switch
			{
				"ChiefAccountant" => isAr ? "رئيس حسابات" : "Chief accountant",
				"Accountant" => isAr ? "محاسب" : "Accountant",
				"Cashier" => isAr ? "أمين صندوق" : "Cashier",
				"Auditor" => isAr ? "مدقّق" : "Auditor",
				_ => r
			};
			return string.Join("، ", roles.Select(name));
		}
	}
}