using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Reporting;

public static class KpiRegistration
{
    // Requires the existing Accounting and Projects dataset packs and module authority adapter.
    public static IServiceCollection AddKpiReporting(this IServiceCollection services)
    {
        services.AddScoped<IKpiRegistry>(sp => new KpiRegistry(sp.GetRequiredService<IReportDatasetRegistry>()));
        services.AddScoped<IKpiEvaluator, KpiEvaluator>();

        // SCR-49: the Workspace dashboard's approved-dataset metrics.
        //
        // REGISTERED HERE AND NOT IN AddCrossBusinessReporting, deliberately. The contributor depends
        // on IKpiRegistry and IKpiEvaluator, which this method registers and the core reporting graph
        // does not — and AddCrossBusinessReporting is self-sufficient by contract, pinned by
        // ReportingDiWiringTests with ValidateOnBuild. Putting it there made a standalone reporting
        // graph fail to build; putting it here keeps each pack resolvable on its own terms.
        //
        // NO Program.cs CHANGE IS NEEDED: AddKpiReporting() is already called there, and the
        // Workspace consumes IEnumerable<IWorkspaceKpiSource>, so registration order does not matter
        // and the dashboard discovers this with no Workspace edit at all. A deployment without the
        // KPI pack simply has no contributor and keeps its module-sourced tiles honestly.
        services.AddScoped<CrossBuy.BL.Workspace.IWorkspaceKpiSource, ReportingWorkspaceKpiSource>();

        return services;
    }
}
