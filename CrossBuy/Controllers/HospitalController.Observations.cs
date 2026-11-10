using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H71: ward observations (NEWS2) and the Morse / Braden assessments, on the nursing station. The panel and
	// a patient's chart are partials the station loads; a set of observations answers with its score and risk so the
	// nurse sees them at once, and says whether an urgent task was opened.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalObservationService? _obsSvc;
		private HospitalObservationService ObsSvc => _obsSvc ??= ActivatorUtilities.CreateInstance<HospitalObservationService>(HttpContext.RequestServices);

		[HttpGet("Observations/Ward/{wardId:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ObservationsWard(int wardId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (rows, ready) = await ObsSvc.WardAsync(ctx, wardId, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return PartialView("_ObsPanel", rows);
		}

		[HttpGet("Observations/{admissionId:int}/Chart")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ObservationsChart(int admissionId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var chart = await ObsSvc.ChartAsync(ctx, admissionId, ct);
			return chart == null ? NotFound() : PartialView("_ObsChart", chart);
		}

		[HttpPost("Observations"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> RecordObservations([FromBody] HospitalObservationInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, news2, risk, escalated) = await ObsSvc.RecordAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			return r.Ok ? Json(new { ok = true, id = r.Id, news2, risk, escalated }) : Result(r);
		}

		[HttpPost("Observations/Assess"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> AssessRisk([FromBody] HospitalRiskInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ObsSvc.AssessAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
