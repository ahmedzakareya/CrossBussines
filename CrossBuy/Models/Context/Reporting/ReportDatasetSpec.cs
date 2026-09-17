namespace CrossBuy.Models.Context.Reporting
{
    // ============================================================================================
    // A DERIVED DATASET, AS STORED — the narrowing an author saved, not a dataset in itself.
    //
    // WHAT THIS ROW IS AND IS NOT. It is a PROJECTION over a dataset that exists in code: the parent's
    // code, a new name, and the subset the author kept. It is not a query, it names no table, and it
    // carries neither a data-source key nor a permission key — because ReportDerivedDatasetSpec, the
    // contract this row stores, has no property for either. Both are inherited from the parent at build
    // time, which is what makes "a derived dataset cannot reach what its parent could not" provable
    // rather than promised.
    //
    // COMPANY-SCOPED, WITHOUT EXCEPTION. A platform-wide derived dataset would mean an author in one
    // company changing what another company sees, which is the boundary the whole product is built on.
    // A genuinely shared dataset is written in C# and reviewed, as it is today.
    //
    // WHY THE PROJECTION IS JSON. It is a document — an ordered field list with per-field presentation,
    // plus baked filters. Relationally it would be two more tables that are only ever read whole, joined
    // on every registry build, and never queried by their own columns. ReportTemplateVersions already
    // stores its layout the same way and for the same reason.
    //
    // SOFT DELETE, like every other Reporting table: a retired derivation stays readable, because saved
    // Studio reports may still reference its code and "this report's dataset vanished" has to remain a
    // statement the platform can make.
    // ============================================================================================
    public class ReportDatasetSpec
    {
        public int Id { get; set; }

        /// The tenant. Set from the resolved BusinessContext at save; never from a request.
        public int CompanyID { get; set; }

        /// The NEW dataset's code, unique within the company and never equal to a code-authored one —
        /// shadowing a platform dataset would replace its permission key, so the builder refuses it.
        public string DatasetCode { get; set; } = "";

        /// The dataset this one narrows. The single source of everything security-bearing.
        public string ParentDatasetCode { get; set; } = "";

        public string TitleAr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string? DescriptionAr { get; set; }
        public string? DescriptionEn { get; set; }

        /// The serialised ReportDerivedDatasetSpec. Rebuilt through ReportDerivedDatasetBuilder on every
        /// load, so a row written under yesterday's rules is re-checked against today's — the same
        /// re-validation a saved visual layout gets when it is reopened.
        public string SpecJson { get; set; } = "";

        public bool IsActive { get; set; } = true;
        public DateTime? DeletedAt { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
