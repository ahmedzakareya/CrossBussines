using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionRebaseRequestResult(bool Ok, string Code, string? Version = null, int? Id = null);

// Qualified proposal, independent review and exposure mapping. Browser orchestration never
// replaces these transaction, current-source and independence checks.
public sealed class ConstructionReservationRebaseRequestService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit)
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) =>
        !c.IsExternalPortal && c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 &&
        !db.CompanyScope.AllowsCrossCompany && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    private async Task<ConstructionRebaseRequestResult> ReadLocked(BusinessContext c, int project, int reservationId,
        int targetId, CancellationToken ct)
    {
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == reservationId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (reservation == null) return new(false, "ReservationNotFound");
        if (await db.Set<ConstructionCommitmentRelease>().AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.CompanyID == c.CompanyId, ct))
            return new(false, "ReservationReleased");
        var target = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == targetId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.IsBaseline && x.Status == "Approved", ct);
        var original = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == reservation.BudgetVersionId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (target == null || original == null || original.Status != "Superseded" ||
            target.PreviousVersionId != original.ID || target.CurrencyId != reservation.CurrencyId || original.CurrencyId != target.CurrencyId)
            return new(false, "RebaseBaselineLineageRequired");
        var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == reservation.AllocationId && x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.PurchaseOrderId == reservation.PurchaseOrderId && x.BudgetVersionId == original.ID && x.IsSealed, ct);
        if (allocation == null || allocation.SourceVersion != reservation.SourceVersion)
            return new(false, "ReservationCoverageRequired");
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) return new(false, "PolicyNotConfigured");
        var lines = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x =>
            x.AllocationId == allocation.ID && x.CompanyID == c.CompanyId && x.ProjectId == project).OrderBy(x => x.ID).ToListAsync(ct);
        var targetLines = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x =>
            x.BudgetVersionId == target.ID && x.CompanyID == c.CompanyId && x.ProjectId == project).OrderBy(x => x.ID).ToListAsync(ct);
        var decimals = await db.Currencies.AsNoTracking().Where(x => x.ID == target.CurrencyId).Select(x => (int?)x.DecimalPlaces).SingleOrDefaultAsync(ct);
        if (decimals == null) return new(false, "InvalidCurrencyPrecision");
        var dimensions = lines.Select(x => new ConstructionRebaseDimension(x.SourceLineId, x.WbsNodeId, x.CostCodeId, x.CostCategory, x.NetDocumentAmount)).ToArray();
        var check = ConstructionReservationRebaseRules.Validate(reservation.CurrencyId, target.CurrencyId, decimals.Value,
            reservation.NetDocumentAmount, dimensions, dimensions);
        if (!check.Ok) return new(false, check.Code);
        if (targetLines.Count == 0 || lines.Any(x => !targetLines.Any(b => b.WbsNodeId == x.WbsNodeId && b.CostCodeId == x.CostCodeId && b.CostCategory == x.CostCategory)))
            return new(false, "BudgetDimensionCoverageRequired");
        // This witness deliberately does not assert remaining budget, financial source validity,
        // or approval. Those must be freshly proved by the future effective-rebase command.
        return new(true, "ProposalReady", Hash(new { reservation, allocation, Lines = lines, Original = original,
            Target = target, TargetLines = targetLines, Policy = new { policy.Mode, policy.ConcurrencyToken }, Decimals = decimals.Value }));
    }

    public async Task<ConstructionRebaseRequestResult> PreviewAsync(BusinessContext c, int project, int reservationId,
        int targetId, CancellationToken ct = default) => await Execute(c, project,
            () => ReadLocked(c, project, reservationId, targetId, ct), ct);

    public async Task<ConstructionRebaseRequestResult> RequestAsync(BusinessContext c, int project, int reservationId,
        int targetId, string expectedVersion, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || expectedVersion?.Length != 64 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidRebaseRequest");
        return await Execute(c, project, async () =>
        {
            var requestHash = Hash(new { project, reservationId, targetId, expectedVersion, Reason = reason.Trim() });
            var prior = await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (prior != null) return prior.ProjectId == project && prior.RequestHash == requestHash
                ? new(true, "AlreadyRequested", prior.SourceVersion, prior.ID) : new(false, "IdempotencyConflict");
            var preview = await ReadLocked(c, project, reservationId, targetId, ct);
            if (!preview.Ok) return preview;
            if (preview.Version != expectedVersion) return new(false, "ConcurrencyConflict");
            var row = new ConstructionReservationRebaseRequest { CompanyID = c.CompanyId, ProjectId = project,
                ReservationId = reservationId, TargetBudgetVersionId = targetId, RequestId = requestId,
                RequestHash = requestHash, SourceVersion = expectedVersion, Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionReservationRebaseRequest), row.ID,
                null, "RequestReservationRebase", null, JsonSerializer.Serialize(new { reservationId, targetId, expectedVersion }),
                ConstructionChangeKind.Created, row.Reason, c.EmployeeId, nameof(ConstructionReservationRebaseRequestService), requestId) });
            await db.SaveChangesAsync(ct);
            return new(true, "RebaseRequested", expectedVersion, row.ID);
        }, ct);
    }

    // A decision reviews the proposal only. Effective admission/readers must not infer authority
    // from this row until the separate effective-rebase workflow has been qualified.
    public async Task<ConstructionRebaseRequestResult> DecideAsync(BusinessContext c, int project, int proposalId,
        string expectedVersion, bool approve, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || expectedVersion?.Length != 64 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidRebaseRequest");
        return await Execute(c, project, async () =>
        {
            var outcome = approve ? "Approved" : "Rejected";
            var requestHash = Hash(new { project, proposalId, expectedVersion, outcome, Actor = c.EmployeeId, Reason = reason.Trim() });
            var prior = await db.Set<ConstructionReservationRebaseDecision>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (prior != null) return prior.ProjectId == project && prior.RequestHash == requestHash
                ? new(true, "AlreadyDecided", prior.SourceVersion, prior.ID) : new(false, "IdempotencyConflict");
            var proposal = await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.ID == proposalId && x.ProjectId == project && x.CompanyID == c.CompanyId, ct);
            if (proposal == null) return new(false, "RebaseRequestNotFound");
            if (proposal.CreatedBy == c.EmployeeId) return new(false, "SeparateApproverRequired");
            if (proposal.SourceVersion != expectedVersion) return new(false, "ConcurrencyConflict");
            if (await db.Set<ConstructionReservationRebaseDecision>().AsNoTracking().AnyAsync(x =>
                x.CompanyID == c.CompanyId && x.RebaseRequestId == proposalId, ct)) return new(false, "RebaseAlreadyDecided");
            if (approve)
            {
                var current = await ReadLocked(c, project, proposal.ReservationId, proposal.TargetBudgetVersionId, ct);
                if (!current.Ok) return current;
                if (current.Version != proposal.SourceVersion) return new(false, "ConcurrencyConflict");
            }
            var row = new ConstructionReservationRebaseDecision { CompanyID = c.CompanyId, ProjectId = project,
                RebaseRequestId = proposalId, Outcome = outcome, RequestId = requestId, RequestHash = requestHash,
                SourceVersion = proposal.SourceVersion, Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionReservationRebaseDecision), row.ID,
                null, "DecideReservationRebase", null, JsonSerializer.Serialize(new { proposalId, outcome, row.SourceVersion }),
                ConstructionChangeKind.Created, row.Reason, c.EmployeeId, nameof(ConstructionReservationRebaseRequestService), requestId) });
            await db.SaveChangesAsync(ct);
            return new(true, "Rebase" + outcome, row.SourceVersion, row.ID);
        }, ct);
    }

    // Source diagnostic only: no remaining-budget assertion, effective fact or permission to spend.
    // Historical baseline RowVersion changes on supersession, so never reconstruct an old
    // allocation token from the new baseline. Use the immutable purchase-creation witness instead.
    public async Task<ConstructionRebaseRequestResult> ReadSourceReadinessAsync(BusinessContext c, int project,
        int proposalId, CancellationToken ct = default) => await Execute(c, project,
            () => ReadSourceReadinessLockedAsync(c, project, proposalId, ct), ct);

    internal Task<ConstructionRebaseRequestResult> ReadSourceReadinessLockedAsync(BusinessContext c, int project,
        int proposalId, CancellationToken ct) => ReadSourceReadinessCore(c, project, proposalId, false, ct);

    internal async Task<ConstructionRebaseRequestResult> ReadAdmittedSourceReadinessLockedAsync(BusinessContext c,
        int project, int proposalId, CancellationToken ct, int? authorizedReceiptWarehouseId = null)
    {
        if (!await ConstructionRebasedPurchaseWitness.ProvenAsync(db, c, project, proposalId, ct))
            return new(false, "RebasedAdmissionSourceNotProven");
        return await ReadSourceReadinessCore(c, project, proposalId, true, ct, authorizedReceiptWarehouseId);
    }

    private async Task<ConstructionRebaseRequestResult> ReadSourceReadinessCore(BusinessContext c, int project,
        int proposalId, bool provenAdmission, CancellationToken ct, int? authorizedReceiptWarehouseId = null)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A source-readiness transaction is required");
        var proposal = await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == proposalId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (proposal == null) return new(false, "RebaseRequestNotFound");
        var decision = await db.Set<ConstructionReservationRebaseDecision>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.RebaseRequestId == proposalId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (decision == null || decision.Outcome != "Approved" || decision.CreatedBy == proposal.CreatedBy ||
            decision.SourceVersion != proposal.SourceVersion) return new(false, "ApprovedRebaseProposalRequired");
        var current = await ReadLocked(c, project, proposal.ReservationId, proposal.TargetBudgetVersionId, ct);
        if (!current.Ok) return current;
        if (current.Version != proposal.SourceVersion) return new(false, "ConcurrencyConflict");
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(x =>
            x.ID == proposal.ReservationId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
            x.ID == reservation.PurchaseOrderId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (order == null) return new(false, "ReservationCoverageRequired");
        if ((provenAdmission ? order.Status is not ("Approved" or "Received") : order.Status != "Draft") ||
            (!provenAdmission && (order.Lines.Any(x => x.ReceivedQty != 0) ||
                await db.GoodsReceipts.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct))) ||
            await db.Set<CrossBuy.Models.Context.Inventory.PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct) ||
            (!provenAdmission && (await db.Set<ConstructionPartialInvoice>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct) ||
                await db.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct))) ||
            (!provenAdmission && await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order.ID, ct)))
            return new(false, "RebaseRealizedSourceUnsupported");
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID, ct);
        if (draft == null || draft.BudgetVersionId != reservation.BudgetVersionId || draft.CurrencyId != reservation.CurrencyId)
            return new(false, "DraftSourceUnproven");
        if (provenAdmission && !await ConstructionReceiptWitness.ProvenAsync(db, c.CompanyId, order,
                order.Lines.ToArray(), ct, authorizedReceiptWarehouseId: authorizedReceiptWarehouseId))
            return new(false, "ReceiptCoverageRequired");
        var sourceHash = ConstructionPurchaseDraftService.CreationHash(order,
            provenAdmission ? ConstructionReceiptWitness.OriginalLines(order.Lines) : null);
        if (sourceHash != draft.InitialSourceVersion) return new(false, "DraftSourceChanged");
        var decimals = await db.Currencies.AsNoTracking().Where(x => x.ID == reservation.CurrencyId).Select(x => (int?)x.DecimalPlaces).SingleOrDefaultAsync(ct);
        if (decimals != draft.CurrencyDecimals || order.CurrencyId != reservation.CurrencyId || order.ExchangeRate != 1m)
            return new(false, "BudgetCurrencyMismatch");
        // Admission may continue only through sealed, reconciled receipt-clearing invoices.
        // This reader has no dependency back to rebase/exposure, so proof cannot recurse.
        if (provenAdmission)
        {
            var history = await ConstructionPartialInvoiceHistoryReader.ReadAsync(db, new(c.CompanyId, project, order.ID), draft.CurrencyDecimals, ct);
            if (!history.Ok) return new(false, history.Code);
        }
        var allocations = await db.Set<ConstructionOrderAllocation>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order.ID).ToListAsync(ct);
        if (allocations.Count == 0 || allocations.Max(x => x.ID) != reservation.AllocationId) return new(false, "AllocationNotCurrent");
        var lines = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.AllocationId == reservation.AllocationId).ToListAsync(ct);
        var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == project).ToListAsync(ct);
        var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId).ToListAsync(ct);
        if (lines.Any(x => !nodes.Any(n => n.ID == x.WbsNodeId && n.Status == "Active" && n.OwnsBudget && n.OwnsCost) ||
            !codes.Any(code => code.ID == x.CostCodeId && code.IsActive && code.AllowDirectPosting && code.Category == x.CostCategory)))
            return new(false, "InvalidBudgetDimension");
        try
        {
            if (order.Lines.Count == 0 || order.Lines.Any(x => x.Qty <= 0 || x.LineTotal < 0 ||
                    lines.Where(a => a.SourceLineId == x.ID).Sum(a => a.NetDocumentAmount) != x.LineTotal) ||
                lines.Any(x => !order.Lines.Any(l => l.ID == x.SourceLineId)) ||
                order.Lines.Sum(x => x.LineTotal) != reservation.NetDocumentAmount || order.SubTotal != reservation.NetDocumentAmount ||
                order.SubTotal + order.TaxTotal != order.GrandTotal)
                return new(false, "ReservationCoverageRequired");
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
        return new(true, "RebaseSourceReady", Hash(new { Proposal = proposal.SourceVersion, Decision = decision,
            Source = sourceHash, Draft = draft, Nodes = nodes.OrderBy(x => x.ID), Codes = codes.OrderBy(x => x.ID) }));
    }

    // Activates analytical exposure only. It neither renews purchase admission nor alters source history.
    public async Task<ConstructionRebaseRequestResult> ActivateExposureAsync(BusinessContext c, int project,
        int proposalId, string expectedSourceVersion, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || expectedSourceVersion?.Length != 64 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidRebaseRequest");
        return await Execute(c, project, async () =>
        {
            var requestHash = Hash(new { project, proposalId, expectedSourceVersion, Reason = reason.Trim() });
            var prior = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (prior != null) return prior.ProjectId == project && prior.RequestHash == requestHash
                ? new(true, "AlreadyExposureRebased", prior.SourceVersion, prior.ID) : new(false, "IdempotencyConflict");
            var proposal = await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.ID == proposalId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
            if (proposal == null) return new(false, "RebaseRequestNotFound");
            if (await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().AnyAsync(x =>
                x.CompanyID == c.CompanyId && x.ReservationId == proposal.ReservationId, ct)) return new(false, "RebaseAlreadyActivated");
            var source = await ReadSourceReadinessLockedAsync(c, project, proposalId, ct);
            if (!source.Ok) return source;
            if (source.Version != expectedSourceVersion) return new(false, "ConcurrencyConflict");
            var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x =>
                x.ID == proposal.TargetBudgetVersionId && x.CompanyID == c.CompanyId && x.ProjectId == project && x.IsBaseline && x.Status == "Approved", ct);
            var exposure = await new ConstructionBudgetExposureService(db, access, audit).ReadLocked(c, project, baseline, null, ct,
                new ConstructionExposureRebaseCandidate(proposalId, expectedSourceVersion));
            if (!exposure.Ok) return new(false, exposure.Code);
            // A reviewed rebase is NOT an approved spending exception. Overrun rebasing remains
            // unsupported in every policy mode; never interpret this guard as a default company policy.
            if (exposure.Rows == null || exposure.Rows.Any(x => x.Remaining < 0)) return new(false, "RebaseOverrunRequiresReview");
            var fact = new ConstructionReservationExposureRebase { CompanyID = c.CompanyId, ProjectId = project,
                ReservationId = proposal.ReservationId, RebaseRequestId = proposalId, TargetBudgetVersionId = baseline.ID,
                SourceVersion = expectedSourceVersion, RequestId = requestId, RequestHash = requestHash,
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(fact); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionReservationExposureRebase), fact.ID,
                null, "RebaseReservationExposure", null, JsonSerializer.Serialize(new { proposalId, fact.ReservationId, baseline.ID, fact.SourceVersion, exposure.Version }),
                ConstructionChangeKind.Created, fact.Reason, c.EmployeeId, nameof(ConstructionReservationRebaseRequestService), requestId) });
            await db.SaveChangesAsync(ct);
            return new(true, "ExposureRebased", fact.SourceVersion, fact.ID);
        }, ct);
    }

    private async Task<ConstructionRebaseRequestResult> Execute(BusinessContext c, int project,
        Func<Task<ConstructionRebaseRequestResult>> command, CancellationToken ct)
    {
        if (!await Allowed(c, project, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct)) return new(false, "ConcurrencyConflict");
            var result = await command();
            if (result.Ok) await tx.CommitAsync(ct);
            else { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); }
            return result;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}

