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

	// ----- per-capability state -----
	//
	// WHY FOUR STATES AND NOT A BOOLEAN. The page previously carried one `ServiceDown` flag for all
	// three capabilities, which could not express the two situations a reader most needs told apart:
	//
	//   "we analysed 500 entries and found nothing wrong"   -> reassuring, and true
	//   "we analysed nothing, so we found nothing"          -> says NOTHING about your books
	//
	// Both rendered as "No anomalies found". A clean bill of health that is really an absence of data
	// is the most dangerous thing an insights screen can show, so InsufficientData is its own state.
	// Unavailable and Failed are likewise separated: an operator can start a stopped service, and can
	// do nothing at all about a malformed response.
	public enum AiInsightState
	{
		/// A result was produced from a non-empty input set.
		Ok = 0,

		/// The model ran and answered honestly that there was not enough input to analyse.
		InsufficientData,

		/// The local ML service could not be reached, or the egress boundary refused the call.
		Unavailable,

		/// The service answered, but with an error status or a payload that did not parse.
		Failed,
	}

	/// <summary>Provenance for one insight panel: what produced it, when, over what, for whom.</summary>
	/// <remarks>
	/// Every field here is a FACT the system already knows. Nothing is inferred and nothing is phrased
	/// by a model — see AiInsights.cshtml, which renders these and adds no prose of its own.
	/// </remarks>
	public sealed class AiInsightPanel
	{
		public AiInsightState State { get; init; } = AiInsightState.Unavailable;

		/// <summary>Operator-facing explanation. NEVER an upstream payload or an exception message.</summary>
		/// <remarks>
		/// The previous version assigned the raw upstream JSON and, on the catch path, `ex.Message`
		/// straight into the view. That put service internals — and potentially the business rows the
		/// payload was built from — on a user's screen. Every value assigned here is a constant chosen
		/// by the controller.
		/// </remarks>
		public string? Detail { get; init; }

		/// <summary>When this result was produced, in UTC. Null when no result was produced.</summary>
		public DateTime? GeneratedAtUtc { get; init; }

		/// <summary>The company the data was scoped to. Resolved, never caller-supplied.</summary>
		public int CompanyId { get; init; }

		/// <summary>Which engine answered. Constant today: everything here is on-premises.</summary>
		public string Source { get; init; } = AiInsightSources.LocalMl;

		/// <summary>How many records the model reported considering. Null when it did not say.</summary>
		public int? RecordsConsidered { get; init; }

		public bool HasResult => State == AiInsightState.Ok;
	}

	/// <summary>Turns one local-ML response into a panel state. Pure, so it can be tested directly.</summary>
	/// <remarks>
	/// EXTRACTED FROM THE CONTROLLER ON PURPOSE. AccountingController takes about twenty dependencies, so
	/// no test can construct it; leaving this logic inside it would have meant proving the four states by
	/// reading the source rather than by running it. The rules below are the product decision this
	/// increment exists to make, and they deserve executable tests.
	/// </remarks>
	public static class AiInsightMapper
	{
		/// <param name="status">Upstream HTTP status. Anything but 200 means "we could not ask".</param>
		/// <param name="recordsConsidered">
		/// How many input records the model reported examining, or null when the payload did not parse.
		/// ZERO IS NOT SUCCESS — a model that examined nothing has said nothing about the business.
		/// </param>
		public static AiInsightPanel Map(int status, int? recordsConsidered, int companyId, DateTime nowUtc)
		{
			if (status != 200)
				return Panel(AiInsightState.Unavailable, "ai-insight:service-unavailable", companyId, null, nowUtc);

			if (recordsConsidered == null)
				return Panel(AiInsightState.Failed, "ai-insight:unreadable-response", companyId, null, nowUtc);

			return recordsConsidered <= 0
				? Panel(AiInsightState.InsufficientData, "ai-insight:no-input-records", companyId, recordsConsidered, nowUtc)
				: Panel(AiInsightState.Ok, null, companyId, recordsConsidered, nowUtc);
		}

		public static AiInsightPanel Unavailable(string detail, int companyId, DateTime nowUtc)
			=> Panel(AiInsightState.Unavailable, detail, companyId, null, nowUtc);

		public static AiInsightPanel Failed(string detail, int companyId, DateTime nowUtc)
			=> Panel(AiInsightState.Failed, detail, companyId, null, nowUtc);

		private static AiInsightPanel Panel(
			AiInsightState state, string? detail, int companyId, int? records, DateTime nowUtc) => new()
		{
			State = state,
			Detail = detail,
			CompanyId = companyId,
			Source = AiInsightSources.LocalMl,
			// A timestamp is stamped ONLY on a real result. Showing "generated at 14:02" beside a panel
			// that produced nothing would date an absence as though it were an answer.
			GeneratedAtUtc = state == AiInsightState.Ok ? nowUtc : null,
			RecordsConsidered = records,
		};
	}

	public static class AiInsightSources
	{
		/// The on-premises Python ML service. Named explicitly on the page so a reader never has to
		/// wonder whether their ledger was sent to a third party — it was not, and cannot be: the
		/// external provider is unapproved and the egress boundary refuses it.
		public const string LocalMl = "local-ml";
	}

	// ----- Inventory Risk Insights (product expansion wave 1) -----
	//
	// WHAT THE LOCAL MODEL ACTUALLY DOES, stated here because the screen must not overclaim it.
	// crossbuy_ai/app/ml/inventory.py is DETERMINISTIC RULES PLUS SIMPLE STATISTICS over on-hand
	// quantity, value and outbound demand. It classifies four situations and computes average daily
	// usage, days-of-cover and a suggested reorder quantity:
	//
	//     slow      on hand, but nothing issued in the window   -> capital tied up
	//     stockout  at or below zero WITH recent demand         -> depletion that already bit
	//     reorder   below the configured reorder point
	//     reorder   days-of-cover under two weeks
	//
	// IT IS NOT A FORECASTER. It does not predict future demand, does not detect anomalous individual
	// movements, and produces no confidence interval. The screen therefore reports severity and the
	// model's own reason codes as what they are — a rules-and-statistics classification — and claims
	// nothing else. Anything more would be a sentence the model never said.
	public sealed class InventoryRiskVm
	{
		public InventoryResult? Result { get; set; }
		public AiInsightPanel Panel { get; set; } = new();

		/// The resolved company. Server-derived; this screen accepts no company from the caller.
		public int CompanyId { get; set; }

		/// The window, in days, the model treated as "recent" — surfaced so a reader can judge the
		/// classification rather than take it on faith.
		public int WindowDays { get; set; }

		public IReadOnlyList<InventoryItem> Flagged =>
			Result?.Flagged ?? (IReadOnlyList<InventoryItem>)Array.Empty<InventoryItem>();

		public IEnumerable<InventoryItem> OfClass(string cls) =>
			Flagged.Where(f => string.Equals(f.Class, cls, StringComparison.OrdinalIgnoreCase));
	}

	// ----- page view model -----
	public class AiInsightsVm
	{
		public AnomalyResult? Anomaly { get; set; }
		public CashflowResult? Cashflow { get; set; }
		public InventoryResult? Inventory { get; set; }

		public AiInsightPanel AnomalyPanel { get; set; } = new();
		public AiInsightPanel CashflowPanel { get; set; } = new();
		public AiInsightPanel InventoryPanel { get; set; } = new();

		/// The resolved company every panel was scoped to.
		public int CompanyId { get; set; }

		/// True only when NO capability produced anything — the whole-page empty state.
		public bool AllUnavailable =>
			!AnomalyPanel.HasResult && !CashflowPanel.HasResult && !InventoryPanel.HasResult;
	}
}
