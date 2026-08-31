using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE RESTAURANT INTELLIGENCE SCREEN — the surface, not the arithmetic.
    //
    // The equations are proven where they belong: PimInventoryIntelligenceTests drives the real
    // service against a real SQL probe database and pins Theoretical, Actual, the two waste subsets,
    // ProductiveActual, Variance, UoM normalisation, the semi-finished rule and the replenishment
    // thresholds. NOTHING HERE RE-ASSERTS ANY OF THAT — a second, weaker copy of an equation is how
    // two definitions of the same number start disagreeing.
    //
    // What this file proves is what the screen adds, and every one of these is a way a read screen can
    // do harm:
    //
    //   * it never reads without a resolved company, and the company never comes from the query string;
    //   * it never writes — opening a report is not an operational fact;
    //   * it carries the module's own read authority at the endpoint;
    //   * it renders no raw key and no hardcoded sentence in the wrong language.
    // =============================================================================================
    public class RestaurantIntelligenceScreenTests
    {
        /// Records every question asked of the intelligence service, and answers with empty results —
        /// so a test can assert the screen did NOT ask, which is the interesting direction.
        private sealed class RecordingIntelligence : IRestaurantInventoryIntelligenceService
        {
            public readonly List<string> Calls = new();
            public ConsumptionVarianceResult Variance = new();

            public Task<ConsumptionVarianceResult> VarianceAsync(int companyId, int branchId, DateTime fromDate,
                DateTime toDate, int? itemId = null, CancellationToken ct = default)
            { Calls.Add($"Variance({companyId},{branchId})"); return Task.FromResult(Variance); }

            public Task<IReadOnlyList<ReplenishmentRecommendation>> ReplenishmentAsync(int companyId, int branchId,
                CancellationToken ct = default)
            {
                Calls.Add($"Replenishment({companyId},{branchId})");
                return Task.FromResult<IReadOnlyList<ReplenishmentRecommendation>>(
                    Array.Empty<ReplenishmentRecommendation>());
            }

            public Task<IReadOnlyList<ShortageSignal>> ShortagesAsync(int companyId, int branchId, DateTime fromDate,
                DateTime toDate, CancellationToken ct = default)
            {
                Calls.Add($"Shortages({companyId},{branchId})");
                return Task.FromResult<IReadOnlyList<ShortageSignal>>(Array.Empty<ShortageSignal>());
            }
        }

        private sealed class FixedCompany : IRequestCompanyResolver
        {
            private readonly int _companyId;
            private readonly bool _ok;
            public FixedCompany(int companyId, bool ok) { _companyId = companyId; _ok = ok; }

            public Task<CompanyResolution> ResolveAsync(
                int? requestSuppliedCompanyId = null, CancellationToken ct = default)
                => Task.FromResult(_ok
                    ? CompanyResolution.Resolved(_companyId, employeeId: 1, branchId: null)
                    : CompanyResolution.Unresolved("no signed-in employee"));
        }

        private static (RestaurantIntelligenceController Controller, RecordingIntelligence Intel) Build(
            int companyId = 7, bool resolves = true)
        {
            var intel = new RecordingIntelligence();
            return (new RestaurantIntelligenceController(intel, new FixedCompany(companyId, resolves)), intel);
        }

        private static RestaurantIntelligenceVm Model(IActionResult result)
            => Assert.IsType<RestaurantIntelligenceVm>(Assert.IsType<ViewResult>(result).Model);

        // -----------------------------------------------------------------------------------------
        // NOTHING IS READ WITHOUT A RESOLVED COMPANY
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unresolved_company_reads_nothing_at_all()
        {
            // The failure this guards: a screen that resolved no company and then queried anyway would
            // read whatever companyId 0 happened to match — or, worse, whatever the default was.
            var (controller, intel) = Build(resolves: false);

            var model = Model(await controller.Index(branchId: 3, from: null, to: null));

            Assert.Empty(intel.Calls);
            Assert.NotNull(model.MessageKey);
            Assert.Empty(model.Variance);
            Assert.Empty(model.Replenishment);
            Assert.Empty(model.Shortages);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task No_branch_means_no_query_rather_than_a_query_across_every_branch(int? branchId)
        {
            var (controller, intel) = Build();

            var model = Model(await controller.Index(branchId, from: null, to: null));

            Assert.Empty(intel.Calls);
            Assert.NotNull(model.MessageKey);
        }

        [Fact]
        public async Task The_company_comes_from_the_resolver_and_the_query_string_cannot_change_it()
        {
            // The action's signature is the proof: there is no companyId parameter to supply. Everything
            // asked of the service carries the RESOLVED company.
            var parameters = typeof(RestaurantIntelligenceController).GetMethod("Index")!
                .GetParameters().Select(p => p.Name!).ToList();
            foreach (var forbidden in new[] { "companyId", "company", "co", "tenantId" })
                Assert.DoesNotContain(forbidden, parameters, StringComparer.OrdinalIgnoreCase);

            var (controller, intel) = Build(companyId: 7);
            await controller.Index(branchId: 3, from: null, to: null);

            Assert.All(intel.Calls, c => Assert.Contains("(7,", c, StringComparison.Ordinal));
        }

        // -----------------------------------------------------------------------------------------
        // THE READ IS A READ
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Opening_the_screen_asks_the_three_read_questions_and_nothing_more()
        {
            var (controller, intel) = Build();

            await controller.Index(branchId: 3, from: null, to: null);

            // Exactly the three reads the screen renders. A fourth call appearing here would mean the
            // screen had grown a side effect, which is the thing a report must never have.
            Assert.Equal(3, intel.Calls.Count);
            Assert.Contains(intel.Calls, c => c.StartsWith("Variance(", StringComparison.Ordinal));
            Assert.Contains(intel.Calls, c => c.StartsWith("Replenishment(", StringComparison.Ordinal));
            Assert.Contains(intel.Calls, c => c.StartsWith("Shortages(", StringComparison.Ordinal));
        }

        [Fact]
        public void The_controller_holds_no_writer_of_any_kind()
        {
            // A recommendation is advice, not a purchase order. Batch 3 issues no PR, PO, transfer or
            // work order automatically, and the cheapest way to keep that true is for this controller to
            // have nothing it could write WITH: its constructor takes a read service and a company
            // resolver, and that is the whole of its reach.
            var injected = typeof(RestaurantIntelligenceController)
                .GetConstructors().Single().GetParameters()
                .Select(p => p.ParameterType.Name).ToList();

            Assert.Equal(
                new[] { nameof(IRestaurantInventoryIntelligenceService), nameof(IRequestCompanyResolver) },
                injected);
        }

        [Fact]
        public void The_screen_has_no_mutating_endpoint()
        {
            var actions = typeof(RestaurantIntelligenceController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .ToList();

            Assert.All(actions, m =>
            {
                Assert.Null(m.GetCustomAttribute<HttpPostAttribute>());
                Assert.Null(m.GetCustomAttribute<HttpPutAttribute>());
                Assert.Null(m.GetCustomAttribute<HttpDeleteAttribute>());
                Assert.Null(m.GetCustomAttribute<HttpPatchAttribute>());
            });
        }

        // -----------------------------------------------------------------------------------------
        // AUTHORIZATION AND LANGUAGE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_screen_requires_a_session_and_the_modules_own_read_authority()
        {
            // Without SessionValidation the page is reachable unauthenticated: the resolver would fail
            // and it would render empty, which LOOKS safe. "It shows nothing" is a data accident, not an
            // authorization — so the check is stated rather than relied upon as a side effect.
            Assert.NotNull(typeof(RestaurantIntelligenceController)
                .GetCustomAttributes(inherit: true)
                .FirstOrDefault(a => a.GetType().Name == "SessionValidationAttribute"));

            Assert.NotNull(typeof(RestaurantIntelligenceController).GetMethod("Index")!
                .GetCustomAttributes(inherit: true)
                .FirstOrDefault(a => a.GetType().Name == "InvPermAttribute"));
        }

        [Fact]
        public async Task The_screens_own_message_is_a_RESOURCE_KEY_and_never_a_sentence_in_one_language()
        {
            // The held version hardcoded an Arabic sentence here, which reads as a bug to every English
            // and French user. The controller now names WHICH message; the view resolves it.
            var (controller, _) = Build(resolves: false);
            var model = Model(await controller.Index(branchId: 0, from: null, to: null));

            Assert.NotNull(model.MessageKey);
            Assert.All(model.MessageKey!, c => Assert.True(c < 0x0590,
                "the controller's message key must be a plain resource key, not localised text: " + model.MessageKey));

            // Service-produced text travels on the OTHER field, so a sentence is never looked up as a key
            // and rendered with a missing-resource marker around it.
            Assert.Null(model.Message);
        }

        [Fact]
        public void Every_key_the_view_asks_for_exists_in_all_three_resource_files()
        {
            // The failure this catches is the one §H names: an Arabic reader handed a raw English key.
            // IViewLocalizer returns the KEY when a resource is missing, silently and with no error, so
            // nothing but a test like this notices.
            var root = RepoRoot();
            var view = File.ReadAllText(Path.Combine(root, "CrossBuy", "Views", "RestaurantIntelligence", "Index.cshtml"));

            var keys = System.Text.RegularExpressions.Regex.Matches(view, "Localizer\\[\"([^\"]+)\"\\]")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            // The controller's key is resolved through the same view resource.
            keys.Add("Choose a branch to see inventory intelligence");

            Assert.NotEmpty(keys);

            foreach (var culture in new[] { "ar", "en", "fr" })
            {
                var path = Path.Combine(root, "CrossBuy", "Resources", "Views", "RestaurantIntelligence",
                    $"Index.{culture}.resx");
                Assert.True(File.Exists(path), "missing resource file: " + path);

                var resx = File.ReadAllText(path);
                foreach (var key in keys)
                    Assert.True(resx.Contains("\"" + System.Security.SecurityElement.Escape(key) + "\"", StringComparison.Ordinal),
                        $"{culture}: no resource for \"{key}\" — an Arabic or French reader would be shown the English key.");
            }
        }

        [Fact]
        public void The_arabic_resource_is_actually_in_arabic()
        {
            // A resx that exists but was filled with English is the same bug wearing a different hat, and
            // it passes the test above. This one reads the values.
            var path = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "RestaurantIntelligence",
                "Index.ar.resx");
            var values = System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(path), "<value>([^<]+)</value>")
                .Select(m => m.Groups[1].Value)
                .Where(v => !v.StartsWith("text/", StringComparison.Ordinal)
                            && !v.Contains("System.Resources", StringComparison.Ordinal)
                            && !v.StartsWith("2.0", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(values);
            Assert.All(values, v => Assert.Contains(v, c => c >= 0x0600 && c <= 0x06FF));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "Views")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
