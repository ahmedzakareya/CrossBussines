using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
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
        public AnnouncementsController(IAnnouncementService ann, IEmployeeService employees, CrossDbContext db)
        { _ann = ann; _employees = employees; _db = db; }

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

        [HttpPost]
        public async Task<IActionResult> Create(string title, string body, string priority, string scope, int? branchId, DateTime? startsAt, DateTime? expiresAt)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return BadRequest();
            var id = await _ann.CreateAsync(me.Value.companyId, me.Value.empId, title, body, priority ?? "Normal", scope ?? "Company", branchId, startsAt, expiresAt);
            return Json(new { id });
        }

        [HttpGet]
        public async Task<IActionResult> Active()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _ann.ActiveForAsync(me.Value.companyId, me.Value.branchId, me.Value.empId));
        }

        [HttpPost]
        public async Task<IActionResult> Dismiss(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _ann.DismissAsync(me.Value.empId, id);
            return Json(new { ok = true });
        }

        [HttpPost]
        public async Task<IActionResult> Toggle(int id, bool active)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            await _ann.SetActiveAsync(me.Value.companyId, id, active);
            return Json(new { ok = true });
        }
    }
}
