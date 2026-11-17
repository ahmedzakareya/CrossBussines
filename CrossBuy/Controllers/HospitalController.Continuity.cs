using CrossBuy.BL;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H166: the desk's continuity — a light "are we connected?" the desk asks every half minute (it shows the
	// time of the last answer, and catches up the board when the connection comes back). The double-press guard is
	// [HospitalIdempotent] on the actions themselves; the print check is the desk's own script.
	// ==========================================================================================
	public partial class HospitalController
	{
		[HospitalReceptionAllowed]   // H166
		[HttpGet("FrontDesk/Ping")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskPing(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return StatusCode(StatusCodes.Status401Unauthorized, new { ok = false });
			Response.Headers["Cache-Control"] = "no-store";
			return Json(new { ok = true, at = DateTime.UtcNow });
		}
	}
}
