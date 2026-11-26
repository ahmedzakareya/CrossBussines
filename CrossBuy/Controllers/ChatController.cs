using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Communication Hub P3 — internal Teams-style chat. Renders the chat shell + JSON endpoints;
    // realtime is over ChatHub (/hubs/chat). Internal employee↔employee only.
    [SessionValidation]
    [AutoValidateAntiforgeryToken]
    public partial class ChatController : Controller
    {
        private readonly IChatService _chat;
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
        private readonly IFileManagerService _files;

        public ChatController(IChatService chat, IEmployeeService employees, CrossDbContext db, IWebHostEnvironment env,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts, IFileManagerService files)
        { _chat = chat; _employees = employees; _db = db; _env = env; _contexts = contexts; _files = files; }

        // F5 — THE COMPANY COMES FROM THE RESOLVED BUSINESS CONTEXT.
        //
        // This used to read Employee.EmpCompanyID: a column on the person's own record. It is not a
        // resolved request scope, it ignores company switching entirely, and it made the authorization
        // company a property of the user row rather than of the request the platform actually resolved.
        // Every other module answers this question through IBusinessContextAccessor, so chat does too.
        //
        // FAIL CLOSED. An unresolved context returns null and every action refuses. That is deliberate:
        // "we could not establish which company you are acting for" must never degrade into "carry on
        // without a company filter". ChatService resolves the context independently for exactly the same
        // reason, so even a caller that got past this one cannot act unscoped.
        private async Task<(int empId, int companyId)?> MeAsync()
        {
            CrossBuy.Models.Platform.BusinessContext? ctx;
            try { ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted); }
            catch (Exception) { return null; }
            if (ctx == null || ctx.CompanyId <= 0 || ctx.EmployeeId is not > 0) return null;
            return (ctx.EmployeeId.Value, ctx.CompanyId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? c, string? m = null, string? tab = null)
        {
            var me = await MeAsync();
            if (me == null) return RedirectToAction("Login", "Account");
            ViewBag.MeId = me.Value.empId;
            ViewBag.OpenConv = c;
            // Communication opens INSIDE the module the person came from (owner, 2026-11-18): its layout and its menu
            ViewBag.CommLayout = CommLayout(m);
            ViewBag.Tab = tab is "calls" or "people" ? tab : "chats";
            ViewBag.Hospital = await HospitalOnAsync();
            ViewBag.CanManage = await MayManageCallsAsync();
            var self = await _db.Employee.AsNoTracking().Where(e => e.ID == me.Value.empId).Select(e => new { e.FullName, e.FullNameEn, e.ProfileImage }).FirstOrDefaultAsync();
            ViewBag.MeName = self == null ? "" : DisplayName.Of(self.FullName, self.FullNameEn ?? self.FullName);
            ViewBag.MeAvatar = string.IsNullOrEmpty(self?.ProfileImage) ? null : self.ProfileImage;
            // my name in the other language, so a mention of me typed in either language is shown as one
            ViewBag.MeAlt = self == null ? null : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? self.FullNameEn : self.FullName;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Conversations()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _chat.MyConversationsAsync(me.Value.companyId, me.Value.empId));
        }

        [HttpGet]
        public async Task<IActionResult> Header(int c)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var h = await _chat.GetHeaderAsync(me.Value.companyId, me.Value.empId, c);
            return h == null ? NotFound() : Json(h);
        }

        [HttpGet]
        public async Task<IActionResult> Messages(int c, int? before)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _chat.GetMessagesAsync(me.Value.empId, c, before));
        }

        [HttpGet]
        public async Task<IActionResult> Directory(string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _chat.DirectoryAsync(me.Value.companyId, me.Value.empId, q));
        }

        // GET /Chat/Search?q=…  — messages across the conversations this caller belongs to.
        //
        // The service resolves the company and the authority itself and returns an empty list when
        // either is missing, so this action adds no gate of its own: a second rule here would be a
        // second place for the two to disagree.
        public async Task<IActionResult> Search(string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _chat.SearchMessagesAsync(q));
        }

        [HttpPost]
        public async Task<IActionResult> StartDirect(int otherId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var id = await _chat.GetOrCreateDirectAsync(me.Value.companyId, me.Value.empId, otherId);
            return Json(new { id });
        }

        [HttpPost]
        public async Task<IActionResult> CreateGroup(string title, string memberIds)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(title)) return BadRequest();
            var ids = (memberIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            var id = await _chat.CreateGroupAsync(me.Value.companyId, me.Value.empId, title.Trim(), ids);
            return Json(new { id });
        }

        [HttpPost]
        [RequestSizeLimit(52_428_800)] // 50 MB
        public async Task<IActionResult> Send(int c, string? body, int? replyToId, string? mentionedIds, Microsoft.AspNetCore.Http.IFormFile? file)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            string? path = null, name = null, type = null;
            int? libraryItemId = null;
            string? physicalPath = null;
            if (file != null && file.Length > 0)
            {
                // F8-A — AUTHORIZE BEFORE ANYTHING REACHES DISK.
                // The upload used to be written first and the message authorized afterwards, inside
                // SendAsync. A non-member's message was correctly refused, but their file had already been
                // saved under wwwroot and stayed there with nothing referencing it — an unauthorized write
                // and an accumulating orphan. Membership is now proved first, so a refused upload never
                // creates a file (F8-C).
                // Refused as JSON, not Forbid(): under the cookie scheme Forbid() issues a 302 to the
                // access-denied page, and this endpoint is called by fetch() — the client would follow the
                // redirect and try to parse a login page as JSON. The refusal carries no detail about
                // whether the conversation exists.
                if (!await _chat.CanSendToAsync(c))
                    return new ObjectResult(new { ok = false, code = "not_found" }) { StatusCode = StatusCodes.Status404NotFound };

                // F8-B — CONSERVATIVE ALLOW-LIST, EXTENSION *AND* DECLARED TYPE.
                // The old code accepted any extension and only used it to choose an icon. Files land under
                // Chat files are stored under the company's File Manager library, in its isolated "chat"
                // subfolder. The static-file gate denies that subfolder entirely; message links are served
                // only by Attachment after the conversation membership check below. Active content is also
                // refused so a member cannot execute an uploaded script in the application's origin.
                var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
                if (!ChatAttachments.IsAcceptedType(ext, file.ContentType)) return BadRequest(new { ok = false, code = "attachment_type" });

                var dir = Path.Combine(_env.WebRootPath, "uploads", "library", me.Value.companyId.ToString(), "chat");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                // The stored name stays a GUID plus the validated extension — the client filename is never
                // used to build a path, only kept as a display string.
                var stored = Guid.NewGuid().ToString("N") + ext;
                physicalPath = Path.Combine(dir, stored);
                using (var s = System.IO.File.Create(physicalPath)) await file.CopyToAsync(s);
                var storedPath = "/uploads/library/" + me.Value.companyId + "/chat/" + stored;
                name = Path.GetFileName(file.FileName);
                try
                {
                    libraryItemId = await _files.AddFileAsync(me.Value.companyId, me.Value.empId, null,
                        name, null, storedPath, file.ContentType, file.Length);
                }
                catch
                {
                    System.IO.File.Delete(physicalPath);
                    throw;
                }
                path = $"/Chat/Attachment?messageId={{messageId}}&fileId={libraryItemId.Value}";
                type = ChatAttachments.IsImage(ext) ? "image" : ChatAttachments.IsAudio(ext) ? "audio" : "file";
            }
            var ids = (mentionedIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            ChatMsgDto? dto;
            try
            {
                dto = await _chat.SendAsync(me.Value.companyId, me.Value.empId, c, body, path, name, type, replyToId, ids);
            }
            catch
            {
                await RemoveUnreferencedUploadAsync(me.Value.companyId, me.Value.empId, libraryItemId, physicalPath);
                throw;
            }
            if (dto == null)
            {
                // A rejected message must not leave a company-library orphan behind.
                await RemoveUnreferencedUploadAsync(me.Value.companyId, me.Value.empId, libraryItemId, physicalPath);
                return BadRequest();
            }
            return Json(dto);
        }

        private async Task RemoveUnreferencedUploadAsync(int companyId, int employeeId, int? itemId, string? physicalPath)
        {
            // SendAsync may throw after persisting the message (for example, while notifying SignalR).
            // In that case retain the library row and bytes because the message already references them.
            if (itemId.HasValue && await _db.ChatMessages.AsNoTracking().AnyAsync(m =>
                    m.DeletedAt == null && m.AttachmentPath != null && m.AttachmentPath.EndsWith($"&fileId={itemId.Value}")))
                return;
            if (itemId.HasValue)
                await _files.DeleteChatAttachmentAsync(companyId, employeeId, itemId.Value);
            if (!string.IsNullOrWhiteSpace(physicalPath) && System.IO.File.Exists(physicalPath))
                System.IO.File.Delete(physicalPath);
        }

        [HttpGet]
        public async Task<IActionResult> Attachment(int messageId, int fileId, bool preview = false, bool download = false)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (messageId <= 0 || fileId <= 0) return NotFound();
            var expectedPath = $"/Chat/Attachment?messageId={messageId}&fileId={fileId}";
            var message = await _db.ChatMessages.AsNoTracking()
                .Where(m => m.ID == messageId && m.DeletedAt == null && m.AttachmentPath == expectedPath)
                .Select(m => new { m.ConversationId }).FirstOrDefaultAsync();
            if (message == null || !await _chat.CanReadAsync(message.ConversationId)) return NotFound();

            var item = await _files.GetChatAttachmentAsync(me.Value.companyId, fileId);
            if (item == null || string.IsNullOrWhiteSpace(item.StoredPath) ||
                !FileManagerPaths.TryResolveLibraryFile(_env.WebRootPath, item.StoredPath, out var physicalPath) ||
                !System.IO.File.Exists(physicalPath)) return NotFound();

            var contentType = item.ContentType ?? "application/octet-stream";
            if (preview && ChatAttachments.IsSafeInlinePreview(item.Name, contentType))
            {
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                Response.Headers["Cache-Control"] = "private, no-store";
                return PhysicalFile(physicalPath, contentType, enableRangeProcessing: true);
            }
            if (download)
                return PhysicalFile(physicalPath, contentType, item.Name, enableRangeProcessing: true);
            if (ChatAttachments.IsImage(Path.GetExtension(item.Name)))
                return PhysicalFile(physicalPath, contentType, enableRangeProcessing: true);
            return PhysicalFile(physicalPath, contentType, item.Name, enableRangeProcessing: true);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int id, string body)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(new { ok = await _chat.EditAsync(me.Value.empId, id, body) });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(new { ok = await _chat.DeleteAsync(me.Value.empId, id) });
        }

        [HttpPost]
        public async Task<IActionResult> React(int id, string emoji)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _chat.ToggleReactionAsync(me.Value.empId, id, emoji);
            return Json(new { ok = true });
        }

        [HttpPost]
        public async Task<IActionResult> MarkRead(int c)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _chat.MarkReadAsync(me.Value.empId, c);
            return Json(new { ok = true });
        }

        // Group management — any member may add; only the Owner may remove others (anyone may remove/leave themselves).
        [HttpPost]
        public async Task<IActionResult> AddMember(int c, int employeeId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _chat.AddMemberAsync(me.Value.companyId, me.Value.empId, c, employeeId);
            return Json(new { ok });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveMember(int c, int employeeId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _chat.RemoveMemberAsync(me.Value.companyId, me.Value.empId, c, employeeId);
            return Json(new { ok });
        }
    }
}
