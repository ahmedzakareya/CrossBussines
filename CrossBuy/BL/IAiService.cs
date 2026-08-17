using CrossBuy.Models.Platform;

namespace CrossBuy.BL
{
	// Thin proxy to the Python AI service (crossbuy_ai, :8000).
	// The .NET app stays the source of truth and applies user permissions
	// BEFORE calling here — the AI only proposes, never authorizes.
	//
	// AI FOUNDATION INCREMENT 3 — every method now REQUIRES an AiEgressApproval.
	//
	// That token can only be minted by IAiEgressPolicy, whose constructor is internal to the assembly, so
	// reaching this service without a policy decision is not a review failure — it does not compile. This
	// is deliberate: any service with IAiService injected could previously post anything outbound, and a
	// convention that says "ask the policy first" is one new caller away from being false.
	//
	// The approval carries the decision's identity (purpose, destination, classification, company, size)
	// and no payload, so it is safe to log in full.
	public interface IAiService
	{
		// Diagnostic echo through .NET -> Python -> Claude. Returns the upstream
		// HTTP status + raw JSON body, passed through to the API caller.
		//
		// NOTE the relay in that sentence: this path forwards to a third-party LLM. That is the evidence
		// AiDestinationResolver uses to refuse to treat the destination as Internal by default.
		Task<AiProxyResult> EchoAsync(AiEgressApproval approval, string message, string tier, CancellationToken ct = default);

		// Generic POST to a Python AI endpoint. The caller (.NET) gathers the data
		// under the user's permissions, then forwards it here; Python only computes.
		Task<AiProxyResult> PostAsync(AiEgressApproval approval, string path, object payload, CancellationToken ct = default);
	}

	public record AiProxyResult(int Status, string Json);
}
