using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Context.Reporting;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — the PUBLIC contract.
    //
    // A calling module knows exactly three types from this platform: ReportRequest, ReportResult and the
    // IReportService it hands the first to. Everything else — catalog, templates, parameter binding, data
    // sources, shaping, renderers, exporters, archive, history — is internal machinery reachable only through
    // that call.
    //
    // In particular NOTHING outside this assembly's Reporting namespace names a renderer. That is the mandate:
    // "the system must not know which renderer is used." A caller asks for a FORMAT; the platform decides which
    // renderer produces it, and a future StimulsoftRenderer changes no caller.
    // ============================================================================================

    // What the caller wants out. This is the ONLY renderer-selection input a caller has, and it is intentionally
    // an output *format*, not an output *engine*.
    public enum ReportOutputFormat
    {
        // Self-contained HTML fragment for embedding in a screen or an email body.
        Html = 0,

        // Paged PDF. Produced today by the Playwright renderer via IHtmlToPdfConverter; a future engine can
        // claim the same format with no caller change.
        Pdf = 1,

        // Excel workbook (data export, not a document).
        Xlsx = 2,

        // RFC 4180 text (data export).
        Csv = 3,

        // A complete HTML document carrying @page rules and print CSS, meant for the browser's print dialog or
        // a print transport. Distinct from Html because the two have different chrome: Html embeds, PrintHtml
        // paginates.
        PrintHtml = 4,
    }

    // Format facts in one place, so a content type or extension is never guessed at a call site.
    public static class ReportFormats
    {
        public static string ContentType(ReportOutputFormat format) => format switch
        {
            ReportOutputFormat.Html => "text/html; charset=utf-8",
            ReportOutputFormat.PrintHtml => "text/html; charset=utf-8",
            ReportOutputFormat.Pdf => "application/pdf",
            ReportOutputFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ReportOutputFormat.Csv => "text/csv; charset=utf-8",
            _ => "application/octet-stream",
        };

        public static string Extension(ReportOutputFormat format) => format switch
        {
            ReportOutputFormat.Html => "html",
            ReportOutputFormat.PrintHtml => "html",
            ReportOutputFormat.Pdf => "pdf",
            ReportOutputFormat.Xlsx => "xlsx",
            ReportOutputFormat.Csv => "csv",
            _ => "bin",
        };

        // true = a document meant to be looked at (paged, styled). false = a data file meant to be opened in a
        // tool. The pipeline branches on this to choose a renderer vs an exporter, and page setup only applies
        // to the document side.
        public static bool IsDocument(ReportOutputFormat format) =>
            format is ReportOutputFormat.Html or ReportOutputFormat.PrintHtml or ReportOutputFormat.Pdf;

        // Inline formats are returned as text for embedding rather than offered as a download.
        public static bool IsInline(ReportOutputFormat format) =>
            format is ReportOutputFormat.Html;

        public static bool TryParse(string? name, out ReportOutputFormat format) =>
            Enum.TryParse(name, ignoreCase: true, out format);
    }

    // ============================================================================================
    // THE REQUEST
    // ============================================================================================

    // Everything a caller may ask for. Note what is ABSENT and cannot be expressed:
    //   * no company id — isolation comes from the resolved BusinessContext, never from the caller
    //     (CLAUDE.md: "a request-supplied companyId is compatibility-only"; here it is not even accepted);
    //   * no employee id — the acting principal is the resolved one;
    //   * no renderer, no connection, no SQL, no file path.
    public sealed class ReportRequest
    {
        public required string ReportCode { get; init; }

        // null = resolve the caller's effective default template (Personal > Team > Company > Platform).
        // Set = this exact template, subject to the caller having access to it.
        public int? TemplateId { get; init; }

        // null = the template's current published version. Set = reproduce that historical version exactly.
        public int? TemplateVersionNo { get; init; }

        public ReportOutputFormat Format { get; init; } = ReportOutputFormat.Html;

        public ReportRunKind Kind { get; init; } = ReportRunKind.Full;

        // Raw text values, exactly as they arrive from a query string or a schedule's stored JSON. They go
        // through IReportParameterBinder, which is the single place text becomes typed — so there is one
        // validation path, not one per caller.
        public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

        // Ad-hoc query intent layered OVER the template. See ReportEngine for the precedence rule.
        public IReadOnlyList<ReportFilter> Filters { get; init; } = Array.Empty<ReportFilter>();
        public IReadOnlyList<ReportSort> Sorts { get; init; } = Array.Empty<ReportSort>();
        public IReadOnlyList<ReportGrouping> Groupings { get; init; } = Array.Empty<ReportGrouping>();

        // Empty = the template's / definition's visible set.
        public IReadOnlyList<string> VisibleColumns { get; init; } = Array.Empty<string>();

        // null = the template's page setup. Only meaningful for document formats.
        public ReportPageSetup? PageSetup { get; init; }

        // UI culture for labels and number/date formatting. null = the ambient CultureInfo.CurrentUICulture,
        // which is what the existing localisation middleware has already resolved for the request.
        public string? Culture { get; init; }

        // Store the artifact in the report archive and return its id on the result. Refused (with a diagnostic,
        // not an exception) when the definition's capabilities forbid archiving or the run is a Preview.
        public bool Archive { get; init; }

        // Caller-imposed row cap, further limited by the definition's MaxRows. Never raises the definition's cap.
        public int? MaxRows { get; init; }

        // Ties this run to the operation that asked for it. Flows to ReportRun.CorrelationId and to every
        // delivery attempt, so a scheduled report and the four emails it produced are one story in the log.
        public Guid? CorrelationId { get; init; }

        public static ReportRequest For(string reportCode, ReportOutputFormat format = ReportOutputFormat.Html) =>
            new() { ReportCode = reportCode, Format = format };

        // Canonical, order-independent hash of the parameter set. Recorded on the run so identical questions are
        // recognisable, and used by the archive to notice it has already produced this exact artifact.
        public static string HashParameters(IReadOnlyDictionary<string, string?> parameters)
        {
            var canonical = string.Join('\n', parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }

    // ============================================================================================
    // THE RESULT
    // ============================================================================================

    public enum ReportDiagnosticSeverity
    {
        Info = 0,

        // The report was produced, but something the caller asked for was not honoured (an unknown sort field
        // dropped, an archive request refused). A warning NEVER silently changes a number.
        Warning = 1,

        // The report was not produced.
        Error = 2,
    }

    // A machine-readable explanation. Code is stable and language-neutral; Message is for a log or a developer.
    // User-facing text is the CALLER's job through Resources — a BL service does not localise for a view it
    // cannot see. (Machine codes here follow the parallel team's module convention, e.g. "title_required".)
    public sealed class ReportDiagnostic
    {
        public required string Code { get; init; }
        public required string Message { get; init; }
        public ReportDiagnosticSeverity Severity { get; init; } = ReportDiagnosticSeverity.Error;

        // The parameter/column key the diagnostic is about, when it is about one.
        public string? Field { get; init; }

        public static ReportDiagnostic Error(string code, string message, string? field = null) =>
            new() { Code = code, Message = message, Field = field, Severity = ReportDiagnosticSeverity.Error };

        public static ReportDiagnostic Warning(string code, string message, string? field = null) =>
            new() { Code = code, Message = message, Field = field, Severity = ReportDiagnosticSeverity.Warning };

        public static ReportDiagnostic Info(string code, string message) =>
            new() { Code = code, Message = message, Severity = ReportDiagnosticSeverity.Info };

        public override string ToString() => $"{Severity}:{Code}{(Field == null ? "" : $"[{Field}]")} {Message}";
    }

    // The bytes plus everything needed to serve them. Content is a byte[] rather than a Stream because every
    // producer we have builds the whole artifact in memory anyway (ClosedXML, string building, a PDF buffer),
    // and a fake Stream over a completed buffer would only invite callers to forget to dispose it. Streaming
    // becomes an ADR when a report is big enough to need it.
    public sealed class ReportArtifact
    {
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required byte[] Content { get; init; }

        public long Length => Content.LongLength;

        // true = render it into the page; false = offer it as a download.
        public bool IsInline { get; init; }

        // SHA-256, lowercase hex. Content-addresses the artifact in the archive and lets a caller cheaply prove
        // two downloads are the same document.
        public string ContentHash { get; init; } = "";

        // Convenience for the inline HTML case, which is the common one on screen.
        public string AsText() => Encoding.UTF8.GetString(Content);

        public static ReportArtifact FromBytes(string fileName, ReportOutputFormat format, byte[] content) => new()
        {
            FileName = fileName,
            ContentType = ReportFormats.ContentType(format),
            Content = content,
            IsInline = ReportFormats.IsInline(format),
            ContentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        };

        public static ReportArtifact FromText(string fileName, ReportOutputFormat format, string text) =>
            FromBytes(fileName, format, Encoding.UTF8.GetBytes(text));
    }

    // What actually happened, in numbers. Mirrors the ReportRun row so a caller can show "1,204 rows in 380 ms"
    // without a second query.
    public sealed class ReportRunSummary
    {
        public long RunId { get; init; }
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public int? TemplateVersionNo { get; init; }
        public int DefinitionVersion { get; init; }
        public ReportRunKind Kind { get; init; }
        public int RowCount { get; init; }

        // true = the row cap was hit and rows are missing. Surfaced separately from RowCount because "1,000
        // rows" and "1,000 rows of an unknown larger number" are different answers.
        public bool Truncated { get; init; }

        public int DurationMs { get; init; }
        public long? ArchiveEntryId { get; init; }
        public DateTime StartedAt { get; init; }
        public string ParametersHash { get; init; } = "";
    }

    // The single return type of the platform.
    //
    // A failure is a RESULT, not an exception: "you may not run this report" and "your date range is backwards"
    // are ordinary answers a controller turns into a 403 or a validation message. Exceptions are reserved for
    // programming errors (an unregistered report code, a missing data source) — see the exceptions below.
    public sealed class ReportResult
    {
        public ReportRunStatus Status { get; init; }
        public required string ReportCode { get; init; }
        public ReportOutputFormat Format { get; init; }
        public ReportArtifact? Artifact { get; init; }
        public ReportRunSummary? Run { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public bool IsSuccess => Status == ReportRunStatus.Succeeded && Artifact != null;
        public bool IsDenied => Status == ReportRunStatus.Denied;

        public IEnumerable<ReportDiagnostic> Errors =>
            Diagnostics.Where(d => d.Severity == ReportDiagnosticSeverity.Error);

        public static ReportResult Success(string reportCode, ReportOutputFormat format, ReportArtifact artifact,
            ReportRunSummary run, IReadOnlyList<ReportDiagnostic>? diagnostics = null) => new()
            {
                Status = ReportRunStatus.Succeeded,
                ReportCode = reportCode,
                Format = format,
                Artifact = artifact,
                Run = run,
                Diagnostics = diagnostics ?? Array.Empty<ReportDiagnostic>(),
            };

        public static ReportResult Failed(string reportCode, ReportOutputFormat format,
            IReadOnlyList<ReportDiagnostic> diagnostics, ReportRunSummary? run = null) => new()
            {
                Status = ReportRunStatus.Failed,
                ReportCode = reportCode,
                Format = format,
                Diagnostics = diagnostics,
                Run = run,
            };

        public static ReportResult Denied(string reportCode, ReportOutputFormat format, string reasonCode,
            string reason) => new()
            {
                Status = ReportRunStatus.Denied,
                ReportCode = reportCode,
                Format = format,
                Diagnostics = new[] { ReportDiagnostic.Error(reasonCode, reason) },
            };
    }

    // ============================================================================================
    // EXCEPTIONS — programming errors only.
    //
    // Same reasoning as EntityCodeNotRegisteredException in the platform kernel: asking for a report that does
    // not exist, or one whose data source was never registered, is a wiring bug. Turning it into a friendly
    // empty result would let a broken deployment look like an empty report.
    // ============================================================================================

    public class ReportingException : InvalidOperationException
    {
        public ReportingException(string message) : base(message) { }
        public ReportingException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class ReportNotRegisteredException : ReportingException
    {
        public ReportNotRegisteredException(string? reportCode)
            : base($"Report code '{reportCode ?? "(null)"}' is not registered in IReportCatalog. " +
                   "Reports are code-first: register a definition through an IReportDefinitionProvider.")
        { ReportCode = reportCode; }

        public string? ReportCode { get; }
    }

    public sealed class ReportDataSourceNotRegisteredException : ReportingException
    {
        public ReportDataSourceNotRegisteredException(string? key, string? reportCode)
            : base($"No IReportDataSource is registered for key '{key ?? "(null)"}' " +
                   $"(required by report '{reportCode ?? "(unknown)"}'). Register one in DI.")
        { DataSourceKey = key; }

        public string? DataSourceKey { get; }
    }

    // No renderer/exporter claimed the requested format at all — a wiring error.
    public sealed class ReportRendererNotRegisteredException : ReportingException
    {
        public ReportRendererNotRegisteredException(ReportOutputFormat format)
            : base($"No IReportRenderer or IReportExporter is registered for format '{format}'.")
        { Format = format; }

        public ReportOutputFormat Format { get; }
    }

    // A renderer exists but cannot work in this deployment (the PDF renderer with no browser bound). Separate
    // from NotRegistered because the fix is different: configure the environment, not the code.
    public sealed class ReportRendererUnavailableException : ReportingException
    {
        public ReportRendererUnavailableException(ReportOutputFormat format, string reason)
            : base($"The renderer for format '{format}' is registered but unavailable: {reason}")
        { Format = format; }

        public ReportOutputFormat Format { get; }
    }
}