using System.Net;
using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.6, Objective D — THE RESTORED LOCAL DEVELOPMENT PATH, EXERCISED FOR REAL.
    //
    // Increment 4.5 restored the local Python path by configuration and proved it with resolver-level
    // unit tests. That proved the CLASSIFICATION was right; it did not prove a single byte moved. This
    // file closes that gap by running the whole .NET leg over a real socket:
    //
    //     real AiEgressPolicy  ->  real AiEgressApproval  ->  real AiService typed client
    //         ->  real HTTP over loopback  ->  a stub standing in for the Python service
    //
    // WHY A STUB AND NOT THE REAL SERVICE. There is no Python runtime on this machine: the interpreter
    // on PATH is the Microsoft Store stub, and crossbuy_ai/.venv was created on a different machine
    // (its pyvenv.cfg points at C:\Users\Lenovo\...). Installing a runtime plus numpy, pandas and
    // scikit-learn is a substantial environment change that this increment did not authorise, so the
    // honest position is: everything on the CrossBuy side is verified end to end here, and "the Python
    // ML computes the right numbers" remains unverified and is reported as such.
    //
    // WHAT THIS THEREFORE PROVES: the governance chain admits the call, the approval is minted, the
    // credential header is attached, the payload leaves the process, and the response is read back.
    // WHAT IT DOES NOT PROVE: the forecast arithmetic inside crossbuy_ai.
    public class AiLocalLoopbackEndToEndTests
    {
        private const int Company = 1;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        /// A loopback listener standing in for crossbuy_ai. Records what it actually received.
        private sealed class LoopbackAiService : IAsyncDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;

            public string BaseUrl { get; }
            public string? ReceivedPath { get; private set; }
            public string? ReceivedBody { get; private set; }
            public string? ReceivedSecret { get; private set; }
            public int Requests { get; private set; }

            public LoopbackAiService(int port)
            {
                BaseUrl = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Add($"{BaseUrl}/");
                _listener.Start();
                _loop = Task.Run(AcceptAsync);
            }

            private async Task AcceptAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }   // listener stopped

                    Requests++;
                    ReceivedPath = ctx.Request.Url?.AbsolutePath;
                    ReceivedSecret = ctx.Request.Headers["X-AI-Secret"];

                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        ReceivedBody = await reader.ReadToEndAsync();

                    // The shape the real service returns for /forecast/cashflow.
                    var body = Encoding.UTF8.GetBytes(
                        "{\"horizonDays\":90,\"openingCash\":100.0,\"series\":[],\"minProjected\":100.0}");

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            }

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                _listener.Stop();
                _listener.Close();
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
                _cts.Dispose();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // A throwaway value generated per run — NOT the real development shared secret.
        //
        // It was the real one until Increment 4.7's secret scan caught it: the dev value from
        // crossbuy_ai/.env had been copied into this tracked test file, putting a live (if low-value)
        // hop-1 credential into source control. The test never needed the real secret — it only needs
        // the value CrossBuy sends to match the value the listener receives, which any string proves.
        private static readonly string TestSecret = $"test-shared-secret-{Guid.NewGuid():N}";

        /// The DEV configuration shape, exactly as appsettings.json declares it.
        private static IConfiguration DevConfig(string baseUrl) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DestinationClass"] = "Internal",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = baseUrl,
                ["AiService:Secret"] = TestSecret,
            }).Build();

        [Fact]
        public async Task The_restored_local_path_carries_a_cashflow_payload_over_a_real_socket()
        {
            await using var python = new LoopbackAiService(FreePort());
            var config = DevConfig(python.BaseUrl);

            // ---- 1. The resolver classifies the local service as Internal ----
            var destination = AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast);
            Assert.Equal(AiEgressDestinationClass.Internal, destination);

            // ---- 2. The REAL policy mints a REAL approval. No test authority: an Internal destination
            // does not consult the provider governance record at all, which is the whole reason local ML
            // is independent of the OpenAI decision. ----
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());   // <- the SHIPPED, empty record

            var payload = new
            {
                openingCash = 125_000.500m,
                asOf = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                horizonDays = 90,
                inflows = new[] { new { date = new DateTime(2026, 9, 1), amount = 20_000m } },
                outflows = new[] { new { date = new DateTime(2026, 9, 5), amount = 8_000m } },
            };
            var bytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(payload));

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = bytes,
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());
            Assert.NotNull(decision.Approval);

            // ---- 3. The REAL typed client sends it over a REAL socket ----
            using var http = new HttpClient { BaseAddress = new Uri(python.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("X-AI-Secret", config["AiService:Secret"]);

            var ai = new AiService(http);
            var result = await ai.PostAsync(decision.Approval!, "/forecast/cashflow", payload);

            // ---- 4. It arrived, authenticated, with the minimised payload and nothing else ----
            Assert.Equal(200, result.Status);
            Assert.Equal(1, python.Requests);
            Assert.Equal("/forecast/cashflow", python.ReceivedPath);
            Assert.Equal(TestSecret, python.ReceivedSecret);

            Assert.Contains("openingCash", python.ReceivedBody!, StringComparison.Ordinal);
            Assert.Contains("inflows", python.ReceivedBody!, StringComparison.Ordinal);

            // The minimisation actually held on the wire: no counterparty, no free text, no identifier.
            foreach (var forbidden in new[] { "customer", "vendor", "invoice", "description", "email", "phone", "name" })
                Assert.DoesNotContain(forbidden, python.ReceivedBody!, StringComparison.OrdinalIgnoreCase);

            // The response came back and is readable.
            Assert.Contains("horizonDays", result.Json, StringComparison.Ordinal);
        }

        // THE REGRESSION THIS EXISTS TO CATCH. Before the Phase 1 fix the dev configuration had no
        // DestinationClass, so the resolver said UnapprovedExternal and the policy refused — the insight
        // screens reported "service unavailable" for a reason unrelated to any provider. If the key is
        // ever removed again, this fails and says so, and ZERO bytes reach the socket.
        [Fact]
        public async Task Without_the_destination_class_the_local_path_is_denied_and_nothing_is_sent()
        {
            await using var python = new LoopbackAiService(FreePort());

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    // DestinationClass deliberately absent — the pre-4.5 state.
                    ["AiService:DeploymentMode"] = "LocalLoopback",
                    ["AiService:BaseUrl"] = python.BaseUrl,
                    ["AiService:Secret"] = "dev-secret",
                }).Build();

            var destination = AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, destination);

            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = 256,
            });

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.DestinationNotApproved, decision.Reason);
            Assert.Equal(0, python.Requests);   // the socket was never touched
        }

        // The restored local path grants OpenAI nothing. Same configuration, external destination: still
        // refused by the governance record, and still zero bytes on the wire.
        [Fact]
        public async Task The_restored_local_path_still_refuses_an_external_processor()
        {
            await using var python = new LoopbackAiService(FreePort());
            var config = DevConfig(python.BaseUrl);

            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = 256,
            });

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
            Assert.Equal(0, python.Requests);
        }
    }
}
