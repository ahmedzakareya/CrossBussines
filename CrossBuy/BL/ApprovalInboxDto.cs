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
        public string? Meta { get; set; }              // period / amount / type
        public DateTime? Date { get; set; }            // requested-at
        public string Url { get; set; } = "#";          // link to the silo screen to act
    }
}