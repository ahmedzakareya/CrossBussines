using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H26: a doctor's weekly schedule and leave, edited on the practitioner's page. Every rule is
	// HospitalDoctorSchedule's; built from the request's services like H11–H25, so Program.cs is untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalScheduleService? _scheduleSvc;
		private HospitalScheduleService ScheduleSvc => _scheduleSvc ??= ActivatorUtilities.CreateInstance<HospitalScheduleService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioner/{id:int}/Schedule/Start"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ScheduleStart(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScheduleSvc.StartWeeklyAsync(ctx, id, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioner/{id:int}/Sessions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ScheduleAddSession(int id, [FromBody] HospitalSessionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScheduleSvc.AddSessionAsync(ctx, id, input, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioner/Sessions/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ScheduleRemoveSession(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScheduleSvc.RemoveSessionAsync(ctx, id, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioner/{id:int}/Leaves"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ScheduleAddLeave(int id, [FromBody] HospitalLeaveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScheduleSvc.AddLeaveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioner/Leaves/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ScheduleRemoveLeave(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScheduleSvc.RemoveLeaveAsync(ctx, id, ct));
		}
	}
}
