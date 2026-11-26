using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §8) — ATTACHMENTS (module 7) and FILE PREVIEW (module 11).
    //
    // THIS PLATFORM STORES NO BYTES. Not a limitation — a boundary:
    //   * The deployment already has a file store (FileManagerService, HrDocumentService, wwwroot/Files).
    //     A second one would mean two retention policies, two virus-scan stories and two backup scopes.
    //   * StorageKey is an OPAQUE handle. This platform never resolves it to a URL, never streams it, and never
    //     puts it in an event or audit payload — a StorageKey is capability-bearing, and an audit row is read by
    //     more people than the file is (see CommAuditWriter's forbidden-key check, which enforces that).
    //   * Download authorization stays with the file store. This platform authorizes the COMMENT; the store
    //     authorizes the BYTES. Conflating them would mean a comment permission silently granting file access.
    //
    // PREVIEW IS CLASSIFICATION, NOT RENDERING. ICommFilePreviewProvider answers "what kind of preview could
    // this have, and is it safe to inline" from the declared content type. It renders nothing and reads no bytes.
    // The answer is computed at WRITE time and stored, so a later change to the classifier does not silently
    // restate what users were already shown.
    // =============================================================================================
    public interface ICommAttachmentService
    {
        // ENROLS in the caller's transaction and DOES NOT SAVE — attachments share the comment's fate.
        Task<IReadOnlyList<CommCommentAttachment>> AttachAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            IReadOnlyList<CommAttachmentRequest>? requests, CancellationToken cancellationToken = default);

        Task<bool> RemoveAsync(
            BusinessContext context, long attachmentId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommAttachmentDto>> ListForCommentAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default);

        // Portal listing is still constrained by the exact comment/thread read policy used by delivery.
        Task<IReadOnlyList<CommAttachmentDto>> ListAccessibleAsync(
            BusinessContext context, string? query, CancellationToken cancellationToken = default);

        // The authorized byte path (GAP-01). Addressed by AttachmentId, never by a storage key. Returns null
        // for absent, foreign-company, unreadable-thread, above-tier, withdrawn and removed alike.
        Task<CommAttachmentDto?> OpenAuthorizedAsync(
            BusinessContext context, long attachmentId, CancellationToken cancellationToken = default);
    }

    // ---------------------------------------------------------------------------------------------
    // EXTENSION POINT: preview classification. A deployment that generates real thumbnails registers its own.
    // ---------------------------------------------------------------------------------------------
    public interface ICommFilePreviewProvider
    {
        CommFilePreviewDto Classify(string fileName, string contentType, long sizeBytes);
    }

    public sealed class DefaultCommFilePreviewProvider : ICommFilePreviewProvider
    {
        // Inlining a large image means the browser downloads all of it to render a preview nobody asked for.
        public const long MaxInlineImageBytes = 5L * 1024 * 1024;

        // SVG IS DELIBERATELY EXCLUDED from inlineable images. An SVG is an XML document that can carry script;
        // inlining one from a comment is stored XSS with extra steps. It classifies as an image (so a UI shows an
        // image icon) but CanInline is false with the reason recorded.
        private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp" };

        private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { "text/plain", "text/csv", "text/markdown", "application/json", "application/xml", "text/xml" };

        private static readonly HashSet<string> OfficeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

        private static readonly HashSet<string> ArchiveTypes = new(StringComparer.OrdinalIgnoreCase)
        { "application/zip", "application/x-zip-compressed", "application/x-rar-compressed", "application/x-7z-compressed", "application/gzip" };

        public CommFilePreviewDto Classify(string fileName, string contentType, long sizeBytes)
        {
            var type = (contentType ?? "").Trim();

            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                if (type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
                    return new CommFilePreviewDto
                    {
                        Kind = CommPreviewKind.Image, CanInline = false,
                        Reason = "SVG is an XML document that can carry script; inlining one from a comment is stored XSS",
                    };

                bool inlineable = InlineImageTypes.Contains(type) && sizeBytes <= MaxInlineImageBytes;
                return new CommFilePreviewDto
                {
                    Kind = CommPreviewKind.Image,
                    CanInline = inlineable,
                    Reason = inlineable ? null
                        : sizeBytes > MaxInlineImageBytes
                            ? $"image is larger than the {MaxInlineImageBytes / (1024 * 1024)} MB inline budget"
                            : "image type is not on the inline allow-list",
                };
            }

            if (type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                return new CommFilePreviewDto { Kind = CommPreviewKind.Pdf, CanInline = true };

            if (TextTypes.Contains(type) || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return new CommFilePreviewDto { Kind = CommPreviewKind.Text, CanInline = true };

            if (OfficeTypes.Contains(type))
                return new CommFilePreviewDto
                {
                    Kind = CommPreviewKind.Office, CanInline = false,
                    Reason = "office documents need a server-side or third-party converter, which this platform does not ship",
                };

            if (ArchiveTypes.Contains(type))
                return new CommFilePreviewDto
                {
                    Kind = CommPreviewKind.Archive, CanInline = false, Reason = "archives have no preview",
                };

            // Unknown types default to NOT inlineable. Fail closed: an unrecognised content type is exactly the
            // case where guessing is most likely to inline something executable.
            return new CommFilePreviewDto
            {
                Kind = CommPreviewKind.None, CanInline = false,
                Reason = string.IsNullOrWhiteSpace(type) ? "no content type declared" : $"'{type}' has no preview classifier",
            };
        }
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommAttachmentService : ICommAttachmentService
    {
        private readonly CommDb _db;
        private readonly ICommFilePreviewProvider _preview;
        private readonly ICommEventPublisher _events;
        private readonly ICommActorDirectory _actors;
        private readonly ICommEntitySurface _surface;
        private readonly ICommAccessPolicy _access;
        private readonly CommunicationPlatformOptions _options;

        public CommAttachmentService(
            CrossDbContext db,
            ICommFilePreviewProvider preview,
            ICommEventPublisher events,
            ICommActorDirectory actors,
            ICommEntitySurface surface,
            ICommAccessPolicy access,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _preview = preview;
            _events = events;
            _actors = actors;
            _surface = surface;
            _access = access;
            _options = options.Value;
        }

        public async Task<IReadOnlyList<CommCommentAttachment>> AttachAsync(
            BusinessContext context, CommThread thread, CommComment comment,
            IReadOnlyList<CommAttachmentRequest>? requests, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(thread);
            ArgumentNullException.ThrowIfNull(comment);

            if (requests == null || requests.Count == 0) return Array.Empty<CommCommentAttachment>();

            // THE ACTOR IS RESOLVED BEFORE ANY WORK, not at the point the row is built. This method is
            // public on ICommAttachmentService and is not reachable only through CommCommentService,
            // so it cannot rely on a caller having already checked — it used to, and that reliance is
            // exactly how the `?? 0` below it stayed invisible.
            var uploader = CommActor.FromContext(context);

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);

            // The Attachments capability is CHECKED SEPARATELY from Comments and maps to the registry's
            // SupportsFiles. An entity may legitimately carry comments without carrying files — a POS order can
            // be discussed without becoming a document store.
            await _surface.RequireAsync(entity, CommCapabilities.Attachments);

            if (requests.Count > _options.MaxAttachmentsPerComment)
                throw new CommValidationException(
                    CommValidationException.Codes.TooManyAttachments,
                    $"{requests.Count} attachments exceeds the limit of {_options.MaxAttachmentsPerComment}.");

            var created = new List<CommCommentAttachment>(requests.Count);

            foreach (var request in requests)
            {
                if (string.IsNullOrWhiteSpace(request.StorageKey))
                    throw new CommValidationException(
                        CommValidationException.Codes.AttachmentStorageKeyRequired,
                        "An attachment must already exist in the deployment's file store; this platform receives no bytes.");

                if (request.SizeBytes < 0 || request.SizeBytes > _options.MaxAttachmentBytes)
                    throw new CommValidationException(
                        CommValidationException.Codes.AttachmentTooLarge,
                        $"Attachment '{request.FileName}' is {request.SizeBytes} bytes, over the " +
                        $"{_options.MaxAttachmentBytes}-byte limit.");

                var classification = _preview.Classify(request.FileName ?? "", request.ContentType ?? "", request.SizeBytes);

                var row = new CommCommentAttachment
                {
                    CompanyID = thread.CompanyID,
                    CommentId = comment.Id,
                    ThreadId = thread.Id,

                    // The file NAME is caller-supplied text that ends up on a screen, so it is stripped of path
                    // separators: "..\\..\\web.config" as a display name is a phishing aid, and it must never be
                    // mistaken for a path by anything downstream.
                    FileName = SafeFileName(request.FileName),
                    ContentType = Cap(request.ContentType ?? "application/octet-stream",
                        Models.Context.Communication.CommunicationModel.ContentTypeLength),
                    SizeBytes = request.SizeBytes,
                    StorageKey = request.StorageKey.Trim(),
                    PreviewKind = classification.Kind,
                    CanInline = classification.CanInline,
                    // WAS `context.EmployeeId ?? 0`. That silently attributed an unresolved identity to
                    // employee 0 and, paired with the ownership test in CommAccessPolicy, made such a
                    // caller the "owner" of every row authored by 0. CommActor.FromContext refuses
                    // instead: an upload that cannot say who made it does not happen.
                    UploadedBy = uploader,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Attachments.Add(row);
                created.Add(row);
            }

            comment.AttachmentCount = created.Count;
            await _db.SaveAsync(cancellationToken);

            foreach (var row in created)
                await _events.PublishAsync(new CommEvent
                {
                    EventType = CommEventTypes.AttachmentAdded,
                    Entity = entity,
                    ThreadId = thread.Id,
                    CommentId = comment.Id,
                    CompanyId = thread.CompanyID,
                    BranchId = thread.BranchID,
                    ActorEmployeeId = context.EmployeeId,
                    Visibility = comment.Visibility,
                    Payload = new CommAttachmentEventPayload
                    {
                        ThreadId = thread.Id, CommentId = comment.Id, AttachmentId = row.Id,
                        ContentType = row.ContentType, SizeBytes = row.SizeBytes,
                        PreviewKind = row.PreviewKind, FileName = row.FileName,

                        // StorageKey is absent by contract — see CommAttachmentEventPayload.
                    },
                    DedupKey = $"attachment:{row.Id}:added",
                    CorrelationId = context.CorrelationId,
                }, cancellationToken);

            return created;
        }

        public async Task<bool> RemoveAsync(
            BusinessContext context, long attachmentId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var row = await _db.Attachments
                .Where(a => a.Id == attachmentId && a.CompanyID == context.CompanyId && a.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null) return false;

            var thread = await _db.Threads
                .Where(t => t.Id == row.ThreadId && t.CompanyID == context.CompanyId && t.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (thread == null) throw new CommNotFoundException("Thread", row.ThreadId);

            var comment = await _db.Comments.AsNoTracking()
                .Where(c => c.Id == row.CommentId && c.CompanyID == context.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) throw new CommNotFoundException("Comment", row.CommentId);

            var threadAccess = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
            var rights = _access.ResolveCommentRights(context, threadAccess, comment);

            // Removing an attachment is governed by the same right as deleting the comment that carries it: the
            // attachment is part of what the author said.
            if (!rights.CanDelete)
                throw new CommAccessDeniedException(
                    "attachment-remove", new CommEntityRef(thread.EntityType, thread.EntityId), rights.Reason);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // Soft delete, and NOTHING is deleted from the file store. This platform did not put the bytes there
            // and does not own their lifecycle; deleting them would break any other reference to the same key.
            row.DeletedAt = DateTime.UtcNow;
            row.DeletedBy = context.EmployeeId;
            row.updatedBy = context.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.AttachmentRemoved,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = row.ThreadId,
                CommentId = row.CommentId,
                CompanyId = context.CompanyId,
                BranchId = thread.BranchID,
                ActorEmployeeId = context.EmployeeId,
                Visibility = comment.Visibility,
                Payload = new CommAttachmentEventPayload
                {
                    ThreadId = row.ThreadId, CommentId = row.CommentId, AttachmentId = row.Id,
                    ContentType = row.ContentType, SizeBytes = row.SizeBytes,
                    PreviewKind = row.PreviewKind, FileName = row.FileName,
                },
                DedupKey = $"attachment:{row.Id}:removed",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        }

        // READING AN ATTACHMENT LIST IS READING THE COMMENT, and must be authorized as such.
        //
        // THE DEFECT THIS CLOSES. This method used to filter on CompanyID alone. Tenant isolation held, but
        // nothing asked whether the caller may READ the comment — so any colleague in the same company could
        // name a commentId and receive the attachment rows for a conversation they have no access to: a
        // thread they are not a participant in, a record their module permission does not cover, or a comment
        // written at a visibility above their tier.
        //
        // WHY THAT IS WORSE THAN AN ORDINARY OVER-READ. The row carries StorageKey, which is a CAPABILITY,
        // not an identifier: /uploads/** is served by StaticFileMiddleware behind PrivateFileGate, and that
        // gate's policy for these prefixes is RequireAuthenticated — it checks that SOMEONE is signed in, not
        // WHO. RequireCompanyMatch exists in that enum and is deliberately applied to no prefix yet. So a
        // StorageKey handed to the wrong reader is a working download link for them, and it cannot be revoked.
        // Returning the key IS granting the file.
        //
        // The checks below are the same four CommCommentService.GetAsync performs, in the same order and with
        // the same 404-not-403 discipline: telling a caller that a comment exists but is above their tier
        // discloses that somebody wrote something confidential about this record. RemoveAsync above already
        // authorized thread AND comment rights; a read that returns a capability deserves no less than the
        // write that merely hides one.
        public async Task<IReadOnlyList<CommAttachmentDto>> ListForCommentAsync(
            BusinessContext context, long commentId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (await AuthorizeCommentReadAsync(context, commentId, cancellationToken) == null)
                throw new CommNotFoundException("Comment", commentId);

            var rows = await _db.Attachments.AsNoTracking()
                .Where(a => a.CommentId == commentId && a.CompanyID == context.CompanyId && a.DeletedAt == null)
                .OrderBy(a => a.Id)
                .ToListAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(rows.Select(r => r.UploadedByEmployeeId), cancellationToken);
            return rows.Select(r => ToDto(r, _actors.Get(actors, r.UploadedByEmployeeId))).ToList();
        }

        public async Task<IReadOnlyList<CommAttachmentDto>> ListAccessibleAsync(
            BusinessContext context, string? query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return Array.Empty<CommAttachmentDto>();

            var term = (query ?? string.Empty).Trim();
            var candidates = await _db.Attachments.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null
                            && (term.Length == 0 || a.FileName.Contains(term)))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) return Array.Empty<CommAttachmentDto>();

            // Authorize once per comment through the same predicate used by OpenAuthorizedAsync. A portal
            // listing must not turn company membership into access to every attachment in that company.
            var visible = new List<CommCommentAttachment>();
            foreach (var group in candidates.GroupBy(a => a.CommentId))
            {
                if (await AuthorizeCommentReadAsync(context, group.Key, cancellationToken) != null)
                    visible.AddRange(group);
            }
            if (visible.Count == 0) return Array.Empty<CommAttachmentDto>();

            var actors = await _actors.ResolveAsync(visible.Select(r => r.UploadedByEmployeeId), cancellationToken);
            return visible.Select(r => ToDto(r, _actors.Get(actors, r.UploadedByEmployeeId))).ToList();
        }

        // THE ONE AUTHORIZATION, so the list path and the delivery path cannot drift apart.
        //
        // Both of them answer the same question — "may this caller read this comment?" — and the moment that
        // question has two implementations, one of them is eventually weakened alone. It returns null rather
        // than throwing so each caller can shape its own refusal: the list throws CommNotFoundException, the
        // byte delivery returns a bare 404. The DECISION is identical either way.
        //
        // Null means: no such comment, another company's comment, a thread this caller may not read, a
        // visibility above their tier, or a withdrawn comment they cannot moderate. Deliberately one value —
        // a caller that could tell those apart could map the conversation it is not allowed to see.
        private async Task<(CommComment Comment, CommThread Thread, CommThreadAccess Access)?>
            AuthorizeCommentReadAsync(BusinessContext context, long commentId, CancellationToken cancellationToken)
        {
            var comment = await _db.Comments.AsNoTracking()
                .Where(c => c.Id == commentId && c.CompanyID == context.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);
            if (comment == null) return null;

            // The thread is read through the comment, never taken from the caller: a caller-supplied thread id
            // alongside a comment id is two facts that can disagree, and the comment's own ThreadId is the one
            // the attachment rows are filed under.
            var thread = await _db.Threads.AsNoTracking()
                .Where(t => t.Id == comment.ThreadId && t.CompanyID == context.CompanyId && t.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (thread == null) return null;

            var access = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
            if (!access.CanRead) return null;

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);
            if (!_access.CanReadComment(visibilities, comment, context)) return null;

            // A deleted comment's attachments stay visible to a moderator and to nobody else — the same rule
            // the comment body itself follows, so the two cannot disagree about what was withdrawn.
            if (comment.DeletedAt != null && !access.CanModerate) return null;

            return (comment, thread, access);
        }

        // GAP-01 — THE AUTHORIZED BYTE PATH.
        //
        // Until now a Comm attachment was reachable by its stored path alone. CommPanel wrote the bytes under
        // wwwroot and put the resulting "/uploads/comm/threads/<guid>.<ext>" into the page as an href, so the
        // only thing between a signed-in user of ANY company and the file was knowing that path —
        // StaticFileMiddleware serves it behind PrivateFileGate, whose policy for that prefix is
        // RequireAuthenticated: it checks that SOMEONE is signed in, never WHO.
        //
        // This method is what the browser now goes through instead. It is addressed by AttachmentId — a
        // business identifier the caller is entitled to hold — and never by a storage key, because naming
        // bytes must not be a way to reach them. It is the same stance DocumentsController already takes for
        // platform documents, applied to the surface that still lacked it.
        //
        // IT RETURNS NULL FOR EVERYTHING IT REFUSES. Absent, another company's, a thread the caller may not
        // read, a comment above their tier, a withdrawn comment, a removed attachment — one answer, so a
        // caller probing ids learns nothing from the difference between "no" and "not here".
        public async Task<CommAttachmentDto?> OpenAuthorizedAsync(
            BusinessContext context, long attachmentId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0 || attachmentId <= 0) return null;

            var row = await _db.Attachments.AsNoTracking()
                .Where(a => a.Id == attachmentId
                            && a.CompanyID == context.CompanyId
                            && a.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null) return null;

            // The comment is taken from the ROW, so the caller cannot pair an attachment it may reach with a
            // comment it may read. The authorization below is about the comment this attachment is actually on.
            if (await AuthorizeCommentReadAsync(context, row.CommentId, cancellationToken) == null) return null;

            var uploader = await _actors.ResolveAsync(new[] { row.UploadedByEmployeeId }, cancellationToken);
            return ToDto(row, _actors.Get(uploader, row.UploadedByEmployeeId));
        }

        internal static CommAttachmentDto ToDto(CommCommentAttachment row, CommActorDto uploader) => new()
        {
            AttachmentId = row.Id,
            CommentId = row.CommentId,
            FileName = row.FileName,
            ContentType = row.ContentType,
            SizeBytes = row.SizeBytes,
            StorageKey = row.StorageKey,
            Preview = new CommFilePreviewDto
            {
                Kind = row.PreviewKind,
                CanInline = row.CanInline,
                ThumbnailStorageKey = row.ThumbnailStorageKey,
            },
            UploadedBy = uploader,
            UploadedAt = row.CreatedAt,
            IsDeleted = row.DeletedAt != null,
        };

        // Display name only — path separators removed so the value can never be mistaken for a path, and so a
        // name like "..\\..\\web.config" cannot be used to mislead a reader about what they are downloading.
        private static string SafeFileName(string? fileName)
        {
            var name = (fileName ?? "").Trim();
            if (name.Length == 0) return "attachment";

            name = name.Replace('\\', '_').Replace('/', '_');
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

            return Cap(name, Models.Context.Communication.CommunicationModel.FileNameLength);
        }

        private static string Cap(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
