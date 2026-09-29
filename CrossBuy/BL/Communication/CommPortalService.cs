using CrossBuy.BL.Portal;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // THE EXTERNAL CONVERSATION SERVICE — Wave 3B-C.
    //
    // ONE BACKBONE, TWO AUTHORITIES. Every row this service reads and writes lives in the existing
    // Communication tables: CommThreads, CommParticipants, CommComments, CommReadReceipts,
    // CommCommentAttachments. There is no PortalMessages, no PortalThreads, no ClientMessages. What
    // differs is the question asked before the row is touched — CommExternalAccessPolicy rather than
    // CommAccessPolicy — because an external customer holds no module permission and never will.
    //
    // WHY THIS IS NOT A BRANCH INSIDE CommCommentService. That service's write path opens with
    // _access.RequireThreadAsync, which routes to IPlatformPermissionProvider and dies at the module
    // gate for any context without an employee. Teaching it to sometimes skip that call would put an
    // external bypass inside the internal path, one `if` away from applying to everybody. The
    // internal services are not modified by this wave at all.
    //
    // EVERY PUBLIC METHOD RE-PROVES AUTHORITY. Not one of them trusts that a previous call in the
    // same session already did: "it appeared in the list" is not authorization, and a thread id typed
    // into a URL must be refused by the same five conditions the list applied. That costs one indexed
    // read per call and buys the property that there is no ordering of calls that grants anything.
    //
    // NOTHING HERE TAKES AN IDENTITY FROM THE CALLER. The only identity parameter is PortalContext,
    // which IPortalContextAccessor builds from the authenticated cookie and the PortalUsers row.
    // There is no companyId, customerId, portalUserId or employeeId argument anywhere in this file,
    // so there is no value for a request to forge — the authority is not validated, it is absent.
    // =============================================================================================

    /// One conversation as an external customer sees it. Deliberately thin: no participant list, no
    /// internal counters, no moderation flags, no StorageKey, and no employee identity of any kind.
    public sealed class PortalConversationDto
    {
        public required long ThreadId { get; init; }

        /// The anchor, as a CODE and an id the portal already knows. It is echoed rather than
        /// concealed because the customer reached this conversation through their own quotation;
        /// what it must never be is an INPUT that grants access, and it is not one.
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }

        public string? SubjectAr { get; init; }
        public string? SubjectEn { get; init; }

        public DateTime? LastActivityAt { get; init; }

        /// Unread as the EXTERNAL reader sees it: comments they may actually read, above their own
        /// watermark. Computed against the portal-visible set, never the thread's total, so a customer
        /// is never shown an unread count they can never clear.
        public required int UnreadCount { get; init; }

        /// Whether this caller may post. False while the thread is locked; it is a rendered decision,
        /// not a hint — the server refuses regardless of what the page does with it.
        public required bool CanComment { get; init; }
    }

    /// One comment as an external customer sees it. No author employee id, ever.
    public sealed class PortalCommentDto
    {
        public required long CommentId { get; init; }
        public required string Body { get; init; }
        public required string BodyFormat { get; init; }
        public required DateTime? CreatedAt { get; init; }

        /// TRUE when this comment was written by the portal side of the conversation. The external
        /// reader is told "you" or "the company" and nothing more: an employee's name, id or role is
        /// internal information, and a conversation does not need it to be readable.
        public required bool IsMine { get; init; }

        public required int AttachmentCount { get; init; }
    }

    /// Attachment METADATA. StorageKey is projected away and never leaves this service — a key names
    /// bytes, and naming bytes to an external caller is handing out a URL that answers to nobody.
    public sealed class PortalAttachmentDto
    {
        public required long AttachmentId { get; init; }
        public required long CommentId { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required long SizeBytes { get; init; }
        public required string PreviewKind { get; init; }
    }

    public interface ICommPortalService
    {
        Task<IReadOnlyList<PortalConversationDto>> ListConversationsAsync(
            PortalContext portal, CommPageRequest? page = null, CancellationToken cancellationToken = default);

        /// Null when this caller may not open it — absent, another company's, another customer's, not
        /// theirs to participate in, internal, and deleted all answer IDENTICALLY, so a thread id
        /// cannot be walked to discover which conversations exist.
        Task<PortalConversationDto?> GetConversationAsync(
            PortalContext portal, long threadId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PortalCommentDto>> ListCommentsAsync(
            PortalContext portal, long threadId, CommPageRequest? page = null, CancellationToken cancellationToken = default);

        Task<PortalCommentDto?> PostCommentAsync(
            PortalContext portal, long threadId, string? body, CancellationToken cancellationToken = default);

        Task<bool> MarkReadAsync(
            PortalContext portal, long threadId, long? upToCommentId = null, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PortalAttachmentDto>> ListAttachmentsAsync(
            PortalContext portal, long threadId, CancellationToken cancellationToken = default);
    }

    public sealed class CommPortalService : ICommPortalService
    {
        private readonly CommDb _db;
        private readonly ICommExternalAccessPolicy _external;
        private readonly ICommBodyPolicy _body;
        private readonly CommunicationPlatformOptions _options;

        public CommPortalService(
            CrossDbContext db,
            ICommExternalAccessPolicy external,
            ICommBodyPolicy body,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _external = external;
            _body = body;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        // LIST
        //
        // THE INTERSECTION IS ONE SERVER-SIDE QUERY, and the ordering of its joins is the security
        // property: participation is the DRIVING table, so the set starts from "threads this portal
        // user was explicitly added to" and is narrowed from there. Starting from threads and filtering
        // afterwards would mean the database briefly considered every thread in the company.
        //
        // NO LOAD-ALL-THEN-FILTER, and no per-row work: company, participation, visibility and the
        // customer anchor are all predicates in the same statement, the page is bounded by the
        // platform's own clamp, and the unread counts for the whole page are read in ONE grouped query
        // rather than one per row.
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<PortalConversationDto>> ListConversationsAsync(
            PortalContext portal, CommPageRequest? page = null, CancellationToken cancellationToken = default)
        {
            if (!IsResolved(portal)) return Array.Empty<PortalConversationDto>();

            int take = _options.ClampPageSize(page?.PageSize);
            var anchorCodes = _external.ExternallyAnchorableEntityCodes.ToList();

            var candidates = await (
                from p in _db.Participants.AsNoTracking()
                where p.CompanyID == portal.CompanyId
                      && p.PortalUserId == portal.PortalUserId
                      && p.RemovedAt == null
                join t in _db.Threads.AsNoTracking() on p.ThreadId equals t.Id
                where t.CompanyID == portal.CompanyId
                      && t.DeletedAt == null
                      && t.Visibility == CommVisibility.Public
                      && anchorCodes.Contains(t.EntityType)
                orderby t.LastActivityAt descending, t.Id descending
                select new
                {
                    t.Id, t.EntityType, t.EntityId, t.SubjectAr, t.SubjectEn,
                    t.LastActivityAt, t.IsLocked,
                })
                .Take(take)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return Array.Empty<PortalConversationDto>();

            // THE CUSTOMER ANCHOR, for the whole page in ONE read per anchor type rather than per row.
            // A thread whose anchor is not this customer's is dropped here; it never reaches the DTO.
            var mine = await OwnedAnchorIdsAsync(
                portal, candidates.Select(c => c.EntityId).Distinct().ToList(), cancellationToken);

            var visible = candidates.Where(c => mine.Contains(c.EntityId)).ToList();
            if (visible.Count == 0) return Array.Empty<PortalConversationDto>();

            var threadIds = visible.Select(c => c.Id).ToList();
            var unread = await UnreadByThreadAsync(portal, threadIds, cancellationToken);

            return visible.Select(c => new PortalConversationDto
            {
                ThreadId = c.Id,
                EntityType = c.EntityType,
                EntityId = c.EntityId,
                SubjectAr = c.SubjectAr,
                SubjectEn = c.SubjectEn,
                LastActivityAt = c.LastActivityAt,
                UnreadCount = unread.TryGetValue(c.Id, out int n) ? n : 0,
                CanComment = !c.IsLocked,
            }).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<PortalConversationDto?> GetConversationAsync(
            PortalContext portal, long threadId, CancellationToken cancellationToken = default)
        {
            var (thread, access) = await AuthorizeAsync(portal, threadId, cancellationToken);
            if (thread == null || access == null) return null;

            var unread = await UnreadByThreadAsync(portal, new List<long> { thread.Id }, cancellationToken);

            return new PortalConversationDto
            {
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                SubjectAr = thread.SubjectAr,
                SubjectEn = thread.SubjectEn,
                LastActivityAt = thread.LastActivityAt,
                UnreadCount = unread.TryGetValue(thread.Id, out int n) ? n : 0,
                CanComment = !thread.IsLocked,
            };
        }

        // ---------------------------------------------------------------------------------------------
        // READ COMMENTS
        //
        // The visibility filter is in SQL, not applied after loading. An internal comment is never
        // materialised for an external caller at all, so there is no object for a later projection
        // bug to leak — the row does not arrive.
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<PortalCommentDto>> ListCommentsAsync(
            PortalContext portal, long threadId, CommPageRequest? page = null, CancellationToken cancellationToken = default)
        {
            var (thread, access) = await AuthorizeAsync(portal, threadId, cancellationToken);
            if (thread == null || access == null) return Array.Empty<PortalCommentDto>();

            int take = _options.ClampPageSize(page?.PageSize);
            long after = page?.AfterId ?? 0;

            var rows = await _db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == thread.Id
                            && c.CompanyID == portal.CompanyId
                            && c.DeletedAt == null
                            && c.Visibility == CommVisibility.Public
                            && c.Id > after)
                .OrderBy(c => c.Id)                      // a conversation reads downwards
                .Take(take)
                .Select(c => new
                {
                    c.Id, c.Body, c.BodyFormat, c.CreatedAt, c.AuthorPortalUserId, c.AttachmentCount,
                })
                .ToListAsync(cancellationToken);

            return rows.Select(c => new PortalCommentDto
            {
                CommentId = c.Id,
                Body = c.Body,
                BodyFormat = c.BodyFormat,
                CreatedAt = c.CreatedAt,
                // "Mine" means THIS portal user. Another contact at the same customer is not "me",
                // and an employee author is reported only as not-mine — no name, no id, no role.
                IsMine = c.AuthorPortalUserId == portal.PortalUserId,
                AttachmentCount = c.AttachmentCount,
            }).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // POST
        //
        // THE AUTHOR AND THE VISIBILITY ARE BOTH SERVER-DECIDED. The author is CommActor.PortalUser
        // built from the resolved context — there is no author parameter. The visibility is the
        // constant CommVisibility.Public — there is no visibility parameter either, so "portal posts
        // an internal note" is not a request that can be made rather than one that is refused.
        // ---------------------------------------------------------------------------------------------
        public async Task<PortalCommentDto?> PostCommentAsync(
            PortalContext portal, long threadId, string? body, CancellationToken cancellationToken = default)
        {
            var (thread, access) = await AuthorizeAsync(portal, threadId, cancellationToken);
            if (thread == null || access == null) return null;

            if (thread.IsLocked) return null;

            // The SAME body policy the internal path uses: one set of length, format and structure
            // rules, so an external comment cannot carry something an internal one may not.
            var prepared = _body.Prepare(body, CommBodyFormat.Markdown);

            var author = CommActor.PortalUser(portal.PortalUserId);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var comment = new CommComment
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                ParentCommentId = null,        // external replies are flat in this increment
                Depth = 0,
                Body = prepared.Body,
                BodyFormat = prepared.Format,

                // NOT a parameter, and not derived from anything the caller sent.
                Visibility = CommVisibility.Public,
                Author = author,

                BodyAnalysisJson = System.Text.Json.JsonSerializer.Serialize(prepared.Analysis),
                CreatedAt = DateTime.UtcNow,
            };
            _db.Comments.Add(comment);

            // Counters in the SAME transaction as the row they count, matching the internal path.
            thread.CommentCount += 1;
            thread.LastActivityAt = comment.CreatedAt;
            thread.UpdatedAt = comment.CreatedAt;
            _db.Threads.Update(thread);

            await _db.SaveAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // NO MENTIONS (deferred, 3B-B1 §15), NO NOTIFICATION (3B-E owns delivery) and NO BUSINESS
            // EVENT (the bridge is off). Each omission is a decision recorded in this wave's report,
            // not an oversight: a portal comment that silently notified nobody would be worse.
            return new PortalCommentDto
            {
                CommentId = comment.Id,
                Body = comment.Body,
                BodyFormat = comment.BodyFormat,
                CreatedAt = comment.CreatedAt,
                IsMine = true,
                AttachmentCount = 0,
            };
        }

        // ---------------------------------------------------------------------------------------------
        // MARK READ
        //
        // Authority FIRST. A watermark write is small, but writing one for a thread the caller cannot
        // open would confirm the thread exists — and it would let an outsider create a row keyed to a
        // conversation that is not theirs.
        // ---------------------------------------------------------------------------------------------
        public async Task<bool> MarkReadAsync(
            PortalContext portal, long threadId, long? upToCommentId = null, CancellationToken cancellationToken = default)
        {
            var (thread, access) = await AuthorizeAsync(portal, threadId, cancellationToken);
            if (thread == null || access == null) return false;

            long watermark = upToCommentId ?? await _db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == thread.Id
                            && c.CompanyID == portal.CompanyId
                            && c.Visibility == CommVisibility.Public)
                .Select(c => c.Id)
                .OrderByDescending(id => id)
                .FirstOrDefaultAsync(cancellationToken);

            // THE PORTAL ARM of the split uniqueness. An employee receipt for the same thread and the
            // same number is a different row and is never found by this predicate.
            var receipt = await _db.ReadReceipts
                .Where(r => r.ThreadId == thread.Id
                            && r.CompanyID == portal.CompanyId
                            && r.PortalUserId == portal.PortalUserId)
                .FirstOrDefaultAsync(cancellationToken);

            if (receipt == null)
            {
                receipt = new CommReadReceipt
                {
                    CompanyID = thread.CompanyID,
                    ThreadId = thread.Id,
                    Reader = CommActor.PortalUser(portal.PortalUserId),
                    LastReadCommentId = watermark,
                    LastReadAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.ReadReceipts.Add(receipt);
            }
            else
            {
                // FORWARD ONLY, matching the internal path: an out-of-order client must not reopen
                // comments the reader has already cleared.
                if (watermark > (receipt.LastReadCommentId ?? 0)) receipt.LastReadCommentId = watermark;
                receipt.LastReadAt = DateTime.UtcNow;
                receipt.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveAsync(cancellationToken);
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        // ATTACHMENT METADATA
        //
        // THERE IS NO DOWNLOAD HERE, AND THAT IS THE POINT. This platform stores no bytes; StorageKey
        // is an opaque handle into the deployment's file store. Emitting it would hand an external
        // caller a name for bytes that the file store, not this policy, decides who may read — the
        // "static file URL authority" the brief forbids. So the key is projected away and never
        // crosses this boundary, which makes "a direct path bypasses authority" not a check that could
        // fail but a URL that does not exist.
        //
        // Byte download for external callers is DEFERRED with its reason in this wave's report.
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<PortalAttachmentDto>> ListAttachmentsAsync(
            PortalContext portal, long threadId, CancellationToken cancellationToken = default)
        {
            var (thread, access) = await AuthorizeAsync(portal, threadId, cancellationToken);
            if (thread == null || access == null) return Array.Empty<PortalAttachmentDto>();

            // Joined to the comment so an attachment on an INTERNAL comment is unreachable even though
            // the thread itself is externally readable. Attachment visibility follows its comment.
            return await (
                from a in _db.Attachments.AsNoTracking()
                where a.ThreadId == thread.Id && a.CompanyID == portal.CompanyId && a.DeletedAt == null
                join c in _db.Comments.AsNoTracking() on a.CommentId equals c.Id
                where c.Visibility == CommVisibility.Public && c.DeletedAt == null
                orderby a.Id
                select new PortalAttachmentDto
                {
                    AttachmentId = a.Id,
                    CommentId = a.CommentId,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    PreviewKind = a.PreviewKind,
                })
                .Take(_options.MaxPageSize)
                .ToListAsync(cancellationToken);
        }

        // =========================================================================================
        // internals
        // =========================================================================================

        private static bool IsResolved(PortalContext? portal)
            => portal != null && portal.CompanyId > 0 && portal.CustomerId > 0 && portal.PortalUserId > 0;

        /// THE ONE AUTHORIZATION CALL every public method makes. It loads the thread and asks the
        /// external policy — the same five conditions, every time, in one place, so a new method
        /// cannot accidentally be written without them.
        private async Task<(CommThread? Thread, CommExternalAccess? Access)> AuthorizeAsync(
            PortalContext portal, long threadId, CancellationToken cancellationToken)
        {
            if (!IsResolved(portal)) return (null, null);

            var thread = await _db.Threads
                .Where(t => t.Id == threadId)
                .FirstOrDefaultAsync(cancellationToken);
            if (thread == null) return (null, null);

            var access = await _external.ResolveThreadAccessAsync(portal, thread, cancellationToken);
            return access.CanRead ? (thread, access) : (null, null);
        }

        /// Which of these anchor ids belong to this customer, in ONE query per anchor type.
        ///
        /// It mirrors CommExternalAccessPolicy's own predicate deliberately: company AND customer in
        /// the same statement, and an entity type outside the allow-list contributes nothing.
        private async Task<HashSet<int>> OwnedAnchorIdsAsync(
            PortalContext portal, List<int> entityIds, CancellationToken cancellationToken)
        {
            if (entityIds.Count == 0) return new HashSet<int>();

            var owned = await _db.Context.Quotations.AsNoTracking()
                .Where(q => entityIds.Contains(q.ID)
                            && q.CompanyID == portal.CompanyId
                            && q.CustomerId == portal.CustomerId)
                .Select(q => q.ID)
                .ToListAsync(cancellationToken);

            return new HashSet<int>(owned);
        }

        /// Unread counts for a PAGE of threads in one grouped query.
        ///
        /// Counted over what this caller MAY READ — Public, undeleted — above their own watermark. The
        /// internal total would show a customer unread items they can never open, and a badge that
        /// cannot be cleared is worse than no badge.
        private async Task<Dictionary<long, int>> UnreadByThreadAsync(
            PortalContext portal, List<long> threadIds, CancellationToken cancellationToken)
        {
            if (threadIds.Count == 0) return new Dictionary<long, int>();

            var watermarks = await _db.ReadReceipts.AsNoTracking()
                .Where(r => threadIds.Contains(r.ThreadId)
                            && r.CompanyID == portal.CompanyId
                            && r.PortalUserId == portal.PortalUserId)
                .Select(r => new { r.ThreadId, r.LastReadCommentId })
                .ToListAsync(cancellationToken);

            var floor = watermarks.ToDictionary(w => w.ThreadId, w => w.LastReadCommentId ?? 0);

            var counts = await _db.Comments.AsNoTracking()
                .Where(c => threadIds.Contains(c.ThreadId)
                            && c.CompanyID == portal.CompanyId
                            && c.DeletedAt == null
                            && c.Visibility == CommVisibility.Public)
                .GroupBy(c => c.ThreadId)
                .Select(g => new { ThreadId = g.Key, Ids = g.Select(x => x.Id).ToList() })
                .ToListAsync(cancellationToken);

            var result = new Dictionary<long, int>();
            foreach (var row in counts)
            {
                long mark = floor.TryGetValue(row.ThreadId, out long m) ? m : 0;
                result[row.ThreadId] = row.Ids.Count(id => id > mark);
            }
            return result;
        }
    }
}
