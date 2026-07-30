using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Communication Hub — the full notifications screen (the bell's "View all"). Employee-scoped, paged, filterable.
    [SessionValidation]
    public class NotificationsController : Controller
    {
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        public NotificationsController(IEmployeeService employees, CrossDbContext db) { _employees = employees; _db = db; }

        private async Task<int?> CurrentEmployeeIdAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            return emp?.ID;
        }

        // Only low-priority/chatty categories may be muted — operational & approval alerts can never be hidden.
        private static readonly string[] MutableCategories = { "Chat", "CRM" };

        [HttpGet]
        public async Task<IActionResult> Prefs()
        {
            var empId = await CurrentEmployeeIdAsync();
            if (empId == null) return Unauthorized();
            var muted = await _db.NotificationMutes.AsNoTracking()
                .Where(x => x.EmployeeId == empId.Value).Select(x => x.Category).ToListAsync();
            return Json(new { categories = MutableCategories, muted });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleMute(string category)
        {
            var empId = await CurrentEmployeeIdAsync();
            if (empId == null) return Unauthorized();
            if (!MutableCategories.Contains(category)) return BadRequest();
            var ex = await _db.NotificationMutes.FirstOrDefaultAsync(x => x.EmployeeId == empId.Value && x.Category == category);
            if (ex != null) _db.NotificationMutes.Remove(ex);
            else _db.NotificationMutes.Add(new Models.Context.Admin.NotificationMute { EmployeeId = empId.Value, Category = category, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
            return Json(new { ok = true, muted = ex == null });
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? q, string? category, bool? unread, int page = 1)
        {
            var empId = await CurrentEmployeeIdAsync();
            if (empId == null) return RedirectToAction("Login", "Account");

            const int pageSize = 20;
            if (page < 1) page = 1;

            var query = _db.Notifications.AsNoTracking().Where(n => n.RecipientEmployeeID == empId.Value);
            if (unread == true) query = query.Where(n => !n.IsRead);
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(n => n.Category == category);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = q.Trim();
                query = query.Where(n =>
                    (n.TitleAr != null && n.TitleAr.Contains(t)) || (n.TitleEn != null && n.TitleEn.Contains(t)) ||
                    (n.BodyAr != null && n.BodyAr.Contains(t)) || (n.BodyEn != null && n.BodyEn.Contains(t)));
            }

            var total = await query.CountAsync();
            var items = await query.OrderByDescending(n => n.ID)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            ViewBag.Q = q; ViewBag.Category = category; ViewBag.Unread = unread;
            ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Total = total;
            ViewBag.Pages = (int)Math.Ceiling(total / (double)pageSize);
            ViewBag.UnreadCount = await _db.Notifications.CountAsync(n => n.RecipientEmployeeID == empId.Value && !n.IsRead);
            ViewBag.Categories = await _db.Notifications.AsNoTracking()
                .Where(n => n.RecipientEmployeeID == empId.Value && n.Category != null)
                .Select(n => n.Category!).Distinct().OrderBy(c => c).ToListAsync();

            // actor photos → shown as the notification avatar
            var actorIds = items.Where(n => n.ActorEmployeeID.HasValue).Select(n => n.ActorEmployeeID!.Value).Distinct().ToList();
            ViewBag.ActorAvatars = await _db.Employee.AsNoTracking()
                .Where(e => actorIds.Contains(e.ID) && e.ProfileImage != null && e.ProfileImage != "")
                .ToDictionaryAsync(e => e.ID, e => e.ProfileImage);
            return View(items);
        }
    }
}