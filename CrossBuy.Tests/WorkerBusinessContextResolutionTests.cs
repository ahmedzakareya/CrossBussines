using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CrossBuy.Tests
{
    /// <summary>
    /// PLATFORM — BusinessContext resolution inside worker/system scopes.
    ///
    /// THE DEFECT THESE PIN. BusinessContextFactory.Publish() told ICompanyScopeHolder which company a scope
    /// operates as — so query filters worked and a background worker read the right rows — and then discarded
    /// the BusinessContext itself. IBusinessContextAccessor, the seam IBusinessEventService.RecordAsync
    /// resolves through, knew only how to rebuild a context from HTTP. A background scope therefore had a
    /// company but no RESOLVABLE context, and TaskOverdueSweepService produced task_became_overdue
    /// notifications while recording ZERO Task.BecameOverdue events (it counted EventsSkippedNoContext rather
    /// than throwing, which is why the gap was visible instead of silent).
    ///
    /// The fix binds the context ForWorker/ForSystem create to the DI scope that created it, and the accessor
    /// consults that binding before attempting HTTP resolution.
    ///
    /// WHY A SCOPED FIELD RATHER THAN AN AMBIENT SLOT is the load-bearing design decision, so the isolation
    /// tests below are the ones that matter: a scoped field cannot leak between companies, iterations or
    /// parallel workers, because the container already gives each its own instance. An AsyncLocal would have
    /// to prove the same properties against execution-context flow instead.
    /// </summary>
    public class WorkerBusinessContextResolutionTests
    {
        private const int CompanyOne = 1;
        private const int CompanySixtyFive = 65;

        private static IBusinessContextAccessor AccessorOver(IBusinessContextFactory factory)
            => new BusinessContextAccessor(factory);

        // =========================================================================================
        // Worker / system resolution — the defect itself
        // =========================================================================================

        [Fact]
        public async Task A_worker_bound_scope_resolves_its_context_without_any_http_request()
        {
            using var host = new PlatformTestHost();
            var factory = host.Contexts(scope: new CompanyScopeHolder());
            var accessor = AccessorOver(factory);

            // Before binding, nothing is resolvable — there is no request and no worker context.
            Assert.Null(await accessor.TryGetCurrentAsync());

            factory.ForWorker(CompanySixtyFive);

            var context = await accessor.TryGetCurrentAsync();
            Assert.NotNull(context);
            Assert.Equal(CompanySixtyFive, context!.CompanyId);
            Assert.Equal(BusinessContextSource.Worker, context.Source);

            // A worker is NOT system: it must not inherit SystemContextPolicy's actions just by being headless.
            Assert.False(context.IsSystem);
        }

        [Fact]
        public async Task A_system_bound_scope_resolves_and_keeps_system_semantics()
        {
            using var host = new PlatformTestHost();
            var factory = host.Contexts(scope: new CompanyScopeHolder());
            var accessor = AccessorOver(factory);

            factory.ForSystem(CompanySixtyFive);

            var context = await accessor.GetCurrentAsync();
            Assert.Equal(CompanySixtyFive, context.CompanyId);
            Assert.Equal(BusinessContextSource.System, context.Source);
            Assert.True(context.IsSystem);
        }

        [Fact]
        public async Task The_binding_survives_an_earlier_negative_answer_in_the_same_scope()
        {
            // The accessor caches the ABSENCE of an HTTP context. A worker that asks first (a health probe, a
            // log line) and binds second must still resolve — otherwise the fix would work only when nothing
            // happened to ask early, which is exactly the kind of ordering dependency that produces a flake.
            using var host = new PlatformTestHost();
            var factory = host.Contexts(scope: new CompanyScopeHolder());
            var accessor = AccessorOver(factory);

            Assert.Null(await accessor.TryGetCurrentAsync());   // caches the negative

            factory.ForWorker(CompanySixtyFive);

            var context = await accessor.TryGetCurrentAsync();
            Assert.NotNull(context);
            Assert.Equal(CompanySixtyFive, context!.CompanyId);
        }

        // =========================================================================================
        // HTTP behaviour unchanged
        // =========================================================================================

        [Fact]
        public async Task An_http_scope_binds_nothing_and_resolves_exactly_as_before()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = 7, FirstName = "T", LastName = "T", FullName = "T", FullNameEn = "T",
                EmpCompanyID = CompanyOne, IsActive = true, Address = "-", PhoneNumber = "-",
                Email = "e7@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
                UserId = "user-7",
            });
            await host.Db.SaveChangesAsync();

            var http = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-7") },
                        "test")),
                },
            };

            var factory = host.Contexts(http: http, scope: new CompanyScopeHolder());

            // The HTTP path does not bind: ForWorker/ForSystem are never called in a request scope, so the
            // accessor takes its original route.
            Assert.Null(factory.ScopeBoundContext);

            var context = await AccessorOver(factory).GetCurrentAsync();
            Assert.Equal(CompanyOne, context.CompanyId);
            Assert.Equal(BusinessContextSource.Http, context.Source);
            Assert.Null(factory.ScopeBoundContext);
        }

        // =========================================================================================
        // Fail-closed
        // =========================================================================================

        [Fact]
        public async Task An_unbound_headless_scope_still_fails_closed()
        {
            // The fix must not become a way to GET a context. A worker that forgets to bind resolves nothing,
            // and the throwing accessor still throws — no default company, no company 1.
            using var host = new PlatformTestHost();
            var accessor = AccessorOver(host.Contexts(scope: new CompanyScopeHolder()));

            Assert.Null(await accessor.TryGetCurrentAsync());
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(() => accessor.GetCurrentAsync());
        }

        [Fact]
        public void Binding_a_worker_scope_to_no_company_is_refused()
        {
            using var host = new PlatformTestHost();
            var factory = host.Contexts(scope: new CompanyScopeHolder());

            Assert.ThrowsAny<Exception>(() => factory.ForWorker(0));
        }

        // =========================================================================================
        // Isolation — the properties that justify a scoped field over ambient state
        // =========================================================================================

        [Fact]
        public async Task Sequential_company_scopes_do_not_bleed_into_one_another()
        {
            // WorkerCompanyRunner walks companies in order. Each gets its own scope, so each gets its own
            // factory instance and its own binding.
            using var host = new PlatformTestHost();

            var a = host.Contexts(scope: new CompanyScopeHolder());
            a.ForWorker(CompanyOne);
            Assert.Equal(CompanyOne, (await AccessorOver(a).GetCurrentAsync()).CompanyId);

            var b = host.Contexts(scope: new CompanyScopeHolder());
            b.ForWorker(CompanySixtyFive);
            Assert.Equal(CompanySixtyFive, (await AccessorOver(b).GetCurrentAsync()).CompanyId);

            // The first scope is untouched by the second.
            Assert.Equal(CompanyOne, (await AccessorOver(a).GetCurrentAsync()).CompanyId);
        }

        [Fact]
        public async Task A_nested_scope_inherits_nothing_and_must_bind_its_own_context()
        {
            // Inheritance would be the leak: a nested scope silently operating as its parent's company is how
            // a cross-tenant write happens. Fail-closed is the correct answer.
            using var host = new PlatformTestHost();

            var outer = host.Contexts(scope: new CompanyScopeHolder());
            outer.ForWorker(CompanySixtyFive);

            var inner = host.Contexts(scope: new CompanyScopeHolder());
            Assert.Null(inner.ScopeBoundContext);
            Assert.Null(await AccessorOver(inner).TryGetCurrentAsync());

            inner.ForWorker(CompanyOne);
            Assert.Equal(CompanyOne, (await AccessorOver(inner).GetCurrentAsync()).CompanyId);
            Assert.Equal(CompanySixtyFive, (await AccessorOver(outer).GetCurrentAsync()).CompanyId);
        }

        [Fact]
        public async Task Parallel_company_scopes_never_share_a_context()
        {
            // The property an AsyncLocal would have had to prove against execution-context flow. Here it falls
            // out of the container: 200 interleaved scopes, each asserting its own company.
            using var host = new PlatformTestHost();

            var companies = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? CompanyOne : CompanySixtyFive).ToArray();

            var observed = await Task.WhenAll(companies.Select(async expected =>
            {
                await Task.Yield();
                var factory = host.Contexts(scope: new CompanyScopeHolder());
                factory.ForWorker(expected);
                await Task.Yield();
                var context = await AccessorOver(factory).GetCurrentAsync();
                return (expected, actual: context.CompanyId);
            }));

            Assert.All(observed, o => Assert.Equal(o.expected, o.actual));
        }

        // =========================================================================================
        // THE CROSS-COMPANY GUARD
        // =========================================================================================

        [Fact]
        public async Task A_worker_for_company_65_cannot_be_rebound_to_company_1()
        {
            // Two different companies in one scope is a bug. ICompanyScopeHolder already keeps its FIRST
            // company (Publish logs and swallows the conflict so one bad call cannot fail an otherwise good
            // pass), so the binding must keep the first too — if it took the LAST, the query filter would read
            // company 65 while a recorded event claimed company 1, which is worse than either value alone.
            using var host = new PlatformTestHost();
            var holder = new CompanyScopeHolder();
            var factory = host.Contexts(scope: holder);

            factory.ForWorker(CompanySixtyFive);
            factory.ForWorker(CompanyOne);          // conflicting rebind — logged and ignored

            var context = await AccessorOver(factory).GetCurrentAsync();

            Assert.Equal(CompanySixtyFive, context.CompanyId);
            Assert.Equal(CompanySixtyFive, holder.CompanyId);

            // The binding and the holder agree. That agreement is the invariant: an event recorded through
            // this context lands in the same company the query filter is reading.
            Assert.Equal(holder.CompanyId, context.CompanyId);
        }

        [Fact]
        public async Task A_worker_context_ignores_a_company_override_so_it_cannot_record_as_another_company()
        {
            // BusinessEventService honours record.CompanyIdOverride ONLY for a System context
            // (BusinessEventService: `context.IsSystem && record.CompanyIdOverride is > 0`). A Worker context
            // is not system, so a worker bound to 65 cannot record an event as company 1 even if a caller asks.
            using var host = new PlatformTestHost();
            var factory = host.Contexts(scope: new CompanyScopeHolder());
            factory.ForWorker(CompanySixtyFive);

            var context = await AccessorOver(factory).GetCurrentAsync();

            Assert.False(context.IsSystem);
            Assert.Equal(CompanySixtyFive, context.CompanyId);
        }
    }
}
