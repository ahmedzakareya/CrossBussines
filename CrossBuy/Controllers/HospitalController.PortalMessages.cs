using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H249 — the clinic's inbox of portal messages: read, reply, close.
	public partial class HospitalController
	{
		private HospitalPortalMessageService? _pmSvc;
		private HospitalPortalMessageService PmSvc => _pmSvc ??= ActivatorUtilities.CreateInstance<HospitalPortalMessageService>(HttpContext.RequestServices);
		public sealed class HospitalPortalClinicReplyInput { public string? Body { get; set; } public bool Close { get; set; } }

		[HttpGet("PortalMessages"), HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> PortalMessages(string? q, string? view, int? id, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await PmSvc.InboxAsync(ctx, q, view, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Value.Total + size - 1) / size);
			ViewBag.Open = id is int t ? await PmSvc.ClinicThreadAsync(ctx, t, DateTime.UtcNow, ct) : null;
			return View(d.Value);
		}

		[HttpPost("PortalMessages/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> PortalMessageReply(int id, [FromBody] HospitalPortalClinicReplyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PmSvc.ClinicReplyAsync(ctx, id, new HospitalPortalReplyInput { Body = input.Body }, input.Close, DateTime.UtcNow, ct));
		}
	}
}
