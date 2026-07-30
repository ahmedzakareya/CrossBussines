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
	}
}
