using CrossBuy.BL;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;

namespace CrossBuy.Tests
{
    // Stage 1 Hotfix A.1 — a recording stand-in for the seven accounting services the API controller depends on.
    //
    // WHY A RECORDER RATHER THAN THE REAL SERVICES
    //
    // The claim under test is about the CONTROLLER: which company and which actor it hands to the service layer, and
    // whether it reaches the service layer at all. The real services would answer a different question (does posting
    // work), need six further dependencies each (IStockService, INotificationService, ICurrencyService,
    // ICurrencyRounding, IBusinessEventService, IStringLocalizer, IFiscalPeriodService…), and would make a refusal
    // indistinguishable from a validation failure deeper down.
    //
    // The recorder makes the distinction exact: `Calls` is EMPTY when the guard refused, and holds exactly one entry
    // — with the company and the actor — when it allowed. That is the difference between "no journal was created"
    // and "a journal was created for the wrong company", which is the whole point of the hotfix.
    //
    // Every member the controller does NOT call throws. A stub that quietly returns default values would let a
    // future controller change slip past this suite; NotImplementedException makes it fail loudly instead.
    public sealed class ServiceRecorder :
        IJournalEntryService, IChartOfAccountsService, IGeneralLedgerService, IAccountingPostingService,
        IReceivableService, IPayableService, IFinancialStatementService
    {
        public sealed record Call(string Service, int CompanyId, int? UserId);

        public List<Call> Calls { get; } = new();

        private void Record(string service, int companyId, int? userId = null) => Calls.Add(new Call(service, companyId, userId));

        private static Exception Unexpected(string member)
            => new NotImplementedException(
                $"AccountingApiController is not expected to call {member}. If it now does, the hotfix tests must " +
                "cover that path rather than this stub silently returning a default.");

        // ---- IJournalEntryService ------------------------------------------------------------------------
        // Note: the entry id is what the controller returns to the client, so a plausible entry is handed back.

        public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId)
        { Record("CreateDraft", input.CompanyID, userId); return Task.FromResult((true, (string?)null, NewEntry(input.CompanyID))); }

        public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId)
        { Record("CreateAndPost", input.CompanyID, userId); return Task.FromResult((true, (string?)null, NewEntry(input.CompanyID))); }

        // PostAsync/ReverseAsync take no company — the controller has already proven ownership before calling, so
        // the recorded company is the one it validated and passed as the actor's scope.
        public Task<(bool ok, string? error)> PostAsync(int entryId, int? userId)
        { Record("Post", 0, userId); return Task.FromResult((true, (string?)null)); }

        public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason)
        { Record("Reverse", 0, userId); return Task.FromResult((true, (string?)null, (int?)99)); }

        public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId)
            => throw Unexpected(nameof(CreateAndPostNoTxAsync));

        private static JournalEntry NewEntry(int companyId) => new()
        {
            ID = 1, CompanyID = companyId, EntryNo = "JV-TEST", EntryDate = new DateTime(2026, 1, 1),
            FiscalPeriodId = 1, CurrencyId = 1, Status = "Posted",
        };

        // ---- IAccountingPostingService -------------------------------------------------------------------

        public Task<(bool ok, string? error, int? entryId)> PostPayrollRunAsync(int companyId, int year, int month, int? userId)
        { Record("PostPayrollRun", companyId, userId); return Task.FromResult((true, (string?)null, (int?)7)); }

        public Task<PayrollRunPreview> PreviewPayrollAsync(int companyId, int year, int month)
        { Record("PreviewPayroll", companyId); return Task.FromResult(new PayrollRunPreview()); }

        public Task<PayrollDisbursementView> GetDisbursementViewAsync(int companyId, int year, int month)
            => throw Unexpected(nameof(GetDisbursementViewAsync));
        public Task<(bool ok, string? error)> DisbursePayrollAsync(int companyId, int year, int month, int payFromGlAccountId, DateTime date, int? userId)
            => throw Unexpected(nameof(DisbursePayrollAsync));
        public Task<(bool ok, string? error)> RemitStatutoryAsync(int companyId, int year, int month, string component, int payFromGlAccountId, DateTime date, int? userId)
            => throw Unexpected(nameof(RemitStatutoryAsync));

        // ---- IReceivableService --------------------------------------------------------------------------

        public Task<Customer> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
        { Record("CreateCustomer", companyId); return Task.FromResult(new Customer { ID = 5, CompanyID = companyId, Name = name }); }

        public Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(int companyId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
        {
            Record("CreateSalesInvoice", companyId, userId);
            return Task.FromResult((true, (string?)null, (SalesInvoice?)new SalesInvoice
            { ID = 3, CompanyID = companyId, InvoiceNo = "SI-1", InvoiceDate = date, CustomerId = customerId, Status = "Posted" }));
        }

        public Task<(bool ok, string? error)> CreateReceiptAsync(int companyId, int customerId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
        { Record("CreateReceipt", companyId, userId); return Task.FromResult((true, (string?)null)); }

        public Task<List<Customer>> GetCustomersAsync(int companyId)
        { Record("GetCustomers", companyId); return Task.FromResult(new List<Customer>()); }

        public Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf)
        { Record("ArAging", companyId); return Task.FromResult(new List<AgingRow>()); }

        public Task<(List<Customer> rows, int total)> SearchCustomersAsync(int companyId, string? q, bool? active, int page, int pageSize)
            => throw Unexpected(nameof(SearchCustomersAsync));
        public Task<List<(string value, string name)>> SuggestCustomersAsync(int companyId, string? term, int take = 10)
            => throw Unexpected(nameof(SuggestCustomersAsync));
        public Task<(bool ok, string? error)> SaveCustomerAsync(int companyId, Customer dto)
            => throw Unexpected(nameof(SaveCustomerAsync));
        public Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(int companyId, int invoiceId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            => throw Unexpected(nameof(EditSalesInvoiceAsync));
        Task<List<SalesInvoice>> IReceivableService.GetInvoicesAsync(int companyId)
            => throw Unexpected("IReceivableService.GetInvoicesAsync");
        public Task<decimal> CustomerOutstandingAsync(int companyId, int customerId)
            => throw Unexpected(nameof(CustomerOutstandingAsync));
        public Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int companyId)
            => throw Unexpected(nameof(GetCustomerAnalyticsAsync));
        public Task<List<SalesReturn>> GetSalesReturnsAsync(int companyId)
            => throw Unexpected(nameof(GetSalesReturnsAsync));
        public Task<SalesReturn?> GetSalesReturnAsync(int companyId, int id)
            => throw Unexpected(nameof(GetSalesReturnAsync));
        public Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(int companyId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null)
            => throw Unexpected(nameof(CreateSalesReturnAsync));
        public Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(int companyId, int returnId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null)
            => throw Unexpected(nameof(EditSalesReturnAsync));

        // ---- IPayableService ----------------------------------------------------------------------------

        public Task<Vendor> CreateVendorAsync(int companyId, string name, string? nameEn, string? taxNo)
        { Record("CreateVendor", companyId); return Task.FromResult(new Vendor { ID = 6, CompanyID = companyId, Name = name }); }

        public Task<(bool ok, string? error, PurchaseInvoice? inv)> CreatePurchaseInvoiceAsync(int companyId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
        {
            Record("CreatePurchaseInvoice", companyId, userId);
            return Task.FromResult((true, (string?)null, (PurchaseInvoice?)new PurchaseInvoice
            { ID = 4, CompanyID = companyId, InvoiceNo = "PV-1", InvoiceDate = date, VendorId = vendorId, Status = "Posted" }));
        }

        public Task<(bool ok, string? error)> CreatePaymentAsync(int companyId, int vendorId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, decimal whtRate = 0, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
        { Record("CreatePayment", companyId, userId); return Task.FromResult((true, (string?)null)); }

        public Task<List<Vendor>> GetVendorsAsync(int companyId)
        { Record("GetVendors", companyId); return Task.FromResult(new List<Vendor>()); }

        Task<List<AgingRow>> IPayableService.AgingAsync(int companyId, DateTime asOf)
        { Record("ApAging", companyId); return Task.FromResult(new List<AgingRow>()); }

        public Task<(List<Vendor> rows, int total)> SearchVendorsAsync(int companyId, string? q, bool? active, int page, int pageSize)
            => throw Unexpected(nameof(SearchVendorsAsync));
        public Task<List<(string value, string name)>> SuggestVendorsAsync(int companyId, string? term, int take = 10)
            => throw Unexpected(nameof(SuggestVendorsAsync));
        public Task<(bool ok, string? error)> SaveVendorAsync(int companyId, Vendor dto)
            => throw Unexpected(nameof(SaveVendorAsync));
        public Task<(bool ok, string? error, PurchaseInvoice? inv)> EditPurchaseInvoiceAsync(int companyId, int invoiceId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            => throw Unexpected(nameof(EditPurchaseInvoiceAsync));
        public Task<(bool ok, string? error, PurchaseInvoice? inv)> MatchGoodsReceiptToInvoiceAsync(int companyId, int goodsReceiptId, DateTime invoiceDate, decimal invoiceAmount, int? userId)
            => throw Unexpected(nameof(MatchGoodsReceiptToInvoiceAsync));
        Task<List<PurchaseInvoice>> IPayableService.GetInvoicesAsync(int companyId)
            => throw Unexpected("IPayableService.GetInvoicesAsync");
        public Task<List<PurchaseReturn>> GetPurchaseReturnsAsync(int companyId)
            => throw Unexpected(nameof(GetPurchaseReturnsAsync));
        public Task<PurchaseReturn?> GetPurchaseReturnAsync(int companyId, int id)
            => throw Unexpected(nameof(GetPurchaseReturnAsync));
        public Task<(bool ok, string? error, PurchaseReturn? ret)> CreatePurchaseReturnAsync(int companyId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId)
            => throw Unexpected(nameof(CreatePurchaseReturnAsync));
        public Task<(bool ok, string? error, PurchaseReturn? ret)> EditPurchaseReturnAsync(int companyId, int returnId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId)
            => throw Unexpected(nameof(EditPurchaseReturnAsync));

        // ---- IChartOfAccountsService --------------------------------------------------------------------

        public Task<List<AccountNode>> GetTreeAsync(int companyId)
        { Record("GetTree", companyId); return Task.FromResult(new List<AccountNode>()); }

        public Task<List<AccountNode>> GetFlatAsync(int companyId, bool postableOnly = false)
            => throw Unexpected(nameof(GetFlatAsync));
        public Task<List<AccountType>> GetAccountTypesAsync()
            => throw Unexpected(nameof(GetAccountTypesAsync));
        public Task<Account?> GetAsync(int companyId, int id)
            => throw Unexpected(nameof(GetAsync));
        public Task<(bool ok, string? error, int? id)> CreateAsync(int companyId, string code, string nameAr, string nameEn, int accountTypeId, int? parentId, bool isPostable, bool requireCostCenter, string? cashFlowCategory, int? userId)
            => throw Unexpected(nameof(CreateAsync));
        public Task<(bool ok, string? error)> UpdateAsync(int companyId, int id, string code, string nameAr, string nameEn, bool isPostable, bool isActive, bool requireCostCenter, string? cashFlowCategory, int? userId)
            => throw Unexpected(nameof(UpdateAsync));

        // ---- IGeneralLedgerService ----------------------------------------------------------------------

        public Task<TrialBalanceResult> TrialBalanceAsync(int companyId, DateTime? from, DateTime? to, int? costCenterId = null)
        { Record("TrialBalance", companyId); return Task.FromResult(new TrialBalanceResult()); }

        public Task<AccountStatement?> AccountStatementAsync(int companyId, int accountId, DateTime? from, DateTime? to, int? costCenterId = null)
        { Record("AccountStatement", companyId); return Task.FromResult<AccountStatement?>(new AccountStatement()); }

        // ---- IFinancialStatementService -----------------------------------------------------------------

        public Task<IncomeStatement> IncomeStatementAsync(int companyId, DateTime from, DateTime to)
        { Record("IncomeStatement", companyId); return Task.FromResult(new IncomeStatement()); }

        public Task<BalanceSheet> BalanceSheetAsync(int companyId, DateTime asOf)
            => throw Unexpected(nameof(BalanceSheetAsync));
        public Task<CashFlowStatement> CashFlowAsync(int companyId, DateTime from, DateTime to)
            => throw Unexpected(nameof(CashFlowAsync));
    }
}
