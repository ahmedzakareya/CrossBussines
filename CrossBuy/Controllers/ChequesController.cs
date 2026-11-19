using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	/// <summary>
	/// Cheques under collection: receive a customer's cheque (a receipt voucher on 1106), clear it to the bank, or record
	/// it as bounced. Guard: accounting "pay", the permission of the receipt voucher itself; receiving at or above the
	/// company's approval threshold needs the chief accountant, exactly as AccountingController.CreateReceipt does.
	/// </summary>
	[SessionValidation]
	public class ChequesController : Controller
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _ar;
		private readonly IJournalEntryService _journals;
		private readonly ICurrencyService _currency;
		private readonly IAccountingAccessService _access;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private int? _companyId;
		private int co => _companyId ?? 0;
		private static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		private static string L(string ar, string en) => IsAr ? ar : en;

		public ChequesController(CrossDbContext db, IReceivableService ar, IJournalEntryService journals, ICurrencyService currency,
			IAccountingAccessService access, CrossBuy.BL.Platform.IRequestCompanyResolver company)
		{ _db = db; _ar = ar; _journals = journals; _currency = currency; _access = access; _company = company; }

		public override async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
		{
			if (!_companyId.HasValue) { var scope = await _company.ResolveAsync(); _companyId = scope.Ok ? scope.CompanyId : 0; }
			await next();
		}

		private ChequeService Svc => new(_db, _ar, _journals, _currency);

		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		[HttpGet][AccPerm("pay")]
		public async Task<IActionResult> Index()
		{
			ViewBag.Customers = (await _db.Customers.AsNoTracking().Where(c => c.CompanyID == co && c.IsActive).OrderBy(c => c.Name)
				.Select(c => new { c.ID, c.Name, c.NameEn }).ToListAsync()).Select(c => (c.ID, IsAr || string.IsNullOrWhiteSpace(c.NameEn) ? c.Name : c.NameEn!)).ToList();
			ViewBag.Banks = (await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == co && a.IsPostable && a.IsActive && a.Code.StartsWith("1101")).OrderBy(a => a.Code)
				.Select(a => new { a.ID, a.Code, a.Name, a.NameEn }).ToListAsync()).Select(a => (a.ID, a.Code + " · " + (IsAr || string.IsNullOrWhiteSpace(a.NameEn) ? a.Name : a.NameEn))).ToList();
			ViewBag.HasHolding = await Svc.HoldingAccountIdAsync(co) != null;
			ViewBag.Currency = await _db.Currencies.AsNoTracking().Where(c => c.ID == _db.Companies.Where(x => x.CompanyID == co).Select(x => x.DefaultCurrencyId).FirstOrDefault())
				.Select(c => c.Code).FirstOrDefaultAsync() ?? "";
			return View();
		}

		[HttpGet][AccPerm("pay")]
		public async Task<IActionResult> Data(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var (rows, total, inHand) = await Svc.SearchAsync(co, q, status, page, pageSize, IsAr, HttpContext.RequestAborted);
			SetPaging(total, page, pageSize);
			Response.Headers["X-GrandValue"] = inHand.ToString(System.Globalization.CultureInfo.InvariantCulture);   // the list helper shows it in #grandValue
			return PartialView("_ChequeRows", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("pay")]
		public async Task<IActionResult> Receive(int customerId, DateTime receiptDate, decimal amount, string chequeNo, DateTime dueDate, string? bank, string? notes)
		{
			var th = await _db.AccountingSettings.AsNoTracking().Where(s => s.CompanyID == co).Select(s => (decimal?)s.ApprovalThreshold).FirstOrDefaultAsync() ?? 0m;
			if (th > 0 && amount >= th && !(await _access.MyRolesAsync()).Contains("ChiefAccountant"))
			{ TempData["AccErr"] = L($"المبلغ ({amount:N2}) يتجاوز حد الاعتماد — يلزم رئيس الحسابات", $"The amount ({amount:N2}) exceeds the approval threshold — the chief accountant is required"); return RedirectToAction(nameof(Index)); }
			var (ok, err, rc) = await Svc.ReceiveAsync(co, customerId, receiptDate, amount, chequeNo, dueDate, bank, notes, null, User?.Identity?.Name, HttpContext.RequestAborted);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L($"سُجّل الشيك وصدر سند القبض {rc!.ReceiptNo}", $"Cheque recorded; receipt {rc!.ReceiptNo} posted") : Translate(err);
			return RedirectToAction(nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("pay")]
		public async Task<IActionResult> Clear(int id, int bankAccountId, DateTime date)
		{
			var (ok, err) = await Svc.ClearAsync(co, id, bankAccountId, date, null, HttpContext.RequestAborted);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L("حُصّل الشيك وأُودع في الحساب", "Cheque cleared into the account") : Translate(err);
			return RedirectToAction(nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("pay")]
		public async Task<IActionResult> Bounce(int id, string reason)
		{
			var (ok, err) = await Svc.BounceAsync(co, id, reason, null, HttpContext.RequestAborted);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L("سُجّل الشيك مرتداً وعاد المبلغ على العميل", "Cheque recorded as bounced; the amount is owed by the customer again") : Translate(err);
			return RedirectToAction(nameof(Index));
		}

		private static string Translate(string? err) => err switch
		{
			"Enter the cheque number" => L("أدخل رقم الشيك", err),
			"The amount must be greater than zero" => L("المبلغ يجب أن يكون أكبر من صفر", err),
			"The cheques-under-collection account (1106) is missing from the chart of accounts" => L("حساب الشيكات تحت التحصيل (1106) غير موجود في دليل الحسابات", err),
			"This cheque is already recorded" => L("هذا الشيك مسجّل من قبل", err),
			"Cheque not found" => L("الشيك غير موجود", err),
			"Only a cheque in hand can be cleared" => L("يُحصَّل الشيك الموجود في الخزينة فقط", err),
			"The cheque's receipt voucher is not posted" => L("سند القبض الخاص بالشيك غير مرحّل", err),
			"Choose a cash or bank account" => L("اختر حساب نقدية أو بنك", err),
			"Say why the cheque bounced" => L("اذكر سبب ارتداد الشيك", err),
			"The cheque has already bounced" => L("الشيك مرتد من قبل", err),
			"Customer not found" => L("العميل غير موجود", err),
			_ => err ?? L("تعذّر الحفظ", "Could not save"),
		};
	}
}
