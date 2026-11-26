using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionRebasedReceiptCheck(bool Ok, string Code, int? EmployeeId = null);
public interface IConstructionRebasedReceiptService
{
    Task<ConstructionRebasedReceiptCheck> EnterAsync(int company, int project, int order, int warehouse, CancellationToken ct = default);
    Task<ConstructionRebasedReceiptCheck> CompleteAsync(int company, int project, int order, int warehouse, int employee, CancellationToken ct = default);
}

// Validates the existing stock/GL writers; never writes stock, journals or a second commitment.
public sealed class ConstructionRebasedReceiptService(CrossDbContext db, IBusinessContextAccessor actors,
    IInventoryAccessService inventory, IProjectsAccessService projects, IConstructionAuditService audit) : IConstructionRebasedReceiptService
{
    // Only immutable source identity is read before the project lock; mutable budget reads follow it.
    internal static Task<int?> ProjectAsync(CrossDbContext db, int company, int order) =>
        db.Set<ConstructionPurchaseDraft>().AsNoTracking().Where(x => x.CompanyID == company && x.PurchaseOrderId == order)
            .Select(x => (int?)x.ProjectId).SingleOrDefaultAsync();

    internal static async Task<bool> RequiresAsync(CrossDbContext db, int company, int order)
    {
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().Where(x =>
            x.CompanyID == company && x.PurchaseOrderId == order).Select(x => new { x.ProjectId, x.BudgetVersionId }).SingleOrDefaultAsync();
        if (draft == null) return false;
        var current = await db.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x =>
            x.CompanyID == company && x.ProjectId == draft.ProjectId && x.IsBaseline && x.Status == "Approved")
            .Select(x => (int?)x.ID).SingleOrDefaultAsync();
        return current != draft.BudgetVersionId;
    }

    public Task<ConstructionRebasedReceiptCheck> EnterAsync(int company, int project, int order, int warehouse, CancellationToken ct = default)
        => Validate(company, project, order, warehouse, null, true, ct);
    public Task<ConstructionRebasedReceiptCheck> CompleteAsync(int company, int project, int order, int warehouse, int employee, CancellationToken ct = default)
        => Validate(company, project, order, warehouse, employee, false, ct);

    private async Task<ConstructionRebasedReceiptCheck> Validate(int company, int project, int order, int warehouse,
        int? expectedEmployee, bool acquire, CancellationToken ct)
    {
        var c = await actors.TryGetCurrentAsync(ct);
        if (c == null || !c.IsAuthenticated || c.IsExternalPortal || c.EmployeeId is not > 0 ||
            c.CompanyId != company || db.CompanyScope.FilterCompanyId != company || db.CompanyScope.AllowsCrossCompany ||
            expectedEmployee.HasValue && c.EmployeeId != expectedEmployee || !await inventory.CanAsync("doc") || warehouse <= 0 || !await inventory.CanUseWarehouseAsync(warehouse) ||
            !await projects.CanAsync(c, ProjectsActions.Read, new PermissionTarget { ProjectId = project }, ct))
            return new(false, "RebasedReceiptAccessDenied");
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            return new(false, "SerializableAdmissionTransactionRequired");
        if (acquire && !await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct))
            return new(false, "ConcurrencyConflict");
        var map = await (from m in db.Set<ConstructionReservationExposureRebase>().AsNoTracking()
            join r in db.Set<ConstructionCommitmentReservation>().AsNoTracking() on m.ReservationId equals r.ID
            where m.CompanyID == company && m.ProjectId == project && r.CompanyID == company && r.ProjectId == project && r.PurchaseOrderId == order
            select m).SingleOrDefaultAsync(ct);
        if (map == null) return new(false, "ReservationRebaseRequired");
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == map.TargetBudgetVersionId && x.CompanyID == company && x.ProjectId == project && x.IsBaseline && x.Status == "Approved", ct);
        if (baseline == null) return new(false, "ApprovedBaselineRequired");
        var hasRebasedWarehouseConsent = await db.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking()
            .AnyAsync(x => x.CompanyID == company && x.ProjectId == project && x.PurchaseOrderId == order, ct);
        var consentProven = false;
        if (hasRebasedWarehouseConsent ||
            await ConstructionWarehouseExceptionRequirement.RequiredAsync(db, company, project, order, ct))
        {
            var consent = await ReviewWarehouseConsentForReceipt(company, project, order, warehouse, map, ct);
            if (!consent.Ok) return consent;
            consentProven = true;
        }
        var source = await new ConstructionReservationRebaseRequestService(db, projects, audit)
            .ReadAdmittedSourceReadinessLockedAsync(c, project, map.RebaseRequestId, ct,
                consentProven ? warehouse : null);
        if (!source.Ok) return new(false, source.Code);
        if (source.Version != map.SourceVersion) return new(false, "ConcurrencyConflict");
        // Receiving realizes stock / GRNI, not an expense or an additional commitment.
        // Prove its financial graph without requiring permission to view project budgets.
        if (await db.GoodsReceipts.AsNoTracking().AnyAsync(x => x.CompanyID == company && x.PurchaseOrderId == order, ct))
        {
            var purchase = await db.PurchaseOrders.AsNoTracking().SingleAsync(x => x.CompanyID == company && x.ID == order, ct);
            if (await ConstructionUnbilledReceiptWitness.ReadAsync(db, projects, audit, c, project, baseline,
                    purchase, ct, authorizedReceiptWarehouseId: consentProven ? warehouse : null) == null)
                return new(false, "ReceiptCoverageRequired");
        }
        return new(true, "RebasedReceiptProven", c.EmployeeId);
    }

    // The admission consumes consent before the PO becomes Approved, so receipt cannot replay the
    // pre-admission quote. Instead it proves that the admitted PO carries that exact approved consent
    // and that its selected destination site still resolves to this receipt warehouse.
    private async Task<ConstructionRebasedReceiptCheck> ReviewWarehouseConsentForReceipt(int company, int project,
        int order, int warehouse, ConstructionReservationExposureRebase map, CancellationToken ct)
    {
        var binding = await db.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == company && x.ProjectId == project && x.PurchaseOrderId == order &&
            x.ExposureRebaseId == map.ID && x.RebaseRequestId == map.RebaseRequestId, ct);
        if (binding == null) return new(false, "WarehouseExceptionContinuationUnavailable");

        var application = await db.Set<ConstructionRebasedWarehouseConsentApplication>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == company && x.ProjectId == project && x.PurchaseOrderId == order && x.BindingId == binding.ID, ct);
        if (application == null || application.ReviewVersion != binding.ReviewVersion)
            return new(false, "WarehouseExceptionContinuationUnavailable");

        var admission = await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == application.AdmissionId && x.CompanyID == company && x.ProjectId == project && x.PurchaseOrderId == order, ct);
        var approvalRequest = admission == null ? null : await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ID == admission.ApprovalRequestId && x.CompanyID == company &&
                x.ProjectId == project && x.PurchaseOrderId == order, ct);
        if (admission == null || approvalRequest == null || binding.ApprovalRequestId != admission.ApprovalRequestId ||
            binding.CreatedBy != approvalRequest.CreatedBy || application.CreatedBy != admission.CreatedBy)
            return new(false, "WarehouseExceptionContinuationUnavailable");

        var request = await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == binding.ConsentRequestRowId && x.CompanyID == company && x.ProjectId == project &&
            x.PurchaseOrderId == order && x.ExposureRebaseId == map.ID && x.DestinationSiteId == binding.DestinationSiteId &&
            x.SourceWarehouseId == binding.SourceWarehouseId, ct);
        var decision = await db.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == binding.ConsentDecisionRowId && x.CompanyID == company && x.ProjectId == project &&
            x.RequestRowId == binding.ConsentRequestRowId && x.Outcome == "Approved", ct);
        if (request == null || decision == null || request.CreatedBy == decision.CreatedBy ||
            decision.CreatedBy == approvalRequest.CreatedBy || decision.CreatedBy == admission.CreatedBy)
            return new(false, "WarehouseExceptionContinuationUnavailable");

        var purchaseWarehouse = await db.PurchaseOrders.AsNoTracking().Where(x =>
            x.ID == order && x.CompanyID == company && x.ProjectId == project)
            .Select(x => x.WarehouseId).SingleOrDefaultAsync(ct);
        var site = await db.Set<ConstructionSite>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == binding.DestinationSiteId && x.CompanyID == company && x.ProjectId == project && x.IsActive, ct);
        var policy = await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == company && x.PolicyKey == "CrossSiteWarehouse", ct);
        var currentMappingVersion = await CurrentMappingVersion(company, project, binding.SourceWarehouseId,
            binding.DestinationSiteId, ct);
        if (purchaseWarehouse != binding.SourceWarehouseId || site?.WarehouseId != warehouse ||
            request.PolicyToken.Length == 0 || policy?.Mode != "ApprovedException" ||
            !policy.ConcurrencyToken.SequenceEqual(request.PolicyToken) ||
            !binding.PolicyToken.SequenceEqual(request.PolicyToken) ||
            request.SourceFactsVersion != binding.SourceFactsVersion || decision.SourceFactsVersion != request.SourceFactsVersion ||
            request.MappingVersion != binding.MappingVersion || currentMappingVersion != binding.MappingVersion ||
            application.ReviewVersion != binding.ReviewVersion)
            return new(false, "WarehouseExceptionContinuationUnavailable");

        return new(true, "RebasedWarehouseConsentProven");
    }

    // Keep this witness byte-for-byte aligned with ConstructionWarehouseClassifier.FactsVersion.
    // Receipt runs with project-read access, so it rechecks the facts without borrowing the classifier's
    // separate budget-view permission or treating consent as a warehouse grant.
    private async Task<string?> CurrentMappingVersion(int company, int project, int sourceWarehouse,
        int destinationSiteId, CancellationToken ct)
    {
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == company && x.ID == sourceWarehouse && x.IsActive, ct);
        var policy = await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == company && x.PolicyKey == "CrossSiteWarehouse", ct);
        if (warehouse == null || policy?.Mode is not ("Restricted" or "ApprovedException")) return null;

        var sites = await db.Set<ConstructionSite>().AsNoTracking().Where(x => x.CompanyID == company &&
            (x.ProjectId == project || x.WarehouseId == sourceWarehouse || x.ID == destinationSiteId))
            .OrderBy(x => x.ID).ToListAsync(ct);
        var destination = sites.SingleOrDefault(x => x.ID == destinationSiteId && x.ProjectId == project && x.IsActive);
        if (destination?.WarehouseId is not > 0 || sites.Any(x => x.RowVersion is not { Length: > 0 })) return null;
        var target = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == destination.WarehouseId && x.CompanyID == company && x.IsActive, ct);
        if (target == null) return null;

        var targetWarehouseState = new { target.ID, target.CompanyID, target.IsActive, target.BranchHierarchicalId, target.WarehouseType };
        var mapped = sites.Where(x => x.WarehouseId == sourceWarehouse).ToArray();
        var active = mapped.Where(x => x.IsActive).ToArray();
        var projectIds = active.Select(x => x.ProjectId).Distinct().ToArray();
        var sourceProjects = await db.Projects.AsNoTracking().Where(x => x.CompanyID == company && projectIds.Contains(x.ID))
            .OrderBy(x => x.ID).Select(x => new { x.ID, x.IsActive }).ToListAsync(ct);
        ConstructionWarehouseRelation relation;
        if (active.Length == 0) relation = mapped.Length == 0 ? ConstructionWarehouseRelation.Unmapped : ConstructionWarehouseRelation.InactiveMapping;
        else if (sourceProjects.Count != projectIds.Length) relation = ConstructionWarehouseRelation.UnprovenMapping;
        else if (sourceProjects.Any(x => !x.IsActive)) relation = ConstructionWarehouseRelation.InactiveMapping;
        else if (active.Length > 1) relation = ConstructionWarehouseRelation.AmbiguousMapping;
        else if (active[0].ProjectId != project) relation = ConstructionWarehouseRelation.OtherProject;
        else relation = active[0].ID == destination.ID ? ConstructionWarehouseRelation.SameSite : ConstructionWarehouseRelation.SameProjectOtherSite;
        if (relation is not (ConstructionWarehouseRelation.OtherProject or ConstructionWarehouseRelation.SameProjectOtherSite)) return null;

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Company = company,
            Project = project,
            Destination = (int?)destinationSiteId,
            Warehouse = new { warehouse.ID, warehouse.CompanyID, warehouse.IsActive, warehouse.BranchHierarchicalId, warehouse.WarehouseType },
            DestinationWarehouse = (object?)targetWarehouseState,
            Policy = new { policy.Mode, policy.ConcurrencyToken },
            Mappings = sites.Select(x => new { x.ID, x.CompanyID, x.ProjectId, x.WarehouseId, x.IsActive, x.RowVersion }),
            SourceProjects = sourceProjects,
            Relation = relation.ToString()
        })));
    }
}
