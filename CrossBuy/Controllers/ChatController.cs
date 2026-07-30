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
    public class ChatController : Controller
    {
        private readonly IChatService _chat;
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        public ChatController(IChatService chat, IEmployeeService employees, CrossDbContext db, IWebHostEnvironment env)
        { _chat = chat; _employees = employees; _db = db; _env = env; }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? c)
        {
            var me = await MeAsync();
            if (me == null) return RedirectToAction("Login", "Account");
            ViewBag.MeId = me.Value.empId;
            ViewBag.OpenConv = c;
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
            if (file != null && file.Length > 0)
            {
                var dir = Path.Combine(_env.WebRootPath, "uploads", "chat");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var ext = Path.GetExtension(file.FileName);
                var stored = Guid.NewGuid() + ext;
                using (var s = System.IO.File.Create(Path.Combine(dir, stored))) await file.CopyToAsync(s);
                path = "/uploads/chat/" + stored; name = Path.GetFileName(file.FileName);
                type = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }.Contains(ext.ToLowerInvariant()) ? "image" : "file";
            }
            var ids = (mentionedIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            var dto = await _chat.SendAsync(me.Value.companyId, me.Value.empId, c, body, path, name, type, replyToId, ids);
            return dto == null ? BadRequest() : Json(dto);
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
    }
}
