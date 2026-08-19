using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // Who a share grant is addressed to.
    public enum ReportPrincipalType
    {
        Employee = 0,
        Role = 1,
        Team = 2,       // an administrative-structure unit
        Company = 3,    // everyone in the owning company
    }

    // What a share grant allows. Ordered — a higher value implies every lower one.
    public enum ReportAccessLevel
    {
        None = 0,
        View = 1,     // see it exists in the catalog/browser
        Run = 2,      // generate it (View + execute)
        Edit = 3,     // publish new template versions (Run + modify layout)
        Manage = 4,   // Edit + re-share + transfer ownership + delete
    }

    // A grant of access to a report or one of its templates.
    //
    // This table is the reporting platform's OWN access model. It is additive and self-contained: it can only
    // ever GRANT reporting rights on top of whatever the platform's module authorization already allowed —
    // IReportAuthorizationService checks the module permission FIRST and a share can never override a denial.
    // That ordering is what keeps report sharing from becoming a back door around module permissions.
    public class ReportShare : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }

        public string ReportCode { get; set; } = "";
        public int? TemplateId { get; set; }         // null = the report itself (any resolved template)

        public ReportPrincipalType PrincipalType { get; set; }

        // Employee → employee id as text; Role → role name; Team → unit id as text; Company → "" (unused).
        // Text rather than a typed column because the four principal kinds have different id types and a
        // discriminated column keeps the grant table one table instead of four.
        public string PrincipalKey { get; set; } = "";

        public ReportAccessLevel AccessLevel { get; set; } = ReportAccessLevel.Run;

        // null = no expiry. A past ExpiresAt is treated as no grant (evaluated at read time, never by a job —
        // an expiry that depends on a sweeper is an expiry that silently keeps working when the sweeper stops).
        public DateTime? ExpiresAt { get; set; }

        public int? GrantedByEmpId { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}