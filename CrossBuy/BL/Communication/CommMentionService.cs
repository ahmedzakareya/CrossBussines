using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-032) — MENTIONS (modules 3, 13, 15).
    //
    // THE REQUIREMENT THIS CLASS EXISTS TO SATISFY, verbatim:
    //
    //     "Every Mention must create: Mention Entity / Notification / Timeline Entry / Audit Entry"
    //
    // All four, or none. They are written in ONE transaction owned by the caller (CommCommentService), which is
    // why RecordAsync here takes no transaction of its own and does not save:
    //
    //   Mention entity  -> CommMention + CommMentionRecipient rows          (this class)
    //   Audit entry     -> CommAuditEntries, via ICommEventPublisher        (this class)
    //   Timeline entry  -> the same audit/event row, surfaced by            (CommTimelineAggregator, source
    //                      CommTimelineSourceKinds.Mention                   = Mention)
    //   Notification    -> CommNotification + delivery rows                 (ICommNotificationService)
    //
    // Note what "Timeline Entry" does NOT mean here: a fifth table. A timeline is a READ over the facts that
    // already exist. Materialising a separate timeline row would be a second copy of the same truth, free to
    // disagree with it — which is exactly the failure the kernel avoided by making ITimelineProjectionService
    // return "a PROJECTION, not events".
    //
    // A MENTION IS NOT A GRANT. Mentioning somebody in a Confidential note does not let them read it. The
    // notification path drops a recipient who may not read the subject (CommNotificationService), and the
    // mention row still exists — so the audit records that the author tried, which is the honest outcome. A
    // mention that silently granted access would make @-typing a privilege-escalation primitive.
    // =============================================================================================
    public interface ICommMentionService
    {
        // Records every mention on a comment: rows + resolution + events. ENROLS in the caller's transaction
        // and DOES NOT SAVE — see the header for why all four artefacts must share one fate.
        Task<CommMentionOutcome> RecordAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            IReadOnlyList<CommMentionToken> tokens, CancellationToken cancellationToken = default);

        // "Where have I been mentioned" (module 15), including via a team or department.
        Task<CommPage<CommMentionHistoryItemDto>> GetHistoryAsync(
            BusinessContext context, CommPageRequest? page = null, CancellationToken cancellationToken = default);

        Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default);

        // Marks the caller's mention rows read. Returns how many changed.
        Task<int> MarkReadAsync(
            BusinessContext context, IReadOnlyList<long>? mentionIds = null, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommMentionDto>> ListForCommentAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);
    }

    public sealed class CommMentionOutcome
    {
        public required IReadOnlyList<CommMention> Mentions { get; init; }

        // Every DISTINCT employee reached, across all mentions on the comment. This is the notification
        // audience; the caller hands it to ICommNotificationService.
        public required IReadOnlyList<int> RecipientEmployeeIds { get; init; }

        // Tokens that resolved to nobody, with the reason. Returned rather than swallowed: "I mentioned the
        // department and nobody was told" must be answerable, and an unresolved mention is a normal condition
        // (an empty department, a switched-off kind), not an error that should fail the comment.
        public required IReadOnlyList<string> UnresolvedNotes { get; init; }

        public static CommMentionOutcome Empty() => new()
        {
            Mentions = Array.Empty<CommMention>(),
            RecipientEmployeeIds = Array.Empty<int>(),
            UnresolvedNotes = Array.Empty<string>(),
        };
    }

    public sealed class CommMentionService : ICommMentionService
    {
        private readonly CommDb _db;
        private readonly ICommPrincipalResolver _principals;
        private readonly ICommEventPublisher _events;
        private readonly ICommActorDirectory _actors;
        private readonly ICommBodyPolicy _body;
        private readonly CommunicationPlatformOptions _options;

        public CommMentionService(
            CrossDbContext db,
            ICommPrincipalResolver principals,
            ICommEventPublisher events,
            ICommActorDirectory actors,
            ICommBodyPolicy body,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _principals = principals;
            _events = events;
            _actors = actors;
            _body = body;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommMentionOutcome> RecordAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            IReadOnlyList<CommMentionToken> tokens, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(thread);
            ArgumentNullException.ThrowIfNull(comment);

            if (tokens == null || tokens.Count == 0) return CommMentionOutcome.Empty();

            if (tokens.Count > _options.MaxMentionsPerComment)
                throw new CommValidationException(
                    CommValidationException.Codes.TooManyMentions,
                    $"{tokens.Count} mentions exceeds the limit of {_options.MaxMentionsPerComment}.");

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            var mentions = new List<CommMention>();
            var allRecipients = new HashSet<int>();
            var notes = new List<string>();

            foreach (var token in tokens.DistinctBy(t => t.DedupKey))
            {
                if (!CommMentionTargetKind.IsValid(token.TargetKind))
                {
                    notes.Add($"{token.DedupKey}: unknown target kind");
                    continue;
                }

                var expansion = await _principals.ExpandAsync(
                    token.TargetKind, token.TargetId, token.TargetKey, context.CompanyId, cancellationToken);

                if (!expansion.Supported)
                {
                    // An unsupported kind (@role today) is RECORDED as a note and the mention row is NOT
                    // written. Writing a row that resolves to nobody would show the author a mention chip
                    // implying somebody was told.
                    notes.Add($"{token.DedupKey}: {expansion.Reason}");
                    continue;
                }

                // REFUSE rather than truncate. Truncation is the worse failure: the author believes the whole
                // department was notified and half of it was not, and nothing in the UI can show them otherwise.
                if (expansion.EmployeeIds.Count > _options.MaxGroupMentionRecipients)
                    throw new CommValidationException(
                        CommValidationException.Codes.GroupMentionTooLarge,
                        $"Mentioning {token.TargetKind}:{token.TargetId} would reach {expansion.EmployeeIds.Count} " +
                        $"people, over the limit of {_options.MaxGroupMentionRecipients}. Narrow the mention — " +
                        "a silently truncated group mention tells the author the wrong thing.");

                var mention = new CommMention
                {
                    CompanyID = thread.CompanyID,
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    EntityType = thread.EntityType,
                    EntityId = thread.EntityId,
                    TargetKind = token.TargetKind,
                    TargetId = token.TargetId,
                    TargetKey = token.TargetKey,

                    // The label as resolved NOW, then frozen. A department renamed next month was not the
                    // department that was mentioned. Author-supplied label loses to the resolved one: the author
                    // could otherwise write "@[CEO](employee:12)" over somebody else's name.
                    LabelAr = expansion.LabelAr ?? token.Label,
                    LabelEn = expansion.LabelEn ?? token.Label,

                    ResolvedRecipientCount = expansion.EmployeeIds.Count,
                    MentionedByEmployeeId = context.EmployeeId ?? 0,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Mentions.Add(mention);

                // Saved per mention so mention.Id is available for its recipient rows and its event. One extra
                // round trip per mention, bounded by MaxMentionsPerComment, inside the caller's transaction.
                await _db.SaveAsync(cancellationToken);

                foreach (var employeeId in expansion.EmployeeIds.Distinct())
                {
                    // The author is not a recipient of their own mention: "@myself" in a note is a bookmark, and
                    // a notification for it is noise. The MENTION row still records it.
                    if (_options.ExcludeActorFromOwnNotifications && employeeId == (context.EmployeeId ?? 0)) continue;

                    _db.MentionRecipients.Add(new CommMentionRecipient
                    {
                        CompanyID = thread.CompanyID,
                        MentionId = mention.Id,
                        CommentId = comment.Id,
                        ThreadId = thread.Id,
                        EmployeeId = employeeId,
                        ViaKind = token.TargetKind,
                        CreatedBy = context.EmployeeId,
                        CreatedAt = DateTime.UtcNow,
                    });
                    allRecipients.Add(employeeId);
                }

                if (expansion.EmployeeIds.Count == 0)
                    notes.Add($"{token.DedupKey}: resolved to nobody ({expansion.Reason ?? "empty group"})");

                mentions.Add(mention);

                // ARTEFACT 2 + 3: the audit entry, which is also what the timeline's Mention source reads.
                await _events.PublishAsync(new CommEvent
                {
                    EventType = CommEventTypes.MentionCreated,
                    Entity = entity,
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    MentionId = mention.Id,
                    CompanyId = thread.CompanyID,
                    BranchId = thread.BranchID,
                    ActorEmployeeId = context.EmployeeId,
                    Visibility = comment.Visibility,
                    Payload = new CommMentionEventPayload
                    {
                        ThreadId = thread.Id,
                        CommentId = comment.Id,
                        MentionId = mention.Id,
                        TargetKind = mention.TargetKind,
                        TargetId = mention.TargetId,
                        TargetKey = mention.TargetKey,
                        ResolvedRecipientCount = mention.ResolvedRecipientCount,
                    },
                    DedupKey = $"mention:{mention.Id}:created",
                    CorrelationId = context.CorrelationId,
                }, cancellationToken);
            }

            comment.MentionCount = mentions.Count;

            return new CommMentionOutcome
            {
                Mentions = mentions,
                RecipientEmployeeIds = allRecipients.ToList(),
                UnresolvedNotes = notes,
            };
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommPage<CommMentionHistoryItemDto>> GetHistoryAsync(
            BusinessContext context, CommPageRequest? page = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return CommPage<CommMentionHistoryItemDto>.Empty();

            int take = _options.ClampPageSize(page?.PageSize);
            long after = page?.AfterId ?? 0;

            // Cursor paging on the recipient row id: stable under insertion, unlike an offset. Take + 1 so
            // "is there more" is answered without a second COUNT query.
            var rows = await _db.MentionRecipients.AsNoTracking()
                .Where(r => r.CompanyID == context.CompanyId
                            && r.EmployeeId == context.EmployeeId.Value
                            && (after == 0 || r.Id < after))
                .OrderByDescending(r => r.Id)
                .Take(take + 1)
                .ToListAsync(cancellationToken);

            bool hasMore = rows.Count > take;
            if (hasMore) rows = rows.Take(take).ToList();
            if (rows.Count == 0) return CommPage<CommMentionHistoryItemDto>.Empty();

            var mentionIds = rows.Select(r => r.MentionId).Distinct().ToList();
            var commentIds = rows.Select(r => r.CommentId).Distinct().ToList();

            var mentions = await _db.Mentions.AsNoTracking()
                .Where(m => mentionIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, cancellationToken);

            var comments = await _db.Comments.AsNoTracking()
                .Where(c => commentIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Body, c.BodyFormat, c.DeletedAt, c.Visibility })
                .ToDictionaryAsync(c => c.Id, cancellationToken);

            var actors = await _actors.ResolveAsync(mentions.Values.Select(m => m.MentionedByEmployeeId), cancellationToken);

            var items = new List<CommMentionHistoryItemDto>(rows.Count);
            foreach (var row in rows)
            {
                if (!mentions.TryGetValue(row.MentionId, out var mention)) continue;

                comments.TryGetValue(row.CommentId, out var comment);

                // AN EXCERPT, NOT THE BODY, and only for a live comment. This list is the caller's own mention
                // history — they were an intended recipient, so a lead-in is theirs to see — but a deleted
                // comment shows nothing, and full access is re-checked when they open the thread. That
                // re-check is what handles a permission revoked after the mention was created.
                string? excerpt = comment != null && comment.DeletedAt == null
                    ? _body.Excerpt(comment.Body, comment.BodyFormat, 160)
                    : null;

                items.Add(new CommMentionHistoryItemDto
                {
                    MentionId = mention.Id,
                    CommentId = row.CommentId,
                    ThreadId = row.ThreadId,
                    Entity = new CommEntityRef(mention.EntityType, mention.EntityId),
                    TargetKind = mention.TargetKind,
                    ViaKind = row.ViaKind,
                    MentionedBy = _actors.Get(actors, mention.MentionedByEmployeeId),
                    Excerpt = excerpt,
                    CreatedAt = row.CreatedAt,
                    ReadAt = row.ReadAt,
                });
            }

            return new CommPage<CommMentionHistoryItemDto>
            {
                Items = items,
                NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id : null,
            };
        }

        public async Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return 0;

            return await _db.MentionRecipients.AsNoTracking()
                .CountAsync(r => r.CompanyID == context.CompanyId
                                 && r.EmployeeId == context.EmployeeId.Value
                                 && r.ReadAt == null,
                    cancellationToken);
        }

        public async Task<int> MarkReadAsync(
            BusinessContext context, IReadOnlyList<long>? mentionIds = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return 0;

            var query = _db.MentionRecipients
                .Where(r => r.CompanyID == context.CompanyId
                            && r.EmployeeId == context.EmployeeId.Value
                            && r.ReadAt == null);

            // The employee filter above is what makes this safe: a caller passing somebody else's mention ids
            // changes nothing, because the predicate is anchored on their own id first.
            if (mentionIds is { Count: > 0 })
            {
                var ids = mentionIds.ToList();
                query = query.Where(r => ids.Contains(r.MentionId));
            }

            var rows = await query.ToListAsync(cancellationToken);
            if (rows.Count == 0) return 0;

            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                row.ReadAt = now;
                row.updatedBy = context.EmployeeId;
                row.UpdatedAt = now;
            }

            // Read state is not an audited fact and produces no event: it is per-user UI state, and auditing it
            // would put one row per badge-clear into an append-only log for no investigative value.
            await _db.SaveAsync(cancellationToken);
            return rows.Count;
        }

        public async Task<IReadOnlyList<CommMentionDto>> ListForCommentAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var rows = await _db.Mentions.AsNoTracking()
                .Where(m => m.CommentId == commentId && m.CompanyID == context.CompanyId)
                .OrderBy(m => m.Id)
                .ToListAsync(cancellationToken);

            return rows.Select(ToDto).ToList();
        }

        internal static CommMentionDto ToDto(CommMention m) => new()
        {
            MentionId = m.Id,
            CommentId = m.CommentId,
            TargetKind = m.TargetKind,
            TargetId = m.TargetId,
            TargetKey = m.TargetKey,
            LabelAr = m.LabelAr,
            LabelEn = m.LabelEn,
            ResolvedRecipientCount = m.ResolvedRecipientCount,
            CreatedAt = m.CreatedAt,
        };
    }
}
