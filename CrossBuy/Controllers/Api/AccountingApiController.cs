using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// Accounting API surface for the mobile client. JWT bearer (same scheme as the rest of the API).
	//
	// ===== STAGE 1 HOTFIX A.1 — SECURITY =====
	//
	// Before this hotfix every action here was authenticated but NOT authorized, and every one took its company
	// from the request with `= 1` as the default. Any employee holding a valid mobile token could post to the
	// general ledger, post a payroll run, or create invoices and payments — and could name ANOTHER COMPANY by
	// passing `?companyId=2` or `{"CompanyID": 2}`.
	//
	// Three rules now hold for every action in this file, without exception:
	//
	//   1. AUTHORIZATION IS SERVER-SIDE AND EXPLICIT. Each action names the accounting permission it requires and
	//      asks IAccountingApiAuthorization, which asks the same context-aware AccountingAccessService the MVC
	//      screens use. Authentication is not authorization: a valid token proves who signed in, nothing more.
	//   2. THE COMPANY COMES FROM THE RESOLVED BusinessContext, never from the request. Every `companyId` /
	//      `dto.CompanyID` parameter is PRESERVED for backward compatibility and is COMPATIBILITY-ONLY: it is
	//      validated against the caller's resolved company and rejected on mismatch — never used as the source of
	//      truth, and never silently coerced.
	//   3. NOTHING RUNS BEFORE THE GATE. The guard is the first statement of every action, so a refused request
	//      begins no transaction, writes no row, posts no journal, raises no business event and sends no
	//      notification.
	//
	// The permission for each action is the SAME one the MVC controller already enforces for the same operation, so
	// an authorized user's rights do not change between the web UI and the API. PayrollPost is `manage`
	// (ChiefAccountant only) because AccountingController.PostPayroll is.
	//
	// Anti-forgery is deliberately NOT added: this controller is bearer-only, so there is no ambient credential for
	// a cross-site request to abuse, and a cookie-backed antiforgery token would break every mobile client. See
	// ADR-025.
	//
	// Batch B's query filters and write guard also protect several of these paths — that is defence in depth and is
	// NOT the control. Every check below is explicit and holds even if the filters were removed.
	[ApiController]
	[Route("api/acc")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class AccountingApiController : ControllerBase
	{
		// The accounting permission vocabulary, spelled once. These are the ONLY five actions
		// AccountingAccessService defines; the guard denies anything else.
		private const string PermRead = "read";
		private const string PermPost = "post";
		private const string PermPay = "pay";
		private const string PermManage = "manage";

		private readonly IChartOfAccountsService _coa;
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IGeneralLedgerService _gl;
		private readonly IAccountingPostingService _posting;
		private readonly IReceivableService _ar;
		private readonly IPayableService _ap;
		private readonly IFinancialStatementService _statements;
		private readonly IAccountingApiAuthorization _guard;

		public AccountingApiController(IChartOfAccountsService coa, CrossDbContext context,
			IJournalEntryService journals, IGeneralLedgerService gl, IAccountingPostingService posting,
			IReceivableService ar, IPayableService ap, IFinancialStatementService statements,
			IAccountingApiAuthorization guard)
		{
			_coa = coa; _context = context; _journals = journals; _gl = gl; _posting = posting; _ar = ar; _ap = ap;
			_statements = statements; _guard = guard;
		}

		// Ownership check for the two actions addressed by a row id instead of a company.
		//
		// It is EXPLICIT rather than relying on Batch B's filter making the row invisible: the query is written
		// against the validated company, and a row belonging to anyone else is reported as NOT FOUND — the same
		// answer a genuinely absent id gives, so nothing can be inferred from the difference.
		private async Task<bool> JournalBelongsToAsync(int entryId, int companyId, CancellationToken cancellationToken)
			=> await _context.JournalEntries.AsNoTracking()
				.AnyAsync(e => e.ID == entryId && e.CompanyID == companyId, cancellationToken);

		private IActionResult NotFoundSafe() => NotFound(new { success = false, message = "غير موجود" });

		// ---- Dashboard / reference ----

		// GET /api/acc/summary?companyId=  (companyId is compatibility-only: validated, never authoritative)
		[HttpGet("summary")]
		public async Task<IActionResult> Summary(int companyId = 0, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;
			companyId = auth.CompanyId;

			var today = DateTime.Today;
			var yearStart = new DateTime(today.Year, 1, 1);

			var pnl = await _statements.IncomeStatementAsync(companyId, yearStart, today);
			var arRows = await _ar.AgingAsync(companyId, today);
			var apRows = await _ap.AgingAsync(companyId, today);

			var cashIds = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Code.StartsWith("1101")).Select(a => a.ID).ToListAsync(cancellationToken);
			var cash = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed") && cashIds.Contains(l.AccountId)
							  select (decimal?)(l.Debit - l.Credit)).SumAsync(cancellationToken) ?? 0;

			var recent = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.Status == "Posted")
				.OrderByDescending(e => e.ID).Take(8)
				.Select(e => new
				{
					e.ID, e.EntryNo, e.EntryDate, e.JournalType, e.Description,
					amount = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0
				}).ToListAsync(cancellationToken);

			return Ok(new
			{
				success = true,
				data = new
				{
					asOf = today,
					cash = Math.Round(cash, 2),
					arTotal = Math.Round(arRows.Sum(r => r.Total), 2),
					apTotal = Math.Round(apRows.Sum(r => r.Total), 2),
					revenue = pnl.Revenue.Total,
					expense = pnl.Expenses.Total,
					netResult = pnl.NetProfit,
					customers = await _context.Customers.CountAsync(c => c.CompanyID == companyId, cancellationToken),
					vendors = await _context.Vendors.CountAsync(v => v.CompanyID == companyId, cancellationToken),
					recent
				}
			});
		}

		// GET /api/acc/account-types — GLOBAL reference data (AccountTypes carries no CompanyID and is classified
		// GlobalReference in B1), so there is no company to validate. It still requires `read`: an unauthenticated
		// or non-accounting caller has no business enumerating the chart's type vocabulary.
		[HttpGet("account-types")]
		public async Task<IActionResult> AccountTypes(CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, null, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var types = await _context.AccountTypes.AsNoTracking()
				.Select(t => new { t.ID, t.Code, t.Name, t.NameEn, t.NormalBalance, t.StatementType })
				.ToListAsync(cancellationToken);
			return Ok(new { success = true, data = types });
		}

		// GET /api/acc/accounts?companyId=  → full chart-of-accounts tree
		[HttpGet("accounts")]
		public async Task<IActionResult> Accounts(int companyId = 0, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var tree = await _coa.GetTreeAsync(auth.CompanyId);
			return Ok(new { success = true, companyId = auth.CompanyId, data = tree });
		}

		// ---- Journal entries ----

		// GET /api/acc/journals?companyId=
		[HttpGet("journals")]
		public async Task<IActionResult> Journals(int companyId = 0, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var list = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == auth.CompanyId)
				.OrderByDescending(e => e.ID)
				.Select(e => new
				{
					e.ID, e.EntryNo, e.EntryDate, e.JournalType, e.Status, e.Description, e.SourceType,
					debit = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0
				})
				.ToListAsync(cancellationToken);
			return Ok(new { success = true, data = list });
		}

		// GET /api/acc/journals/{id} — addressed by id, so the company predicate is explicit.
		[HttpGet("journals/{id:int}")]
		public async Task<IActionResult> Journal(int id, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, null, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var e = await _context.JournalEntries.AsNoTracking().Include(x => x.Lines)
				.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == auth.CompanyId, cancellationToken);
			if (e == null) return NotFoundSafe();
			return Ok(new
			{
				success = true,
				data = new
				{
					e.ID, e.EntryNo, e.EntryDate, e.JournalType, e.Status, e.Description, e.DescriptionEn,
					e.SourceType, e.SourceId, e.ReversedByEntryId,
					lines = e.Lines.OrderBy(l => l.LineNo).Select(l => new { l.LineNo, l.AccountId, l.Debit, l.Credit, l.CostCenterId, l.Description })
				}
			});
		}

		public class CreateJournalDto
		{
			// COMPATIBILITY-ONLY (Hotfix A.1). Preserved so existing clients keep compiling and posting the same
			// body; validated against the caller's resolved company and rejected on mismatch. The default is 0,
			// not 1: "not supplied" must not mean "company 1".
			public int CompanyID { get; set; }
			public DateTime EntryDate { get; set; }
			public string? Description { get; set; }
			public string? DescriptionEn { get; set; }
			public bool PostNow { get; set; }
			public List<JournalLineInput> Lines { get; set; } = new();
		}

		// POST /api/acc/journals  (PostNow=false → draft, true → create & post)   requires: post
		[HttpPost("journals")]
		public async Task<IActionResult> CreateJournal([FromBody] CreateJournalDto dto, CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });

			var auth = await _guard.AuthorizeAsync(PermPost, dto.CompanyID, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var input = new JournalEntryInput
			{
				// The VALIDATED company, never dto.CompanyID.
				CompanyID = auth.CompanyId, EntryDate = dto.EntryDate,
				Description = dto.Description, DescriptionEn = dto.DescriptionEn, Lines = dto.Lines,
			};
			// The actor is recorded now (CreatedBy/PostedBy). MVC already passes empId here; the API passed null,
			// so every API-created journal was anonymous. Additive audit only — no calculation changes.
			if (dto.PostNow)
			{
				var (ok, err, entry) = await _journals.CreateAndPostAsync(input, auth.EmployeeId);
				if (!ok) return BadRequest(new { success = false, message = err });
				return Ok(new { success = true, data = new { entry!.ID, entry.EntryNo, entry.Status } });
			}
			else
			{
				var (ok, err, entry) = await _journals.CreateDraftAsync(input, auth.EmployeeId);
				if (!ok) return BadRequest(new { success = false, message = err });
				return Ok(new { success = true, data = new { entry!.ID, entry.Status } });
			}
		}

		// POST /api/acc/journals/{id}/post   requires: post
		[HttpPost("journals/{id:int}/post")]
		public async Task<IActionResult> Post(int id, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermPost, null, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			// No company arrives on this route, so ownership is checked against the entry itself — otherwise a
			// guessed id would post another company's draft to their ledger.
			if (!await JournalBelongsToAsync(id, auth.CompanyId, cancellationToken)) return NotFoundSafe();

			var (ok, err) = await _journals.PostAsync(id, auth.EmployeeId);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err });
		}

		// POST /api/acc/journals/{id}/reverse   requires: post  (same as AccountingController.ReverseJournal)
		[HttpPost("journals/{id:int}/reverse")]
		public async Task<IActionResult> Reverse(int id, [FromBody] ReverseDto? dto, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermPost, null, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			if (!await JournalBelongsToAsync(id, auth.CompanyId, cancellationToken)) return NotFoundSafe();

			var (ok, err, reversalId) = await _journals.ReverseAsync(id, auth.EmployeeId, dto?.Reason);
			return ok ? Ok(new { success = true, reversalId }) : BadRequest(new { success = false, message = err });
		}
		public class ReverseDto { public string? Reason { get; set; } }

		// ---- Reports ----

		// GET /api/acc/trial-balance?companyId=&from=&to=&costCenterId=
		[HttpGet("trial-balance")]
		public async Task<IActionResult> TrialBalance(int companyId = 0, DateTime? from = null, DateTime? to = null,
			int? costCenterId = null, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var tb = await _gl.TrialBalanceAsync(auth.CompanyId, from, to, costCenterId);
			return Ok(new { success = true, isBalanced = tb.IsBalanced, totalDebit = tb.TotalDebit, totalCredit = tb.TotalCredit, data = tb.Rows });
		}

		// GET /api/acc/ledger/{accountId}?companyId=&from=&to=&costCenterId=
		[HttpGet("ledger/{accountId:int}")]
		public async Task<IActionResult> Ledger(int accountId, int companyId = 0, DateTime? from = null,
			DateTime? to = null, int? costCenterId = null, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			// The service scopes the statement by company, so an accountId from another company resolves to null
			// and answers exactly as a missing account does.
			var st = await _gl.AccountStatementAsync(auth.CompanyId, accountId, from, to, costCenterId);
			if (st == null) return NotFound(new { success = false, message = "الحساب غير موجود" });
			return Ok(new { success = true, data = st });
		}

		// ---- Payroll bridge ----

		// GET /api/acc/payroll/preview?companyId=&year=&month=
		//
		// `read` and not `manage`: the preview computes nothing and posts nothing, and the MVC Payroll screen is a
		// read surface too. Posting it is a different right — see below.
		[HttpGet("payroll/preview")]
		public async Task<IActionResult> PayrollPreview(int companyId = 0, int year = 2026, int month = 1,
			CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var p = await _posting.PreviewPayrollAsync(auth.CompanyId, year, month);
			return Ok(new { success = true, data = p });
		}

		// POST /api/acc/payroll/post?companyId=&year=&month=   requires: MANAGE
		//
		// `manage`, i.e. ChiefAccountant ONLY, because AccountingController.PostPayroll is `manage`. The API must
		// not be the looser door to the payroll journal.
		//
		// Duplicate posting is already prevented inside PostPayrollRunAsync (it refuses when a Payroll-sourced
		// journal exists for this company + period). That logic is untouched.
		[HttpPost("payroll/post")]
		public async Task<IActionResult> PayrollPost(int companyId = 0, int year = 2026, int month = 1,
			CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermManage, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var (ok, err, entryId) = await _posting.PostPayrollRunAsync(auth.CompanyId, year, month, auth.EmployeeId);
			return ok ? Ok(new { success = true, entryId }) : BadRequest(new { success = false, message = err });
		}

		// ---- AR (customers / sales / receipts) ----
		public class CustomerDto { public string Name { get; set; } = ""; public string? NameEn { get; set; } public string? TaxRegNo { get; set; } public decimal? CreditLimit { get; set; } }
		// CompanyID on these DTOs is COMPATIBILITY-ONLY — validated, never authoritative. Default 0, not 1.
		public class SalesInvoiceDto { public int CompanyID { get; set; } public int CustomerId { get; set; } public DateTime InvoiceDate { get; set; } public string? Notes { get; set; } public List<SalesLineInput> Lines { get; set; } = new(); }
		public class ReceiptDto { public int CompanyID { get; set; } public int CustomerId { get; set; } public DateTime ReceiptDate { get; set; } public decimal Amount { get; set; } public string Method { get; set; } = "Cash"; public int CashAccountId { get; set; } }

		[HttpGet("ar/customers")]
		public async Task<IActionResult> Customers(int companyId = 0, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;
			return Ok(new { success = true, data = await _ar.GetCustomersAsync(auth.CompanyId) });
		}

		// requires: post   (AccountingController.SaveCustomer is `post`)
		[HttpPost("ar/customers")]
		public async Task<IActionResult> CreateCustomer([FromBody] CustomerDto dto, int companyId = 0,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPost, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var c = await _ar.CreateCustomerAsync(auth.CompanyId, dto.Name, dto.NameEn, dto.TaxRegNo, dto.CreditLimit);
			return Ok(new { success = true, id = c.ID });
		}

		// requires: post
		[HttpPost("ar/invoices")]
		public async Task<IActionResult> CreateSalesInvoice([FromBody] SalesInvoiceDto dto,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPost, dto.CompanyID, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var (ok, err, inv) = await _ar.CreateSalesInvoiceAsync(
				auth.CompanyId, dto.CustomerId, dto.InvoiceDate, dto.Lines, dto.Notes, auth.EmployeeId);
			return ok ? Ok(new { success = true, id = inv!.ID, invoiceNo = inv.InvoiceNo, grand = inv.GrandTotal })
					  : BadRequest(new { success = false, message = err });
		}

		// requires: pay   (AccountingController.CreateReceipt is `pay`)
		[HttpPost("ar/receipts")]
		public async Task<IActionResult> CreateReceipt([FromBody] ReceiptDto dto,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPay, dto.CompanyID, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var (ok, err) = await _ar.CreateReceiptAsync(
				auth.CompanyId, dto.CustomerId, dto.ReceiptDate, dto.Amount, dto.Method, dto.CashAccountId, null, auth.EmployeeId);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err });
		}

		[HttpGet("ar/aging")]
		public async Task<IActionResult> ArAging(int companyId = 0, DateTime? asOf = null,
			CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;
			return Ok(new { success = true, data = await _ar.AgingAsync(auth.CompanyId, asOf ?? DateTime.UtcNow) });
		}

		// ---- AP (vendors / purchases / payments) ----
		public class VendorDto { public string Name { get; set; } = ""; public string? NameEn { get; set; } public string? TaxRegNo { get; set; } }
		public class PurchaseInvoiceDto { public int CompanyID { get; set; } public int VendorId { get; set; } public DateTime InvoiceDate { get; set; } public string? Notes { get; set; } public List<PurchaseLineInput> Lines { get; set; } = new(); }
		public class PaymentDto { public int CompanyID { get; set; } public int VendorId { get; set; } public DateTime PaymentDate { get; set; } public decimal Amount { get; set; } public string Method { get; set; } = "Cash"; public int CashAccountId { get; set; } }

		[HttpGet("ap/vendors")]
		public async Task<IActionResult> Vendors(int companyId = 0, CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;
			return Ok(new { success = true, data = await _ap.GetVendorsAsync(auth.CompanyId) });
		}

		// requires: post   (AccountingController.SaveVendor is `post`)
		//
		// NOTE: Vendor is NOT one of Batch B's twelve pilot entities, so neither the read filter nor the write guard
		// covers it. This action's ONLY company control is the validation above — which is exactly why the hotfix
		// could not rely on Batch B.
		[HttpPost("ap/vendors")]
		public async Task<IActionResult> CreateVendor([FromBody] VendorDto dto, int companyId = 0,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPost, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var v = await _ap.CreateVendorAsync(auth.CompanyId, dto.Name, dto.NameEn, dto.TaxRegNo);
			return Ok(new { success = true, id = v.ID });
		}

		// requires: post
		[HttpPost("ap/bills")]
		public async Task<IActionResult> CreatePurchaseInvoice([FromBody] PurchaseInvoiceDto dto,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPost, dto.CompanyID, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var (ok, err, inv) = await _ap.CreatePurchaseInvoiceAsync(
				auth.CompanyId, dto.VendorId, dto.InvoiceDate, dto.Lines, dto.Notes, auth.EmployeeId);
			return ok ? Ok(new { success = true, id = inv!.ID, invoiceNo = inv.InvoiceNo, grand = inv.GrandTotal })
					  : BadRequest(new { success = false, message = err });
		}

		// requires: pay   (AccountingController.CreatePayment is `pay`)
		[HttpPost("ap/payments")]
		public async Task<IActionResult> CreatePayment([FromBody] PaymentDto dto,
			CancellationToken cancellationToken = default)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var auth = await _guard.AuthorizeAsync(PermPay, dto.CompanyID, cancellationToken);
			if (!auth.Ok) return auth.Error!;

			var (ok, err) = await _ap.CreatePaymentAsync(
				auth.CompanyId, dto.VendorId, dto.PaymentDate, dto.Amount, dto.Method, dto.CashAccountId, null, auth.EmployeeId);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err });
		}

		[HttpGet("ap/aging")]
		public async Task<IActionResult> ApAging(int companyId = 0, DateTime? asOf = null,
			CancellationToken cancellationToken = default)
		{
			var auth = await _guard.AuthorizeAsync(PermRead, companyId, cancellationToken);
			if (!auth.Ok) return auth.Error!;
			return Ok(new { success = true, data = await _ap.AgingAsync(auth.CompanyId, asOf ?? DateTime.UtcNow) });
		}
	}
}
