using CrossBuy.Models.Platform;
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
		private HospitalSessionKitService? _sessionKitSvc;   // H122/H130
		private HospitalSessionKitService SessionKitSvc => _sessionKitSvc ??= ActivatorUtilities.CreateInstance<HospitalSessionKitService>(HttpContext.RequestServices);

		/// H130: a completed session's kit issued from its page or its row, for any treatment unit.
		[HttpPost("Units/{unit}/Sessions/{id:int}/Kit/Issue"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SessionKitIssue(string unit, int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SessionKitSvc.IssueAsync(ctx, unit, id, ctx.UserId, DateTime.UtcNow, ct));
		}

		/// H130: a session just completed draws its kit; a short store never undoes it — the reason comes back to be told.
		private async Task<IActionResult> CompletedWithKitAsync(BusinessContext ctx, string unit, HospitalResult done, CancellationToken ct)
		{
			if (!done.Ok || done.Id is not int id) return Result(done);
			var kit = await SessionKitSvc.IssueAsync(ctx, unit, id, ctx.UserId, DateTime.UtcNow, ct);
			bool quiet = kit.Ok || kit.Error is "The unit has no kit set" or "The kit is issued when the session is finished" or HospitalSessionKitService.NoTables;
			return Json(new { ok = true, id, kitIssued = kit.Ok, kitError = quiet ? null : _t[kit.Error ?? "The change could not be saved", kit.Args ?? Array.Empty<object>()].Value });
		}

		[HttpPost("Units/{unit}/Settings"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> UnitSettingsSave(string unit, [FromBody] HospitalUnitSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await UnitSettingsSvc.SaveAsync(ctx, unit, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
