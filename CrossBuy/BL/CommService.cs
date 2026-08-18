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
        Task<bool> TrashAsync(int companyId, int id);
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
            return rows.Select(r => new MailListItem
            {
                Id = r.Id, ToAddress = r.ToAddress, Subject = r.Subject,
                Preview = Plain(r.Body),
                Status = r.Status, Starred = r.Starred, HasAttachment = set.Contains(r.Id), Kind = r.Kind, At = r.SentAt ?? r.CreatedAt,
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
            return new MailFull
            {
                Id = x.Id, ToAddress = x.ToAddress, Cc = x.Cc, Subject = x.Subject, Body = x.Body, Status = x.Status,
                Error = x.Error, Starred = x.Starred, SentAt = x.SentAt, CreatedAt = x.CreatedAt,
                Attachments = await _db.CommAttachments.AsNoTracking().Where(a => a.CommMessageId == x.Id)
                    .Select(a => new MailAttachmentDto { FilePath = a.FilePath, FileName = a.FileName, Size = a.Size }).ToListAsync(),
            };
        }

        public async Task<bool> TrashAsync(int companyId, int id)
        {
            var m = await _db.CommMessages.FirstOrDefaultAsync(x => x.Id == id && x.CompanyID == companyId);
            if (m == null) return false;
            m.DeletedAt = DateTime.UtcNow; await _db.SaveChangesAsync(); return true;
        }
        public async Task<bool> ToggleStarAsync(int companyId, int id)
        {
            var m = await _db.CommMessages.FirstOrDefaultAsync(x => x.Id == id && x.CompanyID == companyId);
            if (m == null) return false;
            m.Starred = !m.Starred; await _db.SaveChangesAsync(); return true;
        }
    }
}
