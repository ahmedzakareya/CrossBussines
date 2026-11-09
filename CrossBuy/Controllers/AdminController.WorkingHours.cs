using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// The working-hours schedule for Ashal (Kuwait Resolution 15/2025), 2026-11-09. HR read; printing is Report Studio's
	// hr.working-hours (the view links there and builds no print document of its own).
	public partial class AdminController
	{
		[HttpGet]
		public async Task<IActionResult> WorkingHours(int? year, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var y = year ?? DateTime.Today.Year;
			ViewBag.Year = y;
			return View(await new CrossBuy.BL.Hr.HrWorkingHoursService(Db).BuildAsync(gate.CompanyId, y, ct));
		}
	}
}
