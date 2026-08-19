using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // Stage 0 Batch B — Business Event Monitor (platform operations).
    //
    // A thin shell on purpose. Every rule — company isolation, payload masking, paging, retry eligibility — lives in
    // IBusinessEventMonitorService, and retry itself goes through IEventDispatchStore. This controller adds no
    // filtering of its own, so there is no second place an authorization rule could be forgotten.
    //
    // It is NOT exposed through PlatformTimelineController: the timeline is an end-user document widget, this is an
    // operator tool with a different audience and a different gate.
    [SessionValidation]
    [PlatformOps]
    public class BusinessEventMonitorController : Controller
    {
        private readonly IBusinessEventMonitorService _monitor;
        private readonly IBusinessContextAccessor _context;
        private readonly IRuntimeInstanceInfo _runtime;
        private readonly CertificationRuntimeState _certification;

        public BusinessEventMonitorController(
            IBusinessEventMonitorService monitor, IBusinessContextAccessor context, IRuntimeInstanceInfo runtime,
            CertificationRuntimeState certification)
        { _monitor = monitor; _context = context; _runtime = runtime; _certification = certification; }

        [HttpGet]
        public async Task<IActionResult> Index([FromQuery] BusinessEventMonitorFilter filter, CancellationToken cancellationToken)
        {
            var ctx = await _context.GetCurrentAsync(cancellationToken);
            if (!ctx.IsAuthenticated) return Unauthorized();

            bool crossCompany = PlatformOpsAttribute.IsElevated(HttpContext);
            var page = await _monitor.SearchAsync(filter, ctx, crossCompany, cancellationToken);

            ViewBag.Filter = filter.Normalised();
            ViewBag.CrossCompany = crossCompany;
            ViewBag.EntityTypes = _monitor.KnownEntityTypes();
            ViewBag.Consumers = _monitor.KnownConsumers();
            ViewBag.Statuses = _monitor.KnownStatuses();
            // The dispatcher's health is part of reading this screen: a queue full of Pending rows means something
            // different depending on whether ANY process is dispatching. Surfacing the worker role here is what
            // stops that being diagnosed as an outbox bug.
            ViewBag.Runtime = _runtime;
            return View(page);
        }

        // Runtime diagnostics as JSON — for a health probe or a support call. Deliberately behind the same
        // [PlatformOps] gate as the rest of the screen: instance ids, PIDs and machine names are infrastructure
        // detail, not public information.
        [HttpGet]
        public IActionResult Runtime()
            => Json(new
            {
                instance = _runtime.InstanceName,
                instanceId = _runtime.InstanceId,
                machine = _runtime.MachineName,
                pid = _runtime.ProcessId,
                startedAtUtc = _runtime.StartedAtUtc,
                version = _runtime.ApplicationVersion,
                environment = _runtime.EnvironmentName,
                workerRole = _runtime.WorkerRole.ToString(),
                workerSafetyMode = _runtime.WorkerSafetyMode,
                workersEnabled = _runtime.WorkersEnabled,
                warning = _runtime.WorkerSafetyWarning,

                // Certification runtime contract, read by tools/ui-conformance/determinism-proof.mjs.
                //
                // The gate needs to know WHICH contract applies to a run. In normal runtime it must keep
                // insisting that workerRole reaches Primary. In certification mode the dispatch worker is
                // deliberately not registered, so no lease is ever claimed and the role is terminal at its
                // starting value — waiting for Primary there would wait forever, which is exactly what it
                // did before this pair existed.
                //
                // Both values come from the single CertificationRuntimeState resolved at startup — the
                // same decision that suppressed the writers — so they cannot disagree with reality.
                // Booleans only: no connection string, no environment-variable value, no secret.
                certificationMode = _certification.CertificationMode,
                backgroundWritersSuppressed = _certification.BackgroundWritersSuppressed,
            });

        // Server-side paged rows for the grid. Returns the same partial the full page renders, so paging and
        // filtering cannot drift from the initial render.
        [HttpGet]
        public async Task<IActionResult> Rows([FromQuery] BusinessEventMonitorFilter filter, CancellationToken cancellationToken)
        {
            var ctx = await _context.GetCurrentAsync(cancellationToken);
            if (!ctx.IsAuthenticated) return Unauthorized();

            var page = await _monitor.SearchAsync(filter, ctx, PlatformOpsAttribute.IsElevated(HttpContext), cancellationToken);
            Response.Headers["X-Total"] = page.Total.ToString();
            Response.Headers["X-Page"] = page.Page.ToString();
            Response.Headers["X-Pages"] = page.Pages.ToString();
            // The summary travels with the rows so the cards can never show counts from a different filter than the
            // grid beneath them. Counts only — no payload, actor or error text goes into a header.
            Response.Headers["X-Summary"] = System.Text.Json.JsonSerializer.Serialize(new
            {
                pending = page.Summary.Pending,
                claimed = page.Summary.Claimed,
                failed = page.Summary.Failed,
                done = page.Summary.Done,
                exhausted = page.Summary.Exhausted,
                totalEvents = page.Summary.TotalEvents,
            });
            return PartialView("_Rows", page);
        }

        [HttpGet]
        public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
        {
            var ctx = await _context.GetCurrentAsync(cancellationToken);
            if (!ctx.IsAuthenticated) return Unauthorized();

            // maySeeRestricted is the ELEVATED right, not merely "can open the monitor" — opening the screen must
            // not grant sight of Restricted/System payloads.
            var vm = await _monitor.GetDetailsAsync(id, ctx,
                crossCompany: PlatformOpsAttribute.IsElevated(HttpContext),
                maySeeRestricted: PlatformOpsAttribute.IsElevated(HttpContext),
                cancellationToken);
            if (vm == null) return NotFound();
            return PartialView("_Details", vm);
        }

        // Retry is a POST with anti-forgery, and it can only ever re-queue ONE consumer's row.
        // There is deliberately NO endpoint that edits an event, edits a payload, or sets a status directly.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Retry(long dispatchId, string reason, bool elevatedOverride, CancellationToken cancellationToken)
        {
            var ctx = await _context.GetCurrentAsync(cancellationToken);
            if (!ctx.IsAuthenticated) return Unauthorized();

            if (string.IsNullOrWhiteSpace(reason))
                return Json(new { ok = false, outcome = nameof(DispatchRetryOutcome.ReasonRequired), message = "A reason is required." });

            // The override is admin-only. A non-admin asking for it is refused here rather than being silently
            // downgraded to a normal retry, because a silent downgrade would look like it worked.
            if (elevatedOverride && !PlatformOpsAttribute.IsElevated(HttpContext))
                return Json(new { ok = false, outcome = nameof(DispatchRetryOutcome.NotAuthorized), message = "An elevated override requires platform-admin rights." });

            var result = await _monitor.RetryAsync(dispatchId, ctx, reason.Trim(), elevatedOverride, cancellationToken);
            return Json(new { ok = result.Success, outcome = result.Outcome.ToString(), message = result.Message, attempts = result.Attempts });
        }
    }
}
