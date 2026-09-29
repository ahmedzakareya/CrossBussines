using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // CLIENT PORTAL AUTHENTICATION — registration.
    //
    // ONE extension method, because Program.cs is shared (SHF-01) and a security block written into
    // a composition root is a security decision nobody can test. Everything here is additive: no
    // existing scheme, cookie, default or middleware ordering is touched.
    //
    // THE SINGLE MOST IMPORTANT LINE IN THIS FILE IS THE PARAMETERLESS AddAuthentication().
    //
    //   AddAuthentication()            adds the scheme and LEAVES every default alone.
    //   AddAuthentication("CrossBuy.Portal")  would make the portal scheme the DEFAULT — and a
    //                                  default scheme is what UseAuthentication() runs on every
    //                                  request, which would populate HttpContext.User from the
    //                                  portal cookie for INTERNAL endpoints too. Ordinary
    //                                  [Authorize] would then be satisfied by a customer.
    //
    // That is the escalation this wave exists to prevent, it is one argument wide, and
    // PortalAuthenticationIsolationTests mutates exactly that line to prove the guard is real.
    //
    // WHAT MAKES THE BOUNDARY HOLD, stated as the two independent facts it rests on:
    //
    //   1. DIFFERENT COOKIE NAME. The internal handler looks for
    //      ".AspNetCore.Identity.Application"; this one issues "CrossBuy.Portal". Neither handler
    //      sees the other's cookie, and they do not share a data-protection purpose either, because
    //      the purpose string is derived from the scheme name.
    //
    //   2. NOT THE DEFAULT. Internal [Authorize] resolves through the default authenticate scheme.
    //      A portal cookie therefore never becomes the principal that internal authorization reads,
    //      whatever else is true of the request.
    //
    // Either one alone would be an argument; together they are a boundary.
    // =============================================================================================
    public static class PortalAuthenticationRegistration
    {
        public static IServiceCollection AddCrossBuyPortalAuthentication(this IServiceCollection services)
        {
            // Parameterless — see the note above. This adds a scheme; it changes no default.
            services.AddAuthentication()
                .AddCookie(PortalAuthenticationDefaults.AuthenticationScheme, options =>
                {
                    options.Cookie.Name = PortalAuthenticationDefaults.CookieName;

                    // An authentication cookie is not optional decoration, so consent cannot suppress
                    // it, and script must not be able to read it.
                    options.Cookie.IsEssential = true;
                    options.Cookie.HttpOnly = true;

                    // SameAsRequest, matching what the internal identity cookie already gets by
                    // default. NOT CookieSecurePolicy.None: the session cookie uses None for a
                    // documented dev convenience - one login surviving both the Kestrel HTTP and the
                    // IIS Express HTTPS instance - and copying that onto a brand-new external-facing
                    // credential would be inheriting a workaround as if it were a policy.
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                    // Lax and not Strict: the portal is reached from links in mail a customer was
                    // sent, and Strict drops the cookie on that first cross-site navigation, which
                    // presents as a login loop rather than as a security setting.
                    options.Cookie.SameSite = SameSiteMode.Lax;

                    // The whole site, because the cookie has to be readable on both /PortalAccount
                    // (the door) and /ClientPortal (the room).
                    options.Cookie.Path = "/";

                    options.LoginPath = PortalAuthenticationDefaults.LoginPath;
                    options.LogoutPath = PortalAuthenticationDefaults.LogoutPath;
                    options.AccessDeniedPath = PortalAuthenticationDefaults.AccessDeniedPath;

                    options.ExpireTimeSpan = PortalAuthenticationDefaults.ExpireTimeSpan;
                    options.SlidingExpiration = true;
                });

            services.AddAuthorization(options =>
            {
                // THE POLICY NAMES ITS SCHEME, which is the half that makes the binding explicit at
                // the controller. Without AddAuthenticationSchemes the policy would fall back to the
                // DEFAULT authenticate scheme - so a plain [Authorize(Policy = ...)] on the portal
                // would quietly be satisfied by an INTERNAL identity cookie, which is the mirror
                // image of the defect this wave closes and just as wrong.
                options.AddPolicy(PortalAuthenticationDefaults.PolicyName, policy =>
                {
                    policy.AuthenticationSchemes.Clear();
                    policy.AddAuthenticationSchemes(PortalAuthenticationDefaults.AuthenticationScheme);
                    policy.RequireAuthenticatedUser();
                });
            });

            return services;
        }
    }
}
