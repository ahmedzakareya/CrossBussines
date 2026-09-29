using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Portal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // THE CLIENT PORTAL AUTHORITY, AND THE TEST THAT MAKES DECLARING IT SAFE.
    //
    // AuthorizationSurface.AuthorityTypes now credits IPortalDataService, so CBA001 treats an endpoint
    // that reaches it as authorized. Crediting a SERVICE carries one hazard and it is worth naming: a
    // member added later that forgot to authorize would be credited too - the CORRECTION-004 shape.
    //
    // This file is the mitigation, and the declaration is not safe without it. Every member of the
    // interface is driven REFLECTIVELY, so a new member cannot arrive unnoticed: the count is asserted,
    // and each member is invoked against an accessor that answers "no identity" and must come back
    // empty rather than with rows.
    //
    // WHAT IS DELIBERATELY NOT ASSERTED HERE: which capability each member demands. That is portal
    // product policy and belongs to its owner. What this file pins is the property the ANALYZER relies
    // on - that no member reads a row without first resolving the external identity.
    // ============================================================================================
    public class PortalAuthoritySurfaceTests
    {
        // ---- 1. the identity comes only from the authenticated principal ----------------------------

        [Fact]
        public async Task An_unauthenticated_request_resolves_no_portal_identity()
        {
            using var host = new PlatformTestHost();

            // No principal at all: the accessor must not invent one.
            var accessor = new PortalContextAccessor(host.Db, Http(user: null));

            Assert.Null(await accessor.TryGetCurrentAsync());
        }

        [Fact]
        public async Task A_customer_id_supplied_by_the_request_is_not_an_identity()
        {
            using var host = new PlatformTestHost();

            // THE POINT OF THE WHOLE SEAM. A principal with no NameIdentifier claim, and a request
            // carrying a customerId and a companyId in the query string and the route. If any of those
            // could produce a context, the portal would authorize whoever asked loudest.
            var ctx = new DefaultHttpContext();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("customerId", "5"),
                new Claim("companyId", "1"),
            }, "portal"));
            ctx.Request.QueryString = new QueryString("?customerId=5&companyId=1");
            ctx.Request.RouteValues["customerId"] = "5";

            var accessor = new PortalContextAccessor(host.Db, new HttpContextAccessor { HttpContext = ctx });

            Assert.Null(await accessor.TryGetCurrentAsync());
        }

        [Fact]
        public async Task An_inactive_portal_user_resolves_to_nothing()
        {
            using var host = new PlatformTestHost();
            Seed(host, userId: "u-inactive", companyId: 1, customerId: 5, active: false);

            var accessor = new PortalContextAccessor(host.Db, Http(Principal("u-inactive")));

            // The row exists and names a real customer. IsActive is the whole difference, and a
            // deactivated portal login that still resolved would be the worst kind of stale access.
            Assert.Null(await accessor.TryGetCurrentAsync());
        }

        [Fact]
        public async Task An_active_portal_user_resolves_and_the_guard_is_therefore_not_vacuous()
        {
            using var host = new PlatformTestHost();
            Seed(host, userId: "u-live", companyId: 7, customerId: 5, active: true);

            var resolved = await new PortalContextAccessor(host.Db, Http(Principal("u-live")))
                .TryGetCurrentAsync();

            // Without this every assertion above would be satisfied by an accessor that returned null
            // unconditionally, which proves nothing about authorization.
            Assert.NotNull(resolved);
            Assert.Equal(7, resolved!.CompanyId);
            Assert.Equal(5, resolved.CustomerId);
        }

        // ---- 2. the reflective sweep the declaration depends on --------------------------------------

        [Fact]
        public async Task Every_member_of_the_credited_service_refuses_without_a_portal_identity()
        {
            using var host = new PlatformTestHost();

            // An accessor that answers "nobody". Every member must come back empty: the analyzer credits
            // this type, so a member that read rows anyway would be a hole the declaration created.
            var service = new PortalDataService(host.Db, Http(user: null) is var http
                ? new PortalContextAccessor(host.Db, http)
                : null!);

            var members = typeof(IPortalDataService).GetMethods(BindingFlags.Public | BindingFlags.Instance);

            // A NEW MEMBER CANNOT ARRIVE UNNOTICED. If the interface grows, this count fails and whoever
            // added it has to extend the sweep rather than inherit the credit silently.
            //
            // IT HAS NOW CAUGHT TWO, and the second one is worth recording because it caught ME.
            // RespondToQuotationAsync, the portal's first write, arrived here before it arrived in any
            // screen. RequestContactAsync then arrived and this file was not updated with it - so the
            // sweep went red, a mutation campaign ran against a suite that was ALREADY failing, and
            // every mutant in it appeared to be killed partly by this test. A guard that is red for an
            // unrelated reason kills everything and proves nothing, which is why the campaign was
            // re-run once this was green again.
            // Seven now: IdentityAsync joined for the shell's own header/band read. It is swept like
            // every other member - no identity, no name, no counts.
            Assert.Equal(7, members.Length);

            foreach (var member in members)
            {
                // Arguments are synthesised from the SIGNATURE, so a member added later is swept
                // without anybody remembering to list it. Every value is a plausible one a real caller
                // could send - an id that could exist, a decision somebody could make - because a
                // member that refused only because its arguments were nonsense would prove nothing.
                var args = member.GetParameters().Select(pi =>
                    pi.ParameterType == typeof(CancellationToken) ? (object?)CancellationToken.None
                    : pi.ParameterType == typeof(int) ? 1
                    : pi.ParameterType == typeof(bool) ? true
                    // An OPTIONAL id, supplied as a real one rather than as null: passing null would
                    // skip the very branch that reads a row, and the sweep is here to prove that every
                    // read is gated on identity.
                    : pi.ParameterType == typeof(int?) ? 1
                    // Free text the caller controls. Plausible content, not empty, so a member that
                    // only refused blank input would not pass by accident.
                    : pi.ParameterType == typeof(string) ? "please call me"
                    : throw new NotSupportedException(
                        $"{member.Name} takes a {pi.ParameterType.Name} this sweep does not know how to " +
                        "supply. Extend it deliberately - do not drop the member.")).ToArray();

                var task = (Task)member.Invoke(service, args)!;
                await task;

                var result = task.GetType().GetProperty("Result")!.GetValue(task);

                if (result is System.Collections.IEnumerable rows and not string)
                {
                    Assert.Empty(rows.Cast<object>());
                }
                else if (result is PortalDecisionResult decision)
                {
                    // A WRITE MEMBER CANNOT SATISFY THIS BY RETURNING NULL. It answers with a record
                    // either way, so the assertion is on the verdict: no identity, no decision, and
                    // the refusal is the undifferentiated one that reveals nothing.
                    Assert.False(decision.Ok);
                    Assert.Equal(PortalDecisionResult.NotFound, decision.Code);
                }
                else
                {
                    Assert.Null(result);   // HomeAsync and any future single-object member
                }
            }
        }

        // ---- fixture ---------------------------------------------------------------------------------

        private static ClaimsPrincipal Principal(string userId)
            => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "portal"));

        private static IHttpContextAccessor Http(ClaimsPrincipal? user)
        {
            var ctx = new DefaultHttpContext();
            if (user != null) { ctx.User = user; }
            return new HttpContextAccessor { HttpContext = ctx };
        }

        private static void Seed(PlatformTestHost host, string userId, int companyId, int customerId, bool active)
        {
            host.Seed.Set<PortalUser>().Add(new PortalUser
            {
                UserId = userId,
                CompanyID = companyId,
                CustomerId = customerId,
                IsActive = active,
            });
            host.Seed.SaveChanges();
        }
    }
}
