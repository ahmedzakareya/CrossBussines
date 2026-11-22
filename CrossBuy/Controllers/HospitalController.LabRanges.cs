using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H233 — an analyte's reference ranges by age and sex: read by anyone on the lab setup, kept by the administrator.
	public partial class HospitalController
	{
		[HttpGet("LabTests/Analyte/{id:int}/Ranges"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabRanges(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var rows = await _lab.RangesAsync(ctx, id, ct);
			return Json(new { ok = true, rows = rows.Select(r => new { id = r.ID, sex = r.Sex, ageFromDays = r.AgeFromDays, ageToDays = r.AgeToDays, refLow = r.RefLow, refHigh = r.RefHigh, criticalLow = r.CriticalLow, criticalHigh = r.CriticalHigh }) });
		}

		[HttpPost("LabTests/Ranges"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> LabRangeSave([FromBody] HospitalLabRangeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.SaveRangeAsync(ctx, input, ct));
		}

		[HttpPost("LabTests/Ranges/{id:int}/Delete"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> LabRangeDelete(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.DeleteRangeAsync(ctx, id, ct));
		}
	}
}
