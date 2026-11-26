using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

// Read-only GRNI clearing coverage, not expense realization or permission to release a reservation.
internal static class ConstructionStockInvoiceWitness
{
    internal static async Task<List<ConstructionOrderAllocationLine>?> ReadAsync(CrossDbContext db,
        IProjectsAccessService access, IConstructionAuditService audit, BusinessContext c, int project,
        ConstructionBudgetVersion baseline, PurchaseOrder order, PurchaseInvoice invoice, JournalEntry entry,
        IReadOnlyList<PurchaseOrderLine> ordered, IReadOnlyList<PurchaseInvoiceLine> invoiced,
        IReadOnlyList<PurchaseOrderInvoiceLineLink> links, CancellationToken ct)
    {
        if (order.ExchangeRate != 1m || invoice.ExchangeRate != 1m || order.CurrencyId != baseline.CurrencyId ||
            invoice.SubTotal != order.SubTotal || invoice.TaxTotal != order.TaxTotal || invoice.GrandTotal != order.GrandTotal ||
            invoice.SubTotal + invoice.TaxTotal != invoice.GrandTotal || invoice.SubTotalBase != invoice.SubTotal ||
            invoice.TaxTotalBase != invoice.TaxTotal || invoice.GrandTotalBase != invoice.GrandTotal ||
            entry.FunctionalCurrencyDecimals is not (>= 0 and <= 8)) return null;
        var ledger = await db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntryId == entry.ID).ToListAsync(ct);
        if (ledger.Count == 0 || ledger.Any(x => x.ProjectId != project || x.Debit < 0 || x.Credit < 0) ||
            ledger.Sum(x => x.Debit) != invoice.GrandTotal || ledger.Sum(x => x.Credit) != invoice.GrandTotal) return null;
        var accountIds = ledger.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await (from a in db.Accounts.AsNoTracking()
            join t in db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
            where a.CompanyID == c.CompanyId && accountIds.Contains(a.ID)
            select new { a.ID, t.Code, t.StatementType }).ToListAsync(ct);
        if (accounts.Count != accountIds.Length || accounts.Any(x => x.StatementType != "BalanceSheet" || x.Code is not ("ASSET" or "LIAB"))) return null;
        var graph = links.Select(x => new ConstructionRealizationLineage(x.CompanyID, project, x.PurchaseOrderId,
            x.PurchaseOrderLineId, x.PurchaseInvoiceId, x.PurchaseInvoiceLineId, x.JournalEntryId, x.JournalEntryLineId)).ToArray();
        var netIds = links.Select(x => x.JournalEntryLineId).ToHashSet();
        var net = ledger.Where(x => netIds.Contains(x.ID)).ToArray();
        if (!ConstructionRealizationLineageRules.Validate(c.CompanyId, project, order.ID, invoice.ID, entry.ID,
            ordered.Select(x => x.ID).ToArray(), invoiced.Select(x => x.ID).ToArray(), net.Select(x => x.ID).ToArray(), graph).Ok ||
            ordered.Sum(x => x.LineTotal) != invoice.SubTotal) return null;
        foreach (var link in links)
        {
            var po = ordered.Single(x => x.ID == link.PurchaseOrderLineId);
            var line = invoiced.Single(x => x.ID == link.PurchaseInvoiceLineId);
            var gl = net.Single(x => x.ID == link.JournalEntryLineId);
            var itemAccounts = await (from item in db.Items.AsNoTracking()
                join category in db.ItemCategories.AsNoTracking() on item.ItemCategoryId equals category.ID
                where item.CompanyID == c.CompanyId && category.CompanyID == c.CompanyId && item.ID == po.ItemId && item.ItemType == "Stockable"
                select new { category.GrniAccountId, category.InventoryAccountId }).SingleOrDefaultAsync(ct);
            if (itemAccounts == null) return null;
            var grni = itemAccounts.GrniAccountId;
            var ownedInventory = await (from account in db.Accounts.AsNoTracking()
                join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID
                where account.CompanyID == c.CompanyId && account.ID == itemAccounts.InventoryAccountId &&
                    type.Code == "ASSET" && type.StatementType == "BalanceSheet"
                select account.ID).AnyAsync(ct);
            if (!ownedInventory) return null;
            var receiptPostings = await (from receipt in db.GoodsReceipts.AsNoTracking()
                join received in db.GoodsReceiptLines.AsNoTracking() on receipt.ID equals received.GoodsReceiptId
                join movement in db.StockMovements.AsNoTracking() on received.StockMovementId equals (int?)movement.ID
                join posted in db.JournalEntryLines.AsNoTracking() on movement.JournalEntryId equals (int?)posted.JournalEntryId
                where receipt.CompanyID == c.CompanyId && receipt.PurchaseOrderId == order.ID &&
                    received.PurchaseOrderLineId == po.ID && movement.CompanyID == c.CompanyId
                select new { posted.AccountId, posted.Debit, posted.Credit }).ToListAsync(ct);
            if (itemAccounts?.InventoryAccountId is not > 0 || receiptPostings.Count == 0 ||
                receiptPostings.Where(x => x.AccountId == grni).Sum(x => x.Credit) != po.LineTotal ||
                receiptPostings.Where(x => x.AccountId == itemAccounts.InventoryAccountId).Sum(x => x.Debit) != po.LineTotal ||
                receiptPostings.Any(x => x.Credit > 0 && x.AccountId != grni || x.Debit > 0 && x.AccountId != itemAccounts.InventoryAccountId)) return null;
            if (grni is not > 0 || line.ItemId.HasValue || line.WarehouseId.HasValue || line.ExpenseAccountId != grni ||
                accounts.SingleOrDefault(x => x.ID == grni)?.Code != "LIAB" || gl.AccountId != grni || gl.CostCenterId != line.CostCenterId ||
                gl.LineNo != line.LineNo || gl.Debit != po.LineTotal || gl.Credit != 0 ||
                po.Qty != line.Qty || po.UnitPrice != line.UnitPrice || po.DiscountAmount != line.DiscountAmount ||
                po.TaxRate != line.TaxRate || po.LineTotal != line.LineTotal) return null;
        }
        var remaining = ledger.Where(x => !netIds.Contains(x.ID)).ToArray();
        var vendorAccount = await db.Vendors.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ID == order.VendorId)
            .Select(x => (int?)x.ControlAccountId).SingleOrDefaultAsync(ct);
        var vatAccount = await db.Accounts.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.Code == "110401")
            .Select(x => (int?)x.ID).SingleOrDefaultAsync(ct);
        if (vendorAccount is not > 0 || accounts.SingleOrDefault(x => x.ID == vendorAccount)?.Code != "LIAB" ||
            remaining.Where(x => x.AccountId == vendorAccount).Sum(x => x.Credit) != invoice.GrandTotal ||
            remaining.Any(x => x.Credit > 0 && x.AccountId != vendorAccount || x.Credit > 0 && x.Debit != 0) ||
            remaining.Where(x => x.Debit > 0).Sum(x => x.Debit) != invoice.TaxTotal ||
            remaining.Any(x => x.Debit > 0 && (x.AccountId != vatAccount || accounts.Single(a => a.ID == x.AccountId).Code != "ASSET"))) return null;
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID, ct);
        if (reservation == null || reservation.BudgetVersionId != baseline.ID || reservation.CurrencyId != baseline.CurrencyId ||
            reservation.NetDocumentAmount != order.SubTotal || await db.Set<ConstructionCommitmentRelease>().AsNoTracking()
                .AnyAsync(x => x.CompanyID == c.CompanyId && x.ReservationId == reservation.ID, ct)) return null;
        var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == reservation.AllocationId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID, ct);
        if (allocation == null || allocation.SourceVersion != reservation.SourceVersion) return null;
        var proof = await new ConstructionOrderAllocationService(db, access, audit)
            .ValidateCurrentAsync(c, project, allocation, ct, invoice.ID);
        return proof.Error == null && proof.Lines.Sum(x => x.NetDocumentAmount) == reservation.NetDocumentAmount ? proof.Lines : null;
    }
}
