using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H205 — bedside scanning: each ward's compliance (doses scanned over doses given, HIMSS 95 %) for a period,
	// and every scan — the ones that stopped a dose first among them when filtered. Read with `clinical-read`.
	public partial class HospitalController
	{
		private HospitalBedsideScanService? _bedsideScanSvc;
		private HospitalBedsideScanService BedsideScanSvc => _bedsideScanSvc ??= ActivatorUtilities.CreateInstance<HospitalBedsideScanService>(HttpContext.RequestServices);

		public sealed class HospitalScanPairInput { public string? First { get; set; } public string? Second { get; set; } public string? ScanOverride { get; set; } }

		/// H207: the transfusion starts at the bedside — the wristband and the bag scanned, or the reason written.
		[HttpPost("BloodBank/Units/{id:int}/Transfuse"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> BloodTransfuse(int id, [FromBody] HospitalScanPairInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BedsideScanSvc.TransfuseAsync(ctx, id, input?.First, input?.Second, input?.ScanOverride, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// H207: the mother's band and the baby's band, checked together.
		[HttpPost("Vital/Births/{id:int}/Bands"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> BirthBands(int id, [FromBody] HospitalScanPairInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BedsideScanSvc.MatchBabyAsync(ctx, id, input?.First, input?.Second, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Wards/Scanning")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> BedsideScans(int? ward, string? kind, string? outcome, string? q, int days = 30, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 25;
			days = days is 7 or 30 or 90 ? days : 30;
			if (!string.IsNullOrEmpty(kind) && !HospitalBedsideScanKind.All.Contains(kind)) kind = null;
			if (!string.IsNullOrEmpty(outcome) && !HospitalBedsideScanOutcome.All.Contains(outcome)) outcome = null;
			var to = DateTime.UtcNow; var from = to.AddDays(-days);
			var wards = await ActivatorUtilities.CreateInstance<HospitalNursingService>(HttpContext.RequestServices).WardsAsync(ctx, ct);
			var rows = new List<(HospitalWardOption? Ward, HospitalScanCompliance? Meds)>();
			rows.Add((null, await BedsideScanSvc.ComplianceAsync(ctx, null, HospitalBedsideScanKind.Medication, from, to, ct)));
			foreach (var w in wards) rows.Add((w, await BedsideScanSvc.ComplianceAsync(ctx, w.Id, HospitalBedsideScanKind.Medication, from, to, ct)));
			var (list, total) = await BedsideScanSvc.ListAsync(ctx, ward, kind, outcome, q, from, to, page, size, ct);
			ViewBag.Compliance = rows; ViewBag.Wards = wards;
			ViewBag.Ward = ward; ViewBag.Kind = kind; ViewBag.Outcome = outcome; ViewBag.Q = q; ViewBag.Days = days;
			ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total;
			ViewBag.Ready = rows[0].Meds != null;
			return View(list);
		}
	}
}
