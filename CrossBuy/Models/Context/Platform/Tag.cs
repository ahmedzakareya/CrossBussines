namespace CrossBuy.Models.Context.Platform
{
    // =============================================================================================
    // PLATFORM TAGS — a shared vocabulary, deliberately NOT a Task feature.
    //
    // WHY THIS LIVES IN Models/Context/Platform AND NOT UNDER Tasks. Task Management needs tags
    // first, but CRM needs the same tags next, and the requirement is explicit that CRM must not end
    // up building CrmTag / CrmLeadTag beside this. So the DEFINITION is company-scoped platform data
    // and the ASSIGNMENT is polymorphic (EntityType + EntityId) from the outset. Nothing here names
    // Task, and adding CRM later is a permission and query change, not a schema migration.
    //
    // The polymorphic pair is the same shape TaskItem, BusinessEvent and AiProjection already use for
    // source linkage, so it is the repository's existing idiom rather than a new one.
    //
    // DELIBERATELY ABSENT: colour, icon, ordering, hierarchy, per-user favourites, usage counters.
    // None is required by the current product ask, and every one of them is a column that would have
    // to be migrated for CRM too. A tag is a name.
    // =============================================================================================

    /// A tag DEFINITION, owned by one company.
    public class Tag : BaseEntity
    {
        public int ID { get; set; }

        /// Tenancy. Company 41 and company 77 may each have "Urgent" and they are different tags.
        public int CompanyID { get; set; }

        /// What the user typed and what the user sees. Business data — never translated, because a
        /// tag is a value the tenant chose, not a UI label.
        public string Name { get; set; } = "";

        /// The comparison key. Uniqueness is per company on THIS, not on Name, so "Urgent",
        /// " urgent " and "URGENT" cannot become three tags that mean the same thing. Written only
        /// by TagService.Normalize so the database and the application cannot disagree about it.
        public string NormalizedName { get; set; } = "";

        /// ARCHIVED RATHER THAN DELETED once a tag has been used anywhere.
        ///
        /// The reason is history: an assignment records that somebody classified a task this way, and
        /// hard-deleting the definition would either orphan that row or silently rewrite the past. An
        /// UNUSED tag is a typo and may be deleted outright — that distinction is TagService's, and it
        /// is the platform's existing "supersede, don't destroy" habit (BootstrapAccessPolicy keeps
        /// superseded rows for exactly this reason).
        public bool IsActive { get; set; } = true;
    }

    /// One tag on one entity. The polymorphic half.
    public class TagAssignment : BaseEntity
    {
        public long ID { get; set; }

        /// Denormalised ON PURPOSE. It could be read through TagId, but this row is written for a
        /// Task/Lead/Opportunity whose company must be checked against the tag's, and carrying the
        /// company here is what lets the global query filter and the write guard see this row at all
        /// without a join. TagService refuses an assignment whose three companies do not agree.
        public int CompanyID { get; set; }

        public int TagId { get; set; }

        /// A registered entity code — "Task" today; CRM codes later. Never free text from a request.
        public string EntityType { get; set; } = "";

        public int EntityId { get; set; }
    }
}
