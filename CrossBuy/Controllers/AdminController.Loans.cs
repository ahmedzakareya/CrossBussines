using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR4 — LOANS & SALARY ADVANCES (prototype #/loans, 2026-11-08). Payroll-view reads, payroll-manage grants.
	// The list is a server list (search + pagination, owner rule); the legal cap comes from the country rule pack.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record LoanListRow(int Id, int EmployeeId, string? Name, string Kind, decimal Principal, decimal Installment, decimal? PctOfWage,
			decimal Paid, decimal Outstanding, string Status, int FirstYear, int FirstMonth, DateTime DisbursedOn, string? Reason);
		public sealed record LoanPickEmployee(int Id, string? Name, string? Photo, string Initials);
		public sealed record LoanPayFrom(int Id, string Code, string? Name);
		public sealed record LoansPageModel(CrossBuy.BL.Hr.LoanCap Cap, bool CanManage, bool HasLoanAccount, int ActiveCount, decimal OutstandingTotal,
			decimal MonthlyInstalments, IReadOnlyList<LoanPickEmployee> Employees, IReadOnlyList<LoanPayFrom> PayFrom, string Currency);
		public sealed record AdvanceToPay(int RequestId, int EmployeeId, string? Name, decimal Amount, decimal Installment, DateTime? ApprovedAt, string? Reason);
		public sealed record LoanRowsModel(IReadOnlyList<LoanListRow> Rows, Dictionary<int, string> Photos, decimal CapPct);

		private async Task<List<LoanListRow>> LoadLoanRowsAsync(int company, CancellationToken ct)
		{
			var rows = await new CrossBuy.BL.Hr.HrLoanService(Db).ListAsync(company, null, ct);
			var empIds = rows.Select(r => r.Loan.EmployeeID).Distinct().ToList();
			var names = await Db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == company)
				.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn), ct);
			var cap = await new CrossBuy.BL.Hr.HrLoanService(Db).CapAsync(company, ct);
			var pay = await new CrossBuy.BL.Hr.HrSalaryService(Db).ForManyAsync(empIds, DateTime.Today, ct);
			return rows.Select(r =>
			{
				decimal? pct = null;
				if (pay.TryGetValue(r.Loan.EmployeeID, out var p)) { var w = cap.OnBasic ? p.Basic : p.Total; if (w > 0) pct = Math.Round(r.Loan.Installment * 100m / w, 1); }
				return new LoanListRow(r.Loan.ID, r.Loan.EmployeeID, names.TryGetValue(r.Loan.EmployeeID, out var n) ? n : null, r.Loan.Kind, r.Loan.Principal,
					r.Loan.Installment, pct, r.Paid, r.Outstanding, r.Loan.Status, r.Loan.FirstYear, r.Loan.FirstMonth, r.Loan.DisbursedOn, r.Loan.Reason);
			}).ToList();
		}

		[HttpGet]
		public async Task<IActionResult> Loans(CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return HrDenied("Index");
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			bool canManage = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollManage, null);
			var svc = new CrossBuy.BL.Hr.HrLoanService(Db);
			var cap = await svc.CapAsync(gate.CompanyId, ct);
			var all = await svc.ListAsync(gate.CompanyId, null, ct);
			var active = all.Where(r => r.Loan.Status == "Active" && r.Outstanding > 0).ToList();
			var emps = canManage ? await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == gate.CompanyId && e.IsActive).OrderBy(e => e.FullName)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct) : new();
			var payFrom = canManage ? await Db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == gate.CompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).Select(a => new LoanPayFrom(a.ID, a.Code, a.NameEn)).ToListAsync(ct) : new();
			var currency = (await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(gate.CompanyId, ct))?.Rules.Currency ?? "";
			// HR9: self-service advance requests approved by the chain and not yet paid out
			var paidIds = await Db.EmployeeLoans.AsNoTracking().Where(l => l.CompanyID == gate.CompanyId && l.SourceRequestId != null).Select(l => l.SourceRequestId!.Value).ToListAsync(ct);
			ViewBag.AdvancesToPay = await Db.EmployeeRequests.AsNoTracking()
				.Where(r => r.CompanyID == gate.CompanyId && r.RequestType == "Advance" && r.Status == 1 && !paidIds.Contains(r.ID))
				.OrderBy(r => r.DecisionAt)
				.Select(r => new AdvanceToPay(r.ID, r.EmployeeID, Db.Employee.Where(e => e.ID == r.EmployeeID).Select(e => e.FullName).FirstOrDefault(),
					r.AdvanceAmount ?? 0, r.AdvanceInstallment ?? 0, r.DecisionAt, r.Reason))
				.ToListAsync(ct);
			return View(new LoansPageModel(cap, canManage, await svc.LoanAccountAsync(gate.CompanyId, ct) != null, active.Count,
				active.Sum(r => r.Outstanding), active.Sum(r => Math.Min(r.Loan.Installment, r.Outstanding)),
				emps.Select(e => new LoanPickEmployee(e.ID, CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn), CrossBuy.BL.Hr.PersonTile.Photo(e.ProfileImage),
					CrossBuy.BL.Hr.PersonTile.Initials(e.FullName, e.FullNameEn))).ToList(), payFrom, currency));
		}

		[HttpGet]
		public async Task<IActionResult> LoansData(string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return StatusCode(403);
			var all = await LoadLoanRowsAsync(gate.CompanyId, ct);
			var terms = SearchTerms(q);
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var filtered = all.Where(r => (string.IsNullOrEmpty(status) || r.Status == status)
				&& AllTermsIn(terms, r.Name, r.Reason, r.Id.ToString(), r.Kind, r.Kind == "Advance" ? "سلفة" : "قرض")).ToList();
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var pageRows = Page(filtered, ref page, ref pageSize);
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, pageRows.Select(r => r.EmployeeId).Distinct(), ct);
			var cap = await new CrossBuy.BL.Hr.HrLoanService(Db).CapAsync(gate.CompanyId, ct);
			return PartialView("_LoanRows", new LoanRowsModel(pageRows, photos, cap.Pct));
		}

		// HR9: pay out an approved self-service advance; the loan takes the request's amount and instalment, linked back to it.
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> PayAdvanceRequest(int requestId, string firstMonth, int paidFromAccountId, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage);
			if (!gate.Ok) return HrDenied(nameof(Loans));
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var req = await Db.EmployeeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.ID == requestId && r.CompanyID == gate.CompanyId && r.RequestType == "Advance" && r.Status == 1, ct);
			if (req == null || req.AdvanceAmount is not > 0 || req.AdvanceInstallment is not > 0) { TempData["HrErr"] = ar ? "طلب السلفة غير موجود أو غير معتمد" : "The advance request was not found or is not approved"; return RedirectToAction(nameof(Loans)); }
			if (!DateTime.TryParse((firstMonth ?? "") + "-01", out var fm)) { TempData["HrErr"] = ar ? "شهر بدء الخصم غير صحيح" : "The first deduction month is not valid"; return RedirectToAction(nameof(Loans)); }
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var journals = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IJournalEntryService)) as CrossBuy.BL.IJournalEntryService;
			if (journals == null) return StatusCode(500);
			var (ok, errAr, errEn) = await new CrossBuy.BL.Hr.HrLoanService(Db).CreateAsync(gate.CompanyId, req.EmployeeID, "Advance", req.AdvanceAmount.Value, req.AdvanceInstallment.Value,
				fm.Year, fm.Month, DateTime.Today, paidFromAccountId, req.Reason, ctx?.EmployeeId, ctx?.EmployeeId, journals, ct, sourceRequestId: req.ID);
			if (ok) TempData["HrMsg"] = ar ? "صُرفت السلفة ورُحّل قيدها، وسيُخصم القسط من الرواتب" : "The advance was paid and posted; the instalment will be deducted by payroll";
			else TempData["HrErr"] = ar ? errAr : errEn;
			return RedirectToAction(nameof(Loans));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateLoan(int employeeId, string kind, decimal principal, decimal installment, string firstMonth,
			DateTime disbursedOn, int paidFromAccountId, string? reason, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(Loans));
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (!DateTime.TryParse((firstMonth ?? "") + "-01", out var fm)) { TempData["HrErr"] = ar ? "شهر بدء الخصم غير صحيح" : "The first deduction month is not valid"; return RedirectToAction(nameof(Loans)); }
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var journals = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IJournalEntryService)) as CrossBuy.BL.IJournalEntryService;
			if (journals == null) return StatusCode(500);
			var (ok, errAr, errEn) = await new CrossBuy.BL.Hr.HrLoanService(Db).CreateAsync(gate.CompanyId, employeeId, kind, principal, installment,
				fm.Year, fm.Month, disbursedOn, paidFromAccountId, reason, ctx?.EmployeeId, ctx?.EmployeeId, journals, ct);
			if (ok) TempData["HrMsg"] = ar ? "سُجّل القرض ورُحّل قيد صرفه" : "The loan was recorded and its payment posted";
			else TempData["HrErr"] = ar ? errAr : errEn;
			return RedirectToAction(nameof(Loans));
		}
	}
}
