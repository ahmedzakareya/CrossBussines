using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // CENTRAL DOCUMENT PLATFORM — the secure delivery surface.
    //
    // THE WHOLE POINT: a URL is not an authorization.
    //
    // The product's older file surfaces served bytes from wwwroot, where the only thing between a
    // caller and a passport scan was an unguessable path. PrivateFileGate's own header says it: the
    // store cannot be enumerated, "that is obscurity, and it is worth having, but it is not
    // authorization". These routes are the replacement. Nothing here is static, nothing is reachable
    // by path, and the caller never sees a StorageKey, a physical path or an App_Data location.
    //
    // WHAT THE ROUTES ADDRESS. A DocumentId, and optionally a VersionNo — business identifiers the
    // caller is entitled to hold. Never a StorageKey: a key names bytes, and naming bytes must never
    // be a way to reach them. The service resolves the key AFTER authorization and never returns it.
    //
    // THIS CONTROLLER DECIDES NOTHING. Every refusal below is the service's, which asks
    // IDocumentAccessResolver: resolved company, owner resolution, the owning module's own authority,
    // then confidentiality. The controller's only job is to turn "no" into a 404 and a stream into a
    // response — which is why a reviewer can read it in a minute and see that nothing is missing.
    //
    // ONE REFUSAL SHAPE. Absent, another company's, and not-permitted all return the same bare 404.
    // A 403 here would confirm the document exists, which is exactly what a caller probing ids wants
    // to learn.
    // =============================================================================================
    [SessionValidation]
    [Route("Documents")]
    public class DocumentsController : Controller
    {
        private readonly IPlatformDocumentService _documents;

        public DocumentsController(IPlatformDocumentService documents) { _documents = documents; }

        // GET /Documents/{id}/content — the current version.
        //
        // CurrentVersionId is resolved SERVER-SIDE. The caller cannot name a version here at all, so a
        // stale or guessed version id cannot be smuggled into the current-content route.
        [HttpGet("{id:long}/content")]
        public async Task<IActionResult> Content(long id, CancellationToken ct = default)
        {
            var content = await _documents.OpenCurrentAsync(id, ct);
            return content == null ? NotFound() : Stream(content);
        }

        // GET /Documents/{id}/versions/{versionNo:int}/content — a historical version.
        //
        // The version is addressed by its NUMBER WITHIN THIS DOCUMENT, not by a global version id.
        // That is deliberate: a global id would be a second identifier needing its own authorization,
        // and the pair (document, version-number) cannot express "a version belonging to a document I
        // am not allowed to read" at all. The service scopes the lookup to the authorized document, so
        // a number that exists elsewhere simply is not found here.
        [HttpGet("{id:long}/versions/{versionNo:int}/content")]
        public async Task<IActionResult> VersionContent(long id, int versionNo, CancellationToken ct = default)
        {
            if (versionNo <= 0) return NotFound();
            var content = await _documents.OpenVersionAsync(id, versionNo, ct);
            return content == null ? NotFound() : Stream(content);
        }

        // GET /Documents/for/{entityType}/{entityId} — the authorized listing for one record.
        // Returns metadata only, and only for documents this caller may see; never a key or a path.
        [HttpGet("for/{entityType}/{entityId:int}")]
        public async Task<IActionResult> ForEntity(string entityType, int entityId, CancellationToken ct = default)
            => Json(await _documents.ListForEntityAsync(entityType, entityId, ct));

        // GET /Documents/workspace/{entityType}/{entityId} — the governed lifecycle screen.
        //
        // ONE GET, and it renders what the service already decided. Every row it shows carries the
        // canonical DocumentLifecycle, so the view has nothing to compute: the two boundaries this
        // platform exists to get right - a document expiring TODAY is still valid, and the warning
        // window belongs to the TYPE rather than being a constant - are settled before the model
        // reaches Razor. A `@if (doc.ExpiryDate < DateTime.Now)` in a view would quietly become the
        // system's second date rule, and a test asserts there is not one.
        //
        // AUTHORIZATION IS ListForEntityAsync's, unchanged: it gates the entity once and re-asks per
        // document, so a Restricted row is ABSENT from the model rather than rendered as hidden. This
        // action adds no rule of its own, which is why it needs no new authority member.
        //
        // STAYS GENERIC. The route names an entity type and an id, never an employee - a central
        // document screen that knew about HR would stop being central, and the next family to want
        // documents would need a second one.
        [HttpGet("workspace/{entityType}/{entityId:int}")]
        public async Task<IActionResult> Workspace(string entityType, int entityId, CancellationToken ct = default)
        {
            var model = new DocumentWorkspaceVm
            {
                EntityType = entityType,
                EntityId = entityId,
                Documents = await _documents.ListForEntityAsync(entityType, entityId, ct),
            };
            return View(model);
        }

        // GET /Documents/{id}/history — the version history, metadata only.
        [HttpGet("{id:long}/history")]
        public async Task<IActionResult> History(long id, CancellationToken ct = default)
        {
            var versions = await _documents.HistoryAsync(id, ct);
            // StorageKey is projected AWAY here. It is in the entity because the platform needs it; it
            // has no business crossing the wire, and a caller that never sees one cannot be tempted to
            // build a URL from it.
            return Json(versions.Select(v => new
            {
                v.VersionNo, v.FileName, v.ContentType, v.SizeBytes,
                v.Reason, v.UploadedBy, v.UploadedAt,
            }));
        }


        // ---- self-service submission + verification ----------------------------------------------
        //
        // WHAT THE REQUEST CANNOT NAME: no company, no confidentiality, no status, no verifier, no
        // StorageKey. The employee is not a parameter either — Submit maps to employee-request, whose
        // target IS the caller, so naming somebody else's id simply fails the authorization ask.
        //
        // The 50 MB cap is the framework's, applied BEFORE the body is read, so an oversized post is
        // refused at the boundary rather than after it has spent the disk the cap exists to protect.
        // The type's own MaxSizeBytes is then applied by the service against the real length.
        [HttpPost("submit")]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Submit(
            string entityType, int entityId, long documentTypeId,
            IFormFile? file, string? documentNumber, DateTime? issueDate, DateTime? expiryDate,
            string? metadata, CancellationToken ct = default)
        {
            if (file == null || file.Length == 0) return BadRequest(new { ok = false, code = "file_required" });

            // The filename is sanitised on the way IN as well as out. Path.GetFileName strips any
            // directory part, so a posted "..\\..\\web.config" cannot become an extension check on one
            // string and a stored name derived from another.
            var safeName = System.IO.Path.GetFileName(file.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeName)) return BadRequest(new { ok = false, code = "file_required" });

            // The browser's Content-Type is not trusted: it is passed through for display, and the
            // service decides acceptability from the EXTENSION against the type's allow-list. A
            // mismatch is caught there rather than here, so one place owns the policy.
            await using var stream = file.OpenReadStream();
            var result = await _documents.SubmitAsync(new DocumentSubmissionRequest
            {
                EntityType = entityType,
                EntityId = entityId,
                DocumentTypeId = documentTypeId,
                DocumentNumber = documentNumber,
                IssueDate = issueDate,
                ExpiryDate = expiryDate,
                Metadata = metadata,
                FileName = safeName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                Reason = "submitted",
            }, stream, ct);

            return Respond(result);
        }

        // POST /Documents/{id}/verify — HR accepts a pending document.
        [HttpPost("{id:long}/verify")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Verify(long id, string? note, CancellationToken ct = default)
            => Respond(await _documents.VerifyAsync(id, note, ct));

        // POST /Documents/{id}/reject — HR refuses it, WITH a reason.
        [HttpPost("{id:long}/reject")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(long id, string note, CancellationToken ct = default)
            => Respond(await _documents.RejectAsync(id, note, ct));

        /// One response shape for every write.
        ///
        /// A refusal returns 404 with the service's CODE and nothing else — no StorageKey, no path, no
        /// document id it did not already have, and no distinction between "absent", "another company's"
        /// and "not permitted". A validation refusal (a bad extension, a missing date) returns 400,
        /// because the caller can act on that and it reveals nothing about what exists.
        private IActionResult Respond(DocumentResult result)
        {
            if (result.Ok) return Json(new { ok = true, id = result.DocumentId, versionId = result.VersionId });

            if (AuthorizationShapedRefusals.Contains(result.ReasonCode))
                return NotFound(new { ok = false, code = "not_found" });

            // AN INFRASTRUCTURE FAILURE IS NOT THE CALLER'S FAULT. store_failed and decision_failed mean
            // the storage or the database let us down, and the write was rolled back and compensated.
            // Returning 400 would tell the caller their request was malformed - it was not, and a client
            // that believes that will "fix" a perfectly good submission instead of retrying it. 503 says
            // the honest thing: nothing was recorded, try again.
            if (InfrastructureRefusals.Contains(result.ReasonCode))
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, code = result.ReasonCode });

            return BadRequest(new { ok = false, code = result.ReasonCode });
        }

        /// The two codes that mean "we failed, not you". Both are total refusals: the transaction was
        /// rolled back and any blob written on the way was deleted, so a retry is safe.
        private static readonly System.Collections.Generic.HashSet<string> InfrastructureRefusals =
            new(System.StringComparer.Ordinal) { "store_failed", "decision_failed" };

        /// Codes that answer "may you" or "does it exist". These collapse to one 404 so the write
        /// endpoints cannot be used to probe: everything else is a policy answer the caller may see.
        private static readonly System.Collections.Generic.HashSet<string> AuthorizationShapedRefusals =
            new(System.StringComparer.Ordinal)
            {
                DocumentAccessReasons.CompanyUnresolved,
                DocumentAccessReasons.NoEmployeeIdentity,
                DocumentAccessReasons.CompanyMismatch,
                DocumentAccessReasons.UnknownEntityType,
                DocumentAccessReasons.NoOwnerResolver,
                DocumentAccessReasons.OwnerNotFound,
                DocumentAccessReasons.RelationMismatch,
                DocumentAccessReasons.ModuleDenied,
                DocumentAccessReasons.NoModuleAuthority,
                DocumentAccessReasons.ConfidentialityDenied,
            };

        /// Streams without buffering the whole file, and refuses to let stored metadata become a
        /// header-injection or path-traversal vector.
        ///
        /// The filename and content type come from what the platform VALIDATED at upload, never from
        /// the request. Even so they are sanitised again on the way out: a stored value is data, and
        /// data that reaches a response header gets checked at the boundary it is crossing, not only
        /// at the one it entered through.
        private IActionResult Stream(DocumentContent content)
        {
            // Path.GetFileName strips any directory part, so a stored "..\\..\\web.config" becomes
            // "web.config" and the browser cannot be steered into writing outside its download folder.
            var name = System.IO.Path.GetFileName(content.FileName ?? string.Empty);

            // CR/LF (and anything else non-printable) would let a filename split the response and inject
            // headers. Quotes would break out of the Content-Disposition token. Both are removed rather
            // than escaped: a document called `a"b` is not worth a parser edge case.
            var safe = new string(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Where(name, c => !char.IsControl(c) && c != '"' && c != '\\')));
            if (string.IsNullOrWhiteSpace(safe)) safe = "document";

            // A content type is only honoured when it is one the platform itself recorded and it looks
            // like a media type. Anything else is served as an opaque download, which is the safe
            // default: an unexpected type must not become an instruction to the browser to render.
            var contentType = LooksLikeMediaType(content.ContentType)
                ? content.ContentType
                : "application/octet-stream";

            // enableRangeProcessing: the framework's own implementation, so a large PDF seeks rather
            // than re-streaming. FileStreamResult disposes the stream, and nothing is read into memory.
            return File(content.Content, contentType, safe, enableRangeProcessing: true);
        }

        private static bool LooksLikeMediaType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
            int slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1) return false;
            foreach (var c in value)
                if (char.IsControl(c) || c == ';' || c == ',' || c == '"') return false;
            return true;
        }
    }

    /// What the lifecycle screen renders. It carries NO dates it has to interpret: every row already
    /// holds the state the platform computed, so the view is a rendering and not a second opinion.
    public sealed class DocumentWorkspaceVm
    {
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }
        public IReadOnlyList<CrossBuy.BL.Documents.DocumentListItem> Documents { get; set; }
            = System.Array.Empty<CrossBuy.BL.Documents.DocumentListItem>();
    }
}
