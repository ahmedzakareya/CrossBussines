namespace CrossBuy.BL
{
	// Data model for the HR / Administrative-system main dashboard (Views/Admin/Index.cshtml).
	// Built in AdminController.Index() from live data — mirrors the Accounting dashboard style.
	public class AdminDashboardDto
	{
		// ----- Employees -----
		public int TotalEmployees { get; set; }
		public int ActiveEmployees { get; set; }
		public int InactiveEmployees { get; set; }
		public int NewHiresThisYear { get; set; }
		public List<HrDistRow> ByJobTitle { get; set; } = new();   // headcount distribution (top N)

		// ----- Leave -----
		public int PendingLeaves { get; set; }
		public int ApprovedLeavesYtd { get; set; }
		public int OnLeaveToday { get; set; }
		public List<HrMonthPoint> LeaveMonthly { get; set; } = new();  // last 6 months
		public List<HrLeaveRow> RecentLeaves { get; set; } = new();

		// ----- Attendance (current month, company-wide totals) -----
		public int AttPresent { get; set; }
		public int AttAbsent { get; set; }
		public int AttLate { get; set; }
		public int AttLeave { get; set; }
		public int AttWorkDays { get; set; }

		// ----- Documents & holidays -----
		public int ExpiringSoonCount { get; set; }
		public int ExpiredDocs { get; set; }
		public List<HrExpiryRow> ExpiringDocs { get; set; } = new();
		public List<HrHolidayRow> UpcomingHolidays { get; set; } = new();

		public int Year { get; set; }
	}

	public class HrDistRow { public string? NameAr { get; set; } public string? NameEn { get; set; } public int Count { get; set; } public int Pct { get; set; } }
	public class HrMonthPoint { public int Year { get; set; } public int Month { get; set; } public int Count { get; set; } }
	public class HrLeaveRow { public string? EmployeeName { get; set; } public string? TypeAr { get; set; } public string? TypeEn { get; set; } public DateTime StartDate { get; set; } public DateTime EndDate { get; set; } public int Days { get; set; } public int Status { get; set; } }
	public class HrExpiryRow { public string? EmployeeName { get; set; } public string Label { get; set; } = ""; public DateTime Date { get; set; } public int DaysLeft { get; set; } public string Kind { get; set; } = ""; }
	public class HrHolidayRow { public string? NameAr { get; set; } public string? NameEn { get; set; } public DateTime Date { get; set; } }
}
