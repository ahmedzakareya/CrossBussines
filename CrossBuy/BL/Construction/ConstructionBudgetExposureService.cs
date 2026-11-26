using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionBudgetExposureRow(int WbsNodeId, int CostCodeId, decimal Budget,
    decimal Actual, decimal Commitment, decimal Remaining);
public sealed record ConstructionBudgetExposure(bool Ok, string Code, int? CurrencyId = null,
    IReadOnlyList<ConstructionBudgetExposureRow>? Rows = null, IReadOnlyList<int>? ReplacedOrderIds = null,
    IReadOnlyList<ConstructionSourceIssue>? Issues = null, int? BudgetVersionId = null, string? CurrencyCode = null, string? Version = null);
public interface IConstructionBudgetExposureService
{
    Task<ConstructionBudgetExposure> PreviewAsync(BusinessContext context, int projectId, CancellationToken ct = default);
}

// Read model and the admission kernel share one locked source calculation. A preview never grants
// spending authority. Incomplete coverage returns no amounts, rather than a partial/implicit zero.
public sealed class ConstructionBudgetExposureService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit) : IConstructionBudgetExposureService
{
    private static ConstructionBudgetExposure Refuse(string code, string? issue = null, string entity = "Project", int id = 0) =>
        new(false, code, Issues: issue == null ? null : new[] { new ConstructionSourceIssue(issue, entity, id) });

    public async Task<ConstructionBudgetExposure> PreviewAsync(BusinessContext c, int projectId, CancellationToken ct = default)
    {
        if (!c.IsAuthenticated || c.EmployeeId is not > 0 || c.CompanyId <= 0 || db.CompanyScope.FilterCompanyId != c.CompanyId ||
            !await access.CanAsync(c, ProjectsActions.BudgetView, new PermissionTarget { ProjectId = projectId }, ct) ||
            !await db.Projects.AsNoTracking().AnyAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct))
            return Refuse("AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return Refuse("CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline && x.Status == "Approved", ct);
            if (baseline == null) return Refuse("ApprovedBaselineRequired");
            var result = await ReadLocked(c, projectId, baseline, null, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (OverflowException) { return Refuse("AmountOverflow"); }
    }

    internal async Task<ConstructionBudgetExposure> ReadLocked(BusinessContext c, int projectId,
        ConstructionBudgetVersion baseline, int? excludedProposalOrderId, CancellationToken ct,
        ConstructionExposureRebaseCandidate? rebaseCandidate = null)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A budget exposure transaction is required");
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) return Refuse("PolicyNotConfigured");
        var currencyCode = await db.Currencies.AsNoTracking().Where(x => x.ID == baseline.CurrencyId).Select(x => x.Code).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(currencyCode)) return Refuse("BudgetCurrencyNotProven");
        if (await db.Subcontracts.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct) ||
            await db.SubcontractBillings.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct))
            return Refuse("SubcontractCoverageRequired");
        var actual = await new ConstructionActualCostService(db, access, audit).ReadCoverageLocked(c, projectId, null, null, ct);
        if (actual.Coverage.NetActualsByCurrency == null)
            return new(false, "RealizationCoverageRequired", Issues: actual.Coverage.Issues);
        if (actual.Dimensions.Any(x => x.CurrencyId != baseline.CurrencyId)) return Refuse("BudgetCurrencyMismatch");
        var budgets = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.BudgetVersionId == baseline.ID).ToListAsync(ct);
        if (budgets.Count == 0 || budgets.Any(x => x.Amount < 0) ||
            budgets.Select(x => (x.WbsNodeId, x.CostCodeId)).Distinct().Count() != budgets.Count ||
            actual.Dimensions.Any(x => !budgets.Any(b => b.WbsNodeId == x.WbsNodeId && b.CostCodeId == x.CostCodeId && b.CostCategory == x.CostCategory)))
            return Refuse("BudgetDimensionCoverageRequired");
        var orders = await db.PurchaseOrders.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).OrderBy(x => x.ID).ToListAsync(ct);
        var orderIds = orders.Select(x => x.ID).ToArray();
        var links = await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && orderIds.Contains(x.PurchaseOrderId)).ToListAsync(ct);
        var partialHeaders = await db.Set<ConstructionPartialInvoice>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            (x.ProjectId == projectId || orderIds.Contains(x.PurchaseOrderId))).ToListAsync(ct);
        var partialSlices = await db.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            (x.ProjectId == projectId || orderIds.Contains(x.PurchaseOrderId))).ToListAsync(ct);
        if (partialHeaders.Any(x => x.ProjectId != projectId || !orderIds.Contains(x.PurchaseOrderId)) ||
            partialSlices.Any(x => x.ProjectId != projectId || !partialHeaders.Any(h => h.ID == x.HeaderId && h.PurchaseOrderId == x.PurchaseOrderId)))
            return Refuse("RealizationCoverageRequired", "PartialInvoiceHistoryUnproven");
        var invoiceIds = links.Select(x => x.PurchaseInvoiceId).Concat(partialHeaders.Select(x => x.PurchaseInvoiceId)).Distinct().ToArray();
        var invoices = await db.PurchaseInvoices.AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            (x.ProjectId == projectId || invoiceIds.Contains(x.ID))).ToListAsync(ct);
        if (partialHeaders.Any(x => !invoices.Any(i => i.ID == x.PurchaseInvoiceId)))
            return Refuse("RealizationCoverageRequired", "PartialInvoiceHistoryUnproven");
        var entries = await (from e in db.JournalEntries.AsNoTracking()
            where e.CompanyID == c.CompanyId && (e.Status == "Posted" || e.Status == "Reversed") &&
                db.JournalEntryLines.Any(l => l.JournalEntryId == e.ID && l.ProjectId == projectId)
            select e).ToListAsync(ct);
        var entryIds = entries.Select(x => x.ID).ToArray();
        var ledger = await db.JournalEntryLines.AsNoTracking().Where(x => entryIds.Contains(x.JournalEntryId) && x.ProjectId == projectId).ToListAsync(ct);
        // An expense attribution alone does not identify a fungible purchase. Only the complete,
        // freshly validated immutable receipt/issue graph may transfer commitment into actuals.
        var consumption = await new ConstructionMaterialConsumptionService(db, access, audit).ReadCoverageLocked(c, projectId, ct);
        if (!consumption.Ok)
            return Refuse("RealizationCoverageRequired", consumption.Code);
        if (entries.Any(x => x.SourceType == "Inventory" && (x.Status != "Posted" ||
            !consumption.Records.Any(r => r.IssueJournalEntryId == x.ID))))
            return Refuse("RealizationCoverageRequired", "InventoryConsumptionCoverageRequired");
        var receipts = await db.GoodsReceipts.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId.HasValue && orderIds.Contains(x.PurchaseOrderId.Value)).ToListAsync(ct);
        var receiptOrderIds = receipts.Select(x => x.PurchaseOrderId!.Value).ToHashSet();
        foreach (var entry in entries)
        {
            if (entry.Status == "Reversed")
            {
                var mirror = entries.SingleOrDefault(x => x.ID == entry.ReversedByEntryId && x.SourceType == "Reversal" && x.SourceId == entry.ID);
                if (mirror == null || !Mirrors(entry, mirror, ledger)) return Refuse("RealizationCoverageRequired", "ReversalSourceNotProven", "JournalEntry", entry.ID);
            }
            if (entry.SourceType == "Reversal" && !entries.Any(x => x.ID == entry.SourceId && x.Status == "Reversed" && x.ReversedByEntryId == entry.ID))
                return Refuse("RealizationCoverageRequired", "ReversalSourceNotProven", "JournalEntry", entry.ID);
            var deferredClearing = entry.SourceType == "PurchaseInvoice" && invoices.Any(i => i.ID == entry.SourceId &&
                (links.Any(l => l.PurchaseInvoiceId == i.ID && receiptOrderIds.Contains(l.PurchaseOrderId)) ||
                 partialHeaders.Any(h => h.PurchaseInvoiceId == i.ID && receiptOrderIds.Contains(h.PurchaseOrderId))));
            if (!deferredClearing && !actual.Dimensions.Any(x => x.EntryId == entry.ID))
                return Refuse("RealizationCoverageRequired", "NonExpenseSourceRequiresCoverage", "JournalEntry", entry.ID);
            if (entry.SourceType == "PurchaseInvoice" && !invoices.Any(x => x.ID == entry.SourceId))
                return Refuse("RealizationCoverageRequired", "InvoiceSourceNotProven", "JournalEntry", entry.ID);
        }
        var reservations = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToListAsync(ct);
        var released = await db.Set<ConstructionCommitmentRelease>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).Select(x => x.ReservationId).ToListAsync(ct);
        var lineLinks = await db.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && orderIds.Contains(x.PurchaseOrderId)).ToListAsync(ct);
        var sourceLines = await db.PurchaseOrderLines.AsNoTracking().Where(x => orderIds.Contains(x.PurchaseOrderId)).ToListAsync(ct);
        var invoiceLineIds = invoices.Select(x => x.ID).ToArray();
        var invoiceLines = await db.PurchaseInvoiceLines.AsNoTracking().Where(x => invoiceLineIds.Contains(x.PurchaseInvoiceId)).ToListAsync(ct);
        var replaced = new List<int>();
        var clearingOrders = new HashSet<int>();
        var clearingCommitments = new List<ConstructionOrderAllocationLine>();
        foreach (var invoice in invoices)
        {
            var partial = partialHeaders.SingleOrDefault(x => x.PurchaseInvoiceId == invoice.ID);
            if (partial != null)
            {
                var partialOrder = orders.SingleOrDefault(x => x.ID == partial.PurchaseOrderId);
                var partialEntry = entries.SingleOrDefault(x => x.ID == partial.JournalEntryId);
                if (partialOrder == null || partialEntry?.FunctionalCurrencyDecimals is not (>= 0 and <= 8) ||
                    partialOrder.CurrencyId != baseline.CurrencyId || !receiptOrderIds.Contains(partialOrder.ID))
                    return Refuse("RealizationCoverageRequired", "PartialInvoiceHistoryUnproven");
                // Validate ALL invoices on this order once. Clearing is not expense realization;
                // retain the original commitment once, regardless of the number of invoices.
                if (!clearingOrders.Contains(partialOrder.ID))
                {
                    var history = await ConstructionPartialInvoiceHistoryReader.ReadAsync(db,
                        new(c.CompanyId, projectId, partialOrder.ID), partialEntry.FunctionalCurrencyDecimals.Value, ct);
                    if (!history.Ok) return Refuse("RealizationCoverageRequired", history.Code, "PurchaseOrder", partialOrder.ID);
                    var mapped = await ConstructionUnbilledReceiptWitness.ReadAsync(db, access, audit, c, projectId, baseline, partialOrder, ct,
                        partialEntry.FunctionalCurrencyDecimals.Value);
                    if (mapped == null) return Refuse("RealizationCoverageRequired", "InventoryInvoiceClearingNotProven", "PurchaseOrder", partialOrder.ID);
                    clearingOrders.Add(partialOrder.ID); clearingCommitments.AddRange(mapped);
                }
                continue;
            }
            var link = links.SingleOrDefault(x => x.PurchaseInvoiceId == invoice.ID);
            var order = link == null ? null : orders.SingleOrDefault(x => x.ID == link.PurchaseOrderId);
            var entry = entries.SingleOrDefault(x => x.ID == invoice.JournalEntryId);
            if (order == null || order.Status != "Closed" || invoice.ProjectId != projectId || invoice.VendorId != order.VendorId ||
                invoice.Status != "Posted" || entry == null || entry.Status != "Posted" || entry.SourceType != "PurchaseInvoice" || entry.SourceId != invoice.ID)
                return Refuse("RealizationCoverageRequired", "InvoiceSourceNotProven", "PurchaseInvoice", invoice.ID);
            if (invoice.CurrencyId != baseline.CurrencyId || order.CurrencyId != baseline.CurrencyId ||
                entry.FunctionalCurrencyId != baseline.CurrencyId || invoice.ExchangeRate != 1m)
                return Refuse("BudgetCurrencyMismatch");
            var poLines = sourceLines.Where(x => x.PurchaseOrderId == order.ID).ToArray();
            var invLines = invoiceLines.Where(x => x.PurchaseInvoiceId == invoice.ID).ToArray();
            var ownLinks = lineLinks.Where(x => x.PurchaseOrderId == order.ID).ToArray();
            if (receiptOrderIds.Contains(order.ID))
            {
                var clearing = await ConstructionStockInvoiceWitness.ReadAsync(db, access, audit, c, projectId, baseline,
                    order, invoice, entry, poLines, invLines, ownLinks, ct);
                if (clearing == null) return Refuse("RealizationCoverageRequired", "InventoryInvoiceClearingNotProven", "PurchaseOrder", order.ID);
                clearingOrders.Add(order.ID); clearingCommitments.AddRange(clearing);
                continue;
            }
            if (poLines.Any(x => x.ReceivedQty != 0))
                return Refuse("RealizationCoverageRequired", "InventoryConsumptionCoverageRequired", "PurchaseOrder", order.ID);
            var lineNos = invLines.Select(x => x.LineNo).ToHashSet();
            var netLines = ledger.Where(x => x.JournalEntryId == entry.ID && lineNos.Contains(x.LineNo)).ToArray();
            var graph = ownLinks.Select(x => new ConstructionRealizationLineage(c.CompanyId, projectId, x.PurchaseOrderId,
                x.PurchaseOrderLineId, x.PurchaseInvoiceId, x.PurchaseInvoiceLineId, x.JournalEntryId, x.JournalEntryLineId)).ToArray();
            if (!ConstructionRealizationLineageRules.Validate(c.CompanyId, projectId, order.ID, invoice.ID, entry.ID,
                poLines.Select(x => x.ID).ToArray(), invLines.Select(x => x.ID).ToArray(), netLines.Select(x => x.ID).ToArray(), graph).Ok)
                return Refuse("RealizationCoverageRequired", "InvoiceLineageCoverageRequired", "PurchaseInvoice", invoice.ID);
            if (poLines.Any(x => x.Qty <= 0 || x.LineTotal < 0) || invLines.Select(x => x.LineNo).Distinct().Count() != invLines.Length ||
                poLines.Sum(x => x.LineTotal) != order.SubTotal || invLines.Sum(x => x.LineTotal) != invoice.SubTotal ||
                order.SubTotal != invoice.SubTotal || order.SubTotal + order.TaxTotal != order.GrandTotal ||
                invoice.SubTotal + invoice.TaxTotal != invoice.GrandTotal || invoice.SubTotalBase != invoice.SubTotal ||
                invoice.TaxTotalBase != invoice.TaxTotal || invoice.GrandTotalBase != invoice.GrandTotal ||
                ledger.Where(x => x.JournalEntryId == entry.ID).Sum(x => x.Debit) != invoice.GrandTotalBase ||
                ledger.Where(x => x.JournalEntryId == entry.ID).Sum(x => x.Credit) != invoice.GrandTotalBase)
                return Refuse("RealizationCoverageRequired", "InvoiceAmountsChanged", "PurchaseInvoice", invoice.ID);
            foreach (var identity in ownLinks)
            {
                var poLine = poLines.Single(x => x.ID == identity.PurchaseOrderLineId);
                var invLine = invLines.Single(x => x.ID == identity.PurchaseInvoiceLineId);
                var gl = netLines.Single(x => x.ID == identity.JournalEntryLineId);
                var classified = actual.Dimensions.Where(x => x.JournalLineId == gl.ID).ToArray();
                if (gl.AccountId != invLine.ExpenseAccountId || gl.CostCenterId != invLine.CostCenterId || gl.LineNo != invLine.LineNo ||
                    poLine.Qty != invLine.Qty || poLine.UnitPrice != invLine.UnitPrice || poLine.DiscountAmount != invLine.DiscountAmount ||
                    poLine.TaxRate != invLine.TaxRate || poLine.LineTotal != invLine.LineTotal ||
                    classified.Length == 0 || classified.Any(x => x.Amount <= 0) || classified.Sum(x => x.Amount) != invLine.LineTotal)
                    return Refuse("RealizationCoverageRequired", "InvoiceNetExpenseNotProven", "PurchaseInvoiceLine", invLine.ID);
            }
            var reservation = reservations.SingleOrDefault(x => x.PurchaseOrderId == order.ID);
            if (reservation != null)
            {
                if (released.Contains(reservation.ID) || reservation.CurrencyId != baseline.CurrencyId || reservation.BudgetVersionId != baseline.ID)
                    return Refuse("ReservationRebaseRequired");
                var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID).OrderByDescending(x => x.ID).FirstOrDefaultAsync(ct);
                if (allocation == null || !allocation.IsSealed || allocation.ID != reservation.AllocationId || allocation.ProjectId != projectId ||
                    allocation.BudgetVersionId != baseline.ID || allocation.SourceVersion != reservation.SourceVersion || reservation.NetDocumentAmount != invoice.SubTotal)
                    return Refuse("ReservationCoverageRequired");
                var mapped = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.AllocationId == allocation.ID).ToListAsync(ct);
                foreach (var identity in ownLinks)
                {
                    var committed = mapped.Where(x => x.SourceLineId == identity.PurchaseOrderLineId).GroupBy(x => (x.WbsNodeId, x.CostCodeId, x.CostCategory)).ToDictionary(g => g.Key, g => g.Sum(x => x.NetDocumentAmount));
                    var realized = actual.Dimensions.Where(x => x.JournalLineId == identity.JournalEntryLineId).ToDictionary(x => (x.WbsNodeId, x.CostCodeId, x.CostCategory), x => x.Amount);
                    if (committed.Count != realized.Count || committed.Any(x => !realized.TryGetValue(x.Key, out var amount) || amount != x.Value))
                        return Refuse("ReservationAmendmentRequired");
                }
                if (mapped.Any(x => !poLines.Any(p => p.ID == x.SourceLineId)) || mapped.Sum(x => x.NetDocumentAmount) != reservation.NetDocumentAmount)
                    return Refuse("ReservationCoverageRequired");
            }
            replaced.Add(order.ID);
        }
        if (links.Any(x => !replaced.Contains(x.PurchaseOrderId) && !clearingOrders.Contains(x.PurchaseOrderId))) return Refuse("RealizationCoverageRequired", "ConversionSourceNotProven");
        var commitments = new Dictionary<(int Node, int Code), decimal>();
        var consumed = new Dictionary<(int SourceLine, int Node, int Code), decimal>();
        foreach (var record in consumption.Records)
        {
            var splits = actual.Dimensions.Where(x => x.JournalLineId == record.ExpenseJournalLineId).ToArray();
            if (record.PurchaseOrderId == excludedProposalOrderId ||
                !orders.Any(x => x.ID == record.PurchaseOrderId && x.Status is "Approved" or "Received" or "Closed") ||
                record.FunctionalCurrencyId != baseline.CurrencyId ||
                splits.Length == 0 || splits.Any(x => x.Amount <= 0 || x.CostCategory != "Material") ||
                splits.Sum(x => x.Amount) != record.NetBaseCost)
                return Refuse("RealizationCoverageRequired", "ConsumptionCommitmentCoverageRequired");
            foreach (var split in splits)
            {
                var key = (record.PurchaseOrderLineId, split.WbsNodeId, split.CostCodeId);
                consumed[key] = consumed.GetValueOrDefault(key) + split.Amount;
            }
        }
        var commitmentLines = new List<ConstructionOrderAllocationLine>(clearingCommitments);
        var allocationEvidence = new List<object>();
        var rebaseEvidence = new List<object>();
        var allocations = new ConstructionOrderAllocationService(db, access, audit);
        foreach (var order in orders.Where(x => x.ID != excludedProposalOrderId && !replaced.Contains(x.ID) && !clearingOrders.Contains(x.ID)))
        {
            var reservation = reservations.SingleOrDefault(x => x.PurchaseOrderId == order.ID);
            var active = reservation != null && !released.Contains(reservation.ID);
            if (order.Status == "Draft" && !active) continue;
            List<ConstructionOrderAllocationLine> mapped;
            if (order.Status == "Cancelled" && !active)
            {
                if (sourceLines.Any(x => x.PurchaseOrderId == order.ID && x.ReceivedQty != 0) ||
                    await db.GoodsReceipts.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct))
                    return Refuse("CancellationCoverageRequired");
                continue;
            }
            if (active && reservation!.BudgetVersionId != baseline.ID)
            {
                // Only an unchanged draft or its independently proven, unrealized admitted transition
                // may use this map. Original reservation remains immutable; fulfillment has separate gates.
                var rebased = await ConstructionExposureRebaseReader.ReadAsync(db, access, audit, c, projectId,
                    baseline, reservation, rebaseCandidate, ct);
                if (rebased.Error != null) return Refuse(rebased.Error);
                mapped = rebased.Lines;
                rebaseEvidence.Add(rebased.Evidence!);
            }
            else if (order.Status == "Cancelled" && active)
            {
                if (reservation!.BudgetVersionId != baseline.ID || reservation.CurrencyId != baseline.CurrencyId) return Refuse("ReservationRebaseRequired");
                mapped = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x => x.AllocationId == reservation.AllocationId && x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToListAsync(ct);
                if (mapped.Count == 0 || mapped.Sum(x => x.NetDocumentAmount) != reservation.NetDocumentAmount) return Refuse("ReservationCoverageRequired");
            }
            else
            {
                if (order.Status is not ("Draft" or "Approved" or "Received")) return Refuse("OrderCoverageRequired");
                var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID).OrderByDescending(x => x.ID).FirstOrDefaultAsync(ct);
                if (allocation == null || active && reservation!.AllocationId != allocation.ID) return Refuse("AllocationCoverageRequired");
                var current = await allocations.ValidateCurrentAsync(c, projectId, allocation, ct);
                if (current.Error != null) return Refuse(current.Error);
                mapped = current.Lines;
            }
            commitmentLines.AddRange(mapped);
            allocationEvidence.Add(mapped.OrderBy(x => x.ID).ToArray());
        }
        var committedDimensions = commitmentLines.GroupBy(x => (SourceLine: x.SourceLineId, Node: x.WbsNodeId, Code: x.CostCodeId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.NetDocumentAmount));
        if (consumed.Any(x => !committedDimensions.TryGetValue(x.Key, out var amount) || amount < x.Value))
            return Refuse("RealizationCoverageRequired", "ConsumptionCommitmentCoverageRequired");
        foreach (var dimension in committedDimensions)
        {
            var key = (dimension.Key.Node, dimension.Key.Code);
            commitments[key] = commitments.GetValueOrDefault(key) + dimension.Value - consumed.GetValueOrDefault(dimension.Key);
        }
        if (commitments.Keys.Any(k => !budgets.Any(b => b.WbsNodeId == k.Node && b.CostCodeId == k.Code))) return Refuse("BudgetDimensionCoverageRequired");
        var actuals = actual.Dimensions.GroupBy(x => (x.WbsNodeId, x.CostCodeId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        // The existing company-policy evaluator refuses negative consumed totals. The read model must
        // not advertise that unsupported credit balance as additional spending authority.
        if (actuals.Any(x => x.Value + commitments.GetValueOrDefault(x.Key) < 0))
            return Refuse("RealizationCoverageRequired", "NegativeConsumedAmountRequiresReview");
        var rows = budgets.OrderBy(x => x.WbsNodeId).ThenBy(x => x.CostCodeId).Select(x =>
        {
            var key = (x.WbsNodeId, x.CostCodeId); var cost = actuals.GetValueOrDefault(key); var commitment = commitments.GetValueOrDefault(key);
            return new ConstructionBudgetExposureRow(x.WbsNodeId, x.CostCodeId, x.Amount, cost, commitment, x.Amount - cost - commitment);
        }).ToArray();
        // No actor or approval-request/decision rows enter this witness: requesting/deciding must not
        // invalidate its own financial snapshot. Source identities enter even when totals coincide.
        var evidence = new
        {
            Company = c.CompanyId, Project = projectId, excludedProposalOrderId,
            Baseline = new { baseline.ID, baseline.RowVersion, baseline.CurrencyId },
            Policy = new { policy.Mode, policy.ConcurrencyToken }, Budgets = budgets.OrderBy(x => x.ID),
            Actual = actual.EvidenceVersion,
            Orders = orders.OrderBy(x => x.ID).Select(x => new { x.ID, x.OrderNo, x.OrderDate, x.VendorId, x.WarehouseId,
                x.CurrencyId, x.ExchangeRate, x.Status, x.SubTotal, x.TaxTotal, x.GrandTotal }),
            SourceLines = sourceLines.OrderBy(x => x.ID).Select(x => new { x.ID, x.PurchaseOrderId, x.LineNo, x.ItemId,
                x.ItemDescription, x.Qty, x.UoMId, x.UnitPrice, x.DiscountAmount, x.TaxRate, x.LineTotal, x.ReceivedQty }),
            Invoices = invoices.OrderBy(x => x.ID).Select(x => new { x.ID, x.InvoiceNo, x.InvoiceDate, x.ProjectId, x.VendorId,
                x.CurrencyId, x.ExchangeRate, x.Status, x.SubTotal, x.TaxTotal, x.GrandTotal,
                x.SubTotalBase, x.TaxTotalBase, x.GrandTotalBase, x.JournalEntryId }),
            InvoiceLines = invoiceLines.OrderBy(x => x.ID).Select(x => new { x.ID, x.PurchaseInvoiceId, x.LineNo, x.ItemDescription,
                x.Qty, x.UnitPrice, x.DiscountAmount, x.TaxRate, x.LineTotal, x.ExpenseAccountId, x.CostCenterId, x.ItemId, x.WarehouseId }),
            Entries = entries.OrderBy(x => x.ID).Select(x => new { x.ID, x.Status, x.SourceType, x.SourceId, x.ReversedByEntryId,
                x.FunctionalCurrencyId, x.FunctionalCurrencyDecimals }),
            Receipts = receipts.OrderBy(x => x.ID).Select(x => new { x.ID, x.Status, x.PurchaseOrderId, x.InvoiceId,
                x.CurrencyId, x.ExchangeRate, x.WarehouseId, x.TotalCost, Lines = x.Lines.OrderBy(l => l.ID) }),
            ClearingOrders = clearingOrders.OrderBy(x => x), ClearingCommitments = clearingCommitments.OrderBy(x => x.ID),
            Consumption = consumption.Records,
            Links = links.OrderBy(x => x.ID), LineLinks = lineLinks.OrderBy(x => x.ID),
            Reservations = reservations.OrderBy(x => x.ID), Releases = released.OrderBy(x => x), Allocations = allocationEvidence
        };
        // Preserve the prior byte shape for projects with no partial history.
        object versionEvidence = partialHeaders.Count == 0 && partialSlices.Count == 0 ? evidence : new {
            Source = evidence, PartialInvoices = partialHeaders.OrderBy(x => x.ID), PartialInvoiceSlices = partialSlices.OrderBy(x => x.ID) };
        if (rebaseEvidence.Count > 0) versionEvidence = new { Source = versionEvidence, ExposureRebases = rebaseEvidence };
        var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(versionEvidence)));
        return new(true, "CompleteBudgetExposure", baseline.CurrencyId, Array.AsReadOnly(rows), replaced.AsReadOnly(),
            BudgetVersionId: baseline.ID, CurrencyCode: currencyCode, Version: version);
    }

    private static bool Mirrors(JournalEntry original, JournalEntry mirror, IReadOnlyCollection<JournalEntryLine> ledger)
    {
        if (original.FunctionalCurrencyId != mirror.FunctionalCurrencyId || original.FunctionalCurrencyDecimals != mirror.FunctionalCurrencyDecimals) return false;
        var before = ledger.Where(x => x.JournalEntryId == original.ID).OrderBy(x => x.LineNo).ToArray();
        var after = ledger.Where(x => x.JournalEntryId == mirror.ID).OrderBy(x => x.LineNo).ToArray();
        return before.Length == after.Length && before.Zip(after).All(x => x.First.LineNo == x.Second.LineNo &&
            x.First.AccountId == x.Second.AccountId && x.First.CostCenterId == x.Second.CostCenterId &&
            x.First.Debit == x.Second.Credit && x.First.Credit == x.Second.Debit);
    }
}
