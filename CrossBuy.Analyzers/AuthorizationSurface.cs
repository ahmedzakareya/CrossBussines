using System.Collections.Immutable;

namespace CrossBuy.Analyzers
{
    /// <summary>
    /// The declared authorization surface of the application: what counts as a permission, what counts as an
    /// authority, and — the part that matters most — what is deliberately NOT credited.
    ///
    /// This file is the analyzer's policy. It exists as one readable list rather than as conditions spread
    /// through the resolver because CORRECTION-004 was caused by a credit nobody could see: PosLaneActivityGuard
    /// was on a permission list, checked no role, and silently credited 44 mutating POS actions as protected.
    /// A policy that cannot be read in one place gets that wrong again.
    ///
    /// Every entry below is either a real IAsyncActionFilter that asks an access service, or a real access
    /// service. Nothing is credited for being named like authorization.
    /// </summary>
    internal static class AuthorizationSurface
    {
        /// <summary>
        /// Attribute simple names (without the "Attribute" suffix) that ARE a module permission.
        ///
        /// Each one resolves a BusinessContext and asks a module access service before calling next(); denial is
        /// a 403/redirect, not a log line. Verified against the attribute source, not assumed from the name.
        ///
        /// PosPerm does not exist yet and is listed so that adding it is picked up without editing the analyzer —
        /// the same forward-declaration the accepted PowerShell scanner uses.
        ///
        /// TaskPerm and CommunicationPerm DO exist (Models/ModulePermAttributes.cs) but are applied to zero
        /// actions today, so recognising them changes no count. The scanner omits them; this analyzer includes
        /// them, which is a capability difference with a measured delta of zero. See the reconciliation report.
        /// </summary>
        internal static readonly ImmutableHashSet<string> PermissionAttributes = ImmutableHashSet.Create(
            "AccPerm",
            "InvPerm",
            "CrmPerm",
            "ApiPerm",
            "PlatformOps",
            "HrPerm",
            "ProjectPerm",
            "TaskPerm",
            "CommunicationPerm",
            "PosPerm");

        /// <summary>
        /// Attributes that prove AUTHENTICATION, a CSRF defence, an environment gate or a routing decision — and
        /// are therefore never authorization. Naming them explicitly (rather than just leaving them out of the
        /// list above) is what lets CBA002 say "this endpoint has authentication and no authorization" instead of
        /// silently reporting the same thing as an unexplained gap.
        ///
        /// PosLaneActivityGuard is here by name because of CORRECTION-004: it compares the branch activity preset
        /// against the lane, checks NO role, and calls next() when the session is absent or malformed — so it does
        /// not even prove authentication.
        ///
        /// DevOnly is an ENVIRONMENT gate (404 outside Development). The brief asks the analyzer to recognise it;
        /// it is recognised as its own declared category (see <see cref="EnvironmentGateAttributes"/>) and is NOT
        /// credited as authorization, because crediting a filter that checks no role is the exact false credit
        /// CORRECTION-004 removed.
        /// </summary>
        internal static readonly ImmutableHashSet<string> AuthenticationOnlyAttributes = ImmutableHashSet.Create(
            "Authorize",
            "SessionValidation");

        internal static readonly ImmutableHashSet<string> AntiForgeryAttributes = ImmutableHashSet.Create(
            "ValidateAntiForgeryToken",
            "AutoValidateAntiforgeryToken",
            "IgnoreAntiforgeryToken");

        internal static readonly ImmutableHashSet<string> RoutingOnlyAttributes = ImmutableHashSet.Create(
            "PosLaneActivityGuard");

        internal static readonly ImmutableHashSet<string> EnvironmentGateAttributes = ImmutableHashSet.Create(
            "DevOnly");

        internal static readonly ImmutableHashSet<string> AnonymousAttributes = ImmutableHashSet.Create(
            "AllowAnonymous");

        /// <summary>
        /// Types whose members ARE an authorization authority. Matched by SIMPLE NAME on the containing type of
        /// the resolved method symbol, so it works identically for an interface reference, a concrete field, a
        /// property, a factory call (<c>_permissions().CanAsync(...)</c>) and a fully qualified call.
        ///
        /// Interfaces and implementations are both listed: a controller normally holds the interface, but a
        /// helper may hold the concrete type, and crediting only one of the two would punish the other shape.
        /// </summary>
        internal static readonly ImmutableHashSet<string> AuthorityTypes = ImmutableHashSet.Create(
            // The eight module access services — the canonical permission engines.
            "IAccountingAccessService", "AccountingAccessService",
            "IInventoryAccessService", "InventoryAccessService",
            "ICrmAccessService", "CrmAccessService",
            "IPosAccessService", "PosAccessService",
            "IHrAccessService", "HrAccessService",
            "IProjectsAccessService", "ProjectsAccessService",
            "ITasksAccessService", "TasksAccessService",
            "ICommunicationAccessService", "CommunicationAccessService",
            // The canonical session-free contract every module also implements.
            "IModuleAccessService",
            // The platform permission provider and its adapters.
            "IPlatformPermissionProvider", "PlatformPermissionProvider",
            "IModulePermissionAdapter", "ModulePermissionAdapterBase",
            // Hotfix A.1 — the in-service guard that authorizes AND returns the validated company.
            "IAccountingApiAuthorization", "AccountingApiAuthorization",
            // Stage 2A Batch A — the platform grant writer.
            //
            // ADDED DELIBERATELY AND VISIBLY, which is the process this file's header describes. It is an
            // authority in the full sense: every one of its write methods resolves the actor's administration
            // tier and privilege ceiling THROUGH the real module access services, and refuses before touching a
            // row. An endpoint that calls it cannot reach a write without an authorization decision having been
            // taken — which is exactly the property the declared surface exists to record.
            //
            // It was added because the analyzer BLOCKED Batch A's own three new endpoints with CBA001, correctly:
            // it had no way to know this service authorizes. The alternatives were to suppress the diagnostic or
            // to append to the frozen baseline, and Batch A's brief forbids both. Declaring the authority is the
            // only honest option, and it moves the in-body count by exactly the three endpoints concerned.
            "IPlatformGrantWriter", "PlatformGrantWriter",
            // R1 — the Reporting authorization seam.
            //
            // ADDED BY THE SAME DELIBERATE PROCESS, and only after reading what it actually does. The
            // Reporting platform routes every access decision through ONE seam: AuthorizeReportAsync,
            // AuthorizeTemplateAsync, FilterVisibleAsync and IsAdministratorAsync. It is an authority in
            // the full sense:
            //
            //   * it fails closed on an unresolved company - RoleMapReportPermissionEvaluator returns false
            //     when context.CompanyId <= 0, and ReportTemplateService.SaveAsync refuses with
            //     CodeCompanyUnresolved before touching a row;
            //   * the decision is company-scoped from the BusinessContext. NO endpoint accepts a
            //     caller-supplied companyId - verified by inspection of every action on the controller;
            //   * template writes resolve ownership and scope through it and refuse on !decision.Allowed;
            //   * it is wired to real roles in Program.cs via MapPermission, so it is not a stub that
            //     returns true.
            //
            // WHAT THIS DELIBERATELY DOES NOT DO: it credits only endpoints whose call graph REACHES this
            // seam. Two Reporting writes - RemoveFavorite and ReorderFavorites - authorize by row ownership
            // (CompanyID + EmployeeId predicates, failing closed) and never call this service, so they are
            // NOT credited by this addition and remain visible to CBA001. That is the correct outcome: the
            // declared surface records where authorization demonstrably happens, and forcing the count to
            // "all seven" would be exactly the false credit CORRECTION-004 exists to prevent.
            "IReportAuthorizationService", "ReportAuthorizationService");

        /// <summary>
        /// Members of an authority type that are NOT authorization, named individually.
        ///
        /// IsActivityAllowedForLane lives on IPosAccessService but is the lane routing predicate — the very check
        /// CORRECTION-004 refused to credit. Being reachable through an authority type must not launder it.
        ///
        /// This exclusion is why the analyzer is not simply "any call on an access service": that rule would
        /// re-credit the lane guard through the back door.
        /// </summary>
        internal static readonly ImmutableHashSet<string> NonAuthorityMembers = ImmutableHashSet.Create(
            "IsActivityAllowedForLane");

        /// <summary>
        /// METHOD-name shapes that claim to be authorization. A call to one of these that resolves to no
        /// recognized authority is reported as CBA003 rather than being silently ignored — that is how a newly
        /// added access service, or a hand-rolled permission check, becomes visible instead of quietly reducing
        /// measured coverage.
        ///
        /// This list never grants credit. It only decides when to complain about the absence of credit.
        ///
        /// MATCHED ON THE METHOD NAME ONLY, deliberately. The first version also matched the containing TYPE name,
        /// which fired on <c>PermissionTarget.ForSubjectEmployee</c> — a DTO factory that builds the target of a
        /// check and performs none. Widening it to type names produces exactly the wrong kind of noise: a warning
        /// on a data carrier, on an endpoint that was already correctly credited. A guardrail that cries wolf about
        /// a struct is a guardrail people switch off.
        ///
        /// <c>CanAsync</c> and <c>RolesAsync</c> are included because they are the canonical names on
        /// IModuleAccessService: seeing either on a type that is NOT in the declared surface means a new access
        /// service exists and nobody told the analyzer.
        /// </summary>
        internal static readonly ImmutableArray<string> AuthorizationShapedNameFragments = ImmutableArray.Create(
            "Authorize", "CanAsync", "RolesAsync", "CanAccess", "HasAccess", "IsAllowed", "EnsureAccess",
            "HasPermission", "CheckPermission");

        /// <summary>
        /// Attribute names whose presence means the action is not an endpoint at all.
        /// </summary>
        internal static readonly ImmutableHashSet<string> NonActionAttributes = ImmutableHashSet.Create("NonAction");

        internal const string DiagnosticCategory = "CrossBuy.Authorization";

        /// <summary>The file the baseline must be supplied as, via AdditionalFiles.</summary>
        internal const string BaselineFileName = "authorization-baseline.json";
    }
}
