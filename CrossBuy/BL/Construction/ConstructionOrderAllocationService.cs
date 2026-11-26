using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionAllocationPreview(bool Ok, string Code, string? Version = null,
    ConstructionOrderSource? Source = null, int? BudgetVersionId = null);
public sealed record ConstructionAllocationWrite(bool Ok, string Code, int? Id = null);
public interface IConstructionOrderAllocationService
{
    Task<ConstructionAllocationPreview> PreviewAsync(BusinessContext context, int projectId, int orderId, CancellationToken ct = default);
    Task<ConstructionAllocationWrite> SaveAsync(BusinessContext context, int projectId, int orderId, string expectedVersion,
        Guid requestId, IReadOnlyList<ConstructionCommitmentSplit> splits, string reason, CancellationToken ct = default);
}

// Analytical revisions only. No reservation, budget availability total or document approval is created.
public sealed class ConstructionOrderAllocationService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit) : IConstructionOrderAllocationService
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) =>
        c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    internal async Task<ConstructionAllocationPreview> Load(BusinessContext c, int project, int orderId, bool locking, CancellationToken ct, int? priorAllocationForToken = null, int? clearingInvoiceId = null)
    {
        // Same first lock as receipt and conversion. Never authorize against an EF-tracked PO snapshot.
        var orders = locking && db.Database.IsSqlServer()
            ? db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM dbo.PurchaseOrders WITH (UPDLOCK,HOLDLOCK) WHERE ID={orderId} AND CompanyID={c.CompanyId} AND ProjectId={project}")
            : db.PurchaseOrders.Where(x => x.ID == orderId && x.CompanyID == c.CompanyId && x.ProjectId == project);
        var order = await orders.AsNoTracking().SingleOrDefaultAsync(ct);
        if (order == null) return new(false, "SourceNotFound");
        var invoiceLink = await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct);
        // Closed-source support is internal read-only reconciliation, never an editor/admission gateway.
        if (clearingInvoiceId.HasValue)
        {
            if (order.Status != "Closed" || invoiceLink?.PurchaseInvoiceId != clearingInvoiceId.Value)
                return new(false, "SourceNotOpenAndReconciled");
        }
        else if (order.Status is not ("Draft" or "Approved" or "Received") || invoiceLink != null)
            return new(false, "SourceNotOpenAndReconciled");
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) return new(false, "PolicyNotConfigured");
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == project && x.IsBaseline && x.Status == "Approved", ct);
        if (baseline == null) return new(false, "ApprovedBaselineRequired");
        var currency = await db.Currencies.AsNoTracking().Where(x => x.ID == order.CurrencyId).Select(x => x.Code).SingleOrDefaultAsync(ct);
        if (order.CurrencyId is not > 0 || string.IsNullOrWhiteSpace(currency)) return new(false, "DocumentCurrencyNotProven");
        if (baseline.CurrencyId != order.CurrencyId) return new(false, "BudgetCurrencyMismatch");
        var rows = await db.PurchaseOrderLines.AsNoTracking().Where(x => x.PurchaseOrderId == orderId).OrderBy(x => x.ID).ToListAsync(ct);
        var lines = rows.Select(x => new ConstructionOrderLineSource(x.ID, x.LineNo, x.ItemId, x.Qty,
            x.UnitPrice, x.DiscountAmount, x.TaxRate, x.LineTotal, x.ReceivedQty)).ToArray();
        try
        {
            if (lines.Length == 0 || lines.Any(x => x.Quantity <= 0 || x.UnitPrice < 0 || x.Discount < 0 || x.NetDocumentAmount < 0) ||
                lines.Sum(x => x.NetDocumentAmount) != order.SubTotal || order.TaxTotal < 0 || order.SubTotal + order.TaxTotal != order.GrandTotal)
                return new(false, "SourceNotOpenAndReconciled");
        }
        catch (OverflowException) { return new(false, "SourceNotOpenAndReconciled"); }
        var latest = await db.Set<ConstructionOrderAllocation>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId)
            .Select(x => (int?)x.ID).MaxAsync(ct) ?? 0;
        var source = new ConstructionOrderSource(order.ID, order.OrderNo, order.OrderDate, order.Status, order.VendorId,
            order.CurrencyId, currency, order.ExchangeRate, order.SubTotal, order.TaxTotal, order.GrandTotal, order.Status == "Draft" ? "DraftProposal" : "Open", order.SubTotal, null, lines);
        var witness = source;
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct);
        if (clearingInvoiceId.HasValue && draft == null) return new(false, "AdmissionSourceNotProven");
        if (order.Status == "Draft" && draft != null && ConstructionPurchaseDraftService.CreationHash(order, rows) != draft.InitialSourceVersion) return new(false, "DraftSourceChanged");
        if (order.Status is "Approved" or "Received" or "Closed" && draft != null)
        {
            var admission = await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == orderId, ct);
            if (!await ConstructionReceiptWitness.ProvenAsync(db, c.CompanyId, order, rows, ct, clearingInvoiceId)) return new(false, "ReceiptCoverageRequired");
            var content = ConstructionPurchaseDraftService.CreationHash(order, ConstructionReceiptWitness.OriginalLines(rows));
            if (admission == null || admission.ContentHash != content || admission.ApprovedVersion != ConstructionPurchaseApprovalService.ApprovedVersion(content)) return new(false, "AdmissionSourceNotProven");
            var request = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == admission.ApprovalRequestId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == orderId, ct);
            if (request == null || request.ContentHash != content || request.SourceVersion != admission.BeforeVersion ||
                !await db.InventoryApprovals.AsNoTracking().AnyAsync(x => x.ID == request.ApprovalId && x.CompanyID == c.CompanyId && x.Status == "Approved" && x.DecidedByEmployeeId == admission.CreatedBy, ct) ||
                !await db.Set<ConstructionCommitmentReservation>().AsNoTracking().AnyAsync(x => x.ID == admission.ReservationId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == orderId && x.AllocationId == request.AllocationId && x.SourceVersion == admission.BeforeVersion, ct)) return new(false, "AdmissionSourceNotProven");
            // Only the exact immutable transition authorizes reconstructing the BEFORE witness.
            // Returned source/display retains the actual Approved/Received status.
            witness = source with { Status = "Draft", Classification = "DraftProposal",
                Lines = lines.Select(x => x with { ReceivedQuantity = ConstructionReceiptWitness.OriginalReceivedZero(x.ReceivedQuantity) }).ToArray() };
        }
        // Exact source identity, approved baseline and explicit policy. Not a spending-approval token.
        var version = Hash(new { Company = c.CompanyId, Project = project, Source = witness, Baseline = baseline.ID,
            baseline.RowVersion, Policy = policy.Mode, policy.ConcurrencyToken, LatestAllocation = priorAllocationForToken ?? latest });
        return new(true, "Ready", version, source, baseline.ID);
    }

    public async Task<ConstructionAllocationPreview> PreviewAsync(BusinessContext c, int projectId, int orderId, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await Load(c, projectId, orderId, false, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ConstructionAllocationWrite> SaveAsync(BusinessContext c, int projectId, int orderId, string expectedVersion,
        Guid requestId, IReadOnlyList<ConstructionCommitmentSplit> splits, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length != 64 ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000 || splits.Count == 0 || splits.Count > 10000)
            return new(false, "InvalidAllocationRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        // Freeze mutable caller collections before the first await inside the write transaction.
        var input = splits.OrderBy(x => x.SourceLineId).ThenBy(x => x.WbsNodeId).ThenBy(x => x.CostCodeId).ToArray();
        var requestHash = Hash(new { projectId, orderId, expectedVersion, Reason = reason.Trim(), Splits = input });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var preview = await Load(c, projectId, orderId, true, ct);
            // Existing accepted request remains a historical acknowledgement even if its PO is now closed.
            var existing = await db.Set<ConstructionOrderAllocation>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (existing != null)
            {
                if (existing.ProjectId != projectId || existing.PurchaseOrderId != orderId || existing.RequestHash != requestHash || !existing.IsSealed)
                    return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return new(true, "AlreadySaved", existing.ID);
            }
            if (!preview.Ok) return new(false, preview.Code);
            if (await (from request in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                       join approval in db.InventoryApprovals.AsNoTracking() on request.ApprovalId equals approval.ID
                       where request.CompanyID == c.CompanyId && request.ProjectId == projectId && request.PurchaseOrderId == orderId && approval.CompanyID == c.CompanyId && approval.Status == "Pending"
                       select request.ID).AnyAsync(ct)) return new(false, "ApprovalAlreadyPending");
            if (await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct)) return new(false, "SourceAlreadyAdmitted");
            if (preview.Version != expectedVersion) return new(false, "ConcurrencyConflict");
            var baselineLines = await db.Set<ConstructionBudgetLine>().AsNoTracking()
                .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.BudgetVersionId == preview.BudgetVersionId).ToListAsync(ct);
            var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToDictionaryAsync(x => x.ID, ct);
            var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId).ToDictionaryAsync(x => x.ID, ct);
            var dimensions = baselineLines.Select(x => new ConstructionCommitmentDimension(x.WbsNodeId, x.CostCodeId,
                nodes.TryGetValue(x.WbsNodeId, out var n) && n.Status == "Active" && n.OwnsBudget &&
                codes.TryGetValue(x.CostCodeId, out var code) && code.IsActive && code.AllowDirectPosting && code.Category == x.CostCategory,
                nodes.TryGetValue(x.WbsNodeId, out var owner) && owner.OwnsCost, true)).ToArray();
            var check = ConstructionCommitmentAllocationRules.Validate(preview.Source!, preview.Source!.CurrencyId!.Value, input, dimensions);
            if (!check.Ok) return new(false, check.Code);
            var revision = (await db.Set<ConstructionOrderAllocation>().AsNoTracking()
                .Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId).Select(x => (int?)x.RevisionNo).MaxAsync(ct) ?? 0) + 1;
            var header = new ConstructionOrderAllocation { CompanyID = c.CompanyId, ProjectId = projectId, PurchaseOrderId = orderId,
                BudgetVersionId = preview.BudgetVersionId!.Value, RevisionNo = revision, RequestId = requestId, RequestHash = requestHash,
                SourceVersion = expectedVersion, Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(header); await db.SaveChangesAsync(ct);
            db.AddRange(input.Select(x => new ConstructionOrderAllocationLine { CompanyID = c.CompanyId, ProjectId = projectId,
                AllocationId = header.ID, PurchaseOrderId = orderId, SourceLineId = x.SourceLineId, WbsNodeId = x.WbsNodeId,
                CostCodeId = x.CostCodeId, CostCategory = codes[x.CostCodeId].Category, NetDocumentAmount = x.NetDocumentAmount }));
            await db.SaveChangesAsync(ct);
            header.IsSealed = true;
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionOrderAllocation), header.ID,
                null, "AllocatePurchaseOrder", null, JsonSerializer.Serialize(new { orderId, header.RevisionNo, header.SourceVersion, Splits = input }),
                ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionOrderAllocationService), requestId) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct); return new(true, "Saved", header.ID);
        }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    internal async Task<(string? Error, List<ConstructionOrderAllocationLine> Lines)> ValidateCurrentAsync(BusinessContext c, int project,
        ConstructionOrderAllocation header, CancellationToken ct, int? clearingInvoiceId = null)
    {
        var revisions = db.Set<ConstructionOrderAllocation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == header.PurchaseOrderId);
        if (!header.IsSealed || header.ProjectId != project || await revisions.AnyAsync(x => x.ID > header.ID, ct)) return ("AllocationNotCurrent", new());
        var previous = await revisions.Where(x => x.RevisionNo < header.RevisionNo).Select(x => (int?)x.ID).MaxAsync(ct) ?? 0;
        // The allocation token predates its own insertion; reconstruct that exact predecessor boundary.
        var source = await Load(c, project, header.PurchaseOrderId, false, ct, previous, clearingInvoiceId);
        if (!source.Ok) return (source.Code, new());
        if (source.Version != header.SourceVersion || source.BudgetVersionId != header.BudgetVersionId) return ("AllocationSourceChanged", new());
        var lines = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x => x.AllocationId == header.ID && x.CompanyID == c.CompanyId && x.ProjectId == project).ToListAsync(ct);
        var budget = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.BudgetVersionId == header.BudgetVersionId && x.CompanyID == c.CompanyId && x.ProjectId == project).ToListAsync(ct);
        var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == project).ToDictionaryAsync(x => x.ID, ct);
        var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId).ToDictionaryAsync(x => x.ID, ct);
        var dimensions = budget.Select(x => new ConstructionCommitmentDimension(x.WbsNodeId, x.CostCodeId,
            nodes.TryGetValue(x.WbsNodeId, out var n) && n.Status == "Active" && n.OwnsBudget &&
            codes.TryGetValue(x.CostCodeId, out var code) && code.IsActive && code.AllowDirectPosting && code.Category == x.CostCategory,
            nodes.TryGetValue(x.WbsNodeId, out var owner) && owner.OwnsCost, true)).ToArray();
        var check = ConstructionCommitmentAllocationRules.Validate(source.Source!, source.Source!.CurrencyId!.Value,
            lines.Select(x => new ConstructionCommitmentSplit(x.SourceLineId, x.WbsNodeId, x.CostCodeId, x.NetDocumentAmount)).ToArray(), dimensions);
        return (check.Ok ? null : check.Code, lines);
    }

    private static bool Conflict(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
            if (e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
