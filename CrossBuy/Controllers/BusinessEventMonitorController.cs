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
        // For the resolution record below: the DbContext resolves the note authors' photographs and
        // "me", and the localizer carries the attachment refusals.
        private readonly CrossBuy.Models.Context.CrossDbContext _db;
        private readonly Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> _L;

        public BusinessEventMonitorController(
            IBusinessEventMonitorService monitor, IBusinessContextAccessor context, IRuntimeInstanceInfo runtime,
            CertificationRuntimeState certification, CrossBuy.Models.Context.CrossDbContext db,
            Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> localizer)
        { _monitor = monitor; _context = context; _runtime = runtime; _certification = certification; _db = db; _L = localizer; }

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

        // ================================================================================================
        // THE RESOLUTION RECORD
        //
        // A failed dispatch is diagnosed and fixed by a person, and what they did is the single most
        // valuable thing to keep: it is what the next operator reads when the same consumer fails again.
        // So the modal carries notes — who wrote them, when, and the screenshots pasted as evidence.
        //
        // NOTHING about comments, authorship, attachments, mentions or audit is implemented here. The
        // Communication Platform owns all of it and _EntityConversation.cshtml already renders the panel
        // for any registered family; this adds the family's three endpoints and nothing else.
        //
        // THE GATE IS THE READ GATE, deliberately. A caller who may not SEE an event may not annotate it,
        // and both answer the same NotFound so the write path is no more of an existence oracle than the
        // read path. It is re-asked on every endpoint rather than inherited from having opened the screen.
        //
        // This does NOT edit the event. The class comment's promise — "no endpoint that edits an event,
        // edits a payload, or sets a status directly" — still holds: a note is a separate record that
        // points AT the event, and the event row is untouched.
        // ================================================================================================

        private (CrossBuy.BL.Communication.ICommThreadService Threads,
                 CrossBuy.BL.Communication.ICommCommentService Comments,
                 CrossBuy.BL.Communication.ICommEntitySurface Surface)? TryConversation()
        {
            var sp = HttpContext.RequestServices;
            var threads = sp.GetService(typeof(CrossBuy.BL.Communication.ICommThreadService)) as CrossBuy.BL.Communication.ICommThreadService;
            var comments = sp.GetService(typeof(CrossBuy.BL.Communication.ICommCommentService)) as CrossBuy.BL.Communication.ICommCommentService;
            var surface = sp.GetService(typeof(CrossBuy.BL.Communication.ICommEntitySurface)) as CrossBuy.BL.Communication.ICommEntitySurface;
            return threads is null || comments is null || surface is null ? null : (threads, comments, surface);
        }

        private IActionResult ConversationUnavailable() =>
            StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { ok = false, unavailable = true, code = "communication_unavailable" });

        /// Exists + visible to THIS caller, and narrow-able to the platform's int-keyed reference.
        private async Task<(bool Ok, CrossBuy.Models.Platform.BusinessContext? Context, int RefId)>
            EventGateAsync(long id, CancellationToken ct)
        {
            var ctx = await _context.GetCurrentAsync(ct);
            if (!ctx.IsAuthenticated) return (false, null, 0);

            // CommEntityRef is int-keyed across the whole platform (TAB-1's contract) while EventId is a
            // long. The narrowing is CHECKED rather than cast: an id beyond int range is refused as not
            // found instead of silently wrapping onto some other event's thread.
            if (id <= 0 || id > int.MaxValue) return (false, null, 0);

            bool elevated = PlatformOpsAttribute.IsElevated(HttpContext);
            var vm = await _monitor.GetDetailsAsync(id, ctx, crossCompany: elevated, maySeeRestricted: elevated, ct);
            if (vm == null) return (false, null, 0);
            return (true, ctx, (int)id);
        }

        // GET /BusinessEventMonitor/Notes?id=123
        [HttpGet]
        public async Task<IActionResult> Notes(long id, CancellationToken cancellationToken = default)
        {
            var gate = await EventGateAsync(id, cancellationToken);
            if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

            var comm = TryConversation();
            if (comm == null) return ConversationUnavailable();

            var reference = new CrossBuy.Models.Communication.CommEntityRef(
                CrossBuy.BL.Platform.EntityRegistry.PlatformEvent, gate.RefId);

            // The REGISTRY decides whether this family carries comments, not this controller.
            var allowed = await comm.Value.Surface.EvaluateAsync(reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
            if (!allowed.Allowed)
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, unavailable = true, code = "capability_disabled" });

            try
            {
                var thread = await comm.Value.Threads.GetOrCreateAsync(gate.Context!,
                    new CrossBuy.Models.Communication.CommThreadRequest { Entity = reference }, cancellationToken);
                var page = await comm.Value.Comments.ListAsync(gate.Context!, thread.Id, null, cancellationToken);
                bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

                var avatars = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
                    _db, gate.Context!, page.Items.Select(c => c.Author.EmployeeId), cancellationToken);

                return Json(new
                {
                    ok = true,
                    threadId = thread.Id,
                    entity = new { code = CrossBuy.BL.Platform.EntityRegistry.PlatformEvent, id = gate.RefId },
                    canReact = false,
                    me = await CrossBuy.BL.Communication.CommPanel.MeAsync(_db, gate.Context!, isAr, cancellationToken),
                    comments = CrossBuy.BL.Communication.CommPanel.Project(page.Items, isAr, avatars),
                });
            }
            catch (CrossBuy.Models.Communication.CommAccessDeniedException)
            {
                // The platform's decision, RENDERED rather than swallowed: the panel has a designed
                // disabled state, and a 500 inside a modal tells an operator nothing at all.
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, unavailable = true, code = "capability_disabled" });
            }
        }

        // POST /BusinessEventMonitor/NotesAdd
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(21_000_000)]   // CommPanel.MaxUploadBytes plus the form envelope
        public async Task<IActionResult> NotesAdd(long id, string? body, long? parentCommentId,
            IFormFile? file, CancellationToken cancellationToken = default)
        {
            var gate = await EventGateAsync(id, cancellationToken);
            if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

            // A note MAY be nothing but a screenshot. Requiring text would make "here is the proof"
            // impossible to record without inventing a sentence to go with it.
            if (string.IsNullOrWhiteSpace(body) && (file is null || file.Length == 0))
                return Json(new { ok = false, error = _L["Write a note"].Value });

            var comm = TryConversation();
            if (comm == null) return ConversationUnavailable();

            var reference = new CrossBuy.Models.Communication.CommEntityRef(
                CrossBuy.BL.Platform.EntityRegistry.PlatformEvent, gate.RefId);

            var allowed = await comm.Value.Surface.EvaluateAsync(reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
            if (!allowed.Allowed)
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, unavailable = true, code = "capability_disabled" });

            // NOTHING REACHES DISK BEFORE THE GATE, so a refused caller never leaves an orphan file.
            CrossBuy.Models.Communication.CommAttachmentRequest? attachment = null;
            if (file is { Length: > 0 })
            {
                var env = HttpContext.RequestServices.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment))
                    as Microsoft.AspNetCore.Hosting.IWebHostEnvironment;
                if (env is null) return Json(new { ok = false, error = _L["The file could not be attached"].Value });

                var (staged, refusal) = await CrossBuy.BL.Communication.CommPanel.StageAsync(file, env.WebRootPath, cancellationToken);
                if (staged is null) return Json(new { ok = false, code = refusal, error = AttachmentRefusalText(refusal) });
                attachment = staged;
            }

            try
            {
                var added = await comm.Value.Comments.AddAsync(gate.Context!,
                    new CrossBuy.Models.Communication.CommCommentRequest
                    {
                        Entity = reference,
                        Body = body ?? "",
                        ParentCommentId = parentCommentId is > 0 ? parentCommentId : null,
                        Attachments = attachment is null ? null : new[] { attachment },
                    }, cancellationToken);
                return Json(new { ok = true, id = added.CommentId, threadId = added.ThreadId });
            }
            catch (CrossBuy.Models.Communication.CommAccessDeniedException)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, unavailable = true, code = "capability_disabled" });
            }
        }

        /// One sentence per machine code, so the browser never has to compose a refusal.
        private string AttachmentRefusalText(string code) => code switch
        {
            CrossBuy.BL.Communication.CommPanel.UploadRefusal.TooLarge => _L["The file is larger than 20 MB"].Value,
            CrossBuy.BL.Communication.CommPanel.UploadRefusal.Type => _L["This kind of file cannot be attached"].Value,
            _ => _L["The file could not be attached"].Value,
        };

    }
}
