using System.Collections.Concurrent;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Chat;
using Microsoft.AspNetCore.SignalR;

namespace CrossBuy.Hubs
{
	// ==========================================================================================
	// GUESTS IN A GROUP CALL (owner, 2026-11-22). A guest from outside the company, invited into a group conversation
	// (ChatGuestService), joins a group call started from THAT conversation — from their guest page, with no account.
	//
	//   * The guest has no hub connection: their page talks over plain HTTP (api/guest/call, ChatGuestController) and
	//     polls an inbox here for what the members' devices send them. The members' side is unchanged: a guest is one
	//     more person in the call, with the id −guestId, named by the guest's own name and marked as a guest.
	//   * Device to device as in any mesh call (the guest offers, as every joiner does); no media server: a call on the
	//     media server takes no guest. The call's cap counts the guest.
	//   * No recording with a guest: a guest cannot join a recorded call (or one being asked to be), and a recording is
	//     not asked while a guest is in it — the guest has no way to consent.
	//   * A guest unheard of for 30 seconds has left. When the call ends, or their link expires or is revoked, they are out.
	// ==========================================================================================
	public sealed partial class HospitalCallHub
	{
		internal sealed class GuestIn
		{
			public int GuestId; public string Name = ""; public DateTime Seen = DateTime.UtcNow;
			public readonly ConcurrentQueue<object> Inbox = new();
		}
		private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, GuestIn>> _guestCalls = new();   // call → guest → state

		private static ConcurrentDictionary<int, GuestIn>? GuestsIn(int callId) => _guestCalls.TryGetValue(callId, out var d) ? d : null;
		private static bool HasGuests(int callId) => GuestsIn(callId) is { IsEmpty: false };
		private static object GuestCard(GuestIn x) => new { id = -x.GuestId, name = x.Name, nameEn = x.Name, photo = (string?)null, initials = Initials(x.Name), title = "", department = "", guest = true };
		private static string Initials(string n)
		{
			var parts = (n ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)] : parts[0][..1] + parts[^1][..1];
		}
		private static void ToGuests(int callId, object message, int exceptGuest = 0)
		{
			if (GuestsIn(callId) is not { } d) return;
			foreach (var x in d.Values) if (x.GuestId != exceptGuest && x.Inbox.Count < 500) x.Inbox.Enqueue(message);
		}

		/// The live group call of a guest's conversation, if any (their page offers to join it).
		public static (int CallId, bool Video, int People)? GuestCallOf(ChatGuest guest)
			=> _groups.Values.Where(g => g.CompanyId == guest.CompanyId && g.ConversationId == guest.ConversationId && !g.Joined.IsEmpty)
				.Select(g => ((int, bool, int)?)(g.CallId, g.Video, g.Joined.Count + (GuestsIn(g.CallId)?.Count ?? 0))).FirstOrDefault();

		/// The guest joins: the members' devices learn of them ("groupjoined"), and the guest's page then offers a
		/// connection to each person in the call. `sp` is a scope bound to the guest's company.
		public static async Task<object> GuestJoinAsync(IHubContext<HospitalCallHub> hub, IServiceProvider sp, ChatGuest guest, int callId)
		{
			if (!_groups.TryGetValue(callId, out var g) || g.CompanyId != guest.CompanyId || g.ConversationId != guest.ConversationId || g.Joined.IsEmpty) return new { ok = false, error = "gone" };
			if (g.RecId != null) return new { ok = false, error = "recording" };
			if (_guestRemoved.ContainsKey((callId, guest.ID))) return new { ok = false, error = "removed" };
			var svc = ActivatorUtilities.CreateInstance<HospitalCallService>(sp);
			var s = await svc.SettingsAsync(g.CompanyId);
			if (SfuReady(s)) return new { ok = false, error = "server" };
			var d = _guestCalls.GetOrAdd(callId, _ => new());
			var x = d.GetOrAdd(guest.ID, _ => new GuestIn { GuestId = guest.ID, Name = guest.Name });
			x.Seen = DateTime.UtcNow;
			if (g.Joined.Count + d.Count > CapOf(s)) { d.TryRemove(guest.ID, out _); return new { ok = false, error = "full" }; }
			while (x.Inbox.TryDequeue(out _)) { }
			var card = GuestCard(x);
			foreach (var conn in Conns(g)) await hub.Clients.Client(conn).SendAsync("groupjoined", new { callId, card });
			ToGuests(callId, new { kind = "joined", card }, guest.ID);
			var people = await svc.PeopleAsync(g.CompanyId, DateTime.UtcNow, g.Joined.Keys.ToList());
			var members = people.Select(p => Card(p)).Concat(d.Values.Where(o => o.GuestId != guest.ID).Select(GuestCard)).ToList();
			return new { ok = true, callId, video = g.Video, me = card, members, ice = CrossBuy.Views.Hospital.HospitalTeleRoomModel.IceFrom(sp.GetService<IConfiguration>()) };
		}

		/// One connection message from the guest to one person in the call (a member, or another guest).
		public static async Task<bool> GuestSignalAsync(IHubContext<HospitalCallHub> hub, ChatGuest guest, int callId, int to, string payload)
		{
			if (payload == null || payload.Length > 64_000 || GuestsIn(callId) is not { } d || !d.TryGetValue(guest.ID, out var x)) return false;
			x.Seen = DateTime.UtcNow;
			if (!_groups.TryGetValue(callId, out var g)) return false;
			if (to > 0) { if (!g.Joined.TryGetValue(to, out var conn) || conn.Length == 0) return false; await hub.Clients.Client(conn).SendAsync("gsignal", new { callId, fromId = -guest.ID, payload }); return true; }
			if (d.TryGetValue(-to, out var other)) { other.Inbox.Enqueue(new { kind = "signal", fromId = -guest.ID, payload }); return true; }
			return false;
		}

		/// What has come for the guest since their last look; null when they are not (or no longer) in the call.
		public static async Task<object?> GuestPollAsync(IHubContext<HospitalCallHub> hub, ChatGuest guest, int callId)
		{
			if (!_groups.TryGetValue(callId, out var g)) { _guestCalls.TryRemove(callId, out _); return new { ended = true, events = Array.Empty<object>() }; }
			if (GuestsIn(callId) is not { } d || !d.TryGetValue(guest.ID, out var x)) return _guestRemoved.ContainsKey((callId, guest.ID)) ? new { ended = true, removed = true, events = Array.Empty<object>() } : null;
			x.Seen = DateTime.UtcNow;
			await PruneGuestsAsync(hub, g);
			var list = new List<object>(); while (list.Count < 200 && x.Inbox.TryDequeue(out var m)) list.Add(m);
			return new { ended = false, events = list };
		}

		public static async Task GuestLeaveAsync(IHubContext<HospitalCallHub> hub, int callId, int guestId)
		{
			if (GuestsIn(callId) is not { } d || !d.TryRemove(guestId, out _)) return;
			if (_groups.TryGetValue(callId, out var g)) foreach (var conn in Conns(g)) await hub.Clients.Client(conn).SendAsync("groupleft", new { callId, employeeId = -guestId, removed = false });
			ToGuests(callId, new { kind = "left", id = -guestId });
			if (d.IsEmpty) _guestCalls.TryRemove(callId, out _);
		}

		/// A guest unheard of for 30 seconds has left (their page closed, or their network went).
		private static async Task PruneGuestsAsync(IHubContext<HospitalCallHub> hub, GroupLive g)
		{
			if (GuestsIn(g.CallId) is not { } d) return;
			foreach (var x in d.Values.Where(v => DateTime.UtcNow - v.Seen > TimeSpan.FromSeconds(30)).ToList()) await GuestLeaveAsync(hub, g.CallId, x.GuestId);
		}

		/// The host removes a guest from the call: their page is told so, and they do not come back into this call.
		private static readonly ConcurrentDictionary<(int Call, int Guest), bool> _guestRemoved = new();
		private Task GuestRemoveAsync(int callId, int guestId)
		{
			_guestRemoved[(callId, guestId)] = true;
			return GuestLeaveAsync(_sp.GetRequiredService<IHubContext<HospitalCallHub>>(), callId, guestId);
		}

		// ---- the hooks the group call takes (HospitalCallHub.Group.cs) ----
		/// A member's device sends a connection message to a guest (a negative id): into that guest's inbox.
		private static bool SignalToGuest(int callId, int fromEmployee, int toGuest, string payload)
		{
			if (toGuest >= 0 || GuestsIn(callId) is not { } d || !d.TryGetValue(-toGuest, out var x)) return false;
			x.Inbox.Enqueue(new { kind = "signal", fromId = fromEmployee, payload }); return true;
		}
		/// A member joined after the guest: the guest's page offers them a connection.
		private static void GuestsMemberJoined(int callId, object? card) { if (card != null) ToGuests(callId, new { kind = "joined", card }); }
		private static void GuestsMemberLeft(int callId, int emp) => ToGuests(callId, new { kind = "left", id = emp });
		private static void GuestsCallEnded(int callId) { ToGuests(callId, new { kind = "ended" }); }
		private Task PruneGuestsHereAsync(GroupLive g) => PruneGuestsAsync(_sp.GetRequiredService<IHubContext<HospitalCallHub>>(), g);
	}
}
