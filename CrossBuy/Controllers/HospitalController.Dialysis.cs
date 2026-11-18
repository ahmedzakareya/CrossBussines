using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H87: haemodialysis. The unit's day (searched, filtered by shift and state, paged), the patients on a
	// plan (searched, paged), a patient's plan and history, one session; saving and stopping a plan, starting,
	// observing and finishing a session, and marking a scheduled day missed.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalDialysisService? _dialysisSvc;
		private HospitalDialysisService DialysisSvc => _dialysisSvc ??= ActivatorUtilities.CreateInstance<HospitalDialysisService>(HttpContext.RequestServices);

		private static void Paging(ref int page, ref int pageSize) { if (pageSize is < 1 or > 200) pageSize = 25; if (page < 1) page = 1; }

		private void PageHeaders(int total, int page, int pageSize)
		{
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
		}

		[HttpGet("Dialysis")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Dialysis(DateTime? date, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await DialysisSvc.BoardAsync(ctx, date, null, null, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanTreat = await DialysisSvc.CanTreatAsync(ctx, ct);
			ViewBag.Today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			ViewBag.UnitSettings = await UnitSettingsSvc.ViewAsync(ctx, HospitalSessionKinds.Dialysis, ct);   // H123
			return View(board);
		}

		[HttpGet("Dialysis/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisData(DateTime? date, string? q, int? shift, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (board, _) = await DialysisSvc.BoardAsync(ctx, date, q, shift, state, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(board.Total, page, pageSize);
			ViewBag.CanTreat = await DialysisSvc.CanTreatAsync(ctx, ct);
			ViewBag.Today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			return PartialView("_DialysisBoardRows", board);
		}

		[HttpGet("Dialysis/Patients")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisPatients(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await DialysisSvc.PatientsAsync(ctx, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanPlan = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View();
		}

		[HttpGet("Dialysis/Patients/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisPatientsData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await DialysisSvc.PatientsAsync(ctx, q, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_DialysisPatientRows", rows);
		}

		[HttpGet("Dialysis/Patient/{patientId:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisPatient(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await DialysisSvc.PatientPageAsync(ctx, patientId, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanPlan = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(page);
		}

		[HttpGet("Dialysis/Session/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisSession(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await DialysisSvc.SessionPageAsync(ctx, id, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanTreat = await DialysisSvc.CanTreatAsync(ctx, ct);
			// H122: the unit's kit — what this session draws from the dialysis store, and whether it left
			ViewBag.Kit = await SessionKitSvc.KitAsync(ctx, HospitalSessionKinds.Dialysis, page.Session.KitIssuedAt, ct);
			ViewBag.UnitSettings = await UnitSettingsSvc.ViewAsync(ctx, HospitalSessionKinds.Dialysis, ct);   // H123: the kit, the store and the service, in the unit's settings
			return View(page);
		}

		[HttpPost("Dialysis/Plan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SaveDialysisPlan([FromBody] HospitalDialysisPlanInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DialysisSvc.SavePlanAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Plan/{id:int}/Verify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]   // H183
		public async Task<IActionResult> VerifyDialysisPlan(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalDialysisService>(HttpContext.RequestServices).VerifyPlanAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Plan/{id:int}/Stop"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> StopDialysisPlan(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DialysisSvc.StopPlanAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Start"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> StartDialysis([FromBody] HospitalDialysisStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DialysisSvc.StartAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Observe"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ObserveDialysis([FromBody] HospitalDialysisObservationInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DialysisSvc.ObserveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Session/{id:int}/Finish"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FinishDialysis(int id, [FromBody] HospitalDialysisFinishInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await DialysisSvc.FinishAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			// H122: a finished session's kit leaves the dialysis store now; a short store never undoes the session — the page
			// keeps "not issued" and the reason, and the kit is issued from there once the store is stocked
			return await CompletedWithKitAsync(ctx, HospitalSessionKinds.Dialysis, r, ct);   // H130: one path for the three units
		}

		[HttpPost("Dialysis/Session/{id:int}/Kit/Issue"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DialysisKitIssue(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SessionKitSvc.IssueAsync(ctx, HospitalSessionKinds.Dialysis, id, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dialysis/Miss"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MissDialysis([FromBody] HospitalDialysisMissInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DialysisSvc.MissAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
