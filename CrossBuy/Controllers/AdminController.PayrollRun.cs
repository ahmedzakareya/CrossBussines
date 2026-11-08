using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR6 — THE PAYROLL RUN, HR's SCREEN (prototype #/payroll, 2026-11-08). Payroll-view reads; payroll-manage posts.
	// It reads the SAME preview the accounting payroll entry posts (IAccountingPostingService.PreviewPayrollAsync), so the
	// register here and the journal there are one computation; posting goes through AccountingController.PostPayroll,
	// which keeps the accounting right in front of the ledger.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record PayrollStage(string Ar, string En, bool Done, string? NoteAr = null, string? NoteEn = null);
		public sealed record PayrollRunPageModel(DateTime Month, CrossBuy.BL.PayrollRunPreview Preview, IReadOnlyList<PayrollStage> Stages,
			bool CanPost, string Currency, string? PackNameAr, string? PackNameEn, bool OvertimeNeedsApproval, int OvertimeUndecided);
		public sealed record PayrollRowsModel(IReadOnlyList<CrossBuy.BL.PayrollEmployeeLine> Rows, Dictionary<int, string> Photos);

		private CrossBuy.BL.IAccountingPostingService Posting => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IAccountingPostingService)) as CrossBuy.BL.IAccountingPostingService)!;

		[HttpGet]
		public async Task<IActionResult> PayrollRun(string? month, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var m = MonthOf(month);
			var preview = await Posting.PreviewPayrollAsync(gate.CompanyId, m.Year, m.Month);
			var disb = await Posting.GetDisbursementViewAsync(gate.CompanyId, m.Year, m.Month);
			var ot = new CrossBuy.BL.Hr.HrOvertimeService(Db, AttSvc);
			var (otRequired, _, _) = await ot.SettingsAsync(gate.CompanyId, ct);
			int undecided = otRequired ? (await ot.MonthAsync(gate.CompanyId, m.Year, m.Month, ct)).Count(r => r.Decision == null && r.Measured > 0) : 0;
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var stages = new List<PayrollStage>
			{
				new("الحضور والإضافي", "Attendance & overtime", !otRequired || undecided == 0,
					otRequired ? (undecided == 0 ? "الإضافي معتمد" : $"{undecided} بانتظار اعتماد الإضافي") : "الإضافي يُصرف كما سُجّل",
					otRequired ? (undecided == 0 ? "Overtime approved" : $"{undecided} awaiting overtime approval") : "Overtime paid as recorded"),
				new("حساب المسير", "Calculate", preview.Employees.Count > 0),
				new("ترحيل القيد", "Post the journal", preview.AlreadyPosted, preview.PostedEntryNo, preview.PostedEntryNo),
				new("صرف الصافي", "Pay the net", disb.Disbursed),
				new("توريد التأمينات والضريبة", "Remit insurance & tax", disb.SiRemitted && (disb.Tax <= 0 || disb.TaxRemitted)),
			};
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			bool canPost = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollManage, null);
			var pack = await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(gate.CompanyId, ct);
			return View(new PayrollRunPageModel(m, preview, stages, canPost, pack?.Rules.Currency ?? "", pack?.Pack.NameAr, pack?.Pack.NameEn, otRequired, undecided));
		}

		[HttpGet]
		public async Task<IActionResult> PayrollRunData(string? month, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return StatusCode(403);
			var m = MonthOf(month);
			var preview = await Posting.PreviewPayrollAsync(gate.CompanyId, m.Year, m.Month);
			var terms = SearchTerms(q);
			var all = preview.Employees.Where(e => AllTermsIn(terms, e.Name, e.NameEn, e.CostCenterName)).OrderBy(e => e.Name).ToList();
			var rows = Page(all, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_PayrollRows", new PayrollRowsModel(rows, photos));
		}
	}
}
