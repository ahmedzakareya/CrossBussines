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

        // THE LEGACY PREFIX. Bytes written here before GAP-01 was closed, and the reason it was open.
        //
        // The original note read: "Under a prefix PrivateFileGate already requires authentication for. A new
        // top-level folder would be served anonymously until somebody remembered to register it." That was
        // true and insufficient. PrivateFileGate's policy for this prefix is RequireAuthenticated — it
        // checks that SOMEONE is signed in, never WHO — so every signed-in user of every company could
        // fetch any of these files by path. Authentication was mistaken for authorization.
        //
        // Nothing is written here any more (see StageAsync). The constant stays because rows created
        // earlier still carry keys under it and must keep resolving, and because the delivery path has to
        // recognise them.
        public const string UploadFolderUrl = "/uploads/comm/threads";

        // WHERE NEW BYTES GO: outside the web root, where StaticFileMiddleware cannot reach them at all.
        //
        // This is the same boundary the platform document store already draws (App_Data/documents). A file
        // here has NO url, so there is no static surface to bypass and no gate rule to remember — the
        // protection is structural rather than configured. Keys written under it are opaque: they are not
        // paths the browser could use even if one leaked.
        public const string PrivateStoreFolder = "comm-attachments";

        // The opaque key prefix, chosen so a legacy "/uploads/..." key and a private key can never be
        // confused by the resolver below.
        public const string PrivateKeyPrefix = "commstore:";

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

        /// <summary>
        /// WHICH OF THESE COMMENTS NAMED THE READER — one indexed read for a whole page.
        ///
        /// WHY THIS IS HERE AND NOT IN EACH CONTROLLER. Project() takes `mentionedMe`, and four
        /// screens render that projection (Accounting, Inventory, Hospital, BusinessEventMonitor).
        /// Resolving it inline meant six lines of EF per screen, which is both four chances to write
        /// the predicate differently and four places to forget the company. One screen having a
        /// subtly different idea of "I was mentioned" is not a performance problem, it is two
        /// answers to one question.
        ///
        /// AN OMITTED CALL IS NOT A FAILURE, IT IS A SCREEN THAT HAS NOT OPTED IN. A caller that
        /// does not pass the result gets `mentionsMe = false` on every row — indistinguishable from
        /// "nobody mentioned you". That is a deliberate property of Project's optional parameter and
        /// the reason this helper is one argument rather than six lines: opting a screen in should
        /// be too small a change to get wrong, and too visible to forget.
        ///
        /// IT READS CommMentionRecipients, not CommMentions, so a mention that reached the caller
        /// through their team, department or role counts exactly as a direct one does — the
        /// recipient table is where the platform has already resolved that.
        ///
        /// SCOPED BY COMPANY EXPLICITLY. No Communication entity carries a global query filter —
        /// there is no HasQueryFilter anywhere in CommunicationModel — so every read in this
        /// platform states its own company predicate, 45 times across BL/Communication. The comment
        /// ids alone would very nearly do it here, since they come from a page that is already
        /// scoped; "very nearly" is not a tenancy argument, and (CompanyID, EmployeeId) is also the
        /// leading edge of IX_CommMentionRecipients_Employee, so the correct predicate is the fast
        /// one too.
        /// </summary>
        public static async Task<IReadOnlySet<long>> MentionedMeAsync(
            CrossDbContext db, BusinessContext context,
            IReadOnlyList<CommCommentDto>? items, CancellationToken ct = default)
        {
            // EMPTY, NEVER NULL. Project treats null as "this screen did not ask" and an empty set as
            // "asked, and nothing named you". A caller that genuinely resolved no mentions must not
            // be mistaken for one that never looked.
            var none = (IReadOnlySet<long>)new HashSet<long>();

            if (db is null || context?.EmployeeId is not > 0 || context.CompanyId <= 0) return none;
            if (items is null || items.Count == 0) return none;

            int me = context.EmployeeId.Value;
            int companyId = context.CompanyId;
            var pageIds = items.Select(c => c.CommentId).Distinct().ToList();
            if (pageIds.Count == 0) return none;

            var hit = await new CommDb(db).MentionRecipients.AsNoTracking()
                .Where(r => r.CompanyID == companyId
                            && r.EmployeeId == me
                            && pageIds.Contains(r.CommentId))
                .Select(r => r.CommentId)
                .Distinct()
                .ToListAsync(ct);

            return hit.ToHashSet();
        }

        /// <summary>The per-comment shape the panel renders. `avatars` is employee id → photo url.</summary>
        // `mentionedMe` is the one thing this projection says about the READER rather than about
        // the comment: was I, personally, reached by a mention on it.
        //
        // A BOOLEAN ABOUT MYSELF, never the ids. The rule above stands - target ids and recipient
        // counts describe an organisation the caller may not see - and "I was named here" leaks
        // nothing about anyone else. It is resolved from CommMentionRecipients, so a mention that
        // reached me through my team or my department counts exactly as a direct one does.
        public static object[] Project(
            IReadOnlyList<CommCommentDto> items, bool arabic, IReadOnlyDictionary<int, string> avatars,
            IReadOnlySet<long>? mentionedMe = null)
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

                mentionsMe = mentionedMe is not null && mentionedMe.Contains(c.CommentId),

                // LABELS only — never TargetId, TargetKey or ResolvedRecipientCount. How many people a
                // role mention reached describes the shape of an organisation the caller may not see.
                //
                // TWO FIELDS, BECAUSE WRITING AND READING HAPPEN IN DIFFERENT LANGUAGES. `display` is
                // the name for THIS reader; `match` is every name the mention has ever been known by.
                //
                // The composer writes the name into the body in the author's language, so a message
                // typed in English and read in Arabic carries "@ahmed anas" while `display` is
                // "احمد انس". A panel matching only `display` finds nothing, and the mention falls back
                // to a badge - which is exactly what one message did and the one above it did not,
                // for no reason a reader could see.
                //
                // Both labels are frozen at mention time, so this changes nothing about what the
                // record says; it only lets the reader's own language be the one on screen.
                mentions = c.Mentions.Select(m => new
                {
                    display = arabic ? (m.LabelAr ?? m.LabelEn) : (m.LabelEn ?? m.LabelAr),
                    match = new[] { m.LabelAr, m.LabelEn }
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
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
                        // The authorized route, not the stored path. Every attachment the caller is
                        // already permitted to see gets one, so nothing is dropped from the panel — but
                        // the bytes behind it are re-authorized on each request.
                        url = DeliveryUrl(a.AttachmentId),
                        isImage = a.Preview.CanInline
                                  && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                    }),

                can = new
                {
                    reply = c.Capabilities.CanReply,
                    react = c.Capabilities.CanReact,
                    edit = c.Capabilities.CanEdit,
                    delete = c.Capabilities.CanDelete,
                },
            }).ToArray();
        }

        // THE BROWSER IS GIVEN A ROUTE, NEVER A STORED PATH (GAP-01).
        //
        // This used to return the StorageKey itself whenever it looked like a site-relative /uploads path,
        // which put the file's real location into the page as an href. Two things were wrong with that.
        // The link bypassed every authorization the platform performs — StaticFileMiddleware answers it
        // behind a gate that only asks whether SOMEONE is signed in — and the key, which is capability-
        // bearing, was handed to the client where it could be copied, forwarded or logged and would keep
        // working forever with no way to revoke it.
        //
        // The id is safe to publish precisely because it is NOT a capability: /Documents/comm/{id}/content
        // re-authorizes the caller on every request, so holding the number grants nothing. That is the
        // difference between an identifier and a key, and it is the whole point of addressing bytes by
        // business id.
        public static string DeliveryUrl(long attachmentId) => $"/Documents/comm/{attachmentId}/content";

        // Resolves a stored key to a file on disk, for the AUTHORIZED delivery path only. Never call it
        // before authorizing the attachment's comment.
        //
        // Two shapes are accepted and nothing else:
        //   · "commstore:<name>"            - written by StageAsync, lives outside the web root.
        //   · "/uploads/comm/threads/<name>" - written before GAP-01, still under wwwroot.
        //
        // The file NAME is taken through Path.GetFileName in both branches, so a key carrying "..", a
        // rooted path or a directory part cannot walk out of its folder. A key of any other shape returns
        // null rather than being guessed at.
        public static string? ResolvePhysicalPath(string? storageKey, string webRootPath)
        {
            if (string.IsNullOrWhiteSpace(storageKey) || string.IsNullOrWhiteSpace(webRootPath)) return null;

            var key = storageKey.Replace('\\', '/').Trim();
            if (key.Contains("://", StringComparison.Ordinal)) return null;

            if (key.StartsWith(PrivateKeyPrefix, StringComparison.Ordinal))
            {
                var name = Path.GetFileName(key.Substring(PrivateKeyPrefix.Length));
                if (string.IsNullOrWhiteSpace(name)) return null;
                return Path.Combine(PrivateStoreRoot(webRootPath), name);
            }

            if (key.StartsWith(UploadFolderUrl + "/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(key);
                if (string.IsNullOrWhiteSpace(name)) return null;
                return Path.Combine(webRootPath, "uploads", "comm", "threads", name);
            }

            return null;
        }

        // The content root is the web root's parent — that is what WebRootPath means — so the private store
        // sits beside App_Data rather than inside the served tree. Derived here instead of taken as another
        // parameter so the three existing StageAsync callers keep compiling unchanged.
        public static string PrivateStoreRoot(string webRootPath)
        {
            var contentRoot = Path.GetDirectoryName(webRootPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
            if (string.IsNullOrWhiteSpace(contentRoot)) contentRoot = webRootPath;
            return Path.Combine(contentRoot, "App_Data", PrivateStoreFolder);
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
                // OUTSIDE THE WEB ROOT (GAP-01). Bytes written here have no url at all, so there is no
                // static request that could reach them and no gate rule that has to be remembered. The
                // previous destination was wwwroot/uploads/comm/threads, where the file was served to any
                // signed-in caller who knew the path.
                var dir = PrivateStoreRoot(webRootPath);
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

                    // An OPAQUE key, not a path. Even if this value leaked it names nothing the browser
                    // can fetch; delivery goes through /Documents/comm/{id}/content, which authorizes.
                    StorageKey = PrivateKeyPrefix + stored,
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
