using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H136: the pharmacist's review. On the dispensing screen the pharmacist can:
	//   · set a prescription's priority;
	//   · refuse it with the reason;
	//   · log an intervention, then record the prescriber's answer to it.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalRxPriorityInput { public string? Priority { get; set; } }
		public sealed class HospitalRxRejectInput { public string? Reason { get; set; } }
		public sealed class HospitalInterventionOutcomeInput { public string? Outcome { get; set; } }

		private HospitalPharmacyReviewService? _review;
		private HospitalPharmacyReviewService ReviewSvc => _review ??= ActivatorUtilities.CreateInstance<HospitalPharmacyReviewService>(HttpContext.RequestServices);

		[HttpPost("Pharmacy/{id:int}/Priority"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyPriority(int id, [FromBody] HospitalRxPriorityInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ReviewSvc.SetPriorityAsync(ctx, id, input?.Priority, ct));
		}

		[HttpPost("Pharmacy/{id:int}/Reject"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyReject(int id, [FromBody] HospitalRxRejectInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ReviewSvc.RejectAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/{id:int}/Interventions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyIntervention(int id, [FromBody] HospitalInterventionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ReviewSvc.RecordAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/Interventions/{iid:int}/Outcome"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyInterventionOutcome(int iid, [FromBody] HospitalInterventionOutcomeInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ReviewSvc.OutcomeAsync(ctx, iid, input?.Outcome, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
