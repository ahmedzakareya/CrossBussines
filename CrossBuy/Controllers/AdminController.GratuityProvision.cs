using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// The end-of-service gratuity provision (2026-11-08): payroll-view reads, payroll-manage posts the true-up.
	public partial class AdminController
	{
		public sealed record GratuityRowsModel(IReadOnlyList<CrossBuy.BL.Hr.GratuityProvisionRow> Rows, Dictionary<int, string> Photos);

		private static DateTime AsOfOrMonthEnd(DateTime? asOf) => (asOf ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1).AddDays(-1)).Date;

		[HttpGet]
		public async Task<IActionResult> GratuityProvision(DateTime? asOf, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var pv = await new CrossBuy.BL.Hr.HrGratuityProvisionService(Db).PreviewAsync(gate.CompanyId, AsOfOrMonthEnd(asOf), ct);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			ViewBag.CanPost = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollManage, null);
			return View(pv);
		}

		[HttpGet]
		public async Task<IActionResult> GratuityProvisionData(DateTime? asOf, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return StatusCode(403);
			var pv = await new CrossBuy.BL.Hr.HrGratuityProvisionService(Db).PreviewAsync(gate.CompanyId, AsOfOrMonthEnd(asOf), ct);
			var terms = SearchTerms(q);
			var rows = Page(pv.Rows.Where(r => AllTermsIn(terms, r.Name)).ToList(), ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_GratuityRows", new GratuityRowsModel(rows, photos));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> PostGratuityProvision(DateTime asOf, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage);
			if (!gate.Ok) return HrDenied(nameof(GratuityProvision));
			var journals = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IJournalEntryService)) as CrossBuy.BL.IJournalEntryService;
			if (journals == null) return StatusCode(500);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var (ok, ar, en) = await new CrossBuy.BL.Hr.HrGratuityProvisionService(Db).PostAsync(gate.CompanyId, asOf, ctx?.EmployeeId, journals, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (ok) TempData["HrMsg"] = isAr ? $"رُحّلت تسوية مخصص المكافأة حتى {asOf:yyyy-MM-dd}" : $"Gratuity provision adjustment posted as of {asOf:yyyy-MM-dd}";
			else TempData["HrErr"] = isAr ? ar : en;
			return RedirectToAction(nameof(GratuityProvision), new { asOf = asOf.ToString("yyyy-MM-dd") });
		}
	}
}
