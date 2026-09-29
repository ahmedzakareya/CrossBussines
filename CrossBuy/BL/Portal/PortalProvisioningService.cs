using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Portal
{
    // =============================================================================================
    // GIVING A CUSTOMER A WAY IN.
    //
    // Until this file existed, nothing in the application created a PortalUser row. The entire Client
    // Portal — identity, capabilities, reads, the quotation decision — was reachable only by someone
    // running an INSERT by hand against production. That is the product-enablement defect this closes,
    // and it is worth saying plainly because it means the portal has never been provisioned by the
    // product itself.
    //
    // THIS IS STAFF AUTHORITY, AND THE SHAPE OF THE API IS THE PROOF.
    //
    // There is no companyId parameter anywhere in this service. The company comes from the caller's
    // BusinessContext and from nowhere else, so there is no value a caller could send that would move
    // the provisioning into another tenant — the single worst thing this service could get wrong. A
    // customerId IS accepted, because somebody has to say which customer, but it is validated against
    // the resolved company before anything is written: it names a target, it never grants one.
    //
    // A PORTAL USER CANNOT REACH THIS, and again structurally rather than by a check. Every method
    // begins by resolving a BusinessContext, which BusinessContextFactory produces only for a request
    // that maps to an EMPLOYEE row. A portal customer has none — that is what being a customer means
    // in this product — so an external caller arrives unresolved and is refused before any argument is
    // read. There is no "is this a portal user" branch to forget.
    //
    // CAPABILITIES ARE CHECKED AGAINST THE DECLARED VOCABULARY. An unknown name is REFUSED rather than
    // stored. A capability nobody implements is a grant nobody can reason about, and it reads to
    // whoever looks at the row as access that was given.
    // =============================================================================================

    /// The outcome of a provisioning act. A CODE rather than a sentence, for the same reason the
    /// portal's own results are: callers branch on it, and screens translate it.
    public sealed record PortalProvisioningResult(bool Ok, string Code, int PortalUserId = 0)
    {
        /// No staff identity could be resolved. This is what an external caller gets, and also what a
        /// signed-in employee with no company gets — both are "you are not the one who does this".
        public const string NotPermitted = "not_permitted";

        /// The customer does not exist in the caller's company. Deliberately the same answer for
        /// "no such customer" and "that customer belongs to another tenant".
        public const string NoSuchCustomer = "no_such_customer";

        /// This login is already linked, or this customer already has this contact linked.
        public const string AlreadyLinked = "already_linked";

        /// A capability name the platform does not declare.
        public const string UnknownCapability = "unknown_capability";

        public const string NoSuchPortalUser = "no_such_portal_user";
        public const string InvalidArgument = "invalid_argument";

        public static PortalProvisioningResult Refused(string code) => new(false, code);
        public static PortalProvisioningResult Done(int id) => new(true, "ok", id);
    }

    public interface IPortalProvisioningService
    {
        /// Link an Identity account (or an invitation with no account yet) to ONE customer of the
        /// CALLER'S company. Returns the new portal user id.
        Task<PortalProvisioningResult> ProvisionAsync(
            int customerId, string contactName, string? email = null, string? phone = null,
            string? userId = null, IReadOnlyCollection<string>? capabilities = null,
            CancellationToken ct = default);

        /// Revoke access without losing the audit trail.
        Task<PortalProvisioningResult> DeactivateAsync(int portalUserId, CancellationToken ct = default);

        /// Restore access to the SAME customer in the SAME company. Reactivation never re-targets a
        /// link; that would be a new link wearing an old row's id.
        Task<PortalProvisioningResult> ReactivateAsync(int portalUserId, CancellationToken ct = default);

        /// Replace the explicit capability grant. Every name is validated; an unknown one refuses the
        /// whole call rather than being dropped silently.
        Task<PortalProvisioningResult> SetCapabilitiesAsync(
            int portalUserId, IReadOnlyCollection<string> capabilities, CancellationToken ct = default);
    }

    public sealed class PortalProvisioningService : IPortalProvisioningService
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;

        public PortalProvisioningService(CrossDbContext db, IBusinessContextAccessor contexts)
        { _db = db; _contexts = contexts; }

        /// The staff company, or null. TryGetCurrentAsync rather than GetCurrentAsync deliberately: an
        /// unresolved caller must be REFUSED, not met with an exception, because "an outsider tried" is
        /// an ordinary outcome on this service and not an error condition.
        private async Task<(int CompanyId, int? EmployeeId)?> ActorAsync(CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context == null || context.CompanyId <= 0) return null;
            return (context.CompanyId, context.EmployeeId);
        }

        public async Task<PortalProvisioningResult> ProvisionAsync(
            int customerId, string contactName, string? email = null, string? phone = null,
            string? userId = null, IReadOnlyCollection<string>? capabilities = null,
            CancellationToken ct = default)
        {
            // 1 - WHO IS ASKING. No context, no provisioning: this is the line an external caller
            // cannot cross, and it is the same line every internal service already holds.
            var actor = await ActorAsync(ct);
            if (actor == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.NotPermitted);
            var (companyId, employeeId) = actor.Value;

            if (customerId <= 0 || string.IsNullOrWhiteSpace(contactName))
                return PortalProvisioningResult.Refused(PortalProvisioningResult.InvalidArgument);

            // 2 - CAPABILITIES, validated before anything is written. Refusing the whole call rather
            // than dropping the bad name means a typo can never silently provision a narrower account
            // than the person doing it believed they were creating.
            var grant = capabilities is { Count: > 0 }
                ? capabilities.Select(c => c?.Trim() ?? "").ToArray()
                : PortalCapabilities.Default;

            if (grant.Any(c => !PortalCapabilities.IsKnown(c)))
                return PortalProvisioningResult.Refused(PortalProvisioningResult.UnknownCapability);

            // 3 - THE TARGET, checked against the CALLER'S company. The customerId named a row; this is
            // where it is established that the row is one the caller may act on at all.
            var customerExists = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.ID == customerId && c.CompanyID == companyId, ct);
            if (!customerExists)
                return PortalProvisioningResult.Refused(PortalProvisioningResult.NoSuchCustomer);

            // 4 - COLLISIONS. Two separate rules, and they answer different questions.
            //
            // A login already linked ANYWHERE is refused - not merely "already linked in this company".
            // The database enforces it too (UX_PortalUsers_UserId is unique across the table), and the
            // reason it is global is that a single Identity account speaking for two customers would
            // make the portal's tenant boundary depend on which row a query returned first.
            var trimmedUser = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
            if (trimmedUser != null)
            {
                var loginTaken = await _db.Set<PortalUser>().AsNoTracking()
                    .AnyAsync(p => p.UserId == trimmedUser, ct);
                if (loginTaken) return PortalProvisioningResult.Refused(PortalProvisioningResult.AlreadyLinked);
            }

            // And the same PERSON twice for the same customer, which is the ordinary double-click: an
            // active link for this customer with this contact name already does the job.
            var name = contactName.Trim();
            var duplicateContact = await _db.Set<PortalUser>().AsNoTracking()
                .AnyAsync(p => p.CompanyID == companyId
                               && p.CustomerId == customerId
                               && p.ContactName == name
                               && p.IsActive, ct);
            if (duplicateContact) return PortalProvisioningResult.Refused(PortalProvisioningResult.AlreadyLinked);

            var row = new PortalUser
            {
                CompanyID = companyId,               // from the CONTEXT, never from an argument
                CustomerId = customerId,
                UserId = trimmedUser,
                ContactName = name,
                Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
                Capabilities = string.Join(",", grant),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedByEmployeeId = employeeId,    // the audit trail this row is required to carry
            };

            _db.Set<PortalUser>().Add(row);
            await _db.SaveChangesAsync(ct);
            return PortalProvisioningResult.Done(row.Id);
        }

        public Task<PortalProvisioningResult> DeactivateAsync(int portalUserId, CancellationToken ct = default)
            => SetActiveAsync(portalUserId, active: false, ct);

        public Task<PortalProvisioningResult> ReactivateAsync(int portalUserId, CancellationToken ct = default)
            => SetActiveAsync(portalUserId, active: true, ct);

        /// One implementation for both directions. Reactivation deliberately touches NOTHING but the
        /// flag: not the company, not the customer, not the capabilities. Restoring somebody's access
        /// is not the moment to silently re-decide what they may do, and a reactivation that reset the
        /// grant would quietly widen or narrow it without anyone choosing.
        private async Task<PortalProvisioningResult> SetActiveAsync(int portalUserId, bool active, CancellationToken ct)
        {
            var actor = await ActorAsync(ct);
            if (actor == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.NotPermitted);

            // The row is fetched WITH the company predicate. A staff member of one tenant must not be
            // able to deactivate another tenant's portal login by knowing its id - and "not found" is
            // the same answer for a row that does not exist and one that is not theirs.
            var row = await _db.Set<PortalUser>()
                .FirstOrDefaultAsync(p => p.Id == portalUserId && p.CompanyID == actor.Value.CompanyId, ct);
            if (row == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.NoSuchPortalUser);

            row.IsActive = active;
            await _db.SaveChangesAsync(ct);
            return PortalProvisioningResult.Done(row.Id);
        }

        public async Task<PortalProvisioningResult> SetCapabilitiesAsync(
            int portalUserId, IReadOnlyCollection<string> capabilities, CancellationToken ct = default)
        {
            var actor = await ActorAsync(ct);
            if (actor == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.NotPermitted);

            if (capabilities == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.InvalidArgument);

            var grant = capabilities.Select(c => c?.Trim() ?? "").ToArray();
            if (grant.Any(c => !PortalCapabilities.IsKnown(c)))
                return PortalProvisioningResult.Refused(PortalProvisioningResult.UnknownCapability);

            var row = await _db.Set<PortalUser>()
                .FirstOrDefaultAsync(p => p.Id == portalUserId && p.CompanyID == actor.Value.CompanyId, ct);
            if (row == null) return PortalProvisioningResult.Refused(PortalProvisioningResult.NoSuchPortalUser);

            // An EMPTY grant is a real decision and is stored as one: an empty string, not null.
            // Null means "nobody configured this row" and the resolver reads it as the default, so
            // writing null here would silently hand back the three read capabilities to somebody whose
            // access was just removed.
            row.Capabilities = string.Join(",", grant);
            await _db.SaveChangesAsync(ct);
            return PortalProvisioningResult.Done(row.Id);
        }
    }
}
