using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CrossBuy.Analyzers
{
    /// <summary>
    /// Stage 2A Batch 00-A — the authorization guardrail.
    ///
    /// It answers one question per mutating endpoint: does anything decide whether this caller may change state?
    /// Diagnostics are raised against the frozen Stage 1 baseline, so accepted debt is a warning and NEW debt is
    /// the thing that can be configured to break the build.
    ///
    /// STRUCTURE, and why it is not per-symbol.
    ///
    /// Two of the six diagnostics are compilation-wide by nature: CBA004 asks whether a baseline entry still
    /// matches anything, which no single symbol can answer, and the reconciliation counts are only meaningful over
    /// the whole compilation. So the inventory is built once per compilation in a compilation-end action. That is
    /// slower than a symbol action; it is also the only way to detect a stale allowance, which is the difference
    /// between a shrink-only baseline and a baseline that quietly keeps dead entries.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AuthorizationAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                AuthorizationDiagnostics.NewUnprotectedEndpoint,
                AuthorizationDiagnostics.AuthenticationWithoutAuthorization,
                AuthorizationDiagnostics.UnsupportedAuthorizationHelper,
                AuthorizationDiagnostics.StaleBaselineEntry,
                AuthorizationDiagnostics.UndeclaredAnonymousEndpoint,
                AuthorizationDiagnostics.InvalidSuppression);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();

            // Generated code is analyzed too. An authorization gap in generated code is still an authorization
            // gap, and excluding it would create a place to hide one.
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            context.RegisterCompilationStartAction(start =>
            {
                var baseline = LoadBaseline(start.Options);

                // CBA006 is a syntax question and is answered per tree, so a suppression is reported even in a
                // file that declares no endpoint.
                start.RegisterSyntaxTreeAction(tree => ReportSuppressions(tree));

                start.RegisterCompilationEndAction(end =>
                {
                    var inventory = AuthorizationInventory.Build(end.Compilation, end.CancellationToken);
                    Report(end, inventory, baseline);
                });
            });
        }

        // -------------------------------------------------------------------------------------------------
        // Baseline
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the baseline from AdditionalFiles.
        ///
        /// An analyzer may not open files: the compiler sandboxes it precisely so a build stays reproducible from
        /// its declared inputs. So the baseline is a declared input, and a project that consumes the analyzer must
        /// add it — which also means the baseline used by the build is visible in the project file rather than
        /// discovered by path guessing.
        /// </summary>
        private static BaselineDocument? LoadBaseline(AnalyzerOptions options)
        {
            foreach (var file in options.AdditionalFiles)
            {
                var path = file.Path;
                if (string.IsNullOrEmpty(path)) continue;

                var slash = path.LastIndexOfAny(new[] { '/', '\\' });
                var name = slash >= 0 ? path.Substring(slash + 1) : path;
                if (!string.Equals(name, AuthorizationSurface.BaselineFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = file.GetText();
                if (text == null) continue;

                return BaselineDocument.TryParse(text.ToString());
            }

            return null;
        }

        // -------------------------------------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------------------------------------

        private static void Report(
            CompilationAnalysisContext context,
            AuthorizationInventoryResult inventory,
            BaselineDocument? baseline)
        {
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var endpoint in inventory.Endpoints)
            {
                var location = LocationOf(endpoint);

                // CBA003 — an unrecognised guard, reported whether or not the endpoint is otherwise protected.
                foreach (var helper in endpoint.UnsupportedAuthorizationHelpers)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        AuthorizationDiagnostics.UnsupportedAuthorizationHelper, location, endpoint.Id, helper));
                }

                if (endpoint.IsProtected) continue;

                matched.Add(endpoint.Id);

                var inBaseline = baseline != null && baseline.Entries.TryGetValue(endpoint.Id, out var entry);
                var declaredAnonymous = inBaseline &&
                                        baseline!.Entries[endpoint.Id].IsAnonymousByDesign;

                // CBA005 — anonymous on purpose has to be declared on purpose.
                if (endpoint.IsAnonymousDeclared && !declaredAnonymous)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        AuthorizationDiagnostics.UndeclaredAnonymousEndpoint, location, endpoint.Id));
                }

                // CBA002 — authentication, anti-forgery or the lane guard, and nothing that decides permission.
                var evidence = AuthenticationEvidence(endpoint);
                if (evidence != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        AuthorizationDiagnostics.AuthenticationWithoutAuthorization, location, endpoint.Id, evidence));
                }

                // CBA001 — new debt. Suppressed when no baseline was supplied, because 143 accepted entries would
                // otherwise be reported as new debt on a misconfiguration. That degradation is declared in the
                // design document rather than silently relied on.
                if (baseline != null && !inBaseline)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        AuthorizationDiagnostics.NewUnprotectedEndpoint, location, endpoint.Id));
                }
            }

            if (baseline == null) return;

            // CBA004 — a baseline entry that no longer matches an unprotected endpoint. Covers all three shapes:
            // the endpoint was protected, it was deleted, or it was renamed (the old id stops matching while the
            // new id shows up as CBA001, so a rename costs two diagnostics and cannot pass as pre-existing debt).
            foreach (var entry in baseline.Entries.Values)
            {
                if (matched.Contains(entry.Id)) continue;

                var reason = Reason(inventory, entry);

                // Anchored to the controller when it still exists. A real build proved why this matters: reported at
                // Location.None, the diagnostic matches no `[*.cs]` .editorconfig section, so its configured severity
                // is never applied and it stays a Warning. A vanished controller has nothing left to point at, which
                // is why a global analyzer config also declares the severity — see .globalconfig.
                var location = StaleEntryLocation(inventory, entry);

                context.ReportDiagnostic(Diagnostic.Create(
                    AuthorizationDiagnostics.StaleBaselineEntry, location, entry.Id, reason));
            }
        }

        /// <summary>
        /// The best available source location for a stale entry: the endpoint itself if it still exists, else its
        /// controller, else nothing.
        /// </summary>
        private static Location StaleEntryLocation(AuthorizationInventoryResult inventory, BaselineEntry entry)
        {
            var endpoint = inventory.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, entry.Id, StringComparison.Ordinal));

            if (endpoint != null && endpoint.Location != null) return endpoint.Location;

            return inventory.ControllerLocations.TryGetValue(entry.Controller, out var controller)
                ? controller
                : Location.None;
        }

        private static string Reason(AuthorizationInventoryResult inventory, BaselineEntry entry)
        {
            var endpoint = inventory.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, entry.Id, StringComparison.Ordinal));

            if (endpoint != null)
            {
                return endpoint.Authorization == AuthorizationKind.InBody
                    ? "it is now authorized in-body via " + endpoint.InBodyEvidence
                    : "it is now protected by " + string.Join("+", endpoint.PermissionAttributes);
            }

            return inventory.Controllers.Contains(entry.Controller)
                ? "the action no longer exists on " + entry.Controller
                : "the controller no longer exists";
        }

        /// <summary>
        /// The non-authorization controls an endpoint does carry, named so CBA002 says something actionable
        /// instead of repeating CBA001. Returns null when there is nothing to name — a bare unprotected endpoint
        /// is CBA001's business, not CBA002's.
        /// </summary>
        private static string? AuthenticationEvidence(EndpointFacts endpoint)
        {
            var parts = new List<string>();
            if (endpoint.HasAuthenticationOnly) parts.Add("authentication ([Authorize]/[SessionValidation])");
            if (endpoint.HasAntiForgery) parts.Add("an anti-forgery token");
            if (endpoint.HasLaneGuard) parts.Add("PosLaneActivityGuard (which checks no role)");
            if (endpoint.HasEnvironmentGate) parts.Add("DevOnly (an environment gate, not a permission)");

            return parts.Count == 0 ? null : string.Join(" and ", parts);
        }

        /// <summary>
        /// The endpoint's own declaration location. Carried on the facts rather than re-derived by scanning every
        /// syntax tree for a matching path — which was both O(trees) per diagnostic and case-sensitive on a path.
        /// </summary>
        private static Location LocationOf(EndpointFacts endpoint) => endpoint.Location ?? Location.None;

        // -------------------------------------------------------------------------------------------------
        // CBA006 — suppression detection
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reports any attempt to suppress this analyzer's diagnostics, in either of the two shapes that work:
        /// a <c>#pragma warning disable CBA00x</c> and a <c>[SuppressMessage]</c> naming one of the ids or the
        /// category.
        ///
        /// DECLARED LIMITATION: a suppression of CBA006 itself still works, because Roslyn applies pragmas to
        /// every diagnostic including this one. No analyzer can close that; it is closed by review — the
        /// suppression is visible in the diff, and CI can grep for the ids. Recorded in the design document as a
        /// residual risk rather than presented as covered.
        /// </summary>
        private static void ReportSuppressions(SyntaxTreeAnalysisContext context)
        {
            var root = context.Tree.GetRoot(context.CancellationToken);

            foreach (var trivia in root.DescendantTrivia())
            {
                if (!trivia.HasStructure) continue;
                if (!(trivia.GetStructure() is PragmaWarningDirectiveTriviaSyntax pragma)) continue;
                if (!pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)) continue;

                foreach (var code in pragma.ErrorCodes)
                {
                    var id = code.ToString().Trim();
                    if (!IsOurId(id)) continue;

                    context.ReportDiagnostic(Diagnostic.Create(
                        AuthorizationDiagnostics.InvalidSuppression, code.GetLocation(), id));
                }
            }

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                var name = attribute.Name.ToString();
                if (name.IndexOf("SuppressMessage", StringComparison.Ordinal) < 0) continue;

                var text = attribute.ArgumentList?.ToString() ?? string.Empty;
                var id = OurIdIn(text);
                if (id == null) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    AuthorizationDiagnostics.InvalidSuppression, attribute.GetLocation(), id));
            }
        }

        private static bool IsOurId(string id) =>
            id.Length == 6 &&
            id.StartsWith("CBA00", StringComparison.Ordinal) &&
            id[5] >= '1' && id[5] <= '6';

        private static string? OurIdIn(string text)
        {
            for (var digit = '1'; digit <= '6'; digit++)
            {
                var id = "CBA00" + digit;
                if (text.IndexOf(id, StringComparison.Ordinal) >= 0) return id;
            }

            return text.IndexOf(AuthorizationSurface.DiagnosticCategory, StringComparison.Ordinal) >= 0
                ? AuthorizationSurface.DiagnosticCategory
                : null;
        }
    }
}
