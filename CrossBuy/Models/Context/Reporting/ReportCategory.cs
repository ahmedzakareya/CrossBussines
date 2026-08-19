using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // A folder in the report browser. Hierarchical (ParentId == null = root), company-scoped.
    //
    // Two population sources coexist on purpose:
    //   * PLATFORM categories (CompanyID = 0, IsSystem = true) come from the code-first catalog's CategoryKey,
    //     so a freshly deployed database already has "Accounting", "Inventory", "Sales" without a seed step.
    //   * COMPANY categories (CompanyID = tenant) are user-created and freely renamed/reordered.
    //
    // Key is the stable machine identifier a catalog definition points at; Name/NameEn are display-only and
    // may be edited without breaking any definition.
    public class ReportCategory : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }           // 0 = platform category, visible to every company

        // Stable machine key, e.g. "accounting.financial-statements". Unique per (CompanyID, Key).
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public string? NameEn { get; set; }

        public int? ParentId { get; set; }           // null = root
        public int SortOrder { get; set; }
        public string? Icon { get; set; }            // Metronic KI icon class, e.g. "ki-outline ki-chart-simple"

        // true = came from the catalog, not from a user. A system category may not be renamed or deleted by a
        // tenant; that is what stops a catalog definition from pointing at a category that no longer exists.
        public bool IsSystem { get; set; }

        public DateTime? DeletedAt { get; set; }
    }
}