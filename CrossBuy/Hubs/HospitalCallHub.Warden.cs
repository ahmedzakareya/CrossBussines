using System.Diagnostics;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Hubs
{
	// ==========================================================================================
	// HOSPITAL H176 — the calls' warden. A tab that drops out of an ANSWERED call is given time to come back (Resume /
	// GroupResume): a network blip or a restart of the server must not end a consultation. But a tab that never comes back
	// (closed, the device switched off) must not leave the call open for ever — its people would stay "busy" and could
	// neither call nor be called. Every 20 seconds:
	//   · a two-person call whose side has been away more than 90 seconds ends, measured to the moment that side dropped;
	//   · a group call whose every member has been away more than 90 seconds ends, measured to the last one dropping
	//     (members away while others remain are taken out by those others' GroupPing);
	//   · after a restart, an answered call nobody resumed within three minutes ends at the restart (the moment it lost
	//     its server) — rather than at the four hours the old sweep allowed, with four hours for its length.
	// A hub is created per message, so the warden runs on its own with a scope and the hub context.
	// ==========================================================================================
	public sealed partial class HospitalCallHub
	{
		private static readonly TimeSpan AwayGrace = TimeSpan.FromSeconds(90);
		private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
		private static Timer? _warden;
		private static int _wardenBusy;

		private void StartWarden()
		{
			if (_warden != null) return;
			var scopes = _sp.GetRequiredService<IServiceScopeFactory>();
			var hub = _sp.GetRequiredService<IHubContext<HospitalCallHub>>();
			var t = new Timer(_ => _ = WardenTickAsync(scopes, hub), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
			if (Interlocked.CompareExchange(ref _warden, t, null) != null) t.Dispose();
		}

		private static async Task WardenTickAsync(IServiceScopeFactory scopes, IHubContext<HospitalCallHub> hub)
		{
			if (Interlocked.Exchange(ref _wardenBusy, 1) == 1) return;
			try
			{
				var now = DateTime.UtcNow;
				using var scope = scopes.CreateScope();
				var sp = scope.ServiceProvider;
				var db = sp.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
				var svc = ActivatorUtilities.CreateInstance<HospitalCallService>(sp);
				var groups = ActivatorUtilities.CreateInstance<HospitalCallGroupService>(sp);

				// two-person calls with a side gone for good
				foreach (var live in _calls.Values.Where(l => l.CalleeConn != null).ToList())
				{
					var away = new[] { (live.CallerId, live.CallerAway), (live.CalleeId, live.CalleeAway) }.Where(x => x.Item2 != null).OrderBy(x => x.Item2).FirstOrDefault();
					if (away.Item2 is not DateTime since || now - since < AwayGrace || !_calls.TryRemove(live.CallId, out _)) continue;
					var row = await svc.MoveAsync(live.CompanyId, live.CallId, away.Item1, "drop", null, since);
					var msg = new { callId = live.CallId, status = row?.Status ?? HospitalCallStatus.Ended, reply = (string?)null, seconds = row?.DurationSeconds };
					foreach (var conn in new[] { live.CallerConn, live.CalleeConn }) if (!string.IsNullOrEmpty(conn)) await hub.Clients.Client(conn).SendAsync("ended", msg);
					foreach (var e in new[] { live.CallerId, live.CalleeId })
					{
						if (_busy.TryGetValue(e, out var cid) && cid == live.CallId) _busy.TryRemove(e, out _);
						await hub.Clients.Group(Co(live.CompanyId)).SendAsync("presence", new { employeeId = e, state = Presence(e) });
					}
				}

				// group calls everyone has left without saying so
				foreach (var g in _groups.Values.ToList())
				{
					if (g.Joined.IsEmpty || g.Joined.Values.Any(c => c.Length > 0) || g.Away.IsEmpty) continue;
					var last = g.Away.Values.Max();
					if (now - last < AwayGrace || !_groups.TryRemove(g.CallId, out _)) continue;
					await groups.EndAsync(g.CompanyId, g.CallId, last);
					foreach (var emp in g.Joined.Keys.Concat(g.Ringing.Keys))
					{
						if (_busy.TryGetValue(emp, out var cid) && cid == g.CallId) _busy.TryRemove(emp, out _);
						if (g.Ringing.ContainsKey(emp)) await hub.Clients.Group(Emp(emp)).SendAsync("ended", new { callId = g.CallId, status = HospitalCallStatus.Missed });
						await hub.Clients.Group(Co(g.CompanyId)).SendAsync("presence", new { employeeId = emp, state = Presence(emp) });
					}
				}

				// after a restart: answered calls that lost their server and that nobody came back to
				if (now - ProcessStartUtc > TimeSpan.FromMinutes(3))
				{
					var live1 = _calls.Keys.ToList(); var liveG = _groups.Keys.ToList();
					var orphans = await db.Set<HospitalCall>().Where(c => c.Status == HospitalCallStatus.Answered && c.AnsweredAt < ProcessStartUtc).ToListAsync();
					foreach (var c in orphans.Where(c => !(c.IsGroup ? liveG : live1).Contains(c.ID)))
					{
						if (c.IsGroup) { await groups.EndAsync(c.CompanyId, c.ID, ProcessStartUtc); continue; }
						c.Status = HospitalCallStatus.Ended; c.EndedAt = ProcessStartUtc;
						c.DurationSeconds = Math.Max(0, (int)(ProcessStartUtc - (c.AnsweredAt ?? c.StartedAt)).TotalSeconds);
					}
					if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
				}
			}
			catch (Exception) { }   // the next tick tries again; a missing table (not installed) lands here too
			finally { Interlocked.Exchange(ref _wardenBusy, 0); }
		}
	}
}
