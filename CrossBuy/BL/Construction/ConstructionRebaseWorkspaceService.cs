using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public interface IConstructionRebaseWorkspaceService
{
    Task<ConstructionRebasePage?> ReadAsync(int project, int? order, int? proposal, CancellationToken ct = default);
    Task<ConstructionRebaseRequestResult> SubmitAsync(int project, int order, ConstructionRebaseOperation operation,
        ConstructionRebaseInput input, CancellationToken ct = default);
}

// Browser orchestration only. Domain writers retain transaction, current-source and independence checks.
public sealed class ConstructionRebaseWorkspaceService(CrossDbContext db, IBusinessContextAccessor contexts,
    IProjectsAccessService access, ConstructionReservationRebaseRequestService rebases,
    ConstructionPurchaseApprovalService purchases) : IConstructionRebaseWorkspaceService
{
    private async Task<BusinessContext?> Actor(int project, CancellationToken ct)
    {
        var c = await contexts.TryGetCurrentAsync(ct);
        return c is { IsAuthenticated: true, IsExternalPortal: false, EmployeeId: > 0, CompanyId: > 0 } &&
            db.CompanyScope.FilterCompanyId == c.CompanyId && !db.CompanyScope.AllowsCrossCompany &&
            await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
            await db.Projects.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ID == project, ct) ? c : null;
    }
    public async Task<ConstructionRebasePage?> ReadAsync(int project, int? order, int? proposal, CancellationToken ct = default)
    {
        var c = await Actor(project, ct); if (c == null) return null;
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return null;
        var p = await db.Projects.AsNoTracking().SingleAsync(x => x.CompanyID == c.CompanyId && x.ID == project, ct);
        var target = await db.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            x.ProjectId == project && x.IsBaseline && x.Status == "Approved").Select(x => (int?)x.ID).SingleOrDefaultAsync(ct);
        var orders = await (from r in db.Set<ConstructionCommitmentReservation>().AsNoTracking()
            join po in db.PurchaseOrders.AsNoTracking() on r.PurchaseOrderId equals po.ID
            where r.CompanyID == c.CompanyId && r.ProjectId == project && po.CompanyID == c.CompanyId && po.ProjectId == project &&
                r.BudgetVersionId != target && !db.Set<ConstructionCommitmentRelease>().Any(x => x.CompanyID == c.CompanyId && x.ReservationId == r.ID)
            orderby po.ID descending select new ConstructionRebaseOrder(po.ID, po.OrderNo, po.Status)).Take(101).ToListAsync(ct);
        ConstructionRebaseSelection? selected = null;
        if (order.HasValue)
        {
            var r = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order, ct);
            var po = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == project && x.ID == order, ct);
            if (r == null || po == null) return null;
            var history = await (from request in db.Set<ConstructionReservationRebaseRequest>().AsNoTracking()
                join decision in db.Set<ConstructionReservationRebaseDecision>().AsNoTracking()
                    on request.ID equals decision.RebaseRequestId into decisions
                from decision in decisions.DefaultIfEmpty()
                where request.CompanyID == c.CompanyId && request.ProjectId == project && request.ReservationId == r.ID
                orderby request.ID descending
                select new ConstructionRebaseHistory(request.ID, request.Reason, request.CreatedBy, request.CreatedAt,
                    decision == null ? "Pending" : decision.Outcome)).ToListAsync(ct);
            var proposalId = proposal ?? history.FirstOrDefault()?.Id;
            var chosen = proposalId.HasValue ? await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == project && x.ReservationId == r.ID && x.ID == proposalId, ct) : null;
            if (proposal.HasValue && chosen == null) return null;
            var outcome = chosen == null ? "None" : history.Single(x => x.Id == chosen.ID).Outcome;
            var map = chosen == null ? null : await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyID == c.CompanyId && x.ProjectId == project && x.RebaseRequestId == chosen.ID && x.ReservationId == r.ID, ct);
            var pending = await (from bridge in db.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking()
                join request in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking() on bridge.ApprovalRequestId equals request.ID
                join approval in db.InventoryApprovals.AsNoTracking() on request.ApprovalId equals approval.ID
                where bridge.CompanyID == c.CompanyId && bridge.ProjectId == project && bridge.PurchaseOrderId == order &&
                    request.CompanyID == c.CompanyId && request.ProjectId == project && approval.CompanyID == c.CompanyId
                orderby request.ID descending select new { approval.ID, approval.Status }).FirstOrDefaultAsync(ct);
            var preview = target.HasValue && po.Status == "Draft" && map == null && history.All(x => x.Outcome == "Rejected")
                ? await rebases.PreviewAsync(c, project, r.ID, target.Value, ct) : null;
            var source = chosen != null && outcome == "Approved" && map == null
                ? await rebases.ReadSourceReadinessAsync(c, project, chosen.ID, ct) : null;
            var purchase = map != null && po.Status == "Draft" && pending?.Status != "Pending"
                ? await purchases.PreviewRebasedRequestAsync(project, po.ID, ct) : null;
            var originalVersion = await db.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
                x.ProjectId == project && x.ID == r.BudgetVersionId).Select(x => x.VersionNo).SingleAsync(ct);
            var targetVersion = await db.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
                x.ProjectId == project && x.ID == target).Select(x => (int?)x.VersionNo).SingleOrDefaultAsync(ct);
            var currency = await db.Currencies.AsNoTracking().Where(x => x.ID == r.CurrencyId).Select(x => x.Code).SingleOrDefaultAsync(ct);
            selected = new(po.ID, po.OrderNo, po.Status, r.ID, target, chosen?.ID, chosen?.SourceVersion,
                chosen != null && outcome == "Pending" && chosen.CreatedBy != c.EmployeeId, outcome, map != null,
                preview, source, purchase, pending?.ID, pending?.Status, pending != null && pending.Status == "Pending" && await purchases.CanReviewAsync(c, pending.ID, ct), history.AsReadOnly(), originalVersion, targetVersion, r.NetDocumentAmount, currency);
        }
        return new(project, p.Code, p.Name, p.NameEn, orders.Take(100).ToArray(), orders.Count > 100, selected);
    }
    public async Task<ConstructionRebaseRequestResult> SubmitAsync(int project, int order, ConstructionRebaseOperation operation,
        ConstructionRebaseInput input, CancellationToken ct = default)
    {
        var c = await Actor(project, ct); if (c == null) return new(false, "AccessDenied");
        if (!Enum.IsDefined(operation) || input.RequestId == Guid.Empty || input.Version?.Length != 64 ||
            string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000) return new(false, "InvalidRebaseRequest");
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == order, ct);
        if (reservation == null) return new(false, "ReservationNotFound");
        if (operation == ConstructionRebaseOperation.Request)
            return await rebases.RequestAsync(c, project, reservation.ID, input.TargetId, input.Version, input.RequestId, input.Reason, ct);
        if (operation == ConstructionRebaseOperation.PurchaseRequest)
        {
            var result = await purchases.RequestRebasedAsync(project, order, input.Version, input.RequestId, input.Reason, ct);
            return new(result.Ok, result.Code, Id: result.ApprovalId);
        }
        if (!await db.Set<ConstructionReservationRebaseRequest>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId &&
            x.ProjectId == project && x.ReservationId == reservation.ID && x.ID == input.ProposalId, ct)) return new(false, "RebaseRequestNotFound");
        return operation == ConstructionRebaseOperation.Activate
            ? await rebases.ActivateExposureAsync(c, project, input.ProposalId, input.Version, input.RequestId, input.Reason, ct)
            : await rebases.DecideAsync(c, project, input.ProposalId, input.Version, operation == ConstructionRebaseOperation.Approve,
                input.RequestId, input.Reason, ct);
    }
}
