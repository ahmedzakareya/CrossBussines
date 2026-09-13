using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Communication
{
    // ============================================================================================
    // WHAT THE CONVERSATION PANEL IS SERVED — one projection, one upload rule, every module.
    //
    // The panel (Views/Shared/_EntityConversation.cshtml) is deliberately module-neutral: the screen
    // names its own endpoints, and each module's controller answers them behind its own permission
    // gate. That left the SHAPE of the answer duplicated, and the first version of it was a flat list
    // of {author, body, date} — two of the twelve things the platform stores. A comment already knows
    // its parent, its attachments, its reactions and what THIS caller may do to it; the endpoints just
    // were not saying so.
    //
    // Projected here rather than in each controller because the panel is one piece of markup: a field
    // added for invoices and forgotten for quotations is a feature that works on one screen, which is
    // worse than a feature that works on neither.
    //
    // WHAT IS DELIBERATELY NOT PROJECTED:
    //   · the raw StorageKey, unless it is a path this deployment serves. The platform stores an opaque
    //     handle and resolves no url (ADR-030 §8); handing an arbitrary stored string to the browser as
    //     an href is how an opaque key becomes an open redirect the day another file store is wired.
    //   · mention target ids, counts and keys — labels only, as the controllers already document.
    //   · the visibility rank and the thread's lock reason: the panel neither renders nor decides them.
    // ============================================================================================
    public static class CommPanel
    {
        // 20 MB. Chat allows 50 for a deliberate file-transfer feature; a note on a document is not
        // that, and the cap is what stops a scanned catalogue being pinned to an invoice.
        public const long MaxUploadBytes = 20L * 1024 * 1024;

        // Under a prefix PrivateFileGate already requires authentication for. A new top-level folder
        // would be served anonymously until somebody remembered to register it.
        public const string UploadFolderUrl = "/uploads/comm/threads";

        /// <summary>
        /// WHO IS READING — their own name and photograph, for the composer's avatar.
        ///
        /// The panel used to take that face from the reader's last message in the thread, so a record
        /// they had not written on yet showed a generic silhouette on the box they type in. Nothing is
        /// disclosed by this: it is the caller describing themselves to themselves.
        /// </summary>
        public static async Task<object?> MeAsync(
            CrossDbContext db, BusinessContext context, bool arabic, CancellationToken ct = default)
        {
            if (context?.EmployeeId is not > 0) return null;
            int id = context.EmployeeId.Value;

            var row = await db.Employee.AsNoTracking()
                .Where(e => e.ID == id)
                .Select(e => new { e.FullName, e.FullNameEn })
                .FirstOrDefaultAsync(ct);

            if (row is null) return null;

            var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(db, context, new[] { id }, ct);

            return new
            {
                employeeId = id,
                name = arabic ? (row.FullName ?? row.FullNameEn) : (row.FullNameEn ?? row.FullName),
                avatar = photos.TryGetValue(id, out var url) ? url : null,
            };
        }

        /// <summary>The per-comment shape the panel renders. `avatars` is employee id → photo url.</summary>
        public static object[] Project(
            IReadOnlyList<CommCommentDto> items, bool arabic, IReadOnlyDictionary<int, string> avatars)
        {
            if (items is null) return Array.Empty<object>();

            return items.Select(c => (object)new
            {
                id = c.CommentId,
                parentId = c.ParentCommentId,
                depth = c.Depth,
                body = c.Body,
                author = c.Author.Display(arabic),
                authorEmployeeId = c.Author.EmployeeId,
                authorAvatar = avatars.TryGetValue(c.Author.EmployeeId, out var url) ? url : null,
                createdAt = c.CreatedAt,
                editedAt = c.EditedAt,
                isDeleted = c.IsDeleted,

                // LABELS only — never TargetId, TargetKey or ResolvedRecipientCount. How many people a
                // role mention reached describes the shape of an organisation the caller may not see.
                mentions = c.Mentions.Select(m => new
                {
                    display = arabic ? (m.LabelAr ?? m.LabelEn) : (m.LabelEn ?? m.LabelAr),
                }),

                // `mine` is what lets the chip render as pressed, and it is computed by the platform
                // against the reading employee — never by the browser comparing ids.
                reactions = c.Reactions
                    .Where(r => r.Count > 0)
                    .Select(r => new { key = r.ReactionKey, count = r.Count, mine = r.Mine }),

                attachments = c.Attachments
                    .Where(a => !a.IsDeleted)
                    .Select(a => new
                    {
                        id = a.AttachmentId,
                        name = a.FileName,
                        contentType = a.ContentType,
                        size = a.SizeBytes,
                        url = ServableUrl(a.StorageKey),
                        isImage = a.Preview.CanInline
                                  && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                    })
                    .Where(a => a.url != null),

                can = new
                {
                    reply = c.Capabilities.CanReply,
                    react = c.Capabilities.CanReact,
                    edit = c.Capabilities.CanEdit,
                    delete = c.Capabilities.CanDelete,
                },
            }).ToArray();
        }

        // A STORED HANDLE IS NOT A URL. This deployment's file store writes site-relative paths under
        // /uploads, so those are served as-is and everything else — an absolute url, a Windows path, a
        // key from some future object store — comes back null and the attachment is simply not offered
        // as a link. Refusing to guess is the only safe reading of an opaque handle.
        public static string? ServableUrl(string? storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey)) return null;

            var key = storageKey.Replace('\\', '/').Trim();
            if (key.Contains("://", StringComparison.Ordinal)) return null;
            if (key.Contains("..", StringComparison.Ordinal)) return null;
            if (!key.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return null;

            return key;
        }

        public static class UploadRefusal
        {
            public const string None = "";
            public const string Empty = "attachment_empty";
            public const string TooLarge = "attachment_too_large";
            public const string Type = "attachment_type";
            public const string Failed = "attachment_failed";
        }

        /// <summary>
        /// Writes an accepted upload into the gated folder and returns the platform's attachment
        /// request for it. The caller MUST have authorized the write first — this method is a file
        /// policy, not an authorization: it decides what kind of file may be stored, never who may
        /// store one. (Same distinction, and the same reason, as ChatAttachments.IsAcceptedType.)
        /// </summary>
        public static async Task<(CommAttachmentRequest? Attachment, string Refusal)> StageAsync(
            IFormFile? file, string webRootPath, CancellationToken ct = default)
        {
            if (file is null || file.Length <= 0) return (null, UploadRefusal.Empty);
            if (file.Length > MaxUploadBytes) return (null, UploadRefusal.TooLarge);

            var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();

            // THE SAME ALLOW-LIST AS CHAT, on purpose. Active content (html, svg, script) is refused
            // outright: these files land in the application's own origin, where a colleague opening one
            // would run its script as themselves. One list, so a type accepted in one place is not
            // quietly accepted in another.
            if (!ChatAttachments.IsAcceptedType(ext, file.ContentType)) return (null, UploadRefusal.Type);

            try
            {
                var dir = Path.Combine(webRootPath, "uploads", "comm", "threads");
                Directory.CreateDirectory(dir);

                // A GUID plus the VALIDATED extension. The client's filename is kept only as a display
                // string and never used to build a path.
                var stored = Guid.NewGuid().ToString("N") + ext;
                await using (var stream = File.Create(Path.Combine(dir, stored)))
                    await file.CopyToAsync(stream, ct);

                return (new CommAttachmentRequest
                {
                    FileName = Path.GetFileName(file.FileName ?? stored),
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                        ? "application/octet-stream"
                        : file.ContentType,
                    SizeBytes = file.Length,
                    StorageKey = UploadFolderUrl + "/" + stored,
                }, UploadRefusal.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return (null, UploadRefusal.Failed);
            }
        }
    }
}
