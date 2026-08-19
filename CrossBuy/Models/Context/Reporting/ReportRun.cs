using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // Why a report was generated. The engine treats these differently, so it is stored, not inferred.
    public enum ReportRunKind
    {
        // A user asked for the finished document.
        Full = 0,

        // A capped, non-archivable render for the on-screen preview pane. Never delivered, never archived,
        // and reported separately in history so a preview cannot be mistaken for the document someone signed.
        Preview = 1,

        // Produced by the scheduler on someone's behalf.
        Scheduled = 2,
    }

    public enum ReportRunStatus
    {
        Succeeded = 0,

        // The pipeline ran and failed (bad parameters, data source error, renderer error). Diagnostics say why.
        Failed = 1,

        // Authorization refused before any data was read. Recorded as a run on purpose: a refused report is
        // exactly the event an auditor wants to see, and dropping it would make the history a success log.
        Denied = 2,

        Cancelled = 3,
    }

    // One line of report history: who generated what, with which parameters, and what came out.
    //
    // This is an append-only audit trail — no row is ever updated after CompletedAt and none is deleted (there
    // is no DeletedAt). Retention is by RetainUntil on the ARCHIVE entry, which holds the bytes; the history
    // line itself is small and kept.
    public class ReportRun : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }

        public string ReportCode { get; set; } = "";
        public int? TemplateId { get; set; }
        public int? TemplateVersionNo { get; set; }   // the EXACT version rendered — makes a run reproducible

        public int? EmployeeId { get; set; }          // null only for a Scheduled run with no acting employee
        public ReportRunKind Kind { get; set; }
        public ReportRunStatus Status { get; set; }

        // Stored as the short output-format name (Html/Pdf/Xlsx/Csv/PrintHtml) rather than an int, because
        // history is read by operators and a format name in a grid should not need a lookup table.
        public string Format { get; set; } = "";

        // The bound parameter set, serialized. Kept for reproduction and for "who ran this for December".
        public string? ParametersJson { get; set; }

        // SHA-256 over the canonical (ordered) parameter set. Two runs with the same hash asked the same
        // question — the cheap way to spot the same heavy report being run forty times in an hour.
        public string? ParametersHash { get; set; }

        public int RowCount { get; set; }
        public int DurationMs { get; set; }
        public long? ArchiveEntryId { get; set; }     // set when the run was archived

        public string? ErrorCode { get; set; }        // machine code, e.g. "parameter_invalid", "renderer_unavailable"
        public string? ErrorMessage { get; set; }

        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public Guid? CorrelationId { get; set; }      // ties a scheduled run to its delivery attempts
    }
}