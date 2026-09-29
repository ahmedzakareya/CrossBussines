using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // WAVE 3A-0B — THE CLIENT PORTAL AUTHENTICATION BOUNDARY. Security matrix A-V.
    //
    // WHAT MAKES THESE REAL, because a boundary test that stubs the boundary proves nothing:
    //
    //   * THE COOKIE IS REAL. Sign-in goes through the genuine CookieAuthenticationHandler
    //     registered by the production AddCrossBuyPortalAuthentication(), including real data
    //     protection. Tests that need a request to "hold a cookie" take the Set-Cookie header the
    //     handler actually emitted and send it back on a second request, so the value is decrypted
    //     by the same handler that encrypted it. Nothing is faked into ClaimsPrincipal by hand.
    //
    //   * THE AUTHORIZATION DECISION IS REAL. The policy is built by AuthorizationPolicy.CombineAsync
    //     from the attributes REFLECTED OFF THE ACTUAL CONTROLLER, and evaluated by the framework's
    //     own IPolicyEvaluator - the same two calls AuthorizationMiddleware makes. If somebody
    //     changes the attribute on ClientPortalController, these tests change their answer.
    //
    //   * THE CREDENTIAL CHECK IS REAL. A genuine UserManager<Users> over the EF Identity store
    //     hashes and verifies the password. A "wrong password" test that stubbed the verifier would
    //     be asserting its own stub.
    //
    // WHAT IS DELIBERATELY NOT EXERCISED, named rather than quietly skipped - the discipline
    // tools/authz-probe/README.md already states for this repository:
    //
    //   * No browser and no Kestrel. These run the pipeline in-process, so they prove the server's
    //     decision but not the browser's cookie jar behaviour (SameSite, Secure, domain scoping).
    //     Those are configuration on the handler, asserted here by reading the options rather than
    //     by observing a browser.
    //   * No route table, so Url.Action returns null and the redirect TARGET is not asserted -
    //     only that a refusal carries no redirect and a success carries the portal cookie.
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // An ISession that RECORDS instead of storing.
    //
    // Asserting "Session["Employee"] is absent afterwards" would pass if the controller wrote the key
    // and then removed it - and a login that briefly mints an employee session has still minted one.
    // This records every call, so the assertion is "the controller never touched the session at all".
    // ---------------------------------------------------------------------------------------------
    internal sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        public readonly List<string> Writes = new();
        public readonly List<string> Removes = new();
        public bool Cleared;

        public bool IsAvailable => true;
        public string Id => "test-session";
        public IEnumerable<string> Keys => _store.Keys;

        public void Clear() { Cleared = true; _store.Clear(); }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { Removes.Add(key); _store.Remove(key); }
        public void Set(string key, byte[] value) { Writes.Add(key); _store[key] = value; }
        public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
    }

    // ---------------------------------------------------------------------------------------------
    // A UrlHelper with no route table.
    //
    // ONLY Action() is overridden, and that matters: Url.Action needs an IRouter these tests do not
    // host, but Url.IsLocalUrl — the half that decides whether a returnUrl is safe — is inherited
    // from the framework's own UrlHelperBase and runs UNMODIFIED. Stubbing IsLocalUrl would have made
    // test T an assertion about this class instead of about the product.
    // ---------------------------------------------------------------------------------------------
    /// TempData that goes nowhere. The GET Login action returns View(), and Controller.View()
    /// touches TempData, which would otherwise resolve ITempDataDictionaryFactory out of DI. No test
    /// here asserts anything about TempData, so a no-op provider is the honest substitute.
    internal sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    internal sealed class RoutelessUrlHelper : UrlHelper
    {
        public RoutelessUrlHelper(ActionContext context) : base(context) { }

        public override string? Action(UrlActionContext actionContext)
            => "/" + actionContext.Controller + "/" + actionContext.Action;
    }

    // ---------------------------------------------------------------------------------------------
    // Role and org sources that THROW if consulted.
    //
    // Test R asserts that the module gate refuses a portal identity. A stub returning "no roles" would
    // prove the refusal happened SOMEWHERE; these prove it happened at gate 7, BEFORE any role source
    // was asked - which is the property that makes the gate a boundary rather than a policy outcome.
    // ---------------------------------------------------------------------------------------------
    internal sealed class ThrowingRoleDirectory : IPlatformRoleDirectory
    {
        public Task<IReadOnlyList<RoleGrant>> RolesAsync(
            BusinessContext context, string scope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "gate 7 must refuse a portal identity BEFORE any role lookup; reaching this means it did not.");

        public Task<bool> AnyConfiguredAsync(int companyId, string scope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "gate 7 must refuse a portal identity BEFORE any role lookup; reaching this means it did not.");
    }

    internal sealed class ThrowingOrgHierarchy : IOrgHierarchy
    {
        public Task<IReadOnlySet<int>> DirectAndIndirectReportsAsync(
            int managerEmployeeId, int companyId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not reachable in these tests");

        public Task<int?> DirectManagerAsync(
            int employeeId, int companyId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not reachable in these tests");
    }

    // ---------------------------------------------------------------------------------------------
    // The host: production authentication registration over a real SQLite database.
    // ---------------------------------------------------------------------------------------------
    internal sealed class PortalAuthHost : IDisposable
    {
        public const int CompanyA = 1, CompanyB = 2;
        public const int CustomerAcme = 100, CustomerBeta = 200;

        private readonly SqliteConnection _connection;
        public readonly ServiceProvider Services;
        public readonly CrossDbContext Db;
        public readonly UserManager<Users> Users;

        public PortalAuthHost()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddDataProtection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

            services.AddDbContext<CrossDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton<ICompanyScopeHolder, CompanyScopeHolder>();

            // Real Identity, real password hashing. AddIdentityCore rather than AddIdentity because
            // AddIdentity would register its own cookie schemes and set the defaults - and which
            // scheme is DEFAULT is precisely what several of these tests are about.
            services.AddIdentityCore<Users>(o =>
            {
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredLength = 6;
                o.User.RequireUniqueEmail = false;
            }).AddEntityFrameworkStores<CrossDbContext>();

            // THE INTERNAL SCHEME, registered first and AS THE DEFAULT, exactly as production has it.
            // Without this the "employee cookie" in tests F and G would not exist, and the test that
            // matters most - that bare [Authorize] is satisfied by the internal cookie and the portal
            // policy is not - could not be written at all.
            services.AddAuthentication(IdentityConstants.ApplicationScheme)
                .AddCookie(IdentityConstants.ApplicationScheme, o =>
                {
                    o.Cookie.Name = ".AspNetCore.Identity.Application";
                });

            // The production registration, unmodified.
            services.AddCrossBuyPortalAuthentication();
            services.AddAuthorizationPolicyEvaluator();

            Services = services.BuildServiceProvider();

            Db = Services.GetRequiredService<CrossDbContext>();
            Db.Database.EnsureCreated();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = OFF;";
                command.ExecuteNonQuery();
            }

            Users = Services.GetRequiredService<UserManager<Users>>();
        }

        // ---- seeding -----------------------------------------------------------------------------

        public async Task<Users> AddUserAsync(string userName, string password,
            bool isActive = true, bool isEndUser = true)
        {
            var user = new Users { UserName = userName, Email = userName + "@example.com", IsActive = isActive, IsEndUser = isEndUser };
            var result = await Users.CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
            return user;
        }

        public PortalUser LinkPortal(Users user, int companyId = CompanyA, int customerId = CustomerAcme,
            bool isActive = true, string? capabilities = null)
        {
            var link = new PortalUser
            {
                CompanyID = companyId,
                CustomerId = customerId,
                UserId = user.Id,
                ContactName = "Contact " + user.UserName,
                Email = user.Email,
                IsActive = isActive,
                Capabilities = capabilities ?? string.Join(",", PortalCapabilities.Default),
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<PortalUser>().Add(link);
            Db.SaveChanges();
            return link;
        }

        // ---- pipeline ----------------------------------------------------------------------------

        private readonly List<IServiceScope> _scopes = new();

        /// ONE DI SCOPE PER CONTEXT, exactly as a real request gets.
        ///
        /// This is not tidiness. IAuthenticationHandlerProvider is SCOPED and caches the handler it
        /// built for a scheme, together with the HttpContext that handler was initialised against.
        /// Sharing the root provider across contexts therefore hands every later request a handler
        /// still bound to the FIRST one — so a replayed cookie is read from the wrong context and
        /// authentication silently returns None. Which is a false PASS waiting to happen: a test
        /// asserting "this cookie does not authorize" would succeed for the wrong reason.
        public DefaultHttpContext NewContext(string? cookieHeader = null)
        {
            var scope = Services.CreateScope();
            _scopes.Add(scope);

            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString("localhost");
            ctx.Request.Path = "/";
            ctx.Session = new RecordingSession();
            if (cookieHeader != null) ctx.Request.Headers["Cookie"] = cookieHeader;
            return ctx;
        }

        /// Authenticates a scheme USING THE CONTEXT'S OWN SCOPE, for the same reason.
        public static Task<AuthenticateResult> AuthenticateAsync(HttpContext ctx, string scheme)
            => ctx.RequestServices.GetRequiredService<IAuthenticationService>().AuthenticateAsync(ctx, scheme);

        /// WHAT app.UseAuthentication() DOES, and why omitting it made a test lie.
        ///
        /// PolicyEvaluator.AuthenticateAsync assigns context.User ITSELF only when the policy NAMES a
        /// scheme — the portal policy does, so it worked. The DEFAULT policy names none, and for that
        /// case the evaluator relies on AuthenticationMiddleware having already put the principal on
        /// the context. Without this step context.User was always empty, so "the employee cookie does
        /// not authorize ClientPortal" passed for a reason that had nothing to do with the attribute
        /// under test: mutation M1 replaced [PortalAuthorize] with [Authorize] and the test still
        /// passed. This runs the middleware's one line, so the negative means what it says.
        public static async Task ApplyAuthenticationMiddlewareAsync(HttpContext ctx)
        {
            var result = await ctx.RequestServices.GetRequiredService<IAuthenticationService>()
                .AuthenticateAsync(ctx, scheme: null);   // null ⇒ the DEFAULT scheme, as the middleware does
            if (result.Succeeded && result.Principal != null) ctx.User = result.Principal;
        }

        public PortalAccountController Controller(HttpContext ctx)
        {
            var controller = new PortalAccountController(Users, Db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = ctx,
                    RouteData = new RouteData(),
                    ActionDescriptor = new ControllerActionDescriptor(),
                },
            };
            controller.ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
            controller.Url = new RoutelessUrlHelper(new ActionContext(ctx, new RouteData(), new ActionDescriptor()));
            controller.TempData = new TempDataDictionary(ctx, new NullTempDataProvider());
            return controller;
        }

        /// Every Set-Cookie the response actually emitted, as "name=value" pairs ready to send back.
        public static IReadOnlyList<string> SetCookies(HttpContext ctx)
            => ctx.Response.Headers["Set-Cookie"]
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!.Split(';')[0])
                .ToList();

        public static string? CookieNamed(HttpContext ctx, string name)
            => SetCookies(ctx).FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));

        /// True when the response DELETED the named cookie — the handler signals sign-out by emitting
        /// the cookie with an empty value and an expiry in the past, not by omitting it.
        public static bool CookieDeleted(HttpContext ctx, string name)
            => ctx.Response.Headers["Set-Cookie"]
                .Any(v => v != null
                          && v.StartsWith(name + "=;", StringComparison.Ordinal)
                          && v.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        // ---- the real authorization decision ------------------------------------------------------

        /// Builds the policy from the attributes ON THE REAL CONTROLLER and evaluates it with the
        /// framework's own evaluator — the same two calls AuthorizationMiddleware makes.
        public async Task<PolicyAuthorizationResult> AuthorizeControllerAsync(Type controller, HttpContext ctx)
        {
            var provider = ctx.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
            var authorizeData = controller.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            Assert.True(authorizeData.Count > 0, controller.Name + " carries no authorization attribute at all.");

            var policy = await AuthorizationPolicy.CombineAsync(provider, authorizeData);
            Assert.NotNull(policy);

            await ApplyAuthenticationMiddlewareAsync(ctx);

            var evaluator = ctx.RequestServices.GetRequiredService<IPolicyEvaluator>();
            var authenticate = await evaluator.AuthenticateAsync(policy!, ctx);
            return await evaluator.AuthorizeAsync(policy!, authenticate, ctx, resource: null);
        }

        /// A signed-in portal cookie, obtained through the REAL handler.
        public async Task<string> PortalCookieAsync(Users user)
        {
            var ctx = NewContext();
            var identity = new ClaimsIdentity(
                new[] { new Claim(PortalAuthenticationDefaults.RequiredClaim, user.Id) },
                PortalAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(PortalAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            var cookie = CookieNamed(ctx, PortalAuthenticationDefaults.CookieName);
            Assert.NotNull(cookie);
            return cookie!;
        }

        /// A signed-in INTERNAL employee cookie, through the real Identity.Application handler.
        public async Task<string> EmployeeCookieAsync(Users user)
        {
            var ctx = NewContext();
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName!) },
                IdentityConstants.ApplicationScheme);
            await ctx.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity));
            var cookie = CookieNamed(ctx, ".AspNetCore.Identity.Application");
            Assert.NotNull(cookie);
            return cookie!;
        }

        public void Dispose()
        {
            foreach (var scope in _scopes) scope.Dispose();
            Services.Dispose();
            _connection.Dispose();
        }
    }

    // =============================================================================================
    public class PortalAuthenticationSecurityTests
    {
        private const string Password = "portal-pass-1";

        // -----------------------------------------------------------------------------------------
        // A — a portal-only user, with NO Employee row anywhere, can sign in.
        //
        // This is the whole point of the wave. Before it, the only way to reach the portal was to be
        // a member of staff, because the internal login was the only door.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task A_portal_only_user_with_no_employee_row_can_login()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            Assert.Empty(host.Db.Employee.Where(e => e.UserId == user.Id));

            var ctx = host.NewContext();
            var result = await host.Controller(ctx).Login("acme.contact", Password);

            Assert.True(Succeeded(result));
            Assert.NotNull(PortalAuthHost.CookieNamed(ctx, PortalAuthenticationDefaults.CookieName));
        }

        // -----------------------------------------------------------------------------------------
        // B — login succeeds with NO employee session present.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task B_portal_login_succeeds_without_employee_session()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            var session = (RecordingSession)ctx.Session;
            Assert.False(session.TryGetValue("Employee", out _));

            Assert.True(Succeeded(await host.Controller(ctx).Login("acme.contact", Password)));
        }

        // -----------------------------------------------------------------------------------------
        // C — login creates NO employee session. Asserted as "never touched", not "absent after".
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task C_portal_login_creates_no_employee_session()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            var session = (RecordingSession)ctx.Session;
            Assert.True(Succeeded(await host.Controller(ctx).Login("acme.contact", Password)));

            Assert.Empty(session.Writes);
            Assert.Empty(session.Removes);
            Assert.False(session.Cleared);
        }

        // -----------------------------------------------------------------------------------------
        // D — a successful login issues the CrossBuy.Portal cookie.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task D_successful_login_issues_the_portal_cookie()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            Assert.True(Succeeded(await host.Controller(ctx).Login("acme.contact", Password)));

            var cookie = PortalAuthHost.CookieNamed(ctx, PortalAuthenticationDefaults.CookieName);
            Assert.NotNull(cookie);
            Assert.StartsWith("CrossBuy.Portal=", cookie);
        }

        // -----------------------------------------------------------------------------------------
        // E — and issues NO internal Identity cookie. The single regression this wave most fears:
        // one PasswordSignInAsync would reintroduce it invisibly.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task E_successful_login_issues_no_internal_identity_cookie()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            Assert.True(Succeeded(await host.Controller(ctx).Login("acme.contact", Password)));

            Assert.Null(PortalAuthHost.CookieNamed(ctx, ".AspNetCore.Identity.Application"));

            // Exactly ONE cookie was issued, so nothing was minted that this assertion did not name.
            Assert.Single(PortalAuthHost.SetCookies(ctx));
        }

        // -----------------------------------------------------------------------------------------
        // F — an employee cookie alone does NOT authorize ClientPortal.
        //
        // This is the defect 3A-0B exists to close, and it is asserted against the REAL attribute on
        // the REAL controller: bare [Authorize] would pass this request, [PortalAuthorize] refuses it.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task F_employee_cookie_alone_cannot_authorize_client_portal()
        {
            using var host = new PortalAuthHost();
            var staff = await host.AddUserAsync("staff.member", Password);

            var ctx = host.NewContext(await host.EmployeeCookieAsync(staff));
            var result = await host.AuthorizeControllerAsync(typeof(ClientPortalController), ctx);

            Assert.False(result.Succeeded);
        }

        // -----------------------------------------------------------------------------------------
        // G — a portal cookie alone does NOT authorize a representative internal route.
        //
        // The mirror of F. CommentsController is the representative: it is an ordinary internal
        // controller behind the default scheme.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task G_portal_cookie_alone_cannot_authorize_internal_routes()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext(await host.PortalCookieAsync(user));

            // The internal default policy: authentication through the DEFAULT scheme, which is
            // Identity.Application. The portal cookie is not read by it at all.
            var provider = ctx.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await provider.GetDefaultPolicyAsync();
            await PortalAuthHost.ApplyAuthenticationMiddlewareAsync(ctx);
            var evaluator = ctx.RequestServices.GetRequiredService<IPolicyEvaluator>();
            var authenticate = await evaluator.AuthenticateAsync(policy, ctx);
            var result = await evaluator.AuthorizeAsync(policy, authenticate, ctx, resource: null);

            Assert.False(result.Succeeded);
        }

        // -----------------------------------------------------------------------------------------
        // H — correct credentials, but no PortalUsers association: denied.
        //
        // Eligibility is a SECOND requirement. An internal user's password is correct; that must not
        // be a portal login.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task H_identity_user_without_portal_link_is_denied()
        {
            using var host = new PortalAuthHost();
            await host.AddUserAsync("staff.member", Password);   // no LinkPortal

            var ctx = host.NewContext();
            var result = await host.Controller(ctx).Login("staff.member", Password);

            Assert.False(Succeeded(result));
            Assert.Empty(PortalAuthHost.SetCookies(ctx));
        }

        // -----------------------------------------------------------------------------------------
        // I — a deactivated portal link is denied even with correct credentials.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task I_disabled_portal_user_is_denied()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user, isActive: false);

            var ctx = host.NewContext();
            Assert.False(Succeeded(await host.Controller(ctx).Login("acme.contact", Password)));
            Assert.Empty(PortalAuthHost.SetCookies(ctx));
        }

        // -----------------------------------------------------------------------------------------
        // J — wrong password denied. Verified by the real hasher.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task J_bad_password_is_denied()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            Assert.False(Succeeded(await host.Controller(ctx).Login("acme.contact", "not-the-password")));
            Assert.Empty(PortalAuthHost.SetCookies(ctx));
        }

        // -----------------------------------------------------------------------------------------
        // K — an unknown user is denied.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task K_unknown_user_is_denied()
        {
            using var host = new PortalAuthHost();

            var ctx = host.NewContext();
            Assert.False(Succeeded(await host.Controller(ctx).Login("nobody.at.all", Password)));
            Assert.Empty(PortalAuthHost.SetCookies(ctx));
        }

        // -----------------------------------------------------------------------------------------
        // L — the four refusals are INDISTINGUISHABLE.
        //
        // If they differed, the login screen would be an oracle for "is this address a customer of
        // this business" — which is a disclosure even when no password is ever guessed.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task L_failures_do_not_enumerate_their_cause()
        {
            using var host = new PortalAuthHost();
            var linked = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(linked);
            await host.AddUserAsync("staff.member", Password);            // no link
            var disabled = await host.AddUserAsync("gone.contact", Password);
            host.LinkPortal(disabled, isActive: false);

            var messages = new List<string?>
            {
                await MessageOf(host, "nobody.at.all", Password),   // unknown user
                await MessageOf(host, "acme.contact", "wrong"),     // bad password
                await MessageOf(host, "staff.member", Password),    // no portal link
                await MessageOf(host, "gone.contact", Password),    // disabled link
            };

            Assert.Single(messages.Distinct());
            Assert.False(string.IsNullOrWhiteSpace(messages[0]));
        }

        // -----------------------------------------------------------------------------------------
        // M / N — a forged company or customer on the request cannot alter the resolved authority.
        //
        // Asserted structurally, which is stronger than asserting a value: the cookie CARRIES only
        // the user id, so there is no company or customer in it to forge. PortalAuthenticationDefaults
        // names the claim types that must never appear, and this proves none of them does.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task M_forged_company_cannot_alter_context()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user, companyId: PortalAuthHost.CompanyA);

            var principal = await SignedInPrincipal(host, user);

            Assert.Equal(1, principal.Claims.Count());
            Assert.Equal(PortalAuthenticationDefaults.RequiredClaim, principal.Claims.Single().Type);
            foreach (var forbidden in PortalAuthenticationDefaults.ForbiddenClaimTypes)
                Assert.DoesNotContain(principal.Claims, c => string.Equals(c.Type, forbidden, StringComparison.Ordinal));
        }

        [Fact]
        public async Task N_forged_customer_cannot_alter_context()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            var link = host.LinkPortal(user, customerId: PortalAuthHost.CustomerAcme);

            var principal = await SignedInPrincipal(host, user);

            // The only thing the request can carry is the user id; the customer is read from the row.
            Assert.Equal(user.Id, principal.FindFirstValue(PortalAuthenticationDefaults.RequiredClaim));
            Assert.DoesNotContain(principal.Claims, c => c.Value == link.CustomerId.ToString());
            Assert.DoesNotContain(principal.Claims, c => c.Value == link.Id.ToString());
        }

        // -----------------------------------------------------------------------------------------
        // Q — an ExternalPortal business context has a NULL EmployeeId, and is not a System context.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Q_external_portal_context_has_null_employee_id()
        {
            var context = BusinessContext.ForExternalPortal(
                companyId: PortalAuthHost.CompanyA, userId: "user-1", portalUserId: 5);

            Assert.Null(context.EmployeeId);
            Assert.True(context.IsExternalPortal);
            Assert.False(context.IsSystem);
            Assert.Equal(BusinessContextSource.ExternalPortal, context.Source);
            Assert.Equal(5, context.PortalUserId);
        }

        // -----------------------------------------------------------------------------------------
        // R — a portal identity holds NO module authority, refused at gate 7 before any role lookup.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task R_portal_has_no_tasks_authority()
        {
            using var host = new PortalAuthHost();

            var tasks = new CrossBuy.BL.TasksAccessService(
                host.Db,
                new ThrowingRoleDirectory(),
                new ThrowingOrgHierarchy(),
                () => throw new InvalidOperationException("permissions must not be consulted"),
                NullLogger<CrossBuy.BL.TasksAccessService>.Instance);

            var portal = BusinessContext.ForExternalPortal(
                companyId: PortalAuthHost.CompanyA, userId: "user-1", portalUserId: 5);

            foreach (var action in new[] { "read", "write", "manage" })
                Assert.False(await tasks.CanAsync(portal, action));
        }

        // -----------------------------------------------------------------------------------------
        // S — anonymous access to ClientPortal is refused.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task S_anonymous_client_portal_is_denied()
        {
            using var host = new PortalAuthHost();

            var result = await host.AuthorizeControllerAsync(typeof(ClientPortalController), host.NewContext());

            Assert.False(result.Succeeded);
            Assert.True(result.Challenged);   // no identity at all ⇒ challenge, not forbid
        }

        // -----------------------------------------------------------------------------------------
        // T — an unsafe returnUrl is dropped.
        //
        // Both halves matter: an absolute URL to another origin, AND a LOCAL url pointing at an
        // internal employee surface. The second is the one Url.IsLocalUrl alone would wave through.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [InlineData("https://evil.example.com/steal")]       // another origin
        [InlineData("//evil.example.com/steal")]             // protocol-relative
        [InlineData("/Tasks")]                               // local, but an employee surface
        [InlineData("/Accounting/SalesInvoiceDetail?id=1")]
        [InlineData("/Crm/Leads")]
        [InlineData("/Admin/EmployeesList")]
        public async Task T_unsafe_return_url_is_refused(string returnUrl)
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            var controller = host.Controller(ctx);

            controller.Login(returnUrl);
            Assert.Null(controller.ViewData["ReturnUrl"]);

            var result = await controller.Login("acme.contact", Password, returnUrl: returnUrl);
            Assert.True(Succeeded(result));
            Assert.NotEqual(returnUrl, RedirectOf(result));
        }

        [Theory]
        [InlineData("/ClientPortal")]
        [InlineData("/ClientPortal/Quotations")]
        [InlineData("/PortalAccount/Login")]
        public async Task T_portal_safe_return_url_is_honoured(string returnUrl)
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext();
            var result = await host.Controller(ctx).Login("acme.contact", Password, returnUrl: returnUrl);

            Assert.True(Succeeded(result));
            Assert.Equal(returnUrl, RedirectOf(result));
        }

        // -----------------------------------------------------------------------------------------
        // U — logout invalidates the portal authentication.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task U_portal_logout_invalidates_portal_auth()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var portalCookie = await host.PortalCookieAsync(user);

            // It authorizes before logout — otherwise the assertion after it proves nothing.
            Assert.True((await host.AuthorizeControllerAsync(
                typeof(ClientPortalController), host.NewContext(portalCookie))).Succeeded);

            var ctx = host.NewContext(portalCookie);
            await host.Controller(ctx).Logout();

            Assert.True(PortalAuthHost.CookieDeleted(ctx, PortalAuthenticationDefaults.CookieName));
        }

        // -----------------------------------------------------------------------------------------
        // V — and preserves a simultaneous employee identity.
        //
        // The shared-machine case: a customer signing out of the portal must not sign a colleague out
        // of the ERP. A bare SignOutAsync() or a Session.Clear() would fail this.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task V_portal_logout_preserves_simultaneous_employee_auth()
        {
            using var host = new PortalAuthHost();
            var customer = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(customer);
            var staff = await host.AddUserAsync("staff.member", Password);

            var both = await host.PortalCookieAsync(customer) + "; " + await host.EmployeeCookieAsync(staff);

            var ctx = host.NewContext(both);
            var session = (RecordingSession)ctx.Session;
            await host.Controller(ctx).Logout();

            // The portal cookie is deleted; the internal one is not mentioned at all.
            Assert.True(PortalAuthHost.CookieDeleted(ctx, PortalAuthenticationDefaults.CookieName));
            Assert.False(PortalAuthHost.CookieDeleted(ctx, ".AspNetCore.Identity.Application"));
            Assert.DoesNotContain(PortalAuthHost.SetCookies(ctx),
                c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));

            // And the employee session was not touched.
            Assert.Empty(session.Writes);
            Assert.Empty(session.Removes);
            Assert.False(session.Cleared);

            // The internal identity still authenticates afterwards.
            var after = await PortalAuthHost.AuthenticateAsync(
                host.NewContext(both), IdentityConstants.ApplicationScheme);
            Assert.True(after.Succeeded);
        }

        // -----------------------------------------------------------------------------------------
        // NON-VACUITY CONTROL for F and G.
        //
        // F and G both assert a REFUSAL. A harness that refused everything would pass both while
        // proving nothing — and it did exactly that until mutation M1 exposed it. These two say the
        // harness can also return YES, so the refusals above are decisions rather than silence.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Control_employee_cookie_does_satisfy_the_internal_default_policy()
        {
            using var host = new PortalAuthHost();
            var staff = await host.AddUserAsync("staff.member", Password);

            var ctx = host.NewContext(await host.EmployeeCookieAsync(staff));
            var provider = ctx.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await provider.GetDefaultPolicyAsync();
            await PortalAuthHost.ApplyAuthenticationMiddlewareAsync(ctx);
            var evaluator = ctx.RequestServices.GetRequiredService<IPolicyEvaluator>();
            var authenticate = await evaluator.AuthenticateAsync(policy, ctx);
            var result = await evaluator.AuthorizeAsync(policy, authenticate, ctx, resource: null);

            Assert.True(result.Succeeded,
                "The internal default policy must ACCEPT an employee cookie. If it does not, test G " +
                "is vacuous: it would refuse the portal cookie in a harness that refuses everyone.");
        }

        [Fact]
        public async Task Control_portal_cookie_does_satisfy_the_client_portal_policy()
        {
            using var host = new PortalAuthHost();
            var user = await host.AddUserAsync("acme.contact", Password);
            host.LinkPortal(user);

            var ctx = host.NewContext(await host.PortalCookieAsync(user));
            var result = await host.AuthorizeControllerAsync(typeof(ClientPortalController), ctx);

            Assert.True(result.Succeeded,
                "A valid portal cookie must authorize ClientPortal. If it does not, test F is vacuous.");
        }

        // =========================================================================================
        // THE DECLARATIONS THEMSELVES. These assert the security contract TAB-1 landed is the one
        // actually applied — an attribute nobody checks is a comment.
        // =========================================================================================

        [Fact]
        public void Login_post_is_declared_anonymous_by_design_with_a_real_reason()
        {
            var post = PostAction(nameof(PortalAccountController.Login));

            var declaration = post.GetCustomAttribute<AnonymousByDesignAttribute>();
            Assert.NotNull(declaration);
            Assert.False(string.IsNullOrWhiteSpace(declaration!.Reason));
            Assert.True(declaration.Reason.Length > 80, "A one-word reason is an exemption wearing a better name.");

            Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(post.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        }

        [Fact]
        public void Logout_is_portal_authorized_and_anti_forgery_protected()
        {
            var logout = PostAction(nameof(PortalAccountController.Logout));

            Assert.NotNull(logout.GetCustomAttribute<PortalAuthorizeAttribute>());
            Assert.NotNull(logout.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.Null(logout.GetCustomAttribute<AnonymousByDesignAttribute>());
        }

        // The defect that would silently disarm Logout: [AllowAnonymous] on the CONTROLLER goes into
        // endpoint metadata and makes AuthorizationMiddleware skip the action's own [PortalAuthorize].
        [Fact]
        public void Controller_carries_no_class_level_allow_anonymous()
        {
            Assert.Null(typeof(PortalAccountController)
                .GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>(inherit: true));
        }

        [Fact]
        public void Client_portal_is_bound_to_the_portal_policy_and_not_bare_authorize()
        {
            var attributes = typeof(ClientPortalController)
                .GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>().ToList();

            Assert.Contains(attributes, a => a is PortalAuthorizeAttribute);
            Assert.All(attributes, a => Assert.Equal(PortalAuthenticationDefaults.PolicyName, a.Policy));
        }

        // =========================================================================================
        // helpers
        // =========================================================================================

        private static MethodInfo PostAction(string name)
            => typeof(PortalAccountController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == name && m.GetCustomAttribute<HttpPostAttribute>() != null);

        private static object? Value(IActionResult result, string property)
        {
            var json = Assert.IsType<JsonResult>(result);
            return json.Value?.GetType().GetProperty(property)?.GetValue(json.Value);
        }

        private static bool Succeeded(IActionResult result) => Value(result, "success") as bool? == true;
        private static string? RedirectOf(IActionResult result) => Value(result, "redirectUrl") as string;
        private static string? MessageOfResult(IActionResult result) => Value(result, "message") as string;

        private static async Task<string?> MessageOf(PortalAuthHost host, string user, string password)
        {
            var ctx = host.NewContext();
            var result = await host.Controller(ctx).Login(user, password);
            Assert.False(Succeeded(result));
            return MessageOfResult(result);
        }

        /// Signs in through the real handler, sends the cookie back, and returns the principal the
        /// handler RECONSTRUCTED from it — so the assertion is about what the cookie carries on the
        /// wire, not about the object that went in.
        private static async Task<ClaimsPrincipal> SignedInPrincipal(PortalAuthHost host, Users user)
        {
            var ctx = host.NewContext(await host.PortalCookieAsync(user));
            var result = await PortalAuthHost.AuthenticateAsync(ctx, PortalAuthenticationDefaults.AuthenticationScheme);
            Assert.True(result.Succeeded);
            return result.Principal!;
        }
    }
}
