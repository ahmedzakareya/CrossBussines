namespace CrossBuy.Models.Context.Comm
{
    // Communication Hub P5 — company/branch announcements. Broadcast, dismissible, time-boxed.
    public class Announcement : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public int? BranchID { get; set; }              // when Scope == Branch
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Priority { get; set; } = "Normal"; // Normal | High | Critical
        public string Scope { get; set; } = "Company";   // Company | Branch
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; } = true;
    }

    // Per-employee dismissal (so a banner disappears once acknowledged).
    public class AnnouncementRead
    {
        public int Id { get; set; }
        public int AnnouncementId { get; set; }
        public int EmployeeId { get; set; }
        public DateTime ReadAt { get; set; }
    }
}
