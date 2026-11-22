using System.Threading.Channels;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// WAKING CLOSED PHONES (owner, 2026-11-22: «Google FCM, no content»). A producer (a chat message, a call that rings,
	// a missed call) says WHO should hear and WHAT KIND of news it is; this queue does the sending off the request, so
	// a slow or unreachable Google never delays a message or a ring.
	//
	//   * The wake-up carries only the kind (FcmClient): the app fetches the rest from api/chat/push/summary.
	//   * Each item runs in a worker scope bound to its company (WorkerScope.ForCompany, the platform rule for anything
	//     that runs outside a request), with the device tokens read from ICommPushTokenStore.
	//   * Someone in do-not-disturb is not woken for a message (a call to them is already turned into a missed call).
	//   * A token Google reports as dead is retired at once (soft — the row stays as evidence).
	//   * Nothing configured (no FCM service account) or no token table → every item is dropped quietly.
	//
	// Started on first use from the request that produced it (no hosted service, so no DI registration to add), and
	// bounded: if Google is down for long, the oldest wake-ups are dropped — a stale "you have something" is worthless.
	// ==========================================================================================
	public static class PushWake
	{
		private sealed record Item(int CompanyId, int[] EmployeeIds, string Kind);

		private static readonly Channel<Item> Queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
		private static int _started;
		private static IServiceScopeFactory? _scopes;

		/// kind: "msg" (a message, a mention, a missed call) or "call" (ringing now: urgent, short-lived).
		public static void Enqueue(IServiceProvider services, int companyId, IEnumerable<int> employeeIds, string kind)
		{
			var ids = employeeIds.Where(i => i > 0).Distinct().ToArray();
			if (ids.Length == 0 || companyId <= 0) return;
			if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
			{
				_scopes = services.GetRequiredService<IServiceScopeFactory>();
				_ = Task.Run(RunAsync);
			}
			Queue.Writer.TryWrite(new Item(companyId, ids, kind == "call" ? "call" : "msg"));
		}

		/// Can this person's phone be woken now? Push configured, the token table present, and at least one phone
		/// registered. A call to someone whose app is closed rings through push only when this is true; otherwise it is
		/// recorded as missed at once, as before.
		/// Push configured and the token table present (nothing about any one person).
		public static Task<bool> EnabledAsync(IServiceProvider services)
		{
			var fcm = new FcmClient(services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<IConfiguration>(),
				services.GetRequiredService<ILoggerFactory>().CreateLogger("CrossBuy.Push"));
			var store = services.GetService<ICommPushTokenStore>();
			return Task.FromResult(fcm.IsEnabled && store != null && store.IsProvisioned);
		}

		public static async Task<bool> CanWakeAsync(IServiceProvider services, int companyId, int employeeId)
		{
			try
			{
				var fcm = new FcmClient(services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<IConfiguration>(),
					services.GetRequiredService<ILoggerFactory>().CreateLogger("CrossBuy.Push"));
				if (!fcm.IsEnabled) return false;
				var store = services.GetService<ICommPushTokenStore>();
				if (store == null || !store.IsProvisioned) return false;
				return (await store.ForRecipientAsync(companyId, employeeId)).Any(t => t.Platform != "web");
			}
			catch (Exception) { return false; }
		}

		private static async Task RunAsync()
		{
			await foreach (var it in Queue.Reader.ReadAllAsync())
			{
				ILogger? log = null;
				try
				{
					using var scope = WorkerScope.ForCompany(_scopes!, it.CompanyId);
					var sp = scope.ServiceProvider;
					log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CrossBuy.Push");
					var fcm = new FcmClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(), log);
					if (!fcm.IsEnabled) continue;
					var store = sp.GetService<ICommPushTokenStore>();
					if (store == null || !store.IsProvisioned) continue;
					var db = sp.GetRequiredService<CrossDbContext>();
					int sent = 0;
					foreach (var emp in it.EmployeeIds)
					{
						if (it.Kind == "msg" && await ChatPlusService.InDndAsync(db, emp)) continue;
						foreach (var t in await store.ForRecipientAsync(it.CompanyId, emp))
						{
							if (t.Platform == "web") continue;   // a browser has the page's own realtime connection
							var r = await fcm.WakeAsync(t.Token, it.Kind, CancellationToken.None);
							if (r == FcmClient.Result.Sent) sent++;
							else if (r == FcmClient.Result.DeadToken)
							{
								await store.RevokeAsync(it.CompanyId, t.Token, "fcm_unregistered", null);
								if (!(await store.ForRecipientAsync(it.CompanyId, emp)).Any(x => x.Platform != "web")) CrossBuy.Hubs.HospitalCallHub.SetPhone(emp, false);
							}
						}
					}
					if (sent > 0) log.LogInformation("Push: {Count} {Kind} wake-ups sent for company {CompanyId}", sent, it.Kind, it.CompanyId);
				}
				catch (Exception e)
				{
					log?.LogWarning(e, "Push: a wake-up batch failed");   // never takes the queue down
				}
			}
		}
	}
}
