using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Reporting;

public static class KpiRegistration
{
    // Requires the existing Accounting and Projects dataset packs and module authority adapter.
    public static IServiceCollection AddKpiReporting(this IServiceCollection services)
    {
        services.AddScoped<IKpiRegistry>(sp => new KpiRegistry(sp.GetRequiredService<IReportDatasetRegistry>()));
        services.AddScoped<IKpiEvaluator, KpiEvaluator>();
        return services;
    }
}
