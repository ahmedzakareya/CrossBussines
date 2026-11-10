using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H76: infection prevention and control. The infection-control screen (who is isolated, which devices are
	// in and for how many days, the case register searched and paged, the month's rates per ward), and the ward's own
	// actions from the nursing station: start isolation, record a device in and out, report a suspected infection.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalInfectionService? _infectionSvc;
		private HospitalInfectionService InfectionSvc => _infectionSvc ??= ActivatorUtilities.CreateInstance<HospitalInfectionService>(HttpContext.RequestServices);

		public sealed class HospitalEndIsolationInput { public string? Note { get; set; } }
		public sealed class HospitalRemoveDeviceInput { public DateTime? At { get; set; } }

		[HttpGet("Infection")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Infection(int? year, int? month, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow;
			var local = HospitalNursingService.UtcToWard(now);
			int y = year is >= 2000 and <= 2100 ? year.Value : local.Year;
			int m = month is >= 1 and <= 12 ? month.Value : local.Month;
			if (new DateTime(y, m, 1) > local.Date) { y = local.Year; m = local.Month; }
			ViewBag.Year = y; ViewBag.Month = m;
			ViewBag.Rates = await InfectionSvc.RatesAsync(ctx, y, m, now, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanEnd = ViewBag.CanManage == true || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanWard = ViewBag.CanEnd == true || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(await InfectionSvc.PageAsync(ctx, now, ct));
		}

		[HttpGet("Infection/Cases")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> InfectionCases(string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total) = await InfectionSvc.CasesAsync(ctx, q, status, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_InfectionCaseRows", rows);
		}

		[HttpPost("Infection/Isolation"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> StartIsolation([FromBody] HospitalIsolationInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.StartIsolationAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Infection/Isolation/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> EndIsolation(int id, [FromBody] HospitalEndIsolationInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.EndIsolationAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Infection/Device"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> InsertDevice([FromBody] HospitalDeviceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.InsertDeviceAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Infection/Device/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RemoveDevice(int id, [FromBody] HospitalRemoveDeviceInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.RemoveDeviceAsync(ctx, id, input?.At, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Infection/Case"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ReportInfection([FromBody] HospitalInfectionCaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.ReportCaseAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Infection/Case/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DecideInfection(int id, [FromBody] HospitalInfectionDecideInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InfectionSvc.DecideCaseAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
