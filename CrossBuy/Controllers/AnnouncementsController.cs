using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Communication Hub P5 — company/branch announcements.
    [SessionValidation]
    public class AnnouncementsController : Controller
    {
        private readonly IAnnouncementService _ann;
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly ICommunicationAccessService _access;
        private readonly IBusinessContextAccessor _contexts;
        public AnnouncementsController(IAnnouncementService ann, IEmployeeService employees, CrossDbContext db,
            ICommunicationAccessService access, IBusinessContextAccessor contexts)
        { _ann = ann; _employees = employees; _db = db; _access = access; _contexts = contexts; }

        private async Task<bool> MayPublishAsync(int companyId, int? branchId)
        {
            var context = await _contexts.TryGetCurrentAsync();
            if (context?.EmployeeId is not > 0 || context.CompanyId != companyId) return false;
            return await _access.CanAsync(context, CommunicationActions.AnnouncementSend,
                new PermissionTarget { BranchId = branchId });
        }

        private async Task<(int empId, int companyId, int? branchId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var e = await _db.Employee.AsNoTracking().Where(x => x.ID == emp.ID).Select(x => new { x.EmpCompanyID, x.BranchID }).FirstOrDefaultAsync();
            return (emp.ID, e?.EmpCompanyID ?? 0, e?.BranchID);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            // Dictionary (public type) — NOT an anonymous type, which a runtime-compiled Razor view can't bind via dynamic.
            ViewBag.Branches = await _db.Branches.AsNoTracking().Where(b => b.CompanyID == me.Value.companyId)
                .ToDictionaryAsync(b => b.ID, b => b.Name);
            var list = await _ann.ListAsync(me.Value.companyId);
            return View(list);
        }

        // SEC-02 — a company-wide publication that changed state with no CSRF protection. The caller
        // (Views/Announcements/Index.cshtml) now sends the token with its FormData.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string title, string body, string priority, string scope, int? branchId, DateTime? startsAt, DateTime? expiresAt)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return BadRequest();
            var normalizedScope = scope?.Trim();
            if (!string.Equals(normalizedScope, "Company", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalizedScope, "Branch", StringComparison.OrdinalIgnoreCase)) return BadRequest();

            if (string.Equals(normalizedScope, "Branch", StringComparison.OrdinalIgnoreCase))
            {
                if (branchId is not > 0 || !await _db.Branches.AsNoTracking()
                        .AnyAsync(b => b.ID == branchId.Value && b.CompanyID == me.Value.companyId)) return BadRequest();
            }
            else branchId = null;

            if (!await MayPublishAsync(me.Value.companyId, branchId)) return Forbid();
            var id = await _ann.CreateAsync(me.Value.companyId, me.Value.empId, title, body, priority ?? "Normal",
                normalizedScope!, branchId, startsAt, expiresAt);
            return Json(new { id });
        }

        [HttpGet]
        public async Task<IActionResult> Active()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _ann.ActiveForAsync(me.Value.companyId, me.Value.branchId, me.Value.empId));
        }

        // SEC-02 — token now sent by _AnnouncementsBanner, which renders its own.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Dismiss(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _ann.DismissAsync(me.Value.empId, id);
            return Json(new { ok = true });
        }

        // SEC-02 — activate/deactivate is a state change; token now sent by the admin view.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id, bool active)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var target = await _db.Announcements.AsNoTracking()
                .Where(a => a.Id == id && a.CompanyID == me.Value.companyId)
                .Select(a => new { a.Scope, a.BranchID }).FirstOrDefaultAsync();
            if (target == null) return NotFound();
            var branchId = string.Equals(target.Scope, "Branch", StringComparison.OrdinalIgnoreCase)
                ? target.BranchID : null;
            if (string.Equals(target.Scope, "Branch", StringComparison.OrdinalIgnoreCase) && branchId is not > 0)
                return Forbid();
            if (!await MayPublishAsync(me.Value.companyId, branchId)) return Forbid();
            await _ann.SetActiveAsync(me.Value.companyId, id, active);
            return Json(new { ok = true });
        }
    }
}
