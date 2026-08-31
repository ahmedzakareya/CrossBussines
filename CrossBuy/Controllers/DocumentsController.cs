using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using CrossBuy.BL.Documents;
using CrossBuy.Models;
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
}
