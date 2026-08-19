using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // "Pin this report to my sidebar." One row per (company, employee, report, template).
    //
    // Per-EMPLOYEE, not per-user-account: everything else in the reporting platform keys off EmployeeId
    // (ownership, sharing, schedules), and mixing the two identities is how a favourite would survive a user
    // being re-linked to a different employee.
    public class ReportFavorite : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public int EmployeeId { get; set; }
        public string ReportCode { get; set; } = "";

        // null = the report with its resolved default template. Set = this exact template.
        public int? TemplateId { get; set; }

        public int SortOrder { get; set; }

        // Favourites are hard-deleted (unpinning is not history worth keeping), so there is no DeletedAt here.
        // The uniqueness index can therefore be plain, not filtered.
    }
}