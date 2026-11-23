using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H251 — the front desk sets a family proxy's scope (whole record / bookings only) and end date.
	public partial class HospitalController
	{
		[HospitalReceptionAllowed]
		[HttpPost("PortalAccounts/{id:int}/Family/{patientId:int}/Proxy"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountProxy(int id, int patientId, [FromBody] HospitalProxyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// the date picker gives the ward's day; it ends at that day's end, stored in UTC
			if (input.ExpiresAt is DateTime local) input.ExpiresAt = HospitalNursingService.WardToUtc(local);
			return Result(await ActivatorUtilities.CreateInstance<HospitalPortalProxyService>(HttpContext.RequestServices).SetAsync(ctx, id, patientId, input, DateTime.UtcNow, ct));
		}
	}
}
