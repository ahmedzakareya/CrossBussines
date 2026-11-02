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

        // The platform's own entity vocabulary. A silo's LABEL, ICON and COLOUR are read from the
        // registered EntityDefinition rather than written out here, so a silo this screen has never
        // heard of still renders with its owner's name instead of being painted as something else.
        // Reached through ApprovalEntityMap, the silo -> entity-code mapping landed with U2.
        private readonly CrossBuy.BL.Platform.IEntityRegistry _entities;

        public ApprovalsController(IEmployeeService employees, CrossDbContext db,
            IStringLocalizer<CrossBuy.SharedResources> localizer,
            IApprovalInboxService inbox,
            CrossBuy.BL.Platform.IBusinessContextAccessor context,
            CrossBuy.BL.Platform.IEntityRegistry entities)
        { _employees = employees; _db = db; L = localizer; _inbox = inbox; _context = context; _entities = entities; }

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

            // THE REQUESTER'S PHOTOGRAPH. Resolved in ONE company-scoped lookup for every row on the
            // page, through the same helper the conversation panel and the activity timeline use, so
            // this screen cannot drift away from how the rest of the product draws a person. The helper
            // owns the tenancy predicate and the stored-path normalisation; repeating either here is
            // how four copies of the same query end up disagreeing.
            var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
                _db, context,
                page.Rows.Where(r => r.RequesterEmployeeId is > 0).Select(r => r.RequesterEmployeeId!.Value));

            string? requesterPhoto(PendingApprovalRow r)
                => r.RequesterEmployeeId is > 0 && photos.TryGetValue(r.RequesterEmployeeId.Value, out var p) ? p : null;

            string docTypeName(string? t) => t switch
            {
                "PurchaseOrder" => L["Purchase order"].Value,
                "StockTransfer" => L["Transfer"].Value,
                "StockCount" => L["Stock count"].Value,
                "WriteOff" => L["Write-off"].Value,
                _ => t ?? "",
            };

            // WHERE THIS ROW'S SCREEN IS, INCLUDING ITS IDENTIFIERS.
            //
            // The three original silos are addressed by action alone, so this used to be a two-argument
            // Url.Action and the route values the module supplied were silently dropped. Project billing
            // is the first silo whose screen needs them — /Project/Billing is addressed by project AND
            // billing — and without them the link resolves to the bare action, the project lookup fails
            // and the approver is bounced to the Projects LIST instead of the document they were sent to
            // act on. Centralised so the next silo that carries identifiers inherits the behaviour rather
            // than rediscovering the bug.
            string approvalHref(PendingApprovalRow r)
            {
                var values = new Microsoft.AspNetCore.Routing.RouteValueDictionary();
                if (r.Navigation.RouteValues != null)
                {
                    foreach (var kv in r.Navigation.RouteValues)
                        if (!string.IsNullOrWhiteSpace(kv.Value)) values[kv.Key] = kv.Value!;
                }
                return Url.Action(r.Navigation.Action, r.Navigation.Controller, values) ?? "#";
            }

            // THE SILO'S NAME, ICON AND COLOUR — FROM THE PLATFORM, NOT FROM THIS FILE.
            //
            // ApprovalEntityMap (U2) maps a silo to its canonical entity code; the registry then supplies
            // the owner's own bilingual name, icon and colour. That is why no new resource key is needed
            // for project billing: "مستخلص" / "Progress billing" is already the registered name of the
            // thing being approved, and a second copy here would be a second place to get it wrong.
            //
            // The registry stores Icon WITH its "ki-outline " prefix; the view adds that prefix itself,
            // so it is stripped here rather than rendered twice.
            (string Label, string Icon, string Color) siloChrome(string? silo)
            {
                var code = ApprovalEntityMap.CanonicalEntityType(silo);
                if (code != null && _entities.TryGetDefinition(code, out var def) && def != null)
                {
                    var icon = def.Icon.StartsWith("ki-outline ", StringComparison.Ordinal)
                        ? def.Icon["ki-outline ".Length..]
                        : def.Icon;
                    return (isAr ? def.DisplayNameAr : def.DisplayNameEn, icon, def.Color);
                }
                // An unmapped silo is a real state, not an error. It renders under its own key in a
                // neutral treatment — never borrowing another silo's name, which is the defect this
                // replaced: the default arm used to BE the Inventory arm, so a project billing awaiting
                // approval was presented to the approver as an Inventory document.
                return (silo ?? "", "ki-questionnaire-tablet", "secondary");
            }

            var rows = page.Rows.Select(r => r.Silo switch
            {
                ApprovalSilos.Leave => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Leave"].Value, Icon = "ki-calendar-tick", Color = "primary",
                    Title = nm(r.TitleAr, r.TitleEn),
                    Requester = requesterName(r),
                    RequesterEmployeeId = r.RequesterEmployeeId,
                    RequesterPhoto = requesterPhoto(r),
                    Meta = $"{r.PeriodStart:yyyy-MM-dd} → {r.PeriodEnd:yyyy-MM-dd} · {r.Days} {L["days"].Value}",
                    Date = r.SubmittedAt,
                    Url = approvalHref(r),
                },
                ApprovalSilos.Request => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Employee request"].Value, Icon = "ki-file-added", Color = "info",
                    Title = r.ApprovalType == "Permission" ? L["Permission"].Value : L["Letter"].Value,
                    Requester = requesterName(r),
                    RequesterEmployeeId = r.RequesterEmployeeId,
                    RequesterPhoto = requesterPhoto(r),
                    Meta = r.ApprovalType == "Permission"
                        ? $"{r.OnDate:yyyy-MM-dd} {r.FromTime}–{r.ToTime}"
                        : (r.TitleEn ?? ""),
                    Date = r.SubmittedAt,
                    Url = approvalHref(r),
                },
                // EXPLICIT, no longer the default arm. Inventory keeps the label, icon and colour it has
                // always had, so nothing about the three original silos changes on screen.
                ApprovalSilos.Inventory => new ApprovalInboxRow
                {
                    Silo = r.Silo, SiloLabel = L["Inventory"].Value, Icon = "ki-check-square", Color = "warning",
                    Title = docTypeName(r.ApprovalType),
                    Requester = requesterName(r),
                    RequesterEmployeeId = r.RequesterEmployeeId,
                    RequesterPhoto = requesterPhoto(r),
                    Meta = (r.Amount ?? 0m).ToString("N2"),
                    Date = r.SubmittedAt,
                    Url = approvalHref(r),
                },
                // PROJECT BILLING. The reader already composed a bilingual title that names the stage and
                // the project ("اعتماد مستخلص #12 - ...") — it is used as given rather than reduced to the
                // ApprovalType discriminator, which is what the old default arm did.
                ApprovalSilos.ProjectBilling => new ApprovalInboxRow
                {
                    Silo = r.Silo,
                    SiloLabel = siloChrome(r.Silo).Label,
                    Icon = siloChrome(r.Silo).Icon,
                    Color = siloChrome(r.Silo).Color,
                    Title = nm(r.TitleAr, r.TitleEn),
                    Requester = requesterName(r),
                    RequesterEmployeeId = r.RequesterEmployeeId,
                    RequesterPhoto = requesterPhoto(r),
                    Meta = (r.Amount ?? 0m).ToString("N2"),
                    Date = r.SubmittedAt,
                    Url = approvalHref(r),
                },
                // A SILO THIS SCREEN HAS NOT MET. It renders under its own registered name when the
                // registry knows it, and under its raw key when it does not — never under another silo's.
                _ => new ApprovalInboxRow
                {
                    Silo = r.Silo,
                    SiloLabel = siloChrome(r.Silo).Label,
                    Icon = siloChrome(r.Silo).Icon,
                    Color = siloChrome(r.Silo).Color,
                    Title = nm(r.TitleAr, r.TitleEn) is { Length: > 0 } and not "-"
                        ? nm(r.TitleAr, r.TitleEn)
                        : (r.ApprovalType ?? ""),
                    Requester = requesterName(r),
                    RequesterEmployeeId = r.RequesterEmployeeId,
                    RequesterPhoto = requesterPhoto(r),
                    Meta = r.Amount.HasValue ? r.Amount.Value.ToString("N2") : "",
                    Date = r.SubmittedAt,
                    Url = approvalHref(r),
                },
            }).ToList();

            ViewBag.LeaveCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Leave, out var lc) ? lc : 0;
            ViewBag.RequestCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Request, out var rc) ? rc : 0;
            ViewBag.InventoryCount = page.CountsBySilo.TryGetValue(ApprovalSilos.Inventory, out var ic) ? ic : 0;
            ViewBag.IsInvMgr = page.VisibleSilos.Contains(ApprovalSilos.Inventory);

            // THE FOURTH TILE. Without this the header badge counted every row while the tiles summed
            // only three silos, so the screen contradicted itself whenever a billing was pending.
            //
            // Billing entitlement is per PROJECT, so there is no company-wide flag to ask for: the silo
            // is visible exactly when the module returned something this caller may act on. A user who
            // can never approve a billing is therefore not shown a tile that would always read zero.
            ViewBag.ProjectBillingCount = page.CountsBySilo.TryGetValue(ApprovalSilos.ProjectBilling, out var pb) ? pb : 0;
            ViewBag.IsBillingApprover = page.VisibleSilos.Contains(ApprovalSilos.ProjectBilling);
            ViewBag.ProjectBillingLabel = siloChrome(ApprovalSilos.ProjectBilling).Label;
            ViewBag.ProjectBillingIcon = siloChrome(ApprovalSilos.ProjectBilling).Icon;
            return View(rows);
        }
    }
}
