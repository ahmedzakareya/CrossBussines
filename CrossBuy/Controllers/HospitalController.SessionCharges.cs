using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// H119: a treatment session completed before its price existed, charged from its own screen — once.
	public partial class HospitalController
	{
		private HospitalSessionChargeService? _sessionChargeSvc;
		private HospitalSessionChargeService SessionChargeSvc => _sessionChargeSvc ??= ActivatorUtilities.CreateInstance<HospitalSessionChargeService>(HttpContext.RequestServices);

		[HttpPost("Sessions/{kind}/{id:int}/Charge"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> SessionCharge(string kind, int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return Unauthorized();
			return Result(await SessionChargeSvc.ChargeLaterAsync(ctx, kind, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
