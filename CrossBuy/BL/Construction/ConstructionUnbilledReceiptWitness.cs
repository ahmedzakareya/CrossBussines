using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

// A receipt is an inventory asset / GRNI liability, not an expense. Prove the original
// admitted purchase and each actual receipt posting before attributing a later issue.
internal static class ConstructionUnbilledReceiptWitness
{
    internal static async Task<List<ConstructionOrderAllocationLine>?> ReadAsync(CrossDbContext db,
        IProjectsAccessService access, IConstructionAuditService audit, BusinessContext c, int project,
        ConstructionBudgetVersion baseline, PurchaseOrder order, CancellationToken ct, int? expectedDecimals = null,
        int? authorizedReceiptWarehouseId = null)
    {
        if (order.Status is not ("Approved" or "Received") || order.CurrencyId != baseline.CurrencyId || order.ExchangeRate != 1m ||
            await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct)) return null;
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID, ct);
        if (reservation == null || reservation.CurrencyId != baseline.CurrencyId ||
            reservation.NetDocumentAmount != order.SubTotal || await db.Set<ConstructionCommitmentRelease>().AsNoTracking()
                .AnyAsync(x => x.CompanyID == c.CompanyId && x.ReservationId == reservation.ID, ct)) return null;
        var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == reservation.AllocationId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID, ct);
        if (allocation == null || allocation.SourceVersion != reservation.SourceVersion) return null;
        List<ConstructionOrderAllocationLine> mappedLines;
        if (reservation.BudgetVersionId != baseline.ID)
        {
            var rebased = await ConstructionExposureRebaseReader.ReadAsync(db, access, audit, c, project, baseline,
                reservation, null, ct, authorizedReceiptWarehouseId);
            if (rebased.Error != null) return null;
            mappedLines = rebased.Lines;
        }
        else
        {
            var mapped = await new ConstructionOrderAllocationService(db, access, audit).ValidateCurrentAsync(c, project, allocation, ct);
            if (mapped.Error != null) return null;
            mappedLines = mapped.Lines;
        }
        if (mappedLines.Sum(x => x.NetDocumentAmount) != reservation.NetDocumentAmount) return null;
        var receipts = await (from receipt in db.GoodsReceipts.AsNoTracking()
            join line in db.GoodsReceiptLines.AsNoTracking() on receipt.ID equals line.GoodsReceiptId
            join movement in db.StockMovements.AsNoTracking() on line.StockMovementId equals (int?)movement.ID
            join item in db.Items.AsNoTracking() on movement.ItemId equals item.ID
            join category in db.ItemCategories.AsNoTracking() on item.ItemCategoryId equals category.ID
            where receipt.CompanyID == c.CompanyId && receipt.PurchaseOrderId == order.ID && movement.CompanyID == c.CompanyId &&
                item.CompanyID == c.CompanyId && category.CompanyID == c.CompanyId && item.ItemType == "Stockable"
            select new { movement.JournalEntryId, movement.TotalCost, movement.ID, category.InventoryAccountId, category.GrniAccountId }).ToListAsync(ct);
        var expectedCount = await (from receipt in db.GoodsReceipts.AsNoTracking()
            join line in db.GoodsReceiptLines.AsNoTracking() on receipt.ID equals line.GoodsReceiptId
            where receipt.CompanyID == c.CompanyId && receipt.PurchaseOrderId == order.ID select line.ID).CountAsync(ct);
        if (receipts.Count == 0 || receipts.Count != expectedCount) return null;
        foreach (var receipt in receipts)
        {
            var entry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ID == receipt.JournalEntryId, ct);
            if (entry == null || entry.Status != "Posted" || entry.SourceType != "Inventory" || entry.SourceId != receipt.ID ||
                entry.FunctionalCurrencyId != baseline.CurrencyId || entry.FunctionalCurrencyDecimals is not (>= 0 and <= 8) ||
                expectedDecimals.HasValue && entry.FunctionalCurrencyDecimals != expectedDecimals.Value || receipt.TotalCost <= 0) return null;
            var gl = await db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntryId == entry.ID).ToListAsync(ct);
            // Project-dimensioned receipt journals need separate non-expense coverage in exposure.
            if (gl.Count != 2 || gl.Any(x => x.ProjectId != null) ||
                gl.Count(x => x.AccountId == receipt.InventoryAccountId && x.Debit == receipt.TotalCost && x.Credit == 0) != 1 ||
                gl.Count(x => x.AccountId == receipt.GrniAccountId && x.Credit == receipt.TotalCost && x.Debit == 0) != 1) return null;
            var accounts = await (from account in db.Accounts.AsNoTracking()
                join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID
                where account.CompanyID == c.CompanyId && (account.ID == receipt.InventoryAccountId || account.ID == receipt.GrniAccountId)
                select new { account.ID, type.Code, type.StatementType }).ToListAsync(ct);
            if (!accounts.Any(x => x.ID == receipt.InventoryAccountId && x.Code == "ASSET" && x.StatementType == "BalanceSheet") ||
                !accounts.Any(x => x.ID == receipt.GrniAccountId && x.Code == "LIAB" && x.StatementType == "BalanceSheet")) return null;
        }
        return mappedLines;
    }
}
