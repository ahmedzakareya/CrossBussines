using System.Net;
using System.Net.Sockets;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H227 — machines that speak MLLP talk to the hospital directly (owner-approved 2026-11-22).
	//
	// H215 opened an HTTPS channel; most analysers and ECG carts speak only HL7 over MLLP (a TCP stream, each message
	// framed <VT> … <FS><CR>). This listener receives those frames and files each one through EXACTLY the HTTPS path
	// (HospitalInterfaceInbound, shared with the controller), and answers each with its framed HL7 acknowledgement.
	//
	// WHO IS TALKING. MLLP carries no credential, so each port is configured with the interface it serves AND that
	// interface's key (Hospital:Mllp:Listeners). The key is checked against its stored SHA-256 on every message, so
	// rotating or switching the interface off on the screen cuts the machine off at once, and the message is filed AS
	// the employee the interface acts for, with that person's rights checked each time — the same as over HTTPS.
	//
	// WHERE IT LISTENS. Loopback unless a Bind address is configured, and only the AllowedClients addresses when that
	// list is given. No listener configured ⇒ nothing opens: registering the service changes nothing on its own.
	// Frames are capped at 1 MB; an idle connection is closed after 5 minutes; at most 16 connections at once.
	// ==========================================================================================
	public sealed class HospitalMllpListenerOptions
	{
		public int Port { get; set; }
		public string Bind { get; set; } = "127.0.0.1";
		public string Interface { get; set; } = "";
		public string Key { get; set; } = "";
		public List<string> AllowedClients { get; set; } = new();
	}

	public static class HospitalMllp
	{
		public const byte Start = 0x0B, End = 0x1C, Cr = 0x0D;
		public const int MaxFrame = 1_000_000;

		/// The configured listeners that can open: a port, an interface and its key (the rest are reported and skipped).
		public static (List<HospitalMllpListenerOptions> Ok, List<string> Skipped) Read(IConfiguration? config)
		{
			var all = new List<HospitalMllpListenerOptions>();
			config?.GetSection("Hospital:Mllp:Listeners").Bind(all);
			return Validate(all);
		}

		public static (List<HospitalMllpListenerOptions> Ok, List<string> Skipped) Validate(IEnumerable<HospitalMllpListenerOptions> all)
		{
			var ok = new List<HospitalMllpListenerOptions>(); var skipped = new List<string>();
			foreach (var l in all)
			{
				if (l.Port is < 1 or > 65535) { skipped.Add($"port {l.Port}: not a port"); continue; }
				if (string.IsNullOrWhiteSpace(l.Interface) || string.IsNullOrWhiteSpace(l.Key)) { skipped.Add($"port {l.Port}: no interface or key"); continue; }
				if (!IPAddress.TryParse(l.Bind, out _)) { skipped.Add($"port {l.Port}: bind '{l.Bind}' is not an address"); continue; }
				if (ok.Any(o => o.Port == l.Port)) { skipped.Add($"port {l.Port}: configured twice"); continue; }
				ok.Add(l);
			}
			return (ok, skipped);
		}

		/// Takes every complete frame out of the buffer (bytes before a start byte are line noise and dropped). Returns
		/// false when what is left is larger than a frame may be — the connection is then closed.
		public static bool TryTake(List<byte> buffer, List<string> messages)
		{
			while (true)
			{
				int s = buffer.IndexOf(Start);
				if (s < 0) { buffer.Clear(); return true; }
				if (s > 0) buffer.RemoveRange(0, s);
				int e = -1;
				for (int i = 1; i + 1 < buffer.Count; i++) if (buffer[i] == End && buffer[i + 1] == Cr) { e = i; break; }
				if (e < 0) return buffer.Count <= MaxFrame;
				messages.Add(Encoding.UTF8.GetString(buffer.GetRange(1, e - 1).ToArray()));
				buffer.RemoveRange(0, e + 2);
			}
		}

		public static byte[] Frame(string message)
		{
			var body = Encoding.UTF8.GetBytes(message);
			var b = new byte[body.Length + 3];
			b[0] = Start; body.CopyTo(b, 1); b[^2] = End; b[^1] = Cr;
			return b;
		}

		public static bool Allowed(HospitalMllpListenerOptions l, IPAddress? client)
		{
			if (l.AllowedClients.Count == 0) return true;
			if (client == null) return false;
			var c = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;
			return l.AllowedClients.Any(a => IPAddress.TryParse(a, out var x) && x.Equals(c));
		}
	}

	/// The one path an interface message is filed through, over HTTPS (HospitalInterfaceApiController) or MLLP.
	public static class HospitalInterfaceInbound
	{
		/// The interface a key opens for one of the given kinds, and the context of the employee it acts for (null: refused).
		public static async Task<(HospitalInterface I, BusinessContext Ctx)?> OpenAsync(IServiceProvider sp, string? code, string? key, IReadOnlyList<string> kinds, CancellationToken ct)
		{
			var svc = ActivatorUtilities.CreateInstance<HospitalInterfaceService>(sp);
			foreach (var kind in kinds)
			{
				var i = await svc.ByKeyAsync(code, key, kind, DateTime.UtcNow, ct);
				if (i == null) continue;
				var ctx = await sp.GetRequiredService<IBusinessContextFactory>().ForEmployeeAsync(i.ActsAsEmployeeId, null, ct);
				return ctx == null || ctx.CompanyId != i.CompanyId ? null : (i, ctx);
			}
			return null;
		}

		/// An analyser's ORU^R01 through the laboratory's receive path; the HL7 acknowledgement back.
		public static async Task<string> LabAsync(IServiceProvider sp, HospitalInterface i, BusinessContext ctx, string raw, CancellationToken ct)
		{
			raw = Unframe(raw);
			var r = await ActivatorUtilities.CreateInstance<HospitalLabDeviceService>(sp).ReceiveAsync(ctx, raw, i.ActsAsEmployeeId, DateTime.UtcNow, ct);
			string control = HospitalInterfaceRules.ControlId(raw) ?? "";
			if (!r.Ok) return HospitalInterfaceRules.Ack(control, "AR", r.Error, DateTime.UtcNow);
			var m = await sp.GetRequiredService<CrossDbContext>().Set<HospitalDeviceMessage>().AsNoTracking()
				.Where(x => x.ID == r.Id && x.CompanyId == i.CompanyId).Select(x => new { x.Status, x.Detail }).FirstOrDefaultAsync(ct);
			return HospitalInterfaceRules.Ack(control, HospitalInterfaceRules.AckCode(m?.Status ?? ""), m?.Detail, DateTime.UtcNow);
		}

		/// An ECG cart's measurements filed on the requested study; the HL7 acknowledgement back.
		public static async Task<string> EcgAsync(IServiceProvider sp, BusinessContext ctx, string raw, CancellationToken ct)
		{
			raw = Unframe(raw);
			var r = await ActivatorUtilities.CreateInstance<HospitalCardiologyService>(sp).ImportAsync(ctx, raw, DateTime.UtcNow, ct);
			return HospitalInterfaceRules.Ack(HospitalInterfaceRules.ControlId(raw) ?? "", r.Ack, r.Detail, DateTime.UtcNow);
		}

		/// One MLLP message on a configured port: refused (AR, nothing filed) unless the key still opens the interface.
		public static async Task<string> MllpAsync(IServiceProvider sp, HospitalMllpListenerOptions l, string raw, CancellationToken ct)
		{
			var open = await OpenAsync(sp, l.Interface, l.Key, new[] { HospitalInterfaceKind.LabResults, HospitalInterfaceKind.CardioResults }, ct);
			if (open == null) return HospitalInterfaceRules.Ack(HospitalInterfaceRules.ControlId(raw) ?? "", "AR", "refused", DateTime.UtcNow);
			return open.Value.I.Kind == HospitalInterfaceKind.CardioResults
				? await EcgAsync(sp, open.Value.Ctx, raw, ct)
				: await LabAsync(sp, open.Value.I, open.Value.Ctx, raw, ct);
		}

		/// An MLLP frame passed on as it came: its start and end bytes dropped.
		public static string Unframe(string raw) => raw.Trim('\v', '\x1c', '\r', '\n', ' ');
	}

	public sealed class HospitalMllpListenerService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly IConfiguration? _config;   // optional: no configuration means no listener
		private readonly ILogger<HospitalMllpListenerService> _log;
		private readonly SemaphoreSlim _connections = new(16);
		private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

		public HospitalMllpListenerService(IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification, ILogger<HospitalMllpListenerService> log, IConfiguration? config = null)
		{ _scopes = scopes; _gate = gate; _certification = certification; _config = config; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stop)
		{
			var (listeners, skipped) = HospitalMllp.Read(_config);
			foreach (var s in skipped) _log.LogWarning("Hospital MLLP listener skipped — {Reason}.", s);
			if (listeners.Count == 0) { _log.LogInformation("Hospital MLLP: no listener configured (Hospital:Mllp:Listeners); no port is open."); return; }
			if (_certification.BackgroundWritersSuppressed) { _log.LogInformation("Hospital MLLP is suppressed: this host is a certification runtime."); return; }
			if (!await _gate.WaitUntilAllowedAsync(nameof(HospitalMllpListenerService), stop)) return;
			await Task.WhenAll(listeners.Select(l => ListenAsync(l, stop)));
		}

		private async Task ListenAsync(HospitalMllpListenerOptions l, CancellationToken stop)
		{
			TcpListener tcp;
			try { tcp = new TcpListener(IPAddress.Parse(l.Bind), l.Port); tcp.Start(); }
			catch (Exception ex) { _log.LogError(ex, "Hospital MLLP: port {Port} on {Bind} could not be opened.", l.Port, l.Bind); return; }
			_log.LogInformation("Hospital MLLP: listening on {Bind}:{Port} for interface {Interface}.", l.Bind, l.Port, l.Interface);
			try
			{
				while (!stop.IsCancellationRequested)
				{
					var client = await tcp.AcceptTcpClientAsync(stop);
					var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
					if (!HospitalMllp.Allowed(l, remote)) { _log.LogWarning("Hospital MLLP: {Client} refused on port {Port} (not an allowed client).", remote, l.Port); client.Dispose(); continue; }
					if (!await _connections.WaitAsync(0, stop)) { _log.LogWarning("Hospital MLLP: {Client} refused on port {Port} (too many connections).", remote, l.Port); client.Dispose(); continue; }
					_ = Task.Run(async () => { try { await ServeAsync(l, client, stop); } finally { client.Dispose(); _connections.Release(); } }, stop);
				}
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
			finally { tcp.Stop(); }
		}

		private async Task ServeAsync(HospitalMllpListenerOptions l, TcpClient client, CancellationToken stop)
		{
			var stream = client.GetStream();
			var buffer = new List<byte>(); var chunk = new byte[8192]; var messages = new List<string>();
			try
			{
				while (!stop.IsCancellationRequested)
				{
					using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
					idle.CancelAfter(Idle);
					int n = await stream.ReadAsync(chunk, idle.Token);
					if (n == 0) return;
					buffer.AddRange(chunk.AsSpan(0, n).ToArray());
					messages.Clear();
					if (!HospitalMllp.TryTake(buffer, messages)) { _log.LogWarning("Hospital MLLP: a frame over 1 MB on port {Port}; connection closed.", l.Port); return; }
					foreach (var m in messages)
					{
						string ack;
						try
						{
							using var scope = _scopes.CreateScope();
							ack = await HospitalInterfaceInbound.MllpAsync(scope.ServiceProvider, l, m, stop);
						}
						catch (Exception ex) when (ex is not OperationCanceledException)
						{
							_log.LogError(ex, "Hospital MLLP: a message on port {Port} could not be filed.", l.Port);
							ack = HospitalInterfaceRules.Ack(HospitalInterfaceRules.ControlId(m) ?? "", "AR", "not filed", DateTime.UtcNow);
						}
						await stream.WriteAsync(HospitalMllp.Frame(ack), stop);
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (IOException) { }
		}
	}
}
