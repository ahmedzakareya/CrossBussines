using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H84: immunizations. The due list (overdue, due, coming in two weeks; searched and paged), a patient's
	// card (the schedule over his birth date and what he was given), recording a dose and an adverse event, and the
	// schedule itself (a setting the administrator loads and changes).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalImmunizationService? _immSvc;
		private HospitalImmunizationService ImmSvc => _immSvc ??= ActivatorUtilities.CreateInstance<HospitalImmunizationService>(HttpContext.RequestServices);

		public sealed class HospitalVaccineItemInput { public int? Id { get; set; } public string? Code { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public int DoseNo { get; set; } public int AgeDays { get; set; } public string? Route { get; set; } public bool IsActive { get; set; } = true; }
		public sealed class HospitalAefiInput { public string? Text { get; set; } }

		[HttpGet("Vaccines")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Vaccines(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, ready) = await ImmSvc.ScheduleAsync(ctx, ct);
			ViewBag.Ready = ready;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(rows);
		}

		[HttpGet("Vaccines/Due")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VaccinesDue(string? state, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _) = await ImmSvc.DueAsync(ctx, state, q, page, pageSize, DateTime.UtcNow, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			return PartialView("_VaccineDueRows", rows);
		}

		[HttpGet("Patients/{id:int}/Vaccines")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PatientVaccines(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var card = await ImmSvc.CardAsync(ctx, id, DateTime.UtcNow, ct);
			if (card == null) return NotFound();
			Chrome();
			ViewBag.CanGive = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(card);
		}

		[HttpPost("Vaccines/Give"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> GiveVaccine([FromBody] HospitalImmunizationInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmSvc.RecordAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Vaccines/{id:int}/Aefi"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VaccineAefi(int id, [FromBody] HospitalAefiInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmSvc.ReportAdverseEventAsync(ctx, id, input.Text, ct));
		}

		[HttpPost("Vaccines/Schedule/Seed"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedVaccineSchedule(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmSvc.SeedDefaultsAsync(ctx, ct));
		}

		[HttpPost("Vaccines/Schedule"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveVaccineItem([FromBody] HospitalVaccineItemInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmSvc.SaveScheduleItemAsync(ctx, input.Id, input.Code, input.Name, input.NameEn, input.DoseNo, input.AgeDays, input.Route, input.IsActive, ct));
		}
	}
}
