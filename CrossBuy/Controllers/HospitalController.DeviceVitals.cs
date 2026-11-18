using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H201 — bedside monitors: the sets they sent, waiting for a nurse to chart or discard; a message received by
	// hand (pasted, or the file the monitors' gateway wrote). All `admit` (the ward's nursing work).
	public partial class HospitalController
	{
		public sealed class HospitalVitalRawInput { public string? Raw { get; set; } }
		public sealed class HospitalVitalDiscardInput { public string? Reason { get; set; } }

		private HospitalDeviceVitalService? _devVitalSvc;
		private HospitalDeviceVitalService DevVitalSvc => _devVitalSvc ??= ActivatorUtilities.CreateInstance<HospitalDeviceVitalService>(HttpContext.RequestServices, ObsSvc);

		[HttpGet("Wards/Monitors")]
		[HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> DeviceVitals(string? q, string? status, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(status) && !HospitalDeviceVitalStatus.All.Contains(status)) status = null;
			var (rows, total, waiting, ready) = await DevVitalSvc.ListAsync(ctx, q, status, page, size, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size);
			ViewBag.Total = total; ViewBag.Waiting = waiting; ViewBag.Ready = ready;
			return View(rows);
		}

		[HttpPost("Wards/Monitors/Receive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> DeviceVitalReceive([FromBody] HospitalVitalRawInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DevVitalSvc.ReceiveAsync(ctx, input.Raw, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Wards/Monitors/{id:int}/Accept"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> DeviceVitalAccept(int id, [FromBody] HospitalVitalAcceptInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, news2, risk, escalated) = await DevVitalSvc.AcceptAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, id = r.Id, news2, risk, riskText = risk == null ? null : _t["news2risk:" + risk].Value, escalated });
		}

		[HttpPost("Wards/Monitors/{id:int}/Discard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> DeviceVitalDiscard(int id, [FromBody] HospitalVitalDiscardInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DevVitalSvc.DiscardAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
