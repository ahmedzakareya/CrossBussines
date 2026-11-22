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

            // Communication ↔ Tasks. Registered here, but it takes NO Tasks type in its constructor — it
            // resolves ITaskService/ITasksAccessService at call time and reports their absence. So this
            // line does not make the Tasks module a dependency of the Communication graph, and the
            // activation test that resolves every platform service in a scope keeps passing without the
            // Tasks module registered at all.
            services.AddScoped<ICommTaskIntegrationService, CommTaskIntegrationService>();

            // PERSISTENT TASK LINKS — ACTIVATED BY THE DEPLOYMENT THAT HAS THE TABLE, NOT BY THE BUILD.
            //
            // CommTaskLinks is provisioned on CrossBuyDB2 and absent from CrossBuyDev, and this solution
            // reaches BOTH: the https launch profile overrides the connection to CrossBuyDB2, while the
            // http and IIS Express profiles fall back to appsettings.json and CrossBuyDev. An
            // unconditional EfCommTaskLinkStore would therefore be correct under one profile and a
            // SQL-208 on first use under the other two — the HM-D44 shape, reintroduced.
            //
            // SO THE DATABASE IS ASKED, rather than told. ProbingCommTaskLinkStore reads the system
            // catalogues once per process through the probe registered below and delegates to
            // EfCommTaskLinkStore or UnprovisionedCommTaskLinkStore accordingly. A deployment on
            // CrossBuyDB2 gets persistence and one on CrossBuyDev gets the honest refusal, with no
            // configuration distinguishing them — which is what lets the switch live in a committed
            // file at all. See CommTaskLinkProvisioning.cs for why a failed probe is not cached.
            //
            // THE FLAG REMAINS, AS A KILL SWITCH ONLY. Explicit `false` forces the unprovisioned store
            // with no probe, so an operator can disable the feature without touching schema. Absent and
            // `true` both mean "ask the database": the flag can restrict, never grant. That asymmetry is
            // the point — a committed `true` is harmless against an unprovisioned database, where the
            // old semantics made it a SQL-208.
            //
            // DEFAULT CLOSED is unchanged, because the failure modes are not symmetric. Off against a
            // provisioned database means a Link button that honestly reports "not provisioned". On
            // against an unprovisioned one means an exception from the database driver in the middle of
            // somebody's work. The first is a configuration gap somebody notices; the second is an
            // outage.
            //
            // This reuses both existing stores and the existing probe — no second persistence
            // implementation and no second schema check.
            if (configuration?.GetValue<bool?>("CommunicationPlatform:TaskLinksProvisioned") == false)
            {
                services.AddScoped<ICommTaskLinkStore, UnprovisionedCommTaskLinkStore>();
            }
            else
            {
                // Singleton: the answer is a property of the DATABASE, not of a request, and probing it
                // once per request would be one catalogue round trip per page that shows a Link button.
                services.AddSingleton<CommTaskLinkProvisioning>();
                services.AddScoped<EfCommTaskLinkStore>();
                services.AddScoped<ICommTaskLinkStore, ProbingCommTaskLinkStore>();
            }

            // The activation CHECK, registered always. It is a read against the system catalogues that
            // answers "does this database really have CommTaskLinks?" — the question the presence of a
            // .sql file in the repository does not answer. Registering it does not activate anything;
            // it is what an operator runs BEFORE the line above is changed.
            services.AddScoped<ICommTaskLinkSchemaProbe, CommTaskLinkSchemaProbe>();

            // -------------------------------------------------------------------------------------
            // PUSH TOKENS (module 21 / gap G3) — same rule, decided the same way.
            //
            // CommPushTokens ships as its own slice and is applied nowhere yet, so the database is
            // asked rather than told, exactly as it is for task links. On every deployment today the
            // probe finds no table, ProbingCommPushTokenStore delegates to the unprovisioned store,
            // and nothing observable changes: UnwiredCommNotificationChannel.Push() still answers
            // Skip, because no push ADAPTER is registered either — and a store is not an adapter.
            //
            // THE KILL SWITCH IS NOT SYMMETRY FOR ITS OWN SAKE. A device token is a credential; if one
            // leaks, an operator needs to stop the feature reading them NOW, without waiting for a
            // schema change and without dropping a table that is also the evidence of what happened.
            // `false` is that switch. As with task links the flag cannot GRANT — absent and `true`
            // both mean "ask the database".
            // -------------------------------------------------------------------------------------
            services.AddScoped<ICommPushTokenSchemaProbe, CommPushTokenSchemaProbe>();

            if (configuration?.GetValue<bool?>("CommunicationPlatform:PushTokensEnabled") == false)
            {
                services.AddScoped<ICommPushTokenStore, UnprovisionedCommPushTokenStore>();
            }
            else
            {
                services.AddSingleton<CommPushTokenProvisioning>();
                services.AddScoped<EfCommPushTokenStore>();
                services.AddScoped<ICommPushTokenStore, ProbingCommPushTokenStore>();
            }
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
