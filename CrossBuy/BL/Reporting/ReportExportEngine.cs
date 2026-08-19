namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE EXPORT ENGINE.
    //
    // An EXPORTER is not a renderer, and the distinction is load-bearing rather than taxonomic:
    //
    //   RENDERER  → a DOCUMENT. Paged, styled, grouped, banded, with subtotals and a running footer. Its output
    //               is meant to be READ. Values are formatted for a human ("1,234.56").
    //   EXPORTER  → a DATA FILE. Flat, unstyled, one header row and detail rows. Its output is meant to be
    //               PROCESSED. Values are written as machine values (the decimal 1234.56, a real date).
    //
    // The single most common reporting defect is confusing the two: writing "1,234.56" into a spreadsheet cell
    // produces text, and a user who sums the column gets zero. ReportValues.ForExport exists precisely so an
    // exporter cannot accidentally take the display path.
    //
    // Consequence, stated up front: GROUPING IS NOT REPRESENTED IN AN EXPORT. Group bands and subtotal rows are
    // document furniture; interleaving them into a data file breaks every pivot table built over it. The grouping
    // key columns are present as ordinary columns on every row, which is what a pivot table actually wants.
    // ============================================================================================
    public interface IReportExporter
    {
        string EngineName { get; }

        IReadOnlyList<ReportOutputFormat> Formats { get; }

        bool IsAvailable { get; }

        // Operator-facing reason when IsAvailable is false; null when available. Same contract, and the same
        // reasoning, as IReportRenderer.UnavailableReason.
        string? UnavailableReason { get; }

        // Receives the same ReportView a renderer gets. An exporter reads Columns and Rows and ignores Groups —
        // see the note above.
        Task<ReportArtifact> ExportAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default);
    }

    public interface IReportExportEngine
    {
        IReportExporter Resolve(ReportOutputFormat format);
        bool TryResolve(ReportOutputFormat format, out IReportExporter? exporter);
        IReadOnlyList<ReportOutputFormat> AvailableFormats { get; }

        Task<ReportArtifact> ExportAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default);
    }

    public class ReportExportEngine : IReportExportEngine
    {
        private readonly Dictionary<ReportOutputFormat, IReportExporter> _byFormat = new();

        public ReportExportEngine(IEnumerable<IReportExporter> exporters)
        {
            // Last-wins, same substitution mechanism as the renderer registry: a future engine that produces a
            // richer XLSX replaces ours with one DI line and no caller change.
            foreach (var exporter in exporters)
                foreach (var format in exporter.Formats)
                    _byFormat[format] = exporter;
        }

        public IReadOnlyList<ReportOutputFormat> AvailableFormats => _byFormat
            .Where(kv => kv.Value.IsAvailable)
            .Select(kv => kv.Key)
            .OrderBy(f => (int)f)
            .ToList();

        public IReportExporter Resolve(ReportOutputFormat format)
        {
            if (!_byFormat.TryGetValue(format, out var exporter))
                throw new ReportRendererNotRegisteredException(format);
            if (!exporter.IsAvailable)
                throw new ReportRendererUnavailableException(format,
                    exporter.UnavailableReason
                    ?? $"exporter '{exporter.EngineName}' reports itself unavailable in this deployment.");
            return exporter;
        }

        public bool TryResolve(ReportOutputFormat format, out IReportExporter? exporter)
        {
            exporter = null;
            if (!_byFormat.TryGetValue(format, out var found)) return false;
            if (!found.IsAvailable) return false;
            exporter = found;
            return true;
        }

        public Task<ReportArtifact> ExportAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default) =>
            Resolve(context.Format).ExportAsync(context, cancellationToken);
    }

    // ============================================================================================
    // THE OUTPUT PIPELINE — the one place a format becomes a producer.
    //
    // This is the type that makes the mandate true: the engine asks the pipeline for a format, the pipeline
    // decides whether that is a renderer's job or an exporter's, and no caller anywhere in the product names
    // either. Adding a StimulsoftRenderer for Pdf, or a PdfExporter, changes this file not at all.
    // ============================================================================================
    public interface IReportOutputPipeline
    {
        Task<ReportArtifact> ProduceAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default);

        // Formats that can actually be produced right now — the union of available renderers and exporters,
        // intersected with what the definition permits. Drives the download buttons a screen offers, so an
        // unbound PDF engine simply means no PDF button rather than a button that errors.
        IReadOnlyList<ReportOutputFormat> AvailableFormats(ReportDefinition definition);
    }

    public class ReportOutputPipeline : IReportOutputPipeline
    {
        private readonly IReportRendererRegistry _renderers;
        private readonly IReportExportEngine _exporters;

        public ReportOutputPipeline(IReportRendererRegistry renderers, IReportExportEngine exporters)
        {
            _renderers = renderers;
            _exporters = exporters;
        }

        public Task<ReportArtifact> ProduceAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default) =>
            ReportFormats.IsDocument(context.Format)
                ? _renderers.Resolve(context.Format).RenderAsync(context, cancellationToken)
                : _exporters.Resolve(context.Format).ExportAsync(context, cancellationToken);

        public IReadOnlyList<ReportOutputFormat> AvailableFormats(ReportDefinition definition) =>
            _renderers.AvailableFormats
                .Concat(_exporters.AvailableFormats)
                .Distinct()
                .Where(f => definition.Capabilities.SupportsFormat(f))
                .OrderBy(f => (int)f)
                .ToList();
    }
}