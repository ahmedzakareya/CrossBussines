using CrossBuy.BL.Portal;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // THE EXTERNAL DOOR INTO A CONVERSATION — Wave 3B-B1.
    //
    // WHY THIS IS A SEPARATE POLICY AND NOT A BRANCH IN CommAccessPolicy.
    //
    // CommAccessPolicy asks IPlatformPermissionProvider, which asks a module access service, whose
    // gate 7 refuses any context without an employee id. That gate is CORRECT and this file does not
    // touch it: an external customer holds no module right, and teaching the employee gate to accept
    // one would be the single change most likely to leak the whole ERP. A portal caller is refused
    // there and is meant to be.
    //
    // So external access is answered by a DIFFERENT question, asked of different facts. Not "does
    // this person hold a module permission" — they never will — but "is this conversation attached to
    // a record that belongs to THIS customer, and has this customer been made a participant".
    //
    // THE FIVE CONDITIONS, ALL REQUIRED, ALL SERVER-SIDE.
    //
    //   1. A resolved portal context. It comes from IPortalContextAccessor, which reads the
    //      PortalUsers row behind the authenticated cookie and returns null for an inactive row.
    //      Nothing here is taken from a query string, form, body or route value.
    //   2. Same company, compared on the thread ROW.
    //   3. The anchor entity is one the portal may see at all, and its row belongs to THIS customer.
    //      This is the condition with no precedent in the Communication platform: a thread carries no
    //      CustomerId, so the customer is resolved from the business record the thread is attached to.
    //   4. A LIVE participant row naming this portal user. Being the customer on the quotation is not
    //      enough — somebody has to have added them to the conversation.
    //   5. Content visibility, applied per comment and not per thread: only CommVisibility.Public
    //      ever leaves the company's staff.
    //
    // EACH OF THESE IS LOAD-BEARING, and the brief names the four ways to get this wrong. Same
    // company alone is not enough (two customers of one company). A thread id existing is not enough
    // (that is the oracle this refuses to be). A Public business entity is not enough (public is a
    // property of the CONTENT tier, not a grant to a person). Knowing the entity id is not enough —
    // it is an identifier, never an authority.
    //
    // WHY NOT DENORMALISE CustomerId ONTO CommThreads. It would make condition 3 one column
    // comparison instead of one indexed read, and it would create a second place where the customer
    // of a quotation is recorded — which is a place it can disagree with the quotation. The anchor
    // row is the fact; this policy reads the fact.
    // =============================================================================================

    /// The external answer. Reason is for the audit row and the log, never for the caller: a refusal
    /// that explains itself tells an outsider which of the five conditions they failed, and three of
    /// the five are facts about other people's data.
    public sealed class CommExternalAccess
    {
        public required bool CanRead { get; init; }

        /// External callers may READ in this increment. Commenting is 3B-C and is stated here as a
        /// field rather than assumed, so the day it turns on there is one place to change.
        public required bool CanComment { get; init; }

        public required string Reason { get; init; }

        /// The visibilities an external caller may read. Exactly one tier, and it is a SET rather than
        /// a single value so the read path filters in SQL, the same shape CommVisibilitySet uses.
        public required IReadOnlySet<string> Visibilities { get; init; }

        public static CommExternalAccess Deny(string reason) => new()
        {
            CanRead = false,
            CanComment = false,
            Reason = reason,
            Visibilities = new HashSet<string>(StringComparer.Ordinal),
        };

        public static CommExternalAccess Allow(string reason) => new()
        {
            CanRead = true,
            // NOT YET. External authoring is Wave 3B-C; representing the actor is this wave.
            CanComment = false,
            Reason = reason,
            Visibilities = new HashSet<string>(StringComparer.Ordinal) { CommVisibility.Public },
        };
    }

    public interface ICommExternalAccessPolicy
    {
        /// May THIS portal identity read THIS thread? Takes the PortalContext explicitly rather than
        /// resolving it internally, so a caller cannot accidentally pass a context belonging to
        /// somebody else — there is nothing to pass but the one the accessor produced.
        Task<CommExternalAccess> ResolveThreadAccessAsync(
            PortalContext portal, CommThread thread, CancellationToken cancellationToken = default);

        /// Per-comment read decision. Separate from the thread decision because a thread an external
        /// caller may open still contains internal notes they may not read — visibility is a property
        /// of the comment, and applying it only at thread level is how internal notes leak.
        bool CanReadComment(CommExternalAccess access, CommComment comment);

        /// The entity codes a portal identity may ever reach a conversation through.
        IReadOnlyCollection<string> ExternallyAnchorableEntityCodes { get; }
    }

    public sealed class CommExternalAccessPolicy : ICommExternalAccessPolicy
    {
        private readonly CrossDbContext _db;

        public CommExternalAccessPolicy(CrossDbContext db) => _db = db;

        // ---------------------------------------------------------------------------------------------
        // THE ALLOW-LIST, and it is an allow-list rather than a deny-list on purpose: a new entity type
        // registered by another tab must not become externally reachable by default. Adding a code here
        // is a decision with a customer-resolution arm attached; forgetting to add one costs a feature,
        // while forgetting to REMOVE one from a deny-list would cost a disclosure.
        //
        // Quotation only, in this increment. It is the one comment-capable entity the Client Portal
        // already discloses, and its customer is a column on the row. SalesInvoice is deliberately
        // absent until its anchor arm is written and tested — an entity listed without a resolver
        // would fall through to "no customer proven" and deny, which is safe but looks like a bug.
        // ---------------------------------------------------------------------------------------------
        private const string QuotationCode = "Quotation";

        public IReadOnlyCollection<string> ExternallyAnchorableEntityCodes { get; } =
            new HashSet<string>(StringComparer.Ordinal) { QuotationCode };

        public async Task<CommExternalAccess> ResolveThreadAccessAsync(
            PortalContext portal, CommThread thread, CancellationToken cancellationToken = default)
        {
            // ---- 1. a resolved external identity -------------------------------------------------
            if (portal == null) return CommExternalAccess.Deny("no portal context");
            if (portal.CompanyId <= 0 || portal.CustomerId <= 0 || portal.PortalUserId <= 0)
                return CommExternalAccess.Deny("portal context is not fully resolved");
            if (thread == null) return CommExternalAccess.Deny("no thread");

            // ---- 2. company, on the ROW --------------------------------------------------------
            // These tables carry no global query filter (see CommThreadEntities), so this comparison is
            // the only thing standing between two tenants, exactly as it is on the internal path.
            if (thread.CompanyID != portal.CompanyId)
                return CommExternalAccess.Deny("thread belongs to another company");

            if (thread.DeletedAt != null)
                return CommExternalAccess.Deny("thread is deleted");

            // ---- 3. the anchor, and whose it is -------------------------------------------------
            if (!ExternallyAnchorableEntityCodes.Contains(thread.EntityType))
                return CommExternalAccess.Deny(
                    $"entity type '{thread.EntityType}' is not externally anchorable");

            bool anchorIsTheirs = await AnchorBelongsToCustomerAsync(thread, portal, cancellationToken);
            if (!anchorIsTheirs)
                // ONE REASON for absent, another company's, another customer's and deleted alike, so a
                // thread id cannot be walked to discover which records exist.
                return CommExternalAccess.Deny("anchor record is not this customer's");

            // ---- 4. membership -------------------------------------------------------------------
            // Being the customer on the record is NOT membership. Somebody has to have added this
            // portal user to this conversation, and the row has to still be live.
            bool isParticipant = await _db.Set<CommParticipant>().AsNoTracking()
                .AnyAsync(p => p.ThreadId == thread.Id
                               && p.CompanyID == portal.CompanyId
                               && p.PortalUserId == portal.PortalUserId
                               && p.RemovedAt == null,
                    cancellationToken);

            if (!isParticipant)
                return CommExternalAccess.Deny("not a live participant of this thread");

            // ---- 5. the thread's own ceiling ------------------------------------------------------
            // A thread opened at Internal or above is not externally readable whatever its comments
            // say — the ceiling is the promise made to whoever opened it.
            if (!string.Equals(thread.Visibility, CommVisibility.Public, StringComparison.Ordinal))
                return CommExternalAccess.Deny(
                    $"thread visibility '{thread.Visibility}' is not externally readable");

            return CommExternalAccess.Allow("company, customer anchor, participation and visibility all hold");
        }

        // ---------------------------------------------------------------------------------------------
        /// ONE indexed read that proves company AND customer together.
        ///
        /// Both predicates are in the same query on purpose. Fetching the row and comparing in memory
        /// would put the customer check one `if` away from being forgotten, and would read a row this
        /// caller has no right to before deciding they have no right to it.
        private async Task<bool> AnchorBelongsToCustomerAsync(
            CommThread thread, PortalContext portal, CancellationToken cancellationToken)
        {
            switch (thread.EntityType)
            {
                case QuotationCode:
                    return await _db.Quotations.AsNoTracking()
                        .AnyAsync(q => q.ID == thread.EntityId
                                       && q.CompanyID == portal.CompanyId
                                       && q.CustomerId == portal.CustomerId,
                            cancellationToken);

                default:
                    // Unreachable while the allow-list and this switch agree, and it FAILS CLOSED if
                    // they ever drift — a code added to the list without an arm here denies rather
                    // than falling through to "allowed".
                    return false;
            }
        }

        // ---------------------------------------------------------------------------------------------
        public bool CanReadComment(CommExternalAccess access, CommComment comment)
        {
            ArgumentNullException.ThrowIfNull(access);
            ArgumentNullException.ThrowIfNull(comment);

            if (!access.CanRead) return false;
            if (comment.DeletedAt != null) return false;

            // THE CONTENT-LEVEL CHECK, and the reason it is not folded into the thread decision: a
            // thread an external caller may open still contains internal notes. Public is the only
            // tier that may ever leave the company's staff, and CommVisibility says so in its own
            // definition — this is that rule applied, not a second opinion about it.
            return access.Visibilities.Contains(comment.Visibility);
        }
    }
}
