using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.ViewModel.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	/// Pay source (bank / cash box) option for the disbursement dropdowns.
	/// PUBLIC (not anonymous) so the runtime-compiled Razor view can bind it via dynamic
	/// (anonymous types are internal → RuntimeBinderException across the view assembly).
	public class PaySourceOption { public int GlAccountId { get; set; } public string Name { get; set; } = ""; }
	// public DTO for AccountingRoles ViewBag (avoid anonymous-type dynamic binding across the Views assembly)
	public class AccRoleAssignmentRow { public int ID { get; set; } public int EmployeeId { get; set; } public string? EmployeeName { get; set; } public string Role { get; set; } = ""; }

	/// النظام المحاسبي — Accounting system.
	/// Same Metronic shell/header as the Admin back-office (cloned layout _LayoutAccounting).
	[SessionValidation]
	public class AccountingController : Controller
	{
		private readonly IChartOfAccountsService _coa;
		private readonly IJournalEntryService _journals;
		private readonly IGeneralLedgerService _gl;
		private readonly ICostCenterService _costCenters;
		private readonly IFiscalPeriodService _periods;
		private readonly IAccountingPostingService _posting;
		private readonly IReceivableService _ar;
		private readonly IPayableService _ap;
		private readonly IAccountingDashboardService _dashboard;
		private readonly IBankService _banks;
		private readonly IFixedAssetService _assets;
		private readonly ITaxService _tax;
		private readonly IEtaInvoiceService _eta;
		private readonly IFinancialStatementService _statements;
		private readonly IClosingService _closing;
		private readonly CrossDbContext _context;
		private readonly IAccountingAccessService _access;
		private readonly IAiInsightsService _insights;
		private readonly IExecutiveDashboardService _executive;
		private readonly ICurrencyService _currency;
		private readonly IPricingService _pricing;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public AccountingController(IChartOfAccountsService coa, IJournalEntryService journals,
			IGeneralLedgerService gl, ICostCenterService costCenters, IFiscalPeriodService periods,
			IAccountingPostingService posting, IReceivableService ar, IPayableService ap,
			IAccountingDashboardService dashboard, IBankService banks, IFixedAssetService assets,
			ITaxService tax, IEtaInvoiceService eta, IFinancialStatementService statements, IClosingService closing, CrossDbContext context, IAccountingAccessService access, IAiInsightsService insights, IExecutiveDashboardService executive, ICurrencyService currency, IPricingService pricing, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{
			_coa = coa; _journals = journals; _gl = gl; _costCenters = costCenters; _periods = periods; _posting = posting; _ar = ar; _ap = ap; _dashboard = dashboard; _banks = banks; _assets = assets; _tax = tax; _eta = eta; _statements = statements; _closing = closing; _context = context; _access = access; _insights = insights; _executive = executive; _currency = currency; _pricing = pricing; L = localizer;
		}

		// Multi-Currency helpers shared by the create-document screens
		private async Task<List<Models.Context.Accounting.Currency>> CurrencyListAsync() => await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
		private Task<int> FunctionalCurrencyIdAsync() => _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
		private CrossBuy.BL.IProjectService PrjSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IProjectService)) as CrossBuy.BL.IProjectService)!;

		// رؤى ذكية — AI insights (anomaly + cash-flow + inventory), all from the local
		// ML service (no LLM / no API key). Server-rendered; the AI only surfaces findings.
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> AiInsights()
		{
			var vm = new AiInsightsVm();
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			try
			{
				var a = await _insights.ScanJournalAnomaliesAsync(DefaultCompanyId);
				var c = await _insights.ForecastCashflowAsync(DefaultCompanyId, 90);
				var i = await _insights.AnalyzeInventoryAsync(DefaultCompanyId, 90);
				if (a.Status == 200) vm.Anomaly = JsonSerializer.Deserialize<AnomalyResult>(a.Json, opts);
				if (c.Status == 200) vm.Cashflow = JsonSerializer.Deserialize<CashflowResult>(c.Json, opts);
				if (i.Status == 200) vm.Inventory = JsonSerializer.Deserialize<InventoryResult>(i.Json, opts);
				if (vm.Anomaly == null && vm.Cashflow == null && vm.Inventory == null)
				{
					vm.ServiceDown = true;
					vm.ServiceMessage = a.Json;
				}
			}
			catch (Exception ex)
			{
				vm.ServiceDown = true;
				vm.ServiceMessage = ex.Message;
			}
			return View(vm);
		}

		// ===== segregation of duties helpers (1c) =====
		private async Task<decimal> ApprovalThresholdAsync()
			=> (await _context.AccountingSettings.AsNoTracking().Where(s => s.CompanyID == DefaultCompanyId).Select(s => (decimal?)s.ApprovalThreshold).FirstOrDefaultAsync()) ?? 0m;
		private async Task<bool> IsChiefAsync()
			=> (await _access.MyRolesAsync()).Contains("ChiefAccountant");

		private async Task<List<AccountNode>> CashAccountsAsync() =>
			(await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.Code.StartsWith("1101")).ToList();

		// for Phase 0/1 we operate on a single company; multi-company selector comes later
		private const int DefaultCompanyId = 1;

		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> Index() => View(await _dashboard.BuildAsync(DefaultCompanyId));

		// Cross-module executive command center (KPIs + ratios + trends from every module)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> Executive() => View(await _executive.BuildAsync(DefaultCompanyId));

		// شجرة الحسابات — Chart of Accounts tree
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> ChartOfAccounts()
		{
			ViewBag.AccountTypes = await _coa.GetAccountTypesAsync();
			ViewBag.AllAccounts = await _coa.GetFlatAsync(DefaultCompanyId);
			var tree = await _coa.GetTreeAsync(DefaultCompanyId);
			return View(tree);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CreateAccount(string code, string name, string? nameEn, int accountTypeId, int? parentId, bool isPostable, bool requireCostCenter, string? cashFlowCategory)
		{
			var (ok, err, _) = await _coa.CreateAsync(DefaultCompanyId, code, name, nameEn ?? "", accountTypeId, parentId, isPostable, requireCostCenter, cashFlowCategory, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Account added"].Value : err;
			return RedirectToAction(nameof(ChartOfAccounts));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> EditAccount(int id, string code, string name, string? nameEn, bool isPostable, bool isActive, bool requireCostCenter, string? cashFlowCategory)
		{
			var (ok, err) = await _coa.UpdateAsync(DefaultCompanyId, id, code, name, nameEn ?? "", isPostable, isActive, requireCostCenter, cashFlowCategory, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Account updated"].Value : err;
			return RedirectToAction(nameof(ChartOfAccounts));
		}

		// تفاصيل قيد — Journal entry detail (header + lines)
		[SessionValidation][HttpGet]
		public async Task<IActionResult> JournalEntry(int id)
		{
			var e = await _context.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (e == null) return RedirectToAction(nameof(Journals));
			var accIds = e.Lines.Select(l => l.AccountId).Distinct().ToList();
			ViewBag.Accounts = await _context.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => new[] { a.Code, a.Name, a.NameEn });
			var ccIds = e.Lines.Where(l => l.CostCenterId.HasValue).Select(l => l.CostCenterId!.Value).Distinct().ToList();
			ViewBag.CostCenters = await _context.CostCenters.AsNoTracking().Where(c => ccIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.Name);
			return View(e);
		}

		// تفاصيل فاتورة بيع
		[SessionValidation][HttpGet]
		public async Task<IActionResult> SalesInvoiceDetail(int id)
		{
			var inv = await _context.SalesInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) return RedirectToAction(nameof(SalesInvoices));
			ViewBag.Customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == inv.CustomerId);
			return View(inv);
		}

		// HM-8: official A4 invoice — back-office (accountant) path. Gate = SessionValidation; guard = company (DefaultCompanyId).
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PrintInvoice(int id)
		{
			var inv = await _context.SalesInvoices.AsNoTracking().Include(i => i.Lines)
				.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) return RedirectToAction(nameof(SalesInvoices));
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var pd = await CrossBuy.BL.OfficialInvoiceHelper.LoadPrintDataAsync(_context, inv, isAr);
			ViewBag.Company = pd.Company; ViewBag.Customer = pd.Customer; ViewBag.Dp = pd.Dp;
			ViewBag.CurrencyCode = pd.CurrencyCode; ViewBag.Uoms = pd.Uoms;
			return View("SalesInvoicePrint", inv);
		}

		// HM-8: stamp a walk-in beneficiary onto the invoice's DISPLAY fields (set-once, tax-zero only). Financials untouched.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StampInvoiceCustomer(int id, string name, string? taxNo)
		{
			var inv = await _context.SalesInvoices.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) { TempData["AccErr"] = L["Sales invoice not found"].Value; return RedirectToAction(nameof(SalesInvoices)); }
			var (ok, err) = CrossBuy.BL.OfficialInvoiceHelper.StampCustomer(inv, name, taxNo, _access.CurrentEmployeeId().ToString());
			if (!ok) { TempData["AccErr"] = err; return RedirectToAction(nameof(PrintInvoice), new { id }); }
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["The invoice beneficiary was stamped"].Value;
			return RedirectToAction(nameof(PrintInvoice), new { id });
		}

		// تفاصيل فاتورة شراء
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseInvoiceDetail(int id)
		{
			var inv = await _context.PurchaseInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) return RedirectToAction(nameof(PurchaseInvoices));
			ViewBag.Vendor = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.ID == inv.VendorId);
			return View(inv);
		}

		// كشف حساب عميل
		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomerStatement(int id)
		{
			var c = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (c == null) return RedirectToAction(nameof(Customers));
			ViewBag.Customer = c;
			ViewBag.Invoices = await _context.SalesInvoices.AsNoTracking().Where(i => i.CustomerId == id).OrderBy(i => i.InvoiceDate).ToListAsync();
			ViewBag.Receipts = await _context.Receipts.AsNoTracking().Where(r => r.CustomerId == id).OrderBy(r => r.ReceiptDate).ToListAsync();
			return View();
		}

		// كشف حساب مورد
		[SessionValidation][HttpGet]
		public async Task<IActionResult> VendorStatement(int id)
		{
			var v = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (v == null) return RedirectToAction(nameof(Vendors));
			ViewBag.Vendor = v;
			ViewBag.Invoices = await _context.PurchaseInvoices.AsNoTracking().Where(i => i.VendorId == id).OrderBy(i => i.InvoiceDate).ToListAsync();
			ViewBag.Payments = await _context.Payments.AsNoTracking().Where(p => p.VendorId == id).OrderBy(p => p.PaymentDate).ToListAsync();
			return View();
		}

		// قائمة القيود — Journal entries list
		[SessionValidation]
		[HttpGet]
		public IActionResult Journals() => View();   // shell; rows via JournalsData

		[SessionValidation][HttpGet]
		public async Task<IActionResult> JournalsData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var query = _context.JournalEntries.AsNoTracking().Where(e => e.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.JournalEntry>(terms, s =>
					e => (e.EntryNo != null && e.EntryNo.Contains(s)) || (e.Description != null && e.Description.Contains(s)) || (e.DescriptionEn != null && e.DescriptionEn.Contains(s)) || (e.JournalType != null && e.JournalType.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
			var total = await query.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var list = await query.OrderByDescending(e => e.ID).Skip((page - 1) * pageSize).Take(pageSize)
				.Select(e => new JournalListItem
				{
					Id = e.ID, EntryNo = e.EntryNo, EntryDate = e.EntryDate, JournalType = e.JournalType,
					Status = e.Status, Description = e.Description, DescriptionEn = e.DescriptionEn, SourceType = e.SourceType,
					Total = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0,
				}).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_JournalRows", list);
		}

		// إنشاء قيد — New journal entry (form)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> CreateJournal()
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);   // manual JE lines can carry a Project (JournalLineInput.ProjectId flows through)
			return View();
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreateJournal(DateTime entryDate, string? description, string? linesJson, string action)
		{
			List<JournalLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<JournalLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { lines = new(); }

			var input = new JournalEntryInput
			{
				CompanyID = DefaultCompanyId, EntryDate = entryDate, Description = description, Lines = lines,
			};

			var empId = _access.CurrentEmployeeId();
			var post = action == "post";

			// large entries can't be self-posted: above the threshold they are saved as a draft for a ChiefAccountant to approve & post (SoD)
			var total = lines.Sum(l => l.Debit);
			var threshold = await ApprovalThresholdAsync();
			bool forcedDraft = false;
			if (post && threshold > 0 && total >= threshold) { post = false; forcedDraft = true; }

			bool ok; string? err;
			if (post)
			{
				var (o, e, _) = await _journals.CreateAndPostAsync(input, empId);
				ok = o; err = e;
			}
			else
			{
				var (o, e, _) = await _journals.CreateDraftAsync(input, empId);
				ok = o; err = e;
			}

			if (!ok)
			{
				TempData["AccErr"] = err;
				ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
				ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
				return View();
			}
			TempData["AccMsg"] = forcedDraft ? L["The entry ({0:N2}) exceeds the approval threshold — saved as a draft awaiting the chief accountant's approval", total].Value
				: post ? L["The journal entry was posted successfully"].Value : L["The journal entry was saved as a draft"].Value;
			return RedirectToAction(nameof(Journals));
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> PostJournal(int id)
		{
			var empId = _access.CurrentEmployeeId();
			var entry = await _context.JournalEntries.AsNoTracking().Where(e => e.ID == id && e.CompanyID == DefaultCompanyId)
				.Select(e => new { e.CreatedBy, Total = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0 }).FirstOrDefaultAsync();
			if (entry == null) { TempData["AccErr"] = L["Journal entry not found"].Value; return RedirectToAction(nameof(Journals)); }

			var threshold = await ApprovalThresholdAsync();
			if (threshold > 0 && entry.Total >= threshold)
			{
				// material entry → only a ChiefAccountant may approve & post, and not the one who created it (SoD)
				if (!await IsChiefAsync()) { TempData["AccErr"] = L["The entry ({0:N2}) exceeds the approval threshold — the chief accountant's approval is required", entry.Total].Value; return RedirectToAction(nameof(Journals)); }
				if (entry.CreatedBy != null && entry.CreatedBy == empId) { TempData["AccErr"] = L["Segregation of duties: the entry's creator cannot approve it — another chief accountant is required"].Value; return RedirectToAction(nameof(Journals)); }
			}

			var (ok, err) = await _journals.PostAsync(id, empId);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Posted and approved"].Value : err;
			return RedirectToAction(nameof(Journals));
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> ReverseJournal(int id)
		{
			var (ok, err, _) = await _journals.ReverseAsync(id, null, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The journal entry was reversed"].Value : err;
			return RedirectToAction(nameof(Journals));
		}

		// ميزان المراجعة — Trial Balance (optional cost-center slice)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> TrialBalance(DateTime? from, DateTime? to, int? costCenterId)
		{
			var tb = await _gl.TrialBalanceAsync(DefaultCompanyId, from, to, costCenterId);
			ViewBag.From = from; ViewBag.To = to;
			ViewBag.CostCenterId = costCenterId;
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
			return View(tb);
		}

		// مراكز التكلفة — Cost Centers (tree, linked to the org hierarchy)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> CostCenters()
		{
			var tree = await _costCenters.GetTreeAsync(DefaultCompanyId);
			return View(tree);
		}

		// الفترات المالية — Fiscal Periods (open / soft-close / close)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> Periods()
		{
			var list = await _periods.ListAsync(DefaultCompanyId);
			return View(list);
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> SetPeriodStatus(int id, string status)
		{
			var (ok, err) = await _periods.SetStatusAsync(id, status);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The period status was updated"].Value : err;
			return RedirectToAction(nameof(Periods));
		}

		// قيد الرواتب — Payroll posting (preview computed payroll, then post the journal)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> Payroll(int? year, int? month)
		{
			var y = year ?? 2026;
			var m = month ?? 1;
			var preview = await _posting.PreviewPayrollAsync(DefaultCompanyId, y, m);
			return View(preview);
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> PostPayroll(int year, int month)
		{
			var (ok, err, _) = await _posting.PostPayrollRunAsync(DefaultCompanyId, year, month, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The payroll entry was posted and payslips issued successfully"].Value : err;
			return RedirectToAction(nameof(Payroll), new { year, month });
		}

		// ---------------- Payroll disbursement & statutory remittance (HR-4) ----------------
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PayrollDisbursement(int? year, int? month)
		{
			var y = year ?? 2026; var m = month ?? 1;
			ViewBag.Year = y; ViewBag.Month = m;
			var banks = await _banks.GetBankAccountsAsync(DefaultCompanyId);
			var boxes = await _banks.GetCashBoxesAsync(DefaultCompanyId);
			ViewBag.PaySources = banks.Select(b => new PaySourceOption { GlAccountId = b.GlAccountId, Name = b.BankName })
				.Concat(boxes.Select(c => new PaySourceOption { GlAccountId = c.GlAccountId, Name = c.Name })).ToList();
			return View(await _posting.GetDisbursementViewAsync(DefaultCompanyId, y, m));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("pay")]
		public async Task<IActionResult> DisbursePayroll(int year, int month, int payFromGlAccountId, DateTime payDate)
		{
			var (ok, err) = await _posting.DisbursePayrollAsync(DefaultCompanyId, year, month, payFromGlAccountId, payDate, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Net payroll was disbursed"].Value : err;
			return RedirectToAction(nameof(PayrollDisbursement), new { year, month });
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("pay")]
		public async Task<IActionResult> RemitStatutory(int year, int month, string component, int payFromGlAccountId, DateTime payDate)
		{
			var (ok, err) = await _posting.RemitStatutoryAsync(DefaultCompanyId, year, month, component, payFromGlAccountId, payDate, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The deduction was remitted to the authority"].Value : err;
			return RedirectToAction(nameof(PayrollDisbursement), new { year, month });
		}

		// ---------------- Payroll tax brackets & SI settings (HR-2d) ----------------
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PayrollTaxSettings()
		{
			ViewBag.Settings = await _context.PayrollSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyID == DefaultCompanyId)
				?? new Models.Context.Accounting.PayrollSettings { CompanyID = DefaultCompanyId };
			return View(await _context.PayrollTaxBrackets.AsNoTracking()
				.Where(b => b.CompanyID == DefaultCompanyId).OrderBy(b => b.Ordinal).ToListAsync());
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> SavePayrollSettings(decimal personalExemptionAnnual, bool taxBaseExcludesEmployeeSI, decimal siMinMonthly, decimal siMaxMonthly, decimal standardMonthlyHours)
		{
			var s = await _context.PayrollSettings.FirstOrDefaultAsync(x => x.CompanyID == DefaultCompanyId);
			if (s == null) { s = new Models.Context.Accounting.PayrollSettings { CompanyID = DefaultCompanyId, CreatedAt = DateTime.UtcNow }; _context.PayrollSettings.Add(s); }
			s.PersonalExemptionAnnual = personalExemptionAnnual < 0 ? 0 : personalExemptionAnnual;
			s.TaxBaseExcludesEmployeeSI = taxBaseExcludesEmployeeSI;
			s.SiMinMonthly = siMinMonthly < 0 ? 0 : siMinMonthly;
			s.SiMaxMonthly = siMaxMonthly < 0 ? 0 : siMaxMonthly;
			s.StandardMonthlyHours = standardMonthlyHours <= 0 ? 176 : standardMonthlyHours;
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["Payroll settings saved"].Value;
			return RedirectToAction(nameof(PayrollTaxSettings));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> SavePayrollBracket(int id, int ordinal, decimal fromAmount, decimal? toAmount, decimal rate)
		{
			if (toAmount.HasValue && toAmount.Value <= fromAmount) { TempData["AccErr"] = L["The bracket's upper limit must exceed the lower limit"].Value; return RedirectToAction(nameof(PayrollTaxSettings)); }
			var b = id > 0 ? await _context.PayrollTaxBrackets.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId) : null;
			if (b == null) { b = new Models.Context.Accounting.PayrollTaxBracket { CompanyID = DefaultCompanyId, CreatedAt = DateTime.UtcNow }; _context.PayrollTaxBrackets.Add(b); }
			b.Ordinal = ordinal; b.FromAmount = fromAmount < 0 ? 0 : fromAmount; b.ToAmount = toAmount; b.Rate = rate < 0 ? 0 : rate;
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["Tax bracket saved"].Value;
			return RedirectToAction(nameof(PayrollTaxSettings));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> DeletePayrollBracket(int id)
		{
			var b = await _context.PayrollTaxBrackets.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (b != null) { _context.PayrollTaxBrackets.Remove(b); await _context.SaveChangesAsync(); TempData["AccMsg"] = L["Bracket deleted"].Value; }
			return RedirectToAction(nameof(PayrollTaxSettings));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Payslips(int? year, int? month)
		{
			var y = year ?? 2026; var m = month ?? 1;
			ViewBag.Year = y; ViewBag.Month = m;
			return View(await _context.Payslips.AsNoTracking().Where(s => s.CompanyID == DefaultCompanyId && s.Year == y && s.Month == m).OrderBy(s => s.EmployeeName).ToListAsync());
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Payslip(int id)
		{
			var slip = await _context.Payslips.AsNoTracking().FirstOrDefaultAsync(s => s.ID == id && s.CompanyID == DefaultCompanyId);
			if (slip == null) { TempData["AccErr"] = L["Payslip not found"].Value; return RedirectToAction(nameof(Payslips)); }
			return View(slip);
		}

		// ---------------- AR: Customers / Sales invoices / Receipts ----------------
		[SessionValidation][HttpGet]
		public IActionResult Customers() => View();   // shell; rows loaded via CustomersData

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomersData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _ar.SearchCustomersAsync(DefaultCompanyId, q, active, page, pageSize);
			// per-customer outstanding (posted invoices − receipts − credit notes) — computed ONLY for this page's customers
			var ids = rows.Select(c => c.ID).ToList();
			var outstanding = new Dictionary<int, decimal>();
			if (ids.Count > 0)
			{
				var inv = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.Status == "Posted" && ids.Contains(i.CustomerId)).GroupBy(i => i.CustomerId).Select(g => new { id = g.Key, v = g.Sum(x => x.GrandTotal) }).ToListAsync();
				var rcp = await _context.Receipts.AsNoTracking().Where(r => r.CompanyID == DefaultCompanyId && r.Status == "Posted" && r.CustomerId != null && ids.Contains(r.CustomerId!.Value)).GroupBy(r => r.CustomerId!.Value).Select(g => new { id = g.Key, v = g.Sum(x => x.Amount) }).ToListAsync();
				var ret = await _context.SalesReturns.AsNoTracking().Where(s => s.CompanyID == DefaultCompanyId && s.Status == "Posted" && ids.Contains(s.CustomerId)).GroupBy(s => s.CustomerId).Select(g => new { id = g.Key, v = g.Sum(x => x.GrandTotal) }).ToListAsync();
				foreach (var x in inv) outstanding[x.id] = outstanding.GetValueOrDefault(x.id) + x.v;
				foreach (var x in rcp) outstanding[x.id] = outstanding.GetValueOrDefault(x.id) - x.v;
				foreach (var x in ret) outstanding[x.id] = outstanding.GetValueOrDefault(x.id) - x.v;
			}
			ViewBag.Outstanding = outstanding;
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
			return PartialView("_CustomerRows", rows);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomersExport(string? q, bool? active)
		{
			var (rows, _) = await _ar.SearchCustomersAsync(DefaultCompanyId, q, active, 1, 100000);
			var headers = new[] { L["Name"].Value, "Name (EN)", L["Tax Reg. No."].Value, L["Phone"].Value, L["Email"].Value, L["Contact Person"].Value, L["Segment"].Value, L["Credit Limit"].Value, L["Payment Terms (days)"].Value, L["Status"].Value };
			var data = rows.Select(c => (IReadOnlyList<object?>)new object?[] { c.Name, c.NameEn, c.TaxRegNo, c.Phone, c.Email, c.ContactPerson, c.Segment, c.CreditLimit, c.PaymentTermsDays, c.IsActive ? L["Active"].Value : L["Suspended"].Value });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Customers"].Value, headers, data, L["Customers — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "customers.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> VendorsExport(string? q, bool? active)
		{
			var (rows, _) = await _ap.SearchVendorsAsync(DefaultCompanyId, q, active, 1, 100000);
			var headers = new[] { L["Name"].Value, "Name (EN)", L["Tax Reg. No."].Value, L["Phone"].Value, L["Email"].Value, L["Contact Person"].Value, L["Segment"].Value, L["Payment Terms (days)"].Value, L["Status"].Value };
			var data = rows.Select(v => (IReadOnlyList<object?>)new object?[] { v.Name, v.NameEn, v.TaxRegNo, v.Phone, v.Email, v.ContactPerson, v.Segment, v.PaymentTermsDays, v.IsActive ? L["Active"].Value : L["Suspended"].Value });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Vendors"].Value, headers, data, L["Vendors — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "vendors.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> SalesInvoicesExport(string? q, string? status)
		{
			var query = _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.SalesInvoice>(terms, s => i => i.InvoiceNo.Contains(s) || (i.Notes != null && i.Notes.Contains(s))); if (pred != null) query = query.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
			var rows = await query.OrderByDescending(i => i.ID).ToListAsync();
			var headers = new[] { L["Invoice No."].Value, L["Date"].Value, L["Subtotal"].Value, L["Tax"].Value, L["Total"].Value, L["Status"].Value };
			var data = rows.Select(i => (IReadOnlyList<object?>)new object?[] { i.InvoiceNo, i.InvoiceDate, i.SubTotal, i.TaxTotal, i.GrandTotal, i.Status });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Sales Invoices"].Value, headers, data, L["Sales Invoices — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "sales-invoices.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseInvoicesExport(string? q, string? status)
		{
			var query = _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.PurchaseInvoice>(terms, s => i => i.InvoiceNo.Contains(s) || (i.Notes != null && i.Notes.Contains(s))); if (pred != null) query = query.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
			var rows = await query.OrderByDescending(i => i.ID).ToListAsync();
			var headers = new[] { L["Invoice No."].Value, L["Date"].Value, L["Subtotal"].Value, L["Tax"].Value, L["Total"].Value, L["Status"].Value };
			var data = rows.Select(i => (IReadOnlyList<object?>)new object?[] { i.InvoiceNo, i.InvoiceDate, i.SubTotal, i.TaxTotal, i.GrandTotal, i.Status });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Purchase Invoices"].Value, headers, data, L["Purchase Invoices — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "purchase-invoices.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> ReceiptsExport(string? q)
		{
			var query = _context.Receipts.AsNoTracking().Where(r => r.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.Receipt>(terms, s => r => r.ReceiptNo.Contains(s) || (r.Method != null && r.Method.Contains(s))); if (pred != null) query = query.Where(pred); }
			var rows = await query.OrderByDescending(r => r.ID).ToListAsync();
			var headers = new[] { L["Voucher No."].Value, L["Date"].Value, L["Amount"].Value, L["Method"].Value };
			var data = rows.Select(r => (IReadOnlyList<object?>)new object?[] { r.ReceiptNo, r.ReceiptDate, r.Amount, r.Method });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Receipts"].Value, headers, data, L["Receipts — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "receipts.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> PaymentsExport(string? q)
		{
			var query = _context.Payments.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.Payment>(terms, s => p => p.PaymentNo.Contains(s) || (p.Method != null && p.Method.Contains(s))); if (pred != null) query = query.Where(pred); }
			var rows = await query.OrderByDescending(p => p.ID).ToListAsync();
			var headers = new[] { L["Voucher No."].Value, L["Date"].Value, L["Amount"].Value, L["Method"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.PaymentNo, p.PaymentDate, p.Amount, p.Method });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Payments"].Value, headers, data, L["Payments — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "payments.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> JournalsExport(string? q, string? status)
		{
			var query = _context.JournalEntries.AsNoTracking().Where(e => e.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.JournalEntry>(terms, s => e => (e.EntryNo != null && e.EntryNo.Contains(s)) || (e.Description != null && e.Description.Contains(s)) || (e.JournalType != null && e.JournalType.Contains(s))); if (pred != null) query = query.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
			var rows = await query.OrderByDescending(e => e.ID).Select(e => new { e.EntryNo, e.EntryDate, e.JournalType, e.Status, e.Description, Total = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0 }).ToListAsync();
			var headers = new[] { L["Entry No."].Value, L["Date"].Value, L["Type"].Value, L["Status"].Value, L["Description"].Value, L["Total"].Value };
			var data = rows.Select(e => (IReadOnlyList<object?>)new object?[] { e.EntryNo, e.EntryDate, e.JournalType, e.Status, e.Description, e.Total });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Journals"].Value, headers, data, L["Journal Entries — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "journals.xlsx");
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomersSuggest(string? term)
		{
			var list = await _ar.SuggestCustomersAsync(DefaultCompanyId, term, 10);
			return Json(list.Select(x => new { value = x.value, name = x.name }));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> SaveCustomer(int id, string name, string? nameEn, string? taxRegNo, decimal? creditLimit, int? paymentTermsDays,
			string? phone, string? email, string? contactPerson, string? segment, string? address, string? shippingAddress, bool isActive = true)
		{
			var (ok, err) = await _ar.SaveCustomerAsync(DefaultCompanyId, new Models.Context.Accounting.Customer
			{
				ID = id, Name = name ?? "", NameEn = nameEn, TaxRegNo = taxRegNo, CreditLimit = creditLimit, PaymentTermsDays = paymentTermsDays,
				Phone = phone, Email = email, ContactPerson = contactPerson, Segment = segment, Address = address, ShippingAddress = shippingAddress, IsActive = isActive,
			});
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? (id > 0 ? L["Customer updated"].Value : L["Customer added"].Value) : err;
			return RedirectToAction(nameof(Customers));
		}

		// shared: write pagination headers consumed by the list views' fetch JS
		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		[SessionValidation][HttpGet]
		public IActionResult SalesInvoices() => View();   // shell; rows via SalesInvoicesData

		[SessionValidation][HttpGet]
		public async Task<IActionResult> SalesInvoicesData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var query = _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.SalesInvoice>(terms, s => i => i.InvoiceNo.Contains(s) || (i.Notes != null && i.Notes.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
			var total = await query.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await query.OrderByDescending(i => i.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_SalesInvoiceRows", rows);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> NewSalesInvoice()
		{
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.RevenueAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "REV").ToList();
			// line items are now searched on-demand via Inventory/ItemPickData (no full-catalog preload)
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreateSalesInvoice(int customerId, DateTime invoiceDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<SalesLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<SalesLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewSalesInvoice)); }
			// Pricing 2A — gross-margin floor: Block rejects the invoice; Warn proceeds and notifies.
			var (mBlock, mWarn) = await CheckLineMarginsAsync(lines.Select(l => (l.ItemId, l.Qty, l.UnitPrice, l.DiscountAmount)), currencyId, exchangeRate, invoiceDate);
			if (mBlock != null) { TempData["AccErr"] = mBlock; return RedirectToAction(nameof(NewSalesInvoice)); }
			// Pricing 2D — discount approval ceiling.
			var (dBlock, dWarn) = await _pricing.EvaluateLineDiscountsAsync(DefaultCompanyId, lines.Select(l => (l.Qty, l.UnitPrice, l.DiscountAmount)), await _access.CanAsync("manage"));
			if (dBlock != null) { TempData["AccErr"] = dBlock; return RedirectToAction(nameof(NewSalesInvoice)); }
			var (ok, err, _) = await _ar.CreateSalesInvoiceAsync(DefaultCompanyId, customerId, invoiceDate, lines, notes, null, currencyId, exchangeRate, projectId);
			if (!ok) { TempData["AccErr"] = err; return RedirectToAction(nameof(NewSalesInvoice)); }
			var okMsg2 = string.Join(" · ", new[] { mWarn, dWarn }.Where(m => m != null));
			TempData[okMsg2.Length > 0 ? "AccWarn" : "AccMsg"] = okMsg2.Length > 0 ? okMsg2 : L["The sales invoice was issued and posted"].Value;
			return RedirectToAction(nameof(SalesInvoices));
		}

		// P3: edit a posted sales invoice (reuses the New screen in edit mode → UpdateSalesInvoice)
		[SessionValidation][HttpGet]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditSalesInvoice(int id)
		{
			var inv = await _context.SalesInvoices.AsNoTracking().Include(i => i.Lines)
				.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) return NotFound();
			if (inv.Status != "Posted") { TempData["AccErr"] = L["Only posted invoices can be edited"].Value; return RedirectToAction(nameof(SalesInvoices)); }
			if (await _context.ReceiptAllocations.AsNoTracking().AnyAsync(a => a.CompanyID == DefaultCompanyId && a.SalesInvoiceId == id))
			{ TempData["AccErr"] = L["Cannot edit: this invoice has an allocated receipt — unallocate it first"].Value; return RedirectToAction(nameof(SalesInvoices)); }
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.RevenueAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "REV").ToList();
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);
			var slIds = inv.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
			ViewBag.ItemBarcodes = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && slIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Barcode ?? "");
			ViewBag.EditInvoice = inv;
			return View("NewSalesInvoice", inv);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> UpdateSalesInvoice(int id, int customerId, DateTime invoiceDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<SalesLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<SalesLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(EditSalesInvoice), new { id }); }
			var (mBlock, mWarn) = await CheckLineMarginsAsync(lines.Select(l => (l.ItemId, l.Qty, l.UnitPrice, l.DiscountAmount)), currencyId, exchangeRate, invoiceDate);
			if (mBlock != null) { TempData["AccErr"] = mBlock; return RedirectToAction(nameof(EditSalesInvoice), new { id }); }
			var (dBlock, dWarn) = await _pricing.EvaluateLineDiscountsAsync(DefaultCompanyId, lines.Select(l => (l.Qty, l.UnitPrice, l.DiscountAmount)), await _access.CanAsync("manage"));
			if (dBlock != null) { TempData["AccErr"] = dBlock; return RedirectToAction(nameof(EditSalesInvoice), new { id }); }
			var (ok, err, _) = await _ar.EditSalesInvoiceAsync(DefaultCompanyId, id, customerId, invoiceDate, lines, notes, null, currencyId, exchangeRate, projectId);
			if (!ok) { TempData["AccErr"] = err; return RedirectToAction(nameof(EditSalesInvoice), new { id }); }
			var okMsg = string.Join(" · ", new[] { mWarn, dWarn }.Where(m => m != null));
			TempData[okMsg.Length > 0 ? "AccWarn" : "AccMsg"] = okMsg.Length > 0 ? okMsg : L["The sales invoice was updated"].Value;
			return RedirectToAction(nameof(SalesInvoices));
		}

		// Pricing 2A — evaluate the gross-margin floor for each stock line (see PricingService.CheckMarginAsync).
		private async Task<(string? block, string? warn)> CheckLineMarginsAsync(
			IEnumerable<(int? ItemId, decimal Qty, decimal UnitPrice, decimal DiscountAmount)> lines,
			int? currencyId, decimal? exchangeRate, DateTime asOf)
		{
			var warns = new List<string>();
			foreach (var l in lines)
			{
				if (!l.ItemId.HasValue || l.ItemId.Value <= 0) continue;
				decimal net = l.Qty > 0 ? (l.UnitPrice - l.DiscountAmount / l.Qty) : l.UnitPrice;
				var mc = await _pricing.CheckMarginAsync(DefaultCompanyId, l.ItemId.Value, net, currencyId, exchangeRate, asOf);
				if (mc.Mode == "Off" || mc.Ok || mc.Skipped) continue;
				string msg = L["«{0}»: the price {1:N2} is below the floor {2:N2} (cost {3:N2} + margin {4:N2}%)", mc.ItemName, mc.PriceFunctional, mc.FloorFunctional, mc.CostFunctional, mc.MarginPct].Value;
				if (mc.Mode == "Block") return (L["Margin floor block — "].Value + msg, null);
				warns.Add(msg);
			}
			return (null, warns.Count > 0 ? L["Margin floor warning — "].Value + string.Join(" · ", warns) : null);
		}

		// ---------------- Sales returns / credit notes (P3-3a) ----------------
		[SessionValidation][HttpGet]
		public async Task<IActionResult> SalesReturns()
		{
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			return View(await _ar.GetSalesReturnsAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> NewSalesReturn()
		{
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.RevenueAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "REV").ToList();
			// line items are now searched on-demand via Inventory/ItemPickData (no full-catalog preload)
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreateSalesReturn(int customerId, int? originalInvoiceId, DateTime returnDate, string? notes, string? linesJson)
		{
			List<SalesLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<SalesLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _ar.CreateSalesReturnAsync(DefaultCompanyId, customerId, originalInvoiceId, returnDate, lines, notes, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The sales return was recorded and a credit note issued"].Value : err;
			return ok ? RedirectToAction(nameof(SalesReturns)) : RedirectToAction(nameof(NewSalesReturn));
		}

		// P3: edit a posted sales return (reuses the New screen → UpdateSalesReturn)
		[SessionValidation][HttpGet]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditSalesReturn(int id)
		{
			var ret = await _ar.GetSalesReturnAsync(DefaultCompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(SalesReturns)); }
			if (ret.Status != "Posted") { TempData["AccErr"] = L["Only posted invoices can be edited"].Value; return RedirectToAction(nameof(SalesReturns)); }
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.RevenueAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "REV").ToList();
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			var ids = ret.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
			ViewBag.ItemBarcodes = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Barcode ?? "");
			ViewBag.EditReturn = ret;
			return View("NewSalesReturn", ret);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> UpdateSalesReturn(int id, int customerId, int? originalInvoiceId, DateTime returnDate, string? notes, string? linesJson)
		{
			List<SalesLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<SalesLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _ar.EditSalesReturnAsync(DefaultCompanyId, id, customerId, originalInvoiceId, returnDate, lines, notes, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The sales return was updated"].Value : err;
			return ok ? RedirectToAction(nameof(SalesReturns)) : RedirectToAction(nameof(EditSalesReturn), new { id });
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> SalesReturnDetail(int id)
		{
			var ret = await _ar.GetSalesReturnAsync(DefaultCompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(SalesReturns)); }
			ViewBag.Customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == ret.CustomerId);
			return View(ret);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Receipts()
		{
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.CashAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.Code.StartsWith("1101")).ToList();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			return View();   // shell; rows via ReceiptsData
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> ReceiptsData(string? q, int page = 1, int pageSize = 25)
		{
			var query = _context.Receipts.AsNoTracking().Where(r => r.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.Receipt>(terms, s => r => r.ReceiptNo.Contains(s) || (r.Method != null && r.Method.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			var total = await query.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await query.OrderByDescending(r => r.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_ReceiptRows", rows);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("pay")]
		public async Task<IActionResult> CreateReceipt(int customerId, DateTime receiptDate, decimal amount, string method, int cashAccountId, string? notes, int? currencyId, decimal? exchangeRate)
		{
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(Receipts)); }
			var th = await ApprovalThresholdAsync();
			if (th > 0 && amount >= th && !await IsChiefAsync()) { TempData["AccErr"] = L["The amount ({0:N2}) exceeds the approval threshold — the chief accountant is required", amount].Value; return RedirectToAction(nameof(Receipts)); }
			var (ok, err) = await _ar.CreateReceiptAsync(DefaultCompanyId, customerId, receiptDate, amount, method, cashAccountId, notes, null, currencyId, exchangeRate);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The receipt voucher was recorded and posted"].Value : err;
			return RedirectToAction(nameof(Receipts));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> ArAging() => View(await _ar.AgingAsync(DefaultCompanyId, DateTime.UtcNow));

		// 2.8 customer analytics — profitability / LTV / segment analysis
		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomerAnalytics() => View(await _ar.GetCustomerAnalyticsAsync(DefaultCompanyId));

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomerAnalyticsExport()
		{
			var a = await _ar.GetCustomerAnalyticsAsync(DefaultCompanyId);
			var headers = new[] { L["Customer"].Value, L["Segment"].Value, L["Sales"].Value, L["Collections"].Value, L["Returns"].Value, L["Outstanding"].Value, L["Revenue"].Value, L["Cost"].Value, L["Gross Profit"].Value, L["Margin %"].Value, L["Invoice Count"].Value, L["First Invoice"].Value, L["Last Invoice"].Value, L["Avg Invoice"].Value };
			var rows = a.Rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.Name, x.Segment, x.Invoiced, x.Received, x.Returns, x.Outstanding, x.Revenue, x.Cogs, x.Margin, x.MarginPct, x.InvoiceCount, x.FirstInvoice, x.LastInvoice, x.AvgInvoice });
			var bytes = ExcelExporter.Build(L["Customer Analytics"].Value, headers, rows, L["Customer Analytics — CrossBuy"].Value);
			return File(bytes, ExcelExporter.ContentType, "customer-analytics.xlsx");
		}

		// ---------------- AP: Vendors / Purchase invoices / Payments ----------------
		[SessionValidation][HttpGet]
		public IActionResult Vendors() => View();   // shell; rows loaded via VendorsData

		[SessionValidation][HttpGet]
		public async Task<IActionResult> VendorsData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _ap.SearchVendorsAsync(DefaultCompanyId, q, active, page, pageSize);
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
			return PartialView("_VendorRows", rows);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> VendorsSuggest(string? term)
		{
			var list = await _ap.SuggestVendorsAsync(DefaultCompanyId, term, 10);
			return Json(list.Select(x => new { value = x.value, name = x.name }));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> SaveVendor(int id, string name, string? nameEn, string? taxRegNo, int? paymentTermsDays,
			string? phone, string? email, string? contactPerson, string? segment, string? address, bool isActive = true)
		{
			var (ok, err) = await _ap.SaveVendorAsync(DefaultCompanyId, new Models.Context.Accounting.Vendor
			{
				ID = id, Name = name ?? "", NameEn = nameEn, TaxRegNo = taxRegNo, PaymentTermsDays = paymentTermsDays,
				Phone = phone, Email = email, ContactPerson = contactPerson, Segment = segment, Address = address, IsActive = isActive,
			});
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? (id > 0 ? L["Vendor updated"].Value : L["Vendor added"].Value) : err;
			return RedirectToAction(nameof(Vendors));
		}

		// Inline "quick add" from any document screen's Vendor dropdown → returns {ok,id,name} to append+select.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> VendorQuickAdd(string name, string? nameEn, string? taxRegNo)
		{
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Name is required"].Value });
			var v = await _ap.CreateVendorAsync(DefaultCompanyId, name.Trim(),
				string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(),
				string.IsNullOrWhiteSpace(taxRegNo) ? null : taxRegNo.Trim());
			var isAr = (HttpContext.Items["Culture"]?.ToString() == "ar");
			return Json(new { ok = true, id = v.ID, name = isAr ? v.Name : (v.NameEn ?? v.Name) });
		}

		// Inline "quick add" for the Customer dropdown on sales document screens → {ok,id,name}.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> CustomerQuickAdd(string name, string? nameEn, string? taxRegNo)
		{
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Name is required"].Value });
			var c = await _ar.CreateCustomerAsync(DefaultCompanyId, name.Trim(),
				string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(),
				string.IsNullOrWhiteSpace(taxRegNo) ? null : taxRegNo.Trim(), null);
			var isAr = (HttpContext.Items["Culture"]?.ToString() == "ar");
			return Json(new { ok = true, id = c.ID, name = isAr ? c.Name : (c.NameEn ?? c.Name) });
		}

		[SessionValidation][HttpGet]
		public IActionResult PurchaseInvoices() => View();   // shell; rows via PurchaseInvoicesData

		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseInvoicesData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var query = _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.PurchaseInvoice>(terms, s => i => i.InvoiceNo.Contains(s) || (i.Notes != null && i.Notes.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
			var total = await query.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await query.OrderByDescending(i => i.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_PurchaseInvoiceRows", rows);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> NewPurchaseInvoice()
		{
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			ViewBag.ExpenseAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "EXP" || a.TypeCode == "ASSET").ToList();
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
			// line items are now searched on-demand via Inventory/ItemPickData (no full-catalog preload)
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreatePurchaseInvoice(int vendorId, DateTime invoiceDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<PurchaseLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<PurchaseLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// currency-override guard: issuing a document in a non-functional currency needs the permission
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewPurchaseInvoice)); }
			var (ok, err, _) = await _ap.CreatePurchaseInvoiceAsync(DefaultCompanyId, vendorId, invoiceDate, lines, notes, null, currencyId, exchangeRate, projectId);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The purchase invoice was recorded and posted"].Value : err;
			return ok ? RedirectToAction(nameof(PurchaseInvoices)) : RedirectToAction(nameof(NewPurchaseInvoice));
		}

		// ===== HM-16: vendor-invoice ↔ goods-receipt matching (1:1, minimal). Lists a vendor's OPEN goods receipts
		// (Posted, not yet invoiced) and turns a chosen one into a purchase invoice that CLEARS its GRNI (Dr GRNI / Cr AP,
		// no second stock movement). Price/tax variance is NOT supported here — the invoice value must equal the received
		// value, else the match is REJECTED. The invoice is booked in the branch functional currency (GRNI is a functional
		// balance), so the receipt's functional line costs pass straight through with no re-conversion.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> MatchReceipts(int? vendorId)
		{
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			ViewBag.SelVendor = vendorId;
			if (vendorId is int vid)
				ViewBag.OpenReceipts = await _context.GoodsReceipts.AsNoTracking()
					.Where(g => g.CompanyID == DefaultCompanyId && g.Status == "Posted" && g.InvoiceId == null && g.VendorId == vid)
					.OrderByDescending(g => g.ID).ToListAsync();
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreateInvoiceFromReceipt(int goodsReceiptId, DateTime invoiceDate, decimal invoiceAmount)
		{
			// thin: all validation (posted / open / vendor / value-guard), line-building and set-once live in the service.
			var vendorForReturn = await _context.GoodsReceipts.AsNoTracking().Where(g => g.ID == goodsReceiptId && g.CompanyID == DefaultCompanyId).Select(g => g.VendorId).FirstOrDefaultAsync();
			var (ok, err, _) = await _ap.MatchGoodsReceiptToInvoiceAsync(DefaultCompanyId, goodsReceiptId, invoiceDate, invoiceAmount, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The vendor invoice was matched to the goods receipt and GRNI was cleared"].Value : err;
			return ok ? RedirectToAction(nameof(PurchaseInvoices)) : RedirectToAction(nameof(MatchReceipts), new { vendorId = vendorForReturn });
		}

		// P3: edit a posted purchase invoice (reuses the New screen → UpdatePurchaseInvoice)
		[SessionValidation][HttpGet]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditPurchaseInvoice(int id)
		{
			var inv = await _context.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
				.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == DefaultCompanyId);
			if (inv == null) return NotFound();
			if (inv.Status != "Posted") { TempData["AccErr"] = L["Only posted invoices can be edited"].Value; return RedirectToAction(nameof(PurchaseInvoices)); }
			if (await _context.PaymentAllocations.AsNoTracking().AnyAsync(a => a.CompanyID == DefaultCompanyId && a.PurchaseInvoiceId == id))
			{ TempData["AccErr"] = L["Cannot edit: this invoice has an allocated payment — unallocate it first"].Value; return RedirectToAction(nameof(PurchaseInvoices)); }
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			ViewBag.ExpenseAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "EXP" || a.TypeCode == "ASSET").ToList();
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);
			var plIds = inv.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
			ViewBag.ItemBarcodes = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && plIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Barcode ?? "");
			ViewBag.EditInvoice = inv;
			return View("NewPurchaseInvoice", inv);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> UpdatePurchaseInvoice(int id, int vendorId, DateTime invoiceDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<PurchaseLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<PurchaseLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(EditPurchaseInvoice), new { id }); }
			var (ok, err, _) = await _ap.EditPurchaseInvoiceAsync(DefaultCompanyId, id, vendorId, invoiceDate, lines, notes, null, currencyId, exchangeRate, projectId);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The purchase invoice was updated"].Value : err;
			return ok ? RedirectToAction(nameof(PurchaseInvoices)) : RedirectToAction(nameof(EditPurchaseInvoice), new { id });
		}

		// ---------------- Purchase returns / debit notes (P3-3b) ----------------
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseReturns()
		{
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			return View(await _ap.GetPurchaseReturnsAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> NewPurchaseReturn()
		{
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			// line items are now searched on-demand via Inventory/ItemPickData (no full-catalog preload)
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreatePurchaseReturn(int vendorId, int? originalInvoiceId, DateTime returnDate, string? notes, string? linesJson)
		{
			List<PurchaseLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<PurchaseLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _ap.CreatePurchaseReturnAsync(DefaultCompanyId, vendorId, originalInvoiceId, returnDate, lines, notes, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The purchase return was recorded and a debit note issued"].Value : err;
			return ok ? RedirectToAction(nameof(PurchaseReturns)) : RedirectToAction(nameof(NewPurchaseReturn));
		}

		// P3: edit a posted purchase return (reuses the New screen → UpdatePurchaseReturn)
		[SessionValidation][HttpGet]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditPurchaseReturn(int id)
		{
			var ret = await _ap.GetPurchaseReturnAsync(DefaultCompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(PurchaseReturns)); }
			if (ret.Status != "Posted") { TempData["AccErr"] = L["Only posted invoices can be edited"].Value; return RedirectToAction(nameof(PurchaseReturns)); }
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			var ids = ret.Lines.Where(l => l.ItemId != 0).Select(l => l.ItemId).Distinct().ToList();
			ViewBag.ItemBarcodes = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Barcode ?? "");
			ViewBag.EditReturn = ret;
			return View("NewPurchaseReturn", ret);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> UpdatePurchaseReturn(int id, int vendorId, int? originalInvoiceId, DateTime returnDate, string? notes, string? linesJson)
		{
			List<PurchaseLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<PurchaseLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _ap.EditPurchaseReturnAsync(DefaultCompanyId, id, vendorId, originalInvoiceId, returnDate, lines, notes, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The purchase return was updated"].Value : err;
			return ok ? RedirectToAction(nameof(PurchaseReturns)) : RedirectToAction(nameof(EditPurchaseReturn), new { id });
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseReturnDetail(int id)
		{
			var ret = await _ap.GetPurchaseReturnAsync(DefaultCompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(PurchaseReturns)); }
			ViewBag.Vendor = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.ID == ret.VendorId);
			var retIsEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var retItemIds = ret.Lines.Where(l => l.ItemId > 0).Select(l => l.ItemId).Distinct().ToList();
			ViewBag.ItemNames = await _context.Items.AsNoTracking().Where(i => retItemIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => retIsEn ? (string.IsNullOrEmpty(i.NameEn) ? i.Name : i.NameEn) : i.Name);
			return View(ret);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Payments()
		{
			ViewBag.Vendors = await _ap.GetVendorsAsync(DefaultCompanyId);
			ViewBag.CashAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.Code.StartsWith("1101")).ToList();
			ViewBag.WhtCodes = (await _tax.GetCodesAsync(DefaultCompanyId)).Where(c => c.Kind == "WHT" && c.IsActive).ToList();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			return View();   // shell; rows via PaymentsData
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> PaymentsData(string? q, int page = 1, int pageSize = 25)
		{
			var query = _context.Payments.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Models.Context.Accounting.Payment>(terms, s => p => p.PaymentNo.Contains(s) || (p.Method != null && p.Method.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			var total = await query.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await query.OrderByDescending(p => p.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_PaymentRows", rows);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("pay")]
		public async Task<IActionResult> CreatePayment(int vendorId, DateTime paymentDate, decimal amount, string method, int cashAccountId, string? notes, decimal whtRate = 0, int? currencyId = null, decimal? exchangeRate = null)
		{
			var functional = await FunctionalCurrencyIdAsync();
			if (currencyId.HasValue && currencyId.Value != functional && !await _access.CanAsync("currency-override"))
			{ TempData["AccErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(Payments)); }
			var th = await ApprovalThresholdAsync();
			if (th > 0 && amount >= th && !await IsChiefAsync()) { TempData["AccErr"] = L["The amount ({0:N2}) exceeds the approval threshold — the chief accountant is required", amount].Value; return RedirectToAction(nameof(Payments)); }
			var (ok, err) = await _ap.CreatePaymentAsync(DefaultCompanyId, vendorId, paymentDate, amount, method, cashAccountId, notes, null, whtRate, currencyId, exchangeRate);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The payment voucher was recorded and posted"].Value : err;
			return RedirectToAction(nameof(Payments));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> ApAging() => View(await _ap.AgingAsync(DefaultCompanyId, DateTime.UtcNow));

		// ---------------- Banks / Cash boxes / Transfers / Reconciliation ----------------
		[SessionValidation][HttpGet]
		public async Task<IActionResult> BankAccounts()
		{
			ViewBag.CashAccounts = await CashAccountsAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			return View(await _banks.GetBankAccountsAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CreateBankAccount(string bankName, string? bankNameEn, string? accountNumber, string? iban, int glAccountId, decimal openingBalance, int? currencyId, decimal? foreignBalance)
		{
			if (string.IsNullOrWhiteSpace(bankName) || glAccountId == 0) TempData["AccErr"] = L["The bank name and ledger account are required"].Value;
			else { await _banks.CreateBankAccountAsync(DefaultCompanyId, bankName, bankNameEn, accountNumber, iban, glAccountId, openingBalance, currencyId, foreignBalance); TempData["AccMsg"] = L["Bank account added"].Value; }
			return RedirectToAction(nameof(BankAccounts));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CashBoxes()
		{
			ViewBag.CashAccounts = await CashAccountsAsync();
			return View(await _banks.GetCashBoxesAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CreateCashBox(string name, string? nameEn, int glAccountId)
		{
			if (string.IsNullOrWhiteSpace(name) || glAccountId == 0) TempData["AccErr"] = L["The name and ledger account are required"].Value;
			else { await _banks.CreateCashBoxAsync(DefaultCompanyId, name, nameEn, glAccountId, null); TempData["AccMsg"] = L["Cash box added"].Value; }
			return RedirectToAction(nameof(CashBoxes));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Transfer()
		{
			ViewBag.CashAccounts = await CashAccountsAsync();
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("pay")]
		public async Task<IActionResult> DoTransfer(int fromGlAccountId, int toGlAccountId, decimal amount, DateTime date, string? notes)
		{
			var (ok, err) = await _banks.TransferAsync(DefaultCompanyId, fromGlAccountId, toGlAccountId, amount, date, notes, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The transfer was made and posted"].Value : err;
			return RedirectToAction(nameof(Transfer));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Reconcile(int? bankAccountId, DateTime? statementDate)
		{
			ViewBag.Banks = await _banks.GetBankAccountsAsync(DefaultCompanyId);
			ViewBag.BankAccountId = bankAccountId;
			ViewBag.StatementDate = statementDate;
			if (bankAccountId.HasValue)
			{
				var (bank, book, lines) = await _banks.GetReconciliationViewAsync(DefaultCompanyId, bankAccountId.Value, statementDate ?? DateTime.UtcNow);
				ViewBag.Bank = bank; ViewBag.BookBalance = book;
				return View(lines);
			}
			return View(new List<ReconLine>());
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> DoReconcile(int bankAccountId, DateTime statementDate, decimal statementBalance, List<int>? clearedLineIds)
		{
			var (ok, err, _) = await _banks.ReconcileAsync(DefaultCompanyId, bankAccountId, statementDate, statementBalance, clearedLineIds ?? new());
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The bank reconciliation was saved"].Value : err;
			return RedirectToAction(nameof(Reconciliations));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Reconciliations() => View(await _banks.GetReconciliationsAsync(DefaultCompanyId));

		// كشف حساب — Account ledger (drill-down)
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> Ledger(int accountId, DateTime? from, DateTime? to)
		{
			var st = await _gl.AccountStatementAsync(DefaultCompanyId, accountId, from, to);
			if (st == null) return RedirectToAction(nameof(TrialBalance));
			ViewBag.From = from; ViewBag.To = to;
			return View(st);
		}

		// ===== Phase 6: Fixed Assets & Depreciation =====

		[SessionValidation][HttpGet]
		public async Task<IActionResult> FixedAssets()
		{
			ViewBag.Categories = await _assets.GetCategoriesAsync(DefaultCompanyId);
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
			ViewBag.FundingAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true))
				.Where(a => a.Code.StartsWith("1101") || a.TypeCode == "LIAB").ToList();
			return View(await _assets.GetAssetsAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> CreateFixedAsset(string name, string? nameEn, int? categoryId, DateTime acquisitionDate,
			decimal cost, decimal salvageValue, int usefulLifeMonths, int? costCenterId, int fundingAccountId, string? notes)
		{
			var (ok, err, _) = await _assets.CreateAssetAsync(DefaultCompanyId, new FixedAssetInput
			{
				Name = name, NameEn = nameEn, CategoryId = categoryId, AcquisitionDate = acquisitionDate, Cost = cost,
				SalvageValue = salvageValue, UsefulLifeMonths = usefulLifeMonths, CostCenterId = costCenterId,
				FundingAccountId = fundingAccountId, Notes = notes,
			}, null);
			if (ok) TempData["AccMsg"] = L["The asset was recorded and its acquisition entry posted"].Value; else TempData["AccErr"] = err;
			return RedirectToAction(nameof(FixedAssets));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> FixedAssetDetail(int id)
		{
			var asset = await _assets.GetAssetAsync(DefaultCompanyId, id);
			if (asset == null) return RedirectToAction(nameof(FixedAssets));
			ViewBag.History = await _assets.GetAssetHistoryAsync(id);
			ViewBag.Monthly = _assets.MonthlyDepreciation(asset);
			ViewBag.CashAccounts = await CashAccountsAsync();
			ViewBag.MaintSchedules = await MntSvc.GetSchedulesAsync(DefaultCompanyId, id);   // asset maintenance
			ViewBag.MaintRecords = await MntSvc.GetRecordsAsync(DefaultCompanyId, id);
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);
			return View(asset);
		}

		// ===== Asset scheduled maintenance =====
		private CrossBuy.BL.IMaintenanceService MntSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IMaintenanceService)) as CrossBuy.BL.IMaintenanceService)!;

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> SaveMaintenanceSchedule(int id, int assetId, string title, string type, int intervalMonths, DateTime nextDueDate, decimal? estimatedCost, bool isActive)
		{
			var (ok, err, _) = await MntSvc.SaveScheduleAsync(new CrossBuy.Models.Context.Accounting.MaintenanceSchedule { ID = id, CompanyID = DefaultCompanyId, AssetId = assetId, Title = title ?? "", Type = type, IntervalMonths = intervalMonths, NextDueDate = nextDueDate, EstimatedCost = estimatedCost, IsActive = isActive });
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The maintenance schedule was saved"].Value : err;
			return RedirectToAction(nameof(FixedAssetDetail), new { id = assetId });
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> DeleteMaintenanceSchedule(int id, int assetId)
		{
			await MntSvc.DeleteScheduleAsync(DefaultCompanyId, id);
			TempData["AccMsg"] = L["Schedule deleted"].Value;
			return RedirectToAction(nameof(FixedAssetDetail), new { id = assetId });
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> LogMaintenance(int assetId, int? scheduleId, DateTime date, string? description, decimal cost, string? vendor, int? payFromGlAccountId, int? projectId)
		{
			var (ok, err) = await MntSvc.LogMaintenanceAsync(DefaultCompanyId, assetId, scheduleId, date, description, cost, vendor, payFromGlAccountId, projectId, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? (payFromGlAccountId.HasValue && cost > 0 ? L["Maintenance logged and the journal entry posted"].Value : L["Maintenance logged"].Value) : err;
			return RedirectToAction(nameof(FixedAssetDetail), new { id = assetId });
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> MaintenanceDue(int days = 30)
		{
			ViewBag.Days = days;
			return View(await MntSvc.DueSoonAsync(DefaultCompanyId, days));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> DisposeAsset(int id, DateTime disposalDate, decimal proceeds, int cashAccountId)
		{
			var (ok, err) = await _assets.DisposeAssetAsync(DefaultCompanyId, id, disposalDate, proceeds, cashAccountId, null);
			if (ok) TempData["AccMsg"] = L["The asset was disposed and the journal entry posted"].Value; else TempData["AccErr"] = err;
			return RedirectToAction(nameof(FixedAssetDetail), new { id });
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> AssetCategories()
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			return View(await _assets.GetCategoriesAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CreateAssetCategory(string name, string? nameEn, int defaultUsefulLifeMonths, int costAccountId, int accumDepAccountId, int depExpenseAccountId)
		{
			var (ok, err, _) = await _assets.CreateCategoryAsync(DefaultCompanyId, name, nameEn, defaultUsefulLifeMonths, costAccountId, accumDepAccountId, depExpenseAccountId);
			if (ok) TempData["AccMsg"] = L["Category added"].Value; else TempData["AccErr"] = err;
			return RedirectToAction(nameof(AssetCategories));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> DepreciationRuns() => View(await _assets.GetRunsAsync(DefaultCompanyId));

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> RunDepreciation(DateTime periodDate)
		{
			var (ok, err, _) = await _assets.RunDepreciationAsync(DefaultCompanyId, periodDate, null);
			if (ok) TempData["AccMsg"] = L["Depreciation was calculated and posted"].Value; else TempData["AccErr"] = err;
			return RedirectToAction(nameof(DepreciationRuns));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> DepreciationRunDetail(int id)
		{
			var run = await _assets.GetRunAsync(DefaultCompanyId, id);
			if (run == null) return RedirectToAction(nameof(DepreciationRuns));
			var assetIds = run.Lines.Select(l => l.FixedAssetId).ToList();
			ViewBag.AssetNames = (await _assets.GetAssetsAsync(DefaultCompanyId))
				.Where(a => assetIds.Contains(a.ID)).ToDictionary(a => a.ID, a => a);
			return View(run);
		}

		// ===== Phase 7: Taxes & ETA =====

		[SessionValidation][HttpGet]
		public async Task<IActionResult> TaxCodes() => View(await _tax.GetCodesAsync(DefaultCompanyId));

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CreateTaxCode(string code, string name, string? nameEn, string kind, decimal rate, bool isDefault)
		{
			var (ok, err) = await _tax.CreateCodeAsync(DefaultCompanyId, code, name, nameEn, kind, rate, isDefault);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Tax code added"].Value : err;
			return RedirectToAction(nameof(TaxCodes));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> UpdateTaxCode(int id, string name, string? nameEn, decimal rate, bool isDefault)
		{
			var (ok, err) = await _tax.UpdateCodeAsync(DefaultCompanyId, id, name, nameEn, rate, isDefault);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Tax code updated"].Value : err;
			return RedirectToAction(nameof(TaxCodes));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> ToggleTaxCode(int id)
		{
			var (ok, err) = await _tax.ToggleCodeAsync(DefaultCompanyId, id);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["Tax code updated"].Value : err;
			return RedirectToAction(nameof(TaxCodes));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> VatReturns()
		{
			ViewBag.Codes = (await _tax.GetCodesAsync(DefaultCompanyId)).Where(c => c.Kind == "VAT").ToList();
			return View(await _tax.GetReturnsAsync(DefaultCompanyId));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> FileVatReturn(DateTime periodStart, DateTime periodEnd, string? notes)
		{
			var (ok, err, _) = await _tax.FileVatReturnAsync(DefaultCompanyId, periodStart, periodEnd, notes);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The VAT return was filed"].Value : err;
			return RedirectToAction(nameof(VatReturns));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> VatReturnDetail(int id)
		{
			var ret = await _tax.GetReturnAsync(DefaultCompanyId, id);
			if (ret == null) return RedirectToAction(nameof(VatReturns));
			return View(ret);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> SettleVatReturn(int id, DateTime settleDate)
		{
			var (ok, err) = await _tax.SettleVatReturnAsync(DefaultCompanyId, id, settleDate, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The VAT settlement entry was posted"].Value : err;
			return RedirectToAction(nameof(VatReturnDetail), new { id });
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> EtaStatus()
		{
			var settings = await _tax.GetEtaSettingsAsync(DefaultCompanyId);
			ViewBag.Enabled = _eta.IsEnabled(settings);
			ViewBag.Invoices = await _context.SalesInvoices.AsNoTracking()
				.Where(i => i.CompanyID == DefaultCompanyId).OrderByDescending(i => i.ID).Take(50).ToListAsync();
			return View(settings);
		}

		// ===== Phase 8: Financial Statements =====

		[SessionValidation][HttpGet]
		public async Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to)
		{
			var f = from ?? new DateTime(DateTime.Today.Year, 1, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			return View(await _statements.IncomeStatementAsync(DefaultCompanyId, f, t));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> BalanceSheet(DateTime? asOf)
		{
			var d = asOf ?? DateTime.Today;
			ViewBag.AsOf = d;
			return View(await _statements.BalanceSheetAsync(DefaultCompanyId, d));
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> CashFlow(DateTime? from, DateTime? to)
		{
			var f = from ?? new DateTime(DateTime.Today.Year, 1, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			return View(await _statements.CashFlowAsync(DefaultCompanyId, f, t));
		}

		// ===== Phase 9: Year-end Close =====

		[SessionValidation][HttpGet]
		public async Task<IActionResult> YearEndClose()
		{
			ViewBag.Closings = await _closing.GetClosingsAsync(DefaultCompanyId);
			var years = await _closing.GetYearsAsync(DefaultCompanyId);
			var previews = new Dictionary<int, YearClosePreview>();
			foreach (var y in years) previews[y.ID] = await _closing.PreviewAsync(DefaultCompanyId, y.ID);
			ViewBag.Previews = previews;
			return View(years);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> CloseYear(int fiscalYearId)
		{
			var (ok, err) = await _closing.CloseYearAsync(DefaultCompanyId, fiscalYearId, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The fiscal year was closed and the closing entry posted"].Value : err;
			return RedirectToAction(nameof(YearEndClose));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
			[CrossBuy.Models.AccPerm("manage")]
		public async Task<IActionResult> ReopenYear(int closingId)
		{
			var (ok, err) = await _closing.ReopenYearAsync(DefaultCompanyId, closingId, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The year was reopened and the closing entry reversed"].Value : err;
			return RedirectToAction(nameof(YearEndClose));
		}

		// ================= Accounting roles & segregation of duties (1c) =================
		[SessionValidation][HttpGet][AccPerm("manage")]
		public async Task<IActionResult> AccountingRoles()
		{
			ViewBag.Threshold = await ApprovalThresholdAsync();
			var accIsAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Employees = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == DefaultCompanyId && e.IsActive)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = !accIsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName })
				.OrderBy(e => e.FullName).ToList();
			ViewBag.Assignments = (await (from r in _context.AccountingUserRoles.AsNoTracking().Where(r => r.CompanyID == DefaultCompanyId)
										 join e in _context.Employee.AsNoTracking() on r.EmployeeId equals e.ID into ej
										 from e in ej.DefaultIfEmpty()
										 orderby r.ID descending
										 select new { r.ID, r.EmployeeId, e.FullName, e.FullNameEn, r.Role }).ToListAsync())
				.Select(x => new AccRoleAssignmentRow { ID = x.ID, EmployeeId = x.EmployeeId, EmployeeName = x.FullName != null ? (!accIsAr && !string.IsNullOrWhiteSpace(x.FullNameEn) ? x.FullNameEn : x.FullName) : ("#" + x.EmployeeId), Role = x.Role }).ToList();
			return View();
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> AssignAccRole(int employeeId, string role)
		{
			var allowed = new[] { "ChiefAccountant", "Accountant", "Cashier", "Auditor" };
			if (employeeId <= 0 || !allowed.Contains(role)) { TempData["AccErr"] = L["Invalid data"].Value; return RedirectToAction(nameof(AccountingRoles)); }
			bool exists = await _context.AccountingUserRoles.AnyAsync(r => r.CompanyID == DefaultCompanyId && r.EmployeeId == employeeId && r.Role == role);
			if (!exists)
			{
				_context.AccountingUserRoles.Add(new Models.Context.Accounting.AccountingUserRole { CompanyID = DefaultCompanyId, EmployeeId = employeeId, Role = role, CreatedAt = DateTime.UtcNow });
				await _context.SaveChangesAsync();
			}
			TempData["AccMsg"] = L["Role assigned"].Value;
			return RedirectToAction(nameof(AccountingRoles));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> SaveAccSettings(decimal approvalThreshold)
		{
			var s = await _context.AccountingSettings.FirstOrDefaultAsync(x => x.CompanyID == DefaultCompanyId);
			if (s == null) { s = new Models.Context.Accounting.AccountingSettings { CompanyID = DefaultCompanyId, CreatedAt = DateTime.UtcNow }; _context.AccountingSettings.Add(s); }
			s.ApprovalThreshold = approvalThreshold < 0 ? 0 : approvalThreshold;
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["The approval threshold was saved"].Value;
			return RedirectToAction(nameof(AccountingRoles));
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> RemoveAccRole(int id)
		{
			var r = await _context.AccountingUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (r != null) { _context.AccountingUserRoles.Remove(r); await _context.SaveChangesAsync(); }
			TempData["AccMsg"] = L["Role deleted"].Value;
			return RedirectToAction(nameof(AccountingRoles));
		}
	}

	public class JournalListItem
	{
		public int Id { get; set; }
		public string? EntryNo { get; set; }
		public DateTime EntryDate { get; set; }
		public string JournalType { get; set; } = "";
		public string Status { get; set; } = "";
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string? SourceType { get; set; }
		public decimal Total { get; set; }
	}
}
