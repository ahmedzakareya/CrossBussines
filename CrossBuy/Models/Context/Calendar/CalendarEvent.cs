namespace CrossBuy.Models.Context.Calendar
{
    // Company calendar event. Visibility: owner sees own; Company-scope seen by everyone in the company;
    // invited attendees also see it. One row per event; attendees in a child table.
    public class CalendarEvent : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public string Title { get; set; } = "";
        // English twin. Null = none recorded, and the Arabic title is shown in both languages - the same
        // fallback TaskItems.TitleEn and TaskTemplates.NameEn use.
        public string? TitleEn { get; set; }
        public string? Description { get; set; }
        public string? DescriptionEn { get; set; }      // English twin — shown when the UI is not Arabic
        public string? Location { get; set; }
        public string? LocationEn { get; set; }         // English twin — shown when the UI is not Arabic
        public bool AllDay { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime? EndAt { get; set; }
        public string Scope { get; set; } = "Personal";   // Personal | Company
        public int OwnerEmpId { get; set; }                // creator/owner employee id
        public DateTime? DeletedAt { get; set; }
    }

    public class CalendarEventAttendee
    {
        public int Id { get; set; }
        public int EventId { get; set; }
        public int EmployeeId { get; set; }
    }
}
