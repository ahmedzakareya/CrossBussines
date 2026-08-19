namespace CrossBuy.BL.Workspace
{
    // ============================================================================================
    // CrossBusiness Workspace — DI REGISTRATION
    //
    // ONE extension method, so Program.cs gains ONE line. That file is edited by several tabs at once and a
    // multi-line block collides on every merge.
    //
    // ALL SCOPED. Every service here either touches a scoped module service or the resolved BusinessContext,
    // so nothing is a singleton and the captured-scoped-service defect this codebase already paid for cannot
    // occur.
    //
    // NO HOSTED SERVICE. The Workspace is a read model rendered on request; there is nothing to run in the
    // background, and adding a worker would be a platform decision under ADR-013 rather than a screen's.
    //
    // NO WRITE. There is no mutating endpoint and no writer registered — see the mark-as-read handover.
    // ============================================================================================
    public static class WorkspaceServiceCollectionExtensions
    {
        public static IServiceCollection AddCrossBusinessWorkspace(this IServiceCollection services)
        {
            // ---- the façade + the agenda contract -------------------------------------------------
            services.AddScoped<IWorkspaceService, WorkspaceService>();

            // NOTE: IWorkspaceAgendaService is NOT registered here. It belongs to TAB 4
            // (CrossBuy.BL.TasksCalendar) and is already registered in Program.cs by that tab. The Workspace
            // CONSUMES it — registering a second implementation would be the duplication the brief forbids.

            // ---- platform adapters ----------------------------------------------------------------
            //
            // The only two places the Workspace reads a database, both against PLATFORM tables and both behind
            // a contract so the read model itself takes no DbContext. Replacing either with a real query
            // service later changes these two lines and nothing else.
            services.AddScoped<IWorkspaceNotificationSource, PlatformNotificationWorkspaceSource>();
            services.AddScoped<IWorkspaceIdentityResolver, EmployeeWorkspaceIdentityResolver>();

            // ---- reporting sources (Phase 4) ------------------------------------------------------
            //
            // Reporting is consumed through this seam ONLY — never a report data source, renderer or engine.
            //
            // NOTE: NO IWorkspaceReportSource is registered here, deliberately. Reporting owns report
            // favourites and report-library semantics, and it contributes them itself through
            // ReportingWorkspaceSource (registered by AddCrossBusinessReporting). The Workspace's own
            // ReportLibraryWorkspaceReportSource used to be registered on this line and re-derived the SAME
            // favourites from the SAME IReportLibraryService.GetFavoritesAsync, so every pinned report
            // arrived TWICE — once with a working /Reports/Viewer url and once with Url = null. There is now
            // ONE authoritative producer of Reporting favourites. Predicted as H-1 in
            // docs/platform/Stage-Reporting-UI-05-Workspace-Integration.md and left as this tab's call.
            //
            // The fix is a removed registration, NOT dedup inside LoadReportsAsync: de-duplicating two
            // producers of the same fact would hide the ownership defect instead of resolving it.
            //
            // A deployment WITHOUT Reporting therefore has no contributor at all, and the Reports panel
            // reports Unavailable honestly rather than resolving a second-best implementation.

            // ---- favourites + activity extension points -------------------------------------------
            //
            // A module contributes rows by registering one of these AFTER this call. It never edits the
            // Workspace, and the Workspace never learns about the module.
            services.AddScoped<IWorkspaceFavoritesSource, ReportFavoritesWorkspaceSource>();
            services.AddScoped<IWorkspaceActivitySource, NotificationActivityWorkspaceSource>();

            return services;
        }
    }
}
