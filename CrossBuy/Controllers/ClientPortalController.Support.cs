using CrossBuy.BL.CrmIdentity;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// CRM C8 — Support on the client portal: the customer's own requests, a new request, the thread with our team, the
	// rating after resolution, and the help articles the company published. Every action resolves the caller's portal
	// context first; the service is handed the CONTEXT, so the company and the customer are never values a request
	// supplies. Same shape as Updates: form posts, a redirect back, one TempData flag for a refusal.
	public partial class ClientPortalController
	{
		private ICrmServiceDeskService Desk => _desk ??= ActivatorUtilities.CreateInstance<CrmServiceDeskService>(HttpContext.RequestServices);
		private ICrmServiceDeskService? _desk;

		[HttpGet("Support")]
		public async Task<IActionResult> Support(string? q, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			ViewBag.Help = await Desk.PortalHelpAsync(me, q);
			ViewBag.Q = q;
			return View("Support", await Desk.PortalTicketsAsync(me));
		}

		[HttpPost("Support")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> OpenRequest(string? subject, string? body, string? category, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			var (ok, code, id) = await Desk.PortalOpenAsync(me, subject, body, category);
			if (!ok) { TempData["PortalSupportError"] = code; TempData["PortalSupportSubject"] = subject; TempData["PortalSupportBody"] = body; return RedirectToAction(nameof(Support)); }
			return RedirectToAction(nameof(SupportRequest), new { id });
		}

		[HttpGet("Support/{id:int}")]
		public async Task<IActionResult> SupportRequest(int id, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			var view = await Desk.PortalTicketAsync(me, id);
			if (view == null) return NotFound();
			ViewBag.Suggested = await Desk.SuggestAsync(me.CompanyId, view.T.Subject + " " + view.T.Description, publicOnly: true);
			return View("SupportRequest", view);
		}

		[HttpPost("Support/{id:int}/Reply")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SupportReply(int id, string? body, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			var (ok, code) = await Desk.PortalReplyAsync(me, id, body);
			if (code == "not_found") return NotFound();
			if (!ok) TempData["PortalSupportError"] = code;
			return RedirectToAction(nameof(SupportRequest), new { id });
		}

		[HttpPost("Support/{id:int}/Rate")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SupportRate(int id, int score, string? comment, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			var (ok, code) = await Desk.PortalRateAsync(me, id, score, comment);
			if (code == "not_found") return NotFound();
			if (!ok) TempData["PortalSupportError"] = code;
			return RedirectToAction(nameof(SupportRequest), new { id });
		}

		[HttpGet("Help/{id:int}")]
		public async Task<IActionResult> HelpArticle(int id, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			var a = await Desk.PortalArticleAsync(me, id);
			return a == null ? NotFound() : View("HelpArticle", a);
		}

		[HttpPost("Help/{id:int}/Vote")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> HelpVote(int id, bool helpful, CancellationToken ct = default)
		{
			var me = await _contexts.TryGetCurrentAsync(ct);
			if (me == null) return View("NoAccess");
			if (!await Desk.PortalVoteAsync(me, id, helpful)) return NotFound();
			TempData["PortalHelpVoted"] = true;
			return RedirectToAction(nameof(HelpArticle), new { id });
		}
	}
}
