using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // A free-form label a company puts on reports and templates.
    //
    // Deliberately NOT the same concept as ReportCategory: a report sits in exactly one category (a folder)
    // but carries any number of tags (cross-cutting: "month-end", "board pack", "VAT"). Keeping them separate
    // is what lets the category tree stay a stable navigation structure while tagging stays disposable.
    public class ReportTag : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }

        // Normalised (trimmed, case-folded) label. Unique per company among non-deleted rows.
        public string Name { get; set; } = "";
        public string? NameEn { get; set; }

        // Metronic contextual token (primary | success | warning | danger | info | dark) — presentation only.
        public string ColorToken { get; set; } = "primary";

        public DateTime? DeletedAt { get; set; }
    }

    // Tag → target link. The target is either a catalog report (ReportCode, TemplateId = null) or one specific
    // template (both set). A link row rather than a delimited column so "list everything tagged month-end"
    // stays an index seek instead of a LIKE scan.
    public class ReportTagLink : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public int TagId { get; set; }
        public string ReportCode { get; set; } = "";
        public int? TemplateId { get; set; }         // null = the tag applies to the report itself
    }
}