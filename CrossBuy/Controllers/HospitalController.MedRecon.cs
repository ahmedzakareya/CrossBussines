using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H72: medication reconciliation for one admission — the home list and its decisions on admission, every
	// medicine's decision for home on discharge, each stage signed by a doctor, and the home list written into the
	// discharge summary.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalMedReconService? _medRecSvc;
		private HospitalMedReconService MedRecSvc => _medRecSvc ??= ActivatorUtilities.CreateInstance<HospitalMedReconService>(HttpContext.RequestServices);

		public sealed class HospitalMedReconCompleteInput { public string? Stage { get; set; } public bool NoHomeMeds { get; set; } }

		[HttpGet("Admissions/{id:int}/MedRec")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MedRec(int id, string? stage, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var view = await MedRecSvc.PageAsync(ctx, id, ct);
			if (view == null) return NotFound();
			Chrome();
			ViewBag.Stage = stage == Models.Context.Hospital.HospitalMedReconStage.Discharge ? stage : Models.Context.Hospital.HospitalMedReconStage.Admission;
			ViewBag.CanDecide = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanList = ViewBag.CanDecide == true || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(view);
		}

		[HttpPost("Admissions/MedRec/Line"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AddMedRecLine([FromBody] HospitalMedLineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRecSvc.AddLineAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/MedRec/Line/{lineId:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RemoveMedRecLine(int lineId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRecSvc.RemoveLineAsync(ctx, lineId, ctx.EmployeeId, ct));
		}

		[HttpPost("Admissions/MedRec/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DecideMedRec([FromBody] HospitalMedDecideInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRecSvc.DecideAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/{id:int}/MedRec/Complete"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CompleteMedRec(int id, [FromBody] HospitalMedReconCompleteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRecSvc.CompleteAsync(ctx, id, input.Stage ?? "", input.NoHomeMeds, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/{id:int}/MedRec/ToSummary"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> MedRecToSummary(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRecSvc.CopyToSummaryAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
