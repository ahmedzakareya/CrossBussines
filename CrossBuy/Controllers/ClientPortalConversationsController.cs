using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // THE EXTERNAL CONVERSATION ENDPOINTS — Wave 3B-C.
    //
    // A THIN BOUNDARY OVER ICommPortalService. Every decision this controller could get wrong has
    // already been made one layer down: which conversations exist for this caller, which comments
    // they may read, who the author of a post is, and what visibility it carries. What is left here
    // is transport — and the two things transport must not do.
    //
    // (1) NO ACTION TAKES AN IDENTITY. There is no companyId, customerId, portalUserId or employeeId
    //     parameter anywhere in this file. The only identity is the PortalContext that
    //     IPortalContextAccessor builds from the authenticated CrossBuy.Portal cookie and the
    //     PortalUsers row. A forged identity is not rejected here, it is UNEXPRESSIBLE: there is no
    //     argument to send. threadId IS a parameter, and it is an identifier rather than an
    //     authority — the service re-proves access to it on every single call.
    //
    // (2) [PortalAuthorize] AND NOT [Authorize]. Bare [Authorize] resolves through the DEFAULT
    //     scheme, which is the internal employee cookie — that was the 3A-0B defect, and repeating
    //     it here would put staff inside a customer's conversation. PortalAuthorize binds
    //     PortalAuthenticationDefaults.PolicyName, whose policy names CrossBuy.Portal alone. It is
    //     also what CBA001 credits as this controller's authority, so the two mutating actions below
    //     are genuinely protected rather than merely declared.
    //
    // AUTHENTICATION IS STILL NOT AUTHORIZATION. Holding the portal cookie gets a request past the
    // attribute and no further: an unresolved or revoked PortalUsers row yields no context, and
    // every action answers exactly as it does for a thread that is not the caller's.
    //
    // A REFUSAL AND AN ABSENCE ARE THE SAME ANSWER, deliberately. Another company's thread, another
    // customer's, one this caller was never added to, an internal one, and one that does not exist
    // all return 404. Distinguishing them would turn a thread id into an oracle for which
    // conversations exist, and three of those five cases are facts about other people's business.
    // =============================================================================================
    [PortalAuthorize]
    [Route("ClientPortal/Conversations")]
    public class ClientPortalConversationsController : Controller
    {
        private readonly ICommPortalService _conversations;
        private readonly IPortalContextAccessor _contexts;

        public ClientPortalConversationsController(
            ICommPortalService conversations, IPortalContextAccessor contexts)
        {
            _conversations = conversations;
            _contexts = contexts;
        }

        // ---- reads --------------------------------------------------------------------------------

        [HttpGet("")]
        public async Task<IActionResult> Index(int? pageSize, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            var page = new CommPageRequest { PageSize = pageSize };
            return Json(await _conversations.ListConversationsAsync(portal, page, ct));
        }

        [HttpGet("{threadId:long}")]
        public async Task<IActionResult> Details(long threadId, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            var conversation = await _conversations.GetConversationAsync(portal, threadId, ct);

            // Null covers absent, another company's, another customer's, not-a-participant, internal
            // and deleted. One answer for all six — see the header.
            return conversation == null ? NotFound() : Json(conversation);
        }

        [HttpGet("{threadId:long}/Comments")]
        public async Task<IActionResult> Comments(
            long threadId, long? afterId, int? pageSize, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            var page = new CommPageRequest { AfterId = afterId, PageSize = pageSize };
            return Json(await _conversations.ListCommentsAsync(portal, threadId, page, ct));
        }

        [HttpGet("{threadId:long}/Attachments")]
        public async Task<IActionResult> Attachments(long threadId, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            // METADATA ONLY. No StorageKey leaves the service, so this response names no bytes and
            // there is no URL for a later request to try without authority.
            return Json(await _conversations.ListAttachmentsAsync(portal, threadId, ct));
        }

        // ---- writes -------------------------------------------------------------------------------

        // The body is the ONLY thing the caller supplies. Not the author, not the visibility, not the
        // company, not the customer — the service decides all four from the resolved context.
        [HttpPost("{threadId:long}/Comments")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostComment(
            long threadId, string? body, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            var posted = await _conversations.PostCommentAsync(portal, threadId, body, ct);
            return posted == null ? NotFound() : Json(posted);
        }

        [HttpPost("{threadId:long}/Read")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkRead(
            long threadId, long? upToCommentId, CancellationToken ct = default)
        {
            var portal = await _contexts.TryGetCurrentAsync(ct);
            if (portal == null) return NotFound();

            bool marked = await _conversations.MarkReadAsync(portal, threadId, upToCommentId, ct);
            return marked ? Json(new { ok = true }) : NotFound();
        }
    }
}
