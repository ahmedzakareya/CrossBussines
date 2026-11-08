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

            // ---- approved-dataset metrics (SCR-49) -------------------------------------------------
            //
            // NO IWorkspaceKpiSource IS REGISTERED HERE, and that is the honest state rather than an
            // omission. SCR-49 requires the personal dashboard's metrics to come from approved datasets
            // only. Reporting owns datasets, KPI definitions and their authority; a Workspace-authored
            // implementation would be the Workspace deciding what an approved metric is, which is the
            // inversion ADR-037 exists to prevent — and the same mistake the withdrawn
            // ReportLibraryWorkspaceReportSource made.
            //
            // It cannot be registered by Reporting today either, and the reason is structural: KpiRegistry
            // admits dataset codes from AccountingDatasets and ProjectsDatasets ONLY and throws
            // "Unsupported KPI dataset" for anything else, so no approved KPI over TASK data can exist.
            // Until that changes the task tiles stay module-sourced, and WorkspaceDashboard
            // .UnapprovedMetrics names every tile that is — so the gap is visible instead of implied.
            //
            // When Reporting registers one, the dashboard uses it with NO change to the Workspace.

            // ---- favourites + activity extension points -------------------------------------------
            //
            // A module contributes rows by registering one of these AFTER this call. It never edits the
            // Workspace, and the Workspace never learns about the module.
            // NOTE: NO IWorkspaceFavoritesSource is registered here, deliberately. The seam above is an
            // extension point, not a promise that the Workspace fills it itself.
            //
            // ReportFavoritesWorkspaceSource used to be registered on this line. It read Reporting's
            // favourites through IReportLibraryService.GetFavoritesAsync on EVERY dashboard load - a
            // database round trip - and projected them onto WorkspaceDashboard.Favorites, which NO VIEW
            // RENDERS. Its rows also carried Url = "/Workspace/Reports", the generic-list destination
            // removed from every other Workspace row; nobody ever saw it because nobody drew the panel.
            //
            // It was SPARED when ReportLibraryWorkspaceReportSource was withdrawn above, on the reasoning
            // that it was "a different seam feeding a different panel". That premise was false - there is
            // no such panel - so the same duplicate-producer argument applies to it. The dashboard already
            // shows pinned reports from Reporting's own ReportingWorkspaceSource with working
            // /Reports/Viewer urls.
            //
            // IF A FAVOURITES CARD IS EVER WANTED, THE VIEW COMES FIRST. Registering a source again
            // without one restores exactly this defect: a query per dashboard load for an audience of
            // nobody. Pinned by WorkspaceFavouritesWithdrawnTests.
            // ONE authoritative producer of "what changed". It was the notification store; it is now the
            // platform business-event timeline. Registering both would put the Notifications panel's own
            // rows beside themselves in the Activity panel - the same duplicate-producer defect the
            // Reporting favourites source was withdrawn for.
            services.AddScoped<IWorkspaceActivitySource, TimelineActivityWorkspaceSource>();

            return services;
        }
    }
}
