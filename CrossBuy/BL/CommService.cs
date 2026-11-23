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

        // ---- THE READER'S OWN COPY (owner, 2026-11-23: a mailbox per employee) ----------------
        /// Inbox | Sent — which of the reader's boxes this row came from; null in the company outbox.
        public string? Box { get; set; }
        public bool Unread { get; set; }
        /// Who wrote it: the colleague's name and photo, or the system's name for automated mail.
        public string? FromName { get; set; }
        public string? FromAddress { get; set; }
        public string? FromPhoto { get; set; }
        /// Written outside the company and received here (phase 2).
        public bool External { get; set; }
    }

    /// The folder badges of one person's mailbox. Company is the outbox of the whole company, shown only to whoever
    /// manages it (outbox-manage); null for everybody else.
    public class MailCounts
    {
        public int Inbox { get; set; }
        public int Unread { get; set; }
        public int All { get; set; }
        public int Sent { get; set; }
        public int Outbox { get; set; }
        public int Failed { get; set; }
        public int Trash { get; set; }
        public int Starred { get; set; }
        public int? Company { get; set; }
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

        /// The reader's copy: Inbox | Sent, or null when a manager opened it from the company outbox.
        public string? Box { get; set; }
        public string? FromName { get; set; }
        public string? FromAddress { get; set; }
        public string? FromPhoto { get; set; }
        public int? FromEmployeeId { get; set; }
        /// Written outside the company and received here (phase 2).
        public bool External { get; set; }
        public int? ThreadId { get; set; }

        public List<MailAttachmentDto> Attachments { get; set; } = new();
    }
    public class MailAttachmentDto { public string FilePath { get; set; } = ""; public string FileName { get; set; } = ""; public long Size { get; set; } }
    public record MailFile(string Path, string Name, long Size);

    /// One message of a conversation, as the message screen draws it above the one being read (phase 3).
    public class MailThreadItem
    {
        public int Id { get; set; }
        public string? FromName { get; set; }
        public string? FromAddress { get; set; }
        public string? FromPhoto { get; set; }
        public string ToAddress { get; set; } = "";
        public string? Preview { get; set; }
        public string? Body { get; set; }
        public DateTime? At { get; set; }
        public bool External { get; set; }
        public bool Mine { get; set; }
        public int Attachments { get; set; }
    }

    public interface ICommService
    {
        Task<(int id, string status, string? error)> SendAsync(int companyId, int createdBy, string to, string? cc,
            string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind);

        /// Persists a message as Queued and does NOT transmit. SendAsync reaches SMTP; an automated
        /// producer must not, because this deployment has a live provider configured. Delivery stays with
        /// CommMessageDispatcherHostedService, which owns the status transition.
        Task<(int id, string status, string? error)> QueueAsync(int companyId, int createdBy, string to, string? cc,
            string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind);
        // ---- A MAILBOX PER EMPLOYEE (owner, 2026-11-23) ------------------------------------------
        // Every read below takes the reader. A person sees the messages they wrote (Sent) and the ones delivered to
        // them (Inbox) — never a colleague's. The company outbox ("Company") is the one exception, and the CALLER
        // decides it (outbox-manage); the service only obeys `manager`.
        Task<List<MailListItem>> ListAsync(int companyId, int employeeId, string folder, string? q, bool manager = false);
        Task<MailCounts> CountsAsync(int companyId, int employeeId, bool manager = false);
        /// The message, if the reader holds a copy of it (or manages the company outbox). markRead stamps their Inbox copy.
        Task<MailFull?> GetAsync(int companyId, int employeeId, int id, bool manager = false, bool markRead = false);
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
        //
        // ALL OF THEM ACT ON THE READER'S OWN COPIES. Deleting a message you received takes it from YOUR mailbox;
        // the sender and the other recipients keep theirs. The message row itself goes only when nobody holds it.
        Task<int> TrashAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids);
        Task<int> RestoreAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids);
        Task<int> DeleteForeverAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids);
        Task<int> EmptyTrashAsync(int companyId, int employeeId);
        Task<bool> ToggleStarAsync(int companyId, int employeeId, int id);
        /// May this person send this message again? Only the one who wrote it (or the outbox's manager).
        Task<bool> IsSenderAsync(int companyId, int employeeId, int id);
        /// THE CONVERSATION (phase 3): every message of a thread the reader holds a copy of (or all of them, for the
        /// outbox's manager), oldest first.
        Task<List<MailThreadItem>> ThreadAsync(int companyId, int employeeId, int threadId, bool manager = false);
        bool SmtpConfigured { get; }
        /// The employee's own address for mail from outside the company (phase 2), or null when mail is not set up.
        string? ExternalAddressFor(int employeeId);
    }

    public class CommService : ICommService
    {
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CommService> _log;
        private readonly SmtpOptions _smtp;

        private readonly INotificationService? _notify;

        public CommService(CrossDbContext db, IConfiguration cfg, IWebHostEnvironment env, ILogger<CommService> log,
            INotificationService? notify = null)
        { _db = db; _env = env; _log = log; _notify = notify; _smtp = new SmtpOptions(); cfg.GetSection("Smtp").Bind(_smtp); }

        private IQueryable<CommMailboxItem> Items => _db.Set<CommMailboxItem>();

        private static List<string> Addresses(string? to, string? cc) =>
            ((to ?? "") + "," + (cc ?? "")).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(ValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

        /// This employee's address for mail from outside (phase 2): the company account with their tag. Null without SMTP.
        public string? ExternalAddressFor(int employeeId) =>
            _smtp.IsConfigured && employeeId > 0 ? CrossBuy.BL.Comm.MailTags.ForEmployee(_smtp, employeeId) : null;

        private static bool ValidEmail(string? s) { if (string.IsNullOrWhiteSpace(s)) return false; try { _ = new MailAddress(s.Trim()); return true; } catch { return false; } }

        // QUEUE WITHOUT TRANSMITTING.
        //
        // SendAsync persists AND calls TrySendAsync, so it reaches SMTP. Any automated producer using it
        // would send real mail the moment it ran — and this deployment has a live provider configured.
        // This method is the persist half on its own, leaving delivery to whoever owns delivery state:
        // CommMessageDispatcherHostedService, which claims the row and calls TransmitAsync.
        //
        // SendAsync now routes through it, so the interactive path is unchanged in behaviour and there is
        // only one place that knows how a CommMessage is built.
        public async Task<(int id, string status, string? error)> QueueAsync(
            int companyId, int createdBy, string to, string? cc, string subject, string body,
            IEnumerable<MailFile>? attachments, int? parentId, string kind)
        {
            var m = new CommMessage
            {
                CompanyID = companyId, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
                ToAddress = (to ?? "").Trim(), Cc = string.IsNullOrWhiteSpace(cc) ? null : cc.Trim(),
                Subject = subject ?? "", Body = body, Status = "Queued",
                ParentId = parentId, Kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind,
            };
            if (!m.ToAddress.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(ValidEmail))
            { m.Status = "Failed"; m.Error = "Invalid recipient address"; }

            // THE THREAD: a reply or a forward joins its parent's conversation; anything else starts one.
            if (parentId is int pid)
                m.ThreadId = await _db.CommMessages.AsNoTracking().Where(x => x.Id == pid && x.CompanyID == companyId)
                    .Select(x => x.ThreadId ?? (int?)x.Id).FirstOrDefaultAsync();

            // ---- DELIVERED INSIDE THE SYSTEM (owner, 2026-11-23: "nothing external") ------------
            // An address that belongs to an active employee of this company is that person's mailbox HERE: the
            // message lands in their Inbox and is never handed to SMTP. Only the addresses left over — customers,
            // vendors, anyone outside — go out through the company's mail server, as before. A message with no
            // outside address is therefore delivered the moment it is saved.
            var now = DateTime.UtcNow;
            var addrs = m.Status == "Failed" ? new List<string>() : Addresses(m.ToAddress, m.Cc);
            var lower = addrs.Select(a => a.ToLowerInvariant()).ToList();
            var colleagues = lower.Count == 0 ? new List<(int id, string email)>()
                : (await _db.Employee.AsNoTracking()
                    .Where(e => e.EmpCompanyID == companyId && e.IsActive && e.Email != null && lower.Contains(e.Email.Trim().ToLower()))
                    .Select(e => new { e.ID, e.Email }).ToListAsync())
                    .GroupBy(e => e.ID).Select(g => (id: g.Key, email: g.First().Email.Trim())).ToList();
            var inside = colleagues.Select(c => c.email.ToLowerInvariant()).ToHashSet();
            if (m.Status != "Failed" && addrs.Count > 0 && addrs.All(a => inside.Contains(a.ToLowerInvariant())))
            { m.Status = "Sent"; m.SentAt = now; }

            _db.CommMessages.Add(m);
            await _db.SaveChangesAsync();
            if (m.ThreadId == null) m.ThreadId = m.Id;
            if (attachments != null)
                foreach (var f in attachments)
                    if (!string.IsNullOrWhiteSpace(f.Path))
                        _db.CommAttachments.Add(new CommAttachment { CommMessageId = m.Id, FilePath = f.Path, FileName = f.Name, Size = f.Size, CreatedAt = now });

            var senderIsColleague = createdBy > 0 && await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == createdBy && e.EmpCompanyID == companyId);
            if (senderIsColleague)
                _db.Set<CommMailboxItem>().Add(new CommMailboxItem { CompanyID = companyId, MessageId = m.Id, EmployeeId = createdBy, Box = "Sent", ReadAt = now, CreatedAt = now });
            foreach (var (id, email) in colleagues)
                _db.Set<CommMailboxItem>().Add(new CommMailboxItem { CompanyID = companyId, MessageId = m.Id, EmployeeId = id, Box = "Inbox", Address = email, CreatedAt = now });
            await _db.SaveChangesAsync();

            if (colleagues.Count > 0) await AnnounceAsync(companyId, createdBy, m, colleagues.Select(c => c.id));
            return (m.Id, m.Status, m.Error);
        }

        /// The bell (and its live push) tells each colleague a message has arrived; the click opens it. A failure here
        /// is logged and swallowed: the message is already in their Inbox, which is the delivery — the bell is news of it.
        private async Task AnnounceAsync(int companyId, int createdBy, CommMessage m, IEnumerable<int> recipients)
        {
            if (_notify == null) return;
            try
            {
                var who = createdBy > 0
                    ? await _db.Employee.AsNoTracking().Where(e => e.ID == createdBy).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync()
                    : null;
                var ar = who?.FullName ?? _smtp.FromName;
                var en = string.IsNullOrWhiteSpace(who?.FullNameEn) ? ar : who!.FullNameEn!;
                var subject = string.IsNullOrWhiteSpace(m.Subject) ? "—" : (m.Subject.Length > 120 ? m.Subject[..120] + "…" : m.Subject);
                foreach (var r in recipients.Distinct())
                {
                    if (r == createdBy) continue;          // writing to yourself is not news
                    await _notify.NotifyAsync(r, "رسالة جديدة من " + ar, "New mail from " + en, subject, subject,
                        NotificationTypes.MailReceived, m.Id, "/Comm/View/" + m.Id, companyId, createdBy > 0 ? createdBy : null,
                        entityType: null, entityId: null);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Mail {Id}: the arrival notification failed; the message is delivered", m.Id); }
        }

        public async Task<(int id, string status, string? error)> SendAsync(int companyId, int createdBy, string to, string? cc,
            string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind)
        {
            var queued = await QueueAsync(companyId, createdBy, to, cc, subject, body, attachments, parentId, kind);
            if (queued.status != "Failed") await TrySendAsync(queued.id);
            var sent = await _db.CommMessages.AsNoTracking().FirstAsync(x => x.Id == queued.id);
            return (queued.id, sent.Status, sent.Error);
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
            // THE COLLEAGUES ALREADY HAVE IT. Addresses delivered to a mailbox inside the system are left off the wire:
            // SMTP carries only what is addressed outside the company. Nothing left = nothing to transmit.
            var delivered = (await _db.Set<CommMailboxItem>().AsNoTracking()
                    .Where(i => i.MessageId == id && i.Box == "Inbox" && i.Address != null)
                    .Select(i => i.Address!).ToListAsync(cancellationToken))
                .Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
            bool outside(string a) => ValidEmail(a) && !delivered.Contains(a.Trim().ToLowerInvariant());
            string[] split(string? s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var to = split(m.ToAddress).Where(outside).ToList();
            var cc = split(m.Cc).Where(outside).ToList();
            if (to.Count == 0 && cc.Count == 0) return (true, null);

            if (!_smtp.IsConfigured) return (false, "SMTP is not configured");
            try
            {
                // FROM THE PERSON, THROUGH THE COMPANY'S SERVER. The server only relays its own account, so the address
                // stays the configured one; the NAME is the colleague who wrote it.
                var sender = m.CreatedBy is int by && by > 0
                    ? await _db.Employee.AsNoTracking().Where(e => e.ID == by && e.EmpCompanyID == m.CompanyID)
                        .Select(e => new { e.FullName, e.FullNameEn, e.Email }).FirstOrDefaultAsync(cancellationToken)
                    : null;
                var fromName = string.IsNullOrWhiteSpace(sender?.FullName) ? _smtp.FromName : sender!.FullName;
                using var msg = new MailMessage { From = new MailAddress(_smtp.From, fromName, System.Text.Encoding.UTF8), Subject = m.Subject, SubjectEncoding = System.Text.Encoding.UTF8 };

                // THE ANSWER COMES BACK HERE (phase 2). Reply-To is the company account with this message's tag, so a
                // reply lands in the writer's Inbox, in this conversation (CommInboundService). Message-ID names this
                // message; a reply to mail from outside carries In-Reply-To/References so the other side threads it.
                var replyTo = sender != null ? CrossBuy.BL.Comm.MailTags.ForMessage(_smtp, m.Id) : null;
                if (replyTo != null) msg.ReplyToList.Add(new MailAddress(replyTo, fromName, System.Text.Encoding.UTF8));
                msg.Headers.Add("Message-ID", CrossBuy.BL.Comm.MailTags.MessageId(_smtp, m.Id));
                if (m.ParentId is int parentId)
                {
                    var p = await _db.CommMessages.AsNoTracking().Where(x => x.Id == parentId && x.CompanyID == m.CompanyID)
                        .Select(x => new { x.Id, x.ExternalMessageId }).FirstOrDefaultAsync(cancellationToken);
                    if (p != null)
                    {
                        var re = p.ExternalMessageId ?? CrossBuy.BL.Comm.MailTags.MessageId(_smtp, p.Id);
                        msg.Headers.Add("In-Reply-To", re);
                        msg.Headers.Add("References", re);
                    }
                }
                foreach (var t in to) msg.To.Add(t);
                foreach (var c in cc) msg.CC.Add(c);

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

        // ---- ONE PERSON'S MAILBOX ------------------------------------------------------------------
        //
        // A folder is a question about the reader's own copies (CommMailboxItems), never about the company's table:
        //   Inbox   — delivered to me          Sent    — I wrote it and it went
        //   Outbox  — I wrote it, still going  Failed  — I wrote it and it did not go
        //   Starred / All — my copies, not deleted          Trash — my copies I deleted
        //   Company — every message of the company (the outbox), for whoever manages it; the caller decides that.
        private IQueryable<CommMailboxItem> MineIn(int companyId, int employeeId, string folder)
        {
            var mine = Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.EmployeeId == employeeId);
            var msgs = _db.CommMessages.AsNoTracking();
            return folder switch
            {
                "Trash" => mine.Where(i => i.DeletedAt != null),
                "Starred" => mine.Where(i => i.DeletedAt == null && i.Starred),
                "All" => mine.Where(i => i.DeletedAt == null),
                "Sent" => mine.Where(i => i.DeletedAt == null && i.Box == "Sent" && msgs.Any(m => m.Id == i.MessageId && m.Status == "Sent")),
                "Outbox" => mine.Where(i => i.DeletedAt == null && i.Box == "Sent" && msgs.Any(m => m.Id == i.MessageId && (m.Status == "Queued" || m.Status == "Claimed"))),
                "Failed" => mine.Where(i => i.DeletedAt == null && i.Box == "Sent" && msgs.Any(m => m.Id == i.MessageId && m.Status == "Failed")),
                _ => mine.Where(i => i.DeletedAt == null && i.Box == "Inbox"),
            };
        }

        private sealed record Person(int Id, string Name, string? Email, string? Photo);

        private async Task<Dictionary<int, Person>> PeopleAsync(int companyId, IEnumerable<int?> ids)
        {
            var want = ids.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
            if (want.Count == 0) return new();
            var ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var rows = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && want.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.Email, e.ProfileImage }).ToListAsync();
            return rows.ToDictionary(e => e.ID, e => new Person(e.ID,
                DisplayName.Or(ar ? e.FullName : e.FullNameEn, ar ? e.FullNameEn : e.FullName) ?? "",
                e.Email, string.IsNullOrWhiteSpace(e.ProfileImage) ? null : e.ProfileImage));
        }

        public async Task<List<MailListItem>> ListAsync(int companyId, int employeeId, string folder, string? q, bool manager = false)
        {
            folder = string.IsNullOrWhiteSpace(folder) ? "Inbox" : folder;
            var t = (q ?? "").Trim();
            List<(CommMessage m, CommMailboxItem? i)> page;
            if (folder == "Company")
            {
                if (!manager) return new();
                var query = _db.CommMessages.AsNoTracking().Where(x => x.CompanyID == companyId && x.DeletedAt == null);
                if (t.Length > 0) query = query.Where(x => x.ToAddress.Contains(t) || x.Subject.Contains(t) || (x.Body != null && x.Body.Contains(t)));
                page = (await query.OrderByDescending(x => x.Id).Take(200).ToListAsync()).Select(m => (m, (CommMailboxItem?)null)).ToList();
            }
            else
            {
                var items = MineIn(companyId, employeeId, folder);
                var rows = from i in items
                           join m in _db.CommMessages.AsNoTracking() on i.MessageId equals m.Id
                           select new { i, m };
                // THE SEARCH READS THE WORDS TOO: subject, address, sender and the body.
                if (t.Length > 0)
                {
                    var who = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId
                        && (e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t)))).Select(e => (int?)e.ID);
                    rows = rows.Where(r => r.m.ToAddress.Contains(t) || r.m.Subject.Contains(t) || (r.m.Body != null && r.m.Body.Contains(t))
                                        || (r.m.FromAddress != null && r.m.FromAddress.Contains(t)) || (r.m.FromName != null && r.m.FromName.Contains(t))
                                        || who.Contains(r.m.CreatedBy));
                }
                var got = await rows.OrderByDescending(r => r.m.Id).ThenBy(r => r.i.Box).Take(400).ToListAsync();
                // A message I sent to myself is in both boxes; the list shows it once (the Inbox copy, which can be unread).
                page = got.GroupBy(r => r.m.Id).Select(g => (g.First().m, (CommMailboxItem?)(g.FirstOrDefault(r => r.i.Box == "Inbox")?.i ?? g.First().i)))
                          .Take(200).ToList();
            }

            var ids = page.Select(p => p.m.Id).ToList();
            var withAtt = (await _db.CommAttachments.AsNoTracking().Where(a => ids.Contains(a.CommMessageId)).Select(a => a.CommMessageId).Distinct().ToListAsync()).ToHashSet();
            var photos = await RecipientPhotosAsync(companyId, page.Select(p => p.m.ToAddress).ToList());
            var people = await PeopleAsync(companyId, page.Select(p => p.m.CreatedBy));

            return page.Select(p =>
            {
                people.TryGetValue(p.m.CreatedBy ?? 0, out var from);
                return new MailListItem
                {
                    Id = p.m.Id, ToAddress = p.m.ToAddress, Subject = p.m.Subject,
                    Preview = Plain(p.m.Body),
                    Status = p.m.Status, Kind = p.m.Kind, At = p.m.SentAt ?? p.m.CreatedAt,
                    Starred = p.i?.Starred ?? p.m.Starred,
                    HasAttachment = withAtt.Contains(p.m.Id),
                    RecipientPhoto = p.m.ToAddress != null && photos.TryGetValue(p.m.ToAddress.Trim(), out var ph) ? ph : null,
                    Box = p.i?.Box,
                    Unread = p.i != null && p.i.Box == "Inbox" && p.i.ReadAt == null,
                    // A colleague, or — for mail from outside — the name and address it arrived with.
                    FromName = from?.Name ?? (p.m.Direction == "In" ? (p.m.FromName ?? p.m.FromAddress) : _smtp.FromName),
                    FromAddress = from?.Email ?? (p.m.Direction == "In" ? p.m.FromAddress : _smtp.From), FromPhoto = from?.Photo,
                    External = p.m.Direction == "In",
                };
            }).ToList();
        }

        public async Task<MailCounts> CountsAsync(int companyId, int employeeId, bool manager = false)
        {
            var c = new MailCounts
            {
                Inbox = await MineIn(companyId, employeeId, "Inbox").CountAsync(),
                Unread = await MineIn(companyId, employeeId, "Inbox").CountAsync(i => i.ReadAt == null),
                All = await MineIn(companyId, employeeId, "All").Select(i => i.MessageId).Distinct().CountAsync(),
                Sent = await MineIn(companyId, employeeId, "Sent").CountAsync(),
                Outbox = await MineIn(companyId, employeeId, "Outbox").CountAsync(),
                Failed = await MineIn(companyId, employeeId, "Failed").CountAsync(),
                Trash = await MineIn(companyId, employeeId, "Trash").Select(i => i.MessageId).Distinct().CountAsync(),
                Starred = await MineIn(companyId, employeeId, "Starred").Select(i => i.MessageId).Distinct().CountAsync(),
            };
            if (manager) c.Company = await _db.CommMessages.AsNoTracking().CountAsync(x => x.CompanyID == companyId && x.DeletedAt == null);
            return c;
        }

        public async Task<MailFull?> GetAsync(int companyId, int employeeId, int id, bool manager = false, bool markRead = false)
        {
            var x = await _db.CommMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.CompanyID == companyId);
            if (x == null) return null;

            // THE READER MUST HOLD A COPY. A message id is a lookup key, not a permission: a colleague's mail is
            // "not found", exactly like another company's.
            var mine = await Items.Where(i => i.MessageId == id && i.EmployeeId == employeeId && i.CompanyID == companyId).ToListAsync();
            if (mine.Count == 0 && !manager) return null;
            var copy = mine.FirstOrDefault(i => i.Box == "Inbox") ?? mine.FirstOrDefault();
            if (markRead && copy is { Box: "Inbox", ReadAt: null })
            {
                copy.ReadAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            var people = await PeopleAsync(companyId, new[] { x.CreatedBy });
            people.TryGetValue(x.CreatedBy ?? 0, out var from);

            var full = new MailFull
            {
                Id = x.Id, ToAddress = x.ToAddress, Cc = x.Cc, Subject = x.Subject, Body = x.Body, Status = x.Status,
                Error = x.Error, Starred = copy?.Starred ?? x.Starred, SentAt = x.SentAt, CreatedAt = x.CreatedAt,
                DeletedAt = copy != null ? copy.DeletedAt : x.DeletedAt,
                Box = copy?.Box, ThreadId = x.ThreadId ?? x.Id,
                FromName = from?.Name ?? (x.Direction == "In" ? (x.FromName ?? x.FromAddress) : _smtp.FromName),
                FromAddress = from?.Email ?? (x.Direction == "In" ? x.FromAddress : _smtp.From), FromPhoto = from?.Photo,
                External = x.Direction == "In",
                FromEmployeeId = from?.Id,
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

        public async Task<List<MailThreadItem>> ThreadAsync(int companyId, int employeeId, int threadId, bool manager = false)
        {
            if (companyId <= 0 || threadId <= 0) return new();
            var q = _db.CommMessages.AsNoTracking().Where(m => m.CompanyID == companyId && (m.ThreadId == threadId || m.Id == threadId));
            // ONLY WHAT THE READER HOLDS: a colleague's side of a conversation they were not copied on stays theirs.
            if (!manager)
                q = q.Where(m => Items.Any(i => i.MessageId == m.Id && i.EmployeeId == employeeId && i.CompanyID == companyId && i.DeletedAt == null));
            var rows = await q.OrderBy(m => m.SentAt ?? m.CreatedAt).ThenBy(m => m.Id).Take(60)
                .Select(m => new { m.Id, m.CreatedBy, m.Direction, m.FromName, m.FromAddress, m.ToAddress, m.Body, m.SentAt, m.CreatedAt }).ToListAsync();
            var ids = rows.Select(r => r.Id).ToList();
            var files = await _db.CommAttachments.AsNoTracking().Where(a => ids.Contains(a.CommMessageId))
                .GroupBy(a => a.CommMessageId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
            var people = await PeopleAsync(companyId, rows.Select(r => r.CreatedBy));
            return rows.Select(r =>
            {
                people.TryGetValue(r.CreatedBy ?? 0, out var from);
                return new MailThreadItem
                {
                    Id = r.Id, ToAddress = r.ToAddress, Body = r.Body, Preview = Plain(r.Body), At = r.SentAt ?? r.CreatedAt,
                    FromName = from?.Name ?? (r.Direction == "In" ? (r.FromName ?? r.FromAddress) : _smtp.FromName),
                    FromAddress = from?.Email ?? (r.Direction == "In" ? r.FromAddress : _smtp.From), FromPhoto = from?.Photo,
                    External = r.Direction == "In", Mine = r.CreatedBy == employeeId,
                    Attachments = files.TryGetValue(r.Id, out var n) ? n : 0,
                };
            }).ToList();
        }

        public Task<bool> IsSenderAsync(int companyId, int employeeId, int id) =>
            Items.AsNoTracking().AnyAsync(i => i.CompanyID == companyId && i.EmployeeId == employeeId && i.MessageId == id && i.Box == "Sent");

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
        // Reversible by construction: only the reader's copies are stamped (DeletedAt), which is the flag the Trash
        // folder selects on. The sender and the other recipients keep theirs.
        //
        // ALREADY-TRASHED COPIES ARE SKIPPED rather than re-stamped. Re-stamping would move a deletion date forward
        // every time it was selected, and a retention sweep that one day reads that date would keep the oldest
        // rubbish the longest.
        public async Task<int> TrashAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, employeeId, ids, trashed: false);
            if (rows.Count == 0) return 0;

            var now = DateTime.UtcNow;
            foreach (var i in rows) i.DeletedAt = now;
            await _db.SaveChangesAsync();
            return rows.Select(i => i.MessageId).Distinct().Count();
        }

        /// Put it back where it was. Only a trashed copy can be restored, so a double-click cannot un-delete
        /// something that was never deleted.
        public async Task<int> RestoreAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, employeeId, ids, trashed: true);
            if (rows.Count == 0) return 0;

            foreach (var i in rows) i.DeletedAt = null;
            await _db.SaveChangesAsync();
            return rows.Select(i => i.MessageId).Distinct().Count();
        }

        // ---- DELETE FOREVER ------------------------------------------------------------------
        //
        // ONLY FROM DELETED ITEMS, and the predicate says so rather than the caller promising it. A permanent delete
        // that could be reached from the inbox would be one mis-posted parameter away from destroying live mail.
        //
        // IT REMOVES THE READER'S COPIES. The message itself — and its attachments — go only when nobody holds a copy
        // any more: a recipient emptying their trash must not take the mail out of the sender's Sent.
        public async Task<int> DeleteForeverAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids)
        {
            var rows = await ScopedAsync(companyId, employeeId, ids, trashed: true);
            return rows.Count == 0 ? 0 : await PurgeAsync(rows);
        }

        /// Outlook's "Empty folder": everything currently in this person's Deleted Items.
        public async Task<int> EmptyTrashAsync(int companyId, int employeeId)
        {
            if (companyId <= 0 || employeeId <= 0) return 0;
            var rows = await _db.Set<CommMailboxItem>()
                .Where(i => i.CompanyID == companyId && i.EmployeeId == employeeId && i.DeletedAt != null)
                .ToListAsync();
            return rows.Count == 0 ? 0 : await PurgeAsync(rows);
        }

        // ---- the two halves every operation above shares -------------------------------------

        /// THE TENANT, THE READER AND THE FOLDER ARE ALL IN THE PREDICATE, and none is the caller's to assert. An id is
        /// only ever a lookup key: a colleague's copy — or another company's message — is not found here.
        private async Task<List<CommMailboxItem>> ScopedAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids, bool trashed)
        {
            if (companyId <= 0 || employeeId <= 0 || ids == null || ids.Count == 0) return new List<CommMailboxItem>();

            var wanted = ids.Where(i => i > 0).Distinct().ToList();
            if (wanted.Count == 0) return new List<CommMailboxItem>();

            return await _db.Set<CommMailboxItem>()
                .Where(i => i.CompanyID == companyId && i.EmployeeId == employeeId
                         && wanted.Contains(i.MessageId)
                         && (trashed ? i.DeletedAt != null : i.DeletedAt == null))
                .ToListAsync();
        }

        private async Task<int> PurgeAsync(List<CommMailboxItem> copies)
        {
            var messageIds = copies.Select(i => i.MessageId).Distinct().ToList();
            _db.Set<CommMailboxItem>().RemoveRange(copies);
            await _db.SaveChangesAsync();

            // The messages nobody holds any more.
            var held = await _db.Set<CommMailboxItem>().AsNoTracking().Where(i => messageIds.Contains(i.MessageId))
                .Select(i => i.MessageId).Distinct().ToListAsync();
            var orphanIds = messageIds.Except(held).ToList();
            if (orphanIds.Count > 0)
            {
                var rows = await _db.CommMessages.Where(m => orphanIds.Contains(m.Id)).ToListAsync();
                var attachments = await _db.CommAttachments.Where(a => orphanIds.Contains(a.CommMessageId)).ToListAsync();

                // ONLY THE MAIL'S OWN UPLOADS, AND ONLY WHEN NOTHING ELSE POINTS AT THEM. A file picked from the library
                // is the library's (/uploads/library/...), and a forward re-uses its source's files — deleting either
                // from disk would break a document or another message.
                var paths = attachments.Select(a => a.FilePath).Distinct().ToList();
                var stillUsed = (await _db.CommAttachments.AsNoTracking()
                    .Where(a => paths.Contains(a.FilePath) && !orphanIds.Contains(a.CommMessageId))
                    .Select(a => a.FilePath).Distinct().ToListAsync()).ToHashSet();
                foreach (var path in paths)
                {
                    if (stillUsed.Contains(path) || !path.Replace('\\', '/').TrimStart('/').StartsWith("uploads/comm/", StringComparison.OrdinalIgnoreCase)) continue;
                    var phys = Path.Combine(_env.WebRootPath, path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (System.IO.File.Exists(phys)) System.IO.File.Delete(phys);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A FILE THAT WILL NOT DELETE DOES NOT ABORT THE DELETE: the rows are the record, the file a copy.
                        _log.LogWarning(ex, "Mail attachment {Path} could not be removed; the message is still deleted and the file is orphaned.", path);
                    }
                }

                _db.CommAttachments.RemoveRange(attachments);
                _db.CommMessages.RemoveRange(rows);
                await _db.SaveChangesAsync();
            }
            return messageIds.Count;
        }

        public async Task<bool> ToggleStarAsync(int companyId, int employeeId, int id)
        {
            var mine = await _db.Set<CommMailboxItem>().Where(i => i.CompanyID == companyId && i.EmployeeId == employeeId && i.MessageId == id).ToListAsync();
            if (mine.Count == 0) return false;
            var on = !mine.Any(i => i.Starred);
            foreach (var i in mine) i.Starred = on;
            await _db.SaveChangesAsync();
            return true;
        }
    }
}
