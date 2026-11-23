using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CrossBuy.BL.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H257 — the outbound feeds send by themselves, and a TEST EXCHANGE stands in for the outside parties
	// while the hospital is still in test (owner, 2026-11-23: "we are still in test — add the test data you need").
	//
	//   · HospitalOutboundSenderService (owner-approved Program.cs line): every minute, for each company, the enabled
	//     feeds collect their new events and send them — the same core as the feeds page's buttons.
	//   · The test exchange exists ONLY in a Development process, on this machine:
	//       – an MLLP receiver on 127.0.0.1 (Hospital:TestExchange:MllpPort, default 2576; 0 = closed) that answers
	//         every frame AA and keeps the last 200 in memory — it files nothing;
	//       – a payer FHIR endpoint (api/dev/hospital-exchange/payer) that answers eligibility and pre-authorisation
	//         by fixed rules (HospitalTestExchange) when the test key is presented;
	//       – a test checkout page for the portal's "pay online" link that takes no money and marks nothing paid.
	//     When real addresses arrive the administrator replaces the test ones on the same screens; nothing else changes.
	// ==========================================================================================
	public static class HospitalTestExchange
	{
		public const string KeyHeader = "X-Api-Key";
		public const string Key = "test-exchange-key";
		public const decimal AutoApproveLimit = 1000m;
		public const int DefaultMllpPort = 2576;

		public static string? KeyRefusal(string? presented) => presented == Key ? null : "The test exchange's key is missing or wrong";

		private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

		private static JsonObject Outcome(string type, string id, string request) => new()
		{
			["resourceType"] = type, ["id"] = id, ["status"] = "active", ["outcome"] = "complete",
			["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			["request"] = new JsonObject { ["reference"] = request },
		};

		/// Every eligibility request is answered "in force", except a policy number ending in "X" (a lapsed test policy).
		public static string? Eligibility(string? requestJson)
		{
			if (Parse(requestJson, "CoverageEligibilityRequest") is not JsonObject r) return null;
			var id = Str(r["id"]) ?? "eligibility";
			string? policyNo = null;
			foreach (var c in r["contained"] as JsonArray ?? new JsonArray())
				if (Str(c?["resourceType"]) == "Coverage") policyNo = Str(c?["subscriberId"]);
			bool inForce = !(policyNo ?? "").TrimEnd().EndsWith("X", StringComparison.OrdinalIgnoreCase);
			var o = Outcome("CoverageEligibilityResponse", "answer-" + id, "CoverageEligibilityRequest/" + id);
			o["disposition"] = inForce ? "Test exchange: the policy is in force" : "Test exchange: the policy has lapsed";
			o["insurance"] = new JsonArray(new JsonObject { ["inforce"] = inForce });
			return o.ToJsonString();
		}

		/// A pre-authorisation up to the automatic limit is approved with a reference; above it, refused with a reason.
		public static string? Preauth(string? requestJson)
		{
			if (Parse(requestJson, "Claim") is not JsonObject r) return null;
			var id = Str(r["id"]) ?? "preauth-0";
			decimal total = r["total"]?["value"] is JsonValue tv && decimal.TryParse(tv.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
			string currency = Str(r["total"]?["currency"]) ?? "KWD";
			var o = Outcome("ClaimResponse", "answer-" + id, "Claim/" + id);
			o["use"] = "preauthorization";
			bool approve = total > 0 && total <= AutoApproveLimit;
			var adjudication = approve
				? new JsonObject { ["category"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = "benefit" }) }, ["amount"] = new JsonObject { ["value"] = total, ["currency"] = currency } }
				: new JsonObject
				{
					["category"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = "benefit" }) }, ["amount"] = new JsonObject { ["value"] = 0, ["currency"] = currency },
					["reason"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = "limit", ["display"] = "Above the test exchange's automatic approval limit" }) },
				};
			if (approve) o["preAuthRef"] = "TEST-" + id.ToUpperInvariant();
			o["disposition"] = approve ? "Test exchange: approved" : "Test exchange: refused";
			o["item"] = new JsonArray(new JsonObject { ["itemSequence"] = 1, ["adjudication"] = new JsonArray(adjudication) });
			return o.ToJsonString();
		}

		public static string Error(string text) => new JsonObject
		{
			["resourceType"] = "OperationOutcome",
			["issue"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = "security", ["diagnostics"] = text }),
		}.ToJsonString();

		private static JsonObject? Parse(string? raw, string type)
		{
			try { return JsonNode.Parse(raw ?? "") is JsonObject o && Str(o["resourceType"]) == type ? o : null; }
			catch (System.Text.Json.JsonException) { return null; }
		}

		/// The test receiver's answer to one frame: AA, echoing its control id. It files nothing.
		public static string Ack(string raw, DateTime nowUtc) => HospitalInterfaceRules.Ack(HospitalInterfaceRules.ControlId(raw) ?? "", "AA", "received by the test receiver", nowUtc);

		/// The configured test receiver port in a Development process (null: closed).
		public static int? MllpPort(IConfiguration? config, IHostEnvironment? env)
		{
			if (env == null || !env.IsDevelopment()) return null;
			var p = config?.GetValue<int?>("Hospital:TestExchange:MllpPort") ?? DefaultMllpPort;
			return p is >= 1 and <= 65535 ? p : null;
		}
	}

	public sealed record HospitalTestReceipt(DateTime At, string Type, string ControlId, string Patient);

	/// What the test receiver took (the last 200, in memory; nothing is stored).
	public static class HospitalTestReceiverLog
	{
		private static readonly ConcurrentQueue<HospitalTestReceipt> Items = new();
		public static void Add(string raw, DateTime nowUtc)
		{
			var msh = (raw ?? "").Replace("\r\n", "\r").Split('\r', '\n').FirstOrDefault(s => s.StartsWith("MSH")) ?? "";
			var pid = (raw ?? "").Replace("\r\n", "\r").Split('\r', '\n').FirstOrDefault(s => s.StartsWith("PID|")) ?? "";
			var f = msh.Split('|'); var p = pid.Split('|');
			Items.Enqueue(new HospitalTestReceipt(nowUtc, f.Length > 8 ? f[8] : "?", f.Length > 9 ? f[9] : "?", p.Length > 3 ? p[3].Split('^')[0] : ""));
			while (Items.Count > 200) Items.TryDequeue(out _);
		}
		public static IReadOnlyList<HospitalTestReceipt> Recent() => Items.Reverse().ToList();
	}

	/// The MLLP test receiver: Development only, loopback only, answers AA and files nothing.
	public sealed class HospitalMllpTestReceiverService : BackgroundService
	{
		private readonly IConfiguration? _config;
		private readonly IHostEnvironment? _env;
		private readonly ILogger<HospitalMllpTestReceiverService> _log;
		public HospitalMllpTestReceiverService(ILogger<HospitalMllpTestReceiverService> log, IConfiguration? config = null, IHostEnvironment? env = null) { _log = log; _config = config; _env = env; }

		protected override async Task ExecuteAsync(CancellationToken stop)
		{
			if (HospitalTestExchange.MllpPort(_config, _env) is not int port) return;
			TcpListener tcp;
			try { tcp = new TcpListener(IPAddress.Loopback, port); tcp.Start(); }
			catch (Exception ex) { _log.LogWarning(ex, "Hospital test receiver: port {Port} could not be opened.", port); return; }
			_log.LogInformation("Hospital test receiver: listening on 127.0.0.1:{Port} (Development only; answers AA, files nothing).", port);
			try
			{
				while (!stop.IsCancellationRequested)
				{
					var client = await tcp.AcceptTcpClientAsync(stop);
					_ = Task.Run(async () => { using (client) await ServeAsync(client, stop); }, stop);
				}
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
			finally { tcp.Stop(); }
		}

		private static async Task ServeAsync(TcpClient client, CancellationToken stop)
		{
			var stream = client.GetStream();
			var buffer = new List<byte>(); var chunk = new byte[8192]; var messages = new List<string>();
			try
			{
				while (!stop.IsCancellationRequested)
				{
					using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
					idle.CancelAfter(TimeSpan.FromMinutes(1));
					int n = await stream.ReadAsync(chunk, idle.Token);
					if (n == 0) return;
					buffer.AddRange(chunk.AsSpan(0, n).ToArray());
					messages.Clear();
					if (!HospitalMllp.TryTake(buffer, messages)) return;
					foreach (var m in messages)
					{
						HospitalTestReceiverLog.Add(m, DateTime.UtcNow);
						await stream.WriteAsync(HospitalMllp.Frame(HospitalTestExchange.Ack(m, DateTime.UtcNow)), stop);
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (IOException) { }
		}
	}

	/// Every minute: each company's enabled feeds collect their new events and send them (owner-approved 2026-11-23).
	public sealed class HospitalOutboundSenderService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly IConfiguration? _config;
		private readonly ILogger<HospitalOutboundSenderService> _log;

		public HospitalOutboundSenderService(IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification, ILogger<HospitalOutboundSenderService> log, IConfiguration? config = null)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; _config = config; }

		protected override async Task ExecuteAsync(CancellationToken stop)
		{
			if (_certification.BackgroundWritersSuppressed) { _log.LogInformation("Hospital outbound sender is suppressed: this host is a certification runtime."); return; }
			int seconds = Math.Max(30, _config?.GetValue<int?>("Hospital:Outbound:IntervalSeconds") ?? 60);
			try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (TaskCanceledException) { return; }
			if (!await _gate.WaitUntilAllowedAsync(nameof(HospitalOutboundSenderService), stop)) return;
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
			do
			{
				if (!await _gate.IsStillAllowedAsync(nameof(HospitalOutboundSenderService), stop))
				{
					if (!await _gate.WaitUntilAllowedAsync(nameof(HospitalOutboundSenderService), stop)) return;
				}
				try
				{
					using var root = _scopes.CreateScope();
					await WorkerCompanyRunner.ForEachCompanyAsync(root.ServiceProvider.GetRequiredService<IWorkerCompanyScope>(), _log, nameof(HospitalOutboundSenderService),
						async companyId => await RunCompanyAsync(companyId, stop), stop);
				}
				catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
				catch (Exception ex) { _log.LogError(ex, "Hospital outbound sender: the run failed."); }
			}
			while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
		}

		private async Task RunCompanyAsync(int companyId, CancellationToken stop)
		{
			using var scope = WorkerScope.ForCompany(_scopes, companyId);
			var svc = ActivatorUtilities.CreateInstance<HospitalOutboundService>(scope.ServiceProvider);
			try
			{
				var feeds = await svc.SendableFeedsAsync(companyId, stop);
				if (feeds.Count == 0) return;
				await svc.CollectForAsync(companyId, "CB" + companyId.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow, stop);
				foreach (var id in feeds)
				{
					var r = await svc.SendForAsync(companyId, id, DateTime.UtcNow, stop);
					if (!r.Ok) _log.LogWarning("Hospital outbound sender: company {CompanyId} feed {FeedId} — {Error}", companyId, id, r.Error);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }   // the feeds slice is not installed here
		}
	}
}
