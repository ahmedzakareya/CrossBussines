using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE FAÇADE. THE ONLY PUBLIC DOOR.
    //
    //      "Every module must call only ReportService.Generate()."
    //
    // That mandate is enforced structurally, not by convention. A caller that wanted to bypass the pipeline would
    // have to resolve IReportRendererRegistry or an IReportDataSource itself — and there is no reason to, because
    // this interface exposes everything a screen needs: generate, preview, browse the catalog, and ask which
    // formats are actually producible.
    //
    // What a caller CANNOT do through this door, by construction:
    //   * name a renderer or an engine (it asks for a FORMAT);
    //   * name a company (isolation comes from the resolved BusinessContext);
    //   * reach a data source, a template row, or the archive store directly;
    //   * skip authorization or skip the history record.
    //
    // The façade itself is deliberately thin: it resolves the BusinessContext and delegates to IReportEngine.
    // The split exists because the SCHEDULER has no HTTP request and must pass a context explicitly — so the
    // pipeline lives in the engine, and this class is only "where does the context come from".
    // ============================================================================================
    public interface IReportService
    {
        // THE call. Format in, artifact out.
        Task<ReportResult> GenerateAsync(ReportRequest request, CancellationToken cancellationToken = default);

        // A capped, non-archivable HTML render for an on-screen preview pane. Same pipeline, same authorization,
        // same history — it is a Kind, not a shortcut.
        Task<ReportResult> PreviewAsync(ReportRequest request, CancellationToken cancellationToken = default);

        // The reports this caller may see, for the report browser.
        Task<IReadOnlyList<ReportDefinition>> BrowseAsync(string? module = null, string? categoryKey = null,
            string? tag = null, string? search = null, CancellationToken cancellationToken = default);

        // One definition's metadata (parameters, columns, capabilities) — what a UI needs to build a parameter
        // form. Returns null when the caller may not see it, rather than throwing: "no such report, as far as you
        // are concerned" is the correct answer to an unauthorized probe.
        Task<ReportDefinition?> DescribeAsync(string reportCode, CancellationToken cancellationToken = default);

        // Formats that can be produced for this report RIGHT NOW — the definition's permitted formats intersected
        // with the renderers and exporters actually available in this deployment. A screen binds its download
        // buttons to this, so an unbound PDF engine means no PDF button instead of a button that errors.
        Task<IReadOnlyList<ReportOutputFormat>> AvailableFormatsAsync(string reportCode,
            CancellationToken cancellationToken = default);
    }

    public class ReportService : IReportService
    {
        private readonly IReportEngine _engine;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportOutputPipeline _output;
        private readonly IBusinessContextAccessor _contextAccessor;

        public ReportService(IReportEngine engine, IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportOutputPipeline output,
            IBusinessContextAccessor contextAccessor)
        {
            _engine = engine;
            _catalog = catalog;
            _authorization = authorization;
            _output = output;
            _contextAccessor = contextAccessor;
        }

        public async Task<ReportResult> GenerateAsync(ReportRequest request,
            CancellationToken cancellationToken = default)
        {
            var context = await _contextAccessor.GetCurrentAsync(cancellationToken);
            return await _engine.GenerateAsync(request, context, cancellationToken);
        }

        public async Task<ReportResult> PreviewAsync(ReportRequest request,
            CancellationToken cancellationToken = default)
        {
            // Preview forces three things and lets the caller keep everything else. Forcing them here rather than
            // trusting the caller is what makes "preview" mean one thing everywhere:
            //   Kind    = Preview  → capped rows, banner on the page, recorded separately in history
            //   Format  = Html     → an embeddable fragment; a preview is not a download
            //   Archive = false    → a provisional document never enters the archive
            var previewRequest = new ReportRequest
            {
                ReportCode = request.ReportCode,
                TemplateId = request.TemplateId,
                TemplateVersionNo = request.TemplateVersionNo,
                Format = ReportOutputFormat.Html,
                Kind = ReportRunKind.Preview,
                Parameters = request.Parameters,
                Filters = request.Filters,
                Sorts = request.Sorts,
                Groupings = request.Groupings,
                VisibleColumns = request.VisibleColumns,
                PageSetup = request.PageSetup,
                Culture = request.Culture,
                Archive = false,
                MaxRows = request.MaxRows,
                CorrelationId = request.CorrelationId,
            };

            return await GenerateAsync(previewRequest, cancellationToken);
        }

        public async Task<IReadOnlyList<ReportDefinition>> BrowseAsync(string? module = null,
            string? categoryKey = null, string? tag = null, string? search = null,
            CancellationToken cancellationToken = default)
        {
            // TryGetCurrentAsync, not GetCurrentAsync: browsing may legitimately be reached before a company is
            // resolved (a landing page, a menu build), and the honest answer there is an empty list rather than an
            // exception.
            var context = await _contextAccessor.TryGetCurrentAsync(cancellationToken);
            if (context == null) return Array.Empty<ReportDefinition>();

            var matching = _catalog.Query(module, categoryKey, tag, search);
            return await _authorization.FilterVisibleAsync(matching, context, cancellationToken);
        }

        public async Task<ReportDefinition?> DescribeAsync(string reportCode,
            CancellationToken cancellationToken = default)
        {
            var context = await _contextAccessor.TryGetCurrentAsync(cancellationToken);
            if (context == null) return null;

            if (!_catalog.TryGetDefinition(reportCode, out var definition) || definition == null) return null;

            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.View, context,
                cancellationToken);
            return decision.Allowed ? definition : null;
        }

        public async Task<IReadOnlyList<ReportOutputFormat>> AvailableFormatsAsync(string reportCode,
            CancellationToken cancellationToken = default)
        {
            var definition = await DescribeAsync(reportCode, cancellationToken);
            return definition == null
                ? Array.Empty<ReportOutputFormat>()
                : _output.AvailableFormats(definition);
        }
    }
}