using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H160: checkout at the desk after the consultation — the desk's tab ("awaiting desk completion"), the
	// checkout window (what the visit left for the desk) and closing it. Every rule is HospitalDeskCheckoutService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalCheckoutInput { public string? Note { get; set; } }

		private HospitalDeskCheckoutService? _checkout;
		private HospitalDeskCheckoutService CheckoutSvc => _checkout ??= ActivatorUtilities.CreateInstance<HospitalDeskCheckoutService>(HttpContext.RequestServices, _billing);

		[HospitalReceptionAllowed]   // H160: the desk's tab
		[HttpGet("FrontDesk/Checkouts")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskCheckouts(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			return PartialView("_DeskCheckouts", await CheckoutSvc.AwaitingAsync(ctx, today, ct));
		}

		[HospitalReceptionAllowed]   // H160: the checkout window
		[HttpGet("FrontDesk/Checkout/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskCheckout(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			var sheet = await CheckoutSvc.SheetAsync(ctx, id, today, now, ct);
			if (sheet == null) return NotFound();
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskCheckout", sheet);
		}

		[HospitalReceptionAllowed]   // H160
		[HttpPost("FrontDesk/Checkout/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		[HospitalIdempotent]   // H166: sent twice, done once
		public async Task<IActionResult> FrontDeskCloseCheckout(int id, [FromBody] HospitalCheckoutInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			return Result(await CheckoutSvc.CloseAsync(ctx, id, input?.Note, ctx.EmployeeId, today, now, ct));
		}
	}
}
