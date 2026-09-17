using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE PERMISSIONS MODEL.
    //
    // THE ORDERING RULE, which is the whole model in one sentence:
    //
    //      A module permission GATES a report. A share can only RAISE what that gate already allowed.
    //      A share can never open a gate that is shut.
    //
    // Without that ordering, "share a report" becomes a way to hand someone data their module permissions deny —
    // a back door built out of a convenience feature. So the evaluation is strictly:
    //
    //      1. company resolved?            no  → DENY (fail closed; never default to a company)
    //      2. module permission held?      no  → DENY (shares are not consulted at all)
    //      3. base level                       = Run
    //      4. share grants                     → may raise to Edit / Manage
    //      5. ownership                        → owner of a template gets Manage on it
    //      6. reporting administrator          → Manage
    //      7. template scope visibility    no  → DENY (a Personal template is not yours)
    //
    // WHAT THIS SLICE DOES NOT DO, deliberately: it does not touch, extend or read the platform's authorization
    // services, access services, bootstrap policies or permission adapters. The brief forbids it and the
    // dependency would be wrong anyway — reporting must not become a second place where module authorization is
    // decided. Instead there is ONE seam, IReportPermissionEvaluator, and the shipped implementation is
    // self-contained and FAILS CLOSED: an unmapped permission key is denied, not allowed. Binding it to the real
    // permission provider later is one class and one DI line.
    // ============================================================================================

    // Where an access decision came from. Recorded so an "I can see this report" question has an answer.
    public enum ReportAccessSource
    {
        None = 0,
        ModulePermission = 1,
        Share = 2,
        Ownership = 3,
        Administrator = 4,
    }

    public sealed class ReportAccessDecision
    {
        public bool Allowed { get; init; }
        public ReportAccessLevel EffectiveLevel { get; init; } = ReportAccessLevel.None;
        public ReportAccessSource Source { get; init; }

        // Stable machine code, present only on a denial.
        public string? ReasonCode { get; init; }
        public string? Reason { get; init; }

        public static ReportAccessDecision Allow(ReportAccessLevel level, ReportAccessSource source) =>
            new() { Allowed = true, EffectiveLevel = level, Source = source };

        public static ReportAccessDecision Deny(string code, string reason) =>
            new() { Allowed = false, ReasonCode = code, Reason = reason };
    }

    // ============================================================================================
    // SEAM 1 — the module permission gate.
    // ============================================================================================
    public interface IReportPermissionEvaluator
    {
        // May read anything it needs (it is scoped). Must be side-effect free: this is called on every report
        // browse and every generate.
        Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    // Configuration for the shipped evaluator.
    //
    // A role map rather than a permission provider, because this slice may not depend on the platform's
    // authorization work. It is genuinely usable — an operator maps report permission keys to role names — and it
    // is explicitly a STOPGAP: ADR-037 records replacing it as the first integration task once reporting is
    // approved.
    public sealed class ReportPermissionOptions
    {
        // Roles that hold reporting administration (platform templates, other people's schedules).
        public IReadOnlyList<string> AdministratorRoles { get; set; } = new[] { "Admin", "SuperAdmin" };

        // permissionKey → roles that hold it. A key absent from this map is DENIED.
        public IDictionary<string, string[]> RoleMap { get; set; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // An UNMAPPED key is denied. Flipping this to true would make "forgot to configure it" mean "everyone can
        // see it", which is the failure mode the whole model is arranged to prevent. It exists only so the choice
        // is visible in configuration review rather than buried in code.
        public bool AllowUnmappedPermissions { get; set; }
    }

    public class RoleMapReportPermissionEvaluator : IReportPermissionEvaluator
    {
        private readonly ReportPermissionOptions _options;

        public RoleMapReportPermissionEvaluator(ReportPermissionOptions options) { _options = options; }

        public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // No resolved company means no data at all, so no permission either. This mirrors the platform rule:
            // "an unresolved company scope reads no company-scoped data and writes none. Fail closed."
            if (context.CompanyId <= 0) return Task.FromResult(false);

            // Not "anonymous is public". Public means "any authenticated caller in a resolved company", which is
            // the weakest permission the platform can express — there is no anonymous report.
            if (string.Equals(permissionKey, ReportPermissions.Public, StringComparison.Ordinal))
                return Task.FromResult(context.IsAuthenticated);

            if (IsAdministrator(context)) return Task.FromResult(true);

            if (_options.RoleMap.TryGetValue(permissionKey, out var roles))
                return Task.FromResult(roles.Any(r =>
                    context.Roles.Any(cr => string.Equals(cr, r, StringComparison.OrdinalIgnoreCase))));

            return Task.FromResult(_options.AllowUnmappedPermissions);
        }

        private bool IsAdministrator(BusinessContext context) =>
            _options.AdministratorRoles.Any(r =>
                context.Roles.Any(cr => string.Equals(cr, r, StringComparison.OrdinalIgnoreCase)));
    }

    // ============================================================================================
    // SEAM 2 — team membership, for Team-scope templates and Team share grants.
    // ============================================================================================
    public interface IReportTeamResolver
    {
        // The administrative units the caller belongs to. Empty = belongs to none, which means Team-scope
        // templates simply do not resolve for them — never an error, and never a fallback to "all teams".
        Task<IReadOnlyList<int>> GetTeamIdsAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    // Reads Employee.DepartmentID — the existing administrative-hierarchy node.
    //
    // READ-ONLY and single-table: no production service is called, nothing is written, and no authorization
    // decision is delegated. Filtered on the resolved company so a department id from another tenant cannot come
    // back even if an employee row were mis-keyed.
    public class EmployeeDepartmentTeamResolver : IReportTeamResolver
    {
        private readonly CrossDbContext _db;

        public EmployeeDepartmentTeamResolver(CrossDbContext db) { _db = db; }

        public async Task<IReadOnlyList<int>> GetTeamIdsAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0 || context.CompanyId <= 0) return Array.Empty<int>();

            var departmentId = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == context.EmployeeId!.Value && e.EmpCompanyID == context.CompanyId)
                .Select(e => e.DepartmentID)
                .FirstOrDefaultAsync(cancellationToken);

            return departmentId is > 0 ? new[] { departmentId.Value } : Array.Empty<int>();
        }
    }

    // ============================================================================================
    // THE AUTHORIZATION SERVICE
    // ============================================================================================
    public interface IReportAuthorizationService
    {
        // Access to the REPORT (any template). `required` is the minimum level the caller needs.
        Task<ReportAccessDecision> AuthorizeReportAsync(ReportDefinition definition, ReportAccessLevel required,
            BusinessContext context, CancellationToken cancellationToken = default);

        // Access to ONE template of that report. Applies the report gate first, then the template's scope rules.
        Task<ReportAccessDecision> AuthorizeTemplateAsync(ReportDefinition definition, ReportTemplate template,
            ReportAccessLevel required, BusinessContext context, CancellationToken cancellationToken = default);

        // MAY THIS CALLER AUTHOR A DERIVED DATASET? A shaping right, not a reading one: the holder decides
        // what the company is OFFERED in the Studio, and the derivation they produce grants no data of its
        // own because it inherits its parent's permission.
        //
        // IT LIVES HERE RATHER THAN AS A BARE IReportPermissionEvaluator CALL IN THE ENDPOINT, and the
        // analyzer is what made that the right shape: CBA003 flags a permission check in a mutating
        // endpoint that is not a DECLARED authority, because a check nobody declared is one no other
        // caller is obliged to make. Routing it through the platform's own authorization service puts the
        // decision where every other reporting decision already is, and gives it one implementation.
        Task<ReportAccessDecision> AuthorizeDatasetAuthoringAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        // The subset of a catalog listing the caller may see. Used by the report browser so a user is never shown
        // a report they cannot run.
        Task<IReadOnlyList<ReportDefinition>> FilterVisibleAsync(IEnumerable<ReportDefinition> definitions,
            BusinessContext context, CancellationToken cancellationToken = default);

        Task<bool> IsAdministratorAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    public class ReportAuthorizationService : IReportAuthorizationService
    {
        public const string CodeCompanyUnresolved = "company_unresolved";
        public const string CodeNoModulePermission = "report_permission_denied";
        public const string CodeInsufficientLevel = "report_level_insufficient";
        public const string CodeTemplateNotYours = "template_not_visible";
        public const string CodeTemplateOtherCompany = "template_other_company";
        public const string CodeScopeNotAllowed = "template_scope_not_allowed";

        private readonly CrossDbContext _db;
        private readonly IReportPermissionEvaluator _permissions;
        private readonly IReportTeamResolver _teams;
        private readonly IReportClock _clock;

        public ReportAuthorizationService(CrossDbContext db, IReportPermissionEvaluator permissions,
            IReportTeamResolver teams, IReportClock clock)
        {
            _db = db;
            _permissions = permissions;
            _teams = teams;
            _clock = clock;
        }

        public Task<bool> IsAdministratorAsync(BusinessContext context, CancellationToken cancellationToken = default) =>
            _permissions.HasPermissionAsync(ReportPermissions.Administer, context, cancellationToken);

        public async Task<ReportAccessDecision> AuthorizeReportAsync(ReportDefinition definition,
            ReportAccessLevel required, BusinessContext context, CancellationToken cancellationToken = default)
        {
            // ---- 1. Company guard -------------------------------------------------------------------------
            if (context.CompanyId <= 0)
                return ReportAccessDecision.Deny(CodeCompanyUnresolved,
                    "No company is resolved for this request, so no company-scoped report may be produced.");

            // ---- 2. The gate ------------------------------------------------------------------------------
            var isAdmin = await IsAdministratorAsync(context, cancellationToken);

            if (!isAdmin && !await _permissions.HasPermissionAsync(definition.PermissionKey, context, cancellationToken))
                // Shares are NOT consulted. This early return IS the ordering rule.
                return ReportAccessDecision.Deny(CodeNoModulePermission,
                    $"Permission '{definition.PermissionKey}' is required to use report '{definition.Code}'.");

            if (isAdmin)
                return Satisfies(ReportAccessLevel.Manage, required, ReportAccessSource.Administrator, definition.Code);

            // ---- 3/4. Base level, raised by share grants -------------------------------------------------
            var level = ReportAccessLevel.Run;
            var source = ReportAccessSource.ModulePermission;

            var shared = await HighestShareLevelAsync(definition.Code, templateId: null, context, cancellationToken);
            if (shared > level) { level = shared; source = ReportAccessSource.Share; }

            return Satisfies(level, required, source, definition.Code);
        }

        public async Task<ReportAccessDecision> AuthorizeDatasetAuthoringAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // Fail closed on an unresolved tenant, like every other decision in this file.
            if (context is not { CompanyId: > 0 })
                return ReportAccessDecision.Deny("no_company_scope", "No company scope.");

            return await _permissions.HasPermissionAsync(
                       ReportPermissions.AuthorDatasets, context, cancellationToken)
                ? ReportAccessDecision.Allow(ReportAccessLevel.Manage, ReportAccessSource.ModulePermission)
                : ReportAccessDecision.Deny("dataset_authoring_denied", "Authoring report datasets is not permitted.");
        }

        public async Task<ReportAccessDecision> AuthorizeTemplateAsync(ReportDefinition definition,
            ReportTemplate template, ReportAccessLevel required, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // The report gate first, always. A template is a layout over a report; it cannot grant access to the
            // report itself. Asked for View here rather than `required`, because the template rules below may
            // legitimately raise the level (ownership) and we must not deny before consulting them.
            var reportDecision = await AuthorizeReportAsync(definition, ReportAccessLevel.View, context,
                cancellationToken);
            if (!reportDecision.Allowed) return reportDecision;

            var isAdmin = reportDecision.Source == ReportAccessSource.Administrator;

            // A Platform template (CompanyID 0) is readable by every company; anything else must belong to the
            // caller's company. Checked before scope so a cross-tenant id gets the accurate reason.
            if (template.Scope != ReportTemplateScope.Platform && template.CompanyID != context.CompanyId)
                return ReportAccessDecision.Deny(CodeTemplateOtherCompany,
                    "That template belongs to another company.");

            if (!definition.Capabilities.SupportsScope(template.Scope))
                return ReportAccessDecision.Deny(CodeScopeNotAllowed,
                    $"Report '{definition.Code}' does not permit {template.Scope} templates.");

            var level = ReportAccessLevel.None;
            var source = ReportAccessSource.None;

            switch (template.Scope)
            {
                case ReportTemplateScope.Personal:
                    // Nobody else's private layout — not even a colleague with full module rights. An
                    // administrator gets it (see below) because someone must be able to clean up after a leaver.
                    if (template.OwnerEmpId is > 0 && template.OwnerEmpId == context.EmployeeId)
                    {
                        level = ReportAccessLevel.Manage;
                        source = ReportAccessSource.Ownership;
                    }
                    break;

                case ReportTemplateScope.Team:
                {
                    var teams = await _teams.GetTeamIdsAsync(context, cancellationToken);
                    if (template.TeamId is > 0 && teams.Contains(template.TeamId.Value))
                    {
                        // A team member may RUN the team layout; only its owner (or an administrator) may change
                        // it, so one member cannot silently redefine the team's report.
                        level = template.OwnerEmpId == context.EmployeeId
                            ? ReportAccessLevel.Manage
                            : ReportAccessLevel.Run;
                        source = template.OwnerEmpId == context.EmployeeId
                            ? ReportAccessSource.Ownership
                            : ReportAccessSource.ModulePermission;
                    }
                    break;
                }

                case ReportTemplateScope.Company:
                    // The company standard: everyone who passed the report gate may run it. Editing needs an
                    // explicit Edit/Manage share or administration.
                    level = ReportAccessLevel.Run;
                    source = ReportAccessSource.ModulePermission;
                    if (template.OwnerEmpId is > 0 && template.OwnerEmpId == context.EmployeeId)
                    {
                        level = ReportAccessLevel.Manage;
                        source = ReportAccessSource.Ownership;
                    }
                    break;

                case ReportTemplateScope.Platform:
                    // Shipped with the product: runnable by all, editable by nobody. "Editing" a platform
                    // template is a FORK into a Company/Personal template — see IReportTemplateService.ForkAsync.
                    level = ReportAccessLevel.Run;
                    source = ReportAccessSource.ModulePermission;
                    break;
            }

            var shared = await HighestShareLevelAsync(definition.Code, template.Id, context, cancellationToken);
            if (shared > level) { level = shared; source = ReportAccessSource.Share; }

            if (isAdmin && level < ReportAccessLevel.Manage)
            {
                level = ReportAccessLevel.Manage;
                source = ReportAccessSource.Administrator;
            }

            if (level == ReportAccessLevel.None)
                return ReportAccessDecision.Deny(CodeTemplateNotYours,
                    $"Template {template.Id} is not visible to you ({template.Scope} scope).");

            // A platform template can never be edited, regardless of level — including by an administrator acting
            // in a tenant. Platform content changes by deployment, not by a tenant with a strong role.
            if (template.Scope == ReportTemplateScope.Platform && required >= ReportAccessLevel.Edit)
                return ReportAccessDecision.Deny(CodeScopeNotAllowed,
                    "A platform template is read-only. Fork it into a company or personal template instead.");

            return Satisfies(level, required, source, definition.Code);
        }

        public async Task<IReadOnlyList<ReportDefinition>> FilterVisibleAsync(
            IEnumerable<ReportDefinition> definitions, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var list = definitions.ToList();
            if (context.CompanyId <= 0) return Array.Empty<ReportDefinition>();

            if (await IsAdministratorAsync(context, cancellationToken)) return list;

            // Evaluated per DISTINCT permission key, not per report: a catalog of 200 reports over 12 keys costs
            // 12 evaluations. The evaluator may hit the database, so this matters.
            var keys = list.Select(d => d.PermissionKey).Distinct(StringComparer.Ordinal).ToList();
            var granted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in keys)
                if (await _permissions.HasPermissionAsync(key, context, cancellationToken))
                    granted.Add(key);

            return list.Where(d => granted.Contains(d.PermissionKey)).ToList();
        }

        // ------------------------------------------------------------------------------------------------
        // Share grants. Company-filtered, expiry evaluated HERE against the clock — never by a sweeper, because
        // an expiry that depends on a background job keeps working when the job stops.
        // ------------------------------------------------------------------------------------------------
        private async Task<ReportAccessLevel> HighestShareLevelAsync(string reportCode, int? templateId,
            BusinessContext context, CancellationToken cancellationToken)
        {
            var now = _clock.LocalNow;

            var candidates = await _db.ReportShares.AsNoTracking()
                .Where(s => s.CompanyID == context.CompanyId
                            && s.DeletedAt == null
                            && s.ReportCode == reportCode
                            && (s.ExpiresAt == null || s.ExpiresAt > now)
                            // A grant on the REPORT applies to every template of it; a grant on a TEMPLATE applies
                            // only to that one.
                            && (s.TemplateId == null || (templateId != null && s.TemplateId == templateId)))
                .Select(s => new { s.PrincipalType, s.PrincipalKey, s.AccessLevel })
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return ReportAccessLevel.None;

            var teamIds = candidates.Any(c => c.PrincipalType == ReportPrincipalType.Team)
                ? await _teams.GetTeamIdsAsync(context, cancellationToken)
                : Array.Empty<int>();

            var best = ReportAccessLevel.None;
            foreach (var grant in candidates)
            {
                var applies = grant.PrincipalType switch
                {
                    ReportPrincipalType.Company => true,
                    ReportPrincipalType.Employee => context.EmployeeId is > 0
                        && grant.PrincipalKey == context.EmployeeId.Value.ToString(),
                    ReportPrincipalType.Role => context.Roles.Any(r =>
                        string.Equals(r, grant.PrincipalKey, StringComparison.OrdinalIgnoreCase)),
                    ReportPrincipalType.Team => int.TryParse(grant.PrincipalKey, out var teamId)
                        && teamIds.Contains(teamId),
                    _ => false,
                };

                if (applies && grant.AccessLevel > best) best = grant.AccessLevel;
            }
            return best;
        }

        private static ReportAccessDecision Satisfies(ReportAccessLevel actual, ReportAccessLevel required,
            ReportAccessSource source, string reportCode) =>
            actual >= required
                ? ReportAccessDecision.Allow(actual, source)
                : ReportAccessDecision.Deny(CodeInsufficientLevel,
                    $"Report '{reportCode}' requires {required} access; you hold {actual}.");
    }
}