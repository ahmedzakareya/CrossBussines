using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionRebasedWarehouseConsentQuote(bool Ok, string Code, string? Version = null,
    string? SourceFactsVersion = null, string? MappingVersion = null, int? DestinationSiteId = null,
    int? SourceWarehouseId = null)
{
    public bool OperationalGrant => false;
}

public sealed record ConstructionRebasedWarehouseConsentResult(bool Ok, string Code, int? Id = null)
{
    public bool OperationalGrant => false;
}

public sealed record ConstructionRebasedWarehouseConsentConsumptionReview(bool Ok, string Code,
    int? ConsentRequestRowId = null, int? ConsentDecisionRowId = null, int? ExposureRebaseId = null,
    int? DestinationSiteId = null, int? SourceWarehouseId = null, string? SourceFactsVersion = null,
    string? MappingVersion = null, byte[]? PolicyToken = null, string? Version = null, int? DecisionActor = null)
{
    public bool GrantsWarehouseAccess => false;
}

public interface IConstructionRebasedWarehouseConsentService
{
    Task<ConstructionRebasedWarehouseConsentQuote> PreviewAsync(int projectId, int orderId, int destinationSiteId, CancellationToken ct = default);
    Task<ConstructionRebasedWarehouseConsentResult> RequestAsync(int projectId, int orderId, int destinationSiteId,
        string witness, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionRebasedWarehouseConsentResult> DecideAsync(int projectId, int requestRowId, string outcome,
        Guid decisionId, string reason, CancellationToken ct = default);
}

// Distinct source-bound request/decision for the rebased lineage. Neither row enables admission,
// receipt, stock, GL, or warehouse access; those writers need a later qualified binding/application.
public sealed class ConstructionRebasedWarehouseConsentService(CrossDbContext db, IBusinessContextAccessor contexts,
    IProjectsAccessService projects, IEnumerable<IModuleAccessService> modules, IConstructionWarehouseAccess warehouses,
    IConstructionAuditService audit) : IConstructionRebasedWarehouseConsentService
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task<BusinessContext?> Actor(string action, CancellationToken ct)
    {
        var c = await contexts.TryGetCurrentAsync(ct);
        if (c is not { IsAuthenticated: true, IsExternalPortal: false, EmployeeId: > 0, CompanyId: > 0 } ||
            c.Source is BusinessContextSource.System or BusinessContextSource.Worker ||
            db.CompanyScope.FilterCompanyId != c.CompanyId || db.CompanyScope.AllowsCrossCompany) return null;
        var inventory = modules.FirstOrDefault(x => x.Scope == EntityRegistry.ScopeInventory);
        return inventory != null && await inventory.CanAsync(c, action, new PermissionTarget { CompanyId = c.CompanyId }, ct) ? c : null;
    }

    private async Task<ConstructionRebasedWarehouseConsentQuote> QuoteLocked(BusinessContext c, int projectId,
        int orderId, int destinationSiteId, CancellationToken ct, string action = "purchase")
    {
        if (db.Database.CurrentTransaction == null) return new(false, "CleanUnitOfWorkRequired");
        var current = await Actor(action, ct);
        if (current == null || current.CompanyId != c.CompanyId || current.EmployeeId != c.EmployeeId || current.UserId != c.UserId ||
            !await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct))
            return new(false, "AccessDenied");
        if (projectId <= 0 || orderId <= 0 || destinationSiteId <= 0) return new(false, "InvalidWarehouseExceptionRequest");

        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        if (reservation == null) return new(false, "ReservationRebaseRequired");
        var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ReservationId == reservation.ID, ct);
        if (map == null) return new(false, "ActivatedExposureRebaseRequired");
        var source = await new ConstructionReservationRebaseRequestService(db, projects, audit)
            .ReadSourceReadinessLockedAsync(c, projectId, map.RebaseRequestId, ct);
        if (!source.Ok) return new(false, source.Code);
        if (source.Version != map.SourceVersion) return new(false, "ConcurrencyConflict");

        var order = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ID == orderId && x.Status == "Draft", ct);
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        if (order?.WarehouseId is not > 0 || draft == null || draft.BudgetVersionId != reservation.BudgetVersionId)
            return new(false, "RebaseSourceNotProven");
        if (await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct) ||
            await db.GoodsReceipts.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct) ||
            await db.Set<ConstructionPartialInvoice>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct) ||
            await db.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct) ||
            await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId, ct))
            return new(false, "RebaseRealizedSourceUnsupported");

        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ID == map.TargetBudgetVersionId && x.IsBaseline && x.Status == "Approved", ct);
        if (baseline == null || baseline.ID == reservation.BudgetVersionId) return new(false, "ApprovedBaselineRequired");
        var policy = await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.PolicyKey == "CrossSiteWarehouse", ct);
        if (policy?.Mode != "ApprovedException") return new(false, policy == null ? "CrossSitePolicyNotConfigured" : "WarehouseExceptionPolicyRestricted");

        var classification = await new ConstructionWarehouseClassifier(db, contexts, projects, warehouses)
            .InspectLockedAsync(c, projectId, order.WarehouseId.Value, destinationSiteId, ct);
        if (!classification.Ok) return new(false, classification.Code);
        if (classification.CompanyMode != "ApprovedException" ||
            classification.Relation is not (ConstructionWarehouseRelation.OtherProject or ConstructionWarehouseRelation.SameProjectOtherSite))
            return new(false, "CrossSiteSourceNotProven");
        if (string.IsNullOrWhiteSpace(classification.FactsVersion)) return new(false, "UnprovenMapping");

        var facts = Hash(new
        {
            Kind = "ConstructionRebasedWarehouseConsent/v1", Company = c.CompanyId, Project = projectId, Order = orderId,
            Reservation = reservation, ExposureRebase = map, SourceVersion = source.Version, Draft = draft,
            OrderWarehouse = order.WarehouseId, DestinationSite = destinationSiteId, MappingFacts = classification.FactsVersion,
            Policy = policy.ConcurrencyToken, Baseline = new { baseline.ID, baseline.RowVersion }
        });
        return new(true, "RebasedWarehouseConsentReady", Hash(new { Facts = facts, Actor = c.EmployeeId, c.UserId }),
            facts, classification.FactsVersion, destinationSiteId, order.WarehouseId);
    }

    // Re-evaluates an approved consent inside the canonical purchase transaction. This is a
    // witness for the binding/application records only; it never grants warehouse access.
    internal async Task<ConstructionRebasedWarehouseConsentConsumptionReview> ReviewForPurchaseLockedAsync(
        BusinessContext c, int projectId, int orderId, int consentDecisionRowId, string permissionAction,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null) return new(false, "CleanUnitOfWorkRequired");
        var decision = await db.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == consentDecisionRowId && x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.Outcome == "Approved", ct);
        if (decision == null) return new(false, "ApprovedWarehouseConsentRequired");
        var request = await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == decision.RequestRowId && x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
        if (request == null || request.CreatedBy == decision.CreatedBy) return new(false, "WarehouseConsentLineageChanged");
        var quote = await QuoteLocked(c, projectId, orderId, request.DestinationSiteId, ct, permissionAction);
        if (!quote.Ok) return new(false, quote.Code);
        var policy = await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.PolicyKey == "CrossSiteWarehouse", ct);
        if (policy == null || !policy.ConcurrencyToken.SequenceEqual(request.PolicyToken) ||
            quote.SourceFactsVersion != request.SourceFactsVersion || decision.SourceFactsVersion != request.SourceFactsVersion ||
            quote.MappingVersion != request.MappingVersion || quote.SourceWarehouseId != request.SourceWarehouseId ||
            quote.DestinationSiteId != request.DestinationSiteId)
            return new(false, "ConcurrencyConflict");
        var version = Hash(new { Kind = "ConstructionRebasedWarehouseConsentConsumption/v1",
            SourceFacts = quote.SourceFactsVersion, Mapping = quote.MappingVersion,
            Destination = quote.DestinationSiteId, Warehouse = quote.SourceWarehouseId,
            Decision = decision.ID, decision.DecisionId, DecisionActor = decision.CreatedBy, ConsentRequest = request.ID,
            ConsentActor = request.CreatedBy, Policy = Convert.ToHexString(request.PolicyToken) });
        return new(true, "RebasedWarehouseConsentConsumptionReady", request.ID, decision.ID, request.ExposureRebaseId,
            request.DestinationSiteId, request.SourceWarehouseId, request.SourceFactsVersion, request.MappingVersion,
            request.PolicyToken, version, decision.CreatedBy);
    }

    public async Task<ConstructionRebasedWarehouseConsentQuote> PreviewAsync(int projectId, int orderId,
        int destinationSiteId, CancellationToken ct = default)
    {
        var c = await Actor("purchase", ct);
        if (c == null || !await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct))
            return new(false, "AccessDenied");
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction != null) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var quote = await QuoteLocked(c, projectId, orderId, destinationSiteId, ct);
            await tx.CommitAsync(ct);
            return quote;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 1205 or 1222) { return new(false, "ConcurrencyConflict"); }
    }

    public async Task<ConstructionRebasedWarehouseConsentResult> RequestAsync(int projectId, int orderId,
        int destinationSiteId, string witness, Guid requestId, string reason, CancellationToken ct = default)
    {
        var c = await Actor("purchase", ct);
        if (c == null || !await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct))
            return new(false, "AccessDenied");
        if (requestId == Guid.Empty || witness?.Length != 64 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidWarehouseExceptionRequest");
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction != null) return new(false, "CleanUnitOfWorkRequired");
        reason = reason.Trim();
        var requestHash = Hash(new { Company = c.CompanyId, Project = projectId, Order = orderId, Destination = destinationSiteId,
            Witness = witness, Actor = c.EmployeeId, c.UserId, Reason = reason });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var prior = await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (prior != null) return prior.RequestHash == requestHash ? new(true, "AlreadyRequested", prior.ID) : new(false, "IdempotencyConflict");
            var quote = await QuoteLocked(c, projectId, orderId, destinationSiteId, ct);
            if (!quote.Ok) return new(false, quote.Code);
            if (quote.Version != witness) return new(false, "ConcurrencyConflict");
            var reservationId = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().Where(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId).Select(x => x.ID).SingleAsync(ct);
            var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ReservationId == reservationId, ct);
            if (await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId &&
                x.ExposureRebaseId == map.ID && x.DestinationSiteId == destinationSiteId && x.SourceFactsVersion == quote.SourceFactsVersion && x.CreatedBy == c.EmployeeId, ct))
                return new(false, "WarehouseExceptionAlreadyRequested");
            var row = new ConstructionRebasedWarehouseConsentRequest { CompanyID = c.CompanyId, ProjectId = projectId,
                PurchaseOrderId = orderId, ExposureRebaseId = map.ID, DestinationSiteId = destinationSiteId,
                SourceWarehouseId = quote.SourceWarehouseId!.Value, SourceVersion = map.SourceVersion, SourceFactsVersion = quote.SourceFactsVersion!,
                MappingVersion = quote.MappingVersion!, PolicyToken = (await db.Set<ConstructionCompanyPolicy>().AsNoTracking()
                    .SingleAsync(x => x.CompanyID == c.CompanyId && x.PolicyKey == "CrossSiteWarehouse", ct)).ConcurrencyToken.ToArray(),
                RequestId = requestId, RequestHash = requestHash, Reason = reason, CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionRebasedWarehouseConsentRequest), row.ID,
                null, "RequestRebasedWarehouseConsent", null, JsonSerializer.Serialize(new { row.PurchaseOrderId, row.ExposureRebaseId,
                    row.DestinationSiteId, row.SourceWarehouseId, row.SourceFactsVersion, OperationalGrant = false }),
                ConstructionChangeKind.Created, reason, c.EmployeeId, nameof(ConstructionRebasedWarehouseConsentService), requestId) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "WarehouseExceptionRequested", row.ID);
        }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 1205 or 1222) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<ConstructionRebasedWarehouseConsentResult> DecideAsync(int projectId, int requestRowId,
        string outcome, Guid decisionId, string reason, CancellationToken ct = default)
    {
        var c = await Actor("manage", ct);
        if (c == null || !await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct))
            return new(false, "AccessDenied");
        if (projectId <= 0 || requestRowId <= 0 || outcome is not ("Approved" or "Rejected") || decisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return new(false, "InvalidWarehouseExceptionDecision");
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction != null) return new(false, "CleanUnitOfWorkRequired");
        reason = reason.Trim();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var current = await Actor("manage", ct);
            if (current == null || current.CompanyId != c.CompanyId || current.EmployeeId != c.EmployeeId || current.UserId != c.UserId)
                return new(false, "AccessDenied");
            var request = await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.ID == requestRowId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
            if (request == null) return new(false, "WarehouseExceptionRequestNotFound");
            if (request.CreatedBy == c.EmployeeId) return new(false, "SeparateApproverRequired");
            var hash = Hash(new { Company = c.CompanyId, Project = projectId, Request = request.ID,
                request.SourceFactsVersion, Outcome = outcome, Actor = c.EmployeeId, c.UserId, Reason = reason });
            var prior = await db.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.DecisionId == decisionId, ct);
            if (prior != null) return prior.RequestHash == hash ? new(true, "DecisionSaved", prior.ID) : new(false, "DecisionIdConflict");
            if (await db.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking().AnyAsync(x =>
                x.CompanyID == c.CompanyId && x.RequestRowId == request.ID, ct)) return new(false, "WarehouseExceptionAlreadyDecided");
            if (outcome == "Approved")
            {
                var quote = await QuoteLocked(c, projectId, request.PurchaseOrderId, request.DestinationSiteId, ct, "manage");
                if (!quote.Ok) return new(false, quote.Code);
                if (quote.SourceFactsVersion != request.SourceFactsVersion || quote.MappingVersion != request.MappingVersion ||
                    quote.SourceWarehouseId != request.SourceWarehouseId || quote.Version == null ||
                    !(await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleAsync(x => x.CompanyID == c.CompanyId && x.PolicyKey == "CrossSiteWarehouse", ct))
                        .ConcurrencyToken.SequenceEqual(request.PolicyToken)) return new(false, "ConcurrencyConflict");
            }
            var row = new ConstructionRebasedWarehouseConsentDecision { CompanyID = c.CompanyId, ProjectId = projectId,
                RequestRowId = request.ID, Outcome = outcome, SourceFactsVersion = request.SourceFactsVersion,
                DecisionId = decisionId, RequestHash = hash, Reason = reason, CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionRebasedWarehouseConsentDecision), row.ID,
                null, "DecideRebasedWarehouseConsent", null, JsonSerializer.Serialize(new { row.RequestRowId, row.Outcome,
                    row.SourceFactsVersion, OperationalGrant = false }), ConstructionChangeKind.Created, reason,
                c.EmployeeId, nameof(ConstructionRebasedWarehouseConsentService), decisionId) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "DecisionSaved", row.ID);
        }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 1205 or 1222) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}
