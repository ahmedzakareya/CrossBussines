using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §4) — REACTIONS (module 6) and READ STATUS (module 14).
    //
    // Two small subsystems in one file because they share a shape: both are per-(row, employee) state, both are
    // high-frequency, and both are deliberately CHEAP — no event bridging, minimal auditing.
    //
    // REACTIONS ARE AUDITED BUT NOT BRIDGED. CommEventTypes.KernelActionFor returns null for Reaction.* on
    // purpose: a reaction is a signal, not a business fact, and thousands a day in the table
    // BusinessEventService itself calls "destined to be the largest table in the database" buys nothing.
    //
    // READ STATUS IS NEITHER AUDITED NOR EVENTED. It is per-user UI state. One append-only row per
    // badge-clear would fill an audit log with nothing an investigation can use.
    // =============================================================================================
    public interface ICommReactionService
    {
        // Idempotent. Adding a reaction the caller already has returns the current summary unchanged — a
        // double-click must not produce two rows, and the unique index makes that a guarantee rather than a hope.
        Task<IReadOnlyList<CommReactionSummaryDto>> AddAsync(
            BusinessContext context, long commentId, string reactionKey, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommReactionSummaryDto>> RemoveAsync(
            BusinessContext context, long commentId, string reactionKey, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommReactionSummaryDto>> ListAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);
    }

    public sealed class CommReactionService : ICommReactionService
    {
        private const int SampleSize = 5;

        private readonly CommDb _db;
        private readonly ICommThreadService _threads;
        private readonly ICommAccessPolicy _access;
        private readonly ICommEventPublisher _events;
        private readonly ICommActorDirectory _actors;
        private readonly ICommNotificationService _notifications;

        public CommReactionService(
            CrossDbContext db,
            ICommThreadService threads,
            ICommAccessPolicy access,
            ICommEventPublisher events,
            ICommActorDirectory actors,
            ICommNotificationService notifications)
        {
            _db = new CommDb(db);
            _threads = threads;
            _access = access;
            _events = events;
            _actors = actors;
            _notifications = notifications;
        }

        public async Task<IReadOnlyList<CommReactionSummaryDto>> AddAsync(
            BusinessContext context, long commentId, string reactionKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!CommReactionKeys.IsValid(reactionKey))
                throw new CommValidationException(
                    CommValidationException.Codes.ReactionKeyInvalid,
                    $"Reaction '{reactionKey}' is not one of {string.Join(" | ", CommReactionKeys.Values)}. " +
                    "The key set is frozen so reactions stay enumerable for reporting — see CommReactionKeys.");

            if (context.EmployeeId is not > 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired, "Reacting requires a resolved employee.");

            var (comment, thread, rights) = await LoadAsync(context, commentId, cancellationToken);
            if (!rights.CanReact)
                throw new CommAccessDeniedException(
                    "reaction-add", new CommEntityRef(thread.EntityType, thread.EntityId), rights.Reason);

            var existing = await _db.Reactions
                .Where(r => r.CommentId == commentId
                            && r.EmployeeId == context.EmployeeId.Value
                            && r.ReactionKey == reactionKey)
                .FirstOrDefaultAsync(cancellationToken);

            // Idempotent: already reacted, nothing to do, no second event.
            if (existing != null) return await ListAsync(context, commentId, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var row = new CommReaction
            {
                CompanyID = context.CompanyId,
                CommentId = commentId,
                ThreadId = comment.ThreadId,
                EmployeeId = context.EmployeeId.Value,
                ReactionKey = reactionKey,
                CreatedBy = context.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Reactions.Add(row);

            var tracked = await _db.Comments.FirstAsync(c => c.Id == commentId, cancellationToken);
            tracked.ReactionCount += 1;

            thread.LastActivityAt = DateTime.UtcNow;
            thread.UpdatedAt = thread.LastActivityAt;

            await _db.SaveAsync(cancellationToken);

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.ReactionAdded,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = comment.ThreadId,
                CommentId = commentId,
                CompanyId = context.CompanyId,
                BranchId = thread.BranchID,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new { commentId, reactionKey, employeeId = context.EmployeeId },
                DedupKey = $"reaction:{row.Id}:added",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            // The comment's author is told — unless they reacted to themselves, which the notification service
            // filters as actor_self.
            if (comment.AuthorEmployeeId > 0)
                await _notifications.QueueAsync(context, new CommNotificationRequest
                {
                    TemplateKey = CommTemplateKeys.ReactionOnMyComment,
                    Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                    ThreadId = comment.ThreadId,
                    CommentId = commentId,
                    CompanyId = context.CompanyId,
                    BranchId = thread.BranchID,
                    ActorEmployeeId = context.EmployeeId,
                    RecipientEmployeeIds = new[] { comment.AuthorEmployeeId },
                    SubjectVisibility = comment.Visibility,
                    Tokens = await ReactionTokensAsync(context, thread, reactionKey, cancellationToken),

                    // Keyed on the REACTION ROW, so reacting, un-reacting and re-reacting notifies once per
                    // distinct reaction rather than deduplicating against the first forever.
                    DedupKeyPrefix = $"reaction:{row.Id}",
                }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return await ListAsync(context, commentId, cancellationToken);
        }

        public async Task<IReadOnlyList<CommReactionSummaryDto>> RemoveAsync(
            BusinessContext context, long commentId, string reactionKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return await ListAsync(context, commentId, cancellationToken);

            var (comment, thread, _) = await LoadAsync(context, commentId, cancellationToken);

            var row = await _db.Reactions
                .Where(r => r.CommentId == commentId
                            && r.CompanyID == context.CompanyId
                            && r.EmployeeId == context.EmployeeId.Value
                            && r.ReactionKey == reactionKey)
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return await ListAsync(context, commentId, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // THE ONE HARD DELETE IN THIS PLATFORM, and it is deliberate. A withdrawn reaction is not history
            // anybody needs: it has no author's words in it, no decision rests on it, and a soft-deleted
            // reaction would have to be excluded from every count forever. The AUDIT row records that it
            // happened, which is the part that matters.
            _db.Reactions.Remove(row);

            var tracked = await _db.Comments.FirstAsync(c => c.Id == commentId, cancellationToken);
            tracked.ReactionCount = Math.Max(0, tracked.ReactionCount - 1);

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.ReactionRemoved,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = comment.ThreadId,
                CommentId = commentId,
                CompanyId = context.CompanyId,
                BranchId = thread.BranchID,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new { commentId, reactionKey, employeeId = context.EmployeeId },
                DedupKey = $"reaction:{row.Id}:removed",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return await ListAsync(context, commentId, cancellationToken);
        }

        public async Task<IReadOnlyList<CommReactionSummaryDto>> ListAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var rows = await _db.Reactions.AsNoTracking()
                .Where(r => r.CommentId == commentId && r.CompanyID == context.CompanyId)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0) return Array.Empty<CommReactionSummaryDto>();

            var actors = await _actors.ResolveAsync(rows.Select(r => r.EmployeeId), cancellationToken);
            int me = context.EmployeeId ?? 0;

            return rows
                .GroupBy(r => r.ReactionKey, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => new CommReactionSummaryDto
                {
                    ReactionKey = g.Key,
                    Count = g.Count(),
                    Mine = g.Any(r => r.EmployeeId == me),

                    // Capped: a 200-reaction comment must not return 200 names to render a tooltip.
                    Sample = g.Take(SampleSize).Select(r => _actors.Get(actors, r.EmployeeId)).ToList(),
                })
                .ToList();
        }

        // ---------------------------------------------------------------------------------------------
        private async Task<(CommComment Comment, CommThread Thread, CommCommentRights Rights)> LoadAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken)
        {
            var comment = await _db.Comments.AsNoTracking()
                .Where(c => c.Id == commentId && c.CompanyID == context.CompanyId && c.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) throw new CommNotFoundException("Comment", commentId);

            var thread = await _threads.RequireAsync(context, comment.ThreadId, cancellationToken);
            var threadAccess = await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            var visibilities = await _access.ResolveVisibilitiesAsync(
                context, new CommEntityRef(thread.EntityType, thread.EntityId), cancellationToken);

            // Reacting to a comment the caller cannot read must be impossible, and it answers 404 rather than
            // 403 so the comment's existence is not disclosed.
            if (!_access.CanReadComment(visibilities, comment, context))
                throw new CommNotFoundException("Comment", commentId);

            return (comment, thread, _access.ResolveCommentRights(context, threadAccess, comment));
        }

        private async Task<IReadOnlyDictionary<string, string>> ReactionTokensAsync(
            BusinessContext context, CommThread thread, string reactionKey, CancellationToken cancellationToken)
        {
            var actors = await _actors.ResolveAsync(new[] { context.EmployeeId ?? 0 }, cancellationToken);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CommTemplateTokens.ActorName] = _actors.Get(actors, context.EmployeeId).Display(arabic: false),
                [CommTemplateTokens.EntityLabel] = thread.EntityType + "#" + thread.EntityId,
                [CommTemplateTokens.EntityKind] = thread.EntityType,
                [CommTemplateTokens.ReactionKey] = reactionKey,
            };
        }
    }

    // =============================================================================================
    // READ STATUS (module 14)
    // =============================================================================================
    public interface ICommReadStatusService
    {
        // Marks the thread read up to `upToCommentId`, or to its newest comment when null.
        Task<CommReadStatusDto> MarkReadAsync(
            BusinessContext context, long threadId, long? upToCommentId = null, CancellationToken cancellationToken = default);

        Task<CommReadStatusDto> GetAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);
    }

    public sealed class CommReadStatusService : ICommReadStatusService
    {
        private readonly CommDb _db;
        private readonly ICommThreadService _threads;
        private readonly ICommAccessPolicy _access;

        public CommReadStatusService(CrossDbContext db, ICommThreadService threads, ICommAccessPolicy access)
        {
            _db = new CommDb(db);
            _threads = threads;
            _access = access;
        }

        public async Task<CommReadStatusDto> MarkReadAsync(
            BusinessContext context, long threadId, long? upToCommentId = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired, "Read status needs a resolved employee.");

            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            long watermark = upToCommentId ?? await _db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == threadId && c.CompanyID == context.CompanyId)
                .Select(c => c.Id)
                .OrderByDescending(id => id)
                .FirstOrDefaultAsync(cancellationToken);

            var receipt = await _db.ReadReceipts
                .Where(r => r.ThreadId == threadId && r.EmployeeId == context.EmployeeId.Value)
                .FirstOrDefaultAsync(cancellationToken);

            if (receipt == null)
            {
                receipt = new CommReadReceipt
                {
                    CompanyID = context.CompanyId,
                    ThreadId = threadId,
                    EmployeeId = context.EmployeeId.Value,
                    LastReadCommentId = watermark,
                    LastReadAt = DateTime.UtcNow,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.ReadReceipts.Add(receipt);
            }
            else
            {
                // THE WATERMARK ONLY MOVES FORWARD. A client that reports an older comment id — an out-of-order
                // request, a stale page — must not resurrect notifications the user has already dismissed.
                if (watermark > (receipt.LastReadCommentId ?? 0)) receipt.LastReadCommentId = watermark;
                receipt.LastReadAt = DateTime.UtcNow;
                receipt.updatedBy = context.EmployeeId;
                receipt.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveAsync(cancellationToken);
            return await GetAsync(context, threadId, cancellationToken);
        }

        public async Task<CommReadStatusDto> GetAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            int me = context.EmployeeId ?? 0;

            var receipt = await _db.ReadReceipts.AsNoTracking()
                .Where(r => r.ThreadId == threadId && r.EmployeeId == me)
                .FirstOrDefaultAsync(cancellationToken);

            long watermark = receipt?.LastReadCommentId ?? 0;

            var visibilities = await _access.ResolveVisibilitiesAsync(
                context, new CommEntityRef(thread.EntityType, thread.EntityId), cancellationToken);
            var allowed = visibilities.Allowed.ToList();

            // UNREAD IS COUNTED AGAINST WHAT THE CALLER MAY READ, and excludes their own comments.
            //
            // Counting raw rows would show a Confidential comment they cannot see as an unread they can never
            // clear — a badge that never goes away is worse than no badge. Their own comments are excluded
            // because writing something is not a reason to be told about it.
            var candidates = await _db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == threadId
                            && c.CompanyID == context.CompanyId
                            && c.DeletedAt == null
                            && c.Id > watermark
                            && c.AuthorEmployeeId != me
                            && allowed.Contains(c.Visibility))
                .Select(c => new { c.Id, c.Visibility, c.AuthorEmployeeId })
                .ToListAsync(cancellationToken);

            // The row-level own-author pass for Restricted, same as the comment list.
            int unread = candidates.Count(c =>
                c.Visibility != CommVisibility.Restricted
                || visibilities.MayReadAnyRestricted
                || c.AuthorEmployeeId == me);

            int unreadMentions = await _db.MentionRecipients.AsNoTracking()
                .CountAsync(r => r.ThreadId == threadId
                                 && r.CompanyID == context.CompanyId
                                 && r.EmployeeId == me
                                 && r.ReadAt == null,
                    cancellationToken);

            return new CommReadStatusDto
            {
                ThreadId = threadId,
                EmployeeId = me,
                LastReadCommentId = receipt?.LastReadCommentId,
                LastReadAt = receipt?.LastReadAt,
                UnreadCount = unread,
                UnreadMentionCount = unreadMentions,
            };
        }
    }
}
