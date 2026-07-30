namespace CrossBuy.ViewModel.Ai
{
	// DTOs matching the Python AI service JSON (deserialized case-insensitively).

	public class AiReason { public string Ar { get; set; } = ""; public string En { get; set; } = ""; }

	// ----- anomaly -----
	public class AnomalyResult
	{
		public int Scanned { get; set; }
		public int AnomalyCount { get; set; }
		public List<AnomalyItem> Anomalies { get; set; } = new();
		public AnomalySummary Summary { get; set; } = new();
	}
	public class AnomalyItem
	{
		public int Id { get; set; }
		public string? EntryNo { get; set; }
		public string? Date { get; set; }
		public string? SourceType { get; set; }
		public decimal Amount { get; set; }
		public double Score { get; set; }
		public string? Severity { get; set; }   // high / medium / low
		public List<AiReason> Reasons { get; set; } = new();
	}
	public class AnomalySummary { public int Duplicates { get; set; } public int AmountOutliers { get; set; } }

	// ----- cash-flow -----
	public class CashflowResult
	{
		public string? AsOf { get; set; }
		public int HorizonDays { get; set; }
		public decimal OpeningCash { get; set; }
		public decimal TotalExpectedInflow { get; set; }
		public decimal TotalExpectedOutflow { get; set; }
		public decimal ProjectedEndBalance { get; set; }
		public decimal MinProjectedBalance { get; set; }
		public string? MinBalanceWeek { get; set; }
		public bool NegativeRisk { get; set; }
		public List<CashflowPeriod> Periods { get; set; } = new();
	}
	public class CashflowPeriod
	{
		public string? WeekStart { get; set; }
		public decimal Inflow { get; set; }
		public decimal Outflow { get; set; }
		public decimal Net { get; set; }
		public decimal ProjectedBalance { get; set; }
	}

	// ----- inventory -----
	public class InventoryResult
	{
		public int ItemsAnalyzed { get; set; }
		public int FlaggedCount { get; set; }
		public InventorySummary Summary { get; set; } = new();
		public List<InventoryItem> Flagged { get; set; } = new();
	}
	public class InventorySummary
	{
		public int SlowMoving { get; set; }
		public int Reorder { get; set; }
		public int StockoutRisk { get; set; }
		public decimal DeadStockValue { get; set; }
	}
	public class InventoryItem
	{
		public int ItemId { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public decimal OnHand { get; set; }
		public decimal Value { get; set; }
		public double AvgDailyUsage { get; set; }
		public double? DaysCover { get; set; }
		public string? Class { get; set; }      // slow / reorder / stockout
		public string? Severity { get; set; }   // high / medium / low
		public decimal? SuggestedReorderQty { get; set; }
		public List<AiReason> Reasons { get; set; } = new();
	}

	// ----- page view model -----
	public class AiInsightsVm
	{
		public AnomalyResult? Anomaly { get; set; }
		public CashflowResult? Cashflow { get; set; }
		public InventoryResult? Inventory { get; set; }
		public bool ServiceDown { get; set; }   // Python AI service unreachable / no API key etc.
		public string? ServiceMessage { get; set; }
	}
}
