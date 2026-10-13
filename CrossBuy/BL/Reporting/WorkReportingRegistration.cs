using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // WORK REPORTING — THE REGISTRATION, AS AN EXTENSION AND NOT AS AN EDIT.
    //
    // WHY THIS FILE EXISTS AT ALL. Every other subject pack registers inline in
    // ReportingRegistration.cs, which is a convergence file several tabs edit. This wave is not
    // authorized to touch it, or Program.cs, so the pack ships its registration as a method the
    // coordinator calls with ONE line. Nothing here runs until that line exists: the pack is code
    // that is READY, not a pack that is ACTIVE, and the difference is deliberate.
    //
    // WHAT IS REGISTERED, and nothing else:
    //
    //   * the two dataset definitions, captured PER ITERATION — a loop variable captured in a
    //     closure is the exact bug the per-iteration local exists to avoid, and the neighbouring
    //     packs in ReportingRegistration.cs all write it this way;
    //   * one IReportDataSource per subject, which is the file's own stated convention.
    //
    // WHAT IS NOT REGISTERED, and must not be:
    //
    //   * NO permission adapter or module authority. WorkReportModuleAuthority already exists in
    //     WorkProjectsReportAuthority.cs and is already wired in Program.cs, claiming the whole
    //     `work.` namespace and refusing every unmapped key inside it. A second registration here
    //     would be a second answer to one question.
    //   * NO permission vocabulary. `work.reports.view` is declared once, in WorkReportPermissions,
    //     and this pack references it rather than restating it.
    //   * NO IReportDefinitionProvider. These two subjects reach the Studio through the dataset
    //     registry, which is the seam a module is supposed to use. A provider would be a second
    //     catalogue of prebuilt Work reports and nothing asks for one.
    //
    // THE CONSEQUENCE OF THAT LAST POINT, STATED PLAINLY RATHER THAN DISCOVERED LATER:
    // ReportStudioService offers a dataset only when some ReportDefinition shares its DataSourceKey
    // ("A dataset with no runnable report is not offered"). With no Work definition provider, these
    // two subjects are registered and queryable through the platform but will NOT appear in the
    // Studio picker. That is a known, named gap for a later wave, not an accident of this one.
    // ============================================================================================
    public static class WorkReportingRegistration
    {
        public static IServiceCollection AddWorkReporting(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            foreach (var work in WorkReportingDatasets.All())
            {
                // Captured per iteration, NOT the loop variable's final value.
                var definition = work;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }

            services.AddScoped<IReportDataSource, WorkTasksDataSource>();
            services.AddScoped<IReportDataSource, WorkApprovalsDataSource>();

            return services;
        }
    }
}
