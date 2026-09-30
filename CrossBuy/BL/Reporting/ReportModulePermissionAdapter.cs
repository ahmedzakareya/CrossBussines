using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ==================================================================================================
    // ADR-037 — WHERE REPORTING AUTHORITY STOPS AND MODULE AUTHORITY BEGINS.
    //
    // THE DEFECT THIS CLOSES. RoleMapReportPermissionEvaluator answered TRUE to every permission key for
    // anyone holding a reporting-administrator role, before the role map was consulted at all. A key is
    // just a string to that short-circuit, so `reporting.administer` and `accounting.reports.profitability`
    // were indistinguishable: administering the reporting platform silently conferred the authority to
    // read what the company EARNED on every customer. Nothing in the reporting layer could fix that by
    // itself, because the reporting layer is not the authority on accounting data.
    //
    // THE RULE, AND IT IS GENERIC RATHER THAN A LIST OF MODULES. A key that begins with the reporting
    // namespace is owned by this platform and is answered here. Every other key is owned by whichever
    // module named it, and this adapter's job is to ask that module instead of guessing. No module is
    // hardcoded as special; Accounting is simply the first one with an authoritative service to ask.
    //
    // WHAT IT DELIBERATELY DOES NOT DO. It does not widen anything. A caller who fails the module's own
    // test fails here too, and a caller who never had reporting access is not rescued by holding a module
    // role - this decides ONE question, "may you use this key", and the engine's own report-level and
    // field-level gates still run afterwards.
    // ==================================================================================================

    /// <summary>
    /// One module's answer to "may this caller use a reporting permission key I own".
    /// Returning null means "that key is inside my namespace and I do not recognise it", which the
    /// adapter treats as a refusal — never as permission.
    /// </summary>
    public interface IReportModuleAuthority
    {
        /// The key namespace this module owns, e.g. "accounting.". Matched with Ordinal comparison.
        string KeyPrefix { get; }

        Task<bool?> EvaluateAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public sealed class ReportModulePermissionAdapter : IReportPermissionEvaluator
    {
        private readonly IReportPermissionEvaluator _platform;
        private readonly IReadOnlyList<IReportModuleAuthority> _modules;

        public ReportModulePermissionAdapter(
            RoleMapReportPermissionEvaluator platform, IEnumerable<IReportModuleAuthority> modules)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _modules = (modules ?? Enumerable.Empty<IReportModuleAuthority>()).ToList();
        }

        public async Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // ---- fail closed, and never invent a tenant ----------------------------------------------
            //
            // The company comes from the BusinessContext and from nowhere else. There is no fallback to
            // company 1, no reading of a caller-supplied company, and an unresolved scope is a refusal
            // rather than a lookup - the same rule the module services state for themselves. Stated here
            // as well as downstream on purpose: this method is reached by the engine, the sub-report path
            // and archive retrieval, and a guard that relies on someone else having checked is not a guard.
            if (context is null || context.CompanyId <= 0) return false;
            if (string.IsNullOrWhiteSpace(permissionKey)) return false;

            // ---- a key this platform owns is answered by this platform -------------------------------
            var module = _modules.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.KeyPrefix)
                && permissionKey.StartsWith(m.KeyPrefix, StringComparison.Ordinal));

            // No module claims this namespace. That covers every reporting.* key and every module that
            // has not yet been given an authoritative service - those keep answering from the role map
            // exactly as before, so this adapter adds a boundary without moving anyone's existing rights.
            if (module is null)
                return await _platform.HasPermissionAsync(permissionKey, context, cancellationToken);

            // ---- the owning module decides ------------------------------------------------------------
            //
            // Note what is NOT consulted here: the role map, and reporting administration. A module key
            // that the module refuses is refused, however much reporting authority the caller holds.
            var decided = await module.EvaluateAsync(permissionKey, context, cancellationToken);

            // AN UNRECOGNISED KEY INSIDE A CLAIMED NAMESPACE IS A REFUSAL, not a fall-through. Handing it
            // back to the role map would mean a typo - or a key invented later and never mapped - could be
            // answered by configuration the module never agreed to, and AllowUnmappedPermissions would
            // then decide a module's confidentiality. The module claimed the namespace; silence from it
            // means no.
            return decided ?? false;
        }
    }

    // ==================================================================================================
    // ACCOUNTING. The first module with an authoritative service to ask, and the shape every later one
    // follows: a prefix, a map from reporting key to module action, and nothing else. The mapping is
    // deliberately explicit rather than derived from the key's text - "accounting.reports.profitability"
    // resolving to an action by string surgery is how a rename becomes a silent grant.
    // ==================================================================================================
    public sealed class AccountingReportModuleAuthority : IReportModuleAuthority
    {
        // The CANONICAL, session-free surface. IAccountingAccessService carries only the session-bound
        // CanAsync(action), which reads the company from the ambient HTTP context - exactly what must not
        // decide a reporting question, because reporting also runs from a schedule worker where there is
        // no session. This one takes the BusinessContext the caller is actually standing in.
        private readonly CrossBuy.BL.Platform.IModuleAccessService _accounting;

        public AccountingReportModuleAuthority(CrossBuy.BL.Platform.IModuleAccessService accounting)
        {
            _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        }

        public string KeyPrefix => "accounting.";

        public Task<bool?> EvaluateAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // THE TWO TIERS STAY SEPARATE. Mapping profitability onto the ordinary report action would
            // hand every Accountant and Auditor the margin on every customer, which is the precise
            // disclosure the second tier exists to prevent.
            var action = permissionKey switch
            {
                AccountingReportPermissions.View => AccountingActions.ReportRead,
                AccountingReportPermissions.Profitability => AccountingActions.ProfitabilityReportRead,
                _ => null,
            };

            // Inside our namespace but not a key we issued: null, which the adapter reads as a refusal.
            if (action is null) return Task.FromResult<bool?>(null);

            return Decide(action, context, cancellationToken);
        }

        private async Task<bool?> Decide(string action, BusinessContext context,
            CancellationToken cancellationToken)
        {
            // The context is passed through untouched. CanAsync re-checks the company itself and denies an
            // unresolved one, so the tenant is decided by the module, from the caller's own scope.
            var allowed = await _accounting.CanAsync(context, action, null, cancellationToken);
            return allowed;
        }
    }
}
