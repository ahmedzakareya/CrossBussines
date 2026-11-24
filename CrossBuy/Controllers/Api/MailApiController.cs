using CrossBuy.BL;
using CrossBuy.BL.Comm;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
    // ==========================================================================================
    // EMAIL FOR THE MOBILE APP (owner, 2026-11-24: «تمام موافق» on the prototype pages mail / mail-read / mail-compose).
    // The same ICommService, MailAssistant and file library the browser uses, behind the app's token: the person and
    // the company are the resolved BusinessContext's; every action asks the communication module's floor (read) and
    // the service then matches only the caller's own mailbox copies — no second set of rules.
    // ==========================================================================================
    [ApiController]
    [Route("api/mail")]
    [Produces("application/json")]
    [Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
    public class MailApiController : ControllerBase
    {
        private readonly IBusinessContextAccessor _contexts;
        private readonly ICommService _mail;
        private readonly ICommunicationAccessService _comm;
        private readonly IFileManagerService _fm;
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly IServiceProvider _sp;

        public MailApiController(IBusinessContextAccessor contexts, ICommService mail, ICommunicationAccessService comm, IFileManagerService fm,
            CrossDbContext db, IWebHostEnvironment env, IServiceProvider sp)
        { _contexts = contexts; _mail = mail; _comm = comm; _fm = fm; _db = db; _env = env; _sp = sp; }

        private static object Missing => new { ok = false, code = "not_found" };
        private MailAssistant Assistant => ActivatorUtilities.CreateInstance<MailAssistant>(_sp);

        /// The caller, when they may use communications at all. The app's language follows its Accept-Language.
        /// NOT async, like the chat API's: a culture set inside an async method is undone when it returns, and the names
        /// (DisplayName.Or) would come back in English for an Arabic app.
        private Task<BusinessContext?> MeAsync(CancellationToken ct)
        {
            if (Request.Headers.AcceptLanguage.ToString().StartsWith("ar", StringComparison.OrdinalIgnoreCase))
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
            return ResolveAsync(ct);
        }
        private async Task<BusinessContext?> ResolveAsync(CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0, EmployeeId: > 0 }) return null;
            return await _comm.CanAsync(ctx, CommunicationActions.Read, null, ct) ? ctx : null;
        }
        private Task<bool> ManagerAsync(BusinessContext me, CancellationToken ct) => _comm.CanAsync(me, CommunicationActions.OutboxManage, null, ct);

        /// GET /api/mail/counts — the folder badges, and my address for mail from outside.
        [HttpGet("counts")]
        public async Task<IActionResult> Counts(CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var c = await _mail.CountsAsync(me.CompanyId, me.EmployeeId!.Value);
            return Ok(new { inbox = c.Inbox, unread = c.Unread, sent = c.Sent, outbox = c.Outbox, failed = c.Failed, starred = c.Starred, trash = c.Trash,
                address = _mail.ExternalAddressFor(me.EmployeeId!.Value) });
        }

        /// GET /api/mail/list?folder=Inbox|Sent|Starred|Trash|Outbox|Failed|All&amp;q= — my copies, newest first.
        [HttpGet("list")]
        public async Task<IActionResult> List(string? folder, string? q, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var f = folder is "Sent" or "Starred" or "Trash" or "Outbox" or "Failed" or "All" ? folder : "Inbox";
            var rows = await _mail.ListAsync(me.CompanyId, me.EmployeeId!.Value, f, q);
            return Ok(rows.Select(r => new
            {
                id = r.Id, subject = r.Subject, preview = r.Preview, at = r.At, unread = r.Unread, starred = r.Starred, box = r.Box,
                hasAttachment = r.HasAttachment, kind = r.Kind, status = r.Status, external = r.External,
                // who the row is about: the writer for mail I received, the recipient for mail I sent
                name = r.Box == "Inbox" ? r.FromName : r.ToAddress, photo = r.Box == "Inbox" ? r.FromPhoto : r.RecipientPhoto,
                invite = r.Invite,
            }));
        }

        /// GET /api/mail/message?id= — the message (marked read) with the earlier messages of its conversation.
        [HttpGet("message")]
        public async Task<IActionResult> Message(int id, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var manager = await ManagerAsync(me, ct);
            var m = await _mail.GetAsync(me.CompanyId, me.EmployeeId!.Value, id, manager, markRead: true);
            if (m == null) return NotFound(Missing);
            var thread = (await _mail.ThreadAsync(me.CompanyId, me.EmployeeId!.Value, m.ThreadId ?? m.Id, manager)).Where(t => t.Id != m.Id).ToList();
            return Ok(new
            {
                id = m.Id, subject = m.Subject, box = m.Box, external = m.External, status = m.Status, starred = m.Starred,
                fromName = m.FromName, fromAddress = m.FromAddress, fromPhoto = m.FromPhoto, to = m.ToAddress, cc = m.Cc,
                at = m.SentAt ?? m.CreatedAt, deleted = m.DeletedAt != null,
                text = MailAssistant.Text(m.Body),
                attachments = m.Attachments.Select(a => new { path = a.FilePath, name = a.FileName, size = a.Size }),
                invite = m.Invite == null ? null : new { title = m.Invite.Title, startsAt = m.Invite.StartsAt },
                thread = thread.Select(t => new { id = t.Id, fromName = t.FromName, fromPhoto = t.FromPhoto, preview = t.Preview, at = t.At, external = t.External, mine = t.Mine }),
            });
        }

        /// GET /api/mail/compose?parentId=&amp;kind=Reply|ReplyAll|Forward — the prefilled fields a reply or a forward starts with.
        [HttpGet("compose")]
        public async Task<IActionResult> Compose(int parentId, string? kind, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var src = await _mail.GetAsync(me.CompanyId, me.EmployeeId!.Value, parentId, await ManagerAsync(me, ct));
            if (src == null) return NotFound(Missing);
            string strip(string s, string p) => s.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? s[p.Length..].Trim() : s;
            var baseSubj = strip(strip(src.Subject ?? "", "Re:"), "Fwd:");
            string[] split(string? s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool same(string a, string? b) => string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            var mine = await _db.Employee.AsNoTracking().Where(e => e.ID == me.EmployeeId).Select(e => e.Email).FirstOrDefaultAsync(ct);
            string to = "", cc = "";
            if (kind != "Forward")
            {
                // the same rule as the browser's compose: a reply to mail I received goes to its writer
                to = src.Box == "Inbox" ? (src.FromAddress ?? "") : src.ToAddress;
                if (kind == "ReplyAll")
                    cc = src.Box == "Inbox"
                        ? string.Join(", ", split(src.ToAddress).Concat(split(src.Cc)).Where(a => !same(a, mine) && !same(a, src.FromAddress)).Distinct(StringComparer.OrdinalIgnoreCase))
                        : src.Cc ?? "";
            }
            return Ok(new { to, cc, subject = (kind == "Forward" ? "Fwd: " : "Re: ") + baseSubj, quote = MailAssistant.Text(src.Body), toName = src.Box == "Inbox" ? src.FromName : null });
        }

        /// POST /api/mail/send (multipart) — to, cc, subject, text (plain; the app writes text), parentId, kind,
        /// libraryIds (comma list), files. Colleagues receive it inside; outside addresses go through the company account.
        [HttpPost("send")]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Send([FromForm] string to, [FromForm] string? cc, [FromForm] string subject, [FromForm] string? text,
            [FromForm] int? parentId, [FromForm] string? kind, [FromForm] string? libraryIds, [FromForm] List<IFormFile>? files, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject)) return BadRequest(new { ok = false, code = "recipient_subject_required" });
            kind = kind is "Reply" or "ReplyAll" or "Forward" ? kind : "New";
            var html = string.Concat((text ?? "").Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => "<p>" + System.Net.WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>") + "</p>"));
            var attach = new List<MailFile>();
            if (files is { Count: > 0 })
            {
                var dir = Path.Combine(_env.WebRootPath, "uploads", "comm");
                Directory.CreateDirectory(dir);
                foreach (var f in files.Where(f => f is { Length: > 0 }).Take(20))
                {
                    var ext = Path.GetExtension(f.FileName);
                    if (ext.Length > 10) ext = "";
                    var stored = Guid.NewGuid() + ext;
                    await using (var s = System.IO.File.Create(Path.Combine(dir, stored))) await f.CopyToAsync(s, ct);
                    attach.Add(new MailFile("/uploads/comm/" + stored, Path.GetFileName(f.FileName), f.Length));
                }
            }
            if (parentId is int pid && kind == "Forward")
            {
                var src = await _mail.GetAsync(me.CompanyId, me.EmployeeId!.Value, pid, await ManagerAsync(me, ct));
                if (src != null) attach.AddRange(src.Attachments.Select(a => new MailFile(a.FilePath, a.FileName, a.Size)));
                if (src != null) html += "<hr /><p><strong>----- " + System.Net.WebUtility.HtmlEncode(src.Subject) + " -----</strong></p>" + src.Body;
            }
            else if (parentId is int rid)
            {
                var src = await _mail.GetAsync(me.CompanyId, me.EmployeeId!.Value, rid, await ManagerAsync(me, ct));
                if (src != null) html += "<p><br></p><hr /><p><strong>----- " + System.Net.WebUtility.HtmlEncode(src.Subject) + " -----</strong></p>" + src.Body;
            }
            var ids = (libraryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            if (ids.Count > 0)
                foreach (var lf in await _fm.GetFilesAsync(me.CompanyId, ids))
                    if (!string.IsNullOrEmpty(lf.StoredPath)) attach.Add(new MailFile(lf.StoredPath, lf.Name, lf.Size));
            var (id, status, error) = await _mail.SendAsync(me.CompanyId, me.EmployeeId!.Value, to, cc, subject, html, attach, parentId, kind);
            return Ok(new { ok = status != "Failed", id, status, error });
        }

        /// POST /api/mail/delete?ids=1,2&amp;permanent= — my copies to Deleted Items, or (from there) forever.
        [HttpPost("delete")]
        public async Task<IActionResult> Delete(string ids, bool permanent, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            var n = permanent ? await _mail.DeleteForeverAsync(me.CompanyId, me.EmployeeId!.Value, list) : await _mail.TrashAsync(me.CompanyId, me.EmployeeId!.Value, list);
            return Ok(new { ok = true, affected = n });
        }

        [HttpPost("restore")]
        public async Task<IActionResult> Restore(int id, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            return Ok(new { ok = await _mail.RestoreAsync(me.CompanyId, me.EmployeeId!.Value, new[] { id }) > 0 });
        }

        [HttpPost("star")]
        public async Task<IActionResult> Star(int id, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            return Ok(new { ok = await _mail.ToggleStarAsync(me.CompanyId, me.EmployeeId!.Value, id) });
        }

        /// POST /api/mail/assist?id=&amp;mode=summary|reply|translate&amp;instruction= — then GET assist/status?job=.
        [HttpPost("assist")]
        public async Task<IActionResult> Assist(int id, string mode, string? instruction, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var r = await Assistant.StartAsync(me.CompanyId, me.EmployeeId!.Value, id, mode, instruction, await ManagerAsync(me, ct), ct);
            return Ok(new { ok = r.Ok, job = r.Job, code = r.Code });
        }

        [HttpGet("assist/status")]
        public async Task<IActionResult> AssistStatus(string job, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null || string.IsNullOrEmpty(job)) return NotFound(Missing);
            var s = await Assistant.StatusAsync(me.CompanyId, me.EmployeeId!.Value, job, ct);
            return s == null ? NotFound(Missing) : Ok(s);
        }

        /// GET /api/mail/contacts?q= — colleagues with an address, for the To/Cc picker.
        [HttpGet("contacts")]
        public async Task<IActionResult> Contacts(string? q, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var t = (q ?? "").Trim();
            var query = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == me.CompanyId && e.IsActive && e.Email != null && e.Email != "");
            if (t.Length > 0) query = query.Where(e => e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t)) || e.Email.Contains(t));
            var rows = await query.OrderBy(e => e.FullName).Take(12).Select(e => new { e.FullName, e.FullNameEn, e.Email, e.ProfileImage }).ToListAsync(ct);
            return Ok(rows.Select(e => new { name = ar ? e.FullName : (e.FullNameEn ?? e.FullName), email = e.Email, photo = e.ProfileImage }));
        }

        /// GET /api/mail/library?folderId=&amp;q= — the company's document library, to attach a file without uploading it.
        [HttpGet("library")]
        public async Task<IActionResult> Library(int? folderId, string? q, CancellationToken ct)
        {
            var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
            var r = await _fm.ListAsync(me.CompanyId, folderId, q);
            return Ok(new
            {
                items = r.items.Where(i => !i.isChatAttachment).Select(i => new { i.id, i.isFolder, i.name, i.sizeText, i.ext }),
                parentId = r.parentId, path = r.path.Select(p => new { p.id, p.name }),
            });
        }
    }
}
