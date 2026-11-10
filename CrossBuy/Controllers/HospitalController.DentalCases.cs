using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H17: the long dental cases, orthodontics and the implant register. Every rule is
	// HospitalDentalCasesService's. Built from the request's services like H11–H16, so Program.cs and the
	// shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalOrthoPaymentInput { public decimal Amount { get; set; } public string? Reference { get; set; } }

		private HospitalDentalCasesService? _casesSvc;
		private HospitalDentalCasesService CasesSvc => _casesSvc ??= ActivatorUtilities.CreateInstance<HospitalDentalCasesService>(HttpContext.RequestServices, DentalSvc);

		[HttpGet("Dental/Ortho")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalOrtho(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, stats) = await CasesSvc.OrthoAsync(ctx, DateTime.Now.Date, ct);
			ViewBag.Stats = stats;
			ViewBag.Dentists = await DentalSvc.DentistsAsync(ctx, ct);
			ViewBag.Facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanPay = ViewBag.CanConsult == true || await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			return View(rows);
		}

		[HttpPost("Dental/Ortho"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalOpenOrtho([FromBody] HospitalOrthoCaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.OpenCaseAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Ortho/{id:int}/Visit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalOrthoVisit(int id, [FromBody] HospitalOrthoVisitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.LogVisitAsync(ctx, id, input, await MyPractitionerAsync(ctx, ct), DateTime.UtcNow, DateTime.Now.Date, ct));
		}

		/// `read` at the door; the service asks `bill` or `consult`.
		[HttpPost("Dental/Ortho/{id:int}/Payment"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalOrthoPayment(int id, [FromBody] HospitalOrthoPaymentInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.RecordPaymentAsync(ctx, id, input.Amount, input.Reference, ctx.EmployeeId, DateTime.UtcNow, DateTime.Now.Date, ct));
		}

		[HttpGet("Dental/Implants")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalImplants(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, stats) = await CasesSvc.ImplantsAsync(ctx, DateTime.Now.Date, ct);
			ViewBag.Stats = stats;
			ViewBag.Dentists = await DentalSvc.DentistsAsync(ctx, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(rows);
		}

		[HttpPost("Dental/Implants"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalPlanImplant([FromBody] HospitalImplantInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.PlanImplantAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Implants/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalMoveImplant(int id, [FromBody] HospitalImplantMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.MoveImplantAsync(ctx, id, input, DateTime.Now.Date, ct));
		}
	}
}
