using System.Text.Json;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Communication
{
	/// One approved post from Communication to the internal AI service: the egress policy decides (purpose,
	/// classification, the company, the payload's size) and only an approval it minted reaches IAiService. Null when the
	/// policy refused or the service did not answer — the callers say "not available" and nothing is sent.
	/// Shared by the call summaries and the conversation assistant so there is one way to do it.
	public static class CommAi
	{
		public static async Task<AiProxyResult?> PostAsync(IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config, ILogger log,
			AiEgressPurpose purpose, AiDataClassification cls, int companyId, string path, object payload, CancellationToken ct)
		{
			var scope = OpenAiOptions.FromConfiguration(config).Scope;
			var decision = await egress.EvaluateAsync(new AiEgressRequest
			{
				Purpose = purpose,
				Destination = AiDestinationResolver.Resolve(config, purpose, providers.Assess(DateTime.UtcNow, scope).State),
				ProviderScope = scope,
				Classification = cls,
				DataCompanyId = companyId,
				PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
			}, ct);
			if (!decision.Allowed || decision.Approval == null)
			{
				log.LogInformation("Communication AI step {Path} not sent: egress denied ({Reason}).", path, decision.Reason);
				return null;
			}
			try { return await ai.PostAsync(decision.Approval, path, payload, ct); }
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
			{
				log.LogWarning("Communication AI step {Path}: the AI service did not answer ({Kind}).", path, e.GetType().Name);
				return null;
			}
		}
	}
}
