namespace CrossBuy.Models.Context.Inventory
{
	// Sales-rep routes and visits (deploy/sql/sales_rep_routes.sql) — "زيارات اليوم" in the owner's mobile prototype.

	/// A rep's standing plan: the customers to visit in order, on which weekdays, and the sales to bring in a day.
	public class SalesRepRoute
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int RepEmployeeID { get; set; }
		public decimal DailyTarget { get; set; }
		public bool IsActive { get; set; } = true;
		public string? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
		public DateTime? UpdatedAt { get; set; }
		[System.ComponentModel.DataAnnotations.Schema.ForeignKey(nameof(SalesRepRouteStop.RouteID))]   // the column is RouteID, not EF's SalesRepRouteID
		public ICollection<SalesRepRouteStop> Stops { get; set; } = new List<SalesRepRouteStop>();
	}

	/// One customer on a route: its place in the order, the planned time, and the weekdays it applies to.
	public class SalesRepRouteStop
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int RouteID { get; set; }
		public int CustomerID { get; set; }
		public int Seq { get; set; }
		public TimeSpan? PlannedTime { get; set; }
		/// Bit 0 = Sunday … bit 6 = Saturday; 127 = every day.
		public int DaysMask { get; set; } = 127;
		public bool OnDay(DayOfWeek d) => (DaysMask & (1 << (int)d)) != 0;
	}

	/// One stop on one day, and what happened at it.
	public class SalesRepVisit
	{
		public const string Planned = "Planned", Arrived = "Arrived", Done = "Done", Skipped = "Skipped";

		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int? RouteID { get; set; }
		public int RepEmployeeID { get; set; }
		public int CustomerID { get; set; }
		public DateTime VisitDate { get; set; }
		public TimeSpan? PlannedTime { get; set; }
		public int Seq { get; set; }
		public string Status { get; set; } = Planned;
		public DateTime? ArrivedAt { get; set; }
		public DateTime? CompletedAt { get; set; }
		public int? SalesOrderID { get; set; }
		/// The value of the order taken at this visit — the day's achievement against the route's target is their sum.
		public decimal OrderTotal { get; set; }
		public decimal Collected { get; set; }
		public string? Outcome { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	}
}
