namespace CrossBuy.BL
{
	// Thin proxy to the Python AI service (crossbuy_ai, :8000).
	// The .NET app stays the source of truth and applies user permissions
	// BEFORE calling here — the AI only proposes, never authorizes.
	public interface IAiService
	{
		// Diagnostic echo through .NET -> Python -> Claude. Returns the upstream
		// HTTP status + raw JSON body, passed through to the API caller.
		Task<AiProxyResult> EchoAsync(string message, string tier, CancellationToken ct = default);

		// Generic POST to a Python AI endpoint. The caller (.NET) gathers the data
		// under the user's permissions, then forwards it here; Python only computes.
		Task<AiProxyResult> PostAsync(string path, object payload, CancellationToken ct = default);
	}

	public record AiProxyResult(int Status, string Json);
}
