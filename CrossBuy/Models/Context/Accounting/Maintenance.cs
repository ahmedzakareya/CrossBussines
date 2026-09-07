namespace CrossBuy.Models.Context.Accounting
{
	// Asset scheduled maintenance (Level-1). A Schedule is the recurring plan per asset; a Record is a performed
	// maintenance. Maintenance is OPEX — an optional GL posting (Dr 520110 / Cr cash-or-payable) inherits the
	// asset's cost center (+ optional project). Never touches depreciation or asset carrying value.
	public class MaintenanceSchedule
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int AssetId { get; set; }
		public string Title { get; set; } = "";
		// English twin of the column above. Nullable and never required: read it through
		// DisplayName.Or(<En>, <Ar>) so a row that never got one still shows a name.
		public string? TitleEn { get; set; }
		public string Type { get; set; } = "Preventive";   // Preventive | Inspection | Calibration | Repair
		public int IntervalMonths { get; set; } = 1;
		public DateTime NextDueDate { get; set; }
		public DateTime? LastDoneDate { get; set; }
		public decimal? EstimatedCost { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	public class MaintenanceRecord
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int AssetId { get; set; }
		public int? ScheduleId { get; set; }
		public DateTime Date { get; set; }
		public string? Description { get; set; }
		public decimal Cost { get; set; }
		public string? Vendor { get; set; }
		public string Status { get; set; } = "Done";        // Planned | Done
		public string? Notes { get; set; }
		public int? JournalEntryId { get; set; }             // set when the cost was posted to GL
		public int? ProjectId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
