using System.Net;
using System.Text;
using System.Text.Json;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 7. THE OPENAI ADAPTER — INERT BY CONSTRUCTION.
    //
    // "Inert" is not a flag and not a mock. It is a consequence of the signature: SendAsync demands an
    // AiEgressApproval, whose constructor is internal to this assembly and is only ever called by
    // AiEgressPolicy. The policy will not mint one for an external destination while the governance
    // record approves nobody — which it does today, and which nothing in this file can change.
    //
    // So this class can be registered, resolved, unit-tested against a fake transport, and reviewed, and
    // it still cannot reach OpenAI. When the ZDR grant, the DPA and the three signatures exist, the same
    // code becomes live with no edit here.
    //
    // NO PACKAGE. Direct HttpClient against the documented chat-completions shape. The official OpenAI
    // .NET SDK would be a dependency, a version to track and an abstraction that hides the wire format —
    // for one endpoint with three fields, in a repository with no provider dependencies at all, the
    // smaller maintainable integration is the raw call. IHttpClientFactory supplies the client, so no
    // socket is created per request.
    //
    // WHAT THIS ADAPTER DOES NOT DECIDE: whether OpenAI is approved, whether the payload's classification
    // permits egress, which company the data belongs to, or whether the caller had permission. All four
    // were settled before an approval existed to hand it. It re-checks only what is CHEAPER OR SAFER to
    // verify twice: the switch, the credential, the breaker, and that the approval it was handed matches
    // the request it is being asked to send.
    public sealed class OpenAiProviderAdapter : IAiExternalProvider
    {
        public const string HttpClientName = "OpenAiProvider";

        public string ProviderId => OpenAiOptions.ProviderId;

        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly IAiProviderSwitchboard _switchboard;
        private readonly IAiCircuitBreaker _breaker;
        private readonly IAiRateLimiter _rateLimiter;
        private readonly IAiUsageGuard _usageGuard;
        private readonly IAiEgressAuditSink _audit;
        private readonly ILogger<OpenAiProviderAdapter> _log;

        public OpenAiProviderAdapter(
            IHttpClientFactory httpFactory,
            IConfiguration config,
            IAiProviderSwitchboard switchboard,
            IAiCircuitBreaker breaker,
            IAiRateLimiter rateLimiter,
            IAiUsageGuard usageGuard,
            IAiEgressAuditSink audit,
            ILogger<OpenAiProviderAdapter> log)
        {
            _httpFactory = httpFactory;
            _config = config;
            _switchboard = switchboard;
            _breaker = breaker;
            _rateLimiter = rateLimiter;
            _usageGuard = usageGuard;
            _audit = audit;
            _log = log;
        }

        public async Task<AiProviderResult> SendAsync(AiProviderRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var options = OpenAiOptions.FromConfiguration(_config);
            var startedUtc = DateTime.UtcNow;
            var began = System.Diagnostics.Stopwatch.StartNew();

            // ---- 1. THE APPROVAL MUST MATCH THE REQUEST IT IS BEING USED FOR ----
            //
            // Possessing an approval proves the policy allowed SOMETHING. These checks prove it allowed
            // THIS. Without them a valid approval minted for an internal, small, aggregate call could be
            // replayed to ship an unrelated payload — the token would be authentic and the send wrong.
            var approval = request.Approval;

            if (approval.Destination != AiEgressDestinationClass.ApprovedExternalProcessor)
                return await RefuseAsync(request, options, startedUtc, began,
                    "approval:not-for-external-processor", AiProviderOutcome.Refused, ct);

            if (approval.CompanyId <= 0)
                return await RefuseAsync(request, options, startedUtc, began,
                    "approval:company-unresolved", AiProviderOutcome.Refused, ct);

            // The classification matrix already refused free text and personal data before minting this
            // token. Re-asserted here so a future change to the matrix cannot silently widen what an
            // adapter will transmit.
            if (approval.Classification is not (AiDataClassification.OperationalMetadata
                                                or AiDataClassification.FinancialAggregate
                                                or AiDataClassification.SyntheticTestData))
                return await RefuseAsync(request, options, startedUtc, began,
                    $"approval:classification-not-permitted:{approval.Classification}", AiProviderOutcome.Refused, ct);

            if (string.IsNullOrWhiteSpace(request.PayloadJson))
                return await RefuseAsync(request, options, startedUtc, began,
                    "request:empty-payload", AiProviderOutcome.Refused, ct);

            // ---- 1a. A PAYLOAD CALLED SYNTHETIC MUST ACTUALLY BE SYNTHETIC ----
            //
            // Increment 4.10. The classification is a LABEL, and a label chosen by the caller proves
            // nothing. Without this check, marking a payload of real journal descriptions as
            // SyntheticTestData would carry it past the matrix, which refuses FreeTextBusinessContent at
            // every external destination — the label would have become the bypass.
            //
            // Checked HERE rather than in AiEgressPolicy because the policy never sees the payload: it is
            // handed a byte COUNT, deliberately, so that the boundary cannot be made to depend on
            // inspecting business content. The adapter is the first place the bytes exist.
            if (approval.Classification == AiDataClassification.SyntheticTestData
                && !AiSyntheticPayload.IsSynthetic(request.PayloadJson))
                return await RefuseAsync(request, options, startedUtc, began,
                    "request:not-actually-synthetic", AiProviderOutcome.Refused, ct);

            var payloadBytes = Encoding.UTF8.GetByteCount(request.PayloadJson);
            if (payloadBytes > AiEgressLimits.MaxPayloadBytes)
                return await RefuseAsync(request, options, startedUtc, began,
                    "request:payload-too-large", AiProviderOutcome.Refused, ct);

            // The approval was minted for a measured size. A payload that grew after approval is a
            // different payload.
            if (payloadBytes > approval.PayloadBytes)
                return await RefuseAsync(request, options, startedUtc, began,
                    "approval:payload-grew-after-approval", AiProviderOutcome.Refused, ct);

            // ---- 1b. THE APPROVAL'S SCOPE MUST MATCH THE SCOPE THIS ADAPTER IS ABOUT TO CALL ----
            //
            // Increment 4.9. Possessing an approval proves the policy allowed a call to SOME account
            // context. These checks prove it allowed THIS one. Without them a Development approval could
            // be replayed against the Production project: the token would be authentic and the
            // destination wrong, which is precisely the failure a capability token exists to prevent.
            if (approval.ProviderScope is not AiProviderScope approvedScope || !approvedScope.IsComplete)
                return await RefuseAsync(request, options, startedUtc, began,
                    "approval:no-provider-scope", AiProviderOutcome.Refused, ct);

            var callScope = options.Scope;
            if (!callScope.IsComplete)
                return await RefuseAsync(request, options, startedUtc, began,
                    "config:provider-scope-incomplete", AiProviderOutcome.Refused, ct);

            if (!approvedScope.Covers(callScope))
                return await RefuseAsync(request, options, startedUtc, began,
                    "approval:scope-mismatch", AiProviderOutcome.Refused, ct);

            // ---- 2. PROVIDER KILL SWITCH, for THIS environment ----
            if (!_switchboard.IsEnabled(ProviderId, callScope.Environment))
                return await RefuseAsync(request, options, startedUtc, began,
                    _switchboard.Explain(ProviderId), AiProviderOutcome.Refused, ct);

            // ---- 3. RATE LIMIT, then USAGE CEILING ----
            //
            // In this order because the rate limiter is the cheaper check and catches the loop; the usage
            // guard catches the expensive-but-infrequent case the limiter would wave through.
            var rateKey = new AiRateLimitKey(approval.CompanyId, ProviderId, approval.Purpose);
            var rate = _rateLimiter.TryAcquire(rateKey, startedUtc, ct);
            if (!rate.Allowed)
                return await RefuseAsync(request, options, startedUtc, began, rate.Reason, AiProviderOutcome.Refused, ct);

            var usageKey = new AiUsageKey(approval.CompanyId, ProviderId, approval.Purpose);
            var usage = _usageGuard.TryReserve(usageKey, startedUtc, ct);
            if (!usage.Allowed)
                return await RefuseAsync(request, options, startedUtc, began, usage.Reason, AiProviderOutcome.Refused, ct);

            // ---- 4. CREDENTIAL ----
            //
            // Absent means refuse locally. It must never become an unauthenticated request: that would
            // send the payload and have it rejected at the far end, by which point the data has left.
            var apiKey = OpenAiOptions.ReadApiKey(_config);
            if (apiKey is null)
                return await RefuseAsync(request, options, startedUtc, began,
                    "credential:missing", AiProviderOutcome.Refused, ct);

            // ---- 5. CIRCUIT BREAKER ----
            var circuit = _breaker.TryEnter(ProviderId, startedUtc);
            if (!circuit.Allowed)
                return await RefuseAsync(request, options, startedUtc, began, circuit.Reason, AiProviderOutcome.Refused, ct);

            // ---- 6. TRANSMIT ----
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            try
            {
                var client = _httpFactory.CreateClient(HttpClientName);

                using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
                {
                    Content = new StringContent(BuildBody(request, options), Encoding.UTF8, "application/json"),
                };

                // Set per request and never stored on the client, so the credential does not outlive the
                // call and cannot be observed on a shared handler.
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (!response.IsSuccessStatusCode)
                {
                    // 401/403 are configuration or entitlement problems and are NOT transient: counting
                    // them would open the circuit and replace a precise error with an opaque one.
                    var transient = response.StatusCode is HttpStatusCode.TooManyRequests
                        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

                    _breaker.RecordFailure(ProviderId, DateTime.UtcNow, transient);

                    // The status code only. A provider error body can quote the request back, so it is
                    // neither logged nor audited.
                    return await FinishAsync(request, options, startedUtc, began,
                        AiProviderOutcome.ProviderError, $"provider-error:{(int)response.StatusCode}", null, null, ct);
                }

                var body = await ReadBoundedAsync(response, timeout.Token);
                if (body is null)
                {
                    _breaker.RecordFailure(ProviderId, DateTime.UtcNow, transient: false);
                    return await FinishAsync(request, options, startedUtc, began,
                        AiProviderOutcome.InvalidResponse, "response:too-large", null, null, ct);
                }

                var parsed = Parse(body, options.Model);
                if (parsed is null)
                {
                    _breaker.RecordFailure(ProviderId, DateTime.UtcNow, transient: false);
                    return await FinishAsync(request, options, startedUtc, began,
                        AiProviderOutcome.InvalidResponse, "response:unparseable", null, null, ct);
                }

                _breaker.RecordSuccess(ProviderId, DateTime.UtcNow);
                _usageGuard.RecordUsage(usageKey, parsed.Value.Usage, DateTime.UtcNow);

                return await FinishAsync(request, options, startedUtc, began,
                    AiProviderOutcome.Success, "ok", parsed.Value.Content, parsed.Value.Usage, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER cancelled — the user navigated away, the request aborted. Not a provider
                // fault, so the breaker is untouched.
                return await FinishAsync(request, options, startedUtc, began,
                    AiProviderOutcome.Cancelled, "cancelled", null, null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Our own timeout elapsed. That IS a provider-side symptom.
                _breaker.RecordFailure(ProviderId, DateTime.UtcNow, transient: true);
                return await FinishAsync(request, options, startedUtc, began,
                    AiProviderOutcome.Timeout, $"timeout:{options.TimeoutSeconds}s", null, null, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                // Transport-level: DNS, connection refused, TLS. Transient. The exception message is not
                // recorded — it can contain the URI and, in some stacks, header material.
                _breaker.RecordFailure(ProviderId, DateTime.UtcNow, transient: true);
                return await FinishAsync(request, options, startedUtc, began,
                    AiProviderOutcome.ProviderError, "transport-error", null, null, CancellationToken.None);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Request body. The system prompt is a CrossBuy constant and the user turn is the minimised
        // JSON payload — nothing here is assembled from user-entered text.
        // -----------------------------------------------------------------------------------------
        private static string BuildBody(AiProviderRequest request, OpenAiOptions options)
            => JsonSerializer.Serialize(new
            {
                model = options.Model,
                max_completion_tokens = Math.Min(request.MaxOutputTokens, options.MaxOutputTokens),

                // Deterministic output is worth more than variety for a financial analysis: the same
                // figures should not produce a different risk rating on a second run.
                temperature = 0,

                // The recorded owner policy is zero retention. This is belt-and-braces against the
                // /v1/responses default; chat-completions does not persist, and stating it costs nothing.
                store = false,
                messages = new object[]
                {
                    new { role = "system", content = request.SystemPrompt },
                    new { role = "user", content = request.PayloadJson },
                },
            });

        /// Bounded read. Mirrors AiService.ReadBoundedBodyAsync: refuse on a declared oversize length
        /// before reading, then read with a hard ceiling so a lying Content-Length cannot exhaust memory.
        private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength is { } declared && declared > AiEgressLimits.MaxResponseBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[AiEgressLimits.MaxResponseBytes + 1];
            int total = 0, read;

            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
                total += read;

            // Over the ceiling: refuse outright. Never truncate — a half-parsed analysis that looks
            // complete is worse than no analysis.
            return total > AiEgressLimits.MaxResponseBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }

        /// <summary>Parses the response. Returns null for anything that cannot be trusted.</summary>
        /// <remarks>
        /// Model output is untrusted external input. This extracts a string and token counts and does
        /// nothing else with it — no deserialisation into a domain type, no SQL, no URL, no command.
        /// Interpreting the content is the caller's job, after its own validation.
        /// </remarks>
        private static (string Content, AiProviderUsage Usage)? Parse(string body, string model)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                    return null;

                if (!choices[0].TryGetProperty("message", out var msg)
                    || !msg.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.String)
                    return null;

                var text = content.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;

                int input = 0, output = 0;
                if (root.TryGetProperty("usage", out var u))
                {
                    if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var p)) input = p;
                    if (u.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var c)) output = c;
                }

                var reportedModel = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? model
                    : model;

                return (text, new AiProviderUsage
                {
                    ProviderId = OpenAiOptions.ProviderId,
                    Model = reportedModel,
                    InputTokens = input,
                    OutputTokens = output,
                });
            }
            catch (JsonException)
            {
                // Malformed body. Fail closed, and do not include the body in the reason.
                return null;
            }
        }

        private Task<AiProviderResult> RefuseAsync(
            AiProviderRequest request, OpenAiOptions options, DateTime startedUtc,
            System.Diagnostics.Stopwatch began, string reason, AiProviderOutcome outcome, CancellationToken ct)
            => FinishAsync(request, options, startedUtc, began, outcome, reason, null, null, ct);

        private async Task<AiProviderResult> FinishAsync(
            AiProviderRequest request, OpenAiOptions options, DateTime startedUtc,
            System.Diagnostics.Stopwatch began, AiProviderOutcome outcome, string reason,
            string? content, AiProviderUsage? usage, CancellationToken ct)
        {
            began.Stop();
            var approval = request.Approval;

            // Payload-free by construction: purpose, classification, company, correlation, outcome, sizes,
            // token counts. No prompt, no completion, no credential, no provider message.
            _log.LogInformation(
                "AI provider {Provider} outcome={Outcome} reason={Reason} feature={Feature} " +
                "classification={Classification} company={Company} correlation={Correlation} ms={Ms} " +
                "inTokens={In} outTokens={Out}",
                ProviderId, outcome, reason, approval.Purpose, approval.Classification,
                approval.CompanyId, request.CorrelationId, began.ElapsedMilliseconds,
                usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0);

            await _audit.RecordAsync(new AiEgressAuditRecord
            {
                CompanyId = approval.CompanyId,
                ProviderId = ProviderId,
                Feature = approval.Purpose,
                Classification = approval.Classification,
                Destination = approval.Destination,
                GovernanceDecision = approval.AppliedPolicy,
                ApprovalReference = approval.AppliedPolicy,
                Model = usage?.Model ?? options.Model,
                CorrelationId = request.CorrelationId,
                OccurredAtUtc = startedUtc,
                Duration = began.Elapsed,
                Outcome = outcome,
                RequestBytes = approval.PayloadBytes,
                InputTokens = usage?.InputTokens ?? 0,
                OutputTokens = usage?.OutputTokens ?? 0,
                EstimatedCost = usage?.EstimatedCost,
                Currency = usage?.Currency,
                FailureCategory = outcome == AiProviderOutcome.Success ? null : reason,
            }, ct);

            return new AiProviderResult
            {
                Outcome = outcome,
                Reason = reason,
                Content = content,
                Usage = usage,
                Duration = began.Elapsed,
            };
        }
    }

    /// <summary>
    /// A log-only audit sink. <b>No longer the production sink</b> as of Increment 4.6.
    /// </summary>
    /// <remarks>
    /// <para>Superseded by <see cref="AiEgressAuditSink"/>, which writes this same structured line AND a
    /// durable row to <c>AiEgressAudits</c>. That is what <c>Program.cs</c> now registers.</para>
    /// <para>Retained because it is genuinely useful where a database is neither present nor the subject
    /// of the test — the adapter's own unit tests construct it directly so they exercise the adapter
    /// rather than EF. Deleting it would push a SQLite host into tests that have nothing to do with
    /// persistence.</para>
    /// </remarks>
    public sealed class LoggingAiEgressAuditSink : IAiEgressAuditSink
    {
        private readonly ILogger<LoggingAiEgressAuditSink> _log;
        public LoggingAiEgressAuditSink(ILogger<LoggingAiEgressAuditSink> log) => _log = log;

        public Task RecordAsync(AiEgressAuditRecord r, CancellationToken ct = default)
        {
            _log.LogInformation(
                "AI-EGRESS-AUDIT company={Company} provider={Provider} feature={Feature} model={Model} " +
                "classification={Classification} destination={Destination} decision={Decision} " +
                "outcome={Outcome} correlation={Correlation} at={At:o} ms={Ms} bytes={Bytes} " +
                "inTokens={In} outTokens={Out} cost={Cost} currency={Currency} failure={Failure}",
                r.CompanyId, r.ProviderId, r.Feature, r.Model, r.Classification, r.Destination,
                r.GovernanceDecision, r.Outcome, r.CorrelationId, r.OccurredAtUtc,
                (long)r.Duration.TotalMilliseconds, r.RequestBytes, r.InputTokens, r.OutputTokens,
                r.EstimatedCost, r.Currency, r.FailureCategory);

            return Task.CompletedTask;
        }
    }
}
