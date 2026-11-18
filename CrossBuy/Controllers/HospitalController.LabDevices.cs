using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H198 — the laboratory's analysers: the messages received, a message received by hand (pasted or a file the
	// middleware wrote), a device code linked to an analyte, a message applied again. All `lab` (the bench's work).
	public partial class HospitalController
	{
		public sealed class HospitalDeviceRawInput { public string? Raw { get; set; } }
		public sealed class HospitalDeviceMapInput { public string? Device { get; set; } public string? Code { get; set; } public int AnalyteId { get; set; } }

		private HospitalLabDeviceService DeviceSvc => ActivatorUtilities.CreateInstance<HospitalLabDeviceService>(HttpContext.RequestServices);

		[HttpGet("Lab/Devices")]
		[HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabDevices(string? q, string? status, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var (rows, total, ready) = await DeviceSvc.ListAsync(ctx, q, status, page, size, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total; ViewBag.Ready = ready;
			ViewBag.Analytes = await DeviceSvc.AnalytesAsync(ctx, ct);
			return View(rows);
		}

		[HttpPost("Lab/Devices/Receive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabDeviceReceive([FromBody] HospitalDeviceRawInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await DeviceSvc.ReceiveAsync(ctx, input.Raw, ctx.EmployeeId, DateTime.UtcNow, ct);
			return await ReceiptJson(ctx, r, ct);
		}

		[HttpPost("Lab/Devices/{id:int}/Again"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabDeviceAgain(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await DeviceSvc.ApplyAgainAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			return await ReceiptJson(ctx, r, ct);
		}

		[HttpPost("Lab/Devices/Map"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabDeviceMap([FromBody] HospitalDeviceMapInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DeviceSvc.MapAsync(ctx, input.Device, input.Code, input.AnalyteId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		private async Task<IActionResult> ReceiptJson(BusinessContext ctx, HospitalResult r, CancellationToken ct)
		{
			if (!r.Ok) return Result(r);
			var rc = await DeviceSvc.ReceiptAsync(ctx, r.Id ?? 0, DateTime.UtcNow, ct);
			if (rc == null) return NotFound(new { ok = false, code = "not_found" });
			return Json(new { ok = true, id = rc.MessageId, status = rc.Status, statusText = _t["dev:" + rc.Status].Value, applied = rc.Applied, detail = rc.Detail, ack = rc.Ack });
		}
	}
}
