using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H143: deposits at the counter — taken, used on a bill, refunded — from the desk's payment window. The deposit
	// service moves the money and posts the ledger; every act asks first on the screen (the owner's rule for money).
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDepositTakeInput { public decimal Amount { get; set; } public string? Method { get; set; } public string? Purpose { get; set; } public string? Note { get; set; } }
		public sealed class HospitalDepositUseInput { public decimal Amount { get; set; } }
		public sealed class HospitalDepositRefundInput { public decimal Amount { get; set; } public string? Method { get; set; } public string? Reason { get; set; } }

		private HospitalDepositService? _deposits;
		private HospitalDepositService DepositSvc => _deposits ??= ActivatorUtilities.CreateInstance<HospitalDepositService>(HttpContext.RequestServices, _billing);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Patient/{id:int}/Deposit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FrontDeskTakeDeposit(int id, [FromBody] HospitalDepositTakeInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DepositSvc.TakeAsync(ctx, id, input.Amount, input.Method, input.Purpose, input.Note, me, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Bill/{id:int}/UseDeposit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FrontDeskUseDeposit(int id, [FromBody] HospitalDepositUseInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DepositSvc.UseAsync(ctx, id, input.Amount, me, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Deposits/{id:int}/Refund"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FrontDeskRefundDeposit(int id, [FromBody] HospitalDepositRefundInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DepositSvc.RefundAsync(ctx, id, input.Amount, input.Method, input.Reason, me, DateTime.UtcNow, ct));
		}
	}
}
