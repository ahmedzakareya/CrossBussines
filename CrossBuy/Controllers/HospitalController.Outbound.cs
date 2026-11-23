using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H247 — outbound HL7 feeds: the administrator's feeds, the queued and sent messages, collect and send.
	public partial class HospitalController
	{
		private HospitalOutboundService? _outSvc;
		private HospitalOutboundService OutSvc => _outSvc ??= ActivatorUtilities.CreateInstance<HospitalOutboundService>(HttpContext.RequestServices);
		private static string FacilityCode(BusinessContext ctx) => "CB" + ctx.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture);

		[HttpGet("Interfaces/Outbound"), HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> Outbound(string? q, string? status, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			try { await OutSvc.CollectAsync(ctx, FacilityCode(ctx), DateTime.UtcNow, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
			const int size = 25;
			var d = await OutSvc.PageAsync(ctx, q, status, page, size, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.Status = status ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpPost("Interfaces/Outbound/Feeds"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> OutboundFeedSave([FromBody] HospitalFeedInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OutSvc.SaveFeedAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Interfaces/Outbound/Feeds/{id:int}/Send"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> OutboundSend(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			await OutSvc.CollectAsync(ctx, FacilityCode(ctx), DateTime.UtcNow, ct);
			return Result(await OutSvc.SendAsync(ctx, id, DateTime.UtcNow, ct));
		}
	}
}
