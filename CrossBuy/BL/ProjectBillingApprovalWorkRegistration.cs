using CrossBuy.Models.Context.Accounting;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL;

// Read authority stays in Projects; the task surface delegates to its actionable inbox.
public sealed class ProjectBillingTaskVisibilityProvider(IProgressBillingService billings)
    : ITaskSourceVisibilityProvider, ITaskSourceReadEntranceProvider
{
    public string EntityType => EntityRegistry.ProjectBilling;
    public async Task<IReadOnlySet<int>> VisibleEntityIdsAsync(BusinessContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CompanyId <= 0 || context.EmployeeId is not > 0 || context.IsExternalPortal)
            return new HashSet<int>();
        var pending = await billings.PendingApprovalsForAsync(context, cancellationToken);
        return pending.Where(b => b.Status == ProgressBillingStatuses.Submitted)
            .Select(b => b.BillingId).ToHashSet();
    }
    public Task<IReadOnlySet<int>> AdmissibleEntityIdsAsync(BusinessContext context,
        CancellationToken cancellationToken = default)
        => VisibleEntityIdsAsync(context, cancellationToken);
}

public static class ProjectBillingApprovalWorkRegistration
{
    public static IServiceCollection AddProjectBillingApprovalWork(this IServiceCollection services)
    {
        services.AddScoped<ITaskOrchestrationRule, ProjectBillingApprovalRule>();
        services.AddScoped<ITaskSourceVisibilityProvider, ProjectBillingTaskVisibilityProvider>();
        services.AddScoped<ITaskSourceReadEntranceProvider, ProjectBillingTaskVisibilityProvider>();
        return services;
    }
}
