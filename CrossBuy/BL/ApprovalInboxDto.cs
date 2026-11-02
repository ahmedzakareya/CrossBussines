namespace CrossBuy.BL
{
    // One pending-approval row in the unified "My approvals" inbox — a READ-MODEL union over the
    // existing silos (leave · employee-request · inventory). Each row links to its silo screen to act.
    public class ApprovalInboxRow
    {
        public string Silo { get; set; } = "";        // Leave | Request | Inventory
        public string SiloLabel { get; set; } = "";
        public string Icon { get; set; } = "ki-check-square";
        public string Color { get; set; } = "primary";
        public string Title { get; set; } = "";        // main line (what is being approved)
        public string? Requester { get; set; }         // who asked

        // THE REQUESTER'S FACE, and why the id has to travel with the name.
        //
        // PendingApprovalRow already carries RequesterEmployeeId; this row dropped it and kept only the
        // formatted name, so the screen that names nine people could not show one of them. That is the
        // usual shape of this defect - the chain stops at the read-model, not at the query - and it is
        // why the id is carried here rather than looked up again from the view.
        //
        // Photo is a WEB PATH resolved by BL/Platform/EmployeePhotos, which scopes the lookup to the
        // caller's company and normalises the stored column. Null means "no photograph for this person
        // in this company", NOT "no such employee": the view falls back to initials either way, so a
        // requester from outside the company is never distinguishable from one who simply has no photo.
        public int? RequesterEmployeeId { get; set; }
        public string? RequesterPhoto { get; set; }
        public string? Meta { get; set; }              // period / amount / type
        public DateTime? Date { get; set; }            // requested-at
        public string Url { get; set; } = "#";          // link to the silo screen to act
    }
}