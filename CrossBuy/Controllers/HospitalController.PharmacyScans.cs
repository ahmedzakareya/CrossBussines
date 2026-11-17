using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H150: packs scanned at the pharmacy counter (GS1 DataMatrix). The service reads and checks the code; the
	// dispense then issues the scanned packs from their own batch.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalScanInput { public string? Code { get; set; } }

		private HospitalDispenseScanService? _scans;
		private HospitalDispenseScanService ScanSvc => _scans ??= ActivatorUtilities.CreateInstance<HospitalDispenseScanService>(HttpContext.RequestServices, _pharmacy);

		[HttpPost("Pharmacy/{id:int}/Scan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyScan(int id, [FromBody] HospitalScanInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScanSvc.ScanAsync(ctx, id, input.Code, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/{id:int}/Lines/{lineId:int}/ClearScans"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyClearScans(int id, int lineId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ScanSvc.ClearAsync(ctx, id, lineId, ct));
		}
	}
}
