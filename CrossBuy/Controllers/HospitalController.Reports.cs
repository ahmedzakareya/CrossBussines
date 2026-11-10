using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H49: the hospital's reports (the prototype's #/reports). A list, nothing more: every report is a
	// registered Report Studio dataset (HospitalManagementDatasets) and opens in /Reports/Viewer, where it is run,
	// laid out, exported, scheduled and archived. The financial and insurance ones are the billing desk's (`bill`).
	// ==========================================================================================
	public partial class HospitalController
	{
		[HttpGet("Reports")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Reports(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Finance = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			return View(CrossBuy.BL.Reporting.HospitalManagementDatasets.All());
		}
	}
}
