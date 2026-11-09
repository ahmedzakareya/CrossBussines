using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// End-of-service clearance (2026-11-09): one follow-up task per checklist item, created once (employee-manage).
	public partial class AdminController
	{
		/// Arabic agreement for a count of tasks: مهمة واحدة · مهمتان · ٣–١٠ مهام · ١١+ مهمة.
		private static string TasksAr(int n) => n switch { 1 => "مهمة واحدة", 2 => "مهمتان", >= 3 and <= 10 => $"{n} مهام", _ => $"{n} مهمة" };

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> StartClearance(int employeeId, DateTime? terminationDate, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(FinalSettlement), new { employeeId });
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var created = await new CrossBuy.BL.Hr.HrClearanceService(Db).StartAsync(gate.CompanyId, employeeId, ctx?.EmployeeId, ct);
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			TempData["HrMsg"] = created > 0
				? (ar ? $"أُنشئت {TasksAr(created)} لإخلاء الطرف على ملف الموظف" : $"{created} clearance task(s) created on the employee's record")
				: (ar ? "مهام إخلاء الطرف موجودة بالفعل" : "The clearance tasks already exist");
			return RedirectToAction(nameof(FinalSettlement), new { employeeId, terminationDate = terminationDate?.ToString("yyyy-MM-dd") });
		}
	}
}
