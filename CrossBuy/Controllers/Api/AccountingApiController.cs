using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// Phase-0 read surface for the accounting module. JWT bearer (same scheme as the rest of the API).
	[ApiController]
	[Route("api/acc")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class AccountingApiController : ControllerBase
	{
		private readonly IChartOfAccountsService _coa;
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IGeneralLedgerService _gl;
		private readonly IAccountingPostingService _posting;
		private readonly IReceivableService _ar;
		private readonly IPayableService _ap;
		private readonly IFinancialStatementService _statements;
		public AccountingApiController(IChartOfAccountsService coa, CrossDbContext context,
			IJournalEntryService journals, IGeneralLedgerService gl, IAccountingPostingService posting,
			IReceivableService ar, IPayableService ap, IFinancialStatementService statements)
		{
			_coa = coa; _context = context; _journals = journals; _gl = gl; _posting = posting; _ar = ar; _ap = ap; _statements = statements;
		}

		// GET /api/acc/summary?companyId=1  → one-call finance dashboard for mobile
		[HttpGet("summary")]
		public async Task<IActionResult> Summary(int companyId = 1)
		{
			var today = DateTime.Today;
			var yearStart = new DateTime(today.Year, 1, 1);

			var pnl = await _statements.IncomeStatementAsync(companyId, yearStart, today);
			var arRows = await _ar.AgingAsync(companyId, today);
			var apRows = await _ap.AgingAsync(companyId, today);

			var cashIds = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Code.StartsWith("1101")).Select(a => a.ID).ToListAsync();
			var cash = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed") && cashIds.Contains(l.AccountId)
							  select (decimal?)(l.Debit - l.Credit)).SumAsync() ?? 0;

			var recent = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.Status == "Posted")
				.OrderByDescending(e => e.ID).Take(8)
				.Select(e => new
				{
					e.ID, e.EntryNo, e.EntryDate, e.JournalType, e.Description,
					amount = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0
				}).ToListAsync();

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
					customers = await _context.Customers.CountAsync(c => c.CompanyID == companyId),
					vendors = await _context.Vendors.CountAsync(v => v.CompanyID == companyId),
					recent
				}
			});
		}

		// GET /api/acc/account-types
		[HttpGet("account-types")]
		public async Task<IActionResult> AccountTypes()
		{
			var types = await _context.AccountTypes.AsNoTracking()
				.Select(t => new { t.ID, t.Code, t.Name, t.NameEn, t.NormalBalance, t.StatementType })
				.ToListAsync();
			return Ok(new { success = true, data = types });
		}

		// GET /api/acc/accounts?companyId=1  → full chart-of-accounts tree
		[HttpGet("accounts")]
		public async Task<IActionResult> Accounts(int companyId = 1)
		{
			var tree = await _coa.GetTreeAsync(companyId);
			return Ok(new { success = true, companyId, data = tree });
		}

		// ---- Journal entries ----

		// GET /api/acc/journals?companyId=1
		[HttpGet("journals")]
		public async Task<IActionResult> Journals(int companyId = 1)
		{
			var list = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId)
				.OrderByDescending(e => e.ID)
				.Select(e => new
				{
					e.ID, e.EntryNo, e.EntryDate, e.JournalType, e.Status, e.Description, e.SourceType,
					debit = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0
				})
				.ToListAsync();
			return Ok(new { success = true, data = list });
		}

		// GET /api/acc/journals/{id}
		[HttpGet("journals/{id:int}")]
		public async Task<IActionResult> Journal(int id)
		{
			var e = await _context.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == id);
			if (e == null) return NotFound(new { success = false, message = "غير موجود" });
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
			public int CompanyID { get; set; } = 1;
			public DateTime EntryDate { get; set; }
			public string? Description { get; set; }
			public string? DescriptionEn { get; set; }
			public bool PostNow { get; set; }
			public List<JournalLineInput> Lines { get; set; } = new();
		}

		// POST /api/acc/journals  (PostNow=false → draft, true → create & post)
		[HttpPost("journals")]
		public async Task<IActionResult> CreateJournal([FromBody] CreateJournalDto dto)
		{
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });
			var input = new JournalEntryInput
			{
				CompanyID = dto.CompanyID, EntryDate = dto.EntryDate,
				Description = dto.Description, DescriptionEn = dto.DescriptionEn, Lines = dto.Lines,
			};
			if (dto.PostNow)
			{
				var (ok, err, entry) = await _journals.CreateAndPostAsync(input, null);
				if (!ok) return BadRequest(new { success = false, message = err });
				return Ok(new { success = true, data = new { entry!.ID, entry.EntryNo, entry.Status } });
			}
			else
			{
				var (ok, err, entry) = await _journals.CreateDraftAsync(input, null);
				if (!ok) return BadRequest(new { success = false, message = err });
				return Ok(new { success = true, data = new { entry!.ID, entry.Status } });
			}
		}

		// POST /api/acc/journals/{id}/post
		[HttpPost("journals/{id:int}/post")]
		public async Task<IActionResult> Post(int id)
		{
			var (ok, err) = await _journals.PostAsync(id, null);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err });
		}

		// POST /api/acc/journals/{id}/reverse
		[HttpPost("journals/{id:int}/reverse")]
		public async Task<IActionResult> Reverse(int id, [FromBody] ReverseDto? dto)
		{
			var (ok, err, reversalId) = await _journals.ReverseAsync(id, null, dto?.Reason);
			return ok ? Ok(new { success = true, reversalId }) : BadRequest(new { success = false, message = err });
		}
		public class ReverseDto { public string? Reason { get; set; } }

		// ---- Reports ----

		// GET /api/acc/trial-balance?companyId=1&from=2026-01-01&to=2026-12-31&costCenterId=
		[HttpGet("trial-balance")]
		public async Task<IActionResult> TrialBalance(int companyId = 1, DateTime? from = null, DateTime? to = null, int? costCenterId = null)
		{
			var tb = await _gl.TrialBalanceAsync(companyId, from, to, costCenterId);
			return Ok(new { success = true, isBalanced = tb.IsBalanced, totalDebit = tb.TotalDebit, totalCredit = tb.TotalCredit, data = tb.Rows });
		}

		// GET /api/acc/ledger/{accountId}?companyId=1&from=&to=&costCenterId=
		[HttpGet("ledger/{accountId:int}")]
		public async Task<IActionResult> Ledger(int accountId, int companyId = 1, DateTime? from = null, DateTime? to = null, int? costCenterId = null)
		{
			var st = await _gl.AccountStatementAsync(companyId, accountId, from, to, costCenterId);
			if (st == null) return NotFound(new { success = false, message = "الحساب غير موجود" });
			return Ok(new { success = true, data = st });
		}

		// ---- Payroll bridge ----

		// GET /api/acc/payroll/preview?companyId=1&year=2026&month=1
		[HttpGet("payroll/preview")]
		public async Task<IActionResult> PayrollPreview(int companyId = 1, int year = 2026, int month = 1)
		{
			var p = await _posting.PreviewPayrollAsync(companyId, year, month);
			return Ok(new { success = true, data = p });
		}

		// POST /api/acc/payroll/post?companyId=1&year=2026&month=1
		[HttpPost("payroll/post")]
		public async Task<IActionResult> PayrollPost(int companyId = 1, int year = 2026, int month = 1)
		{
			var (ok, err, entryId) = await _posting.PostPayrollRunAsync(companyId, year, month, null);
			return ok ? Ok(new { success = true, entryId }) : BadRequest(new { success = false, message = err });
		}

		// ---- AR (customers / sales / receipts) ----
		public class CustomerDto { public string Name { get; set; } = ""; public string? NameEn { get; set; } public string? TaxRegNo { get; set; } public decimal? CreditLimit { get; set; } }
		public class SalesInvoiceDto { public int CompanyID { get; set; } = 1; public int CustomerId { get; set; } public DateTime InvoiceDate { get; set; } public string? Notes { get; set; } public List<SalesLineInput> Lines { get; set; } = new(); }
		public class ReceiptDto { public int CompanyID { get; set; } = 1; public int CustomerId { get; set; } public DateTime ReceiptDate { get; set; } public decimal Amount { get; set; } public string Method { get; set; } = "Cash"; public int CashAccountId { get; set; } }

		[HttpGet("ar/customers")]
		public async Task<IActionResult> Customers(int companyId = 1) => Ok(new { success = true, data = await _ar.GetCustomersAsync(companyId) });

		[HttpPost("ar/customers")]
		public async Task<IActionResult> CreateCustomer([FromBody] CustomerDto dto, int companyId = 1)
		{ var c = await _ar.CreateCustomerAsync(companyId, dto.Name, dto.NameEn, dto.TaxRegNo, dto.CreditLimit); return Ok(new { success = true, id = c.ID }); }

		[HttpPost("ar/invoices")]
		public async Task<IActionResult> CreateSalesInvoice([FromBody] SalesInvoiceDto dto)
		{ var (ok, err, inv) = await _ar.CreateSalesInvoiceAsync(dto.CompanyID, dto.CustomerId, dto.InvoiceDate, dto.Lines, dto.Notes, null); return ok ? Ok(new { success = true, id = inv!.ID, invoiceNo = inv.InvoiceNo, grand = inv.GrandTotal }) : BadRequest(new { success = false, message = err }); }

		[HttpPost("ar/receipts")]
		public async Task<IActionResult> CreateReceipt([FromBody] ReceiptDto dto)
		{ var (ok, err) = await _ar.CreateReceiptAsync(dto.CompanyID, dto.CustomerId, dto.ReceiptDate, dto.Amount, dto.Method, dto.CashAccountId, null, null); return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err }); }

		[HttpGet("ar/aging")]
		public async Task<IActionResult> ArAging(int companyId = 1, DateTime? asOf = null) => Ok(new { success = true, data = await _ar.AgingAsync(companyId, asOf ?? DateTime.UtcNow) });

		// ---- AP (vendors / purchases / payments) ----
		public class VendorDto { public string Name { get; set; } = ""; public string? NameEn { get; set; } public string? TaxRegNo { get; set; } }
		public class PurchaseInvoiceDto { public int CompanyID { get; set; } = 1; public int VendorId { get; set; } public DateTime InvoiceDate { get; set; } public string? Notes { get; set; } public List<PurchaseLineInput> Lines { get; set; } = new(); }
		public class PaymentDto { public int CompanyID { get; set; } = 1; public int VendorId { get; set; } public DateTime PaymentDate { get; set; } public decimal Amount { get; set; } public string Method { get; set; } = "Cash"; public int CashAccountId { get; set; } }

		[HttpGet("ap/vendors")]
		public async Task<IActionResult> Vendors(int companyId = 1) => Ok(new { success = true, data = await _ap.GetVendorsAsync(companyId) });

		[HttpPost("ap/vendors")]
		public async Task<IActionResult> CreateVendor([FromBody] VendorDto dto, int companyId = 1)
		{ var v = await _ap.CreateVendorAsync(companyId, dto.Name, dto.NameEn, dto.TaxRegNo); return Ok(new { success = true, id = v.ID }); }

		[HttpPost("ap/bills")]
		public async Task<IActionResult> CreatePurchaseInvoice([FromBody] PurchaseInvoiceDto dto)
		{ var (ok, err, inv) = await _ap.CreatePurchaseInvoiceAsync(dto.CompanyID, dto.VendorId, dto.InvoiceDate, dto.Lines, dto.Notes, null); return ok ? Ok(new { success = true, id = inv!.ID, invoiceNo = inv.InvoiceNo, grand = inv.GrandTotal }) : BadRequest(new { success = false, message = err }); }

		[HttpPost("ap/payments")]
		public async Task<IActionResult> CreatePayment([FromBody] PaymentDto dto)
		{ var (ok, err) = await _ap.CreatePaymentAsync(dto.CompanyID, dto.VendorId, dto.PaymentDate, dto.Amount, dto.Method, dto.CashAccountId, null, null); return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = err }); }

		[HttpGet("ap/aging")]
		public async Task<IActionResult> ApAging(int companyId = 1, DateTime? asOf = null) => Ok(new { success = true, data = await _ap.AgingAsync(companyId, asOf ?? DateTime.UtcNow) });
	}
}
