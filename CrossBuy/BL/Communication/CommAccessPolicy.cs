using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-033) — CONVERSATION AND THREAD PERMISSIONS (modules 23, 24).
    //
    // THE ONE INVARIANT THIS FILE EXISTS TO GUARANTEE
    //
    //     A communication permission can never widen access to a business record.
    //
    // Entity-level authorization is asked FIRST, of the existing IPlatformPermissionProvider — this platform
    // adds no authorization engine, defines no new roles, and touches no module access service. Only once the
    // caller may already View the anchor record does thread-level state (participation, grants, visibility)
    // decide whether they may read or write THIS conversation. A thread grant can therefore open a restricted
    // note to a colleague who can already open the invoice; it can never open the invoice.
    //
    // Everything below follows from that ordering, and each rule has a cost already paid in this repository:
    //
    //   * "Authentication is not authorization, and a query filter is not an authorization control"
    //     (CLAUDE.md). So company is checked on the ROW, explicitly, in addition to any filter.
    //   * "Hiding a UI control is not a control" (CLAUDE.md). So capabilities are computed here and returned,
    //     and the UI renders a decision rather than making one.
    //   * Absent, other-company and soft-deleted answer IDENTICALLY, the rule CommunicationAccessService
    //     already applies to conversations, so a thread id cannot be used as an existence oracle.
    //   * Visibility is evaluated with the kernel's own elevated actions (ViewConfidential / ViewRestricted)
    //     rather than a new vocabulary, so a user's rights mean the same thing on a comment as on an event.
    // =============================================================================================
    public interface ICommAccessPolicy
    {
        // May the caller see this record's conversation surface at all? Entity-level only — the cheap gate a
        // read path applies before it loads anything.
        Task<CommAccessDecision> CanAccessEntityAsync(
            BusinessContext context, CommEntityRef entity, string capability, CancellationToken cancellationToken = default);

        // The caller's rights on ONE thread, including the visibility ceiling they may author at.
        Task<CommThreadAccess> ResolveThreadAccessAsync(
            BusinessContext context, CommThread thread, CancellationToken cancellationToken = default);

        // Throws CommAccessDeniedException unless the caller holds `level` on the thread.
        Task<CommThreadAccess> RequireThreadAsync(
            BusinessContext context, CommThread thread, string level, CancellationToken cancellationToken = default);

        // Which comment visibilities this caller may READ on this thread. Returned as a set so the caller
        // filters in SQL rather than row by row.
        Task<CommVisibilitySet> ResolveVisibilitiesAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default);

        // Per-comment read decision, applying the own-author exception the set alone cannot express.
        bool CanReadComment(CommVisibilitySet visibilities, CommComment comment, BusinessContext context);

        // Per-comment write decisions (edit / delete / restore).
        CommCommentRights ResolveCommentRights(
            BusinessContext context, CommThreadAccess threadAccess, CommComment comment);
    }

    public sealed class CommAccessDecision
    {
        public required bool Allowed { get; init; }

        // For the AUDIT ROW, never for the user — a denial that explains itself leaks facts about content the
        // caller cannot read. See CommAccessDeniedException.
        public required string Reason { get; init; }

        public static CommAccessDecision Allow(string reason) => new() { Allowed = true, Reason = reason };
        public static CommAccessDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
    }

    public sealed class CommThreadAccess
    {
        public required long ThreadId { get; init; }
        public required bool CanRead { get; init; }
        public required bool CanComment { get; init; }
        public required bool CanModerate { get; init; }
        public required string Reason { get; init; }

        // The visibilities this caller may post at HERE. See CommThreadCapabilities.AuthorVisibilities for the
        // two opposing bounds that produce it, and why a single "max" value cannot.
        public required IReadOnlyList<string> AuthorVisibilities { get; init; }

        // Set when the caller is a live participant. Read by the notification path so an author is auto-followed
        // without a second query.
        public required bool IsParticipant { get; init; }

        public bool MayAuthorAt(string? visibility)
            => visibility != null && AuthorVisibilities.Contains(visibility, StringComparer.Ordinal);

        // The most OPEN allowed tier = the lowest rank in the set. Internal when the set is empty, which only
        // happens on a denied access where nothing will be authored anyway.
        public string MostOpenAuthorVisibility =>
            AuthorVisibilities.Count == 0
                ? CommVisibility.Internal
                : AuthorVisibilities.OrderBy(CommVisibility.Rank).First();

        public CommThreadCapabilities ToCapabilities() => new()
        {
            CanRead = CanRead,
            CanComment = CanComment,
            CanModerate = CanModerate,
            AuthorVisibilities = AuthorVisibilities,
            MostOpenAuthorVisibility = MostOpenAuthorVisibility,
        };

        public static CommThreadAccess None(long threadId, string reason) => new()
        {
            ThreadId = threadId, CanRead = false, CanComment = false, CanModerate = false,
            AuthorVisibilities = Array.Empty<string>(), Reason = reason, IsParticipant = false,
        };
    }

    // The readable-visibility answer.
    //
    // TWO values, and the distinction is the one the kernel's timeline learned the hard way (its comment: a
    // single value "silently grants every viewer every restricted event"):
    //   Allowed              — what the SQL filter may let through, and it CONTAINS Restricted for an ordinary
    //                          user, purely so their OWN restricted comments can be fetched.
    //   MayReadAnyRestricted — the manager grant. Only this permits reading somebody ELSE's restricted comment,
    //                          and it is applied row by row afterwards.
    public sealed class CommVisibilitySet
    {
        public required IReadOnlySet<string> Allowed { get; init; }
        public required bool MayReadAnyRestricted { get; init; }

        public static CommVisibilitySet Nothing() => new()
        {
            Allowed = new HashSet<string>(StringComparer.Ordinal),
            MayReadAnyRestricted = false,
        };
    }

    public sealed class CommCommentRights
    {
        public required bool CanEdit { get; init; }
        public required bool CanDelete { get; init; }
        public required bool CanRestore { get; init; }
        public required bool CanReact { get; init; }
        public required bool CanReply { get; init; }
        public required string Reason { get; init; }

        public CommCommentCapabilities ToCapabilities() => new()
        {
            CanEdit = CanEdit, CanDelete = CanDelete, CanRestore = CanRestore,
            CanReact = CanReact, CanReply = CanReply,
        };
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommAccessPolicy : ICommAccessPolicy
    {
        private readonly IPlatformPermissionProvider _permissions;
        private readonly ICommEntitySurface _surface;
        private readonly ICommPrincipalResolver _principals;
        private readonly CommDb _db;
        private readonly CommunicationPlatformOptions _options;

        public CommAccessPolicy(
            IPlatformPermissionProvider permissions,
            ICommEntitySurface surface,
            ICommPrincipalResolver principals,
            CrossDbContext db,
            IOptions<CommunicationPlatformOptions> options)
        {
            _permissions = permissions;
            _surface = surface;
            _principals = principals;
            _db = new CommDb(db);
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommAccessDecision> CanAccessEntityAsync(
            BusinessContext context, CommEntityRef entity, string capability, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.CompanyId <= 0)
                return CommAccessDecision.Deny("no company scope resolved");

            var surface = await _surface.EvaluateAsync(entity, capability);
            if (!surface.Allowed)
                return CommAccessDecision.Deny("surface: " + surface.Reason);

            // THE ENTITY GATE. The existing provider, the existing action vocabulary, the existing module
            // adapters. This platform contributes nothing to this decision — which is exactly why it cannot
            // widen it.
            var view = await _permissions.CanAsync(
                context, surface.Definition!.Code, entity.EntityId, PlatformActions.View, cancellationToken);

            return view.Allowed
                ? CommAccessDecision.Allow("entity View granted by " + (view.Reason ?? "module RBAC"))
                : CommAccessDecision.Deny("entity View denied: " + (view.Reason ?? "module RBAC"));
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommThreadAccess> ResolveThreadAccessAsync(
            BusinessContext context, CommThread thread, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(thread);

            // Company on the ROW. A filter is not an authorization control, and these tables have no filter
            // anyway — so this comparison is the only thing standing between two tenants.
            if (thread.CompanyID != context.CompanyId)
                return CommThreadAccess.None(thread.Id, "thread belongs to another company");

            if (thread.DeletedAt != null)
                return CommThreadAccess.None(thread.Id, "thread is deleted");

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);

            var entityDecision = await CanAccessEntityAsync(context, entity, CommCapabilities.Comments, cancellationToken);
            if (!entityDecision.Allowed)
                return CommThreadAccess.None(thread.Id, entityDecision.Reason);

            var visibilities = await ResolveVisibilitiesAsync(context, entity, cancellationToken);

            bool isParticipant = await IsParticipantAsync(thread.Id, context.EmployeeId, cancellationToken);

            // The thread's own ceiling. A caller who cannot read Confidential cannot read a Confidential
            // THREAD at all, whatever its individual comments say — the ceiling is the promise made to whoever
            // opened the thread at that tier.
            //
            // THE RESTRICTED OWN-ACTOR EXCEPTION applies to the thread row too, and it must: without it, the
            // employee who OPENED a restricted note could not read the thread they had just created, because
            // opening a thread does not by itself make somebody a participant. This mirrors exactly what the
            // kernel's timeline does for a restricted event (`r.ActorEmployeeId != context.EmployeeId`) — the
            // thread's creator IS its actor.
            bool restrictedButMine = thread.Visibility == CommVisibility.Restricted
                                     && (visibilities.MayReadAnyRestricted
                                         || isParticipant
                                         || (context.EmployeeId is > 0 && thread.CreatedBy == context.EmployeeId));

            bool ceilingReadable = visibilities.Allowed.Contains(thread.Visibility)
                                   && (thread.Visibility != CommVisibility.Restricted || restrictedButMine);

            var grant = await ResolveGrantLevelAsync(context, thread.Id, cancellationToken);

            // A grant is what makes a thread above the caller's tier readable — the "additive within the
            // entity" half of the invariant. Note it is consulted only AFTER entity View passed.
            bool canRead = ceilingReadable || CommPermissionLevel.Satisfies(grant, CommPermissionLevel.Read);

            if (!canRead)
                return CommThreadAccess.None(thread.Id,
                    $"thread visibility '{thread.Visibility}' is above the caller's tier and no thread grant covers them");

            bool canComment = !thread.IsLocked
                              && (CommPermissionLevel.Satisfies(grant, CommPermissionLevel.Comment) || ceilingReadable);

            // Moderation needs an explicit Moderate grant or the elevated module right. Being the thread's
            // creator is NOT enough on its own: a thread is created implicitly by the first commenter, so
            // creator-moderates would hand moderation to whoever happened to type first.
            bool canModerate = CommPermissionLevel.Satisfies(grant, CommPermissionLevel.Moderate)
                               || visibilities.MayReadAnyRestricted;

            return new CommThreadAccess
            {
                ThreadId = thread.Id,
                CanRead = true,
                CanComment = canComment,
                CanModerate = canModerate,
                AuthorVisibilities = ResolveAuthorVisibilities(thread, visibilities),
                IsParticipant = isParticipant,
                Reason = thread.IsLocked ? "thread is locked" : "ok",
            };
        }

        // THE TWO OPPOSING BOUNDS, intersected.
        //
        // A tier is authorable when it is BOTH:
        //   * no more open than the thread   (rank >= the thread's rank)
        //   * readable by this caller        (present in the readable set)
        //
        // The bounds pull in opposite directions, which is why one "max" value cannot express them: an earlier
        // version of this method returned the MORE RESTRICTIVE of the two and then refused anything more open —
        // which rejected an Internal comment on an Internal thread, i.e. the ordinary case. The tests caught it.
        //
        // Restricted is present for any resolved employee because a private note about a record you can open is
        // a normal act and the author can always read their own (see ResolveVisibilitiesAsync).
        private static IReadOnlyList<string> ResolveAuthorVisibilities(CommThread thread, CommVisibilitySet visibilities)
        {
            int threadRank = CommVisibility.Rank(thread.Visibility);

            return CommVisibility.Values
                .Where(v => CommVisibility.Rank(v) >= threadRank)
                .Where(visibilities.Allowed.Contains)
                .OrderBy(CommVisibility.Rank)
                .ToList();
        }

        public async Task<CommThreadAccess> RequireThreadAsync(
            BusinessContext context, CommThread thread, string level, CancellationToken cancellationToken = default)
        {
            var access = await ResolveThreadAccessAsync(context, thread, cancellationToken);

            bool ok = level switch
            {
                CommPermissionLevel.Read => access.CanRead,
                CommPermissionLevel.Comment => access.CanComment,
                CommPermissionLevel.Moderate => access.CanModerate,
                _ => false,
            };

            if (!ok)
                throw new CommAccessDeniedException(
                    level, new CommEntityRef(thread.EntityType, thread.EntityId), access.Reason);

            return access;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommVisibilitySet> ResolveVisibilitiesAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return CommVisibilitySet.Nothing();

            // Public and Internal both follow from the entity View that has already been established: Public is
            // by definition the most open tier, so anyone who may read Internal may read Public.
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                CommVisibility.Public,
                CommVisibility.Internal,
            };

            var confidential = await _permissions.CanAsync(
                context, entity.EntityCode, entity.EntityId, PlatformActions.ViewConfidential, cancellationToken);
            if (confidential.Allowed) allowed.Add(CommVisibility.Confidential);

            var restricted = await _permissions.CanAsync(
                context, entity.EntityCode, entity.EntityId, PlatformActions.ViewRestricted, cancellationToken);
            if (restricted.Allowed)
            {
                allowed.Add(CommVisibility.Restricted);
                return new CommVisibilitySet { Allowed = allowed, MayReadAnyRestricted = true };
            }

            if (context.EmployeeId is > 0)
            {
                // The own-author exception, and the reason the SET and the FLAG are separate values. Restricted
                // goes in so a caller's OWN restricted comments can be fetched by the SQL filter; CanReadComment
                // then drops everybody else's. Collapsing the two would grant every viewer every restricted
                // comment — the precise bug the kernel's timeline documents.
                //
                // It also makes Restricted AUTHORABLE for any resolved employee, which is intended: writing a
                // private note about a record you can open is a normal act, and the author can always read
                // their own.
                allowed.Add(CommVisibility.Restricted);
            }

            return new CommVisibilitySet { Allowed = allowed, MayReadAnyRestricted = false };
        }

        public bool CanReadComment(CommVisibilitySet visibilities, CommComment comment, BusinessContext context)
        {
            ArgumentNullException.ThrowIfNull(visibilities);
            ArgumentNullException.ThrowIfNull(comment);

            if (!visibilities.Allowed.Contains(comment.Visibility)) return false;

            // The row-level half of the own-author exception. Testing membership of Allowed here instead would
            // be the leak described above.
            if (comment.Visibility == CommVisibility.Restricted
                && !visibilities.MayReadAnyRestricted
                && comment.AuthorEmployeeId != (context.EmployeeId ?? 0))
                return false;

            return true;
        }

        public CommCommentRights ResolveCommentRights(
            BusinessContext context, CommThreadAccess threadAccess, CommComment comment)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(threadAccess);
            ArgumentNullException.ThrowIfNull(comment);

            bool mine = comment.AuthorEmployeeId == (context.EmployeeId ?? 0);
            bool deleted = comment.DeletedAt != null;

            if (!threadAccess.CanRead)
                return new CommCommentRights
                {
                    CanEdit = false, CanDelete = false, CanRestore = false, CanReact = false, CanReply = false,
                    Reason = "thread not readable",
                };

            // THE EDIT WINDOW. An author may correct their own wording for a while; after that, editing is a
            // moderator act. Without a window, a comment somebody relied on last month can be rewritten
            // silently — the revision history would record it, but nobody re-reads a history they have no
            // reason to suspect. A moderator edit is at least attributable to a role.
            bool withinWindow = _options.AuthorEditWindowMinutes == null
                || (comment.CreatedAt.HasValue
                    && DateTime.UtcNow - comment.CreatedAt.Value <= TimeSpan.FromMinutes(_options.AuthorEditWindowMinutes.Value));

            bool canEdit = !deleted && threadAccess.CanComment
                           && ((mine && withinWindow) || threadAccess.CanModerate);

            // An author may always retract their own comment, window or not: withdrawing something you said is
            // not the same act as rewriting it, and the row survives as a soft delete either way.
            bool canDelete = !deleted && (mine || threadAccess.CanModerate);

            // Only a moderator restores. An author who could restore their own deletion could use delete as a
            // way to hide a comment from a review and bring it back afterwards.
            bool canRestore = deleted && threadAccess.CanModerate;

            return new CommCommentRights
            {
                CanEdit = canEdit,
                CanDelete = canDelete,
                CanRestore = canRestore,
                CanReact = !deleted && threadAccess.CanComment,
                CanReply = !deleted && threadAccess.CanComment && comment.Depth < _options.MaxReplyDepth,
                Reason = mine && !withinWindow && !threadAccess.CanModerate ? "author edit window closed" : "ok",
            };
        }

        // ---------------------------------------------------------------------------------------------
        // The highest LEVEL any live grant gives this caller on the thread, across all four principal kinds.
        //
        // Employee grants are matched in SQL; group grants are expanded through ICommPrincipalResolver — the
        // SAME expansion mentions use, which is what stops a department grant and a department mention from
        // disagreeing about who is in the department.
        private async Task<string?> ResolveGrantLevelAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken)
        {
            if (context.EmployeeId is not > 0) return null;
            int employeeId = context.EmployeeId.Value;

            var grants = await _db.ThreadPermissions.AsNoTracking()
                .Where(p => p.ThreadId == threadId
                            && p.CompanyID == context.CompanyId
                            && p.RevokedAt == null)
                .Select(p => new { p.PrincipalKind, p.PrincipalId, p.PrincipalKey, p.Level })
                .ToListAsync(cancellationToken);

            if (grants.Count == 0) return null;

            string? best = null;

            // Direct employee grants first, and short-circuit on Moderate: it is the top level, so no group
            // expansion can improve on it and every expansion is a database walk.
            foreach (var g in grants.Where(g => g.PrincipalKind == CommPrincipalKind.Employee))
            {
                if (g.PrincipalId == employeeId) best = Higher(best, g.Level);
            }
            if (best == CommPermissionLevel.Moderate) return best;

            foreach (var g in grants.Where(g => g.PrincipalKind != CommPrincipalKind.Employee))
            {
                if (CommPermissionLevel.Rank(g.Level) <= CommPermissionLevel.Rank(best)) continue;   // cannot improve
                if (await _principals.CoversAsync(
                        g.PrincipalKind, g.PrincipalId, g.PrincipalKey, context.CompanyId, employeeId, cancellationToken))
                    best = Higher(best, g.Level);
                if (best == CommPermissionLevel.Moderate) break;
            }

            return best;
        }

        private async Task<bool> IsParticipantAsync(long threadId, int? employeeId, CancellationToken cancellationToken)
        {
            if (employeeId is not > 0) return false;
            return await _db.Participants.AsNoTracking()
                .AnyAsync(p => p.ThreadId == threadId && p.EmployeeId == employeeId.Value && p.RemovedAt == null,
                    cancellationToken);
        }

        private static string? Higher(string? a, string? b)
            => CommPermissionLevel.Rank(a) >= CommPermissionLevel.Rank(b) ? a : b;
    }
}
