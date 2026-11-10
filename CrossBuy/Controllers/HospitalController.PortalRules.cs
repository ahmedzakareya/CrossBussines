using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H23 — Settings › Patient portal: how a patient books online (general rules, per clinic, per doctor).
	public partial class HospitalController
	{
		private HospitalPortalRulesService? _portalRulesSvc;
		private HospitalPortalRulesService PortalRulesSvc => _portalRulesSvc ??= ActivatorUtilities.CreateInstance<HospitalPortalRulesService>(HttpContext.RequestServices);

		[HttpGet("Settings/Portal")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> PortalSettings(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Ready = await PortalRulesSvc.ReadyAsync(ct);
			ViewBag.Clinics = await PortalRulesSvc.ClinicsAsync(ctx, ct);
			ViewBag.Doctors = await PortalRulesSvc.DoctorsAsync(ctx, ct);
			return View(await PortalRulesSvc.SettingsAsync(ctx, ct));
		}

		[HttpPost("Settings/Portal"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePortalSettings([FromBody] HospitalPortalSettings input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalRulesSvc.SaveSettingsAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Settings/Portal/Clinic/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePortalClinic(int id, [FromBody] HospitalPortalClinicInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalRulesSvc.SaveClinicAsync(ctx, id, input, ct));
		}

		[HttpPost("Settings/Portal/Doctor/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePortalDoctor(int id, [FromBody] HospitalPortalDoctorInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalRulesSvc.SaveDoctorAsync(ctx, id, input, ct));
		}
	}
}
