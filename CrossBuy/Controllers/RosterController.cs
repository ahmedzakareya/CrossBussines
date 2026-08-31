using CrossBuy.BL;
using CrossBuy.BL.Hr;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // ROSTER / SHIFT MANAGEMENT — the screen's controller.
    //
    // THIN BY DESIGN. Every decision — may this caller act, does this employee belong to them, does
    // this assignment conflict, may this period publish — belongs to IRosterService. This file
    // resolves route values, calls the service, and renders what comes back. There is no company
    // parameter anywhere on it and no place to put one.
    //
    // THE VISIBLE GATE ON EACH MUTATING ACTION IS REDUNDANT, AND DELIBERATE. The service re-asks
    // every question with the subject in hand and is what an API caller hits. But CBA001 follows a
    // call graph and cannot resolve IRosterService to an implementation, so without a gate here the
    // authorization is invisible — to the analyzer, and to anyone reading four mutating POST actions
    // with nothing apparently guarding them. The gate is deliberately COARSER than the service's
    // ("may this caller administer attendance for their company", no subject), so if the two ever
    // drift the drift produces a false refusal, never a false permit.
    // =============================================================================================
    [Route("Hr/Roster")]
    public class RosterController : Controller
    {
        private readonly IRosterService _roster;
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IHrAccessService _hr;
        private readonly IRosterClock _clock;

        public RosterController(IRosterService roster, CrossDbContext db,
            IBusinessContextAccessor contexts, IHrAccessService hr, IRosterClock clock)
        {
            _roster = roster;
            _db = db;
            _contexts = contexts;
            _hr = hr;
            _clock = clock;
        }

        // The coarse pre-check. No subject and no company argument: the context supplies both, and the
        // service re-asks the precise question immediately afterwards.
        private async Task<bool> MayManageAsync(CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return false;
            return await _hr.CanAsync(ctx, HrActions.AttendanceManage, null, ct);
        }

        // ---- the operational screen ------------------------------------------------------------

        [HttpGet("")]
        [HttpGet("Period/{periodId:int}")]
        public async Task<IActionResult> Index(int? periodId, int? branchId, int? employeeId, CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return Forbid();

            var periods = await _db.Set<RosterPeriod>().AsNoTracking()
                .Where(p => p.CompanyID == ctx.CompanyId)
                .OrderByDescending(p => p.StartDate)
                .Take(40)
                .ToListAsync(ct);

            // Period NAVIGATION rather than a date picker: a roster is published as a period, so the
            // period is the unit a scheduler moves between. Newest first, current one selected.
            var selected = periodId is int id
                ? periods.FirstOrDefault(p => p.ID == id)
                : periods.FirstOrDefault(p => p.StartDate.Date <= _clock.Today && p.EndDate.Date >= _clock.Today)
                  ?? periods.FirstOrDefault();

            RosterView? view = selected == null ? null : await _roster.GetPeriodAsync(selected.ID, ct);

            ViewBag.Periods = periods;
            ViewBag.Selected = selected;
            ViewBag.MayManage = await MayManageAsync(ct);
            ViewBag.Today = _clock.Today;
            ViewBag.BranchFilter = branchId;
            ViewBag.EmployeeFilter = employeeId;
            ViewBag.Branches = await _db.Branches.AsNoTracking()
                .Where(b => b.CompanyID == ctx.CompanyId)
                .Select(b => new { b.ID, b.Name, b.NameAr }).ToListAsync(ct);
            ViewBag.Shifts = await _db.Set<WorkShift>().AsNoTracking()
                .Where(s => s.CompanyID == ctx.CompanyId && s.IsActive)
                .OrderBy(s => s.StartTime).ToListAsync(ct);

            return View(view);
        }

        // ---- the employee's own roster ----------------------------------------------------------
        //
        // A SEPARATE ACTION, not a filtered version of the grid. The service resolves the caller's own
        // employee identity from BusinessContext, so this route has no employee parameter at all —
        // there is nothing here for a request to point at somebody else.
        [HttpGet("Mine")]
        public async Task<IActionResult> Mine(DateTime? from, CancellationToken ct)
        {
            var start = (from?.Date ?? _clock.Today).AddDays(-(int)(from?.Date ?? _clock.Today).DayOfWeek);
            var end = start.AddDays(27);   // four weeks, so a monthly pattern is visible

            ViewBag.From = start;
            ViewBag.To = end;
            ViewBag.Today = _clock.Today;

            // HR-B3: the same window, MEASURED. PlannedVsActualAsync gates on the caller's own
            // identity exactly as MyScheduleAsync does; the employee id comes from BusinessContext,
            // so there is no parameter here a request could use to name somebody else.
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            ViewBag.Actuals = ctx?.EmployeeId is int me
                ? await _roster.PlannedVsActualAsync(start, end, me, ct)
                : (IReadOnlyList<CrossBuy.BL.Hr.PlannedVsActual>)Array.Empty<CrossBuy.BL.Hr.PlannedVsActual>();

            return View(await _roster.MyScheduleAsync(start, end, ct));
        }

        // ---- mutations --------------------------------------------------------------------------

        [HttpPost("Period/Create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreatePeriod(string nameAr, string nameEn,
            DateTime start, DateTime end, int? branchId, CancellationToken ct)
        {
            if (!await MayManageAsync(ct)) return Refuse(null);

            var result = await _roster.CreatePeriodAsync(nameAr, nameEn, start, end, branchId, ct);
            if (!result.Ok) return Refuse(result.Error);

            TempData["RosterMsg"] = "period_created";
            return RedirectToAction(nameof(Index), new { periodId = result.Period!.ID });
        }

        [HttpPost("Period/{periodId:int}/Assign")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Assign(int periodId, int employeeId, DateTime workDate,
            int shiftId, int? branchId, string? note, string? reason, CancellationToken ct)
        {
            if (!await MayManageAsync(ct)) return Refuse(null, periodId);

            var result = await _roster.AssignAsync(periodId, employeeId, workDate, shiftId, branchId, note, reason, ct);
            if (!result.Ok) return Refuse(result.Error, periodId);

            TempData["RosterMsg"] = "assignment_added";
            return RedirectToAction(nameof(Index), new { periodId });
        }

        [HttpPost("Assignment/{assignmentId:int}/Cancel")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelAssignment(int assignmentId, int periodId,
            string? reason, CancellationToken ct)
        {
            if (!await MayManageAsync(ct)) return Refuse(null, periodId);

            var result = await _roster.CancelAssignmentAsync(assignmentId, reason, ct);
            if (!result.Ok) return Refuse(result.Error, periodId);

            TempData["RosterMsg"] = "assignment_cancelled";
            return RedirectToAction(nameof(Index), new { periodId });
        }

        [HttpPost("Period/{periodId:int}/Publish")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Publish(int periodId, CancellationToken ct)
        {
            if (!await MayManageAsync(ct)) return Refuse(null, periodId);

            var result = await _roster.PublishAsync(periodId, ct);
            if (!result.Ok)
            {
                // A blocked publish is not a generic failure. The conflicts are already on the screen
                // the redirect lands on — detection runs on every read — so the message says WHY the
                // publish stopped and the grid shows WHICH rows caused it.
                TempData["RosterErr"] = result.Error;
                return RedirectToAction(nameof(Index), new { periodId });
            }

            TempData["RosterMsg"] = "period_published";
            return RedirectToAction(nameof(Index), new { periodId });
        }

        // One refusal path. The view maps these codes to sentences; keeping them as codes here means
        // an API caller and the screen get the same answer, and no localizer is needed in a controller.
        private IActionResult Refuse(string? code, int? periodId = null)
        {
            TempData["RosterErr"] = code ?? "not_authorized";
            return RedirectToAction(nameof(Index), periodId is int p ? new { periodId = p } : null);
        }
    }
}
