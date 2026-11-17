using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H162: insurance for this visit — the window (payer, verification, services and approvals, estimated and
	// final share, next step), recording a verification, and requesting a prior approval from it. Every rule is
	// HospitalVisitCoverageService's (and the insurance service's for the approval).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalVisitCoverageService? _visitCover;
		private HospitalVisitCoverageService VisitCoverSvc => _visitCover ??= ActivatorUtilities.CreateInstance<HospitalVisitCoverageService>(HttpContext.RequestServices, _pricing, _billing);

		[HospitalReceptionAllowed]   // H162
		[HttpGet("FrontDesk/Coverage/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskCoverage(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			var sheet = await VisitCoverSvc.SheetAsync(ctx, id, today, now, ct);
			if (sheet == null) return NotFound();
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskCoverage", sheet);
		}

		[HospitalReceptionAllowed]   // H162
		[HttpPost("FrontDesk/Coverage/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskVerifyCoverage(int id, [FromBody] HospitalVisitCoverageInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			return Result(await VisitCoverSvc.VerifyAsync(ctx, id, input, ctx.EmployeeId, today, now, ct));
		}

		[HospitalReceptionAllowed]   // H162: the approval the contract needs, asked from the visit's window
		[HttpPost("FrontDesk/Coverage/{id:int}/Preauth/{serviceId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FrontDeskRequestPreauth(int id, int serviceId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var a = await _db.HospitalAppointments.FindAsync(new object[] { id }, ct);
			if (a == null || a.CompanyId != ctx.CompanyId) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.RequestPreauthAsync(ctx, a.PatientId, serviceId, null, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
