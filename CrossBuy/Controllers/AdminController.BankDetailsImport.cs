using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// BANK DETAILS FROM EXCEL (2026-11-09): civil ID, IBAN and bank for many employees at once. Editing employee data, so
	// every step needs employee-manage. The upload only REVIEWS (nothing written, nothing kept: the workbook is read in
	// memory and discarded, so it is not an attachment); the confirm re-checks every change on the server and writes.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record BankDetailsPageModel(IReadOnlyList<CrossBuy.BL.Hr.BankDetailsRow>? Review, string? FileName, int Missing, int Active);

		private async Task<(int Missing, int Active)> BankDetailsCoverageAsync(int companyId, CancellationToken ct)
		{
			var q = Db.Employee.Where(e => e.EmpCompanyID == companyId && e.IsActive);
			int active = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(q, ct);
			int missing = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(q.Where(e => e.Iban == null || e.Iban == "" || e.CivilIdNumber == null || e.CivilIdNumber == ""), ct);
			return (missing, active);
		}

		[HttpGet]
		public async Task<IActionResult> BankDetails(CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var (missing, active) = await BankDetailsCoverageAsync(gate.CompanyId, ct);
			return View(new BankDetailsPageModel(null, null, missing, active));
		}

		[HttpGet]
		public async Task<IActionResult> BankDetailsTemplate(CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(BankDetails));
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var bytes = await new CrossBuy.BL.Hr.HrBankDetailsImport(Db).TemplateAsync(gate.CompanyId, isAr, ct);
			return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"bank-details-{DateTime.Today:yyyyMMdd}.xlsx");
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[RequestSizeLimit(5_000_000)]
		public async Task<IActionResult> BankDetailsUpload(IFormFile? file, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(BankDetails));
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (file is not { Length: > 0 } || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
			{
				TempData["HrErr"] = isAr ? "اختر ملف Excel بصيغة ‎.xlsx" : "Choose an Excel file (.xlsx)";
				return RedirectToAction(nameof(BankDetails));
			}
			await using var s = file.OpenReadStream();
			using var ms = new MemoryStream();
			await s.CopyToAsync(ms, ct); ms.Position = 0;
			var rows = CrossBuy.BL.Hr.HrBankDetailsImport.Parse(ms);
			if (rows == null)
			{
				TempData["HrErr"] = isAr ? "تعذّرت قراءة الملف؛ استخدم النموذج المنزَّل من هذه الشاشة" : "The file could not be read; use the template downloaded from this screen";
				return RedirectToAction(nameof(BankDetails));
			}
			var review = await new CrossBuy.BL.Hr.HrBankDetailsImport(Db).ReviewAsync(gate.CompanyId, rows, ct);
			var (missing, active) = await BankDetailsCoverageAsync(gate.CompanyId, ct);
			return View("BankDetails", new BankDetailsPageModel(review, file.FileName, missing, active));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> BankDetailsApply(int[]? empId, string[]? civil, string[]? iban, string[]? bank, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(BankDetails));
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var changes = new List<CrossBuy.BL.Hr.BankDetailsChange>();
			for (int i = 0; i < (empId?.Length ?? 0); i++)
				changes.Add(new CrossBuy.BL.Hr.BankDetailsChange(empId![i], civil?.ElementAtOrDefault(i), iban?.ElementAtOrDefault(i), bank?.ElementAtOrDefault(i)));
			if (changes.Count == 0) { TempData["HrErr"] = isAr ? "لا تغييرات لحفظها" : "No changes to save"; return RedirectToAction(nameof(BankDetails)); }
			var (applied, refused) = await new CrossBuy.BL.Hr.HrBankDetailsImport(Db).ApplyAsync(gate.CompanyId, changes, gate.EmployeeId, ct);
			TempData[applied > 0 ? "HrMsg" : "HrErr"] = isAr
				? $"حُفظت بيانات {applied} موظف" + (refused > 0 ? $"، ورُفض {refused} لم يعد صالحاً" : "")
				: $"Saved the details of {applied} employee(s)" + (refused > 0 ? $"; {refused} refused as no longer valid" : "");
			return RedirectToAction(nameof(BankDetails));
		}
	}
}
