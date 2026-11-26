using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

internal sealed record ConstructionExposureRebaseCandidate(int ProposalId, string SourceVersion);

internal static class ConstructionExposureRebaseReader
{
    internal static async Task<(string? Error, List<ConstructionOrderAllocationLine> Lines, object? Evidence)> ReadAsync(
        CrossDbContext db, IProjectsAccessService access, IConstructionAuditService audit, BusinessContext c, int project,
        ConstructionBudgetVersion baseline, ConstructionCommitmentReservation reservation,
        ConstructionExposureRebaseCandidate? candidate, CancellationToken ct, int? authorizedReceiptWarehouseId = null)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("An exposure transaction is required");
        var fact = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.ReservationId == reservation.ID, ct);
        var proposalId = candidate?.ProposalId ?? fact?.RebaseRequestId;
        var version = candidate?.SourceVersion ?? fact?.SourceVersion;
        if (proposalId == null || version == null) return ("ReservationRebaseRequired", new(), null);
        if (candidate != null && fact != null) return ("RebaseAlreadyActivated", new(), null);
        if (fact != null && fact.TargetBudgetVersionId != baseline.ID) return ("ReservationRebaseRequired", new(), null);
        var proposal = await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == proposalId && x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.ReservationId == reservation.ID && x.TargetBudgetVersionId == baseline.ID, ct);
        if (proposal == null) return ("ReservationRebaseRequired", new(), null);
        var service = new ConstructionReservationRebaseRequestService(db, access, audit);
        var approved = await db.PurchaseOrders.AsNoTracking().AnyAsync(x => x.ID == reservation.PurchaseOrderId &&
            x.CompanyID == c.CompanyId && x.ProjectId == project && (x.Status == "Approved" || x.Status == "Received"), ct);
        var source = approved
            ? await service.ReadAdmittedSourceReadinessLockedAsync(c, project, proposal.ID, ct, authorizedReceiptWarehouseId)
            : await service.ReadSourceReadinessLockedAsync(c, project, proposal.ID, ct);
        if (!source.Ok || source.Version != version) return (source.Ok ? "ConcurrencyConflict" : source.Code, new(), null);
        var lines = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.AllocationId == reservation.AllocationId).ToListAsync(ct);
        return (null, lines, fact == null ? new { proposal.ID, SourceVersion = version } : fact);
    }
}
