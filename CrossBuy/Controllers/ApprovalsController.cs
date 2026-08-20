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

        // The company-isolated Inventory approval owner service, and the server-side company resolver.
        // Both replace inline queries that named no company at all — see Index.
        private readonly IInventoryApprovalService _inventoryApprovals;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _context;

        public ApprovalsController(IEmployeeService employees, CrossDbContext db,
            IStringLocalizer<CrossBuy.SharedResources> localizer,
            IInventoryApprovalService inventoryApprovals,
            CrossBuy.BL.Platform.IBusinessContextAccessor context)
        { _employees = employees; _db = db; L = localizer; _inventoryApprovals = inventoryApprovals; _context = context; }

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

            // ---------------------------------------------------------------------------------------------
            // COMPANY ISOLATION. All three silos below are company-sensitive, so the company is resolved
            // ONCE, server-side: BusinessContextFactory reads the signed-in employee's own
            // Employee.EmpCompanyID, and a session value that disagrees is logged and loses. It is never
            // taken from a query string, form field, route value or session blob, and there is no company-1
            // fallback left to land on.
            //
            // FAIL CLOSED: if no company resolves, no approval can be attributed to one, so the inbox is
            // NOT rendered unfiltered — the request goes back to sign-in.
            // ---------------------------------------------------------------------------------------------
            var context = await _context.TryGetCurrentAsync();
            if (context == null || context.CompanyId <= 0) return RedirectToAction("Login", "Account");
            int companyId = context.CompanyId;

            bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            string nm(string? a, string? b) => (isAr ? (a ?? b) : (b ?? a)) ?? "-";

            var rows = new List<ApprovalInboxRow>();

            // ----- Silo 1: leave requests pending on me -----
            // LeaveRequests carries no CompanyID column and none is invented here. The boundary comes from
            // the REQUESTER's own Employee row instead: a leave request reaches this inbox only if its
            // requester belongs to the approver's resolved company.
            //
            // This is a READ-side defence, deliberately independent of how the approver was assigned.
            // LeaveWorkflowService's upward climb walks `Hierarchical`, which has no CompanyID, so it can
            // still graft a foreign manager onto a request; that ASSIGNMENT defect is reported separately and
            // not fixed here. Filtering the read means such a request never reaches a foreign approver's
            // screen even while the assignment path stays open.
            var leaves = await _db.LeaveRequests.AsNoTracking()
                .Include(r => r.Employee).Include(r => r.LeaveType)
                .Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == empId.Value
                    && r.Employee != null && r.Employee.EmpCompanyID == companyId)
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
            // EmployeeRequests DOES carry CompanyID — written from the requester's own Employee.EmpCompanyID —
            // so the isolation predicate is stated explicitly. Employee.ID being a global primary key means
            // the approver match already rules out an id COLLISION across companies, but that is
            // identity-bound filtering, not isolation: it holds only as long as approver assignment is
            // correct. Stated explicitly, a row belonging to another company cannot surface here even if a
            // cross-company approver assignment exists.
            var reqs = await _db.EmployeeRequests.AsNoTracking()
                .Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == empId.Value
                    && r.CompanyID == companyId)
                .OrderByDescending(r => r.ID).ToListAsync();
            var reqEmpIds = reqs.Select(r => r.EmployeeID).Distinct().ToList();

            // ----- Silo 3: inventory approvals -----
            // DELEGATED to the owner service. This block used to query _db.InventoryUserRoles and
            // _db.InventoryApprovals inline with no company predicate on either, so any inventory manager saw
            // every company's pending approvals, and a role granted in one company unlocked the inbox in all
            // of them. The service now answers the gate and the rows together, company-scoped, from the
            // server-resolved context. This controller no longer touches either table.
            var (isInvMgr, invs) = await _inventoryApprovals.ApprovalInboxAsync(context, empId.Value);

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