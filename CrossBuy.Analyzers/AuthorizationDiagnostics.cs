using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers
{
    /// <summary>
    /// The six diagnostics.
    ///
    /// EVERY ONE DEFAULTS TO WARNING. That is the dogfood rule from the brief — the analyzer "starts as Warning
    /// only" — and it is also the safe default for a guardrail shipped into a tree with 143 accepted debt entries.
    /// The escalation to build-breaking Error is a CONFIGURATION decision, made in .editorconfig by the project
    /// that consumes the analyzer, not a hard-coded severity here. That separation is what lets the same analyzer
    /// warn in the IDE and fail CI without two builds of it.
    ///
    /// Escalation is proven, not assumed: an analyzer test overrides CBA001 to Error and asserts the diagnostic
    /// comes out as an Error, so "new debt fails the build" is a tested claim rather than a promise.
    /// </summary>
    internal static class AuthorizationDiagnostics
    {
        private const string Category = AuthorizationSurface.DiagnosticCategory;

        /// <summary>New unprotected mutating endpoint — not present in the frozen baseline.</summary>
        internal static readonly DiagnosticDescriptor NewUnprotectedEndpoint = new DiagnosticDescriptor(
            id: "CBA001",
            title: "Mutating endpoint has no authorization and is not in the frozen baseline",
            messageFormat:
                "Mutating endpoint '{0}' has no authorization the analyzer can see and is not listed in " +
                AuthorizationSurface.BaselineFileName +
                ". Add a module permission attribute, or authorize in the body through an access service. " +
                "The baseline may only shrink — a new entry is not an option.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "The baseline is a shrink-only record of accepted Stage 1 debt. A newly added mutating endpoint " +
                "without authorization is new debt, and new debt is not appended silently.",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>Authentication present, authorization absent.</summary>
        internal static readonly DiagnosticDescriptor AuthenticationWithoutAuthorization = new DiagnosticDescriptor(
            id: "CBA002",
            title: "Mutating endpoint is authenticated but not authorized",
            messageFormat:
                "Mutating endpoint '{0}' carries {1} but no authorization. Authentication proves WHO is calling; " +
                "it does not decide WHAT they may change.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "[Authorize] and [SessionValidation] establish identity. A CSRF token proves the request came " +
                "from the site. PosLaneActivityGuard routes a lane and checks no role. None of them is a " +
                "permission, and counting one as a permission is the defect CORRECTION-004 removed.",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>A call that claims authorization but resolves to nothing the analyzer recognises.</summary>
        internal static readonly DiagnosticDescriptor UnsupportedAuthorizationHelper = new DiagnosticDescriptor(
            id: "CBA003",
            title: "Authorization-shaped call is not a recognized authority",
            messageFormat:
                "Mutating endpoint '{0}' calls '{1}', which looks like an authorization check but is not a " +
                "declared authority. Either route the check through a module access service, or add the type to " +
                "AuthorizationSurface.AuthorityTypes with a reason.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "An unrecognised guard is a measurement risk in both directions: the endpoint may be protected " +
                "and read as debt, or may be protected by something that checks nothing. Neither is decided by " +
                "guessing from a method name.",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>A baseline entry that no longer describes an unprotected endpoint.</summary>
        internal static readonly DiagnosticDescriptor StaleBaselineEntry = new DiagnosticDescriptor(
            id: "CBA004",
            title: "Baseline entry is stale",
            messageFormat:
                "Baseline entry '{0}' no longer matches an unprotected mutating endpoint ({1}). Remove it from " +
                AuthorizationSurface.BaselineFileName + ".",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "A protected, renamed or deleted endpoint must disappear from the baseline. Leaving the entry " +
                "behind keeps an allowance alive for an endpoint that no longer exists, and a rename would then " +
                "read as pre-existing debt instead of new debt.",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>An anonymous mutating endpoint that nobody declared anonymous.</summary>
        internal static readonly DiagnosticDescriptor UndeclaredAnonymousEndpoint = new DiagnosticDescriptor(
            id: "CBA005",
            title: "Anonymous mutating endpoint is not declared anonymous-by-design",
            messageFormat:
                "Mutating endpoint '{0}' is reachable anonymously but is not classified AnonymousByDesign in " +
                AuthorizationSurface.BaselineFileName + ". An intentional anonymous write is a decision and must " +
                "be recorded as one.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "Login and similar endpoints are legitimately anonymous. The distinction between 'anonymous on " +
                "purpose' and 'anonymous by accident' cannot be inferred from the code, so it is declared.",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>A suppression of this analyzer's own diagnostics.</summary>
        internal static readonly DiagnosticDescriptor InvalidSuppression = new DiagnosticDescriptor(
            id: "CBA006",
            title: "Authorization diagnostic is suppressed",
            messageFormat:
                "'{0}' is suppressed here. Authorization diagnostics are not suppressible: fix the endpoint, or " +
                "record the decision in " + AuthorizationSurface.BaselineFileName + " where it is reviewable.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "A #pragma or a SuppressMessage attribute removes a security finding with no reviewer and no " +
                "audit line. The baseline exists so that an accepted gap is visible in one reviewable file.");
    }
}
