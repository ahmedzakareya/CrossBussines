using CrossBuy.BL;
using CrossBuy.BL.Hr;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // EMPLOYEE ONBOARDING — the screen's controller.
    //
    // THIN BY DESIGN. Every decision — may this caller act, does this employee belong to them, is
    // this requirement satisfied, may the plan complete — is made by IEmployeeOnboardingService. This
    // file resolves a route value, calls the service, and renders what comes back. There is no
    // company parameter anywhere on it, and no place to put one.
    //
    // WHY THERE IS A VISIBLE GATE HERE ANYWAY. The service is the authority — it re-asks every
    // question below with the SUBJECT employee in hand, and it is what an API caller hits. So the
    // gate on each action is, strictly, redundant.
    //
    // It is here for two reasons, and the first is the real one. CBA001 refused to compile this file
    // without it, and the analyzer was right: it follows a call graph, and IEmployeeOnboardingService
    // cannot be resolved to an implementation statically. So the authorization was invisible — not
    // only to the analyzer, but to anyone reading the controller, who would see four mutating POST
    // actions with nothing guarding them and no way to tell whether that was deliberate.
    //
    // The gate is deliberately COARSER than the service's ("may this caller manage HR employees at
    // all", no subject). A coarse pre-check cannot weaken the service — it can only refuse earlier.
    // The direction matters: if the two ever drift, the drift produces a false refusal, never a false
    // permit, and the service still decides who may touch WHICH employee.
    // =============================================================================================
    [Route("Hr/Onboarding")]
    public class EmployeeOnboardingController : Controller
    {
        private readonly IEmployeeOnboardingService _onboarding;
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IHrAccessService _hr;

        public EmployeeOnboardingController(IEmployeeOnboardingService onboarding, CrossDbContext db,
            IBusinessContextAccessor contexts, IHrAccessService hr)
        {
            _onboarding = onboarding;
            _db = db;
            _contexts = contexts;
            _hr = hr;
        }


        // The coarse pre-check described above. No subject employee and no company argument: the
        // context supplies both, and the service re-asks the precise question straight after.
        private async Task<bool> MayManageOnboardingAsync(CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return false;
            return await _hr.CanAsync(ctx, CrossBuy.BL.HrActions.EmployeeManage, null, ct);
        }

        // GET /Hr/Onboarding/{employeeId}
        [HttpGet("{employeeId:int}")]
        public async Task<IActionResult> Index(int employeeId, CancellationToken ct)
        {
            var view = await _onboarding.GetAsync(employeeId, ct);

            // A refused read and an employee with no plan yet are DIFFERENT states for the user and
            // the same state for an attacker: both render a page with nothing on it, and neither says
            // whether the employee exists. The "start onboarding" action is only offered when the
            // service already proved the caller may manage this employee.
            var employee = await LoadEmployeeAsync(employeeId, ct);
            if (employee == null) return NotFound();

            ViewBag.Employee = employee;
            ViewBag.Today = DateTime.Today;
            return View(view);
        }

        [HttpPost("{employeeId:int}/start")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Start(int employeeId, int? templateId, DateTime? target, CancellationToken ct)
        {
            if (!await MayManageOnboardingAsync(ct))
            {
                TempData["OnbErr"] = Describe(null);
                return RedirectToAction(nameof(Index), new { employeeId });
            }

            var result = await _onboarding.StartAsync(employeeId, templateId, target, ct);
            TempData[result.Ok ? "OnbMsg" : "OnbErr"] = result.Ok
                ? "Onboarding started"
                : Describe(result.Error);
            return RedirectToAction(nameof(Index), new { employeeId });
        }

        [HttpPost("{employeeId:int}/item/{itemId:int}/complete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteItem(int employeeId, int itemId, string? notes, CancellationToken ct)
        {
            if (!await MayManageOnboardingAsync(ct))
            {
                TempData["OnbErr"] = Describe(null);
                return RedirectToAction(nameof(Index), new { employeeId });
            }

            var result = await _onboarding.CompleteItemAsync(itemId, notes, ct);
            TempData[result.Ok ? "OnbMsg" : "OnbErr"] = result.Ok
                ? "Item completed"
                : Describe(result.Error);
            return RedirectToAction(nameof(Index), new { employeeId });
        }

        [HttpPost("{employeeId:int}/item/{itemId:int}/waive")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WaiveItem(int employeeId, int itemId, string reason, CancellationToken ct)
        {
            if (!await MayManageOnboardingAsync(ct))
            {
                TempData["OnbErr"] = Describe(null);
                return RedirectToAction(nameof(Index), new { employeeId });
            }

            var result = await _onboarding.WaiveItemAsync(itemId, reason, ct);
            TempData[result.Ok ? "OnbMsg" : "OnbErr"] = result.Ok
                ? "Requirement waived"
                : Describe(result.Error);
            return RedirectToAction(nameof(Index), new { employeeId });
        }

        [HttpPost("{employeeId:int}/complete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Complete(int employeeId, int onboardingId, CancellationToken ct)
        {
            if (!await MayManageOnboardingAsync(ct))
            {
                TempData["OnbErr"] = Describe(null);
                return RedirectToAction(nameof(Index), new { employeeId });
            }

            var result = await _onboarding.CompleteAsync(onboardingId, ct);
            TempData[result.Ok ? "OnbMsg" : "OnbErr"] = result.Ok
                ? "Onboarding completed"
                : Describe(result.Error);
            return RedirectToAction(nameof(Index), new { employeeId });
        }

        // The service returns machine codes; the screen shows sentences. Kept here rather than in the
        // service so the service stays free of a localizer, and so an API caller keeps the code.
        private string Describe(string? code) => code switch
        {
            "required_document_missing" => "The required document has not been provided, or is not valid.",
            "mandatory_items_outstanding" => "Some mandatory requirements are still outstanding.",
            "waiver_reason_required" => "A reason is required to waive a requirement.",
            "template_not_found" => "That onboarding template is not available.",
            _ => "You are not allowed to perform this action",
        };

        // The header. Company-scoped like everything else — a foreign employee id renders nothing,
        // which is the same answer a non-existent one gives.
        private async Task<EmployeeHeader?> LoadEmployeeAsync(int employeeId, CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return null;

            // THE NULL CHECKS ON JobTitle ARE NOT DEFENSIVE NOISE. Employee.JobTitle is a REQUIRED
            // reference, so `e.JobTitle.TitleAr` makes EF emit an INNER JOIN — and an employee whose
            // job-title row is missing would then be joined away entirely, and this method would
            // return null, and the screen would 404. The onboarding screen must not disappear because
            // a lookup row is absent; the header just shows one blank field. (The same required-nav
            // INNER JOIN is what silently emptied an earlier isolation fixture in this codebase.)
            var header = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId && e.EmpCompanyID == ctx.CompanyId)
                .Select(e => new EmployeeHeader
                {
                    Id = e.ID,
                    NameAr = e.FullName,
                    NameEn = e.FullNameEn,
                    JobTitleAr = e.JobTitle == null ? null : e.JobTitle.TitleAr,
                    JobTitleEn = e.JobTitle == null ? null : e.JobTitle.Title,
                    DepartmentId = e.DepartmentID,
                    JoinedOn = e.DateOfJoining,
                    IsActive = e.IsActive,
                })
                .FirstOrDefaultAsync(ct);

            if (header == null) return null;

            // Department is a Hierarchical node, resolved separately for the same reason: a broken or
            // absent node must cost a label, not the page.
            if (header.DepartmentId is > 0)
            {
                var dept = await _db.Hierarchicals.AsNoTracking()
                    .Where(h => h.H_ID == header.DepartmentId!.Value)
                    .Select(h => new { h.H_Name, h.H_NameEn })
                    .FirstOrDefaultAsync(ct);
                header.DepartmentAr = dept?.H_Name;
                header.DepartmentEn = dept?.H_NameEn;
            }

            return header;
        }

        public sealed class EmployeeHeader
        {
            public int Id { get; init; }
            public string? NameAr { get; init; }
            public string? NameEn { get; init; }
            public string? JobTitleAr { get; init; }
            public string? JobTitleEn { get; init; }
            public int? DepartmentId { get; init; }
            public string? DepartmentAr { get; set; }
            public string? DepartmentEn { get; set; }
            public DateTime JoinedOn { get; init; }
            public bool IsActive { get; init; }
        }
    }
}
