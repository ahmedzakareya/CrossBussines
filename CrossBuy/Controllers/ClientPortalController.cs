using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Communication;   // ICommPortalService - the 3B-C external conversation authority
using CrossBuy.Models.Communication;   // CommValidationException - the body policy's refusal
using CrossBuy.BL.Portal;
using CrossBuy.Models.Platform;   // PortalAuthorize — the external identity's authority
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // CLIENT PORTAL — the external surface.
    //
    // WHY THIS IS NOT PortalController. That name is already taken by the post-login chooser between
    // the HR back office and People self-service — an INTERNAL screen. Reusing it would have put an
    // external product inside a controller whose whole purpose is routing staff, which is exactly
    // the confusion this batch exists to prevent.
    //
    // [PortalAuthorize] AND NOT [SessionValidation]. SessionValidation is the internal filter: it
    // exists to police the employee session blob, and a portal user has none. Requiring it here would
    // have pushed somebody toward giving customers an employee session — the single worst thing that
    // could happen to this boundary.
    //
    // WAVE 3A-0B: THIS WAS [Authorize] AND THAT WAS NOT ENOUGH. Bare [Authorize] accepts the DEFAULT
    // scheme, which is Identity.Application — the internal employee cookie. So a member of staff who
    // was merely signed in satisfied it, and the portal was reachable by exactly the audience it
    // exists to exclude. [PortalAuthorize] binds PortalAuthenticationDefaults.PolicyName, whose
    // policy clears the scheme list and names CrossBuy.Portal alone; an employee cookie now produces
    // no principal for this endpoint at all. It was deliberately left as [Authorize] until this wave
    // because binding the policy before a portal login existed would have shut the portal to
    // everybody, including the customers it belongs to.
    //
    // AUTHENTICATION IS STILL NOT AUTHORIZATION. Holding the portal cookie gets a request past this
    // attribute and no further: every action below resolves IPortalContextAccessor, which reads the
    // PortalUsers row for company, customer, active state and capabilities. A revoked link is refused
    // on the next request even though the cookie is still perfectly valid.
    //
    // EVERY ACTION IS SCOPE-ONLY. Not one of these takes a customerId or a companyId. There is
    // nothing for a caller to substitute: the identity decides the scope, and the URL carries no
    // authority at all. That is why there are no id parameters to validate here — the safest
    // parameter is the one that does not exist.
    //
    // A null summary means "no portal identity", and it renders as a refusal rather than an empty
    // dashboard: an external user must never be shown a working-looking portal with nothing in it,
    // because that is indistinguishable from a customer who has no invoices.
    // =============================================================================================
    [PortalAuthorize]
    [Route("ClientPortal")]
    public class ClientPortalController : Controller
    {
        private readonly IPortalDataService _portal;
        private readonly ICommPortalService _conversations;
        private readonly IPortalContextAccessor _contexts;
        private readonly IPortalDocumentService _documents;
        private readonly IPortalPhotoService _photos;

        public ClientPortalController(
            IPortalDataService portal, ICommPortalService conversations, IPortalContextAccessor contexts,
            IPortalDocumentService documents, IPortalPhotoService photos)
        {
            _portal = portal;
            _conversations = conversations;
            _contexts = contexts;
            _documents = documents;
            _photos = photos;
        }

        // ---- THE CONTACT PHOTOGRAPH, for the portal audience -------------------------------------
        //
        // WHY AN ENDPOINT AND NOT AN <img src="/uploads/portal/...">. The stored file has no URL at
        // all - it lives under the content root, outside wwwroot - so this action is the only way the
        // bytes are reachable, and the authorization is in the service rather than in a middleware
        // that would have to guess which audience was asking. The convergence map measured the old
        // arrangement: a portal cookie is not the default scheme, so the customer got 404 on their
        // own picture, while every employee of every company could fetch anyone's.
        //
        // IT TAKES NO PARAMETER, AND THAT IS THE POINT.
        //
        // The first cut of this action was ContactPhoto(int portalUserId) and the service refused any
        // id that was not the caller's, which was correct but weaker than it needed to be: a portal
        // endpoint that ACCEPTS an identity has to be read carefully to see that it ignores it.
        // PortalConversationUiTests forbids exactly that shape, and it was right to. The portal shows
        // one avatar — the signed-in person's — so the id is resolved from the portal context and
        // there is nothing in the URL for anyone to substitute.
        //
        // This is the rule ClientPortalController has always stated about itself: the identity
        // decides the scope, the URL carries no authority, and the safest parameter is the one that
        // does not exist.
        [HttpGet("Contacts/photo")]
        public async Task<IActionResult> ContactPhoto(CancellationToken ct = default)
        {
            var me = await _contexts.TryGetCurrentAsync(ct);
            if (me == null) return NotFound();

            var content = await _photos.OpenForPortalAsync(me.PortalUserId, ct);

            // One answer for absent, someone else's, deactivated, a locator that is not ours and a
            // file that will not open. NOT 403: confirming that this id names a real contact is
            // itself a disclosure to somebody who may not see them.
            if (content == null) return NotFound();

            // Private personal data must not be retained by a shared cache; the response is as
            // addressable as the cookie that earned it and no more.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
            return File(content.Stream, content.ContentType);
        }

        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(CancellationToken ct = default)
        {
            var home = await _portal.HomeAsync(ct);
            if (home == null) return View("NoAccess");
            return View(home);
        }

        [HttpGet("Quotations")]
        public async Task<IActionResult> Quotations(CancellationToken ct = default)
            => View(await _portal.QuotationsAsync(ct));

        [HttpGet("Projects")]
        public async Task<IActionResult> Projects(CancellationToken ct = default)
            => View(await _portal.ProjectsAsync(ct));

        [HttpGet("Invoices")]
        public async Task<IActionResult> Invoices(CancellationToken ct = default)
            => View(await _portal.InvoicesAsync(ct));

        // ---- MY DOCUMENTS: documents published to this customer (DOC1) -------------------------
        //
        // PRESENTATION ONLY, AGAIN. The whole authorization chain - identity, the portal-documents
        // capability, the publication row, the document's Active state and the current version -
        // lives in IPortalDocumentService. This controller resolves nothing and decides nothing; it
        // asks and it renders.
        //
        // READ-ONLY BY CONSTRUCTION. There is no upload, replace, delete, rename, move, share or
        // public-link action here, and none is stubbed for later: the service exposes no such method,
        // so the control is absent rather than hidden.
        [HttpGet("Documents")]
        public async Task<IActionResult> Documents(CancellationToken ct = default)
            => View(await _documents.ListAsync(ct));

        // The bytes. Addressed by PUBLICATION id, never by document id: a publication id names a
        // grant that already carries its company and its customer, so a guessed one fails against
        // its own row, while a document id would be an invitation to try the next number.
        //
        // NO REDIRECT, EVER. This streams from IDocumentStorage in-process. It does not redirect to
        // /Files or /uploads, and the document platform's content lives outside wwwroot where it has
        // no URL at all - so DOC1 depends on PrivateFileGate for nothing.
        [HttpGet("Documents/{publicationId:long}/content")]
        public async Task<IActionResult> DocumentContent(long publicationId, CancellationToken ct = default)
        {
            var content = await _documents.OpenAsync(publicationId, ct);

            // Null covers absent, revoked, another company's, another customer's, an inactive
            // document, a missing version and missing content alike. One answer for all seven.
            if (content == null) return NotFound();

            // The filename and content type come from what the platform VALIDATED at upload, never
            // from the request - and they are sanitised AGAIN on the way out, because a stored value
            // is data, and data that reaches a response header gets checked at the boundary it is
            // crossing rather than only at the one it entered through. This mirrors what
            // DocumentsController.Stream already does for the internal audience.
            var name = System.IO.Path.GetFileName(content.FileName ?? string.Empty);

            // CR/LF would let a filename split the response and inject headers; a quote would break
            // out of the Content-Disposition token. Both are removed rather than escaped.
            var safe = new string(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Where(name, c => !char.IsControl(c) && c != '"' && c != '\\')));
            if (string.IsNullOrWhiteSpace(safe)) safe = "document";

            // An unexpected content type must not become an instruction to the browser to render, so
            // anything that is not a plausible media type is served as an opaque download.
            var contentType = LooksLikeMediaType(content.ContentType)
                ? content.ContentType!
                : "application/octet-stream";

            return File(content.Content, contentType, safe, enableRangeProcessing: true);
        }

        /// A conservative shape check - one "/" with non-empty, non-space sides. It is not a media
        /// type registry; it only decides whether the stored value is safe to echo into a header.
        private static bool LooksLikeMediaType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.Length > 128) return false;
            if (value.Any(char.IsControl)) return false;

            var parts = value.Split('/');
            return parts.Length == 2
                   && !string.IsNullOrWhiteSpace(parts[0])
                   && !string.IsNullOrWhiteSpace(parts[1]);
        }

        // ---- UPDATES: the customer's conversations (Wave 3B-D) ---------------------------------
        //
        // PRESENTATION ONLY. Every decision — which conversations exist for this caller, which
        // comments they may read, who authored one, what a post's visibility is — was made in
        // Wave 3B-C inside ICommPortalService and is not re-made, softened or second-guessed here.
        // This controller resolves the portal identity, asks that service, and renders what comes
        // back. A UI is not an authority, and none of these actions contains one.
        //
        // WHY THESE RENDER SERVER-SIDE RATHER THAN CALLING THE JSON ENDPOINTS. This portal has no
        // JavaScript at all — not a script tag in any of its five screens — and every page is a
        // model-bound Razor view. Introducing fetch here to reach /ClientPortal/Conversations would
        // have added anti-forgery plumbing, a client error path and a script dependency to a surface
        // that has none, purely to avoid passing a model to a view. The six 3B-C JSON endpoints are
        // untouched and remain the API; these render the same service's answers as HTML.
        //
        // A THREAD ID IS AN IDENTIFIER AND NEVER AN AUTHORITY: the service re-proves access on every
        // single call, so a customer typing another customer's thread id into the URL gets exactly
        // what they get from every other entry point — nothing.
        [HttpGet("Updates")]
        public async Task<IActionResult> Updates(CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return View("NoAccess");

            return View(await _conversations.ListConversationsAsync(portal, null, ct));
        }

        [HttpGet("Updates/{threadId:long}")]
        public async Task<IActionResult> Conversation(long threadId, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return View("NoAccess");

            var conversation = await _conversations.GetConversationAsync(portal, threadId, ct);

            // Null covers absent, another company's, another customer's, not-a-participant, internal
            // and deleted alike. One answer for all six, so a thread id cannot be walked to discover
            // which conversations exist.
            if (conversation == null) return NotFound();

            ViewData["Comments"] = await _conversations.ListCommentsAsync(portal, threadId, null, ct);
            ViewData["Attachments"] = await _conversations.ListAttachmentsAsync(portal, threadId, ct);
            return View(conversation);
        }

        // ---- the two writes, POST-REDIRECT-GET ---------------------------------------------------
        //
        // They exist because this portal is server-rendered: the 3B-C endpoints answer JSON, and a
        // browser form posting to one would render raw JSON at the customer. These are TRANSPORT and
        // nothing else — each is four lines, calls the SAME ICommPortalService the JSON endpoints
        // call, and adds no persistence, no service and no rule of its own. There is no second
        // conversation engine, no second comment path and no second authority.
        [HttpPost("Updates/{threadId:long}/Comments")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostComment(
            long threadId, string? body, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return View("NoAccess");

            // The body is the ONLY thing the customer supplies. Not the author, not the visibility,
            // not the company, not the customer — the service decides all four from the resolved
            // context, so there is no value here for a request to forge.
            //
            // THE SERVICE VALIDATES AND THIS DOES NOT RE-VALIDATE. An empty body, a body over the
            // byte cap and an unsupported format are all refused by ICommBodyPolicy, which THROWS
            // CommValidationException — and an escaping exception would give the customer a 500
            // where the page already has a refusal to render. Catching it here is presentation, not
            // a second rule: the decision stays in the policy, only its delivery changes. A null
            // return means the thread itself was refused, which lands in exactly the same place.
            try
            {
                var posted = await _conversations.PostCommentAsync(portal, threadId, body, ct);
                if (posted == null) TempData["PortalCommentError"] = true;
            }
            catch (CommValidationException)
            {
                // ONE MESSAGE FOR EVERY REFUSAL, like the sign-in screen: the page must not say
                // which check refused, and the customer's remedy is the same in every case.
                TempData["PortalCommentError"] = true;
            }

            return RedirectToAction(nameof(Conversation), new { threadId });
        }

        [HttpPost("Updates/{threadId:long}/Read")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkConversationRead(
            long threadId, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return View("NoAccess");

            // READ STATE IS SERVER STATE. Nothing here marks anything read in the browser: the
            // service writes the watermark against the portal actor, and the redirect re-queries.
            await _conversations.MarkReadAsync(portal, threadId, null, ct);
            return RedirectToAction(nameof(Conversation), new { threadId });
        }

        // JSON twins, for the shell to refresh a panel without a full render. Same service, same
        // scope, same refusals — there is no second read path to keep in step.
        [HttpGet("api/summary")]
        public async Task<IActionResult> Summary(CancellationToken ct = default)
        {
            var home = await _portal.HomeAsync(ct);
            return home == null ? NotFound() : Json(home);
        }

        // ---- THE FIRST PORTAL MUTATION ----------------------------------------------------------
        //
        // The quotation id in the route is an IDENTIFIER AND NOTHING ELSE. It names which row is being
        // asked about; it confers no right to it. Every question that decides the answer — who is
        // calling, which company, which customer, which capability, which state — is resolved inside
        // IPortalDataService from the authenticated portal identity, and none of it can be influenced
        // by anything on this request. That is also why CBA001 credits the SERVICE and not
        // IPortalContextAccessor: resolving who somebody is settles identity, not permission.
        //
        // POST + anti-forgery, because accepting a quotation is a contractual act and must not be
        // reachable by a link, a prefetch, or a page on another site.
        [HttpPost("Quotations/{id:int}/Accept")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> AcceptQuotation(int id, CancellationToken ct = default)
            => RespondAsync(id, accept: true, ct);

        [HttpPost("Quotations/{id:int}/Reject")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RejectQuotation(int id, CancellationToken ct = default)
            => RespondAsync(id, accept: false, ct);

        // ---- "PLEASE GET IN TOUCH" --------------------------------------------------------------
        //
        // The only endpoint in this controller that accepts caller-supplied CONTENT, and the shape of
        // what it accepts is the point: an optional quotation id and an optional message, and nothing
        // else. There is no customerId and no companyId to send, so there is nothing to substitute -
        // the row's company, customer and portal user all come from the resolved identity inside
        // IPortalDataService.
        //
        // POST + anti-forgery, like the decision: this creates work for staff, and a link or a page on
        // another site must not be able to create it on the customer's behalf.
        [HttpPost("ContactRequests")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestContact(
            int? quotationId, string? message, CancellationToken ct = default)
        {
            var result = await _portal.RequestContactAsync(quotationId, message, ct);
            return Respond(result);
        }

        /// One response shape for both decisions.
        ///
        /// not_found answers absent, another company's, another customer's and not-yet-sent alike, so a
        /// customer cannot walk quotation ids to discover which exist. The states a customer legitimately
        /// owns — already answered, lapsed — say so, because the caller can act on those and they reveal
        /// nothing they did not already have.
        private async Task<IActionResult> RespondAsync(int id, bool accept, CancellationToken ct)
            => Respond(await _portal.RespondToQuotationAsync(id, accept, ct));

        /// ONE mapping for every portal write, so the two of them cannot drift into answering the same
        /// refusal with different statuses - which is how a client-side handler ends up with a special
        /// case per endpoint.
        private IActionResult Respond(PortalDecisionResult result)
        {
            if (result.Ok) return Json(new { ok = true, status = result.Status });

            return result.Code switch
            {
                PortalDecisionResult.NotFound => NotFound(new { ok = false, code = PortalDecisionResult.NotFound }),
                PortalDecisionResult.NotPermitted => StatusCode(StatusCodes.Status403Forbidden,
                    new { ok = false, code = PortalDecisionResult.NotPermitted }),
                PortalDecisionResult.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { ok = false, code = PortalDecisionResult.Unavailable }),
                _ => Conflict(new { ok = false, code = result.Code }),
            };
        }
    }
}
