using System.Text;
using System.Text.Json;

namespace CrossBuy.BL
{
	// Authenticated proxy to the Python AI service. The shared secret is injected
	// as a default header by the typed-client registration in Program.cs.
	public class AiService : IAiService
	{
		private readonly HttpClient _http;

		public AiService(HttpClient http)
		{
			_http = http;
		}

		public Task<AiProxyResult> EchoAsync(string message, string tier, CancellationToken ct = default)
			=> PostAsync("/diag/echo", new { message, tier }, ct);

		public async Task<AiProxyResult> PostAsync(string path, object payload, CancellationToken ct = default)
		{
			var json = JsonSerializer.Serialize(payload);
			using var content = new StringContent(json, Encoding.UTF8, "application/json");
			using var resp = await _http.PostAsync(path, content, ct);
			var body = await resp.Content.ReadAsStringAsync(ct);
			return new AiProxyResult((int)resp.StatusCode, body);
		}
	}
}
