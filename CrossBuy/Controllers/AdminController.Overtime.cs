using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR5 — OVERTIME APPROVAL (prototype #/attendance, 2026-11-08). Attendance-manage decides; the list is a server list.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record OvertimePageModel(DateTime Month, bool RequiresApproval, int YearlyCapMinutes, string? Reference, int Employees,
			int Measured, int Approved, int Undecided, bool CanDecide, bool Posted);
		public sealed record OvertimeRowsModel(IReadOnlyList<CrossBuy.BL.Hr.OvertimeMonthRow> Rows, Dictionary<int, string> Photos, DateTime Month,
			int YearlyCapMinutes, bool CanDecide, bool Posted);

		private static DateTime MonthOf(string? month) =>
			DateTime.TryParse((month ?? "") + "-01", out var m) ? new DateTime(m.Year, m.Month, 1) : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

		[HttpGet]
		public async Task<IActionResult> Overtime(string? month, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var m = MonthOf(month);
			var svc = new CrossBuy.BL.Hr.HrOvertimeService(Db, AttSvc);
			var (req, cap, reference) = await svc.SettingsAsync(gate.CompanyId, ct);
			var rows = await svc.MonthAsync(gate.CompanyId, m.Year, m.Month, ct);
			bool posted = await Db.Payslips.AsNoTracking().AnyAsync(p => p.CompanyID == gate.CompanyId && p.Year == m.Year && p.Month == m.Month, ct);
			return View(new OvertimePageModel(m, req, cap, reference, rows.Count, rows.Sum(r => r.Measured),
				rows.Where(r => r.Decision?.Status == "Approved").Sum(r => r.Decision!.TotalMinutes), rows.Count(r => r.Decision == null && r.Measured > 0), true, posted));
		}

		[HttpGet]
		public async Task<IActionResult> OvertimeData(string? month, string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return StatusCode(403);
			var m = MonthOf(month);
			var svc = new CrossBuy.BL.Hr.HrOvertimeService(Db, AttSvc);
			var (_, cap, _) = await svc.SettingsAsync(gate.CompanyId, ct);
			var terms = SearchTerms(q);
			var all = (await svc.MonthAsync(gate.CompanyId, m.Year, m.Month, ct))
				.Where(r => AllTermsIn(terms, r.Name)
					&& (string.IsNullOrEmpty(status) || (status == "pending" ? r.Decision == null : r.Decision?.Status == status)))
				.OrderBy(r => r.Decision != null).ThenByDescending(r => r.Measured).ToList();
			var pageRows = Page(all, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, pageRows.Select(r => r.EmployeeId).Distinct(), ct);
			bool posted = await Db.Payslips.AsNoTracking().AnyAsync(p => p.CompanyID == gate.CompanyId && p.Year == m.Year && p.Month == m.Month, ct);
			return PartialView("_OvertimeRows", new OvertimeRowsModel(pageRows, photos, m, cap, true, posted));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DecideOvertime(int employeeId, string month, bool approve, string? note, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(Overtime));
			var m = MonthOf(month);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var (ok, ar, en) = await new CrossBuy.BL.Hr.HrOvertimeService(Db, AttSvc).DecideAsync(gate.CompanyId, employeeId, m.Year, m.Month, approve, note, ctx?.EmployeeId, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (!ok) TempData["HrErr"] = isAr ? ar : en;
			else TempData["HrMsg"] = (isAr ? ar : en) ?? (approve ? (isAr ? "اعتُمد العمل الإضافي" : "Overtime approved") : (isAr ? "رُفض العمل الإضافي" : "Overtime rejected"));
			return RedirectToAction(nameof(Overtime), new { month = m.ToString("yyyy-MM") });
		}
	}
}
