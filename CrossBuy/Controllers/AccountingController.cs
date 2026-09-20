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
		// D1/CORRECTION-005: the validated company source for remediated actions. See RequestCompanyResolver.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly IAiInsightsService _insights;
		private readonly IExecutiveDashboardService _executive;
		private readonly ICurrencyService _currency;
		private readonly IPricingService _pricing;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public AccountingController(IChartOfAccountsService coa, IJournalEntryService journals,
			IGeneralLedgerService gl, ICostCenterService costCenters, IFiscalPeriodService periods,
			IAccountingPostingService posting, IReceivableService ar, IPayableService ap,
			IAccountingDashboardService dashboard, IBankService banks, IFixedAssetService assets,
			ITaxService tax, IEtaInvoiceService eta, IFinancialStatementService statements, IClosingService closing, CrossDbContext context, IAccountingAccessService access, IAiInsightsService insights, IExecutiveDashboardService executive, ICurrencyService currency, IPricingService pricing, IStringLocalizer<CrossBuy.SharedResources> localizer, CrossBuy.BL.Platform.IRequestCompanyResolver company)
		{
			_coa = coa; _journals = journals; _gl = gl; _costCenters = costCenters; _periods = periods; _posting = posting; _ar = ar; _ap = ap; _dashboard = dashboard; _banks = banks; _assets = assets; _tax = tax; _eta = eta; _statements = statements; _closing = closing; _context = context; _access = access; _insights = insights; _executive = executive; _currency = currency; _pricing = pricing; L = localizer; _company = company;
		}

		// Multi-Currency helpers shared by the create-document screens
		private async Task<List<Models.Context.Accounting.Currency>> CurrencyListAsync() => await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
		private Task<int> FunctionalCurrencyIdAsync() => _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
		private CrossBuy.BL.IProjectService PrjSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IProjectService)) as CrossBuy.BL.IProjectService)!;

		// رؤى ذكية — AI insights (anomaly + cash-flow + inventory), all from the local
		// ML service (no LLM / no API key). Server-rendered; the AI only surfaces findings.
		//
		// SECURITY REMEDIATION — this action used to pass `DefaultCompanyId` (the literal 1) to all three
		// insight calls. Because it is [SessionValidation] and nothing more, ANY authenticated user of ANY
		// company reached it, and six of the tables the service reads carry no Stage-1 global company
		// filter (Account, Vendor, Receipt, Payment, StockBalance, StockMovement) — so for those the
		// constant was the ONLY company control, and company 1's rows were read into this request.
		//
		// Measured, so the severity is not overstated: the OUTBOUND payload did not in fact contain that
		// data, because each payload is assembled by joining against globally-filtered entities (Items,
		// Customers) which return nothing for a mismatched scope. So this was a cross-company READ and a
		// functional break — every non-company-1 user got empty insights — rather than a proven
		// exfiltration. It was one refactor away from becoming one, which is why it is fixed here.
		//
		// The company now comes from IRequestCompanyResolver, the same trusted source the other remediated
		// actions in this controller use, and an unresolved company refuses BEFORE any data is gathered or
		// sent (see AiInsightsService.RequireCompany for the second line of defence).
		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> AiInsights()
		{
			// Resolved, never assumed. The resolver takes no request-supplied company here at all: this
			// action has no company parameter, so there is nothing a caller could offer to be validated.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				// The established refusal pattern for a view-returning action in this controller — the same
				// TempData + redirect the other CORRECTION-005 remediated actions use. The resolver's own
				// reason is deliberately NOT rendered: it names companies, and "your company is 2, the
				// record is company 1" tells a caller that a record exists somewhere they cannot see.
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var vm = new AiInsightsVm { CompanyId = scope.CompanyId };

			// EACH CAPABILITY IS EVALUATED INDEPENDENTLY. Previously one try/catch wrapped all three and
			// one `ServiceDown` flag described them together, so a single failing call blanked the two
			// that had worked. They are separate models over separate data; they fail separately too.
			var anomaly = await ReadInsightAsync<AnomalyResult>(
				() => _insights.ScanJournalAnomaliesAsync(scope.CompanyId), scope.CompanyId,
				r => r.Scanned);
			vm.Anomaly = anomaly.Result;
			vm.AnomalyPanel = anomaly.Panel;

			var cashflow = await ReadInsightAsync<CashflowResult>(
				() => _insights.ForecastCashflowAsync(scope.CompanyId, CashflowHorizonDays), scope.CompanyId,
				// A projection with no periods is a projection of nothing: the horizon produced no weeks
				// to report, which is an input problem rather than a clean forecast.
				r => r.Periods.Count);
			vm.Cashflow = cashflow.Result;
			vm.CashflowPanel = cashflow.Panel;

			var inventory = await ReadInsightAsync<InventoryResult>(
				() => _insights.AnalyzeInventoryAsync(scope.CompanyId, InventorySlowDays), scope.CompanyId,
				r => r.ItemsAnalyzed);
			vm.Inventory = inventory.Result;
			vm.InventoryPanel = inventory.Panel;

			return View(vm);
		}

		private const int CashflowHorizonDays = 90;
		private const int InventorySlowDays = 90;

		/// <summary>Runs one local-ML insight and maps it onto a state the page can render honestly.</summary>
		/// <remarks>
		/// The CLASSIFICATION lives in AiInsightMapper, which is pure and directly tested. This method
		/// owns only the parts that cannot be pure: making the call and turning transport faults into the
		/// same vocabulary.
		///
		/// NOTHING FROM THE SERVICE REACHES THE USER AS TEXT. Every Detail is a constant chosen here; the
		/// upstream payload and any exception stay out of the view. The previous version assigned the raw
		/// response JSON and `ex.Message` straight into the model, putting service internals — and
		/// potentially the business rows the payload was built from — on a user's screen.
		/// </remarks>
		private async Task<(T? Result, AiInsightPanel Panel)> ReadInsightAsync<T>(
			Func<Task<AiProxyResult>> call, int companyId, Func<T, int> recordsConsidered) where T : class
		{
			var nowUtc = DateTime.UtcNow;
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

			try
			{
				var response = await call();
				if (response.Status != 200)
					return (null, AiInsightMapper.Map(response.Status, null, companyId, nowUtc));

				var parsed = JsonSerializer.Deserialize<T>(response.Json, opts);
				if (parsed == null)
					return (null, AiInsightMapper.Failed("ai-insight:unreadable-response", companyId, nowUtc));

				return (parsed, AiInsightMapper.Map(200, recordsConsidered(parsed), companyId, nowUtc));
			}
			catch (JsonException)
			{
				return (null, AiInsightMapper.Failed("ai-insight:unreadable-response", companyId, nowUtc));
			}
			catch (HttpRequestException)
			{
				// The local ML service is not running. Recoverable by an operator, and honestly "we could
				// not ask" rather than "the answer is no".
				return (null, AiInsightMapper.Unavailable("ai-insight:service-unavailable", companyId, nowUtc));
			}
			catch (TaskCanceledException)
			{
				// The HttpClient timeout surfaces as a cancellation, not an HttpRequestException. A slow
				// service is an unavailable one from the reader's side.
				return (null, AiInsightMapper.Unavailable("ai-insight:service-timeout", companyId, nowUtc));
			}
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
			// CORRECTION-005 — the company is RESOLVED, never the compile-time constant. The screen above
			// this action is company-scoped, so a constant here meant a company-2 accountant either saw
			// company 1 or saw nothing, depending only on which ids happened to exist.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			// The company predicate is IN THE QUERY, so a foreign invoice is not found rather than found
			// and then refused. Missing and inaccessible therefore return the SAME redirect, and a caller
			// cannot use the difference to learn that an invoice exists in another company.
			var inv = await _context.SalesInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == scope.CompanyId);
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
		//
		// STAGE 1 BATCH D1 WAVE 1 — CRITICAL. What this action could do before remediation: ANY signed-in
		// employee could rewrite the legal beneficiary NAME and TAX NUMBER on any posted sales invoice in
		// company 1. `SessionValidation` proves a session exists and `ValidateAntiForgeryToken` proves the form
		// came from our page — neither is authorization, and the company came from the compile-time constant
		// `DefaultCompanyId`, not from the caller. The result is alteration of a printed statutory tax document.
		//
		// Two things were added, and deliberately only two:
		//
		//   1. AccPerm("post") — the SAME right that already governs journals and sales/purchase invoices in
		//      AccountingAccessService. Stamping the beneficiary of an issued invoice is an invoice mutation, so
		//      it takes the invoice-mutation right rather than a new vocabulary invented for one action.
		//   2. The company from the resolved BusinessContext (CORRECTION-005), replacing DefaultCompanyId.
		//
		// The statutory POLICY was already correct and is untouched: OfficialInvoiceHelper.StampCustomer refuses
		// a second stamp (SET-ONCE — a printed document's beneficiary cannot change once issued) and refuses any
		// invoice carrying tax (TAX-ZERO ONLY — a taxed invoice's beneficiary must be the ledger account holder).
		// It also already records the ACTOR and the timestamp. So no correction workflow was invented here; the
		// missing controls were authorization and company, and those are what changed.
		//
		// NOT SUPPORTED BY THE MODEL, reported rather than invented: SalesInvoice has no "reason" column for a
		// beneficiary change, so the brief's "record the reason" cannot be satisfied without a schema change.
		// Declared in the Wave 1 report as a gap, not silently skipped.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> StampInvoiceCustomer(int id, string name, string? taxNo)
		{
			// The company is resolved, never assumed. An unresolved identity writes nothing.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(SalesInvoices));
			}

			// Company comes from the RESOLVED scope, and the invoice row is the ownership authority. An invoice
			// in another company answers exactly like one that does not exist — the id cannot be probed.
			var inv = await _context.SalesInvoices.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == scope.CompanyId);
			if (inv == null) { TempData["AccErr"] = L["Sales invoice not found"].Value; return RedirectToAction(nameof(SalesInvoices)); }
			// The actor recorded is the RESOLVED employee, not the session-parsed one.
			var (ok, err) = CrossBuy.BL.OfficialInvoiceHelper.StampCustomer(inv, name, taxNo, scope.EmployeeId?.ToString());
			if (!ok) { TempData["AccErr"] = err; return RedirectToAction(nameof(PrintInvoice), new { id }); }
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["The invoice beneficiary was stamped"].Value;
			return RedirectToAction(nameof(PrintInvoice), new { id });
		}

		// تفاصيل فاتورة شراء
		[SessionValidation][HttpGet]
		public async Task<IActionResult> PurchaseInvoiceDetail(int id)
		{
			// Same remediation and same reasoning as SalesInvoiceDetail above.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var inv = await _context.PurchaseInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == scope.CompanyId);
			if (inv == null) return RedirectToAction(nameof(PurchaseInvoices));
			ViewBag.Vendor = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.ID == inv.VendorId);
			return View(inv);
		}

		// كشف حساب عميل
		[SessionValidation][HttpGet]
		public async Task<IActionResult> CustomerStatement(int id, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 50)
		{
			// RESOLVED, not the compile-time constant: this screen is company-scoped, and the constant is
			// what made a company-2 accountant either see company 1 or see nothing.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Index)); }

			// Reached from the menu with no party: ask for one instead of bouncing to another list.
			if (id <= 0)
			{
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				ViewBag.PartyPicker = (await _context.Customers.AsNoTracking()
					.Where(x => x.CompanyID == scope.CompanyId && x.IsActive)
					.OrderBy(x => x.Name).Select(x => new { x.ID, x.Name, x.NameEn }).ToListAsync())
					.Select(x => new PartyPickItem(x.ID, ar ? x.Name : DisplayName.Or(x.NameEn, x.Name))).ToList();
				return View("PartyStatementPicker");
			}

			var c = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == scope.CompanyId);
			if (c == null) return RedirectToAction(nameof(Customers));

			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Customer = c;
			return View(await BuildPartyStatementAsync(scope.CompanyId, id, isCustomer: true,
				partyName: isAr ? c.Name : DisplayName.Or(c.NameEn, c.Name), from: from, to: to,
				page: page, pageSize: pageSize));
		}

		// ================================================================================================
		// MOVEMENT SUMMARY — one screen that holds a whole movement.
		//
		// Until now a movement was spread over four detail screens and two that did not exist at all: a
		// receipt and a payment have only a LIST, and neither the returns nor the invoices showed the
		// journal entries they produced. So "what actually happened here" meant opening three screens and
		// holding the answer in your head.
		//
		// This is one screen for every kind: the document, the settlements against it, every journal entry
		// it produced one under the other, and the timeline.
		//
		// THE KIND IS THE JOURNAL'S OWN SourceType, not a new vocabulary. Those values are what the posting
		// services already write (SalesInvoice, Receipt, PurchaseInvoice, SalesReturn, PurchaseReturn,
		// Payment), so the entries are found by the link that already exists rather than by a second one
		// invented here. Anything outside that list is refused rather than guessed at.
		// ================================================================================================

		/// One labelled fact on the header strip. A LIST rather than fifteen nullable properties because the
		/// facts a movement actually has differ by kind — a receipt has a method and a cash account, a return
		/// has a warehouse and an original invoice — and a list shows exactly the ones that exist.
		public sealed class MovementFact
		{
			public string Label { get; init; } = "";
			public string Value { get; init; } = "";
			public string? Url { get; init; }
			public bool Mono { get; init; }          // render as a code badge (document numbers, codes)
		}

		public sealed class MovementLine
		{
			public int No { get; init; }
			public string Description { get; init; } = "";
			public string? ItemCode { get; init; }
			public string? Unit { get; init; }
			public string? Warehouse { get; init; }
			public string? CostCenter { get; init; }
			public decimal Qty { get; init; }
			public decimal UnitPrice { get; init; }
			public decimal Discount { get; init; }
			public decimal TaxRate { get; init; }
			public decimal Total { get; init; }
		}

		public sealed class MovementJournalLine
		{
			public string AccountCode { get; init; } = "";
			public string AccountName { get; init; } = "";
			public string? CostCenter { get; init; }
			public string? Project { get; init; }
			public decimal Debit { get; init; }
			public decimal Credit { get; init; }
			public string? Note { get; init; }
		}

		public sealed class MovementJournal
		{
			public int Id { get; init; }
			public string? EntryNo { get; init; }
			public DateTime Date { get; init; }
			public string Status { get; init; } = "";
			public string JournalType { get; init; } = "";
			public string? Description { get; init; }
			public string? CreatedByName { get; init; }
			public string? CreatedByPhoto { get; init; }
			public DateTime? CreatedAt { get; init; }
			public string? PostedByName { get; init; }
			public DateTime? PostedAt { get; init; }
			public int? ReversedByEntryId { get; init; }
			public string? ReversedByNo { get; init; }
			public string? Url { get; init; }
			public List<MovementJournalLine> Lines { get; init; } = new();
			public decimal Total => Lines.Sum(l => l.Debit);
			public decimal TotalCredit => Lines.Sum(l => l.Credit);
			public bool Balanced => Total == TotalCredit;
		}

		/// A settlement seen from either side: on an invoice it is the receipt that paid it, on a receipt
		/// it is the invoice it went against.
		public sealed class MovementSettlement
		{
			public string Kind { get; init; } = "";
			public int Id { get; init; }
			public string DocNo { get; init; } = "";
			public DateTime Date { get; init; }
			public decimal Amount { get; init; }
			public string? Url { get; init; }
			public string? Label { get; init; }      // used by the related-documents list
		}

		public sealed class MovementSummaryModel
		{
			public string Kind { get; init; } = "";
			public string KindLabel { get; init; } = "";
			public int Id { get; init; }
			public string DocNo { get; init; } = "";
			public DateTime Date { get; init; }
			public string Status { get; init; } = "";
			public string PartyName { get; init; } = "";
			public int? PartyId { get; init; }
			public bool PartyIsCustomer { get; init; }
			public string? Notes { get; init; }
			public decimal SubTotal { get; init; }
			public decimal TaxTotal { get; init; }
			public decimal GrandTotal { get; init; }
			public decimal DiscountTotal { get; init; }
			public string? CurrencyCode { get; init; }
			public decimal? ExchangeRate { get; init; }
			public decimal? GrandTotalBase { get; init; }
			public bool IsMoneyDoc { get; init; }            // receipt/payment: no lines, no tax, no discount
			public List<MovementFact> Facts { get; init; } = new();
			public List<MovementLine> Lines { get; init; } = new();
			public List<MovementSettlement> Settlements { get; init; } = new();
			public List<MovementSettlement> Related { get; init; } = new();
			public List<MovementJournal> Journals { get; init; } = new();
			public string? TimelineCode { get; init; }      // an EntityRegistry code, when the family has one
			public decimal SettledAmount => Settlements.Sum(s => s.Amount);
			public decimal Outstanding => GrandTotal - SettledAmount;
		}

		/// The kinds this screen understands, mapped to the registry code whose timeline it can show.
		/// A kind with no registry family still gets every other section — the timeline is simply absent,
		/// which is the honest state rather than an empty widget.
		private static readonly Dictionary<string, string?> MovementKinds = new(StringComparer.Ordinal)
		{
			["SalesInvoice"] = CrossBuy.BL.Platform.EntityRegistry.SalesInvoice,
			["PurchaseInvoice"] = CrossBuy.BL.Platform.EntityRegistry.PurchaseInvoice,
			["SalesReturn"] = CrossBuy.BL.Platform.EntityRegistry.SalesReturn,
			["PurchaseReturn"] = CrossBuy.BL.Platform.EntityRegistry.PurchaseReturn,
			["Receipt"] = CrossBuy.BL.Platform.EntityRegistry.Receipt,
			["Payment"] = CrossBuy.BL.Platform.EntityRegistry.Payment,
		};

		// ملخص الحركة — the whole movement on one screen.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> MovementSummary(string kind, int id)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Index)); }
			if (string.IsNullOrWhiteSpace(kind) || !MovementKinds.ContainsKey(kind) || id <= 0) return NotFound();

			int co = scope.CompanyId;
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			MovementSummaryModel? m = null;
			var settlements = new List<MovementSettlement>();
			var facts = new List<MovementFact>();
			var related = new List<MovementSettlement>();

			// ---- the lookups every kind may need, read once ------------------------------------------
			var curs = await _context.Currencies.AsNoTracking().ToDictionaryAsync(c => c.ID, c => c.Code);
			string? CurCode(int? cid) => cid != null && curs.TryGetValue(cid.Value, out var c) ? c : null;
			var whs = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == co)
				.ToDictionaryAsync(w => w.ID, w => isAr ? w.Name : DisplayName.Or(w.NameEn, w.Name));
			var prjs = await _context.Projects.AsNoTracking().Where(p => p.CompanyID == co)
				.ToDictionaryAsync(p => p.ID, p => isAr ? p.Name : DisplayName.Or(p.NameEn, p.Name));
			var ccs = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == co)
				.ToDictionaryAsync(c => c.ID, c => isAr ? c.Name : DisplayName.Or(c.NameEn, c.Name));
			var uoms = await _context.UnitsOfMeasure.AsNoTracking().Where(u => u.CompanyID == co)
				.ToDictionaryAsync(u => u.ID, u => isAr ? u.Name : DisplayName.Or(u.NameEn, u.Name));

			// THE ITEM'S OWN NAME, because the LINE has no English one on three of the six kinds.
			// SalesInvoiceLine and PurchaseInvoiceLine carry ItemDescriptionEn; SalesReturnLine,
			// PurchaseReturnLine and the money documents do not — so on an English screen a return line
			// printed the Arabic description it was stored with. The item master is where the English
			// name actually lives (PurchaseReturnDetail already resolves it this way), and it is the
			// FALLBACK, not the override: a line description the user typed still wins over the catalogue.
			var items = isAr ? new Dictionary<int, string>()
				: await _context.Items.AsNoTracking().Where(i => i.CompanyID == co && i.NameEn != null && i.NameEn != "")
					.ToDictionaryAsync(i => i.ID, i => i.NameEn!);

			string? Look(Dictionary<int, string> d, int? k) => k != null && d.TryGetValue(k.Value, out var v) ? v : null;
			// Arabic UI: the stored description, always. Otherwise: the line's own English text if it has
			// one, else the item's English name, else the stored description — never a blank.
			string LineText(string? en, string stored, int? itemId) =>
				isAr ? stored
				     : !string.IsNullOrWhiteSpace(en) ? en!
				     : (itemId != null && items.TryGetValue(itemId.Value, out var n) ? n : stored);
			void Fact(string label, string? value, string? url = null, bool mono = false)
			{ if (!string.IsNullOrWhiteSpace(value)) facts.Add(new MovementFact { Label = label, Value = value!, Url = url, Mono = mono }); }

			// The party's own identity fields, added by every branch that has a party.
			void PartyFacts(string? phone, string? taxNo, string? email, int? termDays)
			{
				Fact(L["Phone"].Value, phone);
				Fact(L["Tax registration number"].Value, taxNo, null, true);
				Fact(L["Email"].Value, email);
				if (termDays is > 0) Fact(L["Payment terms"].Value, string.Format(isAr ? "{0} يوم" : "{0} days", termDays));
			}

			// The money facts shared by every kind: currency, rate and the base-currency figure when the
			// document is not in the base currency. A rate of 1 says nothing, so it is not shown.
			void MoneyFacts(int? currencyId, decimal? rate, decimal? baseTotal)
			{
				Fact(L["Currency"].Value, CurCode(currencyId), null, true);
				if (rate is > 0 && rate != 1m)
				{
					Fact(L["Exchange rate"].Value, rate.Value.ToString("0.####"));
					if (baseTotal is > 0) Fact(L["Amount in base currency"].Value, baseTotal.Value.ToString("N2"));
				}
			}

			if (kind == "SalesInvoice")
			{
				var d = await _context.SalesInvoices.AsNoTracking().Include(x => x.Lines)
					.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var cu = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.CustomerId);
				// The receipts that settled it, through the allocation table rather than by guessing at dates.
				settlements = await (from a in _context.ReceiptAllocations.AsNoTracking()
									 join r in _context.Receipts.AsNoTracking() on a.ReceiptId equals r.ID
									 where a.CompanyID == co && a.SalesInvoiceId == id
									 select new MovementSettlement
									 {
										 Kind = "Receipt", Id = r.ID, DocNo = r.ReceiptNo ?? ("#" + r.ID),
										 Date = r.ReceiptDate, Amount = a.ForeignAmount,
									 }).ToListAsync();
				// What came back against it — a return is part of the same movement, not a separate story.
				related = await _context.SalesReturns.AsNoTracking()
					.Where(r => r.CompanyID == co && r.OriginalInvoiceId == id)
					.Select(r => new MovementSettlement
					{
						Kind = "SalesReturn", Id = r.ID, DocNo = r.ReturnNo ?? ("#" + r.ID),
						Date = r.ReturnDate, Amount = r.GrandTotal, Label = L["Sales return"].Value,
					}).ToListAsync();

				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.GrandTotalBase);
				Fact(L["Project"].Value, Look(prjs, d.ProjectId));
				Fact(L["ETA status"].Value, d.EtaStatus);
				Fact(L["ETA UUID"].Value, d.EtaUuid, null, true);
				if (!string.IsNullOrWhiteSpace(d.CustomerNameOverride))
					Fact(L["Invoice beneficiary"].Value, d.CustomerNameOverride);
				PartyFacts(cu?.Phone, cu?.TaxRegNo, cu?.Email, cu?.PaymentTermsDays);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Sales invoice"].Value, Id = d.ID,
					DocNo = d.InvoiceNo ?? ("#" + d.ID), Date = d.InvoiceDate, Status = d.Status,
					PartyName = cu == null ? "" : (isAr ? cu.Name : DisplayName.Or(cu.NameEn, cu.Name)),
					PartyId = d.CustomerId, PartyIsCustomer = true, Notes = d.Notes,
					SubTotal = d.SubTotal, TaxTotal = d.TaxTotal, GrandTotal = d.GrandTotal,
					DiscountTotal = d.Lines.Sum(l => l.DiscountAmount),
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.GrandTotalBase,
					Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new MovementLine
					{
						No = l.LineNo,
						Description = LineText(l.ItemDescriptionEn, l.ItemDescription, l.ItemId),
						ItemCode = l.ItemCode, Unit = Look(uoms, l.UoMId), Warehouse = Look(whs, l.WarehouseId),
						Qty = l.Qty, UnitPrice = l.UnitPrice, Discount = l.DiscountAmount, TaxRate = l.TaxRate, Total = l.LineTotal,
					}).ToList(),
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}
			else if (kind == "PurchaseInvoice")
			{
				var d = await _context.PurchaseInvoices.AsNoTracking().Include(x => x.Lines)
					.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var ve = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.VendorId);
				settlements = await (from a in _context.PaymentAllocations.AsNoTracking()
									 join p in _context.Payments.AsNoTracking() on a.PaymentId equals p.ID
									 where a.CompanyID == co && a.PurchaseInvoiceId == id
									 select new MovementSettlement
									 {
										 Kind = "Payment", Id = p.ID, DocNo = p.PaymentNo ?? ("#" + p.ID),
										 Date = p.PaymentDate, Amount = a.ForeignAmount,
									 }).ToListAsync();
				related = await _context.PurchaseReturns.AsNoTracking()
					.Where(r => r.CompanyID == co && r.OriginalInvoiceId == id)
					.Select(r => new MovementSettlement
					{
						Kind = "PurchaseReturn", Id = r.ID, DocNo = r.ReturnNo ?? ("#" + r.ID),
						Date = r.ReturnDate, Amount = r.GrandTotal, Label = L["Purchase return"].Value,
					}).ToListAsync();

				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.GrandTotalBase);
				Fact(L["Project"].Value, Look(prjs, d.ProjectId));
				PartyFacts(ve?.Phone, ve?.TaxRegNo, ve?.Email, ve?.PaymentTermsDays);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Purchase Invoice"].Value, Id = d.ID,
					DocNo = d.InvoiceNo ?? ("#" + d.ID), Date = d.InvoiceDate, Status = d.Status,
					PartyName = ve == null ? "" : (isAr ? ve.Name : DisplayName.Or(ve.NameEn, ve.Name)),
					PartyId = d.VendorId, PartyIsCustomer = false, Notes = d.Notes,
					SubTotal = d.SubTotal, TaxTotal = d.TaxTotal, GrandTotal = d.GrandTotal,
					DiscountTotal = d.Lines.Sum(l => l.DiscountAmount),
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.GrandTotalBase,
					Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new MovementLine
					{
						No = l.LineNo,
						Description = LineText(l.ItemDescriptionEn, l.ItemDescription, l.ItemId),
						Warehouse = Look(whs, l.WarehouseId), CostCenter = Look(ccs, l.CostCenterId),
						Qty = l.Qty, UnitPrice = l.UnitPrice, Discount = l.DiscountAmount, TaxRate = l.TaxRate, Total = l.LineTotal,
					}).ToList(),
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}
			else if (kind == "SalesReturn")
			{
				var d = await _context.SalesReturns.AsNoTracking().Include(x => x.Lines)
					.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var cu = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.CustomerId);
				// The invoice it came back against — the first thing anyone opening a return asks for.
				if (d.OriginalInvoiceId != null)
				{
					var oi = await _context.SalesInvoices.AsNoTracking()
						.Where(i => i.ID == d.OriginalInvoiceId && i.CompanyID == co)
						.Select(i => new { i.ID, i.InvoiceNo, i.InvoiceDate, i.GrandTotal }).FirstOrDefaultAsync();
					if (oi != null) related.Add(new MovementSettlement
					{
						Kind = "SalesInvoice", Id = oi.ID, DocNo = oi.InvoiceNo ?? ("#" + oi.ID),
						Date = oi.InvoiceDate, Amount = oi.GrandTotal, Label = L["Original invoice"].Value,
					});
				}

				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.GrandTotalBase);
				Fact(L["Warehouse"].Value, Look(whs, d.WarehouseId));
				PartyFacts(cu?.Phone, cu?.TaxRegNo, cu?.Email, cu?.PaymentTermsDays);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Sales return"].Value, Id = d.ID,
					DocNo = d.ReturnNo ?? ("#" + d.ID), Date = d.ReturnDate, Status = d.Status,
					PartyName = cu == null ? "" : (isAr ? cu.Name : DisplayName.Or(cu.NameEn, cu.Name)),
					PartyId = d.CustomerId, PartyIsCustomer = true, Notes = d.Notes,
					SubTotal = d.SubTotal, TaxTotal = d.TaxTotal, GrandTotal = d.GrandTotal,
					DiscountTotal = d.Lines.Sum(l => l.DiscountAmount),
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.GrandTotalBase,
					Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new MovementLine
					{
						No = l.LineNo, Description = LineText(null, l.ItemDescription, l.ItemId), Warehouse = Look(whs, l.WarehouseId),
						Qty = l.Qty, UnitPrice = l.UnitPrice,
						Discount = l.DiscountAmount, TaxRate = l.TaxRate, Total = l.LineTotal,
					}).ToList(),
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}
			else if (kind == "PurchaseReturn")
			{
				var d = await _context.PurchaseReturns.AsNoTracking().Include(x => x.Lines)
					.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var ve = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.VendorId);
				if (d.OriginalInvoiceId != null)
				{
					var oi = await _context.PurchaseInvoices.AsNoTracking()
						.Where(i => i.ID == d.OriginalInvoiceId && i.CompanyID == co)
						.Select(i => new { i.ID, i.InvoiceNo, i.InvoiceDate, i.GrandTotal }).FirstOrDefaultAsync();
					if (oi != null) related.Add(new MovementSettlement
					{
						Kind = "PurchaseInvoice", Id = oi.ID, DocNo = oi.InvoiceNo ?? ("#" + oi.ID),
						Date = oi.InvoiceDate, Amount = oi.GrandTotal, Label = L["Original invoice"].Value,
					});
				}

				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.GrandTotalBase);
				Fact(L["Warehouse"].Value, Look(whs, d.WarehouseId));
				PartyFacts(ve?.Phone, ve?.TaxRegNo, ve?.Email, ve?.PaymentTermsDays);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Purchase return"].Value, Id = d.ID,
					DocNo = d.ReturnNo ?? ("#" + d.ID), Date = d.ReturnDate, Status = d.Status,
					PartyName = ve == null ? "" : (isAr ? ve.Name : DisplayName.Or(ve.NameEn, ve.Name)),
					PartyId = d.VendorId, PartyIsCustomer = false, Notes = d.Notes,
					SubTotal = d.SubTotal, TaxTotal = d.TaxTotal, GrandTotal = d.GrandTotal,
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.GrandTotalBase,
					Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new MovementLine
					{
						No = l.LineNo, Description = LineText(null, l.ItemDescription, l.ItemId), Warehouse = Look(whs, l.WarehouseId),
						Qty = l.Qty, UnitPrice = l.UnitCost,
						Discount = 0, TaxRate = l.TaxRate, Total = l.LineTotal,
					}).ToList(),
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}
			else if (kind == "Receipt")
			{
				var d = await _context.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var cu = d.CustomerId == null ? null : await _context.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.CustomerId);
				// Seen from the other side: the invoices this receipt went against.
				settlements = await (from a in _context.ReceiptAllocations.AsNoTracking()
									 join i in _context.SalesInvoices.AsNoTracking() on a.SalesInvoiceId equals i.ID
									 where a.CompanyID == co && a.ReceiptId == id
									 select new MovementSettlement
									 {
										 Kind = "SalesInvoice", Id = i.ID, DocNo = i.InvoiceNo ?? ("#" + i.ID),
										 Date = i.InvoiceDate, Amount = a.ForeignAmount,
									 }).ToListAsync();

				var cash = await _context.Accounts.AsNoTracking().Where(a => a.ID == d.CashAccountId)
					.Select(a => new { a.Code, a.Name, a.NameEn }).FirstOrDefaultAsync();
				Fact(L["Payment method"].Value, CrossBuy.BL.Platform.MoneyMethodNames.For(d.Method, isAr));
				if (cash != null) Fact(L["Cash/bank account"].Value,
					cash.Code + " — " + (isAr ? cash.Name : DisplayName.Or(cash.NameEn, cash.Name)));
				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.AmountBase);
				PartyFacts(cu?.Phone, cu?.TaxRegNo, cu?.Email, null);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Receipt"].Value, Id = d.ID,
					DocNo = d.ReceiptNo ?? ("#" + d.ID), Date = d.ReceiptDate, Status = d.Status,
					PartyName = cu == null ? "" : (isAr ? cu.Name : DisplayName.Or(cu.NameEn, cu.Name)),
					PartyId = d.CustomerId, PartyIsCustomer = true, Notes = d.Notes,
					SubTotal = d.Amount, TaxTotal = 0, GrandTotal = d.Amount,
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.AmountBase,
					IsMoneyDoc = true,
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}
			else // Payment
			{
				var d = await _context.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
				if (d == null) return NotFound();
				var ve = d.VendorId == null ? null : await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.VendorId);
				settlements = await (from a in _context.PaymentAllocations.AsNoTracking()
									 join i in _context.PurchaseInvoices.AsNoTracking() on a.PurchaseInvoiceId equals i.ID
									 where a.CompanyID == co && a.PaymentId == id
									 select new MovementSettlement
									 {
										 Kind = "PurchaseInvoice", Id = i.ID, DocNo = i.InvoiceNo ?? ("#" + i.ID),
										 Date = i.InvoiceDate, Amount = a.ForeignAmount,
									 }).ToListAsync();

				var cash = await _context.Accounts.AsNoTracking().Where(a => a.ID == d.CashAccountId)
					.Select(a => new { a.Code, a.Name, a.NameEn }).FirstOrDefaultAsync();
				Fact(L["Payment method"].Value, CrossBuy.BL.Platform.MoneyMethodNames.For(d.Method, isAr));
				if (cash != null) Fact(L["Cash/bank account"].Value,
					cash.Code + " — " + (isAr ? cash.Name : DisplayName.Or(cash.NameEn, cash.Name)));
				MoneyFacts(d.CurrencyId, d.ExchangeRate, d.AmountBase);
				PartyFacts(ve?.Phone, ve?.TaxRegNo, ve?.Email, null);
				if (d.CreatedAt != null) Fact(L["Created at"].Value, d.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm"));

				m = new MovementSummaryModel
				{
					Kind = kind, KindLabel = L["Payment"].Value, Id = d.ID,
					DocNo = d.PaymentNo ?? ("#" + d.ID), Date = d.PaymentDate, Status = d.Status,
					PartyName = ve == null ? "" : (isAr ? ve.Name : DisplayName.Or(ve.NameEn, ve.Name)),
					PartyId = d.VendorId, PartyIsCustomer = false, Notes = d.Notes,
					SubTotal = d.Amount, TaxTotal = 0, GrandTotal = d.Amount,
					CurrencyCode = CurCode(d.CurrencyId), ExchangeRate = d.ExchangeRate, GrandTotalBase = d.AmountBase,
					IsMoneyDoc = true,
					TimelineCode = MovementKinds[kind],
					Facts = facts,
				};
			}

			// Each settlement gets a link back into this same screen, so the chain can be walked in both
			// directions without ever leaving it.
			foreach (var s in settlements)
				m.Settlements.Add(new MovementSettlement
				{
					Kind = s.Kind, Id = s.Id, DocNo = s.DocNo, Date = s.Date, Amount = s.Amount,
					// Back into this same screen, so the chain walks in both directions without leaving it.
					Url = Url.Action(nameof(MovementSummary), new { kind = s.Kind, id = s.Id }),
				});

			foreach (var r in related)
				m.Related.Add(new MovementSettlement
				{
					Kind = r.Kind, Id = r.Id, DocNo = r.DocNo, Date = r.Date, Amount = r.Amount, Label = r.Label,
					Url = Url.Action(nameof(MovementSummary), new { kind = r.Kind, id = r.Id }),
				});

			// EVERY JOURNAL THIS MOVEMENT PRODUCED, in order, each with its own lines. The link is the
			// posting services' own SourceType/SourceId — the same pair the reversal and the audit use.
			var jes = await _context.JournalEntries.AsNoTracking().Include(e => e.Lines)
				.Where(e => e.CompanyID == co && e.SourceType == kind && e.SourceId == id)
				.OrderBy(e => e.EntryDate).ThenBy(e => e.ID).ToListAsync();

			var acctIds = jes.SelectMany(e => e.Lines).Select(l => l.AccountId).Distinct().ToList();
			var accts = await _context.Accounts.AsNoTracking().Where(a => acctIds.Contains(a.ID))
				.Select(a => new { a.ID, a.Code, a.Name, a.NameEn }).ToDictionaryAsync(a => a.ID);

			// Who made it and who posted it — CreatedBy/PostedBy hold Employee ids, so the photo the owner
			// asked for on the timeline is available on the journal card too.
			var empIds = jes.SelectMany(e => new[] { e.CreatedBy, e.PostedBy }).Where(x => x != null)
				.Select(x => x!.Value).Distinct().ToList();
			var emps = await _context.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.ProfileImage }).ToDictionaryAsync(e => e.ID);

			// A reversed entry names the entry that reversed it; show its number, not a bare id.
			var revIds = jes.Where(e => e.ReversedByEntryId != null).Select(e => e.ReversedByEntryId!.Value).Distinct().ToList();
			var revNos = revIds.Count == 0 ? new Dictionary<int, string?>()
				: await _context.JournalEntries.AsNoTracking().Where(e => revIds.Contains(e.ID))
					.ToDictionaryAsync(e => e.ID, e => e.EntryNo);

			foreach (var e in jes)
			{
				string? photo = null, createdBy = null, postedBy = null;
				if (e.CreatedBy != null && emps.TryGetValue(e.CreatedBy.Value, out var ce))
				{
					createdBy = ce.FullName;
					if (!string.IsNullOrWhiteSpace(ce.ProfileImage)) photo = Url.Content("~" + ce.ProfileImage!.Replace("\\", "/"));
				}
				if (e.PostedBy != null && emps.TryGetValue(e.PostedBy.Value, out var pe)) postedBy = pe.FullName;

				m.Journals.Add(new MovementJournal
				{
					Id = e.ID, EntryNo = e.EntryNo, Date = e.EntryDate, Status = e.Status, JournalType = e.JournalType,
					Description = isAr ? e.Description : DisplayName.Or(e.DescriptionEn, e.Description),
					CreatedByName = createdBy, CreatedByPhoto = photo, CreatedAt = e.CreatedAt,
					PostedByName = postedBy, PostedAt = e.PostedAt,
					ReversedByEntryId = e.ReversedByEntryId,
					ReversedByNo = e.ReversedByEntryId != null && revNos.TryGetValue(e.ReversedByEntryId.Value, out var rn) ? rn : null,
					Url = Url.Action(nameof(JournalEntry), new { id = e.ID }),
					Lines = e.Lines.OrderBy(l => l.LineNo).Select(l => new MovementJournalLine
					{
						AccountCode = accts.TryGetValue(l.AccountId, out var a) ? (a.Code ?? "") : "",
						AccountName = accts.TryGetValue(l.AccountId, out var a2) ? (isAr ? a2.Name : DisplayName.Or(a2.NameEn, a2.Name)) : "",
						CostCenter = Look(ccs, l.CostCenterId), Project = Look(prjs, l.ProjectId),
						Debit = l.Debit, Credit = l.Credit,
						Note = isAr ? l.Description : DisplayName.Or(l.DescriptionEn, l.Description),
					}).ToList(),
				});
			}

			return View(m);
		}


		// كشف حساب مورد
		[SessionValidation][HttpGet]
		public async Task<IActionResult> VendorStatement(int id, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 50)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Index)); }

			if (id <= 0)
			{
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				ViewBag.PartyPicker = (await _context.Vendors.AsNoTracking()
					.Where(x => x.CompanyID == scope.CompanyId && x.IsActive)
					.OrderBy(x => x.Name).Select(x => new { x.ID, x.Name, x.NameEn }).ToListAsync())
					.Select(x => new PartyPickItem(x.ID, ar ? x.Name : DisplayName.Or(x.NameEn, x.Name))).ToList();
				ViewBag.PickerIsVendor = true;
				return View("PartyStatementPicker");
			}

			var v = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == scope.CompanyId);
			if (v == null) return RedirectToAction(nameof(Vendors));

			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Vendor = v;
			return View(await BuildPartyStatementAsync(scope.CompanyId, id, isCustomer: false,
				partyName: isAr ? v.Name : DisplayName.Or(v.NameEn, v.Name), from: from, to: to,
				page: page, pageSize: pageSize));
		}

		// ================================================================================================
		// THE PARTY STATEMENT — every movement on one customer or one vendor.
		//
		// The pair of actions this replaces fetched two document types and nothing else, so a customer who
		// returned goods saw a balance that DISAGREED WITH THE LEDGER, and neither query carried a company
		// predicate: Receipts, Payments, SalesReturns and PurchaseReturns have no global company filter
		// either (only SalesInvoice, PurchaseInvoice and Customer do), so a receipt belonging to another
		// company was read whenever the party id happened to exist there.
		//
		// WHY THIS IS BUILT FROM DOCUMENTS AND NOT FROM THE LEDGER — the question asked first, because a
		// ledger-driven statement is the textbook answer:
		//   JournalEntryLine carries AccountId, CostCenterId, ProjectId and EmployeeId. It carries NO
		//   customer and NO vendor. Customer.ControlAccountId is a CONTROL account shared by every customer,
		//   so filtering the ledger by it returns all of them together. There is no subsidiary ledger in
		//   this schema, so the documents ARE the only per-party record that exists.
		//   The consequence is stated rather than hidden: a manual journal posted straight to the control
		//   account cannot appear on any one party's statement, because nothing records which party it was
		//   for. That needs a party dimension on the journal line, which is a schema change.
		//
		// AMOUNTS ARE THE BASE-CURRENCY ONES. A statement that adds a dollar invoice to a pound receipt is
		// arithmetic nobody can use; *Base is what the ledger posted. Older rows predate those columns, so
		// the raw amount is the fallback rather than a zero.
		//
		// ONLY POSTED DOCUMENTS MOVE THE BALANCE. A draft invoice is not a receivable, and showing it would
		// make the statement disagree with the ledger in the other direction.
		// ================================================================================================

		/// PUBLIC and named, not anonymous: a runtime-compiled view lives in another assembly and cannot
		/// bind to an internal anonymous type through `dynamic`.
		public sealed record PartyPickItem(int Id, string Text);

		public sealed class PartyStatementRow
		{
			public DateTime Date { get; init; }
			public string Kind { get; init; } = "";      // localised label
			public string DocNo { get; init; } = "";
			public string? Note { get; init; }
			public decimal Debit { get; init; }
			public decimal Credit { get; init; }
			public string? Url { get; init; }            // the document behind the line, when it has a screen
			public decimal Balance { get; set; }         // running, filled after the sort
		}

		public sealed class PartyStatementModel
		{
			public int PartyId { get; init; }
			public string PartyName { get; init; } = "";
			public bool IsCustomer { get; init; }
			public DateTime? From { get; init; }
			public DateTime? To { get; init; }
			public decimal Opening { get; init; }
			public decimal Closing { get; init; }
			public decimal TotalDebit { get; init; }
			public decimal TotalCredit { get; init; }
			public List<PartyStatementRow> Rows { get; init; } = new();

			// Paging. Opening is the period's; CarriedForward is THIS page's starting balance, which is
			// the same figure on page 1 and the previous page's closing on every page after it.
			public int Page { get; init; } = 1;
			public int PageSize { get; init; } = 50;
			public int TotalRows { get; init; }
			public decimal CarriedForward { get; init; }
			public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)(PageSize < 1 ? 50 : PageSize)));
		}

		/// A movement as it is read out of a document table, before the period is applied.
		private sealed record Movement(DateTime Date, string Kind, string DocNo, string? Note,
									   decimal Debit, decimal Credit, string? Url);

		private static decimal Base(decimal? baseAmount, decimal raw) => baseAmount ?? raw;

		private async Task<PartyStatementModel> BuildPartyStatementAsync(
			int companyId, int partyId, bool isCustomer, string partyName, DateTime? from, DateTime? to,
			int page = 1, int pageSize = 50)
		{
			var moves = new List<Movement>();

			if (isCustomer)
			{
				// AR: what the customer owes rises on a debit.
				foreach (var i in await _context.SalesInvoices.AsNoTracking()
					.Where(i => i.CustomerId == partyId && i.CompanyID == companyId && i.Status == "Posted")
					.Select(i => new { i.ID, i.InvoiceDate, i.InvoiceNo, i.GrandTotal, i.GrandTotalBase, i.Notes }).ToListAsync())
					moves.Add(new Movement(i.InvoiceDate, L["Sales invoice"].Value, i.InvoiceNo ?? ("#" + i.ID), i.Notes,
						Base(i.GrandTotalBase, i.GrandTotal), 0, Url.Action(nameof(MovementSummary), new { kind = "SalesInvoice", id = i.ID })));

				// The credit note that was missing entirely. Without it a returned order still showed as owed.
				foreach (var r in await _context.SalesReturns.AsNoTracking()
					.Where(r => r.CustomerId == partyId && r.CompanyID == companyId && r.Status == "Posted")
					.Select(r => new { r.ID, r.ReturnDate, r.ReturnNo, r.GrandTotal, r.GrandTotalBase, r.Notes }).ToListAsync())
					moves.Add(new Movement(r.ReturnDate, L["Sales return"].Value, r.ReturnNo ?? ("#" + r.ID), r.Notes,
						0, Base(r.GrandTotalBase, r.GrandTotal), Url.Action(nameof(MovementSummary), new { kind = "SalesReturn", id = r.ID })));

				foreach (var r in await _context.Receipts.AsNoTracking()
					.Where(r => r.CustomerId == partyId && r.CompanyID == companyId && r.Status == "Posted")
					.Select(r => new { r.ID, r.ReceiptDate, r.ReceiptNo, r.Amount, r.AmountBase, r.Notes }).ToListAsync())
					// Receipts have a LIST screen and no per-row action, so the link goes there rather than to a
					// detail page that does not exist — a link that 404s is worse than a link to the list.
					moves.Add(new Movement(r.ReceiptDate, L["Receipt"].Value, r.ReceiptNo ?? ("#" + r.ID), r.Notes,
						0, Base(r.AmountBase, r.Amount), Url.Action(nameof(MovementSummary), new { kind = "Receipt", id = r.ID })));
			}
			else
			{
				// AP: what we owe the vendor rises on a credit — the mirror of the block above.
				foreach (var i in await _context.PurchaseInvoices.AsNoTracking()
					.Where(i => i.VendorId == partyId && i.CompanyID == companyId && i.Status == "Posted")
					.Select(i => new { i.ID, i.InvoiceDate, i.InvoiceNo, i.GrandTotal, i.GrandTotalBase, i.Notes }).ToListAsync())
					moves.Add(new Movement(i.InvoiceDate, L["Purchase Invoice"].Value, i.InvoiceNo ?? ("#" + i.ID), i.Notes,
						0, Base(i.GrandTotalBase, i.GrandTotal), Url.Action(nameof(MovementSummary), new { kind = "PurchaseInvoice", id = i.ID })));

				foreach (var r in await _context.PurchaseReturns.AsNoTracking()
					.Where(r => r.VendorId == partyId && r.CompanyID == companyId && r.Status == "Posted")
					.Select(r => new { r.ID, r.ReturnDate, r.ReturnNo, r.GrandTotal, r.GrandTotalBase, r.Notes }).ToListAsync())
					moves.Add(new Movement(r.ReturnDate, L["Purchase return"].Value, r.ReturnNo ?? ("#" + r.ID), r.Notes,
						Base(r.GrandTotalBase, r.GrandTotal), 0, Url.Action(nameof(MovementSummary), new { kind = "PurchaseReturn", id = r.ID })));

				foreach (var p in await _context.Payments.AsNoTracking()
					.Where(p => p.VendorId == partyId && p.CompanyID == companyId && p.Status == "Posted")
					.Select(p => new { p.ID, p.PaymentDate, p.PaymentNo, p.Amount, p.AmountBase, p.Notes }).ToListAsync())
					moves.Add(new Movement(p.PaymentDate, L["Payment"].Value, p.PaymentNo ?? ("#" + p.ID), p.Notes,
						Base(p.AmountBase, p.Amount), 0, Url.Action(nameof(MovementSummary), new { kind = "Payment", id = p.ID })));
			}

			// THE OPENING BALANCE IS EVERYTHING BEFORE THE PERIOD, not a stored figure: a statement whose
			// opening does not equal the sum of what came before it is the classic way one stops reconciling.
			decimal opening = 0;
			if (from.HasValue)
			{
				foreach (var m in moves.Where(m => m.Date.Date < from.Value.Date)) opening += m.Debit - m.Credit;
			}

			var inPeriod = moves
				.Where(m => (!from.HasValue || m.Date.Date >= from.Value.Date)
						 && (!to.HasValue || m.Date.Date <= to.Value.Date))
				.OrderBy(m => m.Date).ThenBy(m => m.DocNo, StringComparer.Ordinal)
				.ToList();

			// The balance is run over the WHOLE period BEFORE any slicing, so a row's balance does not
			// depend on which page it happens to land on.
			var all = new List<PartyStatementRow>(inPeriod.Count);
			decimal running = opening, td = 0, tc = 0;
			foreach (var m in inPeriod)
			{
				running += m.Debit - m.Credit; td += m.Debit; tc += m.Credit;
				all.Add(new PartyStatementRow
				{
					Date = m.Date, Kind = m.Kind, DocNo = m.DocNo, Note = m.Note,
					Debit = m.Debit, Credit = m.Credit, Url = m.Url, Balance = running,
				});
			}

			if (pageSize < 1) pageSize = 50; else if (pageSize > 500) pageSize = 500;
			int pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize));
			if (page < 1) page = 1; else if (page > pages) page = pages;
			int skip = (page - 1) * pageSize;

			// What this page starts from: the period's opening on page 1, the previous page's closing after.
			decimal carried = skip == 0 ? opening : all[skip - 1].Balance;

			return new PartyStatementModel
			{
				PartyId = partyId, PartyName = partyName, IsCustomer = isCustomer,
				From = from, To = to, Opening = opening, Closing = running,
				TotalDebit = td, TotalCredit = tc,
				Rows = all.Skip(skip).Take(pageSize).ToList(),
				Page = page, PageSize = pageSize, TotalRows = all.Count, CarriedForward = carried,
			};
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
					Status = e.Status, Description = e.Description, DescriptionEn = e.DescriptionEn, SourceType = e.SourceType, SourceId = e.SourceId,
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
				// The service layer has no localizer by design, so its message arrives in English. A
				// localizer returns the key unchanged when there is no entry, so passing the service's
				// own sentence through L is safe and translates wherever a key exists.
				TempData["AccErr"] = string.IsNullOrWhiteSpace(err) ? err : L[err].Value;
				ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
				ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId);
				ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);   // the GET sets all three; this path was dropping the Project column's options
				return View();
			}
			TempData["AccMsg"] = forcedDraft ? L["The entry ({0:N2}) exceeds the approval threshold — saved as a draft awaiting the chief accountant's approval", total].Value
				: post ? L["The journal entry was posted successfully"].Value : L["The journal entry was saved as a draft"].Value;
			return RedirectToAction(nameof(Journals));
		}

		// تعديل قيد — EDIT A DRAFT MANUAL ENTRY.
		//
		// The rules live in IJournalEntryService.LoadEditableDraftAsync, not here: draft-only, manual-only,
		// no source document, no reversal attached, and both the old and the new date must fall in an OPEN
		// period. This action only decides whether to draw the form.
		//
		// It draws the SAME view the create uses. The lines editor — the classic table and the Excel grid
		// that shares it — exists once; a second copy for editing would drift from it within a week.
		[SessionValidation]
		[HttpGet]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditJournal(int id)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Journals)); }

			// The company predicate is IN the query, so an entry belonging to another company is NOT FOUND
			// rather than found-then-refused — otherwise the id becomes an existence oracle across tenants.
			var entry = await _context.JournalEntries.AsNoTracking().Include(e => e.Lines)
				.FirstOrDefaultAsync(e => e.ID == id && e.CompanyID == scope.CompanyId);
			if (entry == null) { TempData["AccErr"] = L["Journal entry not found"].Value; return RedirectToAction(nameof(Journals)); }

			// A cheap pre-check so the form is not drawn for something that cannot be saved. It is NOT the
			// gate — the service re-checks all of it, including the period, on the post.
			if (entry.Status != "Draft")
			{ TempData["AccErr"] = L["Only a draft entry can be changed — a posted entry is corrected by a reversal"].Value; return RedirectToAction(nameof(Journals)); }
			if (entry.JournalType != "Manual" || !string.IsNullOrWhiteSpace(entry.SourceType) || entry.SourceId != null)
			{ TempData["AccErr"] = L["Only a manual entry can be changed — this entry was generated by the system"].Value; return RedirectToAction(nameof(Journals)); }

			ViewBag.Accounts = await _coa.GetFlatAsync(scope.CompanyId, postableOnly: true);
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(scope.CompanyId);
			ViewBag.Projects = await PrjSvc.ForPickAsync(scope.CompanyId);
			ViewBag.EditId = entry.ID;
			ViewBag.EntryDate = entry.EntryDate;
			ViewBag.EntryDescription = entry.Description;
			ViewBag.ExistingLines = entry.Lines.OrderBy(l => l.LineNo).Select(l => new {
				accountId = l.AccountId, debit = l.Debit, credit = l.Credit,
				costCenterId = l.CostCenterId, projectId = l.ProjectId, description = l.Description
			}).ToList();
			return View("CreateJournal");
		}

		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> EditJournal(int id, DateTime entryDate, string? description, string? linesJson)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Journals)); }

			List<JournalLineInput> lines;
			try { lines = JsonSerializer.Deserialize<List<JournalLineInput>>(linesJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { lines = new(); }

			var input = new JournalEntryInput { CompanyID = scope.CompanyId, EntryDate = entryDate, Description = description, Lines = lines };
			var (ok, err) = await _journals.UpdateDraftAsync(id, scope.CompanyId, input, _access.CurrentEmployeeId());
			if (!ok)
			{
				// The service layer has no localizer by design; a localizer returns the key unchanged when
				// there is no entry, so passing its sentence through L is safe and translates where a key exists.
				TempData["AccErr"] = string.IsNullOrWhiteSpace(err) ? err : L[err].Value;
				return RedirectToAction(nameof(EditJournal), new { id });
			}
			TempData["AccMsg"] = L["The draft entry was updated"].Value;
			return RedirectToAction(nameof(Journals));
		}

		// حذف مسودة — a draft written by mistake has no other remedy: it cannot be posted away and it
		// cannot be reversed (a reversal answers a POSTED entry). Same guard as the edit, deliberately.
		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.AccPerm("post")]
		public async Task<IActionResult> DeleteJournal(int id)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Journals)); }

			var (ok, err) = await _journals.DeleteDraftAsync(id, scope.CompanyId, _access.CurrentEmployeeId());
			if (!ok) TempData["AccErr"] = string.IsNullOrWhiteSpace(err) ? err : L[err].Value;
			else TempData["AccMsg"] = L["The draft entry was deleted"].Value;
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

		// ROUTED THROUGH THE GOVERNED CONTROL SERVICE.
		//
		// This action used to call FiscalPeriodService.SetStatusAsync(id, status), which took no company
		// and no actor - so this screen could close ANOTHER company's period, over an out-of-balance
		// journal, with nothing recorded. It was also gated on AccPerm("manage"), the ROLE-MANAGEMENT
		// right, so sealing a month required the power to grant accounting roles.
		//
		// The authority now lives in the service (period-close / period-reopen, both NeverBootstrapOpen),
		// which also resolves the company from the period's own fiscal year, gates on readiness, and
		// writes the audit row. Nothing about posting, stock, journals, receivables or payments changes
		// here - this is the same screen, asking a governed service instead of an ungoverned setter.
		[SessionValidation]
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SetPeriodStatus(int id, string status, string? reason, bool overrideWarnings = false)
		{
			// Same service-locator pattern this controller already uses for BusinessContexts, so the
			// constructor is untouched - the smallest possible edit to a shared file.
			var __periodControl = HttpContext.RequestServices.GetService(typeof(IAccountingPeriodControlService))
				as IAccountingPeriodControlService;
			var ctx = BusinessContexts == null ? null : await BusinessContexts.TryGetCurrentAsync();
			if (__periodControl == null) ctx = null;
			if (ctx == null)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Periods));
			}

			// The required authority depends on the requested transition, so it is resolved from the status
			// and asked AT THE BOUNDARY. The POLICY still lives in one place - AccountingAccessService decides
			// which roles hold period-close and period-reopen - and the control service asks again before it
			// writes. This call is the boundary refusal, not a second copy of the rule.
			var __required = status == CrossBuy.Models.Context.Accounting.AccountingPeriodStatuses.Open
				? AccountingActions.PeriodReopen
				: AccountingActions.PeriodClose;
			var __accounting = HttpContext.RequestServices.GetService(typeof(AccountingAccessService)) as AccountingAccessService;
			if (__accounting == null || !await __accounting.CanAsync(ctx, __required))
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Periods));
			}

			var (ok, err) = status switch
			{
				CrossBuy.Models.Context.Accounting.AccountingPeriodStatuses.SoftClosed
					=> await __periodControl!.SoftCloseAsync(ctx, id),
				CrossBuy.Models.Context.Accounting.AccountingPeriodStatuses.Closed
					=> await __periodControl!.CloseAsync(ctx, id, overrideWarnings),
				CrossBuy.Models.Context.Accounting.AccountingPeriodStatuses.Open
					=> await __periodControl!.ReopenAsync(ctx, id, reason ?? ""),
				_ => (false, L["Invalid status"].Value),
			};
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
			// CORRECTION-005 — resolved company. An unresolved scope must not export a workbook at
			// all: a spreadsheet leaves the application and cannot be recalled.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var query = _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == scope.CompanyId);
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
			// CORRECTION-005 — resolved company. An unresolved scope must not export a workbook at
			// all: a spreadsheet leaves the application and cannot be recalled.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["AccErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var query = _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == scope.CompanyId);
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
			// CORRECTION-005 — resolved company, and it is the FIRST thing this action does: an
			// unresolved scope returns an empty grid rather than another company's invoices.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { SetPaging(0, 1, pageSize); return PartialView("_SalesInvoiceRows", System.Array.Empty<Models.Context.Accounting.SalesInvoice>()); }

			var query = _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == scope.CompanyId);
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

			// The units a line may be sold in. SalesInvoiceLine.UoMId has always carried this and the stock
			// path has always converted it; the screen simply never offered the choice, so every line was
			// silently the item's base unit. CompanyID is in the predicate like every other list here.
			ViewBag.Units = await _context.UnitsOfMeasure.AsNoTracking()
				.Where(u => u.CompanyID == DefaultCompanyId).OrderBy(u => u.Name).ToListAsync();
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
			// Name the receipt(s) holding this invoice. The old message said only "a receipt",
			// which left the user to search for it - and this is the common case, not a rare one.
			var blockingReceipts = await (from a in _context.ReceiptAllocations.AsNoTracking()
										   join r in _context.Receipts.AsNoTracking() on a.ReceiptId equals r.ID
										   where a.CompanyID == DefaultCompanyId && a.SalesInvoiceId == id
										   select new { r.ReceiptNo, a.ForeignAmount }).ToListAsync();
			if (blockingReceipts.Count > 0)
			{
				var named = string.Join(", ", blockingReceipts.Take(2).Select(r => $"{r.ReceiptNo} ({r.ForeignAmount:N2})"));
				if (blockingReceipts.Count > 2) named += L[" and {0} more", blockingReceipts.Count - 2].Value;
				TempData["AccErr"] = L["Cannot edit {0}: receipt {1} is allocated to it. Unallocate it from Receipts first.",
					inv.InvoiceNo, named].Value;
				return RedirectToAction(nameof(SalesInvoices));
			}
			ViewBag.Customers = await _ar.GetCustomersAsync(DefaultCompanyId);
			ViewBag.RevenueAccounts = (await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true)).Where(a => a.TypeCode == "REV").ToList();
			ViewBag.InvWarehouses = await _context.Warehouses.AsNoTracking().Where(w => w.CompanyID == DefaultCompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			ViewBag.Currencies = await CurrencyListAsync();
			ViewBag.FunctionalCurrencyId = await FunctionalCurrencyIdAsync();
			ViewBag.Projects = await PrjSvc.ForPickAsync(DefaultCompanyId);

			// The units a line may be sold in. SalesInvoiceLine.UoMId has always carried this and the stock
			// path has always converted it; the screen simply never offered the choice, so every line was
			// silently the item's base unit. CompanyID is in the predicate like every other list here.
			ViewBag.Units = await _context.UnitsOfMeasure.AsNoTracking()
				.Where(u => u.CompanyID == DefaultCompanyId).OrderBy(u => u.Name).ToListAsync();
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
			// The company is RESOLVED, not a constant: the screen below shows the document's own currency,
			// and reading the right row for the wrong company is the failure that produces a right-looking
			// number about somebody else's document.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Index)); }
			var ret = await _ar.GetSalesReturnAsync(scope.CompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(SalesReturns)); }
			ViewBag.Customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == ret.CustomerId);
			await ReturnDetailFactsAsync(scope.CompanyId, ret.CurrencyId, ret.JournalEntryId, ret.OriginalInvoiceId, isSales: true);
			return View(ret);
		}

		// The three facts BOTH return screens were showing wrong, resolved once.
		//
		//   · THE CURRENCY WAS THE LITERAL "EGP", printed next to the total. On a document in another
		//     currency that is not a missing label, it is a WRONG ONE — and such documents exist (sales
		//     return 4049 is KWD at a rate of 163). The document's own CurrencyId decides.
		//   · "الفاتورة الأصلية #12509" and "قيد يومية #15162" showed ROW IDS. A reader cannot look up an
		//     id: the invoice is known by its number, and the entry by JV-YYYY-NNNNNN.
		private async Task ReturnDetailFactsAsync(
			int companyId, int? currencyId, int? journalEntryId, int? originalInvoiceId, bool isSales)
		{
			// A document with no CurrencyId is not a document with no currency — it is one recorded in the
			// company's FUNCTIONAL currency, which is exactly what the figure beside it is denominated in.
			// Falling back to it is why the label can be trusted; printing nothing would leave the reader
			// guessing, and printing a constant is what was wrong here in the first place.
			var effectiveCurrencyId = currencyId ?? await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			ViewBag.CurrencyCode = await _context.Currencies.AsNoTracking()
				.Where(c => c.ID == effectiveCurrencyId).Select(c => c.Code).FirstOrDefaultAsync();

			ViewBag.JournalEntryNo = journalEntryId == null ? null
				: await _context.JournalEntries.AsNoTracking()
					.Where(e => e.ID == journalEntryId && e.CompanyID == companyId)
					.Select(e => e.EntryNo).FirstOrDefaultAsync();

			if (originalInvoiceId != null)
				ViewBag.OriginalInvoiceNo = isSales
					? await _context.SalesInvoices.AsNoTracking()
						.Where(i => i.ID == originalInvoiceId && i.CompanyID == companyId)
						.Select(i => i.InvoiceNo).FirstOrDefaultAsync()
					: await _context.PurchaseInvoices.AsNoTracking()
						.Where(i => i.ID == originalInvoiceId && i.CompanyID == companyId)
						.Select(i => i.InvoiceNo).FirstOrDefaultAsync();
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
		// D1 WAVE 1 — CRITICAL (financial master data). A vendor is the payable side of the ledger: creating one
		// silently is how an unauthorized payee enters the system. The right is DERIVED, not invented — `SaveVendor`
		// in this same controller already carries AccPerm("post"), and this creates the same entity, so it takes the
		// same right. ApiPerm, not AccPerm, because this action returns JSON to an inline dropdown: AccPerm would
		// answer a fetch() caller with 302 -> an HTML login page.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.ApiPerm(CrossBuy.Models.ApiPermAttribute.Accounting, "post")]
		public async Task<IActionResult> VendorQuickAdd(string name, string? nameEn, string? taxRegNo)
		{
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Name is required"].Value });
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return Json(new { ok = false, error = L["You do not have permission to perform this action"].Value });
			var v = await _ap.CreateVendorAsync(scope.CompanyId, name.Trim(),
				string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(),
				string.IsNullOrWhiteSpace(taxRegNo) ? null : taxRegNo.Trim());
			var isAr = (HttpContext.Items["Culture"]?.ToString() == "ar");
			return Json(new { ok = true, id = v.ID, name = isAr ? v.Name : (v.NameEn ?? v.Name) });
		}

		// Inline "quick add" for the Customer dropdown on sales document screens → {ok,id,name}.
		// D1 WAVE 1 — CRITICAL (financial master data). Same reasoning as VendorQuickAdd: `SaveCustomer` in this
		// controller carries AccPerm("post") and this creates the same entity. A customer carries a control account
		// and a credit limit, so an unauthorized one is a receivable nobody approved.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.ApiPerm(CrossBuy.Models.ApiPermAttribute.Accounting, "post")]
		public async Task<IActionResult> CustomerQuickAdd(string name, string? nameEn, string? taxRegNo)
		{
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Name is required"].Value });
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return Json(new { ok = false, error = L["You do not have permission to perform this action"].Value });
			var c = await _ar.CreateCustomerAsync(scope.CompanyId, name.Trim(),
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
			// CORRECTION-005 — resolved company, and it is the FIRST thing this action does: an
			// unresolved scope returns an empty grid rather than another company's invoices.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { SetPaging(0, 1, pageSize); return PartialView("_PurchaseInvoiceRows", System.Array.Empty<Models.Context.Accounting.PurchaseInvoice>()); }

			var query = _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == scope.CompanyId);
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
			// Same as the sales side: say WHICH payment, not just that there is one.
			var blockingPayments = await (from a in _context.PaymentAllocations.AsNoTracking()
										   join pm in _context.Payments.AsNoTracking() on a.PaymentId equals pm.ID
										   where a.CompanyID == DefaultCompanyId && a.PurchaseInvoiceId == id
										   select new { pm.PaymentNo, a.ForeignAmount }).ToListAsync();
			if (blockingPayments.Count > 0)
			{
				var named = string.Join(", ", blockingPayments.Take(2).Select(x => $"{x.PaymentNo} ({x.ForeignAmount:N2})"));
				if (blockingPayments.Count > 2) named += L[" and {0} more", blockingPayments.Count - 2].Value;
				TempData["AccErr"] = L["Cannot edit {0}: payment {1} is allocated to it. Unallocate it from Payments first.",
					inv.InvoiceNo, named].Value;
				return RedirectToAction(nameof(PurchaseInvoices));
			}
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
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) { TempData["AccErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Index)); }
			var ret = await _ap.GetPurchaseReturnAsync(scope.CompanyId, id);
			if (ret == null) { TempData["AccErr"] = L["Return not found"].Value; return RedirectToAction(nameof(PurchaseReturns)); }
			ViewBag.Vendor = await _context.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.ID == ret.VendorId);
			await ReturnDetailFactsAsync(scope.CompanyId, ret.CurrencyId, ret.JournalEntryId, ret.OriginalInvoiceId, isSales: false);
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
		public async Task<IActionResult> SaveMaintenanceSchedule(int id, int assetId, string title, string? titleEn, string type, int intervalMonths, DateTime nextDueDate, decimal? estimatedCost, bool isActive)
		{
			var (ok, err, _) = await MntSvc.SaveScheduleAsync(new CrossBuy.Models.Context.Accounting.MaintenanceSchedule { ID = id, CompanyID = DefaultCompanyId, AssetId = assetId, Title = title ?? "", TitleEn = titleEn, Type = type, IntervalMonths = intervalMonths, NextDueDate = nextDueDate, EstimatedCost = estimatedCost, IsActive = isActive });
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

		// =============================================================================================
		// BUSINESS CONVERSATIONS on sales and purchase invoices.
		//
		// The conversation is NOT stored here. There is no invoice-comment table and no second
		// communication store: the thread, the comments, the mentions, the actors and the audit trail all
		// belong to the Communication Platform, reached through its entity-reference abstraction
		// (CommEntityRef + ICommEntitySurface). This controller contributes a permission decision and a
		// projection, nothing else. Tasks proved the same architecture; this reuses it rather than
		// re-implementing it, and the two invoice endpoints share ONE gate and ONE projection below.
		//
		// ORDER OF CHECKS IS THE SECURITY PROPERTY, and it is deliberate:
		//
		//   1. company resolved  — from IRequestCompanyResolver, never from DefaultCompanyId and never
		//                          from anything the caller supplied.
		//   2. module permission — IAccountingAccessService "read", the approved contract. It fails closed
		//                          on an unresolved company and denies an unknown action.
		//   3. THE ROW          — the invoice must exist IN THE RESOLVED COMPANY.
		//   4. platform present — only now may the caller learn whether Communication is deployed.
		//   5. capability       — and whether this entity family carries comments at all.
		//
		// Steps 2 and 3 return the IDENTICAL refusal, deliberately: "no such invoice" and "that invoice
		// belongs to another company" must be indistinguishable. Otherwise the endpoint becomes an
		// existence oracle — a caller could enumerate ids and learn which invoices exist elsewhere. For
		// the same reason the refusal carries no thread id, no participant name, no mention count and no
		// comment body, and the resolver's own reason is never rendered (it names companies).
		// =============================================================================================

		/// The families this surface serves, mapped to the frozen EntityRegistry codes. An entity code
		/// arriving from the query string is matched against THIS list and nothing else: a caller cannot
		/// name an arbitrary family and have it forwarded to the platform.
		private static readonly Dictionary<string, string> ConversationFamilies =
			new(StringComparer.Ordinal)
			{
				["SalesInvoice"] = CrossBuy.BL.Platform.EntityRegistry.SalesInvoice,
				["PurchaseInvoice"] = CrossBuy.BL.Platform.EntityRegistry.PurchaseInvoice,

				// The credit and debit notes go through the SAME endpoints. They are the same question
				// asked of a different table, and three more actions per document family is how a
				// permission gate ends up written four ways.
				["SalesReturn"] = CrossBuy.BL.Platform.EntityRegistry.SalesReturn,
				["PurchaseReturn"] = CrossBuy.BL.Platform.EntityRegistry.PurchaseReturn,

				// The money documents, for the same reason: a receipt is discussed exactly as often as the
				// invoice it settled, and it had nowhere to be discussed because it had no family at all.
				["Receipt"] = CrossBuy.BL.Platform.EntityRegistry.Receipt,
				["Payment"] = CrossBuy.BL.Platform.EntityRegistry.Payment,
			};

		/// Resolved outcome of steps 1-3. `Ok == false` carries no detail on purpose.
		private sealed class ConversationGate
		{
			public bool Ok;
			public CrossBuy.Models.Platform.BusinessContext? Context;
			public string EntityCode = "";
		}

		private CrossBuy.BL.Platform.IBusinessContextAccessor? BusinessContexts =>
			HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor))
				as CrossBuy.BL.Platform.IBusinessContextAccessor;

		/// Steps 1-3. One helper, so the two invoice endpoints cannot drift apart on authorization.
		private async Task<ConversationGate> ConversationGateAsync(string? entity, int id, CancellationToken ct)
		{
			if (id <= 0) return new ConversationGate();
			if (string.IsNullOrWhiteSpace(entity) || !ConversationFamilies.TryGetValue(entity, out var code))
				return new ConversationGate();

			// 1 — the company is RESOLVED. An unresolved scope refuses before any row is read.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new ConversationGate();

			var ctx = BusinessContexts == null ? null : await BusinessContexts.TryGetCurrentAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return new ConversationGate();

			// 2 — the module permission, asked of the approved service rather than decided here.
			//
			// The SESSION-FREE overload, on the concrete AccountingAccessService: it takes the resolved
			// BusinessContext and the target explicitly, so nothing is inferred from ambient session
			// state. IAccountingAccessService only publishes the session-based CanAsync(action), which is
			// why TasksController takes the concrete type for exactly this call — same precedent here.
			// If the service cannot be resolved the gate REFUSES; it does not fall through to allow.
			var accounting = HttpContext.RequestServices.GetService(typeof(AccountingAccessService))
				as AccountingAccessService;
			if (accounting == null) return new ConversationGate();

			if (!await accounting.CanAsync(ctx, "read",
					CrossBuy.Models.Platform.PermissionTarget.ForEntity(code, id), ct))
				return new ConversationGate();

			// 3 — the ROW, in the caller's own company. Company is in the WHERE clause, never checked
			// after loading: a row from another company must never be materialised here at all.
			bool exists = code switch
			{
				var c when c == CrossBuy.BL.Platform.EntityRegistry.SalesInvoice =>
					await _context.SalesInvoices.AsNoTracking()
						.AnyAsync(i => i.ID == id && i.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.PurchaseInvoice =>
					await _context.PurchaseInvoices.AsNoTracking()
						.AnyAsync(i => i.ID == id && i.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.SalesReturn =>
					await _context.SalesReturns.AsNoTracking()
						.AnyAsync(r => r.ID == id && r.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.PurchaseReturn =>
					await _context.PurchaseReturns.AsNoTracking()
						.AnyAsync(r => r.ID == id && r.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.Receipt =>
					await _context.Receipts.AsNoTracking()
						.AnyAsync(r => r.ID == id && r.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.Payment =>
					await _context.Payments.AsNoTracking()
						.AnyAsync(p => p.ID == id && p.CompanyID == ctx.CompanyId, ct),

				// A family in the map with no row check here would be a document nobody verified
				// exists — refuse rather than fall through to the last table in the chain.
				_ => false,
			};

			if (!exists) return new ConversationGate();

			return new ConversationGate { Ok = true, Context = ctx, EntityCode = code };
		}

		/// The optional platform, asked for and never required — the same shape TasksController uses.
		/// Both services come from ONE registration, so a half-present pair is treated as absent rather
		/// than used: a conversation that can list but not add is a worse answer than an honest 503.
		private (CrossBuy.BL.Communication.ICommThreadService Threads,
		         CrossBuy.BL.Communication.ICommCommentService Comments,
		         CrossBuy.BL.Communication.ICommEntitySurface Surface,
		         CrossBuy.BL.Communication.ICommReactionService? Reactions)? TryConversation()
		{
			var sp = HttpContext.RequestServices;
			var threads = sp.GetService(typeof(CrossBuy.BL.Communication.ICommThreadService))
				as CrossBuy.BL.Communication.ICommThreadService;
			var comments = sp.GetService(typeof(CrossBuy.BL.Communication.ICommCommentService))
				as CrossBuy.BL.Communication.ICommCommentService;
			var surface = sp.GetService(typeof(CrossBuy.BL.Communication.ICommEntitySurface))
				as CrossBuy.BL.Communication.ICommEntitySurface;

			// REACTIONS ARE OPTIONAL WHERE THE OTHER THREE ARE NOT. A deployment without them still has
			// a working conversation — the panel simply offers no emoji — whereas a conversation that
			// can list but not add is the half-present pair this method already refuses.
			var reactions = sp.GetService(typeof(CrossBuy.BL.Communication.ICommReactionService))
				as CrossBuy.BL.Communication.ICommReactionService;

			return threads is null || comments is null || surface is null
				? null : (threads, comments, surface, reactions);
		}

		/// The machine code the browser branches on. A CODE, not a sentence: an unavailable capability
		/// must be distinguishable from a refusal and from a failure, and a translated sentence cannot
		/// carry that distinction.
		public const string ConversationUnavailableCode = "communication_unavailable";

		private IActionResult ConversationUnavailable() =>
			StatusCode(StatusCodes.Status503ServiceUnavailable, new
			{
				ok = false,
				unavailable = true,
				code = ConversationUnavailableCode,
				error = L["Conversations are unavailable in this environment"].Value,
			});

		// GET /Accounting/InvoiceConversation?entity=SalesInvoice&id=123
		[SessionValidation][HttpGet]
		public async Task<IActionResult> InvoiceConversation(string? entity, int id, CancellationToken ct = default)
		{
			var gate = await ConversationGateAsync(entity, id, ct);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			var comm = TryConversation();
			if (comm == null) return ConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(gate.EntityCode, id);

			// The registry decides whether this family carries comments — not this controller.
			var allowed = await comm.Value.Surface.EvaluateAsync(
				reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed)
				return StatusCode(StatusCodes.Status503ServiceUnavailable, new
				{
					ok = false, unavailable = true, code = "capability_disabled",
					error = L["Conversations are unavailable in this environment"].Value,
				});

			var thread = await comm.Value.Threads.GetOrCreateAsync(gate.Context!,
				new CrossBuy.Models.Communication.CommThreadRequest { Entity = reference }, ct);

			var page = await comm.Value.Comments.ListAsync(gate.Context!, thread.Id, null, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// THE AUTHORS' PHOTOGRAPHS, resolved once for the page rather than per comment. Scoped to
			// this company by the resolver: an author from outside it comes back with no photo and the
			// panel falls back to initials, so a thread cannot be used to read staff pictures out of a
			// company the caller cannot see.
			var avatars = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
				_context, gate.Context!, page.Items.Select(c => c.Author.EmployeeId), ct);

			return Json(new
			{
				ok = true,
				threadId = thread.Id,
				entity = new { code = gate.EntityCode, id },
				canReact = comm.Value.Reactions is not null,
				me = await CrossBuy.BL.Communication.CommPanel.MeAsync(_context, gate.Context!, isAr, ct),

				// ONE PROJECTION for every module that renders this panel — parent, attachments,
				// reactions and per-comment capabilities included. See CommPanel for what is
				// deliberately left out (raw storage keys, mention target ids).
				comments = CrossBuy.BL.Communication.CommPanel.Project(page.Items, isAr, avatars),
			});
		}

		// POST /Accounting/InvoiceConversationAdd
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[RequestSizeLimit(21_000_000)]   // CommPanel.MaxUploadBytes plus the form envelope
		public async Task<IActionResult> InvoiceConversationAdd(string? entity, int id, string? body,
			long? parentCommentId, IFormFile? file, CancellationToken ct = default)
		{
			// The SAME gate as the read. A caller who may not read the invoice may not comment on it,
			// and the refusal is identical so the write path is not an existence oracle either.
			var gate = await ConversationGateAsync(entity, id, ct);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			// A MESSAGE MAY BE A FILE. Requiring text would make "here is the signed copy" impossible
			// to send without typing something to go with it.
			if (string.IsNullOrWhiteSpace(body) && (file is null || file.Length == 0))
				return Json(new { ok = false, error = L["Write a comment"].Value });

			var comm = TryConversation();
			if (comm == null) return ConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(gate.EntityCode, id);

			var allowed = await comm.Value.Surface.EvaluateAsync(
				reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed)
				return StatusCode(StatusCodes.Status503ServiceUnavailable, new
				{
					ok = false, unavailable = true, code = "capability_disabled",
					error = L["Conversations are unavailable in this environment"].Value,
				});

			// NOTHING REACHES DISK BEFORE THE GATE. The upload is staged only after the same permission
			// check the read path runs, so a refused caller never leaves an orphan file behind — the
			// ordering ChatController documents as F8-A, for the same reason.
			CrossBuy.Models.Communication.CommAttachmentRequest? attachment = null;
			if (file is { Length: > 0 })
			{
				// Resolved per request rather than injected: this controller's constructor is long and
				// shared, and the web root is needed on exactly one path.
				var env = HttpContext.RequestServices
					.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment))
					as Microsoft.AspNetCore.Hosting.IWebHostEnvironment;
				if (env is null) return Json(new { ok = false, error = L["The file could not be attached"].Value });

				var (staged, refusal) = await CrossBuy.BL.Communication.CommPanel.StageAsync(
					file, env.WebRootPath, ct);
				if (staged is null)
					return Json(new { ok = false, code = refusal, error = AttachmentRefusalText(refusal) });
				attachment = staged;
			}

			// The platform owns body policy, mention parsing, the audit row and any notification fan-out.
			// Nothing about a comment is re-implemented here, and no second notification channel exists.
			var added = await comm.Value.Comments.AddAsync(gate.Context!,
				new CrossBuy.Models.Communication.CommCommentRequest
				{
					Entity = reference,
					Body = body ?? "",
					ParentCommentId = parentCommentId is > 0 ? parentCommentId : null,
					Attachments = attachment is null ? null : new[] { attachment },
				}, ct);

			return Json(new { ok = true, id = added.CommentId, threadId = added.ThreadId });
		}

		/// One sentence per machine code, so the browser never has to compose a refusal.
		private string AttachmentRefusalText(string code) => code switch
		{
			CrossBuy.BL.Communication.CommPanel.UploadRefusal.TooLarge =>
				L["The file is larger than 20 MB"].Value,
			CrossBuy.BL.Communication.CommPanel.UploadRefusal.Type =>
				L["This kind of file cannot be attached"].Value,
			_ => L["The file could not be attached"].Value,
		};

		// POST /Accounting/InvoiceConversationReact
		//
		// The SAME gate as the read, so reacting is not an existence oracle either. The platform decides
		// whether this caller may react to this comment (CommCommentCapabilities.CanReact) and refuses
		// on its own terms — this endpoint does not second-guess it.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> InvoiceConversationReact(string? entity, int id, long commentId,
			string? key, bool on, CancellationToken ct = default)
		{
			var gate = await ConversationGateAsync(entity, id, ct);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			var comm = TryConversation();
			if (comm?.Reactions is null) return ConversationUnavailable();
			if (commentId <= 0 || string.IsNullOrWhiteSpace(key))
				return Json(new { ok = false, error = L["The reaction could not be saved"].Value });

			try
			{
				var summary = on
					? await comm.Value.Reactions.AddAsync(gate.Context!, commentId, key, ct)
					: await comm.Value.Reactions.RemoveAsync(gate.Context!, commentId, key, ct);

				return Json(new
				{
					ok = true,
					commentId,
					reactions = summary.Where(r => r.Count > 0)
						.Select(r => new { key = r.ReactionKey, count = r.Count, mine = r.Mine }),
				});
			}
			catch (CrossBuy.Models.Communication.CommAccessDeniedException)
			{
				// Indistinguishable from "no such comment", as everywhere else on this surface.
				return NotFound(new { ok = false, code = "not_found" });
			}
			catch (CrossBuy.Models.Communication.CommValidationException)
			{
				return Json(new { ok = false, error = L["The reaction could not be saved"].Value });
			}
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
		public int? SourceId { get; set; }      // the row needs it to decide whether to offer Edit; the service still re-checks
		public decimal Total { get; set; }
	}
}
