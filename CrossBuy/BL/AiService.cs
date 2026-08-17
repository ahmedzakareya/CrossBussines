using System.Text;
using System.Text.Json;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL
{
	// Authenticated proxy to the Python AI service. The shared secret is injected
	// as a default header by the typed-client registration in Program.cs.
	//
	// INCREMENT 3 — this type performs NO policy of its own. It refuses to act without an
	// AiEgressApproval, which only IAiEgressPolicy can mint, and it enforces one further invariant: the
	// payload it is actually given must not exceed the size the policy approved. Without that check the
	// approval would describe a different request from the one being sent, and the size limit would be
	// advisory rather than enforced.
	public class AiService : IAiService
	{
		private readonly HttpClient _http;

		public AiService(HttpClient http)
		{
			_http = http;
		}

		public Task<AiProxyResult> EchoAsync(AiEgressApproval approval, string message, string tier, CancellationToken ct = default)
			=> PostAsync(approval, "/diag/echo", new { message, tier }, ct);

		public async Task<AiProxyResult> PostAsync(AiEgressApproval approval, string path, object payload, CancellationToken ct = default)
		{
			// No approval, no call. Null here means a caller found a way around the policy — it must be
			// loud, not a silent no-op that looks like the service was down.
			ArgumentNullException.ThrowIfNull(approval,
				"An AI egress approval is required. Obtain one from IAiEgressPolicy before calling the AI service.");

			var json = JsonSerializer.Serialize(payload);

			// The approval is for a payload of a stated size. If the serialized body is larger than what
			// was approved, the decision no longer describes this request, so it is refused rather than
			// sent under a stale approval.
			int bytes = Encoding.UTF8.GetByteCount(json);
			if (bytes > approval.PayloadBytes)
				throw new InvalidOperationException(
					$"The AI payload is {bytes} bytes but egress was approved for {approval.PayloadBytes}. " +
					"Re-evaluate the egress policy for the actual payload rather than sending under a stale approval.");

			using var content = new StringContent(json, Encoding.UTF8, "application/json");

			// ResponseHeadersRead — the request completes as soon as the HEADERS arrive, so the body is
			// NOT buffered before we get a chance to judge it. The previous
			// `PostAsync(...)` + `ReadAsStringAsync(...)` pair did the opposite: it downloaded the entire
			// body first and would only then have been able to measure it, which is no protection at all
			// against an unbounded response.
			using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
			using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

			var body = await ReadBoundedBodyAsync(resp, ct);
			return new AiProxyResult((int)resp.StatusCode, body);
		}

		// ---------------------------------------------------------------------------------------------
		// Reads at most AiEgressLimits.MaxResponseBytes, and refuses rather than truncating.
		//
		// WHY NOT TRUNCATE. A truncated JSON document either fails to parse or — worse — parses into a
		// partial result that looks complete. A partial financial or inventory analysis presented as a
		// finished one is the failure mode this whole boundary exists to avoid, so an oversized response
		// is an ERROR, never a shortened success.
		// ---------------------------------------------------------------------------------------------
		private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage resp, CancellationToken ct)
		{
			// ---- 1. the cheap check: a declared Content-Length over the limit is refused before a single
			// body byte is read. ----
			long? declared = resp.Content.Headers.ContentLength;
			if (declared is > AiEgressLimits.MaxResponseBytes)
				return Oversize(declared.Value);

			// ---- 2. chunked or undeclared length: enforce DURING the read. ----
			// A hostile or faulty service can simply omit Content-Length, so the header check alone is a
			// courtesy, not the control. This is the control.
			await using var stream = await resp.Content.ReadAsStreamAsync(ct);

			// One byte of headroom: if the source produces MaxResponseBytes + 1, the extra byte lands in
			// the buffer and the limit is exceeded detectably. Sizing the buffer to the limit exactly
			// would make "full buffer" ambiguous between "exactly at the limit" and "more to come".
			var buffer = new byte[AiEgressLimits.MaxResponseBytes + 1];
			int total = 0;

			while (total < buffer.Length)
			{
				// The cancellation token is passed straight through, so a timeout or an aborted request
				// terminates the underlying network read rather than being swallowed here.
				int read = await stream.ReadAsync(buffer.AsMemory(total), ct);
				if (read == 0) break;                       // end of body
				total += read;

				if (total > AiEgressLimits.MaxResponseBytes)
					// Abort mid-flight: the remaining bytes are never pulled from the socket, and the
					// response object is disposed by the caller's `using`.
					return Oversize(total);
			}

			return Encoding.UTF8.GetString(buffer, 0, total);
		}

		// The refusal carries the LIMIT and the observed size only. It never includes the body (which is
		// the untrusted content being refused) and never the credential.
		private static string Oversize(long observedBytes) =>
			$"{{\"error\":\"ai_response_too_large\",\"limitBytes\":{AiEgressLimits.MaxResponseBytes}," +
			$"\"observedBytes\":{observedBytes}}}";
	}
}
