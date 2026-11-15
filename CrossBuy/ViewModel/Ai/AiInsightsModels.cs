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

		// SEPARATE FROM Anomalies, AND NOT COUNTED IN AnomalyCount.
		//
		// IsolationForest's `contamination` fixes the PROPORTION it labels, so the old detector
		// flagged exactly 6% of the ledger and would have flagged exactly 6% of a spotless one — it
		// could never say "nothing unusual". That is a ranking, not a finding, so it now arrives as
		// a short queue of the structurally strangest entries, labelled as such. A reader must be
		// able to tell "this entry is wrong" from "this entry is the oddest-looking of the batch".
		public List<AnomalyItem> ReviewQueue { get; set; } = new();

		public AnomalySummary Summary { get; set; } = new();
		public AiReason? Note { get; set; }
	}
	public class AnomalyItem
	{
		public int Id { get; set; }
		public string? EntryNo { get; set; }
		public string? Date { get; set; }
		public string? SourceType { get; set; }
		/// The source DOCUMENT. Duplication is judged on this, never on the description, because
		/// descriptions in this product are templated and cannot distinguish two documents.
		public int? SourceId { get; set; }
		public decimal Amount { get; set; }
		public int LineCount { get; set; }
		public double Score { get; set; }
		public string? Severity { get; set; }   // high / medium / low
		public List<AiReason> Reasons { get; set; } = new();
	}
	public class AnomalySummary
	{
		public int Duplicates { get; set; }
		public int AmountOutliers { get; set; }

		/// Entries whose duplication could NOT be judged: system-generated, carrying no document id
		/// and a templated description, so a duplicate and a coincidence are indistinguishable from
		/// the payload. Surfaced as a number rather than silently absent — a reader is entitled to
		/// know the scan did not cover everything.
		public int NotCheckedForDuplicates { get; set; }
		public int ReviewQueueSize { get; set; }
	}

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

		// ACTIVITY ACROSS THE WHOLE WINDOW. "No issues in 90 days" means dormant stock only if the
		// business was issuing stock at all. Measured on 2026-11-08 the window held 4 active days
		// and 35 movements, with 18 of 80 stocked items moving — so 60 items were being reported as
		// 218,498.16 of dead capital when the finding was about the PERIOD. When the window is
		// near-empty the analyser says so once, here, instead of accusing every item on the shelf.
		public int? WindowActiveDays { get; set; }
		public int? WindowMovements { get; set; }
		public bool WindowTooQuiet { get; set; }
		public AiReason? WindowNote { get; set; }

		/// Items whose issue history cannot support a daily rate, so none was stated for them.
		/// Visible rather than silent: an absent number and a number of zero are different claims.
		public int RateNotEstimable { get; set; }
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
		/// NULLABLE on purpose. The analyser now declines to state a rate it cannot support, and a
		/// non-nullable double would turn that refusal into a confident 0.0 on the screen.
		public double? AvgDailyUsage { get; set; }
		public double? DaysCover { get; set; }
		public string? Class { get; set; }      // slow / reorder / stockout
		public string? Severity { get; set; }   // high / medium / low
		public decimal? SuggestedReorderQty { get; set; }
		/// A..D, or null when the item has no usable issue history. Carried so a reader can see
		/// how much evidence sits behind AvgDailyUsage rather than taking the figure on faith.
		public string? HistoryGrade { get; set; }
		public List<AiReason> Reasons { get; set; } = new();
	}

	// ----- Reorder point advice (crossbuy_ai: app/ml/reorder.py) -----
	//
	// SEPARATE FROM InventoryResult ON PURPOSE. That one classifies an item from a demand TOTAL;
	// this one computes a reorder point from the demand DISTRIBUTION and may decline to compute one
	// at all. The two answer different questions and fail in different ways, so merging them would
	// have forced a shape where "refused, and here is what is missing" had nowhere to go.
	public sealed class ReorderResult
	{
		public string? AsOf { get; set; }
		public int WindowDays { get; set; }
		public double ServiceLevel { get; set; }
		public double Z { get; set; }
		public double DefaultLeadTimeDays { get; set; }
		public int PairsAnalyzed { get; set; }
		public ReorderSummary Summary { get; set; } = new();
		public AiReason? Note { get; set; }
		public List<ReorderRow> Results { get; set; } = new();
	}

	public sealed class ReorderSummary
	{
		public int Advised { get; set; }
		public int RefusedInsufficientHistory { get; set; }
		public int NoDemand { get; set; }
		public int ToOrderNow { get; set; }
		/// Grade letter -> count. A dictionary rather than four properties because the grading
		/// thresholds live in the engine and may gain a band without a C# change.
		public Dictionary<string, int> Grades { get; set; } = new();
	}

	/// One term of the reorder point, itemised so the arithmetic can be read rather than trusted.
	public sealed class ReorderTerm
	{
		public string? Key { get; set; }
		public double Value { get; set; }
		public AiReason? Label { get; set; }
		public AiReason? Detail { get; set; }
	}

	public sealed class ReorderRow
	{
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string? WarehouseName { get; set; }

		public decimal OnHand { get; set; }
		public int ActiveDays { get; set; }
		public int SpanDays { get; set; }
		public decimal TotalQty { get; set; }
		public int EffectiveWindowDays { get; set; }

		/// What the system holds TODAY — null across the board on this installation, which is the
		/// point: the fields have existed all along and nothing has ever filled them.
		public decimal? CurrentReorderPoint { get; set; }
		public decimal? CurrentSafetyStock { get; set; }
		public double LeadTimeDays { get; set; }
		public string? LeadTimeSource { get; set; }   // setting | default

		/// A..D — can the HISTORY support a number at all.
		public string? Grade { get; set; }
		public double Confidence { get; set; }
		/// False means no figure was produced. `Needed` then says what is missing.
		public bool Advise { get; set; }
		public string? Status { get; set; }           // computed | insufficientHistory | noDemand

		public double AvgDailyDemand { get; set; }
		public double SdDailyDemand { get; set; }
		/// sd / mean — is the demand the SHAPE the formula was built for. A separate axis from
		/// Grade: a row can have ample history and still be the wrong shape.
		public double CoefficientOfVariation { get; set; }
		public string? DemandShape { get; set; }      // stable | lumpy | intermittent
		/// Safety stock as a share of the point. High means the figure is mostly padding.
		public double SafetyShare { get; set; }

		public decimal? ReorderPoint { get; set; }
		public decimal? SafetyStock { get; set; }
		public double? DaysOfCover { get; set; }
		public decimal? SuggestedOrderQty { get; set; }

		public string? Action { get; set; }           // order | hold
		public AiReason? ActionText { get; set; }
		public AiReason? Needed { get; set; }
		public List<ReorderTerm> Terms { get; set; } = new();
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

	// ----- Reorder advice screen -----
	//
	// WHAT THIS SCREEN CLAIMS, AND WHAT IT DOES NOT. It proposes a reorder point and a safety stock
	// per item x warehouse and shows the arithmetic behind each one. It does NOT forecast demand, and
	// where the history cannot carry a number it shows the REFUSAL with its reason rather than a
	// figure — on this installation that is most rows, and the screen is built to make that readable
	// instead of hiding it behind an empty table.
	//
	// NOTHING IS WRITTEN BY LOADING IT. A proposed figure reaches ItemWarehouseSettings only through
	// the separate POST, one row at a time, under InvPerm("manage").
	public sealed class InventoryReorderVm
	{
		public ReorderResult? Result { get; set; }
		public AiInsightPanel Panel { get; set; } = new();

		/// The resolved company. Server-derived; this screen accepts no company from the caller.
		public int CompanyId { get; set; }
		public int WindowDays { get; set; }
		public double ServiceLevel { get; set; }

		public IReadOnlyList<ReorderRow> Rows =>
			Result?.Results ?? (IReadOnlyList<ReorderRow>)Array.Empty<ReorderRow>();

		public IEnumerable<ReorderRow> Advised => Rows.Where(r => r.Advise);
		public IEnumerable<ReorderRow> Refused => Rows.Where(r => !r.Advise);

		/// Rows the buyer must act on today, which is the only group that is ever urgent.
		public IEnumerable<ReorderRow> ToOrder =>
			Advised.Where(r => string.Equals(r.Action, "order", StringComparison.OrdinalIgnoreCase));

		/// Why each refusal happened, most common first — the one view that turns 72 individual
		/// refusals into an actionable statement about the DATA rather than about the items.
		public IEnumerable<(string Reason, int Count)> RefusalBreakdown =>
			Refused.Where(r => r.Reasons.Count > 0)
				   .GroupBy(r => r.Reasons[0].Ar)
				   .Select(g => (g.Key, g.Count()))
				   .OrderByDescending(x => x.Item2);
	}

	// ----- CRM Opportunity Insights (product expansion wave 2) -----
	//
	// THERE IS NO CRM MODEL. crossbuy_ai ships journal-anomaly, cashflow and inventory models and nothing
	// that takes an opportunity. This screen is therefore DETERMINISTIC BUSINESS RULES over fields the CRM
	// already stores (see BL/Platform/Ai/CrmOpportunityRiskRules.cs), and it says exactly that to the
	// reader. It performs NO AI egress, because there is nothing to send anywhere.
	public sealed class CrmOpportunityInsightsVm
	{
		public AiInsightPanel Panel { get; set; } = new();

		/// The resolved company. Server-derived; this screen accepts no company from the caller.
		public int CompanyId { get; set; }

		/// Open opportunities examined. Zero means InsufficientData, never "all clear".
		public int Analysed { get; set; }

		public IReadOnlyList<CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Insight> Insights { get; set; }
			= Array.Empty<CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Insight>();

		/// Distinct opportunities carrying at least one finding — the number a reader acts on. The insight
		/// count is larger, because one neglected deal can trip several rules at once.
		public int OpportunitiesWithFindings =>
			Insights.Select(i => i.Opportunity.OpportunityId).Distinct().Count();

		public int CountOf(CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Finding f) =>
			Insights.Count(i => i.Finding == f);

		public int CountOfSeverity(CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Severity s) =>
			Insights.Count(i => i.Severity == s);

		/// Filter values offered in the UI, built from what is actually present rather than from the enum:
		/// offering a filter that can only ever return nothing is a small lie about the data.
		public IReadOnlyList<string> OwnersPresent =>
			Insights.Select(i => i.Opportunity.OwnerName)
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.Select(n => n!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(n => n, StringComparer.CurrentCulture)
				.ToList();

		public IReadOnlyList<string> StagesPresent =>
			Insights.Select(i => i.Opportunity.Stage)
				.Where(v => !string.IsNullOrWhiteSpace(v))
				.Select(v => v!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(v => v, StringComparer.Ordinal)
				.ToList();
	}

	// ----- CRM Account Health (product expansion wave 2) -----
	//
	// A DIFFERENT QUESTION FROM CrmOpportunityInsightsVm, deliberately. That screen asks "which deals need
	// attention"; this one asks "which relationships are weakening". An account can hold three healthy
	// deals and still have gone silent, and no per-opportunity rule can see that.
	//
	// Still no CRM model exists, so this is rules over counts. See CrmAccountHealthRules.
	public sealed class CrmAccountHealthVm
	{
		public AiInsightPanel Panel { get; set; } = new();

		/// Resolved company. Server-derived; this screen accepts no company from the caller.
		public int CompanyId { get; set; }

		/// Accounts examined. Zero means InsufficientData, never a clean bill of health.
		public int Analysed { get; set; }

		public int RecentWindowDays { get; set; }
		public int PreviousWindowDays { get; set; }

		public IReadOnlyList<CrossBuy.BL.Platform.Ai.CrmAccountHealthRules.Insight> Insights { get; set; }
			= Array.Empty<CrossBuy.BL.Platform.Ai.CrmAccountHealthRules.Insight>();

		/// Distinct accounts carrying at least one finding — the number a manager acts on. The insight
		/// count is larger, because one neglected account can trip several rules at once.
		public int AccountsNeedingAttention =>
			Insights.Select(i => i.Account.AccountId).Distinct().Count();

		public int CountOf(CrossBuy.BL.Platform.Ai.CrmAccountHealthRules.Finding f) =>
			Insights.Count(i => i.Finding == f);

		/// Open pipeline value on the accounts that carry a finding, counted ONCE per account however many
		/// findings it tripped. Summing per insight would double-count the same money.
		public decimal ExposedOpenValue =>
			Insights.Select(i => i.Account).DistinctBy(a => a.AccountId).Sum(a => a.OpenOpportunityValue);

		public decimal PastDueValue =>
			Insights.Select(i => i.Account).DistinctBy(a => a.AccountId).Sum(a => a.PastDueOpportunityValue);

		public IReadOnlyList<string> OwnersPresent =>
			Insights.Select(i => i.Account.OwnerName)
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.Select(n => n!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(n => n, StringComparer.CurrentCulture)
				.ToList();
	}

	// ----- Insight -> Task review form (product expansion wave 2) -----
	//
	// Composed by the server, shown to the user for review, posted back, and then REVALIDATED FIELD BY
	// FIELD. Nothing here is trusted on the way back just because the server produced it on the way out:
	// between those two moments it lived in a browser.
	//
	// NOTE what is absent: there is no CompanyId the caller can set. The company is resolved server-side
	// on both the GET and the POST, so there is nothing to tamper with.
	public sealed class InsightTaskFormVm
	{
		/// InsightActions.Sources value. Revalidated against the frozen list.
		public string Source { get; set; } = "";

		/// The source record. Must exist IN THE RESOLVED COMPANY or it is treated as nonexistent.
		public int EntityId { get; set; }

		/// The finding that justified the action. Must be one this source offers a task for.
		public string FindingCode { get; set; } = "";

		/// Display-only, filled from the source row the server just verified.
		public string SourceLabel { get; set; } = "";

		/// Display-only. Resolved, never posted.
		public int CompanyId { get; set; }

		// ---- the editable proposal ----
		public string Title { get; set; } = "";
		public string? Reason { get; set; }
		public int AssigneeEmployeeId { get; set; }
		public DateTime? DueDate { get; set; }
		public string Priority { get; set; } = "Normal";

		/// The owner of the source record, offered as the default assignee when there is one.
		public int SuggestedAssigneeEmployeeId { get; set; }

		public List<(int Id, string Name)> Assignees { get; set; } = new();

		public string? ReturnUrl { get; set; }

		/// An OPEN task already raised for this exact finding, if any. The form warns instead of
		/// silently creating a second one; the server refuses the duplicate regardless.
		public int? ExistingOpenTaskId { get; set; }
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
