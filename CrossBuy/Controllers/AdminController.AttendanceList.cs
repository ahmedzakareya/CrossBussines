using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// The attendance month summary as a SERVER LIST (owner, 2026-11-08: "no search here — make sure there is
	// pagination"). The house list contract (cbServerList): q / page / pageSize in, the rows as a server-rendered
	// partial out, with X-Total / X-Page / X-Pages headers. Search matches the employee's Arabic and English name.
	// The page's four headline figures stay on the whole month (AdminController.Attendance), never on one page.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record AttendanceRowsModel(IReadOnlyList<CrossBuy.BL.AttendanceMonthRow> Rows, Dictionary<int, string> Photos, Dictionary<int, string> Names);

		[HttpGet]
		public async Task<IActionResult> AttendanceData(int? year, int? month, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return StatusCode(403);
			int company = gate.CompanyId;
			int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;

			var summary = await AttSvc.MonthlySummaryAsync(company, y, m);
			var ids = summary.Select(r => r.EmployeeID).ToList();
			var people = await Db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == company)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var names = people.ToDictionary(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));

			// Search terms (Tagify sends them space- or comma-separated): every term must match the Arabic or English name.
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
			var byId = people.ToDictionary(e => e.ID);
			bool Match(CrossBuy.BL.AttendanceMonthRow r) => terms.Count == 0 || terms.All(t =>
				(byId.TryGetValue(r.EmployeeID, out var p) && (((p.FullName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)) || ((p.FullNameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase))))
				|| (r.EmployeeName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
			var filtered = summary.Where(Match).ToList();

			int total = filtered.Count, pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
			if (page > pages) page = pages;
			var rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeID), ct);

			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			return PartialView("_AttendanceRows", new AttendanceRowsModel(rows, photos, names));
		}
	}
}
