using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H134: batch recalls.
	//   · The list: searched, filtered and paged, with the new-recall form.
	//   · One recall's page shows where the batch is now and the patients who received it. Its actions sweep again,
	//     release the batch or close the recall.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalLotRecallEndInput { public string? Note { get; set; } }

		private HospitalLotRecallService? _recalls;
		private HospitalLotRecallService RecallSvc => _recalls ??= ActivatorUtilities.CreateInstance<HospitalLotRecallService>(HttpContext.RequestServices, _pharmacy);

		[HttpGet("Pharmacy/Recalls")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Recalls(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.Medicines = await _pharmacy.FormularyAsync(ctx, ct);
			return View(await RecallSvc.StatsAsync(ctx, ct));
		}

		[HttpGet("Pharmacy/Recalls/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecallsData(string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await RecallSvc.ListAsync(ctx, q, status, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_RecallRows", rows);
		}

		[HttpGet("Pharmacy/Recalls/Batches")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecallBatches(int itemId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			return Json((await RecallSvc.BatchesOfAsync(ctx, itemId, ct)).Select(b => new { batchNo = b.BatchNo, expiry = b.Expiry?.ToString("yyyy-MM-dd"), onHand = b.OnHand }));
		}

		[HttpPost("Pharmacy/Recalls"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> RecallOpen([FromBody] HospitalLotRecallInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecallSvc.OpenAsync(ctx, input, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpGet("Pharmacy/Recalls/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Recall(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await RecallSvc.ViewAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			return View(v);
		}

		[HttpPost("Pharmacy/Recalls/{id:int}/Sweep"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> RecallSweep(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecallSvc.SweepAsync(ctx, id, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/Recalls/{id:int}/Release"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> RecallRelease(int id, [FromBody] HospitalLotRecallEndInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecallSvc.ReleaseAsync(ctx, id, input?.Note, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/Recalls/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> RecallClose(int id, [FromBody] HospitalLotRecallEndInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecallSvc.CloseAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
