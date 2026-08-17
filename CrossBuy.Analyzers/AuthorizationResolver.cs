using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CrossBuy.Analyzers
{
    /// <summary>The outcome of resolving one action's body.</summary>
    internal sealed class InBodyResult
    {
        internal bool Authorized;

        /// <summary>The authority member that granted it, e.g. "ITasksAccessService.CanAsync".</summary>
        internal string? Authority;

        /// <summary>The chain that reached it, e.g. "SaveTask -> TaskGateAsync -> ITasksAccessService.CanAsync".</summary>
        internal string? Chain;

        /// <summary>Calls whose NAME claims authorization but which resolve to no recognized authority.</summary>
        internal readonly List<string> UnsupportedHelpers = new List<string>();
    }

    /// <summary>
    /// Resolves in-body authorization by SEMANTIC call graph.
    ///
    /// WHAT THIS FIXES. The accepted PowerShell scanner declares its own limitation in the source:
    ///
    ///     "this is a SYNTACTIC call graph over ONE file, not Roslyn semantic binding. It cannot follow an
    ///      authorization call through an interface, a base class or another file, and it cannot distinguish two
    ///      same-named methods."
    ///
    /// Each of those three is a real defect. It cannot see a helper in another partial file; it credits any method
    /// whose NAME matches a helper's name even in a different type; and it matches authority by field name
    /// (<c>_access.</c>), so renaming a field silently removes protection from the measurement. Symbols have none
    /// of those failure modes.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO — the boundary, stated rather than implied.
    ///
    /// The walk follows the endpoint's own code path: the action, helpers on its containing type, helpers on that
    /// type's base types, every partial declaration of them, and local functions and lambdas inside those bodies.
    /// It terminates at a call to a DECLARED AUTHORITY (<see cref="AuthorizationSurface.AuthorityTypes"/>).
    ///
    /// It does NOT descend into arbitrary application services to discover that one of them happens to check
    /// something. That would credit an endpoint for a domain rule written for correctness rather than for
    /// authorization — a rule any other caller can bypass, that no one declared as a control, and that would move
    /// measured debt without a decision being taken. Crediting a check that was never declared a control is the
    /// exact shape of CORRECTION-004. If a service IS an authorization authority, it gets added to the declared
    /// surface, deliberately and visibly, and the count moves for a stated reason.
    ///
    /// BOUNDED FIXPOINT. Depth is capped and every visited method is memoised per compilation, so recursion and
    /// mutual recursion terminate. The cap is generous relative to the deepest real chain (action -> gate ->
    /// access service = 2) and the analyzer degrades to "not authorized" at the boundary, never to a hang.
    /// </summary>
    internal sealed class AuthorizationResolver
    {
        private const int MaxDepth = 8;

        private readonly Compilation _compilation;

        /// <summary>Memoised per method symbol: did this method (transitively) reach an authority?</summary>
        private readonly Dictionary<IMethodSymbol, InBodyResult> _cache =
            new Dictionary<IMethodSymbol, InBodyResult>(SymbolEqualityComparer.Default);

        internal AuthorizationResolver(Compilation compilation) => _compilation = compilation;

        internal InBodyResult Resolve(IMethodSymbol action, INamedTypeSymbol controller, CancellationToken ct)
        {
            var result = new InBodyResult();
            var visiting = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            Walk(action, controller, 0, visiting, result, action.Name, ct);
            return result;
        }

        private bool Walk(
            IMethodSymbol method,
            INamedTypeSymbol controller,
            int depth,
            HashSet<IMethodSymbol> visiting,
            InBodyResult result,
            string chain,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (depth > MaxDepth) return false;
            if (!visiting.Add(method)) return false;   // cycle — the fixpoint's fixed point

            try
            {
                if (_cache.TryGetValue(method, out var cached) && cached.Authorized)
                {
                    result.Authorized = true;
                    result.Authority = cached.Authority;
                    result.Chain = chain + " -> " + cached.Authority;
                    return true;
                }

                foreach (var body in Bodies(method))
                {
                    foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        ct.ThrowIfCancellationRequested();

                        var model = _compilation.GetSemanticModel(invocation.SyntaxTree);
                        var target = model.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;

                        if (target == null)
                        {
                            // Unbound call. Record it only if it CLAIMS authorization, so a broken build cannot
                            // quietly turn a guarded endpoint into an unguarded one without a diagnostic.
                            var text = invocation.Expression.ToString();
                            if (ClaimsAuthorization(text)) AddUnsupported(result, text);
                            continue;
                        }

                        if (IsAuthority(target))
                        {
                            var authority = Describe(target);
                            result.Authorized = true;
                            result.Authority = authority;
                            result.Chain = chain + " -> " + authority;
                            _cache[method] = new InBodyResult
                            {
                                Authorized = true,
                                Authority = authority,
                                Chain = result.Chain
                            };
                            return true;
                        }

                        if (IsWithinEndpointCodePath(target, controller))
                        {
                            foreach (var candidate in Candidates(target, controller))
                            {
                                if (Walk(candidate, controller, depth + 1, visiting, result, chain + " -> " + candidate.Name, ct))
                                {
                                    _cache[method] = new InBodyResult
                                    {
                                        Authorized = true,
                                        Authority = result.Authority,
                                        Chain = result.Chain
                                    };
                                    return true;
                                }
                            }
                            continue;
                        }

                        // Outside the endpoint's own code path. If the METHOD name claims authorization it is
                        // reported (CBA003) rather than ignored: an unrecognised guard is a measurement risk, not
                        // a pass. The containing type's name is deliberately not consulted — see
                        // AuthorizationShapedNameFragments for the PermissionTarget false positive that proved it.
                        if (ClaimsAuthorization(target.Name))
                            AddUnsupported(result, Describe(target));
                    }
                }

                _cache[method] = new InBodyResult { Authorized = false };
                return false;
            }
            finally
            {
                visiting.Remove(method);
            }
        }

        /// <summary>
        /// Every syntax body belonging to a method symbol. Iterating DeclaringSyntaxReferences is what gives
        /// partial-file support for free: a partial method's implementing part is simply another reference.
        /// </summary>
        private static IEnumerable<SyntaxNode> Bodies(IMethodSymbol method)
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                switch (syntax)
                {
                    case MethodDeclarationSyntax m:
                        if (m.Body != null) yield return m.Body;
                        else if (m.ExpressionBody != null) yield return m.ExpressionBody;   // expression-bodied action
                        break;
                    case AccessorDeclarationSyntax a:
                        if (a.Body != null) yield return a.Body;
                        else if (a.ExpressionBody != null) yield return a.ExpressionBody;
                        break;
                    case LocalFunctionStatementSyntax l:
                        if (l.Body != null) yield return l.Body;
                        else if (l.ExpressionBody != null) yield return l.ExpressionBody;
                        break;
                    case ArrowExpressionClauseSyntax e:
                        yield return e;
                        break;
                }
            }
        }

        /// <summary>
        /// Is the resolved call an authorization authority? Matched on the CONTAINING TYPE's simple name, so an
        /// interface reference, a concrete reference, a base-class reference and a fully qualified call all reach
        /// the same verdict — and a field rename cannot change it.
        /// </summary>
        private static bool IsAuthority(IMethodSymbol method)
        {
            if (AuthorizationSurface.NonAuthorityMembers.Contains(method.Name)) return false;

            var containing = method.ContainingType;
            if (containing == null) return false;

            if (AuthorizationSurface.AuthorityTypes.Contains(containing.Name)) return true;

            // A concrete access service reached through a base or an interface it implements. Checking the
            // interface set matters for the ModulePermAttributeBase shape, where the call is on
            // IModuleAccessService resolved out of the DI container.
            foreach (var iface in containing.AllInterfaces)
                if (AuthorizationSurface.AuthorityTypes.Contains(iface.Name)) return true;

            for (var baseType = containing.BaseType; baseType != null; baseType = baseType.BaseType)
                if (AuthorizationSurface.AuthorityTypes.Contains(baseType.Name)) return true;

            return false;
        }

        /// <summary>
        /// Is this call part of the endpoint's own code path — a helper on the controller, on one of its base
        /// types, or a local function / lambda in that scope?
        /// </summary>
        private static bool IsWithinEndpointCodePath(IMethodSymbol target, INamedTypeSymbol controller)
        {
            if (target.MethodKind == MethodKind.LocalFunction) return true;

            var owner = target.ContainingType;
            if (owner == null) return false;

            for (var type = controller; type != null; type = type.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, owner)) return true;

                // An interface the controller itself implements: base-class / interface dispatch inside the
                // controller hierarchy, which the brief requires and the syntactic scanner cannot do.
                foreach (var iface in type.AllInterfaces)
                    if (SymbolEqualityComparer.Default.Equals(iface, owner)) return true;
            }

            return false;
        }

        /// <summary>
        /// The concrete methods a call may actually run. For a direct call that is the method itself; for a call
        /// through an interface the controller implements, it is the controller's implementation.
        /// </summary>
        private static IEnumerable<IMethodSymbol> Candidates(IMethodSymbol target, INamedTypeSymbol controller)
        {
            yield return target;

            if (target.ContainingType?.TypeKind != TypeKind.Interface) yield break;

            for (var type = controller; type != null; type = type.BaseType)
            {
                if (type.FindImplementationForInterfaceMember(target) is IMethodSymbol implementation)
                {
                    yield return implementation;
                    yield break;
                }
            }
        }

        private static bool ClaimsAuthorization(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var fragment in AuthorizationSurface.AuthorizationShapedNameFragments)
                if (name.IndexOf(fragment, System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static void AddUnsupported(InBodyResult result, string description)
        {
            if (!result.UnsupportedHelpers.Contains(description)) result.UnsupportedHelpers.Add(description);
        }

        private static string Describe(IMethodSymbol method) =>
            (method.ContainingType?.Name ?? "?") + "." + method.Name;
    }
}
