using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H45: incident reports & patient safety (the prototype's #/incidents). Any staff member reports;
	// quality management reviews, analyses and closes. Every rule is HospitalIncidentService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalIncidentService? _incSvc;
		private HospitalIncidentService IncSvc => _incSvc ??= ActivatorUtilities.CreateInstance<HospitalIncidentService>(HttpContext.RequestServices);

		public sealed class HospitalIncidentMoveInput { public string To { get; set; } = ""; public string? RootCause { get; set; } public string? Actions { get; set; } public string? Outcome { get; set; } }

		[HttpGet("Incidents")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Incidents(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await IncSvc.BoardAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			// the patient picker is the admission desk's search: shown to those who may use it
			ViewBag.CanFindPatient = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).OrderBy(w => w.Code)
				.Select(w => new HospitalChoice(w.ID, w.Name, w.NameEn ?? w.Name, w.Code)).ToListAsync(ct);
			// H54: who a follow-up task can go to — the task module's own list of active staff
			var tasks = HttpContext.RequestServices.GetService<CrossBuy.BL.ITaskService>();
			if (ViewBag.CanManage == true && tasks != null) ViewBag.Staff = await tasks.ActiveEmployeesAsync(ctx.CompanyId);
			return View(board);
		}

		/// H54: a task of the task module for an action the root-cause analysis agreed.
		[HttpPost("Incidents/{id:int}/FollowUps"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> IncidentFollowUp(int id, [FromBody] HospitalIncidentFollowUpInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IncSvc.AddFollowUpAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Incidents"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> IncidentReport([FromBody] HospitalIncidentInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IncSvc.ReportAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Incidents/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> IncidentMove(int id, [FromBody] HospitalIncidentMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IncSvc.MoveAsync(ctx, id, input.To, input.RootCause, input.Actions, input.Outcome, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Incidents/{id:int}/Rca"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> IncidentRca(int id, [FromBody] HospitalIncidentMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IncSvc.SaveRcaAsync(ctx, id, input.RootCause, input.Actions, ct));
		}
	}
}
