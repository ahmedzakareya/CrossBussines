using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // Communication services are registered by Program.cs through this single entry point.
    // Request services stay scoped. Notification delivery is opt-in and requires an explicit
    // positive company allowlist; the hosted worker opens a fresh company scope per pass.
    // DI tests validate both the default graph and singleton/scoped lifetime boundaries.
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
            // Foundation services are AddScoped, matching the DbContext's lifetime. Every one
            // of these services either holds a CrossDbContext or holds something that does, and a singleton
            // capturing a scoped context is the captive-dependency bug ValidateScopes exists to catch.
            services.AddScoped<ICommEntitySurface, CommEntitySurface>();
            services.AddScoped<ICommActorDirectory, CommActorDirectory>();
            services.AddScoped<ICommBodyPolicy, CommBodyPolicy>();
            services.AddScoped<ICommAuditWriter, CommAuditWriter>();

            // ---- principal expansion (ADR-032) ---------------------------------------------------------
            //
            // Registered as IEnumerable, the shape the kernel uses for its adapters. Role uses the shared RBAC
            // directory and requires an explicitly scoped key; a deployment may still replace it by registering
            // another Role source AFTER this call — last registration wins per kind.
            services.AddScoped<ICommPrincipalSource, EmployeeCommPrincipalSource>();
            services.AddScoped<ICommPrincipalSource, TeamCommPrincipalSource>();
            services.AddScoped<ICommPrincipalSource, DepartmentCommPrincipalSource>();
            services.AddScoped<ICommPrincipalSource, RoleCommPrincipalSource>();
            services.AddScoped<ICommPrincipalResolver, CommPrincipalResolver>();
            services.AddScoped<ICommExternalAccessPolicy, CommExternalAccessPolicy>();
            services.AddScoped<ICommPortalService, CommPortalService>();

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
            services.AddScoped<ICommTemplateTextProvider, ResourceCommTemplateTextProvider>();
            services.AddScoped<ICommTemplateRenderer, CommTemplateRenderer>();
            services.AddScoped<ICommPreferenceResolver, CommPreferenceResolver>();
            services.AddScoped<ICommNotificationService, CommNotificationService>();

            // Only the in-app channel is registered. Email/Push/WhatsApp are declared in the vocabulary and have
            // no adapter, so a delivery row addressed to one is parked Skipped WITH A REASON rather than sitting
            // Pending forever — see UnwiredCommNotificationChannel for why each is unwired.
            services.AddScoped<ICommNotificationChannel, InAppCommNotificationChannel>();

            // EMAIL — OPT-IN, and the default stays "declared, not wired".
            //
            // CommMailNotificationChannel QUEUES into the existing CommMessages outbox and never
            // transmits. The reason it is still off by default is not transmission: it is that bridging
            // makes this platform a SECOND PRODUCER for a queue owned elsewhere, with its own status
            // vocabulary and retry policy (HM-D46). That is a production-wiring decision taken with the
            // team that owns the queue, not a flag this platform flips for them.
            if (configuration?.GetValue<bool>("CommunicationPlatform:EmailChannelEnabled") == true)
                services.AddScoped<ICommNotificationChannel, CommMailNotificationChannel>();
            services.AddScoped<ICommDeliveryClaimStore>(sp =>
            {
                var db = sp.GetRequiredService<CrossDbContext>();
                var options = sp.GetRequiredService<IOptions<CommunicationPlatformOptions>>();
                return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal)
                    ? new SqlServerCommDeliveryClaimStore(db, options)
                    : new EfCommDeliveryClaimStore(db, options);
            });
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

            // Re-offers bridgeable events the inline bridge could not deliver. Scoped, never singleton: a
            // singleton here would capture the scoped DbContext, which the activation tests forbid.
            services.AddScoped<ICommEventBridgeReplayService, CommEventBridgeReplayService>();

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

            if (configuration?.GetValue<bool>("CommunicationPlatform:NotificationWorkerEnabled") == true)
            {
                var companies = configuration.GetSection("CommunicationPlatform:NotificationWorkerCompanyIds").Get<List<int>>();
                if (companies == null || companies.Count == 0 || companies.Any(id => id <= 0))
                    throw new InvalidOperationException("Notification activation requires an explicit positive company allow-list.");
                services.AddHostedService<CommNotificationHostedService>();
            }

            // Same fail-closed shape as the notification worker: absent configuration adds NO hosted
            // service at all, and a bare "true" without a company allow-list is refused at startup rather
            // than silently replaying for whichever company a background scope happens to resolve.
            if (configuration?.GetValue<bool>("CommunicationPlatform:BridgeReplayEnabled") == true)
            {
                var replayCompanies = configuration.GetSection("CommunicationPlatform:BridgeReplayCompanyIds").Get<List<int>>();
                if (replayCompanies == null || replayCompanies.Count == 0 || replayCompanies.Any(id => id <= 0))
                    throw new InvalidOperationException("Bridge-replay activation requires an explicit positive company allow-list.");
                services.AddHostedService<CommBridgeReplayHostedService>();
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
