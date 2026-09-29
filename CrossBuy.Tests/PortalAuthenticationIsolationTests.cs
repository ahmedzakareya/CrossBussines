using System.Security.Claims;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // PORTAL AUTHENTICATION ISOLATION — the boundary between the two audiences.
    //
    // THE DEFECT THIS CLOSES. One .AspNetCore.Identity.Application cookie authenticated employees
    // and portal customers alike, and they were told apart only by a database lookup that ran
    // AFTER authentication had already succeeded. That is a check, not a boundary. Meanwhile the
    // global SessionValidationMiddleware demanded an employee session on /ClientPortal, so the
    // portal was in practice reachable only by someone who was ALSO staff — the two faults pointing
    // in opposite directions and both wrong.
    //
    // TWO CONTROLS, PROVED SEPARATELY, which is the point of the split below:
    //
    //   1. The middleware lane removes the portal from the EMPLOYEE SESSION question. It grants
    //      nothing, and section 1 proves both halves — the portal passes, an internal path does not.
    //   2. The dedicated scheme is what actually authorizes the portal. Section 2 proves it exists,
    //      carries its own cookie, and is NOT the default — so a portal cookie never becomes the
    //      principal that ordinary internal [Authorize] reads.
    //
    // WHAT IS STRUCTURAL AND WHY, stated rather than hidden. Program.cs's defaults come from
    // AddIdentity plus the certificate registration, and rebuilding that graph in a test would be
    // testing the replica. So the DEFAULTS claim is split: the DI tests prove the extension method
    // sets no default and names its schemes, and the source tests prove Program.cs calls it without
    // an argument. Neither half alone is the claim.
    // ============================================================================================
    public class PortalAuthenticationIsolationTests
    {
        // ---- 1. the middleware lane ------------------------------------------------------------

        [Fact]
        public async Task A_The_portal_surface_is_no_longer_refused_merely_for_having_no_employee_session()
        {
            foreach (var path in new[] { "/ClientPortal", "/clientportal/invoices", "/PortalAccount/Login" })
            {
                var (nextCalled, context) = await InvokeAsync(path);

                Assert.True(nextCalled, $"{path}: the portal request was stopped by the employee-session gate");
                Assert.NotEqual(StatusCodes.Status302Found, context.Response.StatusCode);
            }
        }

        [Fact]
        public async Task B_The_lane_is_narrow_so_an_internal_path_with_no_employee_session_is_still_refused()
        {
            // THE OTHER HALF OF THE SAME CLAIM. A bypass that let everything through would also make
            // the test above pass, so it proves nothing on its own. These paths deliberately include
            // /Portal, which is the INTERNAL post-login chooser and not the customer portal at all —
            // a prefix written one character shorter would swallow it.
            foreach (var path in new[] { "/Tasks/Index", "/Accounting/Journal", "/Portal", "/PortalChooser" })
            {
                var (nextCalled, context) = await InvokeAsync(path);

                Assert.False(nextCalled, $"{path}: an internal path was let through without an employee session");
                Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
            }
        }

        [Fact]
        public async Task B2_An_employee_session_still_reaches_an_internal_path()
        {
            // Non-vacuity for the test above: if the gate refused EVERYTHING, B would pass for the
            // wrong reason and nobody would notice until the product stopped working.
            var (nextCalled, _) = await InvokeAsync("/Tasks/Index", employeeSession: "7");

            Assert.True(nextCalled);
        }

        // ---- 2. the dedicated scheme -----------------------------------------------------------

        [Fact]
        public void C_The_portal_scheme_is_registered_as_a_cookie_scheme()
        {
            var schemes = Provider().GetRequiredService<IOptions<AuthenticationOptions>>().Value.Schemes;

            var portal = Assert.Single(schemes,
                s => s.Name == PortalAuthenticationDefaults.AuthenticationScheme);
            Assert.Equal(typeof(CookieAuthenticationHandler), portal.HandlerType);
        }

        [Fact]
        public void D_The_portal_cookie_is_not_the_internal_identity_cookie()
        {
            var options = Provider().GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(PortalAuthenticationDefaults.AuthenticationScheme);

            // THE WHOLE STRUCTURAL HALF OF THE BOUNDARY IS THIS ONE STRING. Sharing the internal
            // name would hand the portal the employee audience's cookie, and no amount of policy
            // would put it back.
            Assert.Equal("CrossBuy.Portal", options.Cookie.Name);
            Assert.NotEqual(".AspNetCore.Identity.Application", options.Cookie.Name);
            Assert.NotEqual("CrossBuy.Session", options.Cookie.Name);

            // Settings that are not weaker than the internal cookie's own defaults.
            Assert.True(options.Cookie.HttpOnly);
            Assert.True(options.Cookie.IsEssential);
            Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
            Assert.NotEqual(CookieSecurePolicy.None, options.Cookie.SecurePolicy);
            Assert.Equal(PortalAuthenticationDefaults.LoginPath, options.LoginPath);
            Assert.Equal(PortalAuthenticationDefaults.AccessDeniedPath, options.AccessDeniedPath);
            Assert.True(options.SlidingExpiration);
        }

        [Fact]
        public void E_Registering_the_portal_scheme_makes_it_no_ones_default()
        {
            var options = Provider().GetRequiredService<IOptions<AuthenticationOptions>>().Value;

            // Measured on a collection where the portal registration is the ONLY authentication
            // registration. If AddCrossBuyPortalAuthentication set a default, it would be visible
            // here with nothing else to blame — and in the real application it would silently
            // outrank the identity defaults AddIdentity established earlier in Program.cs.
            foreach (var actual in new[]
            {
                options.DefaultScheme, options.DefaultAuthenticateScheme,
                options.DefaultChallengeScheme, options.DefaultSignInScheme,
                options.DefaultForbidScheme, options.DefaultSignOutScheme,
            })
            {
                Assert.NotEqual(PortalAuthenticationDefaults.AuthenticationScheme, actual);
            }
        }

        [Fact]
        public async Task H_The_default_authorization_policy_does_not_accept_the_portal_scheme()
        {
            var policies = Provider().GetRequiredService<IAuthorizationPolicyProvider>();

            // THE BACKEND BOUNDARY. Ordinary internal [Authorize] runs the DEFAULT policy. If the
            // portal scheme were ever named there, a portal cookie would be authenticated for
            // internal endpoints and a customer would satisfy a staff authorization check.
            var @default = await policies.GetDefaultPolicyAsync();
            Assert.DoesNotContain(PortalAuthenticationDefaults.AuthenticationScheme, @default.AuthenticationSchemes);

            var fallback = await policies.GetFallbackPolicyAsync();
            if (fallback != null)
                Assert.DoesNotContain(PortalAuthenticationDefaults.AuthenticationScheme, fallback.AuthenticationSchemes);
        }

        [Fact]
        public async Task The_portal_policy_names_the_portal_scheme_and_only_it()
        {
            // The mirror of the test above, and just as load-bearing. A policy with NO schemes falls
            // back to the default authenticate scheme - so a portal policy that forgot this line
            // would be satisfied by an INTERNAL identity cookie.
            var policy = await Provider().GetRequiredService<IAuthorizationPolicyProvider>()
                .GetPolicyAsync(PortalAuthenticationDefaults.PolicyName);

            Assert.NotNull(policy);
            Assert.Equal(
                new[] { PortalAuthenticationDefaults.AuthenticationScheme },
                policy!.AuthenticationSchemes);
            Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
        }

        // ---- 3. the claim contract -------------------------------------------------------------

        [Fact]
        public void F_A_portal_principal_carries_a_reference_and_never_an_employee_identity()
        {
            // The contract TAB-3 signs in 3A-0B, asserted here so the shape is fixed before the code
            // that issues it exists.
            Assert.Equal(ClaimTypes.NameIdentifier, PortalAuthenticationDefaults.RequiredClaim);
            Assert.Contains("EmployeeId", PortalAuthenticationDefaults.ForbiddenClaimTypes);

            var principal = PortalPrincipal("portal-user-guid");

            Assert.True(principal.Identity!.IsAuthenticated);
            Assert.Equal(PortalAuthenticationDefaults.AuthenticationScheme, principal.Identity.AuthenticationType);
            Assert.Equal("portal-user-guid", principal.FindFirstValue(ClaimTypes.NameIdentifier));

            foreach (var forbidden in PortalAuthenticationDefaults.ForbiddenClaimTypes)
                Assert.Null(principal.FindFirst(forbidden));

            // No company and no customer either: both are read from the PortalUser row per request,
            // so a tampered cookie buys a lookup and nothing else.
            Assert.DoesNotContain(principal.Claims, c =>
                c.Type.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                c.Type.Contains("Customer", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_claim_PortalContextAccessor_reads_is_the_one_the_contract_requires()
        {
            // The compatibility claim, made concrete. PortalContextAccessor resolves the whole portal
            // identity from ClaimTypes.NameIdentifier off the current principal - so TAB-3 can keep
            // that accessor unchanged provided the portal principal carries this exact claim.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CrossBuy", "BL", "Portal", "PortalContext.cs"));

            Assert.Contains("class PortalContextAccessor", source);
            Assert.Contains("FindFirstValue(ClaimTypes.NameIdentifier)", source);

            // And it must stay a claim question, not a scheme question: an accessor that demanded a
            // particular AuthenticationType would have to be edited by TAB-3 in 3A-0B.
            Assert.DoesNotContain("AuthenticationType", source);
        }

        // ---- 4. Program.cs, the half DI cannot answer -------------------------------------------

        [Fact]
        public void The_composition_root_adds_the_scheme_without_touching_a_default()
        {
            var program = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            Assert.Contains("AddCrossBuyPortalAuthentication()", program);

            // ONE ARGUMENT WIDE. AddAuthentication("CrossBuy.Portal") anywhere would make the portal
            // scheme the application default, and UseAuthentication would then populate
            // HttpContext.User from the portal cookie on EVERY request, internal ones included.
            Assert.DoesNotContain("AddAuthentication(PortalAuthenticationDefaults", program);
            Assert.DoesNotContain("AddAuthentication(\"CrossBuy.Portal\"", program);

            // THE INTERNAL COOKIE CONFIGURATION IS NOT EDITED BY THIS WAVE, asserted as ABSENCE.
            //
            // This used to assert that ConfigureApplicationCookie was PRESENT, which held on the line
            // this suite came from because that line already called it. This composition root does
            // not, and the portal convergence had no business adding one: that call configures the
            // INTERNAL identity cookie, and a portal wave reaching into it is exactly the coupling
            // AddCrossBuyPortalAuthentication's parameterless AddAuthentication() exists to avoid.
            //
            // So the property is kept and its direction corrected — the portal registration must not
            // have introduced a configuration of somebody else's cookie.
            Assert.DoesNotContain("ConfigureApplicationCookie", program);
        }

        [Fact]
        public void G_Internal_login_still_requires_an_employee_for_the_employee_flow()
        {
            // UNCHANGED BY THIS WAVE, asserted so a later edit here has to be deliberate.
            //
            // THE PORTAL BRANCH IS NOW EXPECTED TO BE ABSENT, and that is this test reaching the end
            // state it was written to anticipate. Its previous wording said the portal branch inside
            // AccountController "is what 3A-0B retires once PortalAccountController exists. Removing
            // it NOW would lock out the portal users who have no other door yet." PortalAccountController
            // exists on this branch, so the interim is over: the internal door is for the internal
            // audience only, and a portal customer signs in at their own.
            //
            // The employee branch itself keeps its meaning and is still asserted, so a wave that
            // quietly broke the staff login would still fail here.
            var account = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountController.cs"));

            Assert.Contains("PasswordSignInAsync", account);
            Assert.Contains("Session.SetString(\"Employee\"", account);

            // The internal login resolves an EMPLOYEE and nothing else — no portal link lookup, and
            // no portal capability vocabulary, has leaked into the staff door.
            Assert.Contains("_employeeService", account);
            Assert.DoesNotContain("PortalUser", account);
            Assert.DoesNotContain("PortalCapabilities", account);

            // And it does not sign anybody into the portal scheme: the internal door issues the
            // internal cookie, full stop.
            Assert.DoesNotContain(PortalAuthenticationDefaults.AuthenticationScheme, account);
        }

        // ---- fixtures ---------------------------------------------------------------------------

        private static ServiceProvider Provider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddCrossBuyPortalAuthentication();
            return services.BuildServiceProvider();
        }

        private static ClaimsPrincipal PortalPrincipal(string userId) =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
                PortalAuthenticationDefaults.AuthenticationScheme));

        /// Invokes the REAL middleware. `employeeSession` null means "no employee session at all",
        /// which is the state every external caller is in.
        private static async Task<(bool nextCalled, HttpContext context)> InvokeAsync(
            string path, string? employeeSession = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = HttpMethods.Get;
            context.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(
                new StubSessionFeature(employeeSession));

            var nextCalled = false;
            var middleware = new SessionValidationMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            await middleware.InvokeAsync(context);

            return (nextCalled, context);
        }

        private sealed class StubSessionFeature : Microsoft.AspNetCore.Http.Features.ISessionFeature
        {
            public StubSessionFeature(string? employee) { Session = new StubSession(employee); }
            public ISession Session { get; set; }
        }

        private sealed class StubSession : ISession
        {
            private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

            public StubSession(string? employee)
            {
                if (employee != null)
                    _values["Employee"] = System.Text.Encoding.UTF8.GetBytes(employee);
            }

            public bool IsAvailable => true;
            public string Id => "stub";
            public IEnumerable<string> Keys => _values.Keys;
            public void Clear() => _values.Clear();
            public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
            public void Remove(string key) => _values.Remove(key);
            public void Set(string key, byte[] value) => _values[key] = value;
            public bool TryGetValue(string key, out byte[]? value) => _values.TryGetValue(key, out value);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
