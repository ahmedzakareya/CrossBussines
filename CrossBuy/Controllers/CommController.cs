using System.Security.Claims;
using CrossBuy.BL;
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
        public CommController(ICommService comm, IEmployeeService employees, CrossDbContext db, IWebHostEnvironment env, IFileManagerService fm)
        { _comm = comm; _employees = employees; _db = db; _env = env; _fm = fm; }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Trash(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _comm.TrashAsync(me.Value.companyId, id);
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Star(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _comm.ToggleStarAsync(me.Value.companyId, id);
            return Json(new { ok });
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
