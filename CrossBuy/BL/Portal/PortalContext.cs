using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Portal
{
    // =============================================================================================
    // CLIENT PORTAL — identity resolution.
    //
    // A SEPARATE PATH, ON PURPOSE. This does not extend BusinessContext and does not touch it. An
    // employee context answers "which company does this member of staff act for"; a portal context
    // answers "which customer of which company does this outsider speak for". Merging them would
    // mean one object whose meaning depends on who is holding it, and every consumer would have to
    // remember to ask which kind it was.
    //
    // The separation is also what makes the boundary provable rather than promised. Nothing here can
    // produce a BusinessContext, so no portal request can reach an internal access service with an
    // identity it would accept — those services resolve through BusinessContextFactory, which reads
    // the Employee session blob and refuses when there is none.
    //
    // FAILS CLOSED, ALWAYS. Null is the answer for: no principal, no claim, no PortalUser row, an
    // inactive row, a row with no customer, and any exception while resolving. There is no branch in
    // this file that produces a context from a value supplied by the caller.
    // =============================================================================================

    /// Who is asking, and on whose behalf. Immutable, and never built from a request parameter.
    public sealed record PortalContext(
        int CompanyId,
        int CustomerId,
        int PortalUserId,
        IReadOnlyCollection<string> Capabilities)
    {
        public bool Can(string capability)
            => Capabilities.Contains(capability, StringComparer.Ordinal);
    }

    public interface IPortalContextAccessor
    {
        /// The current external identity, or null when there is not one. Null means REFUSE.
        Task<PortalContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default);
    }

    public sealed class PortalContextAccessor : IPortalContextAccessor
    {
        private readonly CrossDbContext _db;
        private readonly IHttpContextAccessor _http;
        private PortalContext? _cached;
        private bool _resolved;

        public PortalContextAccessor(CrossDbContext db, IHttpContextAccessor http)
        { _db = db; _http = http; }

        public async Task<PortalContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = await ResolveAsync(cancellationToken);
            return _cached;
        }

        private async Task<PortalContext?> ResolveAsync(CancellationToken ct)
        {
            try
            {
                // The ONLY input is the authenticated principal. Not a header, not a route value, not
                // a query string: a portal identity that could be named by the request would be no
                // identity at all.
                var userId = _http.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return null;

                var link = await _db.Set<PortalUser>().AsNoTracking()
                    .Where(p => p.UserId == userId && p.IsActive)
                    .Select(p => new { p.Id, p.CompanyID, p.CustomerId, p.Capabilities })
                    .FirstOrDefaultAsync(ct);

                if (link == null || link.CompanyID <= 0 || link.CustomerId <= 0) return null;

                return new PortalContext(link.CompanyID, link.CustomerId, link.Id, Parse(link.Capabilities));
            }
            catch (Exception)
            {
                // A resolver that cannot answer has not granted anything.
                return null;
            }
        }

        /// Absent capabilities mean the read-only default, not "everything". A blank column is a row
        /// nobody has configured, and the safe reading of that is the minimum.
        private static IReadOnlyCollection<string> Parse(string? csv)
            => string.IsNullOrWhiteSpace(csv)
                ? PortalCapabilities.Default
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
