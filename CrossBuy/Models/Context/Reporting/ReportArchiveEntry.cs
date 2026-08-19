using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // A stored, immutable report artifact — the bytes someone can come back to.
    //
    // The row is metadata; the bytes live wherever IReportArchiveStore puts them (filesystem by default).
    // Splitting them is what lets the store be swapped for blob storage later without touching this table.
    //
    // IMMUTABLE: no field is updated after insert except DeletedAt (retention expiry). An archived artifact is
    // the evidence that a document was produced with a given template version and parameters; rewriting it
    // would destroy the only reason to archive.
    public class ReportArchiveEntry : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }

        public string ReportCode { get; set; } = "";
        public int? TemplateId { get; set; }
        public int? TemplateVersionNo { get; set; }
        public long? RunId { get; set; }              // the history line that produced it

        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Length { get; set; }

        // SHA-256 of the bytes, lowercase hex. The store is content-addressed by this value, so re-archiving
        // an identical artifact reuses the stored file instead of writing a second copy.
        public string ContentHash { get; set; } = "";

        // Opaque to callers — only the store that wrote it interprets it (a relative path today).
        public string StoredPath { get; set; } = "";

        // null = keep indefinitely. A retention sweep is a LATER slice and is deliberately not implemented
        // here: writing a deleter before the archive has any content is how retention bugs get shipped.
        public DateTime? RetainUntil { get; set; }

        public DateTime? DeletedAt { get; set; }
    }
}