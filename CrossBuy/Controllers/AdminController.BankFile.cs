using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR8 — EXPORT FILES WHOSE SHAPE IS A SETTING (2026-11-09), one screen and one engine for two kinds:
	//   kind "bank"      — the salary transfer file for the bank (wage protection), from the month's POSTED payslips;
	//   kind "insurance" — the monthly social-insurance contributions file (Kuwait: PIFSS), from HrInsuranceStatement,
	//                      the computation the statement screen and payroll use.
	// Seeing the lines needs payroll-view; changing a format and downloading a file need payroll-manage. The lines are a
	// server list (search + pagination).
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record BankFilePageModel(string Kind, DateTime Month, CrossBuy.Models.Context.Admin.HrBankFileFormat Format, bool Saved,
			IReadOnlyList<CrossBuy.BL.Hr.HrBankFileColumn> Columns, int Lines, decimal Total, int Problems, bool HasSource, string Currency,
			string FileName, bool CanManage, IReadOnlyList<CrossBuy.BL.Hr.HrBankFileLine> ProblemLines)
		{
			public bool IsInsurance => Kind == CrossBuy.BL.Hr.HrBankFileFields.Insurance;
			public string RouteKind => IsInsurance ? "insurance" : "bank";
		}
		public sealed record BankFileRowsModel(string Kind, IReadOnlyList<CrossBuy.BL.Hr.HrBankFileLine> Rows, Dictionary<int, string> Photos);

		private async Task<string> PackCurrencyAsync(int companyId, CancellationToken ct) =>
			(await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(companyId, ct))?.Rules.Currency ?? "KWD";

		private async Task<List<CrossBuy.BL.Hr.HrBankFileLine>> ExportLinesAsync(string kind, int companyId, DateTime m, CancellationToken ct) =>
			kind == CrossBuy.BL.Hr.HrBankFileFields.Insurance
				? await CrossBuy.BL.Hr.HrInsuranceStatement.FileLinesAsync(Db, companyId, m, ct)
				: await new CrossBuy.BL.Hr.HrBankFileService(Db).LinesAsync(companyId, m.Year, m.Month, ct);

		/// The menu's entry for the social-insurance file: the same screen, kind "insurance".
		[HttpGet]
		public Task<IActionResult> InsuranceFile(string? month, CancellationToken ct = default) => BankFile(month, "insurance", ct);

		[HttpGet]
		public async Task<IActionResult> BankFile(string? month, string? kind = null, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var k = CrossBuy.BL.Hr.HrBankFileFields.Norm(kind);
			var m = month == null && k == CrossBuy.BL.Hr.HrBankFileFields.Bank ? LatestPostedMonth(gate.CompanyId) ?? MonthOf(null) : MonthOf(month);
			var svc = new CrossBuy.BL.Hr.HrBankFileService(Db);
			var (fmt, saved) = await svc.FormatAsync(gate.CompanyId, k, ct);
			var lines = await ExportLinesAsync(k, gate.CompanyId, m, ct);
			var good = lines.Where(l => l.Problem == null).ToList();
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			bool canManage = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollManage, null);
			return View("BankFile", new BankFilePageModel(k, m, fmt, saved, CrossBuy.BL.Hr.HrBankFileService.Columns(fmt), good.Count, good.Sum(l => l.Net),
				lines.Count - good.Count, lines.Count > 0, await PackCurrencyAsync(gate.CompanyId, ct),
				CrossBuy.BL.Hr.HrBankFileService.FileName(fmt, m.Year, m.Month), canManage, lines.Where(l => l.Problem != null).ToList()));
		}

		private DateTime? LatestPostedMonth(int companyId)
		{
			var p = Db.Payslips.AsNoTracking().Where(x => x.CompanyID == companyId).OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
				.Select(x => new { x.Year, x.Month }).FirstOrDefault();
			return p == null ? null : new DateTime(p.Year, p.Month, 1);
		}

		[HttpGet]
		public async Task<IActionResult> BankFileData(string? month, string? kind, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return StatusCode(403);
			var k = CrossBuy.BL.Hr.HrBankFileFields.Norm(kind);
			var terms = SearchTerms(q);
			var all = (await ExportLinesAsync(k, gate.CompanyId, MonthOf(month), ct))
				.Where(l => AllTermsIn(terms, l.NameAr, l.NameEn, l.CivilId, l.Iban, l.BankName)).ToList();
			var rows = Page(all, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_BankFileRows", new BankFileRowsModel(k, rows, photos));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBankFileFormat(string month, string? kind, string? bankName, string delimiter, bool withBom, bool headerRow, string dateFormat,
			int decimalPlaces, string trailerRow, string fileNamePattern, string? companyAccount, string? employerCode, int paymentDay,
			string[]? colField, string[]? colHeader, string[]? colValue, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage);
			if (!gate.Ok) return HrDenied(nameof(BankFile));
			var k = CrossBuy.BL.Hr.HrBankFileFields.Norm(kind);
			var cols = new List<CrossBuy.BL.Hr.HrBankFileColumn>();
			for (int i = 0; i < (colField?.Length ?? 0); i++)
				cols.Add(new CrossBuy.BL.Hr.HrBankFileColumn { Field = colField![i], Header = colHeader?.ElementAtOrDefault(i)?.Trim() ?? "", Value = colValue?.ElementAtOrDefault(i) });
			var (ok, ar, en) = await new CrossBuy.BL.Hr.HrBankFileService(Db).SaveAsync(gate.CompanyId, new CrossBuy.Models.Context.Admin.HrBankFileFormat
			{
				Kind = k, BankName = bankName, Delimiter = delimiter == "tab" ? "\t" : delimiter, WithBom = withBom, HeaderRow = headerRow, DateFormat = dateFormat ?? "dd/MM/yyyy",
				DecimalPlaces = decimalPlaces, TrailerRow = trailerRow, FileNamePattern = fileNamePattern ?? "", CompanyAccount = companyAccount,
				EmployerCode = employerCode, PaymentDay = paymentDay,
			}, cols, gate.EmployeeId, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? (isAr ? "حُفظ شكل الملف" : "File format saved") : (isAr ? ar : en);
			return RedirectToAction(nameof(BankFile), new { month, kind = k == CrossBuy.BL.Hr.HrBankFileFields.Insurance ? "insurance" : null });
		}

		[HttpGet]
		public async Task<IActionResult> DownloadBankFile(string month, string? kind, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage);
			if (!gate.Ok) return HrDenied(nameof(BankFile));
			var k = CrossBuy.BL.Hr.HrBankFileFields.Norm(kind);
			var m = MonthOf(month);
			var (fmt, _) = await new CrossBuy.BL.Hr.HrBankFileService(Db).FormatAsync(gate.CompanyId, k, ct);
			var lines = await ExportLinesAsync(k, gate.CompanyId, m, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (lines.Count == 0)
			{
				TempData["HrErr"] = isAr ? "لا بيانات لهذا الشهر" : "Nothing for this month";
				return RedirectToAction(nameof(BankFile), new { month, kind = k == CrossBuy.BL.Hr.HrBankFileFields.Insurance ? "insurance" : null });
			}
			var file = CrossBuy.BL.Hr.HrBankFileService.Build(fmt, lines, m.Year, m.Month, await PackCurrencyAsync(gate.CompanyId, ct));
			var type = file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "text/csv";
			return File(file.Content, type + "; charset=utf-8", file.FileName);
		}
	}
}
