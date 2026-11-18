using System.Collections.Concurrent;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Hubs
{
	// ==========================================================================================
	// HOSPITAL H176 — group calls on the calls hub. Each device connects to each other one (mesh, at most four people —
	// HospitalCallGroupRules.MeshCap); the hub rings, keeps who is in the call, and passes each pair's connection offer
	// and candidates to the ONE person it is meant for. Media never comes here.
	//
	// Who connects to whom: a person who JOINS opens a connection to each person already in the call (they answer it).
	// So every pair has exactly one offerer and no two offers cross.
	// ==========================================================================================
	public sealed partial class HospitalCallHub
	{
		private sealed class GroupLive
		{
			public int CallId, CompanyId, HostId;
			public bool Video;
			public int? PatientId;
			public string? Reason;
			public readonly ConcurrentDictionary<int, string> Joined = new();      // employee → connection ("" while their tab reconnects)
			public readonly ConcurrentDictionary<int, DateTime> Away = new();      // employee → when their tab dropped
			public readonly ConcurrentDictionary<int, DateTime> Ringing = new();   // employee → invited at
			public int? RecId, RecBy, RecorderId;   // RecBy asked; RecorderId's browser records (a browser, never a phone)
			public readonly ConcurrentDictionary<int, bool> RecYes = new();
			public bool Recording;
			public int? ConversationId;   // the group conversation it was started from (Communication)
			public DateTime StartedAt = DateTime.UtcNow;
		}
		private static readonly ConcurrentDictionary<int, GroupLive> _groups = new();

		/// Communication: the group calls going on now in this company's conversations — the conversation shows them live.
		public static IReadOnlyList<(int ConversationId, int CallId, bool Video, DateTime StartedAt, int[] Joined)> LiveInConversations(int companyId)
			=> _groups.Values.Where(g => g.CompanyId == companyId && g.ConversationId != null && !g.Joined.IsEmpty)
				.Select(g => (g.ConversationId!.Value, g.CallId, g.Video, g.StartedAt, g.Joined.Keys.ToArray())).ToList();

		private static bool RingingInGroup(int employeeId) => _groups.Values.Any(g => g.Ringing.ContainsKey(employeeId));
		private static IEnumerable<string> Conns(GroupLive g) => g.Joined.Values.Where(c => c.Length > 0);

		/// A member away for more than a minute has really gone: they leave the call.
		private async Task PruneAwayAsync(GroupLive g)
		{
			foreach (var (emp, since) in g.Away.ToList())
				if (DateTime.UtcNow - since > TimeSpan.FromSeconds(60)) { g.Away.TryRemove(emp, out _); await LeaveAsync(g, emp, "leave", null); }
		}

		/// Sent every half minute by each tab in a group call: keeps the call tidy (a member away for more than a minute leaves).
		public async Task GroupPing(int callId)
		{
			if (await MeAsync() is not var (me, _) || !_groups.TryGetValue(callId, out var g) || !g.Joined.ContainsKey(me)) return;
			await PruneAwayAsync(g);
		}

		/// A tab that was in a group call reconnects (a drop, or the server restarted): the call is rebuilt from its record
		/// if needed, and this connection takes the member's place. The connections between devices did not stop.
		public async Task<object> GroupResume(int callId)
		{
			if (await MeAsync() is not var (me, co)) return new { ok = false };
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == co && c.IsGroup);
			if (call == null || call.Status != HospitalCallStatus.Answered) return new { ok = false };
			var parts = await GroupSvc.ParticipantsAsync(co, callId);
			if (!parts.Any(p => p.EmployeeId == me && (p.State == HospitalCallParticipantState.Host || p.State == HospitalCallParticipantState.Joined))) return new { ok = false };
			var g = _groups.GetOrAdd(callId, _ => new GroupLive { CallId = callId, CompanyId = co, HostId = call.CallerEmployeeId, Video = call.IsVideo, PatientId = call.PatientId, Reason = call.Reason, ConversationId = call.ConversationId, StartedAt = call.AnsweredAt ?? call.StartedAt });
			if (g.Joined.IsEmpty)
			{
				// rebuilt after a restart: everyone in it is "away" until their tab comes back
				foreach (var p in parts.Where(p => p.State == HospitalCallParticipantState.Host || p.State == HospitalCallParticipantState.Joined)) { g.Joined[p.EmployeeId] = ""; g.Away[p.EmployeeId] = DateTime.UtcNow; }
				var rec = await _db.Set<HospitalCallRecording>().AsNoTracking().Where(r => r.CallId == callId && r.CompanyId == co && r.Status == HospitalCallRecordingStatus.Recording).FirstOrDefaultAsync();
				if (rec != null) { g.RecId = rec.ID; g.RecBy = rec.RequestedByEmployeeId; g.RecorderId = HospitalCallRecordingRules.RecorderOf(rec); g.Recording = true; }
			}
			g.Joined[me] = Context.ConnectionId; g.Away.TryRemove(me, out _); _busy[me] = callId;
			await PresenceOfAsync(co, me);
			foreach (var conn in Conns(g)) if (conn != Context.ConnectionId) await Clients.Client(conn).SendAsync("groupback", new { callId, employeeId = me });
			await PruneAwayAsync(g);
			return new { ok = true, hostId = g.HostId };
		}
		private HospitalCallGroupService GroupSvc => ActivatorUtilities.CreateInstance<HospitalCallGroupService>(_sp);

		private static bool SfuReady(HospitalCallSettings s) => HospitalCallGroupRules.SfuReady(s.SfuUrl, s.SfuApiKey, s.SfuApiSecret);
		private static int CapOf(HospitalCallSettings s) => HospitalCallGroupRules.Cap(s.GroupMax, SfuReady(s));

		/// With the internal media server configured, the group call's media goes through it: this person's room token.
		private async Task<object?> SfuForAsync(HospitalCallSettings s, int companyId, int callId, int employeeId)
		{
			if (!SfuReady(s)) return null;
			var me = (await Svc.PeopleAsync(companyId, DateTime.UtcNow, new[] { employeeId })).FirstOrDefault();
			return new
			{
				url = s.SfuUrl,
				token = HospitalCallSfu.Token(s.SfuApiKey!, s.SfuApiSecret!, employeeId.ToString(), me == null ? null : DisplayName.Of(me.Name, me.NameEn),
					HospitalCallSfu.Room(companyId, callId), DateTime.UtcNow, TimeSpan.FromHours(6)),
			};
		}

		private async Task<object> MembersAsync(GroupLive g)
		{
			var ids = g.Joined.Keys.Concat(g.Ringing.Keys).Distinct().ToList();
			var people = (await Svc.PeopleAsync(g.CompanyId, DateTime.UtcNow, ids)).ToDictionary(p => p.EmployeeId);
			return ids.Where(people.ContainsKey).Select(id => new { card = Card(people[id]), state = g.Joined.ContainsKey(id) ? "joined" : "ringing", host = id == g.HostId }).ToList();
		}

		private async Task PresenceOfAsync(int companyId, int employeeId) => await Clients.Group(Co(companyId)).SendAsync("presence", new { employeeId, state = Presence(employeeId) });

		/// Rings the people of a group call (new, or more people added): each one invited if free, recorded as busy if not.
		private async Task<List<string>> RingAsync(GroupLive g, IEnumerable<int> ids, HospitalCallPerson host, HospitalCallService svc, IReadOnlyDictionary<int, HospitalCallPerson> people, bool fresh)
		{
			var settings = await svc.SettingsAsync(g.CompanyId);
			var patient = g.PatientId is int pid ? await svc.PatientCardAsync(g.CompanyId, pid) : null;
			var refused = new List<string>();
			foreach (var id in ids)
			{
				var who = people[id];
				string state = Presence(id) == "on" ? HospitalCallParticipantState.Invited : HospitalCallParticipantState.Busy;
				if (!fresh) await GroupSvc.InviteAsync(g.CompanyId, g.CallId, id, state, DateTime.UtcNow);
				if (state == HospitalCallParticipantState.Busy) { refused.Add(DisplayName.Of(who.Name, who.NameEn)); await Clients.Group(Emp(id)).SendAsync("missed", new { callId = g.CallId }); await NotifyMissedAsync(g.CompanyId, g.CallId, id, g.HostId, g.Video, true); continue; }
				g.Ringing[id] = DateTime.UtcNow;
				await PresenceOfAsync(g.CompanyId, id);
			}
			var members = await MembersAsync(g);
			var (_, kind, _) = await RecSvc.OptionsAsync(g.CompanyId, host, g.Video);
			foreach (var id in g.Ringing.Keys)
			{
				var (may, _, _) = await RecSvc.OptionsAsync(g.CompanyId, people.TryGetValue(id, out var p) ? p : host, g.Video);
				await Clients.Group(Emp(id)).SendAsync("groupincoming", new
				{
					callId = g.CallId, video = g.Video, reason = g.Reason, ringSeconds = settings.RingSeconds, share = settings.ScreenShareEnabled, maxHeight = settings.MaxVideoHeight,
					host = Card(host), members, joined = g.Joined.Count, patient, recording = g.Recording, canRecord = may, recKind = kind,
				});
			}
			return refused;
		}

		/// The host starts a group call with these people — or turns a live two-person call into one (fromCallId): the
		/// other person of that call joins at once, without ringing again.
		public Task<object> GroupInvite(int[] ids, bool video, int? patientId, string? reason, int? fromCallId)
			=> GroupInviteCore(ids, video, patientId, reason, fromCallId, null);

		/// Communication: a group call from a group conversation — everyone called is a member of it, and the call belongs
		/// to it (the conversation shows it live and lists it).
		public async Task<object> GroupInviteIn(int conversationId, int[] ids, bool video)
		{
			if (await MeAsync() is not var (me, co)) return new { ok = false, error = "forbidden" };
			var members = await ConversationMembersAsync(co, conversationId);
			if (!members.Contains(me) || (ids ?? Array.Empty<int>()).Any(i => !members.Contains(i))) return new { ok = false, error = "not_found" };
			return await GroupInviteCore(ids!, video, null, null, null, conversationId);
		}

		/// Communication: a member of the conversation joins the group call going on in it ("Join" on its banner), invited or not.
		public async Task<object> GroupJoinIn(int callId, bool video)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || g.CompanyId != co || g.ConversationId is not int conv) return new { ok = false };
			if (g.Joined.ContainsKey(me)) return new { ok = false, error = "You are already in a call" };
			if (!(await ConversationMembersAsync(co, conv)).Contains(me)) return new { ok = false };
			var s = await Svc.SettingsAsync(co);
			if (g.Joined.Count + g.Ringing.Count(r => r.Key != me) >= CapOf(s)) return new { ok = false, error = "A group call is up to {0} people", name = CapOf(s).ToString() };
			if (!g.Ringing.ContainsKey(me))
			{
				await GroupSvc.InviteAsync(co, callId, me, HospitalCallParticipantState.Invited, DateTime.UtcNow);
				g.Ringing[me] = DateTime.UtcNow;
			}
			return await GroupJoin(callId, video);
		}

		private async Task<HashSet<int>> ConversationMembersAsync(int companyId, int conversationId)
		{
			bool ours = await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == conversationId && c.CompanyID == companyId && c.Kind == "Group");
			if (!ours) return new HashSet<int>();
			return (await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == conversationId).Select(m => m.EmployeeId).ToListAsync()).ToHashSet();
		}

		private async Task<object> GroupInviteCore(int[] ids, bool video, int? patientId, string? reason, int? fromCallId, int? conversationId)
		{
			if (await MeAsync() is not var (me, co)) return new { ok = false, error = "forbidden" };
			var svc = Svc; var now = DateTime.UtcNow;
			if (!await GroupSvc.InstalledAsync()) return new { ok = false, error = HospitalCallGroupService.NoTables };
			var settings = await svc.SettingsAsync(co);
			if (!settings.GroupEnabled) return new { ok = false, error = "Group calls are switched off (call settings)" };
			if (video && !settings.VideoEnabled) video = false;
			// the app's SignalR client cannot send a JSON null: 0 means "none" for both (no record has id 0)
			if (fromCallId <= 0) fromCallId = null;
			if (patientId <= 0) patientId = null;
			int? peer = null; Live? from = null;
			if (fromCallId is int fc)
			{
				if (!_calls.TryGetValue(fc, out from) || from.CalleeConn == null || (from.CallerId != me && from.CalleeId != me)) return new { ok = false, error = "failed" };
				peer = from.CallerId == me ? from.CalleeId : from.CallerId;
				video = from.Video || video; patientId ??= (await _db.Set<HospitalCall>().AsNoTracking().Where(c => c.ID == fc).Select(c => c.PatientId).FirstOrDefaultAsync());
			}
			else if (_busy.ContainsKey(me)) return new { ok = false, error = "You are already in a call" };
			var invite = (ids ?? Array.Empty<int>()).Where(i => i != me && i != peer).Distinct().ToList();
			int cap = CapOf(settings);
			if (invite.Count == 0 || invite.Count + 1 + (peer != null ? 1 : 0) > cap) return new { ok = false, error = "A group call is up to {0} people with you", name = cap.ToString() };
			if (peer == null && invite.Count < 2) return new { ok = false, error = "Choose at least two people for a group call" };
			var people = (await svc.PeopleAsync(co, now, invite.Append(me).Concat(peer is int pp ? new[] { pp } : Array.Empty<int>()))).ToDictionary(p => p.EmployeeId);
			if (!people.TryGetValue(me, out var host) || invite.Any(i => !people.ContainsKey(i))) return new { ok = false, error = "not_found" };
			foreach (var i in invite) if (!await svc.MayCallAsync(co, host, people[i])) return new { ok = false, error = "Your group may not call this person's group (call settings)" };
			if (patientId is int pid && !await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == co)) patientId = null;

			// the two-person call becomes the group: it ends as a call, its two people move into the new one
			if (from != null)
			{
				_calls.TryRemove(from.CallId, out _);
				await svc.MoveAsync(co, from.CallId, me, "hangup", null, now);
			}
			var list = invite.Select(i => (i, Presence(i) == "on" || i == peer ? HospitalCallParticipantState.Invited : HospitalCallParticipantState.Busy)).ToList();
			if (peer is int p0) list.Insert(0, (p0, HospitalCallParticipantState.Invited));
			var row = await GroupSvc.OpenAsync(co, me, list, video, patientId, reason, now);
			var g = new GroupLive { CallId = row.ID, CompanyId = co, HostId = me, Video = video, PatientId = patientId, Reason = row.Reason, ConversationId = conversationId };
			if (conversationId is int cv) await _db.Set<HospitalCall>().Where(c => c.ID == row.ID && c.CompanyId == co).ExecuteUpdateAsync(x => x.SetProperty(c => c.ConversationId, cv));
			g.Joined[me] = Context.ConnectionId;
			_groups[row.ID] = g;
			_busy[me] = row.ID;
			await PresenceOfAsync(co, me);
			if (from != null && peer is int p1)
			{
				// the peer is already talking to me: they join without ringing (their screen moves to the group stage)
				await GroupSvc.MoveAsync(co, row.ID, p1, "join", null, now);
				string peerConn = from.CallerId == p1 ? from.CallerConn : from.CalleeConn!;
				g.Joined[p1] = peerConn; _busy[p1] = row.ID;
				await Clients.Client(peerConn).SendAsync("groupconvert", new { callId = row.ID, fromCallId = from.CallId });
			}
			var refused = await RingAsync(g, invite, host, svc, people, fresh: true);
			var (may, kind, _) = await RecSvc.OptionsAsync(co, host, video);
			var s = settings;
			return new { ok = true, callId = row.ID, video, ringSeconds = s.RingSeconds, share = s.ScreenShareEnabled, maxHeight = s.MaxVideoHeight, cap, sfu = await SfuForAsync(s, co, row.ID, me),
				host = Card(host), members = await MembersAsync(g), patient = patientId is int p2 ? await svc.PatientCardAsync(co, p2) : null, canRecord = may, recKind = kind, busy = refused };
		}

		/// An invited person joins on THIS tab: they open a connection to each person already in the call.
		public async Task<object> GroupJoin(int callId, bool video)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || g.CompanyId != co || !g.Ringing.ContainsKey(me)) return new { ok = false };
			if (_busy.ContainsKey(me)) return new { ok = false, error = "You are already in a call" };
			var now = DateTime.UtcNow;
			if (await GroupSvc.MoveAsync(co, callId, me, "join", null, now) == null) return new { ok = false };
			g.Ringing.TryRemove(me, out _);
			var existing = g.Joined.Keys.ToList();
			g.Joined[me] = Context.ConnectionId; _busy[me] = callId;
			if (g.Recording && g.RecId is int rid) await GroupSvc.ConsentAsync(co, rid, me, "joined", now);   // joining a recorded call is consent
			var meCard = (await Svc.PeopleAsync(co, now, new[] { me })).FirstOrDefault();
			foreach (var (emp, conn) in g.Joined) if (emp != me && conn.Length > 0) await Clients.Client(conn).SendAsync("groupjoined", new { callId, card = meCard == null ? null : Card(meCard) });
			await Clients.GroupExcept(Emp(me), Context.ConnectionId).SendAsync("taken", new { callId });
			await PresenceOfAsync(co, me);
			var host = (await Svc.PeopleAsync(co, now, new[] { g.HostId })).First();
			var (may, kind, _) = await RecSvc.OptionsAsync(co, meCard ?? host, g.Video);
			var s = await Svc.SettingsAsync(co);
			return new { ok = true, callId, video = g.Video, host = Card(host), members = await MembersAsync(g), connectTo = existing, patient = g.PatientId is int pid ? await Svc.PatientCardAsync(co, pid) : null,
				reason = g.Reason, share = s.ScreenShareEnabled, maxHeight = s.MaxVideoHeight, cap = CapOf(s), canRecord = may, recKind = kind,
				recording = g.Recording ? new { recId = g.RecId, requesterId = g.RecBy } : null, sfu = await SfuForAsync(s, co, callId, me) };
		}

		/// The call as a person already in it sees it — the other person of a two-person call that became this group call
		/// asks for it, then opens a connection to each other person in it.
		public async Task<object> GroupInfo(int callId)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || g.CompanyId != co || !g.Joined.ContainsKey(me)) return new { ok = false };
			var now = DateTime.UtcNow;
			var host = (await Svc.PeopleAsync(co, now, new[] { g.HostId })).First();
			var mine = (await Svc.PeopleAsync(co, now, new[] { me })).FirstOrDefault();
			var (may, kind, _) = await RecSvc.OptionsAsync(co, mine ?? host, g.Video);
			var s = await Svc.SettingsAsync(co);
			return new { ok = true, callId, video = g.Video, host = Card(host), members = await MembersAsync(g), connectTo = g.Joined.Keys.Where(e => e != me).ToList(),
				patient = g.PatientId is int pid ? await Svc.PatientCardAsync(co, pid) : null, reason = g.Reason, share = s.ScreenShareEnabled, maxHeight = s.MaxVideoHeight,
				cap = CapOf(s), canRecord = may, recKind = kind, recording = (object?)null, sfu = await SfuForAsync(s, co, callId, me) };
		}

		/// An invited person declines (or their ring ran out).
		public async Task GroupDecline(int callId, bool timedOut)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || !g.Ringing.TryRemove(me, out _)) return;
			await GroupSvc.MoveAsync(co, callId, me, timedOut ? "timeout" : "decline", null, DateTime.UtcNow);
			if (timedOut) await NotifyMissedAsync(co, callId, me, g.HostId, g.Video, true);
			await Clients.Group(Emp(me)).SendAsync("ended", new { callId, status = timedOut ? HospitalCallStatus.Missed : HospitalCallStatus.Declined });
			foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("groupstate", new { callId, employeeId = me, state = timedOut ? "missed" : "declined" });
			await PresenceOfAsync(co, me);
			await EndIfEmptyAsync(g);
		}

		/// I leave; the call goes on for the others (and ends when one person is left).
		public async Task GroupLeave(int callId)
		{
			if (await MeAsync() is not var (me, _) || !_groups.TryGetValue(callId, out var g)) return;
			await LeaveAsync(g, me, "leave", null);
		}

		private async Task LeaveAsync(GroupLive g, int emp, string action, int? by)
		{
			g.Away.TryRemove(emp, out _);
			if (!g.Joined.TryRemove(emp, out var conn)) { if (g.Ringing.TryRemove(emp, out _)) { await GroupSvc.MoveAsync(g.CompanyId, g.CallId, emp, "remove", by, DateTime.UtcNow); await Clients.Group(Emp(emp)).SendAsync("ended", new { callId = g.CallId, status = "Removed" }); foreach (var c in Conns(g)) await Clients.Client(c).SendAsync("groupstate", new { callId = g.CallId, employeeId = emp, state = "removed" }); await PresenceOfAsync(g.CompanyId, emp); } return; }
			if (_busy.TryGetValue(emp, out var cid) && cid == g.CallId) _busy.TryRemove(emp, out _);
			await GroupSvc.MoveAsync(g.CompanyId, g.CallId, emp, action, by, DateTime.UtcNow);
			if (action == "remove" && conn.Length > 0) await Clients.Client(conn).SendAsync("ended", new { callId = g.CallId, status = "Removed" });
			foreach (var c in Conns(g)) await Clients.Client(c).SendAsync("groupleft", new { callId = g.CallId, employeeId = emp, removed = action == "remove" });
			await PresenceOfAsync(g.CompanyId, emp);
			// a group recording made in the leaver's browser stops with them (the file is kept)
			if (g.Recording && (g.RecBy == emp || g.RecorderId == emp)) await StopGroupRecordingAsync(g, emp);
			// the host role passes to the longest-standing person left
			if (emp == g.HostId && g.Joined.Count > 0) { g.HostId = g.Joined.Keys.First(); foreach (var c in Conns(g)) await Clients.Client(c).SendAsync("grouphost", new { callId = g.CallId, hostId = g.HostId }); }
			await EndIfEmptyAsync(g);
		}

		private async Task EndIfEmptyAsync(GroupLive g)
		{
			if (!HospitalCallGroupRules.Ends(g.Joined.Count, g.Ringing.Count)) return;
			await EndGroupAsync(g);
		}

		private async Task EndGroupAsync(GroupLive g)
		{
			if (!_groups.TryRemove(g.CallId, out _)) return;
			await GroupSvc.EndAsync(g.CompanyId, g.CallId, DateTime.UtcNow);
			foreach (var (emp, conn) in g.Joined)
			{
				if (_busy.TryGetValue(emp, out var cid) && cid == g.CallId) _busy.TryRemove(emp, out _);
				if (conn.Length > 0) await Clients.Client(conn).SendAsync("ended", new { callId = g.CallId, status = HospitalCallStatus.Ended });
				await PresenceOfAsync(g.CompanyId, emp);
			}
			foreach (var emp in g.Ringing.Keys) { await Clients.Group(Emp(emp)).SendAsync("ended", new { callId = g.CallId, status = HospitalCallStatus.Missed }); await PresenceOfAsync(g.CompanyId, emp); await NotifyMissedAsync(g.CompanyId, g.CallId, emp, g.HostId, g.Video, true); }
		}

		/// The host adds people (up to the cap).
		public async Task<object> GroupAdd(int callId, int[] ids)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || g.HostId != me) return new { ok = false, error = "failed" };
			var svc = Svc; var s = await svc.SettingsAsync(co);
			int cap = CapOf(s);
			var add = (ids ?? Array.Empty<int>()).Where(i => !g.Joined.ContainsKey(i) && !g.Ringing.ContainsKey(i)).Distinct().ToList();
			if (add.Count == 0) return new { ok = false, error = "failed" };
			if (add.Count > HospitalCallGroupRules.Room(cap, g.Joined.Count, g.Ringing.Count)) return new { ok = false, error = "A group call is up to {0} people with you", name = cap.ToString() };
			var people = (await svc.PeopleAsync(co, DateTime.UtcNow, add.Append(me))).ToDictionary(p => p.EmployeeId);
			if (add.Any(i => !people.ContainsKey(i))) return new { ok = false, error = "not_found" };
			foreach (var i in add) if (!await svc.MayCallAsync(co, people[me], people[i])) return new { ok = false, error = "Your group may not call this person's group (call settings)" };
			var busy = await RingAsync(g, add, people[me], svc, people, fresh: false);
			var members = await MembersAsync(g);
			foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("groupmembers", new { callId, members });
			return new { ok = true, busy };
		}

		/// The host removes someone (in the call, or still ringing).
		public async Task GroupRemove(int callId, int employeeId)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || g.HostId != me || employeeId == me) return;
			await LeaveAsync(g, employeeId, "remove", me);
			var s = await Svc.SettingsAsync(co);
			if (SfuReady(s)) await HospitalCallSfu.RemoveAsync(s.SfuUrl!, s.SfuApiKey!, s.SfuApiSecret!, HospitalCallSfu.Room(co, callId), employeeId.ToString());
		}

		/// The host ends the call for everyone.
		public async Task GroupEnd(int callId)
		{
			if (await MeAsync() is not var (me, _) || !_groups.TryGetValue(callId, out var g) || g.HostId != me) return;
			if (g.Recording) await StopGroupRecordingAsync(g, me);
			await EndGroupAsync(g);
		}

		/// One pair's connection offer, answer or candidate, passed to the one person it is meant for.
		public async Task GroupSignal(int callId, int toEmployeeId, string payload)
		{
			if (payload == null || payload.Length > 64_000) return;
			if (!_groups.TryGetValue(callId, out var g)) return;
			var from = g.Joined.FirstOrDefault(kv => kv.Value == Context.ConnectionId);
			if (from.Value == null || !g.Joined.TryGetValue(toEmployeeId, out var to) || to.Length == 0) return;
			await Clients.Client(to).SendAsync("gsignal", new { callId, fromId = from.Key, payload });
		}

		// ---- a group recording: EVERY person in the call agrees ----------------------------------------------------

		public async Task<object> GroupRecordRequest(int callId, bool picture)
		{
			if (await MeAsync() is not var (me, co) || !_groups.TryGetValue(callId, out var g) || !g.Joined.ContainsKey(me)) return new { ok = false, error = "failed" };
			if (g.RecId != null) return new { ok = false, error = "This call is already being recorded or asked to be" };
			// from the app: a browser in the call records (a phone cannot mix everyone into one file)
			int? recorder = null;
			if (IsApp(g.Joined.TryGetValue(me, out var myConn) ? myConn : null))
			{
				var pc = g.Joined.Where(j => j.Key != me && j.Value.Length > 0 && !IsApp(j.Value)).Select(j => (int?)j.Key).FirstOrDefault();
				if (pc == null) return new { ok = false, error = NeedsComputer };
				recorder = pc;
			}
			var (row, error) = await RecSvc.AskAsync(co, callId, me, DateTime.UtcNow, picture, recorder);
			if (row == null) return new { ok = false, error };
			g.RecId = row.ID; g.RecBy = me; g.RecorderId = recorder ?? me; g.RecYes.Clear();
			var s = await Svc.SettingsAsync(co);
			var by = (await Svc.PeopleAsync(co, DateTime.UtcNow, new[] { me })).FirstOrDefault();
			var patient = g.PatientId is int pid ? await Svc.PatientCardAsync(co, pid) : null;
			foreach (var (emp, conn) in g.Joined)
				if (emp != me && conn.Length > 0) await Clients.Client(conn).SendAsync("recask", new { recId = row.ID, callId, kind = row.Kind, days = s.RecordingDays, by = by == null ? null : Card(by), patient, group = true, waiting = g.Joined.Count - 1 });
			return new { ok = true, recId = row.ID, kind = row.Kind, waiting = g.Joined.Count - 1 };
		}

		public async Task GroupRecordAnswer(int recId, bool agree)
		{
			if (await MeAsync() is not var (me, co)) return;
			var g = _groups.Values.FirstOrDefault(x => x.RecId == recId && x.CompanyId == co);
			if (g == null || !g.Joined.ContainsKey(me) || me == g.RecBy || g.Recording) return;
			var now = DateTime.UtcNow;
			if (!agree)
			{
				await RecSvc.SettleGroupAsync(co, recId, false, now);
				var who = (await Svc.PeopleAsync(co, now, new[] { me })).FirstOrDefault();
				g.RecId = null; g.RecBy = null; g.RecorderId = null; g.RecYes.Clear();
				foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("recclosed", new { recId, refused = true, byName = who == null ? null : DisplayName.Of(who.Name, who.NameEn) });
				return;
			}
			g.RecYes[me] = true;
			await GroupSvc.ConsentAsync(co, recId, me, "agreed", now);
			foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("recagreed", new { recId, employeeId = me });
			if (!HospitalCallGroupRules.AllAgreed(g.Joined.Keys, g.RecBy ?? 0, g.RecYes.Keys)) return;
			var row = await RecSvc.SettleGroupAsync(co, recId, true, now);
			if (row == null) return;
			g.Recording = true;
			foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("recstart", new { recId, callId = g.CallId, kind = row.Kind, requesterId = row.RequestedByEmployeeId, recorderId = HospitalCallRecordingRules.RecorderOf(row), group = true });
		}

		public async Task GroupRecordStop(int recId)
		{
			if (await MeAsync() is not var (me, co)) return;
			var g = _groups.Values.FirstOrDefault(x => x.RecId == recId && x.CompanyId == co);
			if (g == null || !g.Joined.ContainsKey(me)) return;
			if (!g.Recording)
			{
				// withdrawn (or ended) before everyone answered
				await RecSvc.SettleGroupAsync(co, recId, false, DateTime.UtcNow);
				g.RecId = null; g.RecBy = null; g.RecorderId = null; g.RecYes.Clear();
				foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("recclosed", new { recId, refused = false });
				return;
			}
			await StopGroupRecordingAsync(g, me);
		}

		private async Task StopGroupRecordingAsync(GroupLive g, int by)
		{
			if (g.RecId is not int recId) return;
			await RecSvc.MarkStoppedAsync(g.CompanyId, recId, by);
			var msg = new { recId, byId = by };
			foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("recstop", msg);
			g.Recording = false; g.RecId = null; g.RecBy = null; g.RecorderId = null; g.RecYes.Clear();
		}

		/// A tab closing takes its person out of the group call.
		private async Task DropFromGroupsAsync()
		{
			foreach (var g in _groups.Values.ToList())
			{
				var mine = g.Joined.FirstOrDefault(kv => kv.Value == Context.ConnectionId);
				if (mine.Value == null) continue;
				// kept for a minute: the tab may come back (GroupResume); the others are told meanwhile
				g.Joined[mine.Key] = ""; g.Away[mine.Key] = DateTime.UtcNow;
				foreach (var conn in Conns(g)) await Clients.Client(conn).SendAsync("groupaway", new { callId = g.CallId, employeeId = mine.Key });
			}
		}
	}
}
