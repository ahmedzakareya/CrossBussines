using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H82: the billing desk records a payer's recoupment of an accepted claim line, from the batch screen.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalRecoupService? _recoupSvc;
		private HospitalRecoupService RecoupSvc => _recoupSvc ??= ActivatorUtilities.CreateInstance<HospitalRecoupService>(HttpContext.RequestServices);

		public sealed class HospitalRecoupInput { public decimal Amount { get; set; } public string? Reason { get; set; } }

		[HttpPost("Insurance/Line/{id:int}/Recoup"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> RecoupClaimLine(int id, [FromBody] HospitalRecoupInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecoupSvc.RecoupAsync(ctx, id, input.Amount, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
