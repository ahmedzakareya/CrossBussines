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

            // DOC1 - published client documents. Registered beside the portal's other reads because
            // it IS one: it resolves the same PortalContext, answers the same audience and reaches
            // the document platform only through IDocumentStorage, which Program.cs already provides.
            // Registering it grants nobody anything - every method refuses an unresolved identity and
            // an account without the portal-documents capability.
            services.AddScoped<IPortalDocumentService, PortalDocumentService>();

            // PROVISIONING IS STAFF AUTHORITY AND STILL BELONGS HERE. It is registered beside the
            // portal's own services because it is the thing that CREATES a portal identity, not
            // because a portal user may reach it - it resolves a BusinessContext, which an external
            // caller cannot produce, so registration grants nobody anything.
            services.AddScoped<IPortalProvisioningService, PortalProvisioningService>();

            // CONTACT PHOTOGRAPHS - the ONE authority over a piece of personal data that both
            // audiences ask for. It is registered here rather than beside the CRM services because
            // the portal half of it resolves a PortalContext, and because a single registration is
            // what keeps the staff read and the portal read applying the same storage rules.
            //
            // Registering it grants nobody anything: the portal method refuses anything but the
            // caller's own contact row, and the staff method refuses without the CRM read right and
            // the caller's own company.
            services.AddScoped<IPortalPhotoService, PortalPhotoService>();
            return services;
        }
    }
}
