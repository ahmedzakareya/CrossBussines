using CrossBuy.Models.Communication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §10) — THE ONE REGISTRATION CALL.
    //
    // *** THIS IS NOT CALLED FROM Program.cs, AND THAT IS THE DELIVERABLE. ***
    //
    // The brief says architecture only, no production integration. So the entire platform is wired here, in a
    // file this work stream owns, and the application does not call it. Today the running app is byte-for-byte
    // unchanged: no service resolves, no hosted service starts, no request path differs. Switching the platform
    // on is ONE line in Program.cs —
    //
    //     builder.Services.AddCommunicationPlatform(builder.Configuration);
    //
    // — reviewed by whoever owns production wiring, at a time of their choosing.
    //
    // THE DI GRAPH IS STILL PROVEN. CLAUDE.md is explicit that "a DI graph is not verified by unit tests that
    // construct services by hand" — 112 green tests once coexisted with an application that could not boot.
    // CommunicationDiWiringTests therefore builds a REAL ServiceProvider from this method with
    // ValidateOnBuild + ValidateScopes and resolves every public service. An unregistered dependency, a captive
    // dependency, or a scoped service captured by a singleton fails there rather than at somebody's first
    // request.
    //
    // NO HOSTED SERVICE IS REGISTERED, deliberately. A hosted service is a SINGLETON that may never inject a
    // scoped service, must take IServiceScopeFactory, and must bind an explicit company scope — three rules that
    // each cost a real defect to learn. Delivery is a callable drain (ICommNotificationDispatcher) until the
    // phase that owns production wiring can satisfy all three.
    // =============================================================================================
    public static class CommunicationPlatformRegistration
    {
        public static IServiceCollection AddCommunicationPlatform(
            this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);

            // ---- options -------------------------------------------------------------------------------
            if (configuration != null)
                services.Configure<CommunicationPlatformOptions>(
                    configuration.GetSection(CommunicationPlatformOptions.SectionName));
            else
                // A test or a host with no configuration still gets a valid options object. Without this,
                // IOptions<T> resolves to an instance with EnabledChannels empty and every notification silently
                // reports no_channel — a failure that looks like a bug in the platform rather than missing config.
                services.Configure<CommunicationPlatformOptions>(_ => { });

            // ---- foundations ---------------------------------------------------------------------------
            //
            // Everything is AddScoped, matching the DbContext's lifetime. Nothing here is a singleton: every one
            // of these services either holds a CrossDbContext or holds something that does, and a singleton
            // capturing a scoped context is the captive-dependency bug ValidateScopes exists to catch.
            services.AddScoped<ICommEntitySurface, CommEntitySurface>();
            services.AddScoped<ICommActorDirectory, CommActorDirectory>();
            services.AddScoped<ICommBodyPolicy, CommBodyPolicy>();
            services.AddScoped<ICommAuditWriter, CommAuditWriter>();

            // ---- principal expansion (ADR-032) ---------------------------------------------------------
            //
            // Registered as IEnumerable, the shape the kernel uses for its adapters. A deployment adds @role
            // support by registering an ICommPrincipalSource for Role AFTER this call — last registration wins
            // per kind in CommPrincipalResolver, which is what makes an override actually override.
            services.AddScoped<ICommPrincipalSource, EmployeeCommPrincipalSource>();
            services.AddScoped<ICommPrincipalSource, TeamCommPrincipalSource>();
            services.AddScoped<ICommPrincipalSource, DepartmentCommPrincipalSource>();
            services.AddScoped<ICommPrincipalResolver, CommPrincipalResolver>();

            // ---- authorization -------------------------------------------------------------------------
            //
            // CommAccessPolicy CONSUMES IPlatformPermissionProvider; it does not replace, wrap or extend it.
            // This platform registers no permission provider, no module access service and no role. That is what
            // makes "a communication permission can never widen access to a business record" structurally true
            // rather than a promise.
            services.AddScoped<ICommAccessPolicy, CommAccessPolicy>();

            // ---- events (ADR-030 §7) -------------------------------------------------------------------
            //
            // The NULL bridge is the default. PlatformBusinessEventBridge is NOT registered here: registering it
            // would make IBusinessEventService a hard dependency of this platform's DI graph, and a deployment
            // whose kernel SQL is not applied would then fail to comment at all. Turning the bridge on is TWO
            // deliberate steps — set BridgeToBusinessEvents, then call UseBusinessEventBridge() below — so
            // neither can happen by accident.
            services.AddScoped<ICommBusinessEventBridge, NullCommBusinessEventBridge>();
            services.AddScoped<ICommEventPublisher, CommEventPublisher>();

            // ---- notifications (ADR-034) ---------------------------------------------------------------
            services.AddScoped<ICommTemplateCatalog, CommTemplateCatalog>();
            services.AddScoped<ICommTemplateTextProvider, DefaultCommTemplateTextProvider>();
            services.AddScoped<ICommTemplateRenderer, CommTemplateRenderer>();
            services.AddScoped<ICommPreferenceResolver, CommPreferenceResolver>();
            services.AddScoped<ICommNotificationService, CommNotificationService>();

            // Only the in-app channel is registered. Email/Push/WhatsApp are declared in the vocabulary and have
            // no adapter, so a delivery row addressed to one is parked Skipped WITH A REASON rather than sitting
            // Pending forever — see UnwiredCommNotificationChannel for why each is unwired.
            services.AddScoped<ICommNotificationChannel, InAppCommNotificationChannel>();
            services.AddScoped<ICommDeliveryClaimStore, EfCommDeliveryClaimStore>();
            services.AddScoped<ICommNotificationDispatcher, CommNotificationDispatcher>();

            // ---- collaboration services ----------------------------------------------------------------
            services.AddScoped<ICommFilePreviewProvider, DefaultCommFilePreviewProvider>();
            services.AddScoped<ICommAttachmentService, CommAttachmentService>();
            services.AddScoped<ICommThreadService, CommThreadService>();
            services.AddScoped<ICommMentionService, CommMentionService>();
            services.AddScoped<ICommParticipationService, CommParticipationService>();
            services.AddScoped<ICommCommentService, CommCommentService>();
            services.AddScoped<ICommReactionService, CommReactionService>();
            services.AddScoped<ICommReadStatusService, CommReadStatusService>();

            // ---- timeline (ADR-036) --------------------------------------------------------------------
            //
            // The kernel source is registered FIRST so a replacement registered later wins. It reaches
            // BusinessEvents only through ITimelineProjectionService, so the kernel's four filters keep applying
            // — this platform never queries that table.
            services.AddScoped<ICommTimelineSource, PlatformEventTimelineSource>();
            services.AddScoped<ICommTimelineSource, CommCommentTimelineSource>();
            services.AddScoped<ICommTimelineSource, CommMentionTimelineSource>();
            services.AddScoped<ICommTimelineSource, CommAuditTimelineSource>();
            services.AddScoped<ICommTimelineAggregator, CommTimelineAggregator>();

            return services;
        }

        // -------------------------------------------------------------------------------------------------
        // OPT-IN: forward communication events to the platform kernel.
        //
        // A SEPARATE, EXPLICIT CALL, and it must be made together with BridgeToBusinessEvents = true. Two
        // switches for one behaviour is not redundancy — it is the difference between a deployment choosing to
        // couple its commenting to the kernel's schema and a configuration flag doing it silently.
        //
        // BEFORE CALLING THIS, confirm on every database the app runs against:
        //   * deploy/sql/platform_business_events.sql            is applied (BusinessEvents / BusinessEventDispatch)
        //   * deploy/sql/platform_business_events_slice_002.sql  is applied (Notifications.EntityType/EntityId)
        //
        // RecordAsync has no swallowing catch. A missing table is SQL-208 inside the caller's transaction, which
        // means a failed comment on every screen — CLAUDE.md records exactly this coupling for the sale/purchase
        // path (HM-D44/D45) and for reversal (HM-D53). Coordinate with the kernel owner.
        // -------------------------------------------------------------------------------------------------
        public static IServiceCollection UseBusinessEventBridge(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            // Replaces the null bridge. AddScoped appends, and the last registration wins for a single-service
            // resolve, so this override is complete without removing the earlier line.
            services.AddScoped<ICommBusinessEventBridge, PlatformBusinessEventBridge>();
            return services;
        }
    }
}
