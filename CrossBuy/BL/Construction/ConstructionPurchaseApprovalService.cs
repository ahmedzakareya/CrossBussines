using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using CrossBuy.ViewModel;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionPurchaseApprovalResult(bool Ok, string Code, int? ApprovalId = null, int? OrderId = null);
public sealed record ConstructionApprovalMoney(string CurrencyCode, int Decimals);
// A locked review quote only. No caller may use it as a purchase writer grant.
public sealed record ConstructionRebasedAdmissionReview(bool Ok, string Code, string? Version = null,
    int? ReservationId = null, int? TargetBudgetVersionId = null, decimal? Commitment = null)
{
    public bool GrantsCurrentAdmission => false;
}
public interface IConstructionPurchaseApprovalService
{
    Task<ConstructionPurchaseRequestPage?> ReadRequestAsync(int projectId, int orderId, CancellationToken ct = default);
    Task<IReadOnlyList<ConstructionPurchaseRequestLink>> RequestLinksAsync(IReadOnlyCollection<int> orderIds, CancellationToken ct = default);
    Task<bool> CanReviewAsync(BusinessContext candidate, int approvalId, CancellationToken ct = default);
    Task<ConstructionPurchaseApprovalResult> RequestAsync(int projectId, int orderId, int allocationId, string sourceVersion,
        int? exceptionId, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionPurchaseApprovalResult> ApproveAsync(int approvalId, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionPurchaseApprovalResult> RejectAsync(int approvalId, string reason, CancellationToken ct = default);
    Task<ConstructionRebasedAdmissionReview> PreviewRebasedRequestWithWarehouseConsentAsync(int projectId, int orderId,
        int consentDecisionRowId, CancellationToken ct = default);
    Task<ConstructionPurchaseApprovalResult> RequestRebasedWithWarehouseConsentAsync(int projectId, int orderId,
        int consentDecisionRowId, string expectedReviewVersion, Guid requestId, string reason, CancellationToken ct = default);
}

// Explicit manual approval of a persisted construction proposal. Legacy inventory threshold routing is unchanged.
public sealed class ConstructionPurchaseApprovalService(CrossDbContext db, IBusinessContextAccessor context,
    IEnumerable<IModuleAccessService> modules, IProjectsAccessService projects, IConstructionAuditService audit,
    IConstructionBudgetPolicyService policy, IBusinessEventService events, IConstructionWarehouseAccess? warehouseAccess = null) : IConstructionPurchaseApprovalService
{
    public const string DocumentType = "ConstructionPurchaseOrder";
    private ConstructionCommitmentReservationService Kernel => new(db, projects, audit, policy);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static bool Valid(Guid id, string reason) => id != Guid.Empty && !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 1000;
    internal static string ApprovedVersion(string content) => Hash(new { Kind = "ConstructionPurchaseAdmission/v1", Status = "Approved", Content = content });
    internal static string RebasedReviewVersion(string requestVersion, int reviewer, int approvalId, int reservationId, int targetId) =>
        Hash(new { Kind = "ConstructionPurchaseRebaseDecision/v1", RequestVersion = requestVersion, Reviewer = reviewer, approvalId, reservationId, targetId });
    public static Guid DecisionRequestId(int company, int approval) => new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { company, approval, Operation = "ApproveConstructionPurchase" }))[..16]);

    private async Task<BusinessContext?> Actor(string action, CancellationToken ct)
    {
        var c = await context.TryGetCurrentAsync(ct);
        if (c == null || !c.IsAuthenticated || c.IsExternalPortal || c.EmployeeId is not > 0 || c.CompanyId <= 0 || db.CompanyScope.FilterCompanyId != c.CompanyId) return null;
        var inventory = modules.FirstOrDefault(x => x.Scope == EntityRegistry.ScopeInventory);
        return inventory != null && await inventory.CanAsync(c, action, new PermissionTarget { CompanyId = c.CompanyId }, ct) ? c : null;
    }
    private async Task<bool> ProjectAllowed(BusinessContext c, int project, CancellationToken ct) =>
        await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    // The inbox also runs for worker-resolved candidates; never substitute the worker's current identity.
    public async Task<bool> CanReviewAsync(BusinessContext candidate, int approvalId, CancellationToken ct = default)
    {
        if (!candidate.IsAuthenticated || candidate.IsExternalPortal || candidate.EmployeeId is not > 0 ||
            candidate.CompanyId <= 0 || db.CompanyScope.FilterCompanyId != candidate.CompanyId) return false;
        var inventory = modules.FirstOrDefault(x => x.Scope == EntityRegistry.ScopeInventory);
        if (inventory == null || !await inventory.CanAsync(candidate, "manage", new PermissionTarget { CompanyId = candidate.CompanyId }, ct)) return false;
        var request = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyID == candidate.CompanyId && x.ApprovalId == approvalId, ct);
        if (request == null || request.CreatedBy == candidate.EmployeeId || !await ProjectAllowed(candidate, request.ProjectId, ct)) return false;
        return await db.Set<ConstructionPurchaseDraft>().AsNoTracking().AnyAsync(x => x.ID == request.DraftId &&
            x.CompanyID == candidate.CompanyId && x.CreatedBy != candidate.EmployeeId, ct);
    }

    private async Task<ConstructionPurchaseApprovalResult> Unit(Func<Task<ConstructionPurchaseApprovalResult>> work, CancellationToken ct)
    {
        if (db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        async Task<ConstructionPurchaseApprovalResult> Run() => await ConstructionAdmissionUnit.RunAsync(db, work, static x => x.Ok, static code => new ConstructionPurchaseApprovalResult(false, code), ct);
        if (db.Database.CurrentTransaction != null) return await Run(); // The owner receives exceptions and keeps commit responsibility.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await Run(); if (result.Ok) await tx.CommitAsync(ct); return result;
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (ex is not AggregateException && Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<PurchaseOrder?> Source(BusinessContext c, int project, int order, CancellationToken ct)
    {
        var query = db.Database.IsSqlServer()
            ? db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM dbo.PurchaseOrders WITH (UPDLOCK,HOLDLOCK) WHERE ID={order} AND CompanyID={c.CompanyId} AND ProjectId={project}")
            : db.PurchaseOrders.Where(x => x.ID == order && x.CompanyID == c.CompanyId && x.ProjectId == project);
        var source = await query.AsNoTracking().SingleOrDefaultAsync(ct);
        if (source != null) source.Lines = await db.PurchaseOrderLines.AsNoTracking().Where(x => x.PurchaseOrderId == source.ID).OrderBy(x => x.ID).ToListAsync(ct);
        return source;
    }

    private async Task<bool> CurrencyCurrent(BusinessContext c, PurchaseOrder source, ConstructionPurchaseDraft draft, CancellationToken ct) =>
        source.CurrencyId == draft.CurrencyId && source.ExchangeRate == 1 &&
        await db.Companies.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.DefaultCurrencyId == draft.CurrencyId, ct) &&
        await db.Currencies.AsNoTracking().AnyAsync(x => x.ID == draft.CurrencyId && x.DecimalPlaces == draft.CurrencyDecimals, ct);

    // Private continuation foundation: not on the public interface or wired to a UI endpoint.
    // The future command must repeat this read under its own transaction, then prove the
    // canonical independent purchase approval and persist immutable readmission evidence.
    public Task<ConstructionRebasedAdmissionReview> PreviewRebasedAdmissionAsync(int projectId, int orderId,
        CancellationToken ct = default) => PreviewRebased(projectId, orderId, "manage", true, ct);

    public Task<ConstructionRebasedAdmissionReview> PreviewRebasedRequestAsync(int projectId, int orderId,
        CancellationToken ct = default) => PreviewRebased(projectId, orderId, "purchase", false, ct);

    public Task<ConstructionRebasedAdmissionReview> PreviewRebasedRequestWithWarehouseConsentAsync(int projectId, int orderId,
        int consentDecisionRowId, CancellationToken ct = default) =>
        PreviewRebased(projectId, orderId, "purchase", false, ct, consentDecisionRowId);

    private async Task<ConstructionRebasedAdmissionReview> PreviewRebased(int projectId, int orderId,
        string action, bool independent, CancellationToken ct, int? consentDecisionRowId = null)
    {
        var c = await Actor(action, ct);
        if (c == null || db.CompanyScope.AllowsCrossCompany || !await ProjectAllowed(c, projectId, ct))
            return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges())
            return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var actor = await Actor(action, ct);
            if (actor?.EmployeeId != c.EmployeeId || actor?.CompanyId != c.CompanyId ||
                db.CompanyScope.AllowsCrossCompany || !await ProjectAllowed(c, projectId, ct))
                return new(false, "AccessDenied");
            var result = await ReadRebasedAdmissionLocked(c, projectId, orderId, ct, independent,
                consentDecisionRowId: consentDecisionRowId, consentPermissionAction: action);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
        catch (Exception ex) when (ex is not AggregateException && Conflict(ex))
        { return new(false, "ConcurrencyConflict"); }
    }

    private async Task<ConstructionRebasedAdmissionReview> ReadRebasedAdmissionLocked(BusinessContext c,
        int projectId, int orderId, CancellationToken ct, bool independent = true, int? witnessActor = null,
        int? consentDecisionRowId = null, string consentPermissionAction = "manage")
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A purchase review transaction is required");
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        if (draft == null) return new(false, "PersistedDraftRequired");
        if (independent && draft.CreatedBy == c.EmployeeId) return new(false, "IndependentApproverRequired");
        // Initial-purchase consent cannot authorize a different budget/readmission lineage.
        // Refuse before creating a pending approval that the canonical decision cannot consume.
        ConstructionRebasedWarehouseConsentConsumptionReview? consentReview = null;
        if (await ConstructionWarehouseExceptionRequirement.RequiredAsync(db, c.CompanyId, projectId, orderId, ct))
        {
            if (consentDecisionRowId is not > 0) return new(false, "WarehouseExceptionContinuationUnavailable");
            if (warehouseAccess == null) return new(false, "WarehouseExceptionContinuationUnavailable");
            consentReview = await new ConstructionRebasedWarehouseConsentService(db, context, projects, modules,
                warehouseAccess, audit).ReviewForPurchaseLockedAsync(c, projectId, orderId,
                consentDecisionRowId.Value, consentPermissionAction, ct);
            if (!consentReview.Ok) return new(false, consentReview.Code);
        }
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        if (reservation == null) return new(false, "ReservationRebaseRequired");
        var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ReservationId == reservation.ID, ct);
        if (map == null) return new(false, "ActivatedExposureRebaseRequired");
        var readiness = await new ConstructionReservationRebaseRequestService(db, projects, audit)
            .ReadSourceReadinessLockedAsync(c, projectId, map.RebaseRequestId, ct);
        if (!readiness.Ok) return new(false, readiness.Code);
        if (readiness.Version != map.SourceVersion) return new(false, "ConcurrencyConflict");
        var source = await Source(c, projectId, orderId, ct);
        if (source == null || string.IsNullOrWhiteSpace(source.OrderNo)) return new(false, "DraftSourceUnproven");
        if (!await CurrencyCurrent(c, source, draft, ct)) return new(false, "DraftCurrencyChanged");
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ID == map.TargetBudgetVersionId &&
            x.Status == "Approved" && x.IsBaseline, ct);
        if (baseline == null || baseline.ID == reservation.BudgetVersionId) return new(false, "RebaseBaselineLineageRequired");
        // Do not exclude this order or add its amount again: the activated map already counts it.
        var exposure = await new ConstructionBudgetExposureService(db, projects, audit)
            .ReadLocked(c, projectId, baseline, null, ct);
        if (!exposure.Ok) return new(false, exposure.Code);
        if (exposure.Rows == null || exposure.Rows.Any(x => x.Remaining < 0))
            return new(false, "RebaseOverrunRequiresReview");
        var version = Hash(new { Kind = "ConstructionRebasedAdmissionReview/v1", Company = c.CompanyId,
            Actor = witnessActor ?? c.EmployeeId, projectId, orderId, Draft = draft, Reservation = reservation,
            ExposureRebase = map, Source = readiness.Version, Exposure = exposure.Version,
            WarehouseConsent = consentReview?.Version });
        return new(true, "RebasedAdmissionReviewReady", version, reservation.ID, baseline.ID, reservation.NetDocumentAmount);
    }

    // Private request writer. The independent decision/realization continuation remains gated.
    public async Task<ConstructionPurchaseApprovalResult> RequestRebasedAsync(int projectId, int orderId,
        string expectedReviewVersion, Guid requestId, string reason, CancellationToken ct = default)
        => await RequestRebasedCoreAsync(projectId, orderId, expectedReviewVersion, requestId, reason, null, ct);

    public async Task<ConstructionPurchaseApprovalResult> RequestRebasedWithWarehouseConsentAsync(int projectId, int orderId,
        int consentDecisionRowId, string expectedReviewVersion, Guid requestId, string reason, CancellationToken ct = default)
        => await RequestRebasedCoreAsync(projectId, orderId, expectedReviewVersion, requestId, reason, consentDecisionRowId, ct);

    private async Task<ConstructionPurchaseApprovalResult> RequestRebasedCoreAsync(int projectId, int orderId,
        string expectedReviewVersion, Guid requestId, string reason, int? consentDecisionRowId, CancellationToken ct)
    {
        var c = await Actor("purchase", ct);
        if (c == null || db.CompanyScope.AllowsCrossCompany || !await ProjectAllowed(c, projectId, ct))
            return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || expectedReviewVersion?.Length != 64) return new(false, "InvalidApprovalRequest");
        var hash = Hash(new { Kind = "ConstructionPurchaseRebaseRequest/v1", projectId, orderId,
            expectedReviewVersion, Actor = c.EmployeeId, Reason = reason.Trim() });
        return await Unit(async () =>
        {
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var actor = await Actor("purchase", ct);
            if (actor?.EmployeeId != c.EmployeeId || actor?.CompanyId != c.CompanyId ||
                db.CompanyScope.AllowsCrossCompany || !await ProjectAllowed(c, projectId, ct)) return new(false, "AccessDenied");
            var replay = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (replay != null)
                return replay.RequestHash == hash && await db.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking()
                    .AnyAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ApprovalRequestId == replay.ID, ct)
                    ? new(true, "AlreadyRequested", replay.ApprovalId, replay.PurchaseOrderId) : new(false, "IdempotencyConflict");
            var review = await ReadRebasedAdmissionLocked(c, projectId, orderId, ct, false,
                consentDecisionRowId: consentDecisionRowId, consentPermissionAction: "purchase");
            if (!review.Ok) return new(false, review.Code);
            if (review.Version != expectedReviewVersion) return new(false, "ConcurrencyConflict");
            var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ID == review.ReservationId, ct);
            if (reservation.DecisionCode is not ("WithinBudget" or "BudgetWarning")) return new(false, "RebaseExceptionContinuationUnsupported");
            if (await (from r in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                join a in db.InventoryApprovals.AsNoTracking() on r.ApprovalId equals a.ID
                where r.CompanyID == c.CompanyId && r.PurchaseOrderId == orderId && a.CompanyID == c.CompanyId && a.Status == "Pending"
                select r.ID).AnyAsync(ct)) return new(false, "ApprovalAlreadyPending");
            var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ReservationId == reservation.ID, ct);
            var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
            var source = await Source(c, projectId, orderId, ct);
            var payload = JsonSerializer.Serialize(new { Kind = "ConstructionRebasedPurchaseOrder/v1", projectId,
                orderId, DraftId = draft.ID, reservation.AllocationId, reservation.SourceVersion,
                ExposureRebaseId = map.ID, ReviewVersion = expectedReviewVersion });
            var now = DateTime.UtcNow;
            var approval = new InventoryApproval { CompanyID = c.CompanyId, DocType = DocumentType, Amount = source!.GrandTotal,
                PayloadJson = payload, Status = "Pending", RequestedByEmployeeId = c.EmployeeId, RequestedAt = now };
            db.Add(approval); await db.SaveChangesAsync(ct);
            var request = new ConstructionPurchaseApprovalRequest { CompanyID = c.CompanyId, ProjectId = projectId,
                PurchaseOrderId = orderId, DraftId = draft.ID, AllocationId = reservation.AllocationId, ApprovalId = approval.ID,
                RequestId = requestId, RequestHash = hash, SourceVersion = reservation.SourceVersion,
                ContentHash = draft.InitialSourceVersion, PayloadJson = payload, Reason = reason.Trim(),
                CreatedBy = c.EmployeeId!.Value, CreatedAt = now };
            db.Add(request); await db.SaveChangesAsync(ct);
            db.Add(new ConstructionPurchaseRebaseRequest { CompanyID = c.CompanyId, ProjectId = projectId,
                PurchaseOrderId = orderId, ApprovalRequestId = request.ID, ExposureRebaseId = map.ID,
                ReviewVersion = expectedReviewVersion, CreatedBy = c.EmployeeId.Value, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            if (consentDecisionRowId is > 0)
            {
                if (warehouseAccess == null) return new(false, "WarehouseExceptionContinuationUnavailable");
                var consent = await new ConstructionRebasedWarehouseConsentService(db, context, projects, modules,
                    warehouseAccess, audit).ReviewForPurchaseLockedAsync(c, projectId, orderId,
                    consentDecisionRowId.Value, "purchase", ct);
                if (!consent.Ok || consent.ConsentRequestRowId is not > 0 || consent.ConsentDecisionRowId is not > 0 ||
                    consent.ExposureRebaseId != map.ID || consent.DecisionActor == c.EmployeeId)
                    return new(false, consent.Ok ? "IndependentWarehouseConsentRequired" : consent.Code);
                db.Add(new ConstructionRebasedWarehouseConsentBinding { CompanyID = c.CompanyId, ProjectId = projectId,
                    PurchaseOrderId = orderId, ExposureRebaseId = map.ID, RebaseRequestId =
                        await db.Set<ConstructionPurchaseRebaseRequest>().Where(x => x.ApprovalRequestId == request.ID)
                            .Select(x => x.ID).SingleAsync(ct), ApprovalRequestId = request.ID,
                    ConsentRequestRowId = consent.ConsentRequestRowId.Value, ConsentDecisionRowId = consent.ConsentDecisionRowId.Value,
                    DestinationSiteId = consent.DestinationSiteId!.Value, SourceWarehouseId = consent.SourceWarehouseId!.Value,
                    SourceFactsVersion = consent.SourceFactsVersion!, MappingVersion = consent.MappingVersion!,
                    PolicyToken = consent.PolicyToken!, ReviewVersion = consent.Version!, CreatedBy = c.EmployeeId.Value, CreatedAt = now });
                await db.SaveChangesAsync(ct);
            }
            Record(c, projectId, request.ID, "RequestRebasedConstructionPurchaseApproval",
                new { approval.ID, orderId, ExposureRebaseId = map.ID, ReviewVersion = expectedReviewVersion }, reason, requestId);
            await db.SaveChangesAsync(ct); await Emit(approval, InventoryApprovalEvents.Requested, ct);
            return new(true, "Requested", approval.ID, orderId);
        }, ct);
    }

    public async Task<IReadOnlyList<ConstructionPurchaseRequestLink>> RequestLinksAsync(IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
    {
        var c = await Actor("purchase", ct);
        if (c == null || orderIds.Count == 0 || orderIds.Count > 200) return Array.Empty<ConstructionPurchaseRequestLink>();
        var ids = orderIds.Distinct().ToArray();
        var rows = await db.Set<ConstructionPurchaseDraft>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && ids.Contains(x.PurchaseOrderId))
            .Select(x => new ConstructionPurchaseRequestLink(x.PurchaseOrderId, x.ProjectId)).ToListAsync(ct);
        var allowed = new HashSet<int>();
        foreach (var project in rows.Select(x => x.ProjectId).Distinct()) if (await ProjectAllowed(c, project, ct)) allowed.Add(project);
        return rows.Where(x => allowed.Contains(x.ProjectId)).ToArray();
    }

    // A coherent read witness only. The POST revalidates every fact and never trusts displayed totals or eligibility.
    public async Task<ConstructionPurchaseRequestPage?> ReadRequestAsync(int projectId, int orderId, CancellationToken ct = default)
    {
        var c = await Actor("purchase", ct);
        if (c == null || !await ProjectAllowed(c, projectId, ct)) return null;
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return null;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await Kernel.ProjectLock(c, projectId, ct)) return null;
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        var source = await Source(c, projectId, orderId, ct);
        if (draft == null || source == null) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct);
        var currency = await db.Currencies.AsNoTracking().SingleOrDefaultAsync(x => x.ID == draft.CurrencyId, ct);
        var allocation = await db.Set<ConstructionOrderAllocation>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId && x.IsSealed)
            .OrderByDescending(x => x.RevisionNo).FirstOrDefaultAsync(ct);
        var history = await (from request in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                             join approval in db.InventoryApprovals.AsNoTracking() on request.ApprovalId equals approval.ID
                             where request.CompanyID == c.CompanyId && request.ProjectId == projectId && request.PurchaseOrderId == orderId && approval.CompanyID == c.CompanyId
                             orderby request.ID descending
                             select new ConstructionPurchaseRequestHistory(approval.ID, request.CreatedAt, approval.Status, request.Reason, approval.DecisionNote)).ToListAsync(ct);
        var code = source.Status != "Draft" ? "SourceAlreadyProcessed"
            : CreationChanged() ? "DraftSourceChanged"
            : !await CurrencyCurrent(c, source, draft, ct) || draft.CurrencyDecimals is < 0 or > 8 ? "DraftCurrencyChanged"
            : allocation == null ? "AllocationNotFound" : "Ready";
        bool CreationChanged() => ConstructionPurchaseDraftService.CreationHash(source) != draft.InitialSourceVersion;
        var exceptions = new List<ConstructionPurchaseExceptionOption>();
        var splits = new List<ConstructionPurchaseRequestSplit>();
        var canSubmit = false;
        if (code == "Ready")
        {
            var candidate = await Kernel.PrepareLockedAsync(c, projectId, allocation!.ID, allocation.SourceVersion, ct);
            if (candidate.Ready && candidate.Baseline!.ID != draft.BudgetVersionId) candidate = candidate with { Ready = false, Code = "AllocationSourceChanged" };
            code = candidate.Code;
            if (candidate.Ready)
            {
                splits = await (from line in db.Set<ConstructionOrderAllocationLine>().AsNoTracking()
                                join wbs in db.Set<WbsNode>().AsNoTracking() on line.WbsNodeId equals wbs.ID
                                join cost in db.Set<ConstructionCostCode>().AsNoTracking() on line.CostCodeId equals cost.ID
                                where line.CompanyID == c.CompanyId && line.ProjectId == projectId && line.AllocationId == allocation.ID &&
                                    wbs.CompanyID == c.CompanyId && wbs.ProjectId == projectId && cost.CompanyID == c.CompanyId
                                orderby wbs.Code, cost.Code
                                select new ConstructionPurchaseRequestSplit(wbs.Code, wbs.Name, wbs.NameEn, cost.Code, cost.Name, cost.NameEn, line.NetDocumentAmount)).ToListAsync(ct);
                if (candidate.RequiresApproval)
                {
                    exceptions = await (from request in db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking()
                                        join decision in db.Set<ConstructionBudgetExceptionDecision>().AsNoTracking() on request.ID equals decision.ExceptionRequestId
                                        where request.CompanyID == c.CompanyId && request.ProjectId == projectId && request.AllocationId == allocation.ID &&
                                            request.AdmissionVersion == candidate.Version && decision.CompanyID == c.CompanyId && decision.Outcome == "Approved" &&
                                            !db.Set<ConstructionBudgetExceptionConsumption>().Any(x => x.CompanyID == c.CompanyId && x.ExceptionRequestId == request.ID)
                                        orderby request.ID descending
                                        select new ConstructionPurchaseExceptionOption(request.ID, request.Overrun, request.Reason)).ToListAsync(ct);
                    if (exceptions.Count == 0) code = "MatchingBudgetExceptionRequired";
                }
                canSubmit = candidate.Allowed || candidate.RequiresApproval && exceptions.Count > 0;
            }
        }
        if (history.Any(x => x.Status == "Pending")) { code = "ApprovalAlreadyPending"; canSubmit = false; }
        var page = new ConstructionPurchaseRequestPage(projectId, orderId, source.OrderNo, project.Code, project.Name, project.NameEn,
            source.Status, currency?.Code ?? "", draft.CurrencyDecimals, source.SubTotal, source.TaxTotal, source.GrandTotal, code, canSubmit,
            allocation?.ID, allocation?.SourceVersion, Guid.NewGuid(), source.Lines.OrderBy(x => x.ID)
                .Select(x => new ConstructionPurchaseRequestLine(x.ItemDescription, x.Qty, x.LineTotal)).ToArray(), splits, exceptions, history);
        await tx.CommitAsync(ct); return page;
    }

    public Task<ConstructionPurchaseApprovalResult> RequestAsync(int projectId, int orderId, int allocationId, string sourceVersion,
        int? exceptionId, Guid requestId, string reason, CancellationToken ct = default) => RequestCore(projectId, orderId, allocationId, sourceVersion, exceptionId, requestId, reason, null, ct);

    // Private programme continuation; no UI endpoint yet. The outer unit owns request, binding, audit and outbox.
    public async Task<ConstructionPurchaseApprovalResult> RequestWithWarehouseExceptionAsync(int projectId,int orderId,int allocationId,string sourceVersion,
        int? exceptionId,int warehouseDecisionId,Guid requestId,string reason,CancellationToken ct=default)
    {
        var c=await Actor("purchase",ct);
        if(c==null||warehouseAccess==null||db.CompanyScope.AllowsCrossCompany||!await ProjectAllowed(c,projectId,ct))return new(false,"AccessDenied");
        if(!Valid(requestId,reason)||warehouseDecisionId<=0||sourceVersion?.Length!=64)return new(false,"InvalidApprovalRequest");
        var hash=Hash(new{projectId,orderId,allocationId,sourceVersion,exceptionId,warehouseDecisionId,Actor=c.EmployeeId,c.UserId,Reason=reason.Trim()});
        return await Unit(async()=>{
            if(!await Kernel.ProjectLock(c,projectId,ct))return new(false,"ConcurrencyConflict");
            var freshActor=await Actor("purchase",ct); if(freshActor?.EmployeeId!=c.EmployeeId||freshActor?.CompanyId!=c.CompanyId||freshActor?.UserId!=c.UserId||!await ProjectAllowed(c,projectId,ct))return new(false,"AccessDenied");
            var prior=await db.Set<ConstructionWarehouseExceptionBinding>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.RequestId==requestId,ct);
            if(prior!=null){
                var saved=await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==prior.ApprovalRequestId&&x.CompanyID==c.CompanyId,ct);
                return prior.RequestHash==hash&&saved!=null?new(true,"AlreadyRequested",saved.ApprovalId,saved.PurchaseOrderId):new(false,"IdempotencyConflict");
            }
            var decision=await db.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==warehouseDecisionId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.Outcome=="Approved",ct);
            if(decision==null)return new(false,"ApprovedWarehouseExceptionRequired");
            var intent=await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==decision.ExceptionRequestId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.PurchaseOrderId==orderId,ct);
            if(intent==null||intent.CreatedBy==decision.CreatedBy||intent.SourceFactsVersion!=decision.SourceFactsVersion)return new(false,"WarehouseExceptionSourceMismatch");
            var quote=await new ConstructionWarehouseExceptionRequests(db,context,projects,modules,warehouseAccess,audit).Quote(c,projectId,orderId,intent.DestinationSiteId,ct);
            if(!quote.Ok)return new(false,quote.Code);
            if(quote.FactsVersion!=intent.SourceFactsVersion)return new(false,"ConcurrencyConflict");
            var submitted=await RequestCore(projectId,orderId,allocationId,sourceVersion,exceptionId,requestId,reason,warehouseDecisionId,ct);
            if(!submitted.Ok)return submitted;
            var approval=await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.RequestId==requestId,ct);
            var binding=new ConstructionWarehouseExceptionBinding{CompanyID=c.CompanyId,ProjectId=projectId,PurchaseOrderId=orderId,ApprovalRequestId=approval.ID,DecisionId=decision.ID,SourceFactsVersion=intent.SourceFactsVersion,RequestId=requestId,RequestHash=hash,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
            db.Add(binding);await db.SaveChangesAsync(ct);
            Record(c,projectId,approval.ID,"BindWarehouseException",new{binding.ID,binding.DecisionId,binding.SourceFactsVersion},reason,requestId);await db.SaveChangesAsync(ct);
            return submitted;
        },ct);
    }

    private async Task<ConstructionPurchaseApprovalResult> RequestCore(int projectId, int orderId, int allocationId, string sourceVersion,
        int? exceptionId, Guid requestId, string reason, int? warehouseDecisionId, CancellationToken ct)
    {
        var c = await Actor("purchase", ct);
        if (c == null || !await ProjectAllowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || sourceVersion?.Length != 64) return new(false, "InvalidApprovalRequest");
        var hash = Hash(new { projectId, orderId, allocationId, sourceVersion, exceptionId, Actor = c.EmployeeId, Reason = reason.Trim() });
        return await Unit(async () =>
        {
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var actor = await Actor("purchase", ct);
            if (actor?.EmployeeId != c.EmployeeId || actor?.CompanyId != c.CompanyId || !await ProjectAllowed(c, projectId, ct)) return new(false, "AccessDenied");
            var replay = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if(warehouseDecisionId==null&&await ConstructionWarehouseExceptionRequirement.RequiredAsync(db,c.CompanyId,projectId,orderId,ct))return new(false,"WarehouseExceptionBindingRequired");
            if (replay != null) return replay.RequestHash == hash ? new(true, "AlreadyRequested", replay.ApprovalId, replay.PurchaseOrderId) : new(false, "IdempotencyConflict");
            var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
            var source = await Source(c, projectId, orderId, ct);
            if (draft == null || source?.Status != "Draft") return new(false, "PersistedDraftRequired");
            var content = ConstructionPurchaseDraftService.CreationHash(source);
            if (content != draft.InitialSourceVersion) return new(false, "DraftSourceChanged");
            if (!await CurrencyCurrent(c, source, draft, ct)) return new(false, "DraftCurrencyChanged");
            var candidate = await Kernel.PrepareLockedAsync(c, projectId, allocationId, sourceVersion, ct);
            if (!candidate.Ready) return new(false, candidate.Code);
            if (candidate.Proposal!.PurchaseOrderId != orderId || candidate.Baseline!.ID != draft.BudgetVersionId) return new(false, "AllocationSourceChanged");
            if (candidate.RequiresApproval)
            {
                if (!exceptionId.HasValue || !await db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().AnyAsync(x => x.ID == exceptionId && x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.AllocationId == allocationId && x.AdmissionVersion == candidate.Version, ct))
                    return new(false, "MatchingBudgetExceptionRequired");
            }
            else if (exceptionId.HasValue) return new(false, "BudgetExceptionNotRequired");
            var pending = await (from r in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                                 join a in db.InventoryApprovals.AsNoTracking() on r.ApprovalId equals a.ID
                                 where r.CompanyID == c.CompanyId && r.PurchaseOrderId == orderId && a.CompanyID == c.CompanyId && a.Status == "Pending"
                                 select r.ID).AnyAsync(ct);
            if (pending) return new(false, "ApprovalAlreadyPending");
            var payload = JsonSerializer.Serialize(new { Kind = "ConstructionPurchaseOrder/v1", projectId, orderId, DraftId = draft.ID, allocationId, sourceVersion, exceptionId });
            var now = DateTime.UtcNow;
            var approval = new InventoryApproval { CompanyID = c.CompanyId, DocType = DocumentType, Amount = source.GrandTotal,
                PayloadJson = payload, Status = "Pending", RequestedByEmployeeId = c.EmployeeId, RequestedAt = now };
            db.Add(approval); await db.SaveChangesAsync(ct);
            var request = new ConstructionPurchaseApprovalRequest { CompanyID = c.CompanyId, ProjectId = projectId, PurchaseOrderId = orderId,
                DraftId = draft.ID, AllocationId = allocationId, ApprovalId = approval.ID, ExceptionId = exceptionId, RequestId = requestId,
                RequestHash = hash, SourceVersion = sourceVersion, ContentHash = content, PayloadJson = payload, Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = now };
            db.Add(request); await db.SaveChangesAsync(ct);
            Record(c, projectId, request.ID, "RequestConstructionPurchaseApproval", new { approval.ID, orderId, sourceVersion, content }, reason, requestId);
            await db.SaveChangesAsync(ct); await Emit(approval, InventoryApprovalEvents.Requested, ct);
            return new(true, "Requested", approval.ID, orderId);
        }, ct);
    }

    public Task<ConstructionPurchaseApprovalResult> ApproveAsync(int approvalId, Guid requestId, string reason, CancellationToken ct = default) => Decide(approvalId, requestId, reason, true, ct);
    public Task<ConstructionPurchaseApprovalResult> RejectAsync(int approvalId, string reason, CancellationToken ct = default) => Decide(approvalId, Guid.NewGuid(), reason, false, ct);

    private async Task<ConstructionPurchaseApprovalResult> Decide(int approvalId, Guid requestId, string reason, bool approve, CancellationToken ct)
    {
        var c = await Actor("manage", ct);
        if (c == null) return new(false, "AccessDenied");
        if ((approve && requestId == Guid.Empty) || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return new(false, "InvalidApprovalDecision");
        var request = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ApprovalId == approvalId, ct);
        if (request == null || !await ProjectAllowed(c, request.ProjectId, ct)) return new(false, "AccessDenied");
        var hash = Hash(new { approvalId, Actor = c.EmployeeId, Reason = reason.Trim() });
        return await Unit(async () =>
        {
            if (!await Kernel.ProjectLock(c, request.ProjectId, ct)) return new(false, "ConcurrencyConflict");
            var actor = await Actor("manage", ct);
            if (actor?.EmployeeId != c.EmployeeId || actor?.CompanyId != c.CompanyId || !await ProjectAllowed(c, request.ProjectId, ct)) return new(false, "AccessDenied");
            var approval = await db.InventoryApprovals.AsNoTracking().SingleOrDefaultAsync(x => x.ID == approvalId && x.CompanyID == c.CompanyId, ct);
            var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleAsync(x => x.ID == request.DraftId && x.CompanyID == c.CompanyId, ct);
            if (request.CreatedBy == c.EmployeeId || draft.CreatedBy == c.EmployeeId) return new(false, "IndependentApproverRequired");
            if (approval == null || approval.DocType != DocumentType || approval.RequestedByEmployeeId != request.CreatedBy || !string.Equals(approval.PayloadJson, request.PayloadJson, StringComparison.Ordinal)) return new(false, "ApprovalSnapshotChanged");
            if (approve)
            {
                var warehouseBinding=await db.Set<ConstructionWarehouseExceptionBinding>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==request.ProjectId&&x.PurchaseOrderId==request.PurchaseOrderId&&x.ApprovalRequestId==request.ID,ct);
                var rebasedWarehouseBinding=await db.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==request.ProjectId&&x.PurchaseOrderId==request.PurchaseOrderId&&x.ApprovalRequestId==request.ID,ct);
                var hasWarehouseIntent=await ConstructionWarehouseExceptionRequirement.RequiredAsync(db,c.CompanyId,request.ProjectId,request.PurchaseOrderId,ct);
                if(hasWarehouseIntent&&warehouseBinding==null&&rebasedWarehouseBinding==null)return new(false,"WarehouseExceptionBindingRequired");
                var replay = await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
                if(replay!=null&&warehouseBinding!=null&&!await db.Set<ConstructionWarehouseExceptionApplication>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.BindingId==warehouseBinding.ID&&x.AdmissionId==replay.ID&&x.RequestId==requestId,ct))return new(false,"WarehouseExceptionApplicationRequired");
                if(replay!=null&&rebasedWarehouseBinding!=null&&!await db.Set<ConstructionRebasedWarehouseConsentApplication>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.BindingId==rebasedWarehouseBinding.ID&&x.AdmissionId==replay.ID&&x.RequestId==requestId,ct))return new(false,"RebasedWarehouseConsentApplicationRequired");
                if (replay != null) return replay.ApprovalRequestId == request.ID && replay.RequestHash == hash && approval.Status == "Approved"
                    ? new(true, "AlreadyApproved", approvalId, request.PurchaseOrderId) : new(false, "IdempotencyConflict");
            }
            else if (approval.Status == "Rejected" && approval.DecidedByEmployeeId == c.EmployeeId && approval.DecisionNote == reason.Trim()) return new(true, "AlreadyRejected", approvalId, request.PurchaseOrderId);
            if (approval.Status != "Pending") return new(false, "ApprovalAlreadyDecided");
            string? number = null;
            if (approve)
            {
                var rebaseRequest = await db.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.CompanyID == c.CompanyId && x.ProjectId == request.ProjectId && x.ApprovalRequestId == request.ID, ct);
                var source = await Source(c, request.ProjectId, request.PurchaseOrderId, ct);
                if (source?.Status != "Draft" || ConstructionPurchaseDraftService.CreationHash(source) != request.ContentHash || request.ContentHash != draft.InitialSourceVersion || source.GrandTotal != approval.Amount) return new(false, "DraftSourceChanged");
                if (!await CurrencyCurrent(c, source, draft, ct)) return new(false, "DraftCurrencyChanged");
                var binding=await db.Set<ConstructionWarehouseExceptionBinding>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==request.ProjectId&&x.PurchaseOrderId==source.ID&&x.ApprovalRequestId==request.ID,ct);
                ConstructionWarehouseConsumptionReview? warehouseReview=null;
                if(binding!=null){
                    if(warehouseAccess==null||rebaseRequest!=null)return new(false,"WarehouseExceptionContinuationUnavailable");
                    warehouseReview=await new ConstructionWarehouseExceptionConsumptionReview(db,context,projects,modules,warehouseAccess,audit).ReadLockedAsync(c,request.ProjectId,source.ID,binding.DecisionId,request.ID,ct);
                    if(!warehouseReview.Ok)return new(false,warehouseReview.Code);
                }
                int? reservationId;
                ConstructionRebasedWarehouseConsentConsumptionReview? rebasedConsentReview = null;
                if (rebaseRequest != null)
                {
                    if (db.CompanyScope.AllowsCrossCompany || request.ExceptionId != null) return new(false, "AccessDenied");
                    var consentBinding = await db.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().SingleOrDefaultAsync(x =>
                        x.CompanyID == c.CompanyId && x.ProjectId == request.ProjectId && x.PurchaseOrderId == request.PurchaseOrderId &&
                        x.ApprovalRequestId == request.ID && x.RebaseRequestId == rebaseRequest.ID, ct);
                    var review = await ReadRebasedAdmissionLocked(c, request.ProjectId, request.PurchaseOrderId, ct, true,
                        request.CreatedBy, consentBinding?.ConsentDecisionRowId, "manage");
                    if (!review.Ok) return new(false, review.Code);
                    if (review.Version != rebaseRequest.ReviewVersion) return new(false, "ConcurrencyConflict");
                    var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
                        x.ID == rebaseRequest.ExposureRebaseId && x.CompanyID == c.CompanyId && x.ProjectId == request.ProjectId &&
                        x.ReservationId == review.ReservationId && x.TargetBudgetVersionId == review.TargetBudgetVersionId, ct);
                    if (map == null) return new(false, "AdmissionSourceChanged");
                    if (consentBinding != null)
                    {
                        if (warehouseAccess == null || consentBinding.CreatedBy != request.CreatedBy ||
                            consentBinding.ExposureRebaseId != map.ID)
                            return new(false, "RebasedWarehouseConsentBindingChanged");
                        rebasedConsentReview = await new ConstructionRebasedWarehouseConsentService(db, context, projects,
                            modules, warehouseAccess, audit).ReviewForPurchaseLockedAsync(c, request.ProjectId,
                            request.PurchaseOrderId, consentBinding.ConsentDecisionRowId, "manage", ct);
                        if (!rebasedConsentReview.Ok) return new(false, rebasedConsentReview.Code);
                        if (rebasedConsentReview.DecisionActor == c.EmployeeId || rebasedConsentReview.DecisionActor == request.CreatedBy)
                            return new(false, "IndependentWarehouseConsentRequired");
                        if (rebasedConsentReview.ConsentRequestRowId != consentBinding.ConsentRequestRowId ||
                            rebasedConsentReview.ExposureRebaseId != consentBinding.ExposureRebaseId ||
                            rebasedConsentReview.DestinationSiteId != consentBinding.DestinationSiteId ||
                            rebasedConsentReview.SourceWarehouseId != consentBinding.SourceWarehouseId ||
                            rebasedConsentReview.SourceFactsVersion != consentBinding.SourceFactsVersion ||
                            rebasedConsentReview.MappingVersion != consentBinding.MappingVersion ||
                            rebasedConsentReview.Version != consentBinding.ReviewVersion ||
                            rebasedConsentReview.PolicyToken == null || !rebasedConsentReview.PolicyToken.SequenceEqual(consentBinding.PolicyToken))
                            return new(false, "ConcurrencyConflict");
                    }
                    db.Add(new ConstructionPurchaseRebaseDecision { CompanyID = c.CompanyId, ProjectId = request.ProjectId,
                        PurchaseOrderId = source.ID, RebaseRequestId = rebaseRequest.ID, ReservationId = review.ReservationId!.Value,
                        TargetBudgetVersionId = review.TargetBudgetVersionId!.Value, RequestVersion = review.Version!, SourceVersion = map.SourceVersion,
                        ReviewVersion = RebasedReviewVersion(review.Version!, c.EmployeeId!.Value, approvalId, review.ReservationId.Value, review.TargetBudgetVersionId.Value),
                        RequestId = requestId, RequestHash = hash, Reason = reason.Trim(), CreatedBy = c.EmployeeId.Value, CreatedAt = DateTime.UtcNow });
                    await db.SaveChangesAsync(ct);
                    reservationId = review.ReservationId;
                }
                else
                {
                    var grant = request.ExceptionId.HasValue
                        ? await new ConstructionBudgetExceptionService(db, projects, audit, policy).ConsumeInTransactionAsync(c, request.ProjectId, request.ExceptionId.Value, requestId, reason, ct)
                        : await Kernel.ReserveInTransactionAsync(c, request.ProjectId, request.AllocationId, request.SourceVersion, requestId, reason, ct);
                    if (!grant.GrantsCurrentAdmission) return new(false, grant.Ok ? "FreshBudgetAdmissionRequired" : grant.Code);
                    reservationId = grant.Id;
                }
                if (!await db.Set<ConstructionCommitmentReservation>().AsNoTracking().AnyAsync(x => x.ID == reservationId && x.CompanyID == c.CompanyId && x.ProjectId == request.ProjectId && x.PurchaseOrderId == source.ID && x.AllocationId == request.AllocationId && x.SourceVersion == request.SourceVersion && x.CurrencyId == draft.CurrencyId && x.NetDocumentAmount == source.SubTotal, ct))
                    return new(false, "AdmissionSourceChanged");
                var fact = new ConstructionPurchaseAdmission { CompanyID = c.CompanyId, ProjectId = request.ProjectId, PurchaseOrderId = source.ID,
                    ApprovalRequestId = request.ID, ReservationId = reservationId!.Value, RequestId = requestId, RequestHash = hash,
                    BeforeVersion = request.SourceVersion, ApprovedVersion = ApprovedVersion(request.ContentHash), ContentHash = request.ContentHash,
                    Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
                db.Add(fact); await db.SaveChangesAsync(ct);
                if(binding!=null){
                    db.Add(new ConstructionWarehouseExceptionApplication{CompanyID=c.CompanyId,ProjectId=request.ProjectId,PurchaseOrderId=source.ID,BindingId=binding.ID,AdmissionId=fact.ID,RequestId=requestId,ReviewVersion=warehouseReview!.Version!,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow});
                    await db.SaveChangesAsync(ct);
                    Record(c,request.ProjectId,request.ID,"ConsumeWarehouseException",new{BindingId=binding.ID,AdmissionId=fact.ID,warehouseReview.Version},reason,requestId);await db.SaveChangesAsync(ct);
                }
                if (rebaseRequest != null)
                {
                    var consentBinding = await db.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().SingleOrDefaultAsync(x =>
                        x.CompanyID == c.CompanyId && x.ProjectId == request.ProjectId && x.PurchaseOrderId == source.ID &&
                        x.ApprovalRequestId == request.ID && x.RebaseRequestId == rebaseRequest.ID, ct);
                    if (consentBinding != null)
                    {
                        // The exact consent was revalidated under this serializable purchase lock before
                        // creating the admission. Re-reading afterwards treats that admission as a realized
                        // source and rejects the proof that authorized it.
                        if (rebasedConsentReview == null || rebasedConsentReview.Version != consentBinding.ReviewVersion)
                            return new(false, "ConcurrencyConflict");
                        db.Add(new ConstructionRebasedWarehouseConsentApplication { CompanyID = c.CompanyId,
                            ProjectId = request.ProjectId, PurchaseOrderId = source.ID, BindingId = consentBinding.ID,
                            AdmissionId = fact.ID, RequestId = requestId, ReviewVersion = rebasedConsentReview.Version!,
                            CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow });
                        await db.SaveChangesAsync(ct);
                        Record(c, request.ProjectId, request.ID, "ConsumeRebasedWarehouseConsent",
                            new { BindingId = consentBinding.ID, AdmissionId = fact.ID, rebasedConsentReview.Version }, reason, requestId);
                        await db.SaveChangesAsync(ct);
                    }
                }
                if (await db.PurchaseOrders.Where(x => x.ID == source.ID && x.CompanyID == c.CompanyId && x.Status == "Draft").ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"), ct) != 1) return new(false, "ConcurrencyConflict");
                number = source.OrderNo;
            }
            var status = approve ? "Approved" : "Rejected"; var now = DateTime.UtcNow; var note = reason.Trim();
            if (await db.InventoryApprovals.Where(x => x.ID == approvalId && x.CompanyID == c.CompanyId && x.Status == "Pending")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, status).SetProperty(p => p.DecidedByEmployeeId, c.EmployeeId)
                    .SetProperty(p => p.DecidedAt, (DateTime?)now).SetProperty(p => p.DecisionNote, note).SetProperty(p => p.ResultDocNo, number), ct) != 1) return new(false, "ConcurrencyConflict");
            var stored = await db.InventoryApprovals.AsNoTracking().SingleAsync(x => x.ID == approvalId && x.CompanyID == c.CompanyId, ct);
            Record(c, request.ProjectId, request.ID, approve ? "ApproveConstructionPurchase" : "RejectConstructionPurchase", new { approvalId, request.PurchaseOrderId, request.SourceVersion, status }, reason, requestId);
            await db.SaveChangesAsync(ct); await Emit(stored, approve ? InventoryApprovalEvents.Approved : InventoryApprovalEvents.Rejected, ct);
            return new(true, status, approvalId, request.PurchaseOrderId);
        }, ct);
    }

    private void Record(BusinessContext c, int project, int id, string action, object value, string reason, Guid request) =>
        audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionPurchaseApprovalRequest), id, null,
            action, null, JsonSerializer.Serialize(value), ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionPurchaseApprovalService), request) });

    private Task<long> Emit(InventoryApproval approval, string type, CancellationToken ct) => events.RecordAsync(new BusinessEventRecord
    {
        EntityCode = EntityRegistry.InventoryApproval, EntityId = approval.ID, EventType = type,
        PayloadVersion = InventoryApprovalService.InventoryApprovalEventPayload.Version, Visibility = BusinessEventVisibility.Internal,
        DedupKey = $"inventoryapproval:{approval.ID}:{BusinessEventTypes.ActionOf(type)}",
        Payload = new InventoryApprovalService.InventoryApprovalEventPayload { ApprovalId = approval.ID, DocType = approval.DocType, Status = approval.Status,
            Amount = approval.Amount, RequestedByEmployeeId = approval.RequestedByEmployeeId, DecidedByEmployeeId = approval.DecidedByEmployeeId, ResultDocNo = approval.ResultDocNo }
    }, ct);

    private static bool Conflict(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
