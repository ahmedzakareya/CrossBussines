using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR lists as SERVER LISTS (owner, 2026-11-08: every list has search and pagination). The house contract
	// (cbServerList): q / page / pageSize (+ filters) in, a server-rendered row partial out, with X-Total / X-Page /
	// X-Pages headers. Every term must match (AND); matching is case-insensitive on Arabic and English fields.
	// Each list keeps its own gate and reads only the caller's company — the same reads as the page it serves.
	// ==========================================================================================
	public partial class AdminController
	{
		private static List<string> SearchTerms(string? q) =>
			(q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

		private static bool AllTermsIn(IReadOnlyList<string> terms, params string?[] fields) =>
			terms.Count == 0 || terms.All(t => fields.Any(f => !string.IsNullOrEmpty(f) && f.Contains(t, StringComparison.OrdinalIgnoreCase)));

		private List<T> Page<T>(IReadOnlyList<T> all, ref int page, ref int pageSize)
		{
			if (pageSize is < 1 or > 200) pageSize = 25;
			int pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize));
			if (page < 1) page = 1;
			if (page > pages) page = pages;
			Response.Headers["X-Total"] = all.Count.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			return all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
		}

		// ---- Employee directory ----
		public sealed record EmployeeRowsModel(IReadOnlyList<CrossBuy.ViewModel.EmployeeListItemDto> Rows);

		[HttpGet]
		public async Task<IActionResult> EmployeesData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return StatusCode(403);
			var svc = HttpContext.RequestServices.GetService(typeof(BL.IEmployeeService)) as BL.IEmployeeService;
			var all = svc != null ? await svc.GetAllAsync() : new List<ViewModel.EmployeeListItemDto>();
			var terms = SearchTerms(q);
			var filtered = all.Where(e => (status != "1" || e.IsActive) && (status != "0" || !e.IsActive)
				&& AllTermsIn(terms, e.FullName, e.FullNameEn, e.JobTitleAr, e.JobTitleEn, e.BranchAr, e.BranchEn, e.Email, e.PhoneNumber, e.CountryAr, e.CountryEn)).ToList();
			return PartialView("_EmployeeRows", new EmployeeRowsModel(Page(filtered, ref page, ref pageSize)));
		}

		// ---- Leave requests ----
		public sealed record LeaveRowsModel(IReadOnlyList<LeaveRow> Rows, Dictionary<int, string> Photos);

		[HttpGet]
		public async Task<IActionResult> LeaveData(string? q, int? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return StatusCode(403);
			var today = DateTime.Today;
			var m = new DateTime(today.Year, today.Month, 1);
			var all = await LoadLeaveRowsAsync(gate.CompanyId, m, m.AddMonths(1).AddDays(-1), ct);
			var terms = SearchTerms(q);
			var filtered = all.Where(r => (status == null || r.Status == status)
				&& AllTermsIn(terms, r.Name, r.TypeAr, r.TypeEn, r.WaitingOnAr, r.WaitingOnEn, r.DecidedByAr, r.DecidedByEn)).ToList();
			var rows = Page(filtered, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_LeaveRows", new LeaveRowsModel(rows, photos));
		}

		// ---- Documents & expiry ----
		public sealed record ExpiryRowsModel(IReadOnlyList<CrossBuy.BL.ExpiryItem> Rows, Dictionary<int, string> Photos);

		[HttpGet]
		public async Task<IActionResult> ExpiryData(string? q, int? days, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return StatusCode(403);
			var all = await DocSvc.ExpiringAsync(gate.CompanyId, days ?? 60);
			var terms = SearchTerms(q);
			var filtered = all.Where(i => AllTermsIn(terms, i.EmployeeName, i.Label, ExpiryLabelAr(i.Label), i.Kind)).ToList();
			var rows = Page(filtered, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_ExpiryRows", new ExpiryRowsModel(rows, photos));
		}

		// ---- Final settlements (history) ----
		public sealed record SettlementRowsModel(IReadOnlyList<CrossBuy.Models.Context.Admin.FinalSettlement> Rows, Dictionary<int, string> Photos);

		[HttpGet]
		public async Task<IActionResult> SettlementsData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);   // settlement money is payroll data
			if (!gate.Ok) return StatusCode(403);
			var all = await Db.FinalSettlements.AsNoTracking().Where(s => s.CompanyID == gate.CompanyId).OrderByDescending(s => s.TerminationDate).ThenByDescending(s => s.ID).ToListAsync(ct);
			var ids = all.Select(s => s.EmployeeID).Distinct().ToList();
			var people = await Db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == gate.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			var terms = SearchTerms(q);
			var filtered = all.Where(s => AllTermsIn(terms, s.EmployeeName, s.Reason,
				people.TryGetValue(s.EmployeeID, out var p1) ? p1.FullName : null, people.TryGetValue(s.EmployeeID, out var p2) ? p2.FullNameEn : null)).ToList();
			var rows = Page(filtered, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeID).Distinct(), ct);
			return PartialView("_SettlementRows", new SettlementRowsModel(rows, photos));
		}

		// HR2: run the expiry follow-up now (it also runs daily in DocumentExpiryHostedService). Creates one task per
		// expiring document/contract, due on its expiry date; never twice for the same instrument and date.
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateExpiryTasks(int? days, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(DocExpiryAlerts));
			var run = await new CrossBuy.BL.Hr.HrExpiryFollowUp(Db, DocSvc).RunAsync(gate.CompanyId, ct);
			TempData["HrMsg"] = string.Format(L["Follow-up tasks created: {0} (already tracked: {1})"].Value, run.TasksCreated, run.AlreadyTracked);
			return RedirectToAction(nameof(DocExpiryAlerts), new { days });
		}

		// The Arabic name of a document/contract code, so an Arabic search term finds it ("جواز" finds Passport).
		private static string? ExpiryLabelAr(string? code) => code switch
		{
			"NationalId" => "البطاقة المدنية", "Passport" => "جواز السفر", "Qualification" => "المؤهل", "Contract" => "عقد",
			"Insurance" => "التأمين", "WorkPermit" => "إذن العمل", "Other" => "أخرى",
			"Permanent" => "عقد دائم", "FixedTerm" => "عقد محدد المدة", "Probation" => "عقد تجربة", "PartTime" => "عقد دوام جزئي",
			_ => null,
		};
	}
}
