using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 / ADR-035) — COMMENTS: the platform's centre.
    //
    // Covers modules 1 (Comments Engine), 8/9 (Internal + Public Notes), 10 (Rich Text), 28 (Version History),
    // 29 (Soft Delete), 30 (Edit History), and orchestrates 3/13 (Mentions + their notifications), 4/5
    // (auto-participation) and 31 (Audit).
    //
    // ONE TRANSACTION, SIX TABLES. AddAsync writes CommThreads (counters), CommComments, CommMentions,
    // CommMentionRecipients, CommNotifications + CommNotificationDeliveries, and CommAuditEntries. They share
    // one CommTransaction because a comment with no audit row, or mentions with no comment, is worse than a
    // failed post — it is a lie in an append-only log. The collaborating services (mentions, participation,
    // notifications) therefore ENROL and never save; only CommitAsync persists.
    //
    // WHAT IS DELIBERATELY *NOT* HERE:
    //   * No rendering. The body is stored as authored — see CommBodyPolicy for why storing rendered HTML
    //     freezes a sanitizer's bugs into the data.
    //   * No delivery. Delivery rows are written Pending; sending happens after commit, because an SMTP round
    //     trip inside a database transaction is either a held lock or an email for a comment that rolled back.
    //   * No row deletion, ever. Corrections are a revision row plus a soft-delete marker, both append-only.
    // =============================================================================================
    public interface ICommCommentService
    {
        Task<CommCommentDto> AddAsync(
            BusinessContext context, CommCommentRequest request, CancellationToken cancellationToken = default);

        Task<CommCommentDto> EditAsync(
            BusinessContext context, CommCommentEditRequest request, CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(
            BusinessContext context, long commentId, string? reason = null, CancellationToken cancellationToken = default);

        Task<bool> RestoreAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);

        // A thread's comments, oldest first (a conversation reads downwards), visibility-filtered.
        Task<CommPage<CommCommentDto>> ListAsync(
            BusinessContext context, long threadId, CommPageRequest? page = null, CancellationToken cancellationToken = default);

        Task<CommCommentDto> GetAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);

        // Full edit history (modules 28, 30).
        Task<IReadOnlyList<CommCommentRevisionDto>> GetRevisionsAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);
    }

    public sealed class CommCommentService : ICommCommentService
    {
        private readonly CommDb _db;
        private readonly ICommThreadService _threads;
        private readonly ICommAccessPolicy _access;
        private readonly ICommBodyPolicy _body;
        private readonly ICommMentionService _mentions;
        private readonly ICommParticipationService _participation;
        private readonly ICommNotificationService _notifications;
        private readonly ICommAttachmentService _attachments;
        private readonly ICommEventPublisher _events;
        private readonly ICommActorDirectory _actors;
        private readonly ICommEntitySurface _surface;
        private readonly CommunicationPlatformOptions _options;

        public CommCommentService(
            CrossDbContext db,
            ICommThreadService threads,
            ICommAccessPolicy access,
            ICommBodyPolicy body,
            ICommMentionService mentions,
            ICommParticipationService participation,
            ICommNotificationService notifications,
            ICommAttachmentService attachments,
            ICommEventPublisher events,
            ICommActorDirectory actors,
            ICommEntitySurface surface,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _threads = threads;
            _access = access;
            _body = body;
            _mentions = mentions;
            _participation = participation;
            _notifications = notifications;
            _attachments = attachments;
            _events = events;
            _actors = actors;
            _surface = surface;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommCommentDto> AddAsync(
            BusinessContext context, CommCommentRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            if (context.EmployeeId is not > 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired,
                    "A comment needs a resolved author. An unresolved identity cannot author content.");

            // ---- 0. idempotency, checked BEFORE any work. A retried POST must not re-resolve mentions or
            // re-notify — it must return the original comment.
            if (!string.IsNullOrWhiteSpace(request.DedupKey))
            {
                var existingId = await _db.Comments.AsNoTracking()
                    .Where(c => c.CompanyID == context.CompanyId && c.DedupKey == request.DedupKey)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (existingId != 0) return await GetAsync(context, existingId, cancellationToken);
            }

            // ---- 1. the body. Validated before anything is authorized, because a rejected body is a cheaper
            // answer than a permission evaluation.
            var prepared = _body.Prepare(request.Body, request.BodyFormat);

            // ---- 2. the thread. Supplying both a ThreadId and a (Kind, Key) is refused rather than silently
            // preferring one — a caller who supplied both has a bug, and picking for them hides it.
            if (request.ThreadId.HasValue && (request.ThreadKind != null || request.ThreadKey != null))
                throw new CommValidationException(
                    CommValidationException.Codes.ThreadKindInvalid,
                    "Supply either ThreadId or (ThreadKind, ThreadKey), not both.");

            CommThread thread = request.ThreadId.HasValue
                ? await _threads.RequireAsync(context, request.ThreadId.Value, cancellationToken)
                : await _threads.GetOrCreateAsync(context, new CommThreadRequest
                {
                    Entity = request.Entity,
                    Kind = request.ThreadKind ?? CommThreadKind.Discussion,
                    ThreadKey = request.ThreadKey ?? "",

                    // The thread's ceiling starts at the first comment's visibility. A record whose first note
                    // is Confidential gets a Confidential thread, which is the author's evident intent.
                    Visibility = request.Visibility,
                }, cancellationToken);

            // An explicit ThreadId must still belong to the entity the caller named. Without this check a
            // caller could post to any thread in their company by id while claiming a different anchor, and the
            // comment's denormalised EntityType/EntityId would then disagree with its thread's.
            if (thread.EntityType != request.Entity.EntityCode || thread.EntityId != request.Entity.EntityId)
                throw new CommValidationException(
                    CommValidationException.Codes.EntityRefInvalid,
                    $"Thread {thread.Id} is anchored to {thread.EntityType}#{thread.EntityId}, not to {request.Entity.Key}.");

            // ---- 3a. LOCK FIRST, and the order is deliberate.
            //
            // A locked thread is a STATE, not an authorization failure. CommThreadAccess.CanComment is false for
            // a locked thread — correctly, so a UI does not offer a compose box — but if the authorization check
            // ran first, a lock would surface as 403 "denied" and the caller would be told they lack permission
            // when in fact the conversation is simply closed. The distinct thread_locked code exists so a UI can
            // say the true thing, and it only reaches the caller if it is checked before the access gate.
            if (thread.IsLocked)
                throw new CommValidationException(
                    CommValidationException.Codes.ThreadLocked,
                    "The conversation is locked. A locked thread still reads; it accepts no new comments.");

            // ---- 3b. authorization.
            var threadAccess = await _access.RequireThreadAsync(
                context, thread, CommPermissionLevel.Comment, cancellationToken);

            // ---- 4. visibility. Two ceilings: the caller's own tier and the thread's.
            if (!CommVisibility.IsValid(request.Visibility))
                throw new CommValidationException(
                    CommValidationException.Codes.VisibilityInvalid,
                    $"Visibility '{request.Visibility}' is not one of {string.Join(" | ", CommVisibility.Values)}.");

            // ONE membership test against the intersected set, rather than two rank comparisons in opposite
            // directions. See CommThreadCapabilities.AuthorVisibilities: the bounds are "no more open than the
            // thread" and "readable by this caller", and collapsing them into a single comparison rejected the
            // ordinary case.
            if (!threadAccess.MayAuthorAt(request.Visibility))
                throw new CommValidationException(
                    CommValidationException.Codes.VisibilityInvalid,
                    $"'{request.Visibility}' is not a visibility this caller may post at in this thread " +
                    $"(allowed: {string.Join(", ", threadAccess.AuthorVisibilities)}). A comment may be no more " +
                    "open than its thread — that would break the promise made to whoever opened it at that tier — " +
                    "and no more restricted than the caller can read, which would let them lose sight of their " +
                    "own note.");

            // ---- 5. the reply chain.
            var (parent, depth) = await ResolveParentAsync(context, thread, request.ParentCommentId, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var comment = new CommComment
            {
                CompanyID = context.CompanyId,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                ParentCommentId = parent?.Id,
                Depth = depth,
                Body = prepared.Body,
                BodyFormat = prepared.Format,
                Visibility = request.Visibility,
                AuthorEmployeeId = context.EmployeeId.Value,
                RevisionCount = 0,
                BodyAnalysisJson = System.Text.Json.JsonSerializer.Serialize(prepared.Analysis),
                DedupKey = string.IsNullOrWhiteSpace(request.DedupKey) ? null : request.DedupKey!.Trim(),
                CreatedBy = context.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Comments.Add(comment);

            thread.CommentCount += 1;
            thread.LastActivityAt = comment.CreatedAt;
            thread.UpdatedAt = comment.CreatedAt;
            if (parent != null) parent.ReplyCount += 1;

            // Saved so comment.Id exists for mentions, attachments, notifications and audit. A round trip inside
            // the transaction, not a commit.
            await _db.SaveAsync(cancellationToken);

            // ---- 6. attachments (module 7).
            var attachments = await _attachments.AttachAsync(
                context, thread, comment, request.Attachments, cancellationToken);

            // ---- 7. mentions. Parsed from the body UNIONED with any supplied explicitly by a picker UI.
            var tokens = UnionMentions(prepared.Mentions, request.Mentions);
            var mentionOutcome = await _mentions.RecordAsync(context, thread, comment, tokens, cancellationToken);

            // ---- 8. participation. The author becomes a Participant; every mentioned person becomes a
            // Follower — being named is a stronger signal of interest than having commented, so it earns the
            // higher notification tier (see CommParticipationService.RoleRank).
            await _participation.EnsureAsync(
                context, thread, context.EmployeeId.Value,
                CommParticipantRole.Participant, CommParticipationSource.Author, cancellationToken);

            foreach (var employeeId in mentionOutcome.RecipientEmployeeIds)
                await _participation.EnsureAsync(
                    context, thread, employeeId,
                    CommParticipantRole.Follower, CommParticipationSource.Mention, cancellationToken);

            // ---- 9. the comment event. Published BEFORE notifications so the audit row for the comment
            // precedes the audit rows for what it caused — an investigator reads the log downwards.
            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.CommentAdded,
                Entity = entity,
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new CommCommentEventPayload
                {
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    ParentCommentId = comment.ParentCommentId,
                    ThreadKind = thread.Kind,
                    Visibility = comment.Visibility,
                    BodyFormat = comment.BodyFormat,
                    BodyBytes = prepared.Analysis.Bytes,
                    MentionCount = mentionOutcome.Mentions.Count,
                    AttachmentCount = attachments.Count,
                    RevisionNo = 0,
                },
                DedupKey = $"comment:{comment.Id}:added",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            // ---- 10. notifications. Mention recipients get the mention template; followers get the
            // followed-thread template; a reply's parent author gets the reply template. Mention wins where
            // somebody qualifies twice — the DedupKey root makes that automatic rather than a special case,
            // because the mention notification is queued first and the second attempt finds it already there.
            await QueueMentionNotificationsAsync(context, thread, comment, mentionOutcome, cancellationToken);
            await QueueReplyNotificationAsync(context, thread, comment, parent, mentionOutcome, cancellationToken);
            await QueueFollowerNotificationsAsync(context, thread, comment, mentionOutcome, parent, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return await GetAsync(context, comment.Id, cancellationToken);
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommCommentDto> EditAsync(
            BusinessContext context, CommCommentEditRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            var comment = await RequireCommentAsync(context, request.CommentId, cancellationToken);
            var thread = await _threads.RequireAsync(context, comment.ThreadId, cancellationToken);
            var threadAccess = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);

            var rights = _access.ResolveCommentRights(context, threadAccess, comment);
            if (!rights.CanEdit)
                throw rights.Reason == "author edit window closed"
                    ? new CommValidationException(
                        CommValidationException.Codes.EditWindowClosed,
                        $"The author edit window ({_options.AuthorEditWindowMinutes} minutes) has closed. " +
                        "A later change is a moderator action, so it is attributable to a role.")
                    : new CommAccessDeniedException(
                        "comment-edit", new CommEntityRef(thread.EntityType, thread.EntityId), rights.Reason);

            var prepared = _body.Prepare(request.Body, request.BodyFormat ?? comment.BodyFormat);

            string newVisibility = request.Visibility ?? comment.Visibility;
            if (!CommVisibility.IsValid(newVisibility))
                throw new CommValidationException(
                    CommValidationException.Codes.VisibilityInvalid,
                    $"Visibility '{newVisibility}' is not one of {string.Join(" | ", CommVisibility.Values)}.");
            if (!threadAccess.MayAuthorAt(newVisibility))
                throw new CommValidationException(
                    CommValidationException.Codes.VisibilityInvalid,
                    $"'{newVisibility}' is not a visibility this caller may post at in this thread " +
                    $"(allowed: {string.Join(", ", threadAccess.AuthorVisibilities)}).");

            var changed = new List<string>();
            if (!string.Equals(prepared.Body, comment.Body, StringComparison.Ordinal)) changed.Add("Body");
            if (!string.Equals(prepared.Format, comment.BodyFormat, StringComparison.Ordinal)) changed.Add("BodyFormat");
            if (!string.Equals(newVisibility, comment.Visibility, StringComparison.Ordinal)) changed.Add("Visibility");

            // A no-op edit writes NOTHING: no revision, no event, no audit row. Otherwise an accidental
            // double-submit inflates the revision history with identical entries and makes the real edits
            // harder to find.
            if (changed.Count == 0) return await GetAsync(context, comment.Id, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // THE REVISION HOLDS THE **PREVIOUS** TEXT. Storing the new body instead would duplicate the current
            // comment and leave the original unrecoverable — the opposite of an edit history.
            int revisionNo = comment.RevisionCount + 1;
            _db.Revisions.Add(new CommCommentRevision
            {
                CompanyID = comment.CompanyID,
                CommentId = comment.Id,
                RevisionNo = revisionNo,
                Body = comment.Body,
                BodyFormat = comment.BodyFormat,
                Visibility = comment.Visibility,
                EditedByEmployeeId = context.EmployeeId ?? 0,
                EditedAt = DateTime.UtcNow,
                Reason = request.Reason,
                CreatedBy = context.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            });

            comment.Body = prepared.Body;
            comment.BodyFormat = prepared.Format;
            comment.Visibility = newVisibility;
            comment.BodyAnalysisJson = System.Text.Json.JsonSerializer.Serialize(prepared.Analysis);
            comment.RevisionCount = revisionNo;
            comment.EditedAt = DateTime.UtcNow;
            comment.EditedBy = context.EmployeeId;
            comment.updatedBy = context.EmployeeId;
            comment.UpdatedAt = DateTime.UtcNow;

            thread.LastActivityAt = comment.UpdatedAt;
            thread.UpdatedAt = comment.UpdatedAt;

            // NEW mentions introduced by the edit are recorded and notified; mentions REMOVED by an edit are
            // NOT retracted. Someone who was told they were mentioned was told — deleting the record would make
            // the notification they already received unexplainable. The unique index on (comment, target)
            // absorbs re-mentions of the same target.
            var existingTokens = (await _mentions.ListForCommentAsync(context, comment.Id, cancellationToken))
                .Select(m => new CommMentionToken(m.TargetKind, m.TargetId, m.TargetKey, null).DedupKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newTokens = prepared.Mentions.Where(t => !existingTokens.Contains(t.DedupKey)).ToList();
            var mentionOutcome = newTokens.Count > 0
                ? await _mentions.RecordAsync(context, thread, comment, newTokens, cancellationToken)
                : CommMentionOutcome.Empty();

            // MentionCount is the TOTAL, not just this edit's — RecordAsync sets it to what it wrote, so it is
            // corrected here.
            comment.MentionCount = existingTokens.Count + mentionOutcome.Mentions.Count;

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.CommentEdited,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new CommCommentEventPayload
                {
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    Visibility = comment.Visibility,
                    BodyFormat = comment.BodyFormat,
                    BodyBytes = prepared.Analysis.Bytes,
                    MentionCount = comment.MentionCount,
                    RevisionNo = revisionNo,

                    // Which FIELDS changed, never their values. The old body is content and already lives in
                    // CommCommentRevisions, where the visibility rules apply to it.
                    ChangedFields = changed.ToArray(),
                },
                DedupKey = $"comment:{comment.Id}:edited:{revisionNo}",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await QueueMentionNotificationsAsync(context, thread, comment, mentionOutcome, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return await GetAsync(context, comment.Id, cancellationToken);
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<bool> DeleteAsync(
            BusinessContext context, long commentId, string? reason = null, CancellationToken cancellationToken = default)
        {
            var comment = await RequireCommentAsync(context, commentId, cancellationToken);
            var thread = await _threads.RequireAsync(context, comment.ThreadId, cancellationToken);
            var threadAccess = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);

            var rights = _access.ResolveCommentRights(context, threadAccess, comment);
            if (!rights.CanDelete)
                throw new CommAccessDeniedException(
                    "comment-delete", new CommEntityRef(thread.EntityType, thread.EntityId), rights.Reason);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // SOFT DELETE. The row survives so revisions, mentions and the reply chain beneath it stay intact —
            // deleting a parent must not orphan its replies, and the audit trail already knows it existed.
            comment.DeletedAt = DateTime.UtcNow;
            comment.DeletedBy = context.EmployeeId;
            comment.updatedBy = context.EmployeeId;
            comment.UpdatedAt = DateTime.UtcNow;

            thread.CommentCount = Math.Max(0, thread.CommentCount - 1);
            thread.UpdatedAt = comment.UpdatedAt;

            bool byModerator = comment.AuthorEmployeeId != (context.EmployeeId ?? 0);

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.CommentDeleted,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new CommCommentEventPayload
                {
                    ThreadId = thread.Id, CommentId = comment.Id, Visibility = comment.Visibility,
                    RevisionNo = comment.RevisionCount, ChangedFields = new[] { "DeletedAt" },
                },
                DedupKey = $"comment:{comment.Id}:deleted",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            // The AUTHOR is told when somebody else removes their words. A comment that disappears without
            // explanation reads as censorship; one with an attributable actor and a reason reads as moderation.
            if (byModerator && comment.AuthorEmployeeId > 0)
                await _notifications.QueueAsync(context, new CommNotificationRequest
                {
                    TemplateKey = CommTemplateKeys.MyCommentDeleted,
                    Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    CompanyId = context.CompanyId,
                    BranchId = context.BranchId,
                    ActorEmployeeId = context.EmployeeId,
                    RecipientEmployeeIds = new[] { comment.AuthorEmployeeId },
                    SubjectVisibility = comment.Visibility,
                    Tokens = await TokensAsync(context, thread, actorName: null, extra: (CommTemplateTokens.Reason, reason ?? ""), cancellationToken),
                    DedupKeyPrefix = $"comment:{comment.Id}:deleted",
                }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        }

        public async Task<bool> RestoreAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            // Deleted rows are excluded by RequireCommentAsync's normal path, so restore reads directly.
            var comment = await _db.Comments
                .Where(c => c.Id == commentId && c.CompanyID == context.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) throw new CommNotFoundException("Comment", commentId);
            if (comment.DeletedAt == null) return false;

            var thread = await _threads.RequireAsync(context, comment.ThreadId, cancellationToken);
            var threadAccess = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);

            var rights = _access.ResolveCommentRights(context, threadAccess, comment);
            if (!rights.CanRestore)
                throw new CommAccessDeniedException(
                    "comment-restore", new CommEntityRef(thread.EntityType, thread.EntityId),
                    "only a moderator may restore a deleted comment");

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            comment.DeletedAt = null;
            comment.DeletedBy = null;
            comment.updatedBy = context.EmployeeId;
            comment.UpdatedAt = DateTime.UtcNow;

            thread.CommentCount += 1;
            thread.UpdatedAt = comment.UpdatedAt;

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.CommentRestored,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new CommCommentEventPayload
                {
                    ThreadId = thread.Id, CommentId = comment.Id, Visibility = comment.Visibility,
                    ChangedFields = new[] { "DeletedAt" },
                },

                // Ticks: delete/restore/delete/restore is a legitimate moderation sequence and each occurrence
                // is its own fact.
                DedupKey = $"comment:{comment.Id}:restored:{DateTime.UtcNow.Ticks}",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommPage<CommCommentDto>> ListAsync(
            BusinessContext context, long threadId, CommPageRequest? page = null, CancellationToken cancellationToken = default)
        {
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            var threadAccess = await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);
            var allowed = visibilities.Allowed.ToList();

            int take = _options.ClampPageSize(page?.PageSize);
            long after = page?.AfterId ?? 0;
            bool newestFirst = page?.NewestFirst ?? false;   // a conversation reads downwards

            // Visibility is filtered IN SQL so an unreadable comment never leaves the database. Deleted rows are
            // included only for a moderator — an ordinary reader must not see a gap where a row used to be, and a
            // moderator must.
            var query = _db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == threadId
                            && c.CompanyID == context.CompanyId
                            && allowed.Contains(c.Visibility)
                            && (threadAccess.CanModerate || c.DeletedAt == null));

            query = newestFirst
                ? query.Where(c => after == 0 || c.Id < after).OrderByDescending(c => c.Id)
                : query.Where(c => after == 0 || c.Id > after).OrderBy(c => c.Id);

            var rows = await query.Take(take + 1).ToListAsync(cancellationToken);

            bool hasMore = rows.Count > take;
            if (hasMore) rows = rows.Take(take).ToList();

            // The row-level own-author pass. The SQL filter above deliberately lets Restricted through so the
            // caller's OWN restricted comments are fetched; this drops everybody else's. Collapsing the two
            // would show every viewer every restricted comment — the bug the kernel's timeline documents.
            rows = rows.Where(c => _access.CanReadComment(visibilities, c, context)).ToList();

            var dtos = await ProjectAsync(context, thread, threadAccess, rows, cancellationToken);

            return new CommPage<CommCommentDto>
            {
                Items = dtos,
                NextCursor = hasMore && rows.Count > 0 ? (newestFirst ? rows[^1].Id : rows[^1].Id) : null,
            };
        }

        public async Task<CommCommentDto> GetAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            var comment = await _db.Comments.AsNoTracking()
                .Where(c => c.Id == commentId && c.CompanyID == context.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) throw new CommNotFoundException("Comment", commentId);

            var thread = await _threads.RequireAsync(context, comment.ThreadId, cancellationToken);
            var threadAccess = await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);

            if (!_access.CanReadComment(visibilities, comment, context))
                // 404, not 403: telling the caller a comment exists but is above their tier discloses that
                // somebody wrote something confidential about this record.
                throw new CommNotFoundException("Comment", commentId);

            if (comment.DeletedAt != null && !threadAccess.CanModerate)
                throw new CommNotFoundException("Comment", commentId);

            var dtos = await ProjectAsync(context, thread, threadAccess, new List<CommComment> { comment }, cancellationToken);
            return dtos[0];
        }

        public async Task<IReadOnlyList<CommCommentRevisionDto>> GetRevisionsAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            // GetAsync applies every read gate (thread access, visibility, soft delete). Reaching the revisions
            // without it would expose a previous body of a comment the caller may not read — and a previous body
            // is exactly where the sensitive wording usually was.
            await GetAsync(context, commentId, cancellationToken);

            var rows = await _db.Revisions.AsNoTracking()
                .Where(r => r.CommentId == commentId && r.CompanyID == context.CompanyId)
                .OrderBy(r => r.RevisionNo)
                .ToListAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(rows.Select(r => r.EditedByEmployeeId), cancellationToken);

            return rows.Select(r => new CommCommentRevisionDto
            {
                RevisionId = r.Id,
                CommentId = r.CommentId,
                RevisionNo = r.RevisionNo,
                Body = r.Body,
                BodyFormat = r.BodyFormat,
                EditedBy = _actors.Get(actors, r.EditedByEmployeeId),
                EditedAt = r.EditedAt,
                Reason = r.Reason,
            }).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        private async Task<CommComment> RequireCommentAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken)
        {
            var comment = await _db.Comments
                .Where(c => c.Id == commentId && c.CompanyID == context.CompanyId && c.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) throw new CommNotFoundException("Comment", commentId);
            return comment;
        }

        private async Task<(CommComment? Parent, int Depth)> ResolveParentAsync(
            BusinessContext context, CommThread thread, long? parentCommentId, CancellationToken cancellationToken)
        {
            if (parentCommentId is not > 0) return (null, 1);

            var parent = await _db.Comments
                .Where(c => c.Id == parentCommentId.Value && c.CompanyID == context.CompanyId && c.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (parent == null) throw new CommNotFoundException("Comment", parentCommentId.Value);

            // A reply must stay in its parent's thread. Without this, a reply could be grafted onto a comment in
            // a thread the caller was never authorized for, and the reply's own thread-level checks would then
            // be answering about the wrong thread.
            if (parent.ThreadId != thread.Id)
                throw new CommValidationException(
                    CommValidationException.Codes.ReplyParentMismatch,
                    $"Comment {parent.Id} belongs to thread {parent.ThreadId}, not {thread.Id}.");

            int depth = parent.Depth + 1;
            if (depth > _options.MaxReplyDepth)
                throw new CommValidationException(
                    CommValidationException.Codes.ReplyDepthExceeded,
                    $"Reply depth {depth} exceeds the limit of {_options.MaxReplyDepth}. Depth is bounded because " +
                    "an unbounded chain makes the thread read query unbounded too.");

            return (parent, depth);
        }

        // Body-parsed and explicitly-supplied mentions, unioned and deduped. A picker UI knows the target id
        // without the author having typed a token, and an author may type a token the picker never saw — both
        // are legitimate, so neither wins.
        private static List<CommMentionToken> UnionMentions(
            IReadOnlyList<CommMentionToken> parsed, IReadOnlyList<CommMentionRequest>? explicitMentions)
        {
            var result = new List<CommMentionToken>(parsed);
            var seen = parsed.Select(t => t.DedupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in explicitMentions ?? Array.Empty<CommMentionRequest>())
            {
                var token = new CommMentionToken(m.TargetKind, m.TargetId, m.TargetKey, null);
                if (seen.Add(token.DedupKey)) result.Add(token);
            }
            return result;
        }

        // ---------------------------------------------------------------------------------------------
        private async Task QueueMentionNotificationsAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            CommMentionOutcome outcome, CancellationToken cancellationToken)
        {
            if (outcome.RecipientEmployeeIds.Count == 0) return;

            await _notifications.QueueAsync(context, new CommNotificationRequest
            {
                TemplateKey = CommTemplateKeys.MentionedInComment,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                MentionId = outcome.Mentions.Count == 1 ? outcome.Mentions[0].Id : null,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                RecipientEmployeeIds = outcome.RecipientEmployeeIds,

                // The COMMENT's visibility, not the thread's: the notification quotes the comment, so the
                // comment's tier is what the recipient must be able to read.
                SubjectVisibility = comment.Visibility,
                Tokens = await TokensAsync(context, thread, actorName: null, extra: (CommTemplateTokens.Excerpt, _body.Excerpt(comment.Body, comment.BodyFormat)), cancellationToken),

                // Keyed on the COMMENT, so a retried transaction produces one mention notification per person —
                // and so a follower notification for the same comment (different prefix) does not collide.
                DedupKeyPrefix = $"comment:{comment.Id}:mention",
            }, cancellationToken);
        }

        private async Task QueueReplyNotificationAsync(
            BusinessContext context, CommThread thread, CommComment comment, CommComment? parent,
            CommMentionOutcome outcome, CancellationToken cancellationToken)
        {
            if (parent == null || parent.AuthorEmployeeId <= 0) return;

            // Already mentioned => already told, with a better message. Sending both would be two notifications
            // for one comment.
            if (outcome.RecipientEmployeeIds.Contains(parent.AuthorEmployeeId)) return;

            await _notifications.QueueAsync(context, new CommNotificationRequest
            {
                TemplateKey = CommTemplateKeys.ReplyToMyComment,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                RecipientEmployeeIds = new[] { parent.AuthorEmployeeId },
                SubjectVisibility = comment.Visibility,
                Tokens = await TokensAsync(context, thread, actorName: null, extra: (CommTemplateTokens.Excerpt, _body.Excerpt(comment.Body, comment.BodyFormat)), cancellationToken),
                DedupKeyPrefix = $"comment:{comment.Id}:reply",
            }, cancellationToken);
        }

        private async Task QueueFollowerNotificationsAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            CommMentionOutcome outcome, CommComment? parent, CancellationToken cancellationToken)
        {
            var followers = await _participation.ResolveNotifiableAsync(
                context, thread.Id, context.EmployeeId, cancellationToken);

            // Subtract everybody already told about this comment through a more specific template. A mention and
            // a "new comment on a thread you follow" for the same comment is one notification too many, and the
            // mention is the better message.
            var already = new HashSet<int>(outcome.RecipientEmployeeIds);
            if (parent?.AuthorEmployeeId > 0) already.Add(parent.AuthorEmployeeId);

            var recipients = followers.Where(id => !already.Contains(id)).ToList();
            if (recipients.Count == 0) return;

            await _notifications.QueueAsync(context, new CommNotificationRequest
            {
                TemplateKey = CommTemplateKeys.CommentOnFollowedThread,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CommentId = comment.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                RecipientEmployeeIds = recipients,
                SubjectVisibility = comment.Visibility,
                Tokens = await TokensAsync(context, thread, actorName: null, extra: (CommTemplateTokens.Excerpt, _body.Excerpt(comment.Body, comment.BodyFormat)), cancellationToken),
                DedupKeyPrefix = $"comment:{comment.Id}:follow",
            }, cancellationToken);
        }

        // The token dictionary every template needs. Built once per notification batch, not per recipient — the
        // tokens describe the ACTOR and the RECORD, neither of which varies by who is being told.
        private async Task<IReadOnlyDictionary<string, string>> TokensAsync(
            BusinessContext context, CommThread thread, string? actorName,
            (string Key, string Value) extra, CancellationToken cancellationToken)
        {
            if (actorName == null && context.EmployeeId is > 0)
            {
                var actors = await _actors.ResolveAsync(new[] { context.EmployeeId.Value }, cancellationToken);
                var actor = _actors.Get(actors, context.EmployeeId);
                actorName = actor.Display(arabic: false);
            }

            var entityLabel = await ResolveEntityLabelAsync(thread, cancellationToken);

            var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CommTemplateTokens.ActorName] = actorName ?? "-",
                [CommTemplateTokens.EntityLabel] = entityLabel,
                [CommTemplateTokens.EntityKind] = thread.EntityType,
                [CommTemplateTokens.ThreadSubject] = thread.SubjectEn ?? thread.SubjectAr ?? "",
            };
            if (!string.IsNullOrEmpty(extra.Key)) tokens[extra.Key] = extra.Value ?? "";
            return tokens;
        }

        // The record's own display label, through the registry when it can resolve one.
        //
        // A BusinessContext is required by IEntityRegistry.ResolveAsync, and this platform deliberately does NOT
        // call it here: resolution runs a per-type query against the business table, which is a read of module
        // data from inside a notification path. The entity KEY ("SalesInvoice#42") is enough for a notification
        // title and costs nothing. Recorded in CPS-001 §9 as a deliberate simplification, not an oversight.
        private Task<string> ResolveEntityLabelAsync(CommThread thread, CancellationToken cancellationToken)
        {
            var label = !string.IsNullOrWhiteSpace(thread.SubjectEn) ? thread.SubjectEn!
                : !string.IsNullOrWhiteSpace(thread.SubjectAr) ? thread.SubjectAr!
                : thread.EntityType + "#" + thread.EntityId;
            return Task.FromResult(label);
        }

        // ---------------------------------------------------------------------------------------------
        // Projection. Every collection a comment carries is loaded for the WHOLE PAGE in one query each —
        // mentions, attachments, reactions, actors — so a 50-comment page is a fixed number of round trips
        // rather than 200.
        private async Task<List<CommCommentDto>> ProjectAsync(
            BusinessContext context, CommThread thread, CommThreadAccess threadAccess,
            List<CommComment> rows, CancellationToken cancellationToken)
        {
            if (rows.Count == 0) return new List<CommCommentDto>();

            var ids = rows.Select(r => r.Id).ToList();

            var mentions = await _db.Mentions.AsNoTracking()
                .Where(m => ids.Contains(m.CommentId) && m.CompanyID == context.CompanyId)
                .ToListAsync(cancellationToken);

            var attachments = await _db.Attachments.AsNoTracking()
                .Where(a => ids.Contains(a.CommentId) && a.CompanyID == context.CompanyId && a.DeletedAt == null)
                .ToListAsync(cancellationToken);

            var reactions = await _db.Reactions.AsNoTracking()
                .Where(r => ids.Contains(r.CommentId) && r.CompanyID == context.CompanyId)
                .ToListAsync(cancellationToken);

            var actorIds = rows.Select(r => (int?)r.AuthorEmployeeId)
                .Concat(rows.Select(r => r.EditedBy))
                .Concat(rows.Select(r => r.DeletedBy))
                .Concat(attachments.Select(a => (int?)a.UploadedByEmployeeId))
                .Concat(reactions.Select(r => (int?)r.EmployeeId));
            var actors = await _actors.ResolveAsync(actorIds, cancellationToken);

            int me = context.EmployeeId ?? 0;
            var results = new List<CommCommentDto>(rows.Count);

            foreach (var row in rows)
            {
                var rights = _access.ResolveCommentRights(context, threadAccess, row);
                bool deleted = row.DeletedAt != null;

                results.Add(new CommCommentDto
                {
                    CommentId = row.Id,
                    ThreadId = row.ThreadId,
                    ParentCommentId = row.ParentCommentId,
                    Depth = row.Depth,

                    // A deleted comment's BODY IS BLANKED even for the moderator who can see the row. The row's
                    // existence is what moderation needs; the text is recoverable from CommCommentRevisions,
                    // which is a deliberate, separate, audited read.
                    Body = deleted ? "" : row.Body,
                    BodyFormat = row.BodyFormat,
                    Visibility = row.Visibility,
                    Author = _actors.Get(actors, row.AuthorEmployeeId),
                    CreatedAt = row.CreatedAt,
                    EditedAt = row.EditedAt,
                    RevisionCount = row.RevisionCount,
                    LastEditedBy = row.EditedBy.HasValue ? _actors.Get(actors, row.EditedBy) : null,
                    IsDeleted = deleted,
                    DeletedAt = row.DeletedAt,
                    DeletedBy = row.DeletedBy.HasValue ? _actors.Get(actors, row.DeletedBy) : null,
                    Mentions = mentions.Where(m => m.CommentId == row.Id).Select(CommMentionService.ToDto).ToList(),
                    Attachments = attachments.Where(a => a.CommentId == row.Id)
                        .Select(a => CommAttachmentService.ToDto(a, _actors.Get(actors, a.UploadedByEmployeeId))).ToList(),
                    Reactions = SummariseReactions(reactions.Where(r => r.CommentId == row.Id).ToList(), me, actors),
                    BodyAnalysis = DeserializeAnalysis(row.BodyAnalysisJson),
                    Capabilities = rights.ToCapabilities(),
                });
            }
            return results;
        }

        // Sample size 5: enough for a hover tooltip, and it keeps a 200-reaction comment from returning 200
        // employee names to render one.
        private const int ReactionSampleSize = 5;

        private List<CommReactionSummaryDto> SummariseReactions(
            List<CommReaction> reactions, int me, IReadOnlyDictionary<int, CommActorDto> actors)
            => reactions
                .GroupBy(r => r.ReactionKey, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => new CommReactionSummaryDto
                {
                    ReactionKey = g.Key,
                    Count = g.Count(),
                    Mine = g.Any(r => r.EmployeeId == me),
                    Sample = g.Take(ReactionSampleSize).Select(r => _actors.Get(actors, r.EmployeeId)).ToList(),
                })
                .ToList();

        private static CommBodyAnalysisDto? DeserializeAnalysis(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            // A stored analysis that will not parse is metadata, not content: the comment must still render.
            // Same stance BusinessEventService takes on an unparseable payload — deliver the row without it.
            try { return System.Text.Json.JsonSerializer.Deserialize<CommBodyAnalysisDto>(json); }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }
}
