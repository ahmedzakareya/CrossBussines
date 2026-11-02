using System.Net;
using System.Net.Mail;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Comm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    public class MailListItem
    {
        public int Id { get; set; }
        public string ToAddress { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? Preview { get; set; }
        public string Status { get; set; } = "Queued";
        public bool Starred { get; set; }
        public bool HasAttachment { get; set; }
        public string Kind { get; set; } = "New";
        public DateTime? At { get; set; }

        // ---- WHO THIS WENT TO, WHEN WE KNOW THEM ------------------------------------------
        //
        // A mail row addresses an EMAIL, not an employee: most recipients are customers, vendors
        // or nobody on file. So this is null far more often than not, and the row falls back to
        // the coloured initial - which is the honest rendering of "we have no picture of this
        // address", not a placeholder waiting to be fixed.
        public string? RecipientPhoto { get; set; }
    }
    public class MailFull
    {
        public int Id { get; set; }
        public string ToAddress { get; set; } = "";
        public string? Cc { get; set; }
        public string Subject { get; set; } = "";
        public string? Body { get; set; }
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }
        public bool Starred { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? CreatedAt { get; set; }

        // The message's OWN state, so the screen can tell "delete" from "delete forever"
        // without being told which folder the user arrived from - a link can be shared,
        // bookmarked or reloaded, and a query-string folder would be a guess by then.
        public DateTime? DeletedAt { get; set; }

        /// THE SAME PICTURE THE LIST ALREADY SHOWS. The mailbox resolved a recipient's photo and the
        /// opened message did not, so the one screen that has room for a face fell back to a coloured
        /// letter while the cramped row beside it showed the person. Null stays the common answer -
        /// most addresses belong to nobody on file - and the initial is the honest rendering of that.
        public string? RecipientPhoto { get; set; }

        /// The meeting this message IS, when it carries one. Null for ordinary mail, and null for an
        /// invitation this reader cannot parse - the attachment chip still shows either way.
        public MailInvite? Invite { get; set; }

        public List<MailAttachmentDto> Attachments { get; set; } = new();
    }
    public class MailAttachmentDto { public string FilePath { get; set; } = ""; public string FileName { get; set; } = ""; public long Size { get; set; } }
    public record MailFile(string Path, string Name, long Size);

    public interface ICommService
    {
        Task<(int id, string status, string? error)> SendAsync(int companyId, int createdBy, string to, string? cc,
            string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind);
        Task<List<MailListItem>> ListAsync(int companyId, string folder, string? q);
        Task<(int all, int sent, int outbox, int failed, int trash, int starred)> CountsAsync(int companyId);
        Task<MailFull?> GetAsync(int companyId, int id);
        Task<bool> TrySendAsync(int id);

        /// Stage 0 (Slice-003): performs ONLY the SMTP transmission for an already-persisted message and returns
        /// the outcome. It does NOT touch Status/Attempts/SentAt/Error — the caller owns delivery state. This is
        /// what CommMessageDispatcherHostedService uses, so the outbox store stays the single writer of status
        /// (and Attempts is incremented exactly once, by the claim).
        Task<(bool ok, string? error)> TransmitAsync(int id, CancellationToken cancellationToken = default);
        // ---- DELETE, THE WAY A MAIL CLIENT MEANS IT -------------------------------------------
        //
        // Delete is TWO operations wearing one word, and which one you get depends on where you are
        // standing. From any folder it MOVES the message to Deleted Items and nothing is lost. From
        // inside Deleted Items it is final. That is the rule every mail client has taught people to
        // expect, and getting it wrong in either direction is the kind of mistake a user cannot undo.
        //
        // ALL FOUR TAKE A SET. A mailbox is a list and people act on lists - select six, delete six.
        // A per-row call in a loop would be six round trips and six chances to half-finish.
        Task<int> TrashAsync(int companyId, IReadOnlyCollection<int> ids);
        Task<int> RestoreAsync(int companyId, IReadOnlyCollection<int> ids);
        Task<int> DeleteForeverAsync(int companyId, IReadOnlyCollection<int> ids);
        Task<int> EmptyTrashAsync(int companyId);
        Task<bool> ToggleStarAsync(int companyId, int id);
        bool SmtpConfigured { get; }
    }

    public class CommService : ICommService
    {
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CommService> _log;
        private readonly SmtpOptions _smtp;

        public CommService(CrossDbContext db, IConfiguration cfg, IWebHostEnvironment env, ILogger<CommService> log)
        { _db = db; _env = env; _log = log; _smtp = new SmtpOptions(); cfg.GetSection("Smtp").Bind(_smtp); }

        public class SmtpOptions
        {
            public bool Enabled { get; set; }
            public string Host { get; set; } = ""; public int Port { get; set; } = 587;
            public string User { get; set; } = ""; public string Password { get; set; } = "";
            public string From { get; set; } = ""; public string FromName { get; set; } = "CrossBuy";
            public bool EnableSsl { get; set; } = true;
            public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
        }
        public bool SmtpConfigured => _smtp.IsConfigured;

        private static bool ValidEmail(string? s) { if (string.IsNullOrWhiteSpace(s)) return false; try { _ = new MailAddress(s.Trim()); return true; } catch { return false; } }

        public async Task<(int id, string status, string? error)> SendAsync(int companyId, int createdBy, string to, string? cc,
            string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind)
        {
            var m = new CommMessage
            {
                CompanyID = companyId, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
                ToAddress = (to ?? "").Trim(), Cc = string.IsNullOrWhiteSpace(cc) ? null : cc.Trim(),
                Subject = subject ?? "", Body = body, Status = "Queued",
                ParentId = parentId, Kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind,
            };
            // To may be a group (comma/semicolon separated) — valid if at least one address parses
            if (!m.ToAddress.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(ValidEmail))
            { m.Status = "Failed"; m.Error = "Invalid recipient address"; }
            _db.CommMessages.Add(m);
            await _db.SaveChangesAsync();
            if (attachments != null)
                foreach (var f in attachments)
                    if (!string.IsNullOrWhiteSpace(f.Path))
                        _db.CommAttachments.Add(new CommAttachment { CommMessageId = m.Id, FilePath = f.Path, FileName = f.Name, Size = f.Size, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
            if (m.Status != "Failed") await TrySendAsync(m.Id);
            var fresh = await _db.CommMessages.AsNoTracking().FirstAsync(x => x.Id == m.Id);
            return (m.Id, fresh.Status, fresh.Error);
        }

        // Unchanged behaviour: sends AND owns the status transition. Used by the interactive send path.
        public async Task<bool> TrySendAsync(int id)
        {
            var m = await _db.CommMessages.FirstOrDefaultAsync(x => x.Id == id);
            if (m == null || m.Status == "Sent") return m?.Status == "Sent";
            if (!_smtp.IsConfigured) { m.Status = "Queued"; await _db.SaveChangesAsync(); return false; }
            var (ok, error) = await TransmitAsync(id);
            if (ok)
            {
                m.Status = "Sent"; m.SentAt = DateTime.UtcNow; m.Error = null; m.Attempts++;
                await _db.SaveChangesAsync();
                return true;
            }
            m.Status = "Failed"; m.Error = error; m.Attempts++;
            await _db.SaveChangesAsync();
            return false;
        }

        // Stage 0: the wire transmission only. No status, no Attempts, no SaveChanges on the message row.
        public async Task<(bool ok, string? error)> TransmitAsync(int id, CancellationToken cancellationToken = default)
        {
            var m = await _db.CommMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (m == null) return (false, "Message not found");
            if (!_smtp.IsConfigured) return (false, "SMTP is not configured");
            try
            {
                using var msg = new MailMessage { From = new MailAddress(_smtp.From, _smtp.FromName), Subject = m.Subject };
                foreach (var t in m.ToAddress.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (ValidEmail(t)) msg.To.Add(t);
                if (!string.IsNullOrWhiteSpace(m.Cc))
                    foreach (var c in m.Cc.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (ValidEmail(c)) msg.CC.Add(c);

                // Pasted base64 images (data: URIs) are stripped by Gmail/Outlook. Convert each to an INLINE CID resource
                // so it actually renders in the received email. (The DB keeps the original HTML as-is — only the wire copy changes.)
                var html = m.Body ?? "";
                var linked = new List<LinkedResource>();
                html = System.Text.RegularExpressions.Regex.Replace(html,
                    "src=[\"'](data:(image/[a-zA-Z0-9.+-]+);base64,([^\"']+))[\"']",
                    mch =>
                    {
                        try
                        {
                            var media = mch.Groups[2].Value;
                            var bytes = Convert.FromBase64String(mch.Groups[3].Value);
                            var cid = Guid.NewGuid().ToString("N");
                            var lr = new LinkedResource(new System.IO.MemoryStream(bytes), media) { ContentId = cid };
                            lr.ContentType.MediaType = media;
                            linked.Add(lr);
                            return $"src=\"cid:{cid}\"";
                        }
                        catch { return mch.Value; }
                    });
                var htmlView = AlternateView.CreateAlternateViewFromString(html, System.Text.Encoding.UTF8, "text/html");
                foreach (var lr in linked) htmlView.LinkedResources.Add(lr);
                msg.AlternateViews.Add(htmlView);

                foreach (var a in await _db.CommAttachments.AsNoTracking().Where(x => x.CommMessageId == m.Id).ToListAsync())
                {
                    var phys = Path.Combine(_env.WebRootPath, a.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                    if (System.IO.File.Exists(phys)) msg.Attachments.Add(new System.Net.Mail.Attachment(phys) { Name = a.FileName });
                }
                using var client = new SmtpClient(_smtp.Host, _smtp.Port)
                {
                    EnableSsl = _smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network,
                    Credentials = string.IsNullOrWhiteSpace(_smtp.User) ? CredentialCache.DefaultNetworkCredentials : new NetworkCredential(_smtp.User, _smtp.Password),
                };
                await client.SendMailAsync(msg);
                return (true, null);
            }
            catch (Exception ex)
            {
                // Log the message id and the exception only — never the body, the recipient list or attachment bytes.
                _log.LogWarning(ex, "Mail {Id} transmission failed", m.Id);
                return (false, ex.Message.Length > 900 ? ex.Message[..900] : ex.Message);
            }
        }

        private static string? StatusOf(string folder) => folder switch { "Sent" => "Sent", "Outbox" => "Queued", "Failed" => "Failed", _ => null };

        // strip HTML tags → short plain-text snippet for the list preview (body is now rich HTML)
        private static string Plain(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            return text.Length > 100 ? text[..100] : text;
        }

        /// The photo for each of these addresses, in one round trip.
        ///
        /// ONE QUERY FOR THE PAGE, not one per row: a mailbox renders dozens of rows and a lookup
        /// inside the loop is the N+1 every list in this product is written to avoid.
        ///
        /// COMPANY-SCOPED, and that is not decoration. Matching on an email alone would let an
        /// address that also exists in another tenant put THAT employee's face on this company's
        /// mail - a small leak, and a confusing one.
        ///
        /// CASE- AND SPACE-INSENSITIVE, because an address typed by hand is not a key: the same
        /// person appears as "Ahmed.Zakarya@..." and "ahmed.zakarya@..." in the same mailbox.
        private async Task<Dictionary<string, string>> RecipientPhotosAsync(
            int companyId, IReadOnlyCollection<string> addresses)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (companyId <= 0 || addresses == null || addresses.Count == 0) return result;

            var wanted = addresses
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
            if (wanted.Count == 0) return result;

            var rows = await _db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == companyId
                         && e.Email != null
                         && e.ProfileImage != null
                         && e.ProfileImage != ""
                         && wanted.Contains(e.Email.Trim().ToLower()))
                .Select(e => new { e.Email, e.ProfileImage })
                .ToListAsync();

            foreach (var r in rows)
            {
                var key = r.Email!.Trim();
                if (!result.ContainsKey(key)) result[key] = r.ProfileImage!;
            }
            return result;
        }

        public async Task<List<MailListItem>> ListAsync(int companyId, string folder, string? q)
        {
            var query = _db.CommMessages.AsNoTracking().Where(x => x.CompanyID == companyId);
            query = folder == "Trash" ? query.Where(x => x.DeletedAt != null) : query.Where(x => x.DeletedAt == null);
            if (folder == "Starred") query = query.Where(x => x.Starred);
            var st = StatusOf(folder); if (st != null) query = query.Where(x => x.Status == st);
            if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.ToAddress.Contains(t) || x.Subject.Contains(t)); }
            var rows = await query.OrderByDescending(x => x.Id).Take(200)
                .Select(x => new { x.Id, x.ToAddress, x.Subject, x.Body, x.Status, x.Starred, x.Kind, x.SentAt, x.CreatedAt }).ToListAsync();
            var ids = rows.Select(r => r.Id).ToList();
            var withAtt = await _db.CommAttachments.AsNoTracking().Where(a => ids.Contains(a.CommMessageId)).Select(a => a.CommMessageId).Distinct().ToListAsync();
            var set = withAtt.ToHashSet();

            // One lookup for the whole page; most addresses will not be on file and that is normal.
            var photos = await RecipientPhotosAsync(companyId, rows.Select(r => r.ToAddress).ToList());

            return rows.Select(r => new MailListItem
            {
                Id = r.Id, ToAddress = r.ToAddress, Subject = r.Subject,
                Preview = Plain(r.Body),
                Status = r.Status, Starred = r.Starred, HasAttachment = set.Contains(r.Id), Kind = r.Kind, At = r.SentAt ?? r.CreatedAt,
                RecipientPhoto = r.ToAddress != null && photos.TryGetValue(r.ToAddress.Trim(), out var p) ? p : null,
            }).ToList();
        }

        public async Task<(int all, int sent, int outbox, int failed, int trash, int starred)> CountsAsync(int companyId)
        {
            var q = _db.CommMessages.AsNoTracking().Where(x => x.CompanyID == companyId);
            var live = q.Where(x => x.DeletedAt == null);
            return (
                await live.CountAsync(),
                await live.CountAsync(x => x.Status == "Sent"),
                await live.CountAsync(x => x.Status == "Queued"),
                await live.CountAsync(x => x.Status == "Failed"),
                await q.CountAsync(x => x.DeletedAt != null),
                await live.CountAsync(x => x.Starred)
            );
        }

        public async Task<MailFull?> GetAsync(int companyId, int id)
        {
            var x = await _db.CommMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.CompanyID == companyId);
            if (x == null) return null;

            var full = new MailFull
            {
                Id = x.Id, ToAddress = x.ToAddress, Cc = x.Cc, Subject = x.Subject, Body = x.Body, Status = x.Status,
                Error = x.Error, Starred = x.Starred, SentAt = x.SentAt, CreatedAt = x.CreatedAt,
                DeletedAt = x.DeletedAt,
                Attachments = await _db.CommAttachments.AsNoTracking().Where(a => a.CommMessageId == x.Id)
                    .Select(a => new MailAttachmentDto { FilePath = a.FilePath, FileName = a.FileName, Size = a.Size }).ToListAsync(),
            };

            // Through the SAME lookup the list uses, so the picture cannot differ between the row and
            // the message it opens - two resolutions of "who is this address" would eventually
            // disagree, and the one on the detail screen is the one people would believe.
            var photos = await RecipientPhotosAsync(companyId, new[] { x.ToAddress });
            full.RecipientPhoto = photos.TryGetValue(x.ToAddress ?? "", out var p) ? p : null;

            full.Invite = await ReadInviteAsync(full.Attachments);
            return full;
        }

        /// The first .ics on the message, parsed. ONE file only: a message carrying two invitations
        /// is not something this product sends, and drawing two cards would raise the question of
        /// which one the "when" belongs to.
        ///
        /// READ FROM DISK, not from the database, because that is where the bytes are. A missing file
        /// - swept, moved, written by another instance - yields no card rather than an error.
        private async Task<MailInvite?> ReadInviteAsync(List<MailAttachmentDto> attachments)
        {
            var ics = attachments.FirstOrDefault(a =>
                a.FileName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)
                || a.FilePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase));
            if (ics == null) return null;

            try
            {
                var phys = Path.Combine(_env.WebRootPath,
                    ics.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                if (!System.IO.File.Exists(phys)) return null;

                // Bounded: an invitation is a few hundred bytes and this runs on a page render.
                var info = new FileInfo(phys);
                if (info.Length > 256 * 1024) return null;

                return MailInvite.TryParse(await System.IO.File.ReadAllTextAsync(phys));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // ---- MOVE TO DELETED ITEMS ----------------------------------------------------------
        //
        // Reversible by construction: the row stays and only DeletedAt is stamped, which is the same
        // flag the Trash folder selects on. Nothing is read from disk and no file is touched.
        //
        // ALREADY-TRASHED IDS ARE SKIPPED rather than re-stamped. Re-stamping would move a message's
        // deletion date forward every time it was selected, and a retention sweep that one day reads
        // that date would keep the oldest rubbish the longest.
        public async Task<int> TrashAsync(int companyId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, ids, trashed: false);
            if (rows.Count == 0) return 0;

            var now = DateTime.UtcNow;
            foreach (var m in rows) m.DeletedAt = now;
            await _db.SaveChangesAsync();
            return rows.Count;
        }

        /// Put it back where it was. Only a trashed message can be restored, so a double-click cannot
        /// un-delete something that was never deleted.
        public async Task<int> RestoreAsync(int companyId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, ids, trashed: true);
            if (rows.Count == 0) return 0;

            foreach (var m in rows) m.DeletedAt = null;
            await _db.SaveChangesAsync();
            return rows.Count;
        }

        // ---- DELETE FOREVER ------------------------------------------------------------------
        //
        // ONLY FROM DELETED ITEMS, and the predicate says so rather than the caller promising it. A
        // permanent delete that could be reached from the inbox would be one mis-posted parameter away
        // from destroying live mail.
        //
        // THE ATTACHMENTS GO WITH IT - rows AND the bytes on disk. Leaving the files behind would turn
        // every permanent delete into a slow leak of exactly the material the user asked to be rid of.
        //
        // A FILE THAT WILL NOT DELETE DOES NOT ABORT THE DELETE. It is logged and the message still
        // goes: a locked or already-missing file must not leave the user staring at mail they have
        // twice asked to remove. The rows are the record; the file is a copy of it.
        public async Task<int> DeleteForeverAsync(int companyId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, ids, trashed: true);
            return rows.Count == 0 ? 0 : await PurgeAsync(rows);
        }

        /// Outlook's "Empty folder": everything currently in Deleted Items, for this company only.
        public async Task<int> EmptyTrashAsync(int companyId)
        {
            if (companyId <= 0) return 0;
            var rows = await _db.CommMessages
                .Where(x => x.CompanyID == companyId && x.DeletedAt != null)
                .ToListAsync();
            return rows.Count == 0 ? 0 : await PurgeAsync(rows);
        }

        // ---- the two halves every operation above shares -------------------------------------

        /// THE TENANT AND THE FOLDER ARE BOTH IN THE PREDICATE, and neither is the caller's to assert.
        /// An id is only ever a lookup key: a message belonging to another company is not found here,
        /// so no amount of guessing ids reaches it.
        private async Task<List<CommMessage>> ScopedAsync(
            int companyId, IReadOnlyCollection<int> ids, bool trashed)
        {
            if (companyId <= 0 || ids == null || ids.Count == 0) return new List<CommMessage>();

            var wanted = ids.Where(i => i > 0).Distinct().ToList();
            if (wanted.Count == 0) return new List<CommMessage>();

            return await _db.CommMessages
                .Where(x => x.CompanyID == companyId
                         && wanted.Contains(x.Id)
                         && (trashed ? x.DeletedAt != null : x.DeletedAt == null))
                .ToListAsync();
        }

        private async Task<int> PurgeAsync(List<CommMessage> rows)
        {
            var messageIds = rows.Select(m => m.Id).ToList();
            var attachments = await _db.CommAttachments
                .Where(a => messageIds.Contains(a.CommMessageId))
                .ToListAsync();

            foreach (var a in attachments)
            {
                // Resolved exactly as the sender resolves it, so the two cannot disagree about where
                // a stored path lives on disk.
                var phys = Path.Combine(_env.WebRootPath,
                    a.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (System.IO.File.Exists(phys)) System.IO.File.Delete(phys);
                }
                catch (IOException ex)
                {
                    _log.LogWarning(ex, "Mail attachment {Path} could not be removed; the message is "
                        + "still deleted and the file is orphaned.", a.FilePath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _log.LogWarning(ex, "Mail attachment {Path} could not be removed; the message is "
                        + "still deleted and the file is orphaned.", a.FilePath);
                }
            }

            _db.CommAttachments.RemoveRange(attachments);
            _db.CommMessages.RemoveRange(rows);
            await _db.SaveChangesAsync();
            return rows.Count;
        }
        public async Task<bool> ToggleStarAsync(int companyId, int id)
        {
            var m = await _db.CommMessages.FirstOrDefaultAsync(x => x.Id == id && x.CompanyID == companyId);
            if (m == null) return false;
            m.Starred = !m.Starred; await _db.SaveChangesAsync(); return true;
        }
    }
}
