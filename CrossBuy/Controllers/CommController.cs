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

        // THE REGISTRY DIRECTLY, not ITaskLinkResolver. That wrapper refuses any type the record
        // PICKER does not list - JournalEntry among them - and picker membership has nothing to do
        // with whether a document can be named in a subject line. A mail about JV-2026-003637 should
        // say so whether or not a task may be linked to it.
        private readonly CrossBuy.BL.Platform.IEntityRegistry _registry;

        public CommController(ICommService comm, IEmployeeService employees, CrossDbContext db, IWebHostEnvironment env, IFileManagerService fm,
            ICommunicationAccessService access, IBusinessContextAccessor contexts,
            IMailInviteResponder invites, CrossBuy.BL.Platform.IEntityRegistry registry)
        { _comm = comm; _employees = employees; _db = db; _env = env; _fm = fm; _access = access; _contexts = contexts; _invites = invites; _registry = registry; }

        /// May this caller manage the mailbox? `outbox-manage` is the module's own name for exactly
        /// this - the email outbox - so deleting mail answers to the same right that already governs
        /// operating it, rather than to a new permission invented for one button.
        private async Task<bool> MayManageOutboxAsync(CancellationToken cancellationToken = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (ctx is null || ctx.CompanyId <= 0) return false;      // unresolved tenant decides nothing
            return await _access.CanAsync(ctx, CommunicationActions.OutboxManage, null, cancellationToken);
        }

        /// THE MODULE'S FLOOR for acting on one's own mailbox: may this caller use communications at all? Which
        /// messages they may touch is decided by the service, which matches only their own copies.
        private async Task<bool> MayUseMailAsync(CancellationToken cancellationToken = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (ctx is null || ctx.CompanyId <= 0) return false;
            return await _access.CanAsync(ctx, CommunicationActions.Read, null, cancellationToken);
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
        // ONE PERSON'S BADGES (owner, 2026-11-23: a mailbox per employee). `company` is present only for whoever
        // manages the company outbox.
        private async Task<object> CountsAsync(int companyId, int empId)
        {
            var c = await _comm.CountsAsync(companyId, empId, await MayManageOutboxAsync());
            return new { inbox = c.Unread, all = c.All, sent = c.Sent, outbox = c.Outbox, failed = c.Failed, trash = c.Trash, starred = c.Starred, company = c.Company };
        }

        private async Task FillCountsAsync(int companyId, int empId)
        {
            var manager = await MayManageOutboxAsync();
            var c = await _comm.CountsAsync(companyId, empId, manager);
            ViewBag.Counts = c;
            ViewBag.Manager = manager;
            ViewBag.CountAll = c.All; ViewBag.CountSent = c.Sent; ViewBag.CountOutbox = c.Outbox;
            ViewBag.CountFailed = c.Failed; ViewBag.CountTrash = c.Trash; ViewBag.CountStarred = c.Starred;
            ViewBag.Configured = _comm.SmtpConfigured;
            ViewBag.MyAddress = _comm.ExternalAddressFor(empId);      // phase 2: where people outside write to me
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? folder, string? q)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            folder = string.IsNullOrWhiteSpace(folder) ? "Inbox" : folder;
            var manager = await MayManageOutboxAsync();
            if (folder == "Company" && !manager) folder = "Inbox";      // the company outbox is its manager's alone
            ViewBag.Folder = folder; ViewBag.Q = q;
            await FillCountsAsync(me.Value.companyId, me.Value.empId);
            // SharePoint-style: surface the top-level document-library folders in the mail sidebar
            var lib = await _fm.ListAsync(me.Value.companyId, null, null);
            ViewBag.LibFolders = lib.items.Where(i => i.isFolder).Take(8).ToList();
            return View(await _comm.ListAsync(me.Value.companyId, me.Value.empId, folder, q, manager));
        }

        [HttpGet]
        public async Task<IActionResult> Compose(int? parentId, string? kind)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            await FillCountsAsync(me.Value.companyId, me.Value.empId);
            kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind;
            ViewBag.Kind = kind; ViewBag.ParentId = parentId;
            ViewBag.To = ""; ViewBag.Cc = ""; ViewBag.Subject = ""; ViewBag.Body = "";
            ViewBag.Inherited = new List<MailAttachmentDto>();
            if (parentId.HasValue)
            {
                var src = await _comm.GetAsync(me.Value.companyId, me.Value.empId, parentId.Value, await MayManageOutboxAsync());
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
                    else if (src.Box == "Inbox")
                    {
                        // A REPLY TO MAIL I RECEIVED GOES TO WHOEVER WROTE IT. Reply-all adds everyone else it was
                        // addressed to, without me (I am writing) and without the writer twice.
                        var mine = await _db.Employee.AsNoTracking().Where(e => e.ID == me.Value.empId).Select(e => e.Email).FirstOrDefaultAsync();
                        string[] split(string? s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        bool same(string a, string? b) => string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                        ViewBag.To = src.FromAddress ?? "";
                        ViewBag.Cc = kind == "ReplyAll"
                            ? string.Join(", ", split(src.ToAddress).Concat(split(src.Cc)).Where(a => !same(a, mine) && !same(a, src.FromAddress)).Distinct(StringComparer.OrdinalIgnoreCase))
                            : "";
                        ViewBag.Subject = "Re: " + baseSubj; ViewBag.Body = quote;
                    }
                    else { ViewBag.To = src.ToAddress; ViewBag.Cc = kind == "ReplyAll" ? (src.Cc ?? "") : ""; ViewBag.Subject = "Re: " + baseSubj; ViewBag.Body = quote; }
                }
            }
            // A DRAFT FROM THE ASSISTANT (phase 4) leads the body, as plain paragraphs above the quoted message.
            if (TempData["MailDraft"] is string draft && !string.IsNullOrWhiteSpace(draft))
                ViewBag.Body = string.Concat(draft.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(par => "<p>" + System.Net.WebUtility.HtmlEncode(par.Trim()).Replace("\n", "<br>") + "</p>")) + (ViewBag.Body as string ?? "");
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> View(int id)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            // OPENING IT READS IT: the reader's Inbox copy is stamped read on the way in.
            var m = await _comm.GetAsync(me.Value.companyId, me.Value.empId, id, await MayManageOutboxAsync(), markRead: true);
            if (m == null) return NotFound();
            await FillCountsAsync(me.Value.companyId, me.Value.empId);
            // THE CONVERSATION (phase 3): the other messages of this thread the reader holds, oldest first.
            ViewBag.Thread = (await _comm.ThreadAsync(me.Value.companyId, me.Value.empId, m.ThreadId ?? m.Id, await MayManageOutboxAsync()))
                .Where(t => t.Id != m.Id).ToList();

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
        // `ajax` is how the MODAL sends. Everything below is unchanged - same validation, same
        // attachments, same ICommService.SendAsync - only the ANSWER differs: a fetch cannot follow
        // a redirect into a folder listing, and a modal must not navigate the page it sits on.
        public async Task<IActionResult> Send(string to, string? cc, string subject, string? body, int? parentId, string? kind,
            List<Microsoft.AspNetCore.Http.IFormFile>? files, string? libraryFileIds, bool ajax = false)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayUseMailAsync()) return Forbid();
            kind = string.IsNullOrWhiteSpace(kind) ? "New" : kind;
            // safety net (client also has `required`): don't create junk rows for empty recipient/subject
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject))
            {
                if (ajax) return Json(new { ok = false, error = Ar() ? "المستلم والموضوع مطلوبان" : "Recipient and subject are required" });
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
                var src = await _comm.GetAsync(me.Value.companyId, me.Value.empId, parentId.Value, await MayManageOutboxAsync());
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
            // The modal reports the SAME three states the folder landing encodes - queued is not a
            // failure and must not be shown as one.
            if (ajax) return Json(new { ok = status != "Failed", status, error });

            TempData["MailSent"] = status;                       // Sent | Queued | Failed
            if (!string.IsNullOrEmpty(error)) TempData["MailSentError"] = error;
            // land on a folder where the new message is actually visible (Queued → Outbox, else its status folder)
            var landing = status == "Sent" ? "Sent" : status == "Failed" ? "Failed" : "Outbox";
            return RedirectToAction("Index", new { folder = landing });
        }

        // THE SUBJECT A RECORD DESERVES, resolved rather than typed.
        //
        // The modal opens from a screen that is already showing one document, so the subject and a
        // link to it are facts the system holds - asking the sender to restate them is how a mail
        // about invoice SV-2026-12546 ends up titled "re: the invoice".
        // This controller has no IStringLocalizer; the module states its few strings inline,
        // as its views do. One helper so both new answers speak the reader’s language.
        private static bool Ar() => System.Globalization.CultureInfo.CurrentUICulture
            .TwoLetterISOLanguageName == "ar";

        [HttpGet]
        public async Task<IActionResult> RecordSubject(string? entity, int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(entity) || id <= 0) return Json(new { ok = false });

            if (!_registry.TryGetDefinition(entity, out var def) || def == null) return Json(new { ok = false });

            // ForSystem(company): this is a LABEL read for a caller already standing on the record,
            // and the registry still scopes the row to that company - a wrong id answers "#id".
            var link = await _registry.ResolveAsync(def.Code, id,
                CrossBuy.Models.Platform.BusinessContext.ForSystem(me.Value.companyId));

            var type = Ar() ? def.DisplayNameAr : def.DisplayNameEn;
            // An ABSOLUTE url: the mail is read outside this tab, often outside the browser.
            var url = string.IsNullOrWhiteSpace(link.Url) ? null
                : $"{Request.Scheme}://{Request.Host}{link.Url}";
            return Json(new { ok = true, subject = (type + " " + link.Label).Trim(), label = link.Label, url });
        }

        // ---- THE ASSISTANT IN THE MAILBOX (phase 4) ----------------------------------------------------
        // A summary of the conversation, a draft reply, a translation - on the company's own AI service. Only for
        // mail the reader holds (MailAssistant reads through the reader's mailbox); a job is read by its starter only.
        private CrossBuy.BL.Comm.MailAssistant Assistant => ActivatorUtilities.CreateInstance<CrossBuy.BL.Comm.MailAssistant>(HttpContext.RequestServices);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Assist(int id, string mode, string? instruction)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayUseMailAsync()) return Forbid();
            var r = await Assistant.StartAsync(me.Value.companyId, me.Value.empId, id, mode, instruction, await MayManageOutboxAsync(), HttpContext.RequestAborted);
            return Json(new { ok = r.Ok, job = r.Job, code = r.Code });
        }

        [HttpGet]
        public async Task<IActionResult> AssistStatus(string job)
        {
            var me = await MeAsync(); if (me == null || string.IsNullOrEmpty(job)) return NotFound(new { ok = false });
            var s = await Assistant.StatusAsync(me.Value.companyId, me.Value.empId, job, HttpContext.RequestAborted);
            return s == null ? NotFound(new { ok = false }) : Json(s);
        }

        /// "Use this reply": the draft goes into the reply's compose box (as its first paragraph, above the quote),
        /// where the reader edits it and sends it - the assistant never sends anything itself.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UseDraft(int id, string? text, string? kind)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayUseMailAsync()) return Forbid();
            if (!string.IsNullOrWhiteSpace(text)) TempData["MailDraft"] = text.Length > 8000 ? text[..8000] : text;
            return RedirectToAction("Compose", new { parentId = id, kind = kind == "ReplyAll" ? "ReplyAll" : "Reply" });
        }

        /// The reader's badges, for the inbox to stay current without a reload (phase 3: live mailbox).
        [HttpGet]
        public async Task<IActionResult> Counts()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await CountsAsync(me.Value.companyId, me.Value.empId));
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
            // NO outbox-manage GATE ANY MORE: this deletes the caller's OWN copies (owner, 2026-11-23: a mailbox per
            // employee). The service matches only rows of this employee, so no id reaches a colleague's mail.
            if (!await MayUseMailAsync()) return Forbid();

            bool permanent = string.Equals(folder, "Trash", StringComparison.OrdinalIgnoreCase);
            int affected = permanent
                ? await _comm.DeleteForeverAsync(me.Value.companyId, me.Value.empId, ids ?? Array.Empty<int>())
                : await _comm.TrashAsync(me.Value.companyId, me.Value.empId, ids ?? Array.Empty<int>());

            // A LIST MUTATION ANSWERS IN PLACE. The page removes the rows it just acted on rather than
            // reloading - a reload would lose the scroll position and the user's place in a mailbox.
            // The message VIEW still redirects, because there the message being gone IS the navigation.
            // THE COUNTS COME BACK WITH THE ANSWER, so the page does not have to reload to stay
            // truthful. Recomputed server-side rather than decremented in the browser: the client
            // knows how many rows it removed, not how many were already trashed, starred or failed,
            // and a badge that drifts from the folder it names is worse than no badge.
            if (ajax) return Json(new { ok = true, affected, permanent, counts = await CountsAsync(me.Value.companyId, me.Value.empId) });
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
            if (!await MayUseMailAsync()) return Forbid();
            int affected = await _comm.RestoreAsync(me.Value.companyId, me.Value.empId, ids ?? Array.Empty<int>());
            if (ajax) return Json(new { ok = true, affected, counts = await CountsAsync(me.Value.companyId, me.Value.empId) });
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
            if (!await MayUseMailAsync()) return Forbid();
            int affected = await _comm.EmptyTrashAsync(me.Value.companyId, me.Value.empId);
            if (ajax) return Json(new { ok = true, affected, counts = await CountsAsync(me.Value.companyId, me.Value.empId) });
            return RedirectToAction("Index", new { folder = "Trash" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Star(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (!await MayUseMailAsync()) return Forbid();
            var ok = await _comm.ToggleStarAsync(me.Value.companyId, me.Value.empId, id);

            // STARRING MOVES A FOLDER COUNT, so it answers with the counts for the same reason delete
            // does: the Starred badge names a set this action just changed, and a badge that only
            // catches up on the next page load is wrong in between.
            return Json(new { ok, counts = await CountsAsync(me.Value.companyId, me.Value.empId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resend(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            // ONLY THE ONE WHO WROTE IT (or the outbox's manager) sends it again.
            if (!await MayUseMailAsync()) return Forbid();
            var owns = await _comm.IsSenderAsync(me.Value.companyId, me.Value.empId, id)
                || (await MayManageOutboxAsync() && await _db.CommMessages.AsNoTracking().AnyAsync(x => x.Id == id && x.CompanyID == me.Value.companyId));
            if (!owns) return NotFound();
            await _comm.TrySendAsync(id);
            return RedirectToAction("View", new { id });
        }
    }
}
