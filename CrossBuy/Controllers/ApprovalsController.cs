using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Approvals;
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
    //
    // MIGRATED to the reusable read platform. This action used to run the three-silo union inline, with
    // each silo's company boundary written out here. That made the boundaries unreusable — Workspace
    // could only have re-implemented them — and they are the code that has already needed two security
    // fixes (96cb210, 87ec8fa). IApprovalInboxService now owns the union and each MODULE owns its own
    // boundary. What is left here is presentation: culture, labels, icons, colours and the formatted
    // period text, which is why the rendered page is unchanged.
    [SessionValidation]
    public class ApprovalsController : Controller
    {
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        private readonly IStringLocalizer<CrossBuy.SharedResources> L;

        // The reusable cross-silo inbox, and the server-side company resolver.
        private readonly IApprovalInboxService _inbox;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _context;

        public ApprovalsController(IEmployeeService employees, CrossDbContext db,
            IStringLocalizer<CrossBuy.SharedResources> localizer,
            IApprovalInboxService inbox,
            CrossBuy.BL.Platform.IBusinessContextAccessor context)
        { _employees = employees; _db = db; L = localizer; _inbox = inbox; _context = context; }

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
            // COMPANY ISOLATION. The company is resolved ONCE, server-side: BusinessContextFactory reads
            // the signed-in employee's own Employee.EmpCompanyID, and a session value that disagrees is
            // logged and loses. It is never taken from a query string, form field, route value or session
            // blob, and there is no company-1 fallback left to land on. Each module reader then applies
            // its own boundary to this context.
            //
            // FAIL CLOSED: if no company resolves, no approval can be attributed to one, so the inbox is
            // NOT rendered unfiltered — the request goes back to sign-in.
            // ---------------------------------------------------------------------------------------------
            var context = await _context.TryGetCurrentAsync();
            if (context == null || context.CompanyId <= 0) return RedirectToAction("Login", "Account");

            // take: unlimited. This screen has always listed every pending row; Workspace is the consumer
            // that asks for a short list.
            var page = await _inbox.GetPendingForCurrentApproverAsync(context, empId.Value);

            bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            string nm(string? a, string? b) => (isAr ? (a ?? b) : (b ?? a)) ?? "-";

            // Requester names the modules did not carry (inventory records only an employee id). Resolved
            // in ONE lookup, as before. Employee is not an approval table — the three silo tables are no
            // longer reachable from this controller at all.
            var missingNames = page.Rows
                .Where(r => r.RequesterNameAr == null && r.RequesterNameEn == null && r.RequesterEmployeeId is > 0)
                .Select(r => r.RequesterEmployeeId!.Value).Distinct().ToList();
            var names = missingNames.Count == 0
                ? new Dictionary<int, (string? FullName, string? FullNameEn)>()
                : await _db.Employee.AsNoTracking().Where(e => missingNames.Contains(e.ID))
                    .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                    .ToDictionaryAsync(e => e.ID, e => (e.FullName, e.FullNameEn));

            string requesterName(PendingApprovalRow r)
            {
                if (r.RequesterNameAr != null || r.RequesterNameEn != null)
                    return nm(r.RequesterNameAr, r.RequesterNameEn);
                if (r.RequesterEmployeeId is > 0 && names.TryGetValue(r.RequesterEmployeeId.Value, out var v))
                    return nm(v.FullName, v.FullNameEn);
                return "-";
            }

            string docTypeName(string? t) => t switch
            {
                "PurchaseOrder" => L["Purchase order"].Value,
                "StockTransfer" => L["Transfer"].Value,
                "StockCount" => L["Stock count"].Value,
                "WriteOff" => L["Write-off"].Value,
                _ => t ?? "",
            };

            var rows = page.Rows.Select(r => r.Silo switch
            {
                ApprovalSilos.Leave => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Leave"].Value, Icon = "ki-calendar-tick", Color = "primary",
                    Title = nm(r.TitleAr, r.TitleEn),
                    Requester = requesterName(r),
                    Meta = $"{r.PeriodStart:yyyy-MM-dd} → {r.PeriodEnd:yyyy-MM-dd} · {r.Days} {L["days"].Value}",
                    Date = r.SubmittedAt,
                    Url = Url.Action(r.Navigation.Action, r.Navigation.Controller) ?? "#",
                },
                ApprovalSilos.Request => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Employee request"].Value, Icon = "ki-file-added", Color = "info",
                    Title = r.ApprovalType == "Permission" ? L["Permission"].Value : L["Letter"].Value,
                    Requester = requesterName(r),
                    Meta = r.ApprovalType == "Permission"
                        ? $"{r.OnDate:yyyy-MM-dd} {r.FromTime}–{r.ToTime}"
                        : (r.TitleEn ?? ""),
                    Date = r.SubmittedAt,
                    Url = Url.Action(r.Navigation.Action, r.Navigation.Controller) ?? "#",
                },
                _ => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Inventory"].Value, Icon = "ki-check-square", Color = "warning",
                    Title = docTypeName(r.ApprovalType),
                    Requester = requesterName(r),
                    Meta = (r.Amount ?? 0m).ToString("N2"),
                    Date = r.SubmittedAt,
                    Url = Url.Action(r.Navigation.Action, r.Navigation.Controller) ?? "#",
                },
            }).ToList();

            ViewBag.LeaveCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Leave, out var lc) ? lc : 0;
            ViewBag.RequestCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Request, out var rc) ? rc : 0;
            ViewBag.InventoryCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Inventory, out var ic) ? ic : 0;
            ViewBag.IsInvMgr = page.VisibleSilos.Contains(ApprovalSilos.Inventory);
            return View(rows);
        }
    }
}
