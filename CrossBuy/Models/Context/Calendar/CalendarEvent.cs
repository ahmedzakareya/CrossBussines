namespace CrossBuy.Models.Context.Calendar
{
    // Company calendar event. Visibility: owner sees own; Company-scope seen by everyone in the company;
    // invited attendees also see it. One row per event; attendees in a child table.
    public class CalendarEvent : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Location { get; set; }
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
