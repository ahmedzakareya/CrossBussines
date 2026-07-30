using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
    // Communication Hub — unified "My pending approvals" inbox. READ-MODEL union over the existing
    // approval silos (leave · employee-request · inventory). It never writes: each row links to the
    // silo's own screen to actually approve/reject (no duplicated business logic).
    [SessionValidation]
    public class ApprovalsController : Controller
    {
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly IStringLocalizer<CrossBuy.SharedResources> L;
        public ApprovalsController(IEmployeeService employees, CrossDbContext db, IStringLocalizer<CrossBuy.SharedResources> localizer)
        { _employees = employees; _db = db; L = localizer; }

        private async Task<int?> CurrentEmployeeIdAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            return emp?.ID;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var empId = await CurrentEmployeeIdAsync();
            if (empId == null) return RedirectToAction("Login", "Account");
            bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            string nm(string? a, string? b) => (isAr ? (a ?? b) : (b ?? a)) ?? "-";

            var rows = new List<ApprovalInboxRow>();

            // ----- Silo 1: leave requests pending on me -----
            var leaves = await _db.LeaveRequests.AsNoTracking()
                .Include(r => r.Employee).Include(r => r.LeaveType)
                .Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == empId.Value)
                .OrderByDescending(r => r.ID).ToListAsync();
            foreach (var r in leaves)
                rows.Add(new ApprovalInboxRow
                {
                    Silo = "Leave", SiloLabel = L["Leave"].Value, Icon = "ki-calendar-tick", Color = "primary",
                    Title = nm(r.LeaveType?.NameAr, r.LeaveType?.NameEn),
                    Requester = nm(r.Employee?.FullName, r.Employee?.FullNameEn),
                    Meta = $"{r.StartDate:yyyy-MM-dd} → {r.EndDate:yyyy-MM-dd} · {r.Days} {L["days"].Value}",
                    Date = r.CreatedAt ?? r.StartDate,
                    Url = Url.Action("Leaves", "People") ?? "#",
                });

            // ----- Silo 2: employee requests (letters / permissions) pending on me -----
            var reqs = await _db.EmployeeRequests.AsNoTracking()
                .Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == empId.Value)
                .OrderByDescending(r => r.ID).ToListAsync();
            var reqEmpIds = reqs.Select(r => r.EmployeeID).Distinct().ToList();

            // ----- Silo 3: inventory approvals — only if I'm an InventoryManager, and not my own requests -----
            var isInvMgr = await _db.InventoryUserRoles.AsNoTracking()
                .AnyAsync(r => r.EmployeeId == empId.Value && r.Role == "InventoryManager");
            List<Models.Context.Inventory.InventoryApproval> invs = new();
            if (isInvMgr)
                invs = await _db.InventoryApprovals.AsNoTracking()
                    .Where(a => a.Status == "Pending" && a.RequestedByEmployeeId != empId.Value)
                    .OrderByDescending(a => a.ID).ToListAsync();

            // resolve names for the request + inventory rows in one lookup
            var nameIds = reqEmpIds.Concat(invs.Select(a => a.RequestedByEmployeeId).Where(x => x.HasValue).Select(x => x!.Value)).Distinct().ToList();
            var names = await _db.Employee.AsNoTracking().Where(e => nameIds.Contains(e.ID))
                .ToDictionaryAsync(e => e.ID, e => new { e.FullName, e.FullNameEn });
            string empName(int id) => names.TryGetValue(id, out var v) ? nm(v.FullName, v.FullNameEn) : "-";

            foreach (var r in reqs)
            {
                var kind = r.RequestType == "Permission" ? L["Permission"].Value : L["Letter"].Value;
                var meta = r.RequestType == "Permission"
                    ? $"{r.PermissionDate:yyyy-MM-dd} {r.FromTime}–{r.ToTime}"
                    : (r.LetterType ?? "");
                rows.Add(new ApprovalInboxRow
                {
                    Silo = "Request", SiloLabel = L["Employee request"].Value, Icon = "ki-file-added", Color = "info",
                    Title = kind, Requester = empName(r.EmployeeID), Meta = meta,
                    Date = r.CreatedAt, Url = Url.Action("Requests", "People") ?? "#",
                });
            }

            string docTypeName(string t) => t switch
            {
                "PurchaseOrder" => L["Purchase order"].Value,
                "StockTransfer" => L["Transfer"].Value,
                "StockCount" => L["Stock count"].Value,
                "WriteOff" => L["Write-off"].Value,
                _ => t,
            };
            foreach (var a in invs)
                rows.Add(new ApprovalInboxRow
                {
                    Silo = "Inventory", SiloLabel = L["Inventory"].Value, Icon = "ki-check-square", Color = "warning",
                    Title = docTypeName(a.DocType), Requester = empName(a.RequestedByEmployeeId ?? 0),
                    Meta = a.Amount.ToString("N2"), Date = a.RequestedAt,
                    Url = Url.Action("Approvals", "Inventory") ?? "#",
                });

            var ordered = rows.OrderByDescending(r => r.Date ?? DateTime.MinValue).ToList();
            ViewBag.LeaveCount = leaves.Count;
            ViewBag.RequestCount = reqs.Count;
            ViewBag.InventoryCount = invs.Count;
            ViewBag.IsInvMgr = isInvMgr;
            return View(ordered);
        }
    }
}