using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Portal
{
    /// The Client Portal's ONE registration extension, per SHF-01.
    ///
    /// It registers the external identity resolver and the scoped read service and NOTHING ELSE. In
    /// particular it registers no internal access service and no BusinessContext collaborator: the
    /// portal must not be able to answer an employee's authorization question, and the surest way to
    /// guarantee that is for the container never to hand it the means.
    public static class PortalRegistration
    {
        public static IServiceCollection AddClientPortal(IServiceCollection services)
        {
            services.AddScoped<IPortalContextAccessor, PortalContextAccessor>();
            services.AddScoped<IPortalDataService, PortalDataService>();
            return services;
        }
    }
}
