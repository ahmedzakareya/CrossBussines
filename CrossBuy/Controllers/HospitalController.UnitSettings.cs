using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H123: a treatment unit's settings (its session service; its kit and store), saved from the modal every unit
	// page carries in its toolbar.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalUnitSettingsService? _unitSettingsSvc;
		private HospitalUnitSettingsService UnitSettingsSvc => _unitSettingsSvc ??= ActivatorUtilities.CreateInstance<HospitalUnitSettingsService>(HttpContext.RequestServices);

		[HttpPost("Units/{unit}/Settings"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> UnitSettingsSave(string unit, [FromBody] HospitalUnitSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await UnitSettingsSvc.SaveAsync(ctx, unit, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
