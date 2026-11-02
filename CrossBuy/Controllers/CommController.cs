using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Communication Hub — email (Metronic demo39 inbox: listing + compose + view/reply pages).
    [SessionValidation]
    public class CommController : Controller
    {
        private readonly ICommService _comm;
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly IFileManagerService _fm;

        // THE MODULE'S OWN AUTHORITY, asked before anything is destroyed.
        //
        // The rest of this controller sits inside a FROZEN baseline of pre-existing authorization debt
        // that a later wave is meant to pay down. The honest way to add a destructive endpoint to it is
        // not to add three more entries to that baseline - it is to authorize properly, which is where
        // the whole file is going anyway.
        private readonly IMailInviteResponder _invites;
        private readonly ICommunicationAccessService _access;
        private readonly IBusinessContextAccessor _contexts;

        public CommController(ICommService comm, IEmployeeService employees, CrossDbContext db, IWebHostEnvironment env, IFileManagerService fm,
            ICommunicationAccessService access, IBusinessContextAccessor contexts,
            IMailInviteResponder invites)
        { _comm = comm; _employees = employees; _db = db; _env = env; _fm = fm; _access = access; _contexts = contexts; _invites = invites; }

        /// May this caller manage the mailbox? `outbox-manage` is the module's own name for exactly
        /// this - the email outbox - so deleting mail answers to the same right that already governs
        /// operating it, rather than to a new permission invented for one button.
        private async Task<bool> MayManageOutboxAsync(CancellationToken cancellationToken = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (ctx is null || ctx.CompanyId <= 0) return false;      // unresolved tenant decides nothing
            return await _access.CanAsync(ctx, CommunicationActions.OutboxManage, null, cancellationToken);
        }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
        }

        /// The folder badges, in one shape. FillCountsAsync puts them on the ViewBag for the first
        /// render; this returns the same six for every mutation that follows.
        private async Task<object> CountsAsync(int companyId)
        {
            var c = await _comm.CountsAsync(companyId);
            return new { all = c.all, sent = c.sent, outbox = c.outbox, failed = c.failed, trash = c.trash, starred = c.starred };
        }

        private async Task FillCountsAsync(int companyId)
        {
            var c = await _comm.CountsAsync(companyId);
            ViewBag.CountAll = c.all; ViewBag.CountSent = c.sent; ViewBag.CountOutbox = c.outbox;
            ViewBag.CountFailed = c.failed; ViewBag.CountTrash = c.trash; ViewBag.CountStarred = c.starred;
            ViewBag.Configured = _comm.SmtpConfigured;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? folder, string? q)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            folder = string.IsNullOrWhiteSpace(folder) ? "All" : folder;
            ViewBag.Folder = folder; ViewBag.Q = q;
            await FillCountsAsync(me.Value.companyId);
            // SharePoint-style: surface the top-level document-library folders in the mail sidebar
            var lib = await _fm.ListAsync(me.Value.companyId, null, null);
            ViewBag.LibFolders = lib.items.Where(i => i.isFolder).Take(8).ToList();
            return View(await _comm.ListAsync(me.Value.companyId, folder, q));
        }

        [HttpGet]
        public async Task<IActionResult> Compose(int? parentId, string? kind)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            await FillCountsAsync(me.Value.companyId);
            kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind;
            ViewBag.Kind = kind; ViewBag.ParentId = parentId;
            ViewBag.To = ""; ViewBag.Cc = ""; ViewBag.Subject = ""; ViewBag.Body = "";
            ViewBag.Inherited = new List<MailAttachmentDto>();
            if (parentId.HasValue)
            {
                var src = await _comm.GetAsync(me.Value.companyId, parentId.Value);
                if (src != null)
                {
                    // body is rich HTML now → build an HTML quote (subject encoded, original body kept as-is)
                    string quote = $"<p><br></p><hr /><p><strong>----- {System.Net.WebUtility.HtmlEncode(src.Subject)} -----</strong></p>{src.Body}";
                    string strip(string s, string p) => s != null && s.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? s[p.Length..].Trim() : s ?? "";
                    var baseSubj = strip(strip(src.Subject, "Re:"), "Fwd:");
                    if (kind == "Forward")
                    {
                        ViewBag.Subject = "Fwd: " + baseSubj; ViewBag.Body = quote;
                        ViewBag.Inherited = src.Attachments;
                    }
                    else { ViewBag.To = src.ToAddress; ViewBag.Cc = kind == "ReplyAll" ? (src.Cc ?? "") : ""; ViewBag.Subject = "Re: " + baseSubj; ViewBag.Body = quote; }
                }
            }
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> View(int id)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            var m = await _comm.GetAsync(me.Value.companyId, id);
            if (m == null) return NotFound();
            await FillCountsAsync(me.Value.companyId);

            // WHETHER TO OFFER THE THREE BUTTONS AT ALL. Asking the responder is the same question
            // it will ask again when the answer is posted - "is there an attendee row for you on this
            // meeting" - so the screen cannot offer a control the action would then refuse. A
            // forwarded invitation renders the card and no buttons.
            if (m.Invite is not null)
            {
                var current = await _invites.CurrentAsync(me.Value.companyId, me.Value.empId, id);
                ViewBag.InviteResponse = current;
                ViewBag.CanRespond = await _invites.IsAddressedToAsync(me.Value.companyId, me.Value.empId, id);

                // WHETHER THE MEETING IS ALREADY ON THEIR CALENDAR, decided here rather than by a
                // click. For most readers it is - the invitation created it when it was sent - so an
                // "add" button is an action with nothing to do, and the reader learns that only after
                // pressing it. With this the screen can simply say so and offer the way in.
                var where = await _invites.LocateAsync(me.Value.companyId, me.Value.empId, id);
                ViewBag.InviteOnCalendar = where.present;
                ViewBag.InviteCalendarUrl = where.present
                    ? Url.Action("Index", "Calendar", new { day = where.day, eventId = where.eventId })
                    : null;
            }

            return View(m);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Send(string to, string? cc, string subject, string? body, int? parentId, string? kind,
            List<Microsoft.AspNetCore.Http.IFormFile>? files, string? libraryFileIds)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind;
            // safety net (client also has `required`): don't create junk rows for empty recipient/subject
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject))
            {
                TempData["MailError"] = "recipient_subject_required";
                return RedirectToAction("Compose", new { parentId, kind });
            }
            var attach = new List<MailFile>();
            if (files != null && files.Count > 0)
            {
                var dir = System.IO.Path.Combine(_env.WebRootPath, "uploads", "comm");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                foreach (var f in files)
                {
                    if (f == null || f.Length == 0) continue;
                    var stored = Guid.NewGuid() + System.IO.Path.GetExtension(f.FileName);
                    using (var s = System.IO.File.Create(System.IO.Path.Combine(dir, stored))) await f.CopyToAsync(s);
                    attach.Add(new MailFile("/uploads/comm/" + stored, System.IO.Path.GetFileName(f.FileName), f.Length));
                }
            }
            if (kind == "Forward" && parentId.HasValue)
            {
                var src = await _comm.GetAsync(me.Value.companyId, parentId.Value);
                if (src != null) foreach (var a in src.Attachments) attach.Add(new MailFile(a.FilePath, a.FileName, a.Size));
            }
            // attach files chosen from the document library — reference the stored file directly (no re-upload)
            if (!string.IsNullOrWhiteSpace(libraryFileIds))
            {
                var ids = libraryFileIds.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
                if (ids.Count > 0)
                    foreach (var lf in await _fm.GetFilesAsync(me.Value.companyId, ids))
                        if (!string.IsNullOrEmpty(lf.StoredPath)) attach.Add(new MailFile(lf.StoredPath, lf.Name, lf.Size));
            }
            var (_, status, error) = await _comm.SendAsync(me.Value.companyId, me.Value.empId, to, cc, subject ?? "", body ?? "", attach, parentId, kind);
            TempData["MailSent"] = status;                       // Sent | Queued | Failed
            if (!string.IsNullOrEmpty(error)) TempData["MailSentError"] = error;
            // land on a folder where the new message is actually visible (Queued → Outbox, else its status folder)
            var landing = status == "Sent" ? "Sent" : status == "Failed" ? "Failed" : "Outbox";
            return RedirectToAction("Index", new { folder = landing });
        }

        // people-picker: company employees (name + email + photo) matching a query, for the To/Cc autocomplete
        [HttpGet]
        public async Task<IActionResult> Contacts(string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var query = _db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == me.Value.companyId && e.IsActive && e.Email != null && e.Email != "");
            var t = (q ?? "").Trim();
            if (t.Length > 0) query = query.Where(e => e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t)) || e.Email.Contains(t));
            var rows = await query.OrderBy(e => e.FullName).Take(8)
                .Select(e => new { e.FullName, e.FullNameEn, e.Email, e.ProfileImage }).ToListAsync();
            var list = rows.Select(e => new { name = isAr ? e.FullName : (e.FullNameEn ?? e.FullName), email = e.Email, photo = e.ProfileImage });
            return Json(list);
        }

        // ---- DELETE: ONE VERB, TWO MEANINGS, DECIDED BY WHERE THE USER IS STANDING ------------
        //
        // From any folder it MOVES to Deleted Items. From inside Deleted Items it is FINAL. That is
        // what every mail client has taught people to expect, and the folder the user was looking at
        // is the only honest way to tell the two apart - so it is a parameter, not a guess.
        //
        // THE SERVICE STILL CHECKS. Passing folder="Trash" from the inbox cannot destroy live mail:
        // DeleteForeverAsync only matches rows that are ALREADY trashed, so the worst a bad parameter
        // can do is delete nothing. The parameter chooses the intent; the predicate enforces it.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int[] ids, string? folder, bool ajax = false)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayManageOutboxAsync()) return Forbid();

            bool permanent = string.Equals(folder, "Trash", StringComparison.OrdinalIgnoreCase);
            int affected = permanent
                ? await _comm.DeleteForeverAsync(me.Value.companyId, ids ?? Array.Empty<int>())
                : await _comm.TrashAsync(me.Value.companyId, ids ?? Array.Empty<int>());

            // A LIST MUTATION ANSWERS IN PLACE. The page removes the rows it just acted on rather than
            // reloading - a reload would lose the scroll position and the user's place in a mailbox.
            // The message VIEW still redirects, because there the message being gone IS the navigation.
            // THE COUNTS COME BACK WITH THE ANSWER, so the page does not have to reload to stay
            // truthful. Recomputed server-side rather than decremented in the browser: the client
            // knows how many rows it removed, not how many were already trashed, starred or failed,
            // and a badge that drifts from the folder it names is worse than no badge.
            if (ajax) return Json(new { ok = true, affected, permanent, counts = await CountsAsync(me.Value.companyId) });
            return RedirectToAction("Index", new { folder });
        }

        // ---- ANSWERING A MEETING INVITATION --------------------------------------------------
        //
        // NO outbox-manage GATE HERE, and the difference is the point: deleting mail is managing the
        // mailbox, answering an invitation is replying to something addressed to YOU. The responder
        // refuses anyone without an attendee row on that meeting, which is a narrower and more
        // accurate check than a mailbox right - a colleague who may administer the outbox still
        // cannot accept a meeting on your behalf.
        /// Put the meeting on this product's own calendar. Same authority floor as Respond: the
        /// module right says you belong here, and the service decides what it is safe to create.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToCalendar(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();

            var ctx = await _contexts.TryGetCurrentAsync();
            if (ctx is null || ctx.CompanyId <= 0) return Forbid();
            if (!await _access.CanAsync(ctx, CommunicationActions.Read)) return Forbid();

            var (ok, added, error, eventId, day) = await _invites.AddToCalendarAsync(
                me.Value.companyId, me.Value.empId, id);

            // The URL is built HERE, not in the browser, so the one place that knows the calendar's
            // route is the server's own routing table. A hand-assembled "/Calendar?..." in a script
            // survives a route change silently and lands on a 404.
            string? url = ok && eventId > 0
                ? Url.Action("Index", "Calendar", new { day, eventId })
                : null;

            return Json(new { ok, added, error, eventId, day, url });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Respond(int id, string response)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();

            // THE FLOOR, not the decision. "read" asks only whether this caller may be in the
            // communications module at all; whether they may answer THIS meeting is decided by the
            // responder, which refuses anyone without an attendee row on it. Two checks, because the
            // module right cannot express "addressed to you" and the attendee row cannot express
            // "belongs in this module".
            var ctx = await _contexts.TryGetCurrentAsync();
            if (ctx is null || ctx.CompanyId <= 0) return Forbid();
            if (!await _access.CanAsync(ctx, CommunicationActions.Read)) return Forbid();

            var (ok, error, status) = await _invites.RespondAsync(
                me.Value.companyId, me.Value.empId, id, response);

            return Json(new { ok, error, status });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(int[] ids, bool ajax = false)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayManageOutboxAsync()) return Forbid();
            int affected = await _comm.RestoreAsync(me.Value.companyId, ids ?? Array.Empty<int>());
            if (ajax) return Json(new { ok = true, affected, counts = await CountsAsync(me.Value.companyId) });
            return RedirectToAction("Index", new { folder = "Trash" });
        }

        /// Outlook's "Empty folder". Deliberately NOT reachable with a folder parameter: there is one
        /// folder this can mean, and naming it in the route is what stops a mistyped value emptying
        /// the inbox.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EmptyTrash(bool ajax = false)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayManageOutboxAsync()) return Forbid();
            int affected = await _comm.EmptyTrashAsync(me.Value.companyId);
            if (ajax) return Json(new { ok = true, affected, counts = await CountsAsync(me.Value.companyId) });
            return RedirectToAction("Index", new { folder = "Trash" });
        }

        [HttpPost]
        public async Task<IActionResult> Star(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _comm.ToggleStarAsync(me.Value.companyId, id);

            // STARRING MOVES A FOLDER COUNT, so it answers with the counts for the same reason delete
            // does: the Starred badge names a set this action just changed, and a badge that only
            // catches up on the next page load is wrong in between.
            return Json(new { ok, counts = await CountsAsync(me.Value.companyId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resend(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var owns = await _db.CommMessages.AsNoTracking().AnyAsync(x => x.Id == id && x.CompanyID == me.Value.companyId);
            if (!owns) return NotFound();
            await _comm.TrySendAsync(id);
            return RedirectToAction("View", new { id });
        }
    }
}
