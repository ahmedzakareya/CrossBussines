using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionConsumptionPreview(bool Ok, string Code, string? Version = null, decimal? NetBaseCost = null);
public sealed record ConstructionConsumptionWrite(bool Ok, string Code, int? Id = null);
internal sealed record ConstructionConsumptionCoverage(bool Ok, string Code,
    IReadOnlyList<ConstructionMaterialConsumption> Records);
public interface IConstructionMaterialConsumptionService
{
    Task<ConstructionConsumptionPreview> PreviewAsync(BusinessContext context, int projectId, int receiptLineId, int issueMovementId, CancellationToken ct = default);
    Task<ConstructionConsumptionWrite> RegisterAsync(BusinessContext context, int projectId, int receiptLineId, int issueMovementId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default);
}

// Analytical provenance only. No stock, GL, reservation release, automatic FIFO or historical backfill.
public sealed class ConstructionMaterialConsumptionService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit) : IConstructionMaterialConsumptionService
{
    private sealed record Fact(ConstructionConsumptionIssue Issue, int ActualId, string Version, string LegacyVersion);
    private sealed record Candidate(string? Error, ConstructionConsumptionReceipt? Receipt = null, Fact? Fact = null,
        int ReservationId = 0, string? Version = null);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) => AllowedCore(c, project, ct);
    private async Task<bool> AllowedCore(BusinessContext c, int project, CancellationToken ct) => !c.IsExternalPortal &&
        c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    private async Task<Candidate> Load(BusinessContext c, int project, int receiptLineId, int issueId, CancellationToken ct,
        bool registeredRead = false)
    {
        var link = await (from l in db.GoodsReceiptLines.AsNoTracking()
            join r in db.GoodsReceipts.AsNoTracking() on l.GoodsReceiptId equals r.ID
            join p in db.PurchaseOrders.AsNoTracking() on r.PurchaseOrderId equals (int?)p.ID
            where l.ID == receiptLineId && r.CompanyID == c.CompanyId && p.CompanyID == c.CompanyId && p.ProjectId == project
            select new { Line = l, Receipt = r, Order = p }).SingleOrDefaultAsync(ct);
        if (link == null) return new("ReceiptSourceNotFound");
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.IsBaseline && x.Status == "Approved", ct);
        var invoiceLink = await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.PurchaseOrderId == link.Order.ID, ct);
        var invoice = invoiceLink == null ? null : await db.PurchaseInvoices.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == invoiceLink.PurchaseInvoiceId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        var invoiceEntry = invoice == null ? null : await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == invoice.JournalEntryId && x.CompanyID == c.CompanyId, ct);
        if (baseline == null) return new("InventoryInvoiceClearingNotProven");
        var ordered = await db.PurchaseOrderLines.AsNoTracking().Where(x => x.PurchaseOrderId == link.Order.ID).ToListAsync(ct);
        var invoiceId = invoice?.ID ?? 0;
        var invoiced = await db.PurchaseInvoiceLines.AsNoTracking().Where(x => x.PurchaseInvoiceId == invoiceId).ToListAsync(ct);
        var lineage = await db.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            x.PurchaseOrderId == link.Order.ID).ToListAsync(ct);
        List<ConstructionOrderAllocationLine>? mapped;
        if (invoiceLink == null)
            mapped = await ConstructionUnbilledReceiptWitness.ReadAsync(db, access, audit, c, project, baseline, link.Order, ct);
        else
        {
            if (invoice == null || invoiceEntry == null || link.Order.Status != "Closed" || invoice.Status != "Posted" ||
                invoice.VendorId != link.Order.VendorId || invoiceEntry.Status != "Posted" || invoiceEntry.SourceType != "PurchaseInvoice" ||
                invoiceEntry.SourceId != invoice.ID || invoiceEntry.FunctionalCurrencyId != baseline.CurrencyId)
                return new("InventoryInvoiceClearingNotProven");
            mapped = await ConstructionStockInvoiceWitness.ReadAsync(db, access, audit, c, project, baseline,
                link.Order, invoice, invoiceEntry, ordered, invoiced, lineage, ct);
        }
        if (mapped == null) return new("InventoryInvoiceClearingNotProven");
        var incoming = await db.StockMovements.AsNoTracking().SingleOrDefaultAsync(x => x.ID == link.Line.StockMovementId && x.CompanyID == c.CompanyId, ct);
        var incomingEntry = incoming == null ? null : await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == incoming.JournalEntryId && x.CompanyID == c.CompanyId, ct);
        var poLine = ordered.SingleOrDefault(x => x.ID == link.Line.PurchaseOrderLineId);
        if (incoming == null || incomingEntry?.FunctionalCurrencyDecimals is not (>= 0 and <= 8) || poLine == null || poLine.LineTotal <= 0)
            return new("ReceiptCoverageRequired");
        object? partialFinancialStage = null;
        var partialHeaders = await db.Set<ConstructionPartialInvoice>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.PurchaseOrderId == link.Order.ID).OrderBy(x => x.ID).ToListAsync(ct);
        if (partialHeaders.Count > 0 || await db.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().AnyAsync(x =>
            x.CompanyID == c.CompanyId && x.PurchaseOrderId == link.Order.ID, ct))
        {
            var history = await ConstructionPartialInvoiceHistoryReader.ReadAsync(db,
                new(c.CompanyId, project, link.Order.ID), incomingEntry.FunctionalCurrencyDecimals.Value, ct);
            if (!history.Ok) return new(history.Code);
            partialFinancialStage = new { Headers = partialHeaders, history.Quantities, Values = history.Values!.OrderBy(x => x.Key) };
        }
        var receipt = new ConstructionConsumptionReceipt(c.CompanyId, project, link.Order.ID, poLine.ID, link.Receipt.ID,
            link.Line.ID, incoming.ID, baseline.CurrencyId, incomingEntry.FunctionalCurrencyDecimals.Value,
            incoming.ItemId, incoming.WarehouseId, incoming.QtyBase, incoming.TotalCost);
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == link.Order.ID, ct);
        var actual = await new ConstructionActualCostService(db, access, audit).ReadCoverageLocked(c, project, null, null, ct);
        if (actual.Coverage.NetActualsByCurrency == null) return new("ActualCoverageRequired");

        async Task<Fact?> Issue(int id)
        {
            var movement = await db.StockMovements.AsNoTracking().SingleOrDefaultAsync(x => x.ID == id && x.CompanyID == c.CompanyId, ct);
            if (movement == null || movement.Direction != -1 || movement.SourceType != "Issue" || movement.ItemId != receipt.ItemId ||
                movement.WarehouseId != receipt.WarehouseId || movement.TotalCost <= 0 || movement.QtyBase <= 0) return null;
            var entry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.ID == movement.JournalEntryId && x.CompanyID == c.CompanyId, ct);
            if (entry == null || entry.Status != "Posted" || entry.SourceType != "Inventory" || entry.SourceId != movement.ID ||
                entry.FunctionalCurrencyId != receipt.CurrencyId || entry.FunctionalCurrencyDecimals != receipt.CurrencyDecimals) return null;
            var gl = await db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntryId == entry.ID).OrderBy(x => x.ID).ToListAsync(ct);
            if (gl.Count != 2 || gl.Any(x => x.ProjectId != project || x.Debit < 0 || x.Credit < 0) ||
                gl.Sum(x => x.Debit) != movement.TotalCost || gl.Sum(x => x.Credit) != movement.TotalCost) return null;
            var debit = gl.SingleOrDefault(x => x.Debit == movement.TotalCost && x.Credit == 0);
            var credit = gl.SingleOrDefault(x => x.Credit == movement.TotalCost && x.Debit == 0);
            if (debit == null || credit == null) return null;
            var classifications = await (from a in db.Accounts.AsNoTracking()
                join t in db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
                where a.CompanyID == c.CompanyId && (a.ID == debit.AccountId || a.ID == credit.AccountId)
                select new { a.ID, t.Code, t.StatementType }).ToListAsync(ct);
            if (!classifications.Any(x => x.ID == debit.AccountId && x.Code == "EXP" && x.StatementType == "IncomeStatement") ||
                !classifications.Any(x => x.ID == credit.AccountId && x.Code == "ASSET" && x.StatementType == "BalanceSheet")) return null;
            var inventoryId = await (from item in db.Items.AsNoTracking()
                join category in db.ItemCategories.AsNoTracking() on item.ItemCategoryId equals category.ID
                where item.ID == receipt.ItemId && item.CompanyID == c.CompanyId && category.CompanyID == c.CompanyId
                select category.InventoryAccountId).SingleOrDefaultAsync(ct);
            if (inventoryId != credit.AccountId) return null;
            var allocation = await db.Set<ConstructionActualCostAllocation>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == project && x.JournalEntryLineId == debit.ID && x.IsSealed, ct);
            if (allocation == null) return null;
            var splits = actual.Dimensions.Where(x => x.JournalLineId == debit.ID).ToArray();
            var original = mapped.Where(x => x.SourceLineId == poLine.ID).GroupBy(x => (x.WbsNodeId, x.CostCodeId, x.CostCategory))
                .ToDictionary(x => x.Key, x => x.Sum(l => l.NetDocumentAmount));
            if (original.Count != splits.Length || splits.Any(x => x.Amount <= 0 || x.CostCategory != "Material" ||
                !original.TryGetValue((x.WbsNodeId, x.CostCodeId, x.CostCategory), out var amount) ||
                checked(x.Amount * poLine.LineTotal) != checked(amount * movement.TotalCost))) return null;
            var issue = new ConstructionConsumptionIssue(c.CompanyId, project, movement.ID, entry.ID, debit.ID,
                receipt.CurrencyId, receipt.CurrencyDecimals, movement.ItemId, movement.WarehouseId, movement.QtyBase, movement.TotalCost);
            object Evidence(int? invoiceId) => new { receipt, Issue = issue, movement,
                ReceiptSource = new { link.Receipt.ID, link.Receipt.ReceiptDate, InvoiceId = invoiceId, link.Receipt.Status,
                    link.Receipt.TotalCost, link.Line, Movement = incoming, Entry = new { incomingEntry.ID, incomingEntry.EntryDate,
                        incomingEntry.FiscalPeriodId, incomingEntry.Status, incomingEntry.FunctionalCurrencyId, incomingEntry.FunctionalCurrencyDecimals } },
                Entry = new { entry.ID, entry.EntryDate, entry.FiscalPeriodId, entry.Status, entry.SourceType,
                entry.SourceId, entry.FunctionalCurrencyId, entry.FunctionalCurrencyDecimals },
                GL = gl.Select(x => new { x.ID, x.JournalEntryId, x.LineNo, x.AccountId, x.Debit, x.Credit, x.ProjectId, x.CostCenterId }), Types = classifications.OrderBy(x => x.ID),
                Actual = allocation, Dimensions = splits.OrderBy(x => x.WbsNodeId).ThenBy(x => x.CostCodeId), Original = mapped.OrderBy(x => x.ID), Reservation = reservation.ID };
            // The receipt's later invoice link is a separately verified financial transition.
            // Keep original issue/receipt facts stable; accept legacy invoiced witnesses unchanged.
            return new(issue, allocation.ID, Hash(Evidence(null)), Hash(Evidence(link.Receipt.InvoiceId)));
        }

        var proposed = await Issue(issueId);
        if (proposed == null) return new("IssueOrActualSourceNotProven");
        if (!registeredRead && await db.Set<ConstructionMaterialConsumption>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId &&
            (x.IssueMovementId == issueId || x.ExpenseJournalLineId == proposed.Issue.ExpenseJournalLineId), ct)) return new("IssueAlreadyAttributed");
        var previous = await db.Set<ConstructionMaterialConsumption>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.GoodsReceiptLineId == receiptLineId).OrderBy(x => x.ID).ToListAsync(ct);
        var issues = new List<ConstructionConsumptionIssue>(); var links = new List<ConstructionConsumptionLink>();
        foreach (var record in previous)
        {
            var fact = await Issue(record.IssueMovementId);
            if (fact == null || record.ProjectId != project || record.PurchaseOrderId != receipt.OrderId || record.PurchaseOrderLineId != receipt.OrderLineId ||
                record.GoodsReceiptId != receipt.ReceiptId || record.ReceiptMovementId != receipt.MovementId || record.ReservationId != reservation.ID ||
                record.SourceVersion != fact.Version && record.SourceVersion != fact.LegacyVersion ||
                record.ActualAllocationId != fact.ActualId || record.IssueJournalEntryId != fact.Issue.JournalEntryId ||
                record.ExpenseJournalLineId != fact.Issue.ExpenseJournalLineId || record.QuantityBase != fact.Issue.QuantityBase || record.NetBaseCost != fact.Issue.NetBaseCost ||
                record.FunctionalCurrencyId != receipt.CurrencyId || record.FunctionalCurrencyDecimals != receipt.CurrencyDecimals)
                return new("ConsumptionHistoryRequiresReview");
            issues.Add(fact.Issue); links.Add(Link(receipt, fact.Issue));
        }
        if (registeredRead)
        {
            if (previous.Count(x => x.IssueMovementId == issueId) != 1) return new("ConsumptionHistoryRequiresReview");
        }
        else { issues.Add(proposed.Issue); links.Add(Link(receipt, proposed.Issue)); }
        var check = ConstructionConsumptionLineageRules.Validate(c.CompanyId, project, receipt.CurrencyId, new[] { receipt }, issues, links);
        if (!check.Ok) return new(check.Code);
        var financialStage = new { link.Order.Status, link.Receipt.InvoiceId, Invoice = invoiceEntry == null ? null : new {
                invoiceEntry.ID, invoiceEntry.EntryDate, invoiceEntry.FiscalPeriodId, invoiceEntry.Status,
                invoiceEntry.FunctionalCurrencyId, invoiceEntry.FunctionalCurrencyDecimals } };
        // The stored issue/receipt fact hash above stays immutable across financial transitions.
        // Only an outstanding preview intent gains the newly proved partial-invoice stage.
        object evidence = partialFinancialStage == null ? new { proposed.Version, FinancialStage = financialStage, Prior = previous } :
            new { proposed.Version, FinancialStage = new { Source = financialStage, PartialInvoices = partialFinancialStage }, Prior = previous };
        return new(null, receipt, proposed, reservation.ID, Hash(evidence));
    }

    // Called inside the same serializable exposure/admission transaction. A stored row is not
    // sufficient: revalidate every receipt's complete graph against current financial sources.
    // BudgetView is enough to READ; admission callers already hold BudgetManage. Neither
    // permission is replaced with a bootstrap or role-name shortcut. RegisterAsync retains BudgetManage.
    internal async Task<ConstructionConsumptionCoverage> ReadCoverageLocked(BusinessContext c, int project, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A consumption coverage transaction is required");
        if (c.IsExternalPortal || !c.IsAuthenticated || c.EmployeeId is not > 0 || c.CompanyId <= 0 ||
            db.CompanyScope.FilterCompanyId != c.CompanyId ||
            !(await access.CanAsync(c, ProjectsActions.BudgetView, new PermissionTarget { ProjectId = project }, ct) ||
              await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct)) ||
            !await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct))
            return new(false, "AccessDenied", Array.Empty<ConstructionMaterialConsumption>());
        var records = await db.Set<ConstructionMaterialConsumption>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project).OrderBy(x => x.ID).ToListAsync(ct);
        foreach (var group in records.GroupBy(x => x.GoodsReceiptLineId))
        {
            var candidate = await Load(c, project, group.Key, group.First().IssueMovementId, ct, registeredRead: true);
            if (candidate.Error != null) return new(false, candidate.Error, Array.Empty<ConstructionMaterialConsumption>());
        }
        return new(true, "CompleteConsumptionCoverage", records.AsReadOnly());
    }

    private static ConstructionConsumptionLink Link(ConstructionConsumptionReceipt r, ConstructionConsumptionIssue i) =>
        new(r.CompanyId, r.ProjectId, r.OrderId, r.OrderLineId, r.ReceiptLineId, r.MovementId, i.MovementId, i.JournalEntryId, i.ExpenseJournalLineId);

    public async Task<ConstructionConsumptionPreview> PreviewAsync(BusinessContext c, int projectId, int receiptLineId, int issueMovementId, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var candidate = await Load(c, projectId, receiptLineId, issueMovementId, ct); await tx.CommitAsync(ct);
            return candidate.Error == null ? new(true, "Ready", candidate.Version, candidate.Fact!.Issue.NetBaseCost) : new(false, candidate.Error);
        }
        catch (OverflowException) { return new(false, "ConsumptionAmountOverflow"); }
    }

    public async Task<ConstructionConsumptionWrite> RegisterAsync(BusinessContext c, int projectId, int receiptLineId, int issueMovementId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (receiptLineId <= 0 || issueMovementId <= 0 || expectedVersion?.Length != 64 || requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return new(false, "InvalidConsumptionRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        var requestHash = Hash(new { projectId, receiptLineId, issueMovementId, expectedVersion, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var replay = await db.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (replay != null)
            {
                if (replay.ProjectId != projectId || replay.RequestHash != requestHash) return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return new(true, "AlreadyRegistered", replay.ID); // historical acknowledgement only
            }
            var candidate = await Load(c, projectId, receiptLineId, issueMovementId, ct);
            if (candidate.Error != null) return new(false, candidate.Error);
            if (candidate.Version != expectedVersion) return new(false, "ConcurrencyConflict");
            var r = candidate.Receipt!; var f = candidate.Fact!; var i = f.Issue;
            var row = new ConstructionMaterialConsumption { CompanyID = c.CompanyId, ProjectId = projectId,
                PurchaseOrderId = r.OrderId, PurchaseOrderLineId = r.OrderLineId, GoodsReceiptId = r.ReceiptId, GoodsReceiptLineId = r.ReceiptLineId,
                ReceiptMovementId = r.MovementId, IssueMovementId = i.MovementId, IssueJournalEntryId = i.JournalEntryId,
                ExpenseJournalLineId = i.ExpenseJournalLineId, ActualAllocationId = f.ActualId, ReservationId = candidate.ReservationId,
                FunctionalCurrencyId = r.CurrencyId, FunctionalCurrencyDecimals = r.CurrencyDecimals, QuantityBase = i.QuantityBase,
                NetBaseCost = i.NetBaseCost, SourceVersion = f.Version, RequestId = requestId, RequestHash = requestHash,
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionMaterialConsumption), row.ID,
                null, "RegisterMaterialConsumption", null, JsonSerializer.Serialize(new { r.OrderId, r.ReceiptLineId, i.MovementId, i.NetBaseCost }),
                ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionMaterialConsumptionService), requestId) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, "Registered", row.ID);
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "ConsumptionAmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private static bool Conflict(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
