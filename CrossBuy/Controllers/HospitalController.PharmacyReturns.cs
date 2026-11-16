using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H146: medicine a patient brings back, from the dispense panel — back on the shelf, off the open bill, any
	// overpaid excess as a credit on the patient's deposit balance. The service decides; the screen asks first.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDispenseReturnInput { public decimal Quantity { get; set; } public string? Reason { get; set; } }

		private HospitalDispenseReturnService? _returns;
		private HospitalDispenseReturnService ReturnSvc => _returns ??= ActivatorUtilities.CreateInstance<HospitalDispenseReturnService>(HttpContext.RequestServices, _billing);

		[HttpPost("Pharmacy/{id:int}/Lines/{lineId:int}/Return"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyReturn(int id, int lineId, [FromBody] HospitalDispenseReturnInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ReturnSvc.ReturnAsync(ctx, id, lineId, input.Quantity, input.Reason, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}
	}
}
