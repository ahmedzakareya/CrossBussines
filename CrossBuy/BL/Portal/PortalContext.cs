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
    /// PhotoPath and ContactName are carried so the portal shell can name and picture the person
    /// without a second query - the accessor caches per request, so the header costs no round trip.
    ///
    /// NEITHER IS AN AUTHORITY. They are display facts read from the same PortalUsers row the
    /// company and the customer come from, and nothing in the product may grant on the strength of
    /// them. PhotoPath in particular is NOT a URL the browser is given: it is the stored location
    /// that PortalPhotoService resolves behind an authorising endpoint (see ClientPortalController
    /// .ContactPhoto), because a raw path handed to a client would be authorization by obscurity.
    public sealed record PortalContext(
        int CompanyId,
        int CustomerId,
        int PortalUserId,
        IReadOnlyCollection<string> Capabilities,
        string? PhotoPath = null,
        string? ContactName = null)
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
        private readonly CrossBuy.BL.Platform.IBusinessContextFactory? _contexts;

        /// Read ONLY to detect a tenant disagreement before binding - see the fail-closed check below.
        /// The portal never writes to it directly; ForExternalPortal does that.
        private readonly CrossBuy.BL.Platform.ICompanyScopeHolder? _scope;
        private PortalContext? _cached;
        private bool _resolved;

        /// The factory is OPTIONAL only so a test can construct this accessor for a pure identity
        /// question. Production always has one - it is registered Scoped beside CrossDbContext - and
        /// without it the portal resolves an identity that no company filter and no event recorder can
        /// see, which is the defect this whole seam exists to close.
        public PortalContextAccessor(CrossDbContext db, IHttpContextAccessor http,
            CrossBuy.BL.Platform.IBusinessContextFactory? contexts = null,
            CrossBuy.BL.Platform.ICompanyScopeHolder? scope = null)
        { _db = db; _http = http; _contexts = contexts; _scope = scope; }

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
                    .Select(p => new { p.Id, p.CompanyID, p.CustomerId, p.Capabilities, p.PhotoPath, p.ContactName })
                    .FirstOrDefaultAsync(ct);

                if (link == null || link.CompanyID <= 0 || link.CustomerId <= 0) return null;

                // ---- BIND THE EXTERNAL CONTEXT, which is two fixes in one call -----------------
                //
                // WHAT WAS BROKEN. CrossDbContext's company query filters read ICompanyScopeHolder, and
                // the holder is normally populated as a SIDE EFFECT of resolving a BusinessContext -
                // which BusinessContextFactory does by finding an EMPLOYEE row. A portal customer has
                // none, so the holder was never set, FilterCompanyId stayed 0, and Quotation,
                // SalesInvoice and Customer - all pilot-filtered - returned NOTHING. Every portal screen
                // was empty in production while every portal test passed, because the tests hand the
                // service the cross-company seeding context on purpose.
                //
                // AND SEPARATELY, BusinessEventService.RecordAsync resolves its company through
                // IBusinessContextAccessor, which knew only how to rebuild a context from an Employee
                // row - so a portal-originated producer threw instead of recording.
                //
                // ForExternalPortal closes BOTH: it Publishes the company to the scope holder (the
                // filters) and Binds the context to this DI scope (the accessor, and therefore the event
                // recorder). An earlier revision of this file called ICompanyScopeHolder.Set directly,
                // which fixed only the first half.
                //
                // THE COMPANY COMES FROM THE PortalUser ROW and from nowhere else. The context it
                // produces is IsSystem=false with EmployeeId=null by construction, so nothing here can
                // borrow the trusted-platform-code paths reserved for the outbox dispatcher - the
                // escalation this seam was designed to make impossible rather than merely discouraged.
                //
                // A THROW HERE RESOLVES NO IDENTITY. The holder refuses a conflicting company in one
                // scope, and the catch below turns that refusal into null rather than letting a request
                // proceed with a context the filters disagree with.
                // FAIL CLOSED ON A TENANT DISAGREEMENT, and this check cannot be delegated.
                //
                // BusinessContextFactory.Publish SWALLOWS a conflicting company: Set() throws, Publish
                // catches it, logs an error and the scope KEEPS ITS FIRST COMPANY. That is a reasonable
                // platform choice - one bad resolution should not take down an otherwise fine request -
                // but it is the wrong outcome here. It would leave the EF filters scoped to one company
                // while this accessor handed the portal an identity for another, and the portal would
                // then read nothing while believing it had access, or record an event in a tenant the
                // filters disagree with.
                //
                // It is reachable: a user who is BOTH an employee and a portal contact has their
                // employee company published by CompanyScopeMiddleware first. If the two ever differ,
                // no portal identity is the only safe answer.
                if (_scope != null && _scope.IsResolved && _scope.CompanyId != link.CompanyID)
                    return null;

                _contexts?.ForExternalPortal(link.CompanyID, userId, link.Id);

                return new PortalContext(link.CompanyID, link.CustomerId, link.Id, Parse(link.Capabilities),
                    link.PhotoPath, link.ContactName);
            }
            catch (Exception)
            {
                // A resolver that cannot answer has not granted anything.
                return null;
            }
        }

        /// NULL and EMPTY are different answers, and the difference is load-bearing.
        ///
        /// NULL is a row nobody has configured, and the safe reading of that is the read-only default
        /// rather than "everything". An EMPTY STRING is somebody having decided, explicitly, that this
        /// login may do nothing - which is what provisioning writes when a grant is cleared. Treating
        /// the two alike (which this did, via IsNullOrWhiteSpace) would hand the three read
        /// capabilities straight back to an account whose access had just been taken away.
        private static IReadOnlyCollection<string> Parse(string? csv)
            => csv == null
                ? PortalCapabilities.Default
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
