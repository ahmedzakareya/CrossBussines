namespace CrossBuy.BL
{
	// Gathers module data (company-scoped) and forwards it to the Python AI/ML
	// service. Shared by the JWT API (AiController) and the web page
	// (AccountingController.AiInsights) so the gathering logic lives in ONE place.
	// Returns the upstream status + raw JSON; callers pass through or deserialize.
	public interface IAiInsightsService
	{
		Task<AiProxyResult> ScanJournalAnomaliesAsync(int companyId, CancellationToken ct = default);
		Task<AiProxyResult> ForecastCashflowAsync(int companyId, int horizonDays, CancellationToken ct = default);
		Task<AiProxyResult> AnalyzeInventoryAsync(int companyId, int slowDays, CancellationToken ct = default);

		// Proposes ReorderPoint / SafetyStock per item x warehouse, or refuses per row and says what
		// history is missing. Distinct from AnalyzeInventoryAsync because it sends the demand
		// DISTRIBUTION rather than a total: see the gathering method for why a total cannot support a
		// reorder point. It proposes only — nothing is written to ItemWarehouseSettings here.
		Task<AiProxyResult> ComputeReorderPointsAsync(int companyId, int windowDays, double serviceLevel,
			double defaultLeadTimeDays, CancellationToken ct = default);
	}
}
