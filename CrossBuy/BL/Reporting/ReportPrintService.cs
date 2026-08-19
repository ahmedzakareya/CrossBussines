using CrossBuy.Models.Context.Reporting;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE PRINT SERVICE.
    //
    // Printing is NOT a format. It is a DESTINATION, and conflating the two is why report engines end up with a
    // "PrintPdf" format that nobody can explain. Here:
    //   * the DOCUMENT comes from the ordinary pipeline as PrintHtml (or Pdf, when a transport wants bytes);
    //   * the DESTINATION is an IReportPrintTransport, chosen by a printer-profile key.
    //
    // The default transport hands the document back to the BROWSER, which prints it. That is not a placeholder —
    // it is the correct default for a web application: the browser already knows the user's installed printers,
    // their paper trays and their permissions, and a server-side print queue would have to be told all three.
    // Server-side transports (a network printer, an ESC/POS thermal head for the hypermarket lane) implement the
    // same interface and change no caller.
    // ============================================================================================

    public enum ReportPrintStatus
    {
        // The document is ready and is being returned to the caller to print. The browser-transport outcome.
        Prepared = 0,

        // A transport accepted the document and will print it.
        Sent = 1,

        Failed = 2,
    }

    // A named print destination + how to lay out for it.
    //
    // A profile, not a printer name, because the thing a report needs to know is "receipt roll" vs "A4 office
    // printer" — the physical device is the transport's business. This is what lets one report print to both.
    public sealed class ReportPrinterProfile
    {
        public required string Key { get; init; }
        public required string NameAr { get; init; }
        public required string NameEn { get; init; }

        // Page geometry this destination imposes. Overrides the report's own page setup, because paper wins over
        // preference: an A4 layout sent to an 80 mm roll must be re-laid out, not scaled.
        public ReportPageSetup PageSetup { get; init; } = ReportPageSetup.Default;

        // Which transport handles it.
        public string TransportKey { get; init; } = ReportPrintTransports.Browser;

        // Transport-specific address (a printer queue name, a device path). Empty for the browser transport.
        public string Target { get; init; } = "";

        // The two profiles the platform ships. A4Office is the office default; Receipt80 is the hypermarket
        // thermal profile — narrow, no margins to speak of, no page numbers on a continuous roll.
        public static ReportPrinterProfile A4Office { get; } = new()
        {
            Key = "a4-office",
            NameAr = "طابعة مكتبية A4",
            NameEn = "A4 office printer",
            PageSetup = ReportPageSetup.Default,
        };

        public static ReportPrinterProfile Receipt80 { get; } = new()
        {
            Key = "receipt-80",
            NameAr = "طابعة إيصالات 80 مم",
            NameEn = "80 mm receipt printer",
            PageSetup = new ReportPageSetup
            {
                PageSize = ReportPageSize.Thermal80,
                MarginTopMm = 3,
                MarginBottomMm = 3,
                MarginLeftMm = 2,
                MarginRightMm = 2,
                ShowPageNumbers = false,   // a continuous roll has no page count
                RepeatHeaderRow = false,
            },
        };

        public string Name(bool arabic) => arabic ? NameAr : NameEn;
    }

    public static class ReportPrintTransports
    {
        // Return the document to the client; the browser's print dialog does the printing.
        public const string Browser = "browser";
    }

    // One print job.
    public sealed class ReportPrintJob
    {
        public required Guid JobId { get; init; }
        public required string ReportCode { get; init; }
        public required ReportPrinterProfile Profile { get; init; }
        public int Copies { get; init; } = 1;
        public required ReportArtifact Artifact { get; init; }
        public int? RequestedByEmployeeId { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    public sealed class ReportPrintResult
    {
        public ReportPrintStatus Status { get; init; }
        public ReportPrintJob? Job { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        // Convenience for the common browser case: the HTML the client should print.
        public string? Document => Job?.Artifact.IsInline == true || Job?.Artifact.ContentType.StartsWith("text/html") == true
            ? Job.Artifact.AsText()
            : null;

        public bool IsSuccess => Status is ReportPrintStatus.Prepared or ReportPrintStatus.Sent;
    }

    // A print destination. Implementations may be added freely; none of them changes a caller.
    public interface IReportPrintTransport
    {
        string TransportKey { get; }

        // The format this transport wants. The browser transport wants PrintHtml; a network-printer transport
        // would want Pdf; an ESC/POS head would want its own format and would be added as a new
        // renderer + transport pair.
        ReportOutputFormat PreferredFormat { get; }

        bool IsAvailable { get; }

        Task<ReportPrintResult> SendAsync(ReportPrintJob job, CancellationToken cancellationToken = default);
    }

    // The default: don't print — hand the document back and let the browser print it.
    public sealed class BrowserPrintTransport : IReportPrintTransport
    {
        public string TransportKey => ReportPrintTransports.Browser;

        public ReportOutputFormat PreferredFormat => ReportOutputFormat.PrintHtml;

        public bool IsAvailable => true;

        public Task<ReportPrintResult> SendAsync(ReportPrintJob job, CancellationToken cancellationToken = default) =>
            // Prepared, not Sent. The distinction is honest reporting: nothing has printed yet, and a caller that
            // logged "Sent" here would be logging something that had not happened.
            Task.FromResult(new ReportPrintResult { Status = ReportPrintStatus.Prepared, Job = job });
    }

    public interface IReportPrintService
    {
        IReadOnlyList<ReportPrinterProfile> GetProfiles();

        // Generates the report for a print destination and hands it to the profile's transport.
        //
        // The profile's page setup REPLACES the request's — paper wins over preference. Everything else about the
        // request (parameters, filters, template) is honoured unchanged, so "print this" prints what is on screen.
        Task<ReportPrintResult> PrintAsync(ReportRequest request, string? printerProfileKey = null,
            int copies = 1, CancellationToken cancellationToken = default);
    }

    public class ReportPrintService : IReportPrintService
    {
        private readonly IReportService _reports;
        private readonly IReadOnlyList<ReportPrinterProfile> _profiles;
        private readonly Dictionary<string, IReportPrintTransport> _transports;
        private readonly IReportClock _clock;

        public ReportPrintService(IReportService reports, IEnumerable<IReportPrintTransport> transports,
            IReportClock clock)
        {
            _reports = reports;
            _clock = clock;
            _transports = transports.ToDictionary(t => t.TransportKey, StringComparer.OrdinalIgnoreCase);

            // Shipped profiles. A configurable per-company profile list is a later slice — it needs a table and a
            // management screen, and inventing one here would be building UI in an architecture slice.
            _profiles = new[] { ReportPrinterProfile.A4Office, ReportPrinterProfile.Receipt80 };
        }

        public IReadOnlyList<ReportPrinterProfile> GetProfiles() => _profiles;

        public async Task<ReportPrintResult> PrintAsync(ReportRequest request, string? printerProfileKey = null,
            int copies = 1, CancellationToken cancellationToken = default)
        {
            var profile = _profiles.FirstOrDefault(p =>
                              string.Equals(p.Key, printerProfileKey, StringComparison.OrdinalIgnoreCase))
                          ?? ReportPrinterProfile.A4Office;

            if (!_transports.TryGetValue(profile.TransportKey, out var transport) || !transport.IsAvailable)
                return new ReportPrintResult
                {
                    Status = ReportPrintStatus.Failed,
                    Diagnostics = new[]
                    {
                        ReportDiagnostic.Error("print_transport_unavailable",
                            $"Print transport '{profile.TransportKey}' for profile '{profile.Key}' is not " +
                            "registered or not available in this deployment."),
                    },
                };

            var printRequest = new ReportRequest
            {
                ReportCode = request.ReportCode,
                TemplateId = request.TemplateId,
                TemplateVersionNo = request.TemplateVersionNo,
                Format = transport.PreferredFormat,
                Kind = request.Kind == ReportRunKind.Preview ? ReportRunKind.Full : request.Kind,
                Parameters = request.Parameters,
                Filters = request.Filters,
                Sorts = request.Sorts,
                Groupings = request.Groupings,
                VisibleColumns = request.VisibleColumns,
                PageSetup = profile.PageSetup,     // paper wins
                Culture = request.Culture,
                Archive = request.Archive,
                MaxRows = request.MaxRows,
                CorrelationId = request.CorrelationId,
            };

            // Printing goes through the SAME façade as everything else — IReportService.GenerateAsync. The print
            // service has no private path to a renderer, so authorization, history and archiving all still happen.
            var result = await _reports.GenerateAsync(printRequest, cancellationToken);

            if (!result.IsSuccess || result.Artifact == null)
                return new ReportPrintResult
                {
                    Status = ReportPrintStatus.Failed,
                    Diagnostics = result.Diagnostics,
                };

            var job = new ReportPrintJob
            {
                JobId = Guid.NewGuid(),
                ReportCode = request.ReportCode,
                Profile = profile,
                Copies = copies < 1 ? 1 : copies,
                Artifact = result.Artifact,
                CreatedAt = _clock.LocalNow,
            };

            return await transport.SendAsync(job, cancellationToken);
        }
    }
}