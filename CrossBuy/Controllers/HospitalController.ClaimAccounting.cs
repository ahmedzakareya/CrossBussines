using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H20 — insurance accounting: the account mapping (billing settings) and a payer's payment on a batch.
	public partial class HospitalController
	{
		public sealed class HospitalPayerAccountInput { public int? AccountId { get; set; } }
		public sealed class HospitalRemittanceInput { public decimal Amount { get; set; } public string? Reference { get; set; } }

		private HospitalClaimAccountingService? _claimAccountingSvc;
		private HospitalClaimAccountingService ClaimAccountingSvc => _claimAccountingSvc ??= ActivatorUtilities.CreateInstance<HospitalClaimAccountingService>(HttpContext.RequestServices);

		[HttpPost("Settings/InsuranceAccounts"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveInsuranceAccounts([FromBody] HospitalInsuranceAccountsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ClaimAccountingSvc.SaveAccountsAsync(ctx, input ?? new(), ct));
		}

		[HttpPost("Settings/InsuranceAccounts/Payer/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePayerReceivable(int id, [FromBody] HospitalPayerAccountInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ClaimAccountingSvc.SavePayerAccountAsync(ctx, id, input?.AccountId, ct));
		}

		[HttpPost("Insurance/Batch/{id:int}/Remit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> RemitClaimBatch(int id, [FromBody] HospitalRemittanceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ClaimAccountingSvc.RemitAsync(ctx, id, input.Amount, input.Reference, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
