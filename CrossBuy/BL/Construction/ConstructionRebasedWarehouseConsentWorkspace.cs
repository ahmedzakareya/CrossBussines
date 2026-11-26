using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionRebasedWarehouseConsentHistory(int Id, string SiteName, string? SiteNameEn,
    string Reason, DateTime CreatedAt, string Status, string? DecisionReason, DateTime? DecidedAt,
    bool CanDecide);

public sealed record ConstructionRebasedWarehouseConsentPanel(int ProjectId, int OrderId,
    IReadOnlyList<WarehouseSiteOption> Sites, int? SelectedSiteId, string? Witness, Guid RequestId,
    string Reason, IReadOnlyList<ConstructionRebasedWarehouseConsentHistory> History, bool CanRequest,
    string? RefusalCode = null);

public interface IConstructionRebasedWarehouseConsentWorkspace
{
    Task<ConstructionRebasedWarehouseConsentPanel?> ReadAsync(int projectId, int orderId,
        IReadOnlyList<WarehouseSiteOption> sites, int? selectedSiteId, CancellationToken ct = default);
}

public sealed class ConstructionRebasedWarehouseConsentWorkspace(CrossDbContext db,
    IBusinessContextAccessor contexts, IProjectsAccessService projects,
    IEnumerable<IModuleAccessService> modules) : IConstructionRebasedWarehouseConsentWorkspace
{
    public async Task<ConstructionRebasedWarehouseConsentPanel?> ReadAsync(int projectId, int orderId,
        IReadOnlyList<WarehouseSiteOption> sites, int? selectedSiteId, CancellationToken ct = default)
    {
        if (projectId <= 0 || orderId <= 0 || db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return null;
        var c = await contexts.TryGetCurrentAsync(ct);
        if (c is not { IsAuthenticated: true, IsExternalPortal: false, EmployeeId: > 0, CompanyId: > 0 } ||
            c.Source is BusinessContextSource.System or BusinessContextSource.Worker ||
            db.CompanyScope.FilterCompanyId != c.CompanyId || db.CompanyScope.AllowsCrossCompany ||
            !await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct)) return null;
        var inventory = modules.FirstOrDefault(x => x.Scope == EntityRegistry.ScopeInventory);
        if (inventory == null || !await inventory.CanAsync(c, "purchase", new PermissionTarget { CompanyId = c.CompanyId }, ct)) return null;
        var order = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == orderId && x.ProjectId == projectId && x.CompanyID == c.CompanyId, ct);
        if (order == null) return null;
        var reservationId = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().Where(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId)
            .Select(x => (int?)x.ID).SingleOrDefaultAsync(ct);
        if (reservationId is not > 0 || !await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().AnyAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ReservationId == reservationId, ct) ||
            !await ConstructionWarehouseExceptionRequirement.RequiredAsync(db, c.CompanyId, projectId, orderId, ct)) return null;

        var canManage = await inventory.CanAsync(c, "manage", new PermissionTarget { CompanyId = c.CompanyId }, ct);
        var requests = await db.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.ID).Take(50).ToArrayAsync(ct);
        var ids = requests.Select(x => x.ID).ToArray();
        var decisions = await db.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && ids.Contains(x.RequestRowId)).ToArrayAsync(ct);
        var siteIds = requests.Select(x => x.DestinationSiteId).Distinct().ToArray();
        var sitesById = await db.Set<ConstructionSite>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            siteIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
        var decisionByRequest = decisions.ToDictionary(x => x.RequestRowId);
        var history = requests.Select(request =>
        {
            decisionByRequest.TryGetValue(request.ID, out var decision);
            sitesById.TryGetValue(request.DestinationSiteId, out var site);
            var status = decision?.Outcome ?? "Pending";
            return new ConstructionRebasedWarehouseConsentHistory(request.ID, site?.Name ?? "", site?.NameEn,
                request.Reason, request.CreatedAt, status, decision?.Reason, decision?.CreatedAt,
                canManage && status == "Pending" && request.CreatedBy != c.EmployeeId);
        }).ToArray();
        var canRequest = order.Status == "Draft" && sites.Any(x => x.Id == selectedSiteId);
        return new(projectId, orderId, sites, selectedSiteId, null, Guid.NewGuid(), "", history, canRequest);
    }
}
