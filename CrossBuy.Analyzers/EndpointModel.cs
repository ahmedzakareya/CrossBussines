using System.Collections.Generic;

namespace CrossBuy.Analyzers
{
    /// <summary>How an endpoint's authorization was established — or why it was not.</summary>
    internal enum AuthorizationKind
    {
        /// <summary>No authorization the analyzer can see. This is the debt category.</summary>
        None = 0,

        /// <summary>A permission attribute on the action itself.</summary>
        ActionAttribute,

        /// <summary>A permission attribute on the controller class (any partial declaration of it).</summary>
        InheritedAttribute,

        /// <summary>
        /// The action's own code path asks an authority — directly, or through a helper chain resolved
        /// semantically to a bounded fixpoint.
        /// </summary>
        InBody,
    }

    /// <summary>
    /// One mutating endpoint and everything the analyzer concluded about it. Deliberately a flat record of
    /// facts: the reconciliation report has to be able to explain any single row without re-running analysis.
    /// </summary>
    internal sealed class EndpointFacts
    {
        internal string Controller = string.Empty;
        internal string Action = string.Empty;

        /// <summary>Baseline identity. Renaming either half produces a different id — which is the point.</summary>
        internal string Id => Controller + "." + Action;

        internal string HttpMethods = string.Empty;
        internal string FilePath = string.Empty;
        internal int Line;

        /// <summary>
        /// The endpoint's declaration location, carried directly rather than re-derived from FilePath+Line.
        /// Re-deriving it meant scanning every syntax tree per diagnostic, and a diagnostic reported without a
        /// location silently loses its configured severity (see ControllerLocations).
        /// </summary>
        internal Microsoft.CodeAnalysis.Location? Location;

        internal bool IsMutating;
        internal bool IsApi;

        internal AuthorizationKind Authorization = AuthorizationKind.None;

        /// <summary>The permission attributes found, action-level then class-level.</summary>
        internal readonly List<string> PermissionAttributes = new List<string>();

        /// <summary>The authority call that established InBody authorization, for evidence.</summary>
        internal string? InBodyEvidence;

        /// <summary>The helper chain walked to reach it, e.g. "SaveX -> GateAsync -> ITasksAccessService.CanAsync".</summary>
        internal string? InBodyChain;

        internal bool HasAuthenticationOnly;
        internal bool HasAntiForgery;
        internal bool HasLaneGuard;
        internal bool HasEnvironmentGate;
        internal bool IsAnonymousDeclared;

        /// <summary>
        /// Calls that look like authorization but resolve to no recognized authority. Drives CBA003.
        /// </summary>
        internal readonly List<string> UnsupportedAuthorizationHelpers = new List<string>();

        internal bool IsProtected => Authorization != AuthorizationKind.None;

        internal bool IsAttributeProtected =>
            Authorization == AuthorizationKind.ActionAttribute ||
            Authorization == AuthorizationKind.InheritedAttribute;
    }

    /// <summary>The compilation-wide result.</summary>
    internal sealed class AuthorizationInventoryResult
    {
        internal readonly List<EndpointFacts> Endpoints = new List<EndpointFacts>();
        internal readonly HashSet<string> Controllers = new HashSet<string>();

        /// <summary>
        /// Where each controller is declared, so a stale-baseline diagnostic (CBA004) can be anchored to real source.
        ///
        /// This exists because of a defect a real build found: CBA004 was reported at <c>Location.None</c>, and a
        /// path-matched <c>.editorconfig</c> section cannot apply a severity to a diagnostic that has no path — so
        /// the rule stayed a Warning and the build passed. Anchoring it fixes both the severity and the fact that a
        /// developer could not click through to anything.
        /// </summary>
        internal readonly Dictionary<string, Microsoft.CodeAnalysis.Location> ControllerLocations =
            new Dictionary<string, Microsoft.CodeAnalysis.Location>();

        internal int MutatingCount;
        internal int AttributeProtectedCount;
        internal int InBodyProtectedCount;
        internal int GapCount;
    }
}
