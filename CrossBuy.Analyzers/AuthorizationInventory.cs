using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers
{
    /// <summary>
    /// Discovers every mutating endpoint in a compilation and classifies its authorization.
    ///
    /// This type is the SINGLE derivation. The analyzer uses it to raise diagnostics and the reconciliation
    /// harness uses it to produce the headline counts, so the enforced rule and the reported number cannot
    /// diverge. Stage 1 lost a batch to exactly that divergence: the documents quoted one scan and the evidence
    /// CSVs came from another.
    /// </summary>
    internal static class AuthorizationInventory
    {
        /// <summary>
        /// Lifecycle members that are not endpoints. Filtered by name because they are the MVC filter hooks, and
        /// a controller may declare them without an override modifier when implementing IActionFilter directly.
        /// </summary>
        private static readonly HashSet<string> NonEndpointMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Dispose",
            "OnActionExecuting",
            "OnActionExecuted",
            "OnActionExecutionAsync",
        };

        internal static AuthorizationInventoryResult Build(Compilation compilation, CancellationToken ct)
        {
            var result = new AuthorizationInventoryResult();
            var resolver = new AuthorizationResolver(compilation);

            foreach (var controller in Controllers(compilation, ct))
            {
                result.Controllers.Add(controller.Name);

                var declaration = controller.Locations.FirstOrDefault(l => l.IsInSource);
                if (declaration != null && !result.ControllerLocations.ContainsKey(controller.Name))
                    result.ControllerLocations[controller.Name] = declaration;

                foreach (var facts in Endpoints(controller, resolver, ct))
                {
                    if (!facts.IsMutating) continue;
                    result.Endpoints.Add(facts);
                }
            }

            result.MutatingCount = result.Endpoints.Count;
            result.AttributeProtectedCount = result.Endpoints.Count(e => e.IsAttributeProtected);
            result.InBodyProtectedCount = result.Endpoints.Count(e => e.Authorization == AuthorizationKind.InBody);
            result.GapCount = result.Endpoints.Count(e => !e.IsProtected);

            return result;
        }

        /// <summary>
        /// Controller types, scoped to the Controllers folder.
        ///
        /// THE SCOPE IS DELIBERATE AND IT IS A DECLARED LIMITATION. The accepted Stage 1 measurement scanned
        /// CrossBuy/Controllers/** only, so 388 means "mutating endpoints under Controllers/". Widening the scope
        /// here would change the number without changing the code, which is the one thing the brief forbids. A
        /// controller placed outside that folder would therefore be invisible — recorded as a gap in the design
        /// document rather than silently accepted.
        /// </summary>
        internal static IEnumerable<INamedTypeSymbol> Controllers(Compilation compilation, CancellationToken ct)
        {
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace, ct))
            {
                if (type.TypeKind != TypeKind.Class) continue;
                if (!LooksLikeController(type)) continue;
                if (!DeclaredUnderControllersFolder(type)) continue;
                yield return type;
            }
        }

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns, CancellationToken ct)
        {
            foreach (var member in ns.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (member is INamespaceSymbol child)
                {
                    foreach (var nested in AllTypes(child, ct)) yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in type.GetTypeMembers()) yield return nested;
                }
            }
        }

        /// <summary>
        /// A controller by SEMANTICS where the reference graph allows it, by name where it does not.
        ///
        /// The base-type walk is the primary test and it is what gives base-controller support: a controller
        /// deriving from an abstract project base still reaches ControllerBase. The name test is a fallback rather
        /// than the rule, because a name is not a contract — but dropping a type whose base failed to bind would
        /// silently remove endpoints from the measured surface, which is the worse error.
        /// </summary>
        private static bool LooksLikeController(INamedTypeSymbol type)
        {
            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.Name == "ControllerBase" || baseType.Name == "Controller") return true;
            }

            foreach (var attribute in type.GetAttributes())
            {
                var name = AttributeFacts.Name(attribute);
                if (name == "ApiController" || name == "Controller") return true;
            }

            return type.Name.EndsWith("Controller", StringComparison.Ordinal);
        }

        private static bool DeclaredUnderControllersFolder(INamedTypeSymbol type)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree.FilePath;
                if (string.IsNullOrEmpty(path)) continue;

                var normalized = path.Replace('\\', '/');
                if (normalized.IndexOf("/Controllers/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        internal static IEnumerable<EndpointFacts> Endpoints(
            INamedTypeSymbol controller,
            AuthorizationResolver resolver,
            CancellationToken ct)
        {
            var classAttributes = controller.GetAttributes().Select(AttributeFacts.Name).ToList();
            var inheritedPermissions = classAttributes
                .Where(AuthorizationSurface.PermissionAttributes.Contains)
                .ToList();

            var isApi = classAttributes.Contains("ApiController") || DerivesFromControllerBaseOnly(controller);

            foreach (var member in controller.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (!(member is IMethodSymbol method)) continue;
                if (method.MethodKind != MethodKind.Ordinary) continue;
                if (method.DeclaredAccessibility != Accessibility.Public) continue;
                if (method.IsStatic) continue;                              // MVC never routes to a static method
                if (NonEndpointMembers.Contains(method.Name)) continue;
                if (method.DeclaringSyntaxReferences.Length == 0) continue;  // compiler-generated

                var attributeNames = method.GetAttributes().Select(AttributeFacts.Name).ToList();
                if (attributeNames.Any(AuthorizationSurface.NonActionAttributes.Contains)) continue;

                var verbs = AttributeFacts.HttpVerbs(method);
                var facts = new EndpointFacts
                {
                    Controller = controller.Name,
                    Action = method.Name,
                    HttpMethods = string.Join("|", verbs),
                    IsMutating = AttributeFacts.IsMutating(verbs),
                    IsApi = isApi,
                };

                var location = method.Locations.FirstOrDefault(l => l.IsInSource);
                if (location != null)
                {
                    facts.Location = location;
                    facts.FilePath = location.SourceTree?.FilePath ?? string.Empty;
                    facts.Line = location.GetLineSpan().StartLinePosition.Line + 1;
                }

                facts.HasAuthenticationOnly =
                    attributeNames.Any(AuthorizationSurface.AuthenticationOnlyAttributes.Contains) ||
                    classAttributes.Any(AuthorizationSurface.AuthenticationOnlyAttributes.Contains);
                facts.HasAntiForgery = attributeNames.Any(AuthorizationSurface.AntiForgeryAttributes.Contains);
                facts.HasLaneGuard =
                    attributeNames.Any(AuthorizationSurface.RoutingOnlyAttributes.Contains) ||
                    classAttributes.Any(AuthorizationSurface.RoutingOnlyAttributes.Contains);
                facts.HasEnvironmentGate =
                    attributeNames.Any(AuthorizationSurface.EnvironmentGateAttributes.Contains) ||
                    classAttributes.Any(AuthorizationSurface.EnvironmentGateAttributes.Contains);
                facts.IsAnonymousDeclared = attributeNames.Any(AuthorizationSurface.AnonymousAttributes.Contains);

                // ---- 1. an action-level permission attribute ----
                var actionPermissions = attributeNames.Where(AuthorizationSurface.PermissionAttributes.Contains).ToList();
                if (actionPermissions.Count > 0)
                {
                    facts.Authorization = AuthorizationKind.ActionAttribute;
                    facts.PermissionAttributes.AddRange(actionPermissions);
                }
                // ---- 2. a class-level permission attribute, from ANY partial declaration ----
                else if (inheritedPermissions.Count > 0)
                {
                    facts.Authorization = AuthorizationKind.InheritedAttribute;
                    facts.PermissionAttributes.AddRange(inheritedPermissions);
                }

                // The body is resolved even for an attribute-protected action, because CBA003 must be able to
                // report an unrecognised guard regardless of how the action is otherwise protected.
                if (facts.IsMutating)
                {
                    var inBody = resolver.Resolve(method, controller, ct);
                    facts.UnsupportedAuthorizationHelpers.AddRange(inBody.UnsupportedHelpers);

                    if (facts.Authorization == AuthorizationKind.None && inBody.Authorized)
                    {
                        facts.Authorization = AuthorizationKind.InBody;
                        facts.InBodyEvidence = inBody.Authority;
                        facts.InBodyChain = inBody.Chain;
                    }
                }

                yield return facts;
            }
        }

        private static bool DerivesFromControllerBaseOnly(INamedTypeSymbol type)
        {
            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.Name == "Controller") return false;
                if (baseType.Name == "ControllerBase") return true;
            }
            return false;
        }
    }
}
