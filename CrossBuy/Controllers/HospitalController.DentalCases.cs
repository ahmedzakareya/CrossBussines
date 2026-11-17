using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H17: the long dental cases, orthodontics and the implant register. Every rule is
	// HospitalDentalCasesService's. Built from the request's services like H11–H16, so Program.cs and the
	// shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalOrthoPaymentInput { public decimal Amount { get; set; } public string? Method { get; set; } public string? Reference { get; set; } }

		private HospitalDentalCasesService? _casesSvc;
		private HospitalDentalCasesService CasesSvc => _casesSvc ??= ActivatorUtilities.CreateInstance<HospitalDentalCasesService>(HttpContext.RequestServices, DentalSvc, _billing);

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
			ViewBag.CanPay = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);   // H167: money is the cashier's act
			ViewBag.UnitSettings = await ActivatorUtilities.CreateInstance<HospitalUnitSettingsService>(HttpContext.RequestServices).ViewAsync(ctx, HospitalKitUnits.DentalOrtho, ct);
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

		/// H167: money is the cashier's act — `bill` at the door and in the service.
		[HttpPost("Dental/Ortho/{id:int}/Payment"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		[HospitalIdempotent]   // H166: sent twice, collected once
		public async Task<IActionResult> DentalOrthoPayment(int id, [FromBody] HospitalOrthoPaymentInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.RecordPaymentAsync(ctx, id, input.Amount, input.Method, input.Reference, ctx.EmployeeId, DateTime.UtcNow, DateTime.Now.Date, ct));
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
			// H168: the clinic's loading protocol (and who approved it), every implant's stability measurements
			var protocol = await CasesSvc.ProtocolAsync(ctx, ct);
			ViewBag.Protocol = protocol;
			ViewBag.ProtocolBy = protocol == null ? null : await _db.Employee.AsNoTracking().Where(e => e.ID == protocol.ApprovedByEmployeeId && e.EmpCompanyID == ctx.CompanyId).Select(e => e.FullName).FirstOrDefaultAsync(ct);
			ViewBag.Measures = await CasesSvc.MeasuresOfAsync(ctx, rows.Select(r => r.Implant.ID).ToList(), ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
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
			return Result(await CasesSvc.MoveImplantAsync(ctx, id, input, DateTime.Now.Date, ct, ctx.EmployeeId));
		}

		[HttpPost("Dental/Implants/{id:int}/Measure"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]   // H168
		public async Task<IActionResult> DentalImplantMeasure(int id, [FromBody] HospitalImplantMeasureInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.AddMeasureAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, DateTime.Now.Date, ct));
		}

		[HttpPost("Dental/Implants/Protocol"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]   // H168: the clinical lead's approval
		public async Task<IActionResult> DentalImplantProtocol([FromBody] HospitalImplantProtocolInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CasesSvc.SaveProtocolAsync(ctx, input, me, DateTime.UtcNow, ct));
		}
	}
}
