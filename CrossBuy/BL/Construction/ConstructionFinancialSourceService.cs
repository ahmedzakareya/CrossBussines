using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionSourceIssue(string Code, string Source, int Id);
public sealed record ConstructionOrderLineSource(int Id, int LineNo, int? ItemId, decimal Quantity,
    decimal UnitPrice, decimal Discount, decimal TaxRate, decimal NetDocumentAmount, decimal ReceivedQuantity);
public sealed record ConstructionOrderSource(int Id, string? Number, DateTime Date, string Status, int VendorId,
    int? CurrencyId, string? CurrencyCode, decimal? ExchangeRate, decimal StoredSubtotal, decimal StoredTax,
    decimal StoredGrandTotal, string Classification, decimal? OpenCommitmentNetDocumentAmount,
    int? ProvenInvoiceId, IReadOnlyList<ConstructionOrderLineSource> Lines);
public sealed record ConstructionInvoiceSource(int Id, int VendorId, string Status, int? CurrencyId,
    decimal DocumentGrandTotal, decimal? StoredBaseGrandTotal, int? JournalEntryId, int? ProvenPurchaseOrderId);
public sealed record ConstructionLedgerSource(int EntryId, int LineId, DateTime EntryDate, int FiscalPeriodId,
    string EntryStatus, int AccountId, string? AccountTypeCode, string Classification,
    decimal DebitStoredBase, decimal CreditStoredBase, decimal? ExpenseNetStoredBase,
    int HeaderCurrencyId, int? LineCurrencyId, string? SourceType, int? SourceId,
    int? ProvenFunctionalCurrencyId, int? ProvenFunctionalCurrencyDecimals);
public sealed record ConstructionSubcontractSource(int Id, string Status, decimal? ContractValue);
public sealed record ConstructionFinancialSourceSnapshot(int CompanyId, int ProjectId, DateTime? LedgerFrom,
    DateTime? LedgerThrough, string? PolicyMode, int? BaselineId, int? BaselineCurrencyId,
    IReadOnlyList<ConstructionOrderSource> Orders, IReadOnlyList<ConstructionInvoiceSource> Invoices,
    IReadOnlyList<ConstructionLedgerSource> Ledger, IReadOnlyList<ConstructionSubcontractSource> Subcontracts,
    IReadOnlyList<ConstructionSourceIssue> Issues, string SourceVersion)
{
    // This register establishes provenance. Dimension allocations and historical base-currency identity
    // are not yet complete; it must never authorize spending or present an invented remaining budget.
    public bool CanEvaluateBudget => false;
    public decimal? RemainingBudget => null;
    public decimal? ProjectActualCost => null;
    public bool HistoricalCommitmentsAsOfSupported => false;
}
public sealed record ConstructionFinancialSourceResult(bool Ok, string Code, ConstructionFinancialSourceSnapshot? Snapshot = null);

public interface IConstructionFinancialSourceService
{
    Task<ConstructionFinancialSourceResult> GetAsync(BusinessContext context, int projectId,
        DateTime? from = null, DateTime? through = null, CancellationToken ct = default);
}

// Read-only, source-backed register. No client totals, note-text matching, FX inference, GL/stock writes,
// automatic allocation or policy selection. Authoritative source IDs remain the identity of every fact.
public sealed class ConstructionFinancialSourceService(CrossDbContext db, IProjectsAccessService access)
    : IConstructionFinancialSourceService
{
    public async Task<ConstructionFinancialSourceResult> GetAsync(BusinessContext c, int projectId,
        DateTime? from = null, DateTime? through = null, CancellationToken ct = default)
    {
        if (!c.IsAuthenticated || c.EmployeeId is not > 0 || c.CompanyId <= 0 || db.CompanyScope.FilterCompanyId != c.CompanyId ||
            !await access.CanAsync(c, ProjectsActions.BudgetView, new PermissionTarget { ProjectId = projectId }, ct))
            return new(false, "AccessDenied");
        if (from.HasValue && through.HasValue && from.Value.Date > through.Value.Date) return new(false, "InvalidPeriod");
        // A report must not label a caller's uncommitted financial rows as committed facts. Never commit,
        // roll back, flush or clear a caller's unit of work to manufacture a report.
        if (db.Database.CurrentTransaction != null) return new(false, "CommittedSourceReadRequired");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct))
            return new(false, "ProjectNotFound");
        var issues = new List<ConstructionSourceIssue>();
        var headers = await db.PurchaseOrders.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId)
            .OrderBy(x => x.ID).ToListAsync(ct);
        var orderIds = headers.Select(x => x.ID).ToList();
        var lines = await db.PurchaseOrderLines.AsNoTracking().Where(x => orderIds.Contains(x.PurchaseOrderId))
            .OrderBy(x => x.PurchaseOrderId).ThenBy(x => x.LineNo).ThenBy(x => x.ID).ToListAsync(ct);
        var links = await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && orderIds.Contains(x.PurchaseOrderId)).OrderBy(x => x.ID).ToListAsync(ct);
        var invoices = await db.PurchaseInvoices.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId)
            .OrderBy(x => x.ID).ToListAsync(ct);
        var invoiceIds = invoices.Select(x => x.ID).ToList();
        var invoicePostings = await db.JournalEntries.AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            x.Status == "Posted" && x.SourceType == "PurchaseInvoice" && x.SourceId.HasValue && invoiceIds.Contains(x.SourceId.Value))
            .Select(x => new { x.ID, x.SourceId }).ToListAsync(ct);
        var currencies = await db.Currencies.AsNoTracking().ToDictionaryAsync(x => x.ID, x => x.Code, ct);
        var orderSources = new List<ConstructionOrderSource>();
        foreach (var header in headers)
        {
            var own = lines.Where(x => x.PurchaseOrderId == header.ID).Select(x => new ConstructionOrderLineSource(
                x.ID, x.LineNo, x.ItemId, x.Qty, x.UnitPrice, x.DiscountAmount, x.TaxRate, x.LineTotal, x.ReceivedQty)).ToArray();
            var link = links.SingleOrDefault(x => x.PurchaseOrderId == header.ID);
            var invoice = link == null ? null : invoices.SingleOrDefault(x => x.ID == link.PurchaseInvoiceId && x.VendorId == header.VendorId);
            var classification = header.Status switch
            {
                "Approved" or "Received" => "Open",
                "Cancelled" => "Released",
                "Draft" => "DraftProposal",
                "Closed" => invoice != null ? "TransferredToInvoice" : "UnprovenClosed",
                _ => "UnknownStatus"
            };
            if (link != null && (invoice == null || header.Status != "Closed"))
            { classification = "InconsistentConversion"; issues.Add(new("ConversionLinkMismatch", "PurchaseOrder", header.ID)); }
            if (invoice != null && invoice.Status != "Posted")
            { classification = "InvoiceHistoryRequiresReview"; issues.Add(new("InvoiceHistoryRequiresReview", "PurchaseOrder", header.ID)); }
            else if (invoice != null && !invoicePostings.Any(x => x.ID == invoice.JournalEntryId && x.SourceId == invoice.ID))
            { classification = "InvoicePostingNotProven"; issues.Add(new("InvoicePostingNotProven", "PurchaseOrder", header.ID)); }
            var validCurrency = header.CurrencyId is > 0 && currencies.TryGetValue(header.CurrencyId.Value, out var code) && !string.IsNullOrWhiteSpace(code);
            if (!validCurrency) issues.Add(new("DocumentCurrencyNotProven", "PurchaseOrder", header.ID));
            var reconciled = own.Length > 0 && own.All(x => x.NetDocumentAmount >= 0 && x.Quantity > 0 && x.UnitPrice >= 0 && x.Discount >= 0)
                && own.Sum(x => x.NetDocumentAmount) == header.SubTotal && header.SubTotal + header.TaxTotal == header.GrandTotal;
            if (!reconciled) issues.Add(new("OrderTotalsRequireReconciliation", "PurchaseOrder", header.ID));
            if (classification is "UnprovenClosed" or "UnknownStatus") issues.Add(new(classification, "PurchaseOrder", header.ID));
            // Only commercial outstanding net, in this document's explicit currency. A transferred AP
            // invoice is NOT recognized project expense, and reversal never silently reopens the order.
            decimal? outstanding = validCurrency && reconciled ? classification switch
                { "Open" => header.SubTotal, "Released" or "TransferredToInvoice" => 0m, _ => null } : null;
            orderSources.Add(new(header.ID, header.OrderNo, header.OrderDate, header.Status, header.VendorId,
                header.CurrencyId, validCurrency ? currencies[header.CurrencyId!.Value] : null, header.ExchangeRate,
                header.SubTotal, header.TaxTotal, header.GrandTotal, classification, outstanding,
                invoice?.ID, Array.AsReadOnly(own)));
        }
        var invoiceSources = invoices.Select(x =>
        {
            var proven = orderSources.SingleOrDefault(o => o.ProvenInvoiceId == x.ID);
            if (proven == null) issues.Add(new("InvoiceSourceNotProven", "PurchaseInvoice", x.ID));
            return new ConstructionInvoiceSource(x.ID, x.VendorId, x.Status, x.CurrencyId, x.GrandTotal, x.GrandTotalBase, x.JournalEntryId, proven?.Id);
        }).ToArray();

        // Posted originals remain financial history after being marked Reversed. Include their actual
        // reversing entries in their own periods; filtering originals out would invent negative costs.
        var ledgerQuery = from entry in db.JournalEntries.AsNoTracking()
                          join line in db.JournalEntryLines.AsNoTracking() on entry.ID equals line.JournalEntryId
                          where entry.CompanyID == c.CompanyId && line.ProjectId == projectId && (entry.Status == "Posted" || entry.Status == "Reversed")
                          join account in db.Accounts.AsNoTracking().Where(a => a.CompanyID == c.CompanyId) on line.AccountId equals account.ID into accounts
                          from account in accounts.DefaultIfEmpty()
                          join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID into types
                          from type in types.DefaultIfEmpty()
                          select new { Entry = entry, Line = line, TypeCode = type == null ? null : type.Code, Statement = type == null ? null : type.StatementType };
        if (from.HasValue) ledgerQuery = ledgerQuery.Where(x => x.Entry.EntryDate >= from.Value.Date);
        if (through.HasValue) ledgerQuery = ledgerQuery.Where(x => x.Entry.EntryDate.Date <= through.Value.Date);
        var raw = await ledgerQuery.OrderBy(x => x.Entry.ID).ThenBy(x => x.Line.ID).ToListAsync(ct);
        var ledgerSources = raw.Select(x =>
        {
            var classification = x.TypeCode == "EXP" && x.Statement == "IncomeStatement" ? "ExpensePosting"
                : (x.TypeCode is "ASSET" or "LIAB" or "EQUITY") && x.Statement == "BalanceSheet" ? "BalanceSheetPosting"
                : x.TypeCode == "REV" && x.Statement == "IncomeStatement" ? "RevenuePosting" : "UnclassifiedAccount";
            if (classification == "UnclassifiedAccount") issues.Add(new("AccountClassificationNotProven", "JournalEntryLine", x.Line.ID));
            // Header CurrencyId is the journal/document currency; current company defaults cannot prove
            // the historic functional identity of the stored base debits/credits. No cross-currency sum.
            var proven = x.Entry.FunctionalCurrencyId is > 0 && x.Entry.FunctionalCurrencyDecimals is >= 0 and <= 28 &&
                currencies.TryGetValue(x.Entry.FunctionalCurrencyId.Value, out var functionalCode) && !string.IsNullOrWhiteSpace(functionalCode);
            if (!proven) issues.Add(new("HistoricalBaseCurrencyIdentityNotProven", "JournalEntryLine", x.Line.ID));
            return new ConstructionLedgerSource(x.Entry.ID, x.Line.ID, x.Entry.EntryDate, x.Entry.FiscalPeriodId,
                x.Entry.Status, x.Line.AccountId, x.TypeCode, classification, x.Line.Debit, x.Line.Credit,
                classification == "ExpensePosting" ? x.Line.Debit - x.Line.Credit : null,
                x.Entry.CurrencyId, x.Line.CurrencyId, x.Entry.SourceType, x.Entry.SourceId,
                proven ? x.Entry.FunctionalCurrencyId : null, proven ? x.Entry.FunctionalCurrencyDecimals : null);
        }).ToArray();
        var subs = await db.Subcontracts.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId)
            .OrderBy(x => x.ID).Select(x => new ConstructionSubcontractSource(x.ID, x.Status, x.ContractValue)).ToListAsync(ct);
        foreach (var sub in subs)
        {
            if (sub.ContractValue == null) issues.Add(new("ContractCapUnknown", "Subcontract", sub.Id));
            issues.Add(new("SubcontractCurrencyNotProven", "Subcontract", sub.Id));
        }
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline && x.Status == "Approved", ct);
        if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) issues.Add(new("PolicyNotConfigured", "Project", projectId));
        if (baseline == null) issues.Add(new("ApprovedBaselineMissing", "Project", projectId));
        issues.Add(new("WbsCostCodeAllocationsIncomplete", "Project", projectId));
        var orderedIssues = issues.Distinct().OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Id).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var sources = new ConstructionFinancialSourceSnapshot(c.CompanyId, projectId, from?.Date, through?.Date,
            policy?.Mode, baseline?.ID, baseline?.CurrencyId, orderSources.AsReadOnly(), Array.AsReadOnly(invoiceSources),
            Array.AsReadOnly(ledgerSources), subs.AsReadOnly(), Array.AsReadOnly(orderedIssues), "");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Sources = sources, PolicyVersion = policy?.ConcurrencyToken, BaselineVersion = baseline?.RowVersion })));
        await tx.CommitAsync(ct);
        return new(true, "SourceRegisterOnly", sources with { SourceVersion = fingerprint });
    }
}
