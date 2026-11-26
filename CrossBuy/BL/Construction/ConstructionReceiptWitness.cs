using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Construction;

// Read-only fulfillment proof. Receipt quantities may evolve; the approved commercial basis may not.
// Asset capitalization, receipt cancellation/reversal and invoiced stock consumption need their own coverage.
internal static class ConstructionReceiptWitness
{
    internal static async Task<bool> ProvenAsync(CrossDbContext db, int company, PurchaseOrder order,
        IReadOnlyList<PurchaseOrderLine> ordered, CancellationToken ct, int? clearingInvoiceId = null,
        int? authorizedReceiptWarehouseId = null)
    {
        if (company <= 0 || order.CompanyID != company || db.CompanyScope.FilterCompanyId != company) return false;
        var receipts = await db.GoodsReceipts.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.CompanyID == company && x.PurchaseOrderId == order.ID).ToListAsync(ct);
        if (receipts.Count == 0) return !clearingInvoiceId.HasValue && ordered.All(x => x.ReceivedQty == 0) && order.Status != "Received";
        // All contributing sources must be read from one coherent owner transaction.
        if (db.Database.CurrentTransaction == null ||
            db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel != System.Data.IsolationLevel.Serializable) return false;
        var receiptIds = receipts.Select(x => x.ID).ToArray();
        var movements = await db.StockMovements.AsNoTracking().Where(x => x.CompanyID == company &&
            x.SourceType == "Receipt" && x.SourceId.HasValue && receiptIds.Contains(x.SourceId.Value)).ToListAsync(ct);
        var entryIds = movements.Where(x => x.JournalEntryId.HasValue).Select(x => x.JournalEntryId!.Value).ToArray();
        var entries = await db.JournalEntries.AsNoTracking().Where(x => x.CompanyID == company && entryIds.Contains(x.ID)).ToListAsync(ct);
        var journalLines = await db.JournalEntryLines.AsNoTracking().Where(x => entryIds.Contains(x.JournalEntryId)).ToListAsync(ct);
        var warehouseIds = receipts.Select(x => x.WarehouseId).Distinct().ToArray();
        if (await db.Warehouses.AsNoTracking().CountAsync(x => x.CompanyID == company && warehouseIds.Contains(x.ID), ct) != warehouseIds.Length) return false;
        var received = ordered.ToDictionary(x => x.ID, _ => 0m);
        var receivedCost = ordered.ToDictionary(x => x.ID, _ => 0m);
        var usedMovements = new HashSet<int>();
        try
        {
            foreach (var receipt in receipts)
            {
                if (receipt.Status != "Posted" || receipt.VendorId != order.VendorId ||
                    receipt.InvoiceId != clearingInvoiceId ||
                    receipt.CurrencyId != order.CurrencyId || receipt.ExchangeRate != order.ExchangeRate ||
                    order.WarehouseId.HasValue && receipt.WarehouseId != order.WarehouseId &&
                        receipt.WarehouseId != authorizedReceiptWarehouseId || receipt.Lines.Count == 0 ||
                    receipt.TotalCost != receipt.Lines.Sum(x => x.LineTotal)) return false;
                foreach (var line in receipt.Lines)
                {
                    var source = ordered.SingleOrDefault(x => x.ID == line.PurchaseOrderLineId);
                    var movement = movements.SingleOrDefault(x => x.ID == line.StockMovementId);
                    if (source == null || line.ItemId != source.ItemId || line.UoMId != source.UoMId ||
                        line.Qty <= 0 || line.LineTotal < 0 || line.UnitCost < 0 || movement == null ||
                        !usedMovements.Add(movement.ID) || movement.SourceId != receipt.ID || movement.ItemId != line.ItemId ||
                        movement.WarehouseId != receipt.WarehouseId || movement.Direction != 1 || movement.QtyBase <= 0 ||
                        movement.QtyInUoM != line.Qty || line.UoMId.HasValue && movement.UoMId != line.UoMId ||
                        movement.TotalCost != line.LineTotal || movement.UnitCost < 0) return false;
                    // This foundation supports only unchanged commercial cost. Even a balanced stock/GL
                    // posting cannot authorize a price variance. Fractional unit rounding needs separate coverage.
                    if (source.Qty <= 0 || line.UnitCost != source.UnitPrice - source.DiscountAmount / source.Qty) return false;
                    if (movement.TotalCost != 0 || movement.JournalEntryId.HasValue)
                    {
                        var entry = entries.SingleOrDefault(x => x.ID == movement.JournalEntryId);
                        var lines = journalLines.Where(x => x.JournalEntryId == movement.JournalEntryId).ToArray();
                        if (entry == null || entry.Status != "Posted" || entry.SourceType != "Inventory" ||
                            entry.SourceId != movement.ID || entry.FunctionalCurrencyId != order.CurrencyId || lines.Length == 0 ||
                            lines.Any(x => x.Debit < 0 || x.Credit < 0) || lines.Sum(x => x.Debit) != movement.TotalCost ||
                            lines.Sum(x => x.Credit) != movement.TotalCost) return false;
                    }
                    received[source.ID] = checked(received[source.ID] + line.Qty);
                    receivedCost[source.ID] = checked(receivedCost[source.ID] + line.LineTotal);
                }
            }
        }
        catch (OverflowException) { return false; }
        if (usedMovements.Count != movements.Count || ordered.Any(x => x.ReceivedQty < 0 || x.ReceivedQty > x.Qty || received[x.ID] != x.ReceivedQty)) return false;
        if (ordered.Any(x => receivedCost[x.ID] > x.LineTotal || received[x.ID] == x.Qty && receivedCost[x.ID] != x.LineTotal)) return false;
        if (clearingInvoiceId.HasValue) return order.Status == "Closed" && ordered.All(x => x.ReceivedQty == x.Qty);
        return (order.Status == "Received") == ordered.All(x => x.ReceivedQty == x.Qty);
    }

    // Same entity shape and property order as the original v1 hash. Never change persisted history or current rows.
    // v1 serializes decimal scale as well as value; the zero must retain the database representation.
    internal static decimal OriginalReceivedZero(decimal current) => new(0, 0, 0, false,
        (byte)((decimal.GetBits(current)[3] >> 16) & 0xff));
    internal static PurchaseOrderLine[] OriginalLines(IEnumerable<PurchaseOrderLine> rows) => rows.Select(x => new PurchaseOrderLine {
        ID=x.ID, PurchaseOrderId=x.PurchaseOrderId, LineNo=x.LineNo, ItemId=x.ItemId, ItemDescription=x.ItemDescription,
        Qty=x.Qty, UoMId=x.UoMId, UnitPrice=x.UnitPrice, DiscountAmount=x.DiscountAmount, TaxRate=x.TaxRate,
        LineTotal=x.LineTotal, ReceivedQty=OriginalReceivedZero(x.ReceivedQty)
    }).ToArray();
}
