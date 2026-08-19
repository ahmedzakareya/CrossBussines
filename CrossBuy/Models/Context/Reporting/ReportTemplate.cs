using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // Reporting Platform (ADR-037) — the four template scopes, in RESOLUTION PRECEDENCE order.
    //
    // The numeric values are the precedence: a higher value wins when two templates are both eligible for the
    // same (report, employee). That is why they are explicit rather than implicit — the ordering IS the
    // business rule (a person's own layout beats their team's, which beats the company's, which beats the
    // one the platform shipped), and an accidental reorder would silently change which layout a user sees.
    public enum ReportTemplateScope
    {
        // Shipped with the product. CompanyID = 0 (belongs to no company) and no owner. Read-only to tenants:
        // a user "editing" a platform template actually forks it into a Company/Personal template.
        Platform = 0,

        // The company standard, maintained by whoever holds report-manage rights in that company.
        Company = 1,

        // Owned by an administrative unit (department/team). TeamId names it.
        Team = 2,

        // One employee's private layout. OwnerEmpId names them; nobody else resolves it.
        Personal = 3,
    }

    // A named, versioned layout over one catalog report.
    //
    // A template NEVER carries data or SQL — only presentation and query intent (visible columns, saved
    // filters/sorts/groups, page setup). The report's data contract lives in the code-first catalog
    // (IReportCatalog), so a template can never widen what a report may read. That split is what lets a
    // Personal template be user-editable without becoming an authorization hole.
    public class ReportTemplate : BaseEntity
    {
        public int Id { get; set; }

        // 0 for Scope = Platform (a platform template belongs to no tenant). Every other scope carries the
        // owning company. Reads are ALWAYS (CompanyID == ctx.CompanyId || CompanyID == 0), never unfiltered.
        public int CompanyID { get; set; }

        // Frozen catalog code, e.g. "Platform.ReportInventory". Validated against IReportCatalog on save —
        // a template for an unregistered report is a programming error, not a runtime condition.
        public string ReportCode { get; set; } = "";

        public string Name { get; set; } = "";
        public string? NameEn { get; set; }          // English twin — shown when UI language != Arabic

        public ReportTemplateScope Scope { get; set; } = ReportTemplateScope.Personal;

        public int? OwnerEmpId { get; set; }         // required for Personal; the maintainer for Company/Team
        public int? TeamId { get; set; }             // required for Team — an administrative-structure unit id

        public int? CategoryId { get; set; }         // ReportCategories.Id, optional

        // Points at the ReportTemplateVersions row users get. Versions are immutable once published, so this
        // is the only mutable pointer — a rollback is "move this back", never "edit history".
        public int CurrentVersionNo { get; set; }

        // Within a (CompanyID, ReportCode, Scope, owner) group, at most one default. Enforced by the service,
        // not the schema, because "at most one per group" over a soft-deleted table needs a filtered index per
        // scope shape and the service already owns the invariant.
        public bool IsDefault { get; set; }

        public DateTime? DeletedAt { get; set; }
    }

    // One immutable published revision of a template's layout.
    //
    // Immutability is the point of the table: an archived PDF from March must be reproducible in December, so
    // the archive entry records (TemplateId, VersionNo) and this row may never change after IsPublished. Edits
    // append a new VersionNo. This is the reporting analogue of our "reverse, never delete" rule — history
    // stays whole.
    public class ReportTemplateVersion : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }           // denormalised from the template so every read filters on it
        public int TemplateId { get; set; }

        // 1-based, monotonic per template. (TemplateId, VersionNo) is unique.
        public int VersionNo { get; set; }

        // Serialized ReportLayout (System.Text.Json). Presentation + query intent only — see ReportLayout.
        public string LayoutJson { get; set; } = "";

        // SHA-256 of LayoutJson. Lets the service skip publishing a version identical to the current one, so
        // an idempotent "save" does not inflate the version history.
        public string ContentHash { get; set; } = "";

        public string? ChangeNote { get; set; }

        // false = draft (still editable). true = published (frozen forever).
        public bool IsPublished { get; set; }
        public DateTime? PublishedAt { get; set; }
        public int? PublishedBy { get; set; }
    }
}