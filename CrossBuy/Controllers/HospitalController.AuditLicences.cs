using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H264 — the audit viewer of patient-file access; H265 — professional licences ahead of their lapse.
	public partial class HospitalController
	{
		private HospitalAccessAuditService? _auditSvc;
		private HospitalAccessAuditService AuditSvc => _auditSvc ??= ActivatorUtilities.CreateInstance<HospitalAccessAuditService>(HttpContext.RequestServices);
		private HospitalLicenceService? _licenceSvc;
		private HospitalLicenceService LicenceSvc => _licenceSvc ??= ActivatorUtilities.CreateInstance<HospitalLicenceService>(HttpContext.RequestServices);

		[HttpGet("Privacy/AccessLog"), HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AccessLog(string? who, string? patient, DateTime? fromDate, DateTime? toDate, string? action, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 25;
			var (f, t) = HospitalAccessAuditRules.Range(fromDate, toDate, DeskClock(DateTime.UtcNow).NowLocal);
			var d = await AuditSvc.PageAsync(ctx, who, patient, f, t, action, page, size, ct);
			if (d == null) return Forbid();
			ViewBag.Who = who; ViewBag.PatientQ = patient; ViewBag.FromDate = f; ViewBag.ToDate = t; ViewBag.Action = action ?? "";
			ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpGet("Practitioners/Licences"), HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> Licences(string? state, string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await LicenceSvc.PageAsync(ctx, state, q, page, size, DeskClock(DateTime.UtcNow).NowLocal, ct);
			if (d == null) return Forbid();
			ViewBag.State = state ?? ""; ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}
	}
}
