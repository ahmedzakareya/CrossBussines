using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HR5 — import a fingerprint-device export (attendance-manage). The file is read and discarded: it is a data feed whose
	// every line becomes an attendance record, not a document kept about anyone.
	public partial class AdminController
	{
		[HttpGet]
		public async Task<IActionResult> AttendanceImport()
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(Attendance));
			ViewBag.Result = TempData["AttImport"] is string json ? System.Text.Json.JsonSerializer.Deserialize<CrossBuy.BL.Hr.AttendanceImportResult>(json) : null;
			ViewBag.Mapped = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
				Db.Employee, e => e.EmpCompanyID == gate.CompanyId && e.IsActive && e.AttendanceDeviceId != null);
			return View();
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[RequestSizeLimit(20 * 1024 * 1024)]
		public async Task<IActionResult> AttendanceImport(IFormFile? file, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(Attendance));
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (file == null || file.Length == 0) { TempData["HrErr"] = ar ? "اختر ملف التصدير من جهاز البصمة" : "Choose the export file from the device"; return RedirectToAction(nameof(AttendanceImport)); }
			var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
			if (ext is not (".csv" or ".txt" or ".dat" or ".log")) { TempData["HrErr"] = ar ? "الملف يجب أن يكون نصياً (CSV أو TXT أو DAT)" : "The file must be text (CSV, TXT or DAT)"; return RedirectToAction(nameof(AttendanceImport)); }
			await using var body = file.OpenReadStream();
			var result = await new CrossBuy.BL.Hr.HrAttendanceImport(Db, AttSvc).ImportAsync(gate.CompanyId, body, User?.Identity?.Name, ct);
			TempData["AttImport"] = System.Text.Json.JsonSerializer.Serialize(result);
			return RedirectToAction(nameof(AttendanceImport));
		}
	}
}
