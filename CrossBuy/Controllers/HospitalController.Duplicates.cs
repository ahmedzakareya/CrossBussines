using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H57: duplicate patient files. The front desk (`register`) sees the suspected pairs and compares them;
	// only the hospital administrator (`manage`, owner ruling 2026-11-08) merges.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalPatientMergeService? _mergeSvc;
		private HospitalPatientMergeService MergeSvc => _mergeSvc ??= ActivatorUtilities.CreateInstance<HospitalPatientMergeService>(HttpContext.RequestServices);

		public sealed class HospitalMergeInput { public int SurvivorId { get; set; } public int MergedId { get; set; } public string? Reason { get; set; } }

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Duplicates")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> Duplicates(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (history, ready) = await MergeSvc.HistoryAsync(ctx, ct);
			ViewBag.History = history; ViewBag.Ready = ready;
			ViewBag.CanMerge = ready && await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await MergeSvc.CandidatesAsync(ctx, 5000, ct));
		}

		/// The two files side by side (a partial the screen shows in its window).
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Duplicates/Compare")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> DuplicatesCompare(int a, int b, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var sides = await MergeSvc.CompareAsync(ctx, a, b, ct);
			if (sides == null) return NotFound();
			ViewBag.CanMerge = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_DuplicateCompare", sides.Value);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Duplicates/Merge"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DuplicatesMerge([FromBody] HospitalMergeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MergeSvc.MergeAsync(ctx, input.SurvivorId, input.MergedId, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
