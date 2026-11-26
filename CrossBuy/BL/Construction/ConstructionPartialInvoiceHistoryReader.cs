using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionPartialInvoiceHistory(bool Ok, string Code,
    IReadOnlyList<ConstructionInvoiceBilledQuantity>? Quantities = null,
    IReadOnlyDictionary<int, IReadOnlyList<ConstructionInvoiceCommercialHistory>>? Values = null);

// Internal evidence reader, not an access gateway. The caller must authorize, own a serializable
// transaction and acquire the construction project lock before using a result to issue an invoice.
// No financial ledger is written and no posted history is inferred from tracked objects.
public static class ConstructionPartialInvoiceHistoryReader
{
    public static async Task<ConstructionPartialInvoiceHistory> ReadAsync(CrossDbContext db,
        ConstructionInvoiceScope scope, int decimals, CancellationToken ct = default)
    {
        ConstructionPartialInvoiceHistory Refuse(string code) => new(false, code);
        if (scope.CompanyId <= 0 || scope.ProjectId <= 0 || scope.OrderId <= 0 ||
            db.CompanyScope.FilterCompanyId != scope.CompanyId || db.CompanyScope.AllowsCrossCompany ||
            db.Database.CurrentTransaction == null ||
            db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel != System.Data.IsolationLevel.Serializable || decimals is < 0 or > 8)
            return Refuse("PartialInvoiceHistoryScopeUnproven");
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
            x.ID == scope.OrderId && x.CompanyID == scope.CompanyId && x.ProjectId == scope.ProjectId, ct);
        if (order == null || order.Lines.Count == 0 || order.Status is not ("Approved" or "Received") || order.ExchangeRate != 1m || order.CurrencyId == null ||
            await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x => x.CompanyID == scope.CompanyId && x.PurchaseOrderId == scope.OrderId, ct))
            return Refuse("PartialInvoiceHistorySourceUnproven");
        var headers = await db.Set<ConstructionPartialInvoice>().AsNoTracking().Where(x =>
            x.CompanyID == scope.CompanyId && x.PurchaseOrderId == scope.OrderId).OrderBy(x => x.ID).ToListAsync(ct);
        var slices = await db.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().Where(x =>
            x.CompanyID == scope.CompanyId && x.PurchaseOrderId == scope.OrderId).ToListAsync(ct);
        if (headers.Any(x => !x.IsSealed || x.ProjectId != scope.ProjectId || x.RequestId == Guid.Empty ||
                x.CreatedBy <= 0 || !Digest(x.RequestHash) || !Digest(x.SourceVersion)) ||
            headers.Select(x => x.RequestId).Distinct().Count() != headers.Count ||
            headers.Select(x => x.PurchaseInvoiceId).Distinct().Count() != headers.Count ||
            slices.Any(x => !headers.Any(h => h.ID == x.HeaderId && h.PurchaseInvoiceId == x.PurchaseInvoiceId &&
                h.JournalEntryId == x.JournalEntryId && h.ProjectId == x.ProjectId) || x.Quantity <= 0))
            return Refuse("PartialInvoiceHistoryUnsealedOrInconsistent");
        var orders = order.Lines.ToDictionary(x => x.ID);
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(x => x.CompanyID == scope.CompanyId &&
            x.PurchaseOrderId == scope.OrderId).ToListAsync(ct);
        var receiptIds = receipts.Select(x => x.ID).ToArray();
        var received = await db.GoodsReceiptLines.AsNoTracking().Where(x => receiptIds.Contains(x.GoodsReceiptId)).ToListAsync(ct);
        var quantities = new List<ConstructionInvoiceBilledQuantity>();
        var values = orders.Keys.ToDictionary(x => x, _ => new List<ConstructionInvoiceCommercialHistory>());
        decimal Round(decimal value) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
        try
        {
            foreach (var header in headers)
            {
                var invoice = await db.PurchaseInvoices.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
                    x.CompanyID == scope.CompanyId && x.ID == header.PurchaseInvoiceId, ct);
                var entry = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
                    x.CompanyID == scope.CompanyId && x.ID == header.JournalEntryId, ct);
                var own = slices.Where(x => x.HeaderId == header.ID).ToArray();
                if (invoice == null || entry == null || own.Length == 0 || invoice.Lines.Count == 0 ||
                    invoice.Status != "Posted" || invoice.ProjectId != scope.ProjectId || invoice.VendorId != order.VendorId ||
                    invoice.CurrencyId != order.CurrencyId || invoice.ExchangeRate != 1m || invoice.JournalEntryId != entry.ID ||
                    entry.Status != "Posted" || entry.ReversedByEntryId != null || entry.SourceType != "PurchaseInvoice" || entry.SourceId != invoice.ID ||
                    entry.CurrencyId != order.CurrencyId || entry.FunctionalCurrencyId != order.CurrencyId || entry.FunctionalCurrencyDecimals != decimals ||
                    entry.Lines.Any(x => x.ProjectId != scope.ProjectId || x.Debit < 0 || x.Credit < 0) ||
                    entry.Lines.Sum(x => x.Debit) != invoice.GrandTotal || entry.Lines.Sum(x => x.Credit) != invoice.GrandTotal ||
                    invoice.SubTotal != invoice.Lines.Sum(x => x.LineTotal) ||
                    invoice.TaxTotal != invoice.Lines.Sum(x => Round(x.LineTotal * x.TaxRate / 100m)) ||
                    invoice.GrandTotal != invoice.SubTotal + invoice.TaxTotal || invoice.SubTotalBase != invoice.SubTotal ||
                    invoice.TaxTotalBase != invoice.TaxTotal || invoice.GrandTotalBase != invoice.GrandTotal ||
                    own.Any(x => !invoice.Lines.Any(l => l.ID == x.PurchaseInvoiceLineId)) ||
                    own.Select(x => (x.PurchaseInvoiceLineId, x.GoodsReceiptLineId)).Distinct().Count() != own.Length)
                    return Refuse("PartialInvoicePostedHistoryUnproven");
                var netIds = new HashSet<int>();
                foreach (var line in invoice.Lines)
                {
                    var parts = own.Where(x => x.PurchaseInvoiceLineId == line.ID).ToArray();
                    if (parts.Length == 0 || parts.Select(x => x.PurchaseOrderLineId).Distinct().Count() != 1 ||
                        parts.Select(x => x.JournalEntryLineId).Distinct().Count() != 1 || parts.Sum(x => x.Quantity) != line.Qty ||
                        !orders.TryGetValue(parts[0].PurchaseOrderLineId, out var original))
                        return Refuse("PartialInvoiceLineHistoryUnproven");
                    var gl = entry.Lines.SingleOrDefault(x => x.ID == parts[0].JournalEntryLineId);
                    if (gl == null || !netIds.Add(gl.ID) || gl.LineNo != line.LineNo || gl.AccountId != line.ExpenseAccountId ||
                        gl.CostCenterId != line.CostCenterId || gl.Debit != line.LineTotal || gl.Credit != 0 ||
                        line.ItemId != null || line.WarehouseId != null || line.UnitPrice != original.UnitPrice || line.TaxRate != original.TaxRate)
                        return Refuse("PartialInvoiceNetPostingUnproven");
                    var account = await (from item in db.Items.AsNoTracking()
                        join category in db.ItemCategories.AsNoTracking() on item.ItemCategoryId equals category.ID
                        join a in db.Accounts.AsNoTracking() on category.GrniAccountId equals (int?)a.ID
                        join t in db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
                        where item.CompanyID == scope.CompanyId && category.CompanyID == scope.CompanyId && a.CompanyID == scope.CompanyId &&
                            item.ID == original.ItemId && item.ItemType == "Stockable" && t.Code == "LIAB" && t.StatementType == "BalanceSheet"
                        select a.ID).SingleOrDefaultAsync(ct);
                    if (account <= 0 || account != line.ExpenseAccountId) return Refuse("PartialInvoiceClearingAccountUnproven");
                    var commercial = ConstructionPartialInvoiceValueRules.Validate(new(original.Qty, original.UnitPrice,
                        original.DiscountAmount, original.TaxRate, original.LineTotal, Round(original.LineTotal * original.TaxRate / 100m)),
                        values[original.ID], line.Qty, decimals);
                    if (!commercial.Ok || commercial.Discount != line.DiscountAmount || commercial.Net != line.LineTotal ||
                        commercial.Tax != Round(line.LineTotal * line.TaxRate / 100m)) return Refuse("PartialInvoiceCommercialHistoryUnproven");
                    values[original.ID].Add(new(line.Qty, line.DiscountAmount, line.LineTotal, commercial.Tax));
                    foreach (var part in parts)
                    {
                        var receipt = receipts.SingleOrDefault(x => x.ID == part.GoodsReceiptId);
                        var receivedLine = received.SingleOrDefault(x => x.ID == part.GoodsReceiptLineId);
                        if (receipt == null || receivedLine == null || receivedLine.GoodsReceiptId != receipt.ID ||
                            receivedLine.PurchaseOrderLineId != original.ID || receivedLine.ItemId != original.ItemId || receivedLine.UoMId != original.UoMId ||
                            receipt.Status != "Posted" || receipt.InvoiceId != null || receipt.VendorId != order.VendorId ||
                            receipt.CurrencyId != order.CurrencyId || receipt.ExchangeRate != 1m || receivedLine.Qty <= 0)
                            return Refuse("PartialInvoiceReceiptHistoryUnproven");
                        quantities.Add(new(scope, part.ID, header.RequestId, invoice.ID, line.ID, receivedLine.ID, part.Quantity, "Posted"));
                    }
                }
                var other = entry.Lines.Where(x => !netIds.Contains(x.ID)).ToArray();
                var payable = await db.Vendors.AsNoTracking().Where(x => x.CompanyID == scope.CompanyId && x.ID == order.VendorId)
                    .Select(x => x.ControlAccountId).SingleOrDefaultAsync(ct);
                var vat = await db.Accounts.AsNoTracking().Where(x => x.CompanyID == scope.CompanyId && x.Code == "110401")
                    .Select(x => x.ID).SingleOrDefaultAsync(ct);
                var settlementAccounts = await (from a in db.Accounts.AsNoTracking()
                    join t in db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
                    where a.CompanyID == scope.CompanyId && (a.ID == payable || a.ID == vat)
                    select new { a.ID, t.Code, t.StatementType }).ToListAsync(ct);
                if (!settlementAccounts.Any(x => x.ID == payable && x.Code == "LIAB" && x.StatementType == "BalanceSheet") ||
                    invoice.TaxTotal > 0 && !settlementAccounts.Any(x => x.ID == vat && x.Code == "ASSET" && x.StatementType == "BalanceSheet") ||
                    other.Length != (invoice.TaxTotal > 0 ? 2 : 1) ||
                    other.Count(x => x.AccountId == payable && x.Debit == 0 && x.Credit == invoice.GrandTotal) != 1 ||
                    invoice.TaxTotal > 0 && other.Count(x => x.AccountId == vat && x.Debit == invoice.TaxTotal && x.Credit == 0) != 1)
                    return Refuse("PartialInvoiceSettlementPostingUnproven");
            }
            if (quantities.GroupBy(x => x.ReceiptLineId).Any(x => x.Sum(y => y.Billed) > received.Single(y => y.ID == x.Key).Qty) ||
                slices.GroupBy(x => x.PurchaseOrderLineId).Any(x => !orders.ContainsKey(x.Key) || x.Sum(y => y.Quantity) > orders[x.Key].Qty))
                return Refuse("PartialInvoiceHistoryQuantityExceeded");
            return new(true, "ProvenPartialInvoiceHistory", quantities.AsReadOnly(),
                new System.Collections.ObjectModel.ReadOnlyDictionary<int, IReadOnlyList<ConstructionInvoiceCommercialHistory>>(
                    values.ToDictionary(x => x.Key, x => (IReadOnlyList<ConstructionInvoiceCommercialHistory>)x.Value.AsReadOnly())));
        }
        catch (OverflowException) { return Refuse("PartialInvoiceHistoryArithmeticOverflow"); }
    }

    private static bool Digest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
