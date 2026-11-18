using System.Collections.Concurrent;
using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Hubs
{
	// ==========================================================================================
	// HOSPITAL H176 — the internal-calls hub (/hubs/calls). It rings, answers, refuses and hangs up, and passes each
	// browser's connection offer and network candidates to the OTHER party of the call only. The media never comes here.
	//
	// IDENTITY AND COMPANY come from the authenticated connection (NameIdentifier → Employee row), never from the client,
	// as ChatHub does: a hub invocation has no HTTP request for the business-context factory to read. Every call id a
	// client sends is checked against the live call it belongs to — a connection that is not one of its two ends gets
	// nothing relayed and learns nothing.
	//
	// PRESENCE is per employee and per company: "on" while any of their tabs is connected, "busy" while they are in a
	// call. One server process holds it (the app runs as one instance); after a restart each tab tells the server which
	// call it is in (Resume), and the warden closes what nobody came back to (HospitalCallHub.Warden.cs).
	// ==========================================================================================
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public sealed partial class HospitalCallHub : Hub
	{
		private sealed class Live
		{
			public int CallId, CompanyId, CallerId, CalleeId;
			public string CallerConn = "";
			public string? CalleeConn;
			public bool Video;
			public DateTime? CallerAway, CalleeAway;   // when that side's tab dropped (the warden ends the call after 90 s)
		}

		private static readonly ConcurrentDictionary<int, int> _online = new();          // employee → connections
		private static readonly ConcurrentDictionary<int, int> _company = new();         // employee → company
		private static readonly ConcurrentDictionary<int, Live> _calls = new();          // call id → live call
		private static readonly ConcurrentDictionary<int, int> _busy = new();            // employee → call id
		/// Connections from the mobile app (it connects with ?client=app): a phone cannot record a call, so a recording it
		/// asks for is recorded by the browser at the other end.
		private static readonly ConcurrentDictionary<string, bool> _appConns = new();
		private static bool IsApp(string? conn) => !string.IsNullOrEmpty(conn) && _appConns.ContainsKey(conn);
		public const string NeedsComputer = "Recording needs one side on a computer: a phone cannot record the call";

		/// "busy" also while someone's screen is ringing: a second caller is told at once instead of waiting out the ring.
		public static string Presence(int employeeId) => _busy.ContainsKey(employeeId) || _calls.Values.Any(l => l.CalleeId == employeeId) || RingingInGroup(employeeId) ? "busy" : _online.TryGetValue(employeeId, out var c) && c > 0 ? "on" : "off";
		public static int BusyCount(int companyId) => _busy.Keys.Count(e => _company.TryGetValue(e, out var c) && c == companyId);

		private static string Emp(int id) => "hcall-e-" + id;
		private static string Co(int id) => "hcall-c-" + id;

		private readonly IServiceProvider _sp;
		private readonly IEmployeeService _employees;
		private readonly CrossBuy.Models.Context.CrossDbContext _db;
		public HospitalCallHub(IServiceProvider sp, IEmployeeService employees, CrossBuy.Models.Context.CrossDbContext db) { _sp = sp; _employees = employees; _db = db; }

		private HospitalCallService Svc => ActivatorUtilities.CreateInstance<HospitalCallService>(_sp);
		private HospitalCallRecordingService RecSvc => ActivatorUtilities.CreateInstance<HospitalCallRecordingService>(_sp);

		private async Task<(int Emp, int Company)?> MeAsync()
		{
			if (Context.Items.TryGetValue("emp", out var e) && e is int emp && Context.Items.TryGetValue("co", out var c) && c is int co) return (emp, co);
			var uid = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (string.IsNullOrEmpty(uid)) return null;
			var row = await _employees.GetEmployeeByUserIdAsync(uid);
			if (row == null) return null;
			var company = await _db.Employee.AsNoTracking().Where(x => x.ID == row.ID && x.IsActive).Select(x => x.EmpCompanyID).FirstOrDefaultAsync();
			if (company <= 0) return null;
			Context.Items["emp"] = row.ID; Context.Items["co"] = company;
			return (row.ID, company);
		}

		public override async Task OnConnectedAsync()
		{
			if (await MeAsync() is var (emp, co))
			{
				await Groups.AddToGroupAsync(Context.ConnectionId, Emp(emp));
				await Groups.AddToGroupAsync(Context.ConnectionId, Co(co));
				_company[emp] = co;
				if (Context.GetHttpContext()?.Request.Query["client"].ToString() == "app") _appConns[Context.ConnectionId] = true;
				StartWarden();
				if (_online.AddOrUpdate(emp, 1, (_, n) => n + 1) == 1) await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = emp, state = Presence(emp) });
			}
			await base.OnConnectedAsync();
		}

		public override async Task OnDisconnectedAsync(Exception? exception)
		{
			if (Context.Items.TryGetValue("emp", out var e) && e is int emp && Context.Items.TryGetValue("co", out var c) && c is int co)
			{
				// A RINGING call this tab was carrying ends with it. An ANSWERED one does not: the audio and picture go device to
				// device without us, so the tab is given time to reconnect (Resume); the other side is told meanwhile, and its own
				// connection ends the call if the media really failed.
				foreach (var live in _calls.Values.Where(l => l.CallerConn == Context.ConnectionId || l.CalleeConn == Context.ConnectionId).ToList())
				{
					if (live.CalleeConn == null) { await CloseAsync(live, emp, "drop", null); continue; }
					bool caller = live.CallerConn == Context.ConnectionId;
					if (caller) { live.CallerConn = ""; live.CallerAway = DateTime.UtcNow; } else { live.CalleeConn = ""; live.CalleeAway = DateTime.UtcNow; }
					string other = caller ? live.CalleeConn! : live.CallerConn;
					if (other.Length > 0) await Clients.Client(other).SendAsync("peeraway", new { callId = live.CallId });
				}
				_appConns.TryRemove(Context.ConnectionId, out _);
				await DropFromGroupsAsync();   // H176 group calls
				if (_online.AddOrUpdate(emp, 0, (_, n) => Math.Max(0, n - 1)) == 0) await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = emp, state = Presence(emp) });
			}
			await base.OnDisconnectedAsync(exception);
		}

		/// The caller rings someone. Returns { ok, callId, ringSeconds } or { ok:false, error } where error is a resx key.
		public async Task<object> Invite(int calleeId, bool video, int? patientId, string? reason)
		{
			if (await MeAsync() is not var (me, co)) return new { ok = false, error = "forbidden" };
			if (calleeId == me) return new { ok = false, error = "You cannot call yourself" };
			var svc = Svc; var now = DateTime.UtcNow;
			if (!await svc.InstalledAsync()) return new { ok = false, error = HospitalCallService.NoTables };
			await svc.SweepAsync(co, now);
			var settings = await svc.SettingsAsync(co);
			if (video && !settings.VideoEnabled) video = false;
			var people = (await svc.PeopleAsync(co, now, new[] { me, calleeId })).ToDictionary(p => p.EmployeeId);
			if (!people.TryGetValue(me, out var caller) || !people.TryGetValue(calleeId, out var callee)) return new { ok = false, error = "not_found" };
			if (!await svc.MayCallAsync(co, caller, callee)) return new { ok = false, error = "Your group may not call this person's group (call settings)" };
			if (_busy.ContainsKey(me)) return new { ok = false, error = "You are already in a call" };
			if (patientId is int pid && !await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == co)) patientId = null;

			string state = Presence(calleeId);
			if (state != "on")
			{
				// offline or in another call: recorded as a missed call so it waits for a call back
				var missed = await svc.OpenAsync(co, me, calleeId, video, patientId, reason, HospitalCallStatus.Missed, state == "busy" ? "busy" : "offline", now);
				await Clients.Group(Emp(calleeId)).SendAsync("missed", new { callId = missed.ID });
				await NotifyMissedAsync(co, missed.ID, calleeId, me, video, false);
				return new { ok = false, error = state == "busy" ? "{0} is in another call — it is recorded as a missed call so they call you back" : "{0} is not connected — it is recorded as a missed call so they call you back", name = DisplayName.Of(callee.Name, callee.NameEn) };
			}

			var row = await svc.OpenAsync(co, me, calleeId, video, patientId, reason, HospitalCallStatus.Ringing, null, now);
			var live = new Live { CallId = row.ID, CompanyId = co, CallerId = me, CalleeId = calleeId, CallerConn = Context.ConnectionId, Video = video };
			_calls[row.ID] = live;
			_busy[me] = row.ID;
			await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = me, state = "busy" });
			await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = calleeId, state = "busy" });   // ringing
			var patient = patientId is int p2 ? await svc.PatientCardAsync(co, p2) : null;
			var rec = RecSvc;
			var (calleeMay, kind, _) = await rec.OptionsAsync(co, callee, video);
			var (callerMay, _, _) = await rec.OptionsAsync(co, caller, video);
			await Clients.Group(Emp(calleeId)).SendAsync("incoming", new
			{
				callId = row.ID, video, reason = row.Reason, ringSeconds = settings.RingSeconds, share = settings.ScreenShareEnabled, maxHeight = settings.MaxVideoHeight,
				caller = Card(caller), patient, canRecord = calleeMay, recKind = kind,
			});
			return new { ok = true, callId = row.ID, ringSeconds = settings.RingSeconds, share = settings.ScreenShareEnabled, maxHeight = settings.MaxVideoHeight, video, callee = Card(callee), patient, canRecord = callerMay, recKind = kind };
		}

		/// A missed call reaches the bell too (the notification centre), once per call and person, with the way back to the
		/// calls screen — so it is seen even when the calls page is not open.
		public const string MissedNotificationType = "hospital_missed_call";
		private async Task NotifyMissedAsync(int companyId, int callId, int calleeId, int callerId, bool video, bool group)
		{
			try
			{
				var notify = _sp.GetService<INotificationService>();
				if (notify == null) return;
				var who = (await Svc.PeopleAsync(companyId, DateTime.UtcNow, new[] { callerId })).FirstOrDefault();
				string ar = who?.Name ?? "", en = who?.NameEn ?? who?.Name ?? "";
				string kindAr = group ? "مكالمة جماعية" : video ? "مكالمة فيديو داخلية" : "مكالمة صوتية داخلية";
				string kindEn = group ? "Group call" : video ? "Internal video call" : "Internal voice call";
				await notify.NotifyAsync(calleeId, "مكالمة فائتة من " + ar, "Missed call from " + en,
					kindAr + " — يمكنك معاودة الاتصال من صفحة الاتصال الداخلي", kindEn + " — call back from the Internal calls page",
					MissedNotificationType, callId, "/Hospital/Calls", companyId, callerId,
					priority: "High", category: "Hospital", dedupKey: $"hcall-missed-{callId}-{calleeId}", icon: "ki-call");
			}
			catch (Exception) { }   // a failed notice never breaks the call flow
		}

		private static object Card(HospitalCallPerson p) => new
		{
			id = p.EmployeeId, name = DisplayName.Of(p.Name, p.NameEn), nameEn = p.NameEn, photo = p.Photo, initials = HospitalUi.Initials(p.NameEn),
			title = DisplayName.Of(p.Title ?? "", p.TitleEn ?? p.Title ?? ""), department = DisplayName.Of(p.Department ?? "", p.DepartmentEn ?? p.Department ?? ""),
		};

		/// The callee answers on THIS tab; the caller's browser then sends its offer.
		public async Task<object> Answer(int callId, bool video)
		{
			if (await MeAsync() is not var (me, co) || !_calls.TryGetValue(callId, out var live) || live.CalleeId != me || live.CalleeConn != null) return new { ok = false };
			var row = await Svc.MoveAsync(co, callId, me, "answer", null, DateTime.UtcNow);
			if (row == null) return new { ok = false };
			live.CalleeConn = Context.ConnectionId;
			_busy[me] = callId;
			await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = me, state = "busy" });
			await Clients.Client(live.CallerConn).SendAsync("answered", new { callId, video });
			await Clients.GroupExcept(Emp(me), Context.ConnectionId).SendAsync("taken", new { callId });   // my other tabs stop ringing
			return new { ok = true };
		}

		public async Task Decline(int callId, string? reply)
		{
			if (await MeAsync() is not var (me, _) || !_calls.TryGetValue(callId, out var live) || live.CalleeId != me) return;
			await CloseAsync(live, me, "decline", reply);
		}

		/// The caller gives up (missed = the ring time ran out), or either side hangs up.
		public async Task Hangup(int callId, bool timedOut)
		{
			if (await MeAsync() is not var (me, _) || !_calls.TryGetValue(callId, out var live) || (live.CallerId != me && live.CalleeId != me)) return;
			var row = await _db.Set<HospitalCall>().AsNoTracking().Where(c => c.ID == callId).Select(c => c.Status).FirstOrDefaultAsync();
			string action = row == HospitalCallStatus.Answered ? "hangup" : timedOut ? "timeout" : "cancel";
			await CloseAsync(live, me, action, null);
		}

		private async Task CloseAsync(Live live, int by, string action, string? reply)
		{
			if (!_calls.TryRemove(live.CallId, out _)) return;
			var row = await Svc.MoveAsync(live.CompanyId, live.CallId, by, action, reply, DateTime.UtcNow);
			foreach (var e in new[] { live.CallerId, live.CalleeId })
			{
				if (_busy.TryGetValue(e, out var cid) && cid == live.CallId) _busy.TryRemove(e, out _);
				await Clients.Group(Co(live.CompanyId)).SendAsync("presence", new { employeeId = e, state = Presence(e) });
			}
			var msg = new { callId = live.CallId, status = row?.Status ?? HospitalCallStatus.Ended, reply = row?.Reply, seconds = row?.DurationSeconds };
			if (row?.Status == HospitalCallStatus.Missed) await NotifyMissedAsync(live.CompanyId, live.CallId, live.CalleeId, live.CallerId, live.Video, false);
			if (live.CallerConn.Length > 0) await Clients.Client(live.CallerConn).SendAsync("ended", msg);
			if (live.CalleeConn == null) await Clients.Group(Emp(live.CalleeId)).SendAsync("ended", msg);   // still ringing on every tab
			else if (live.CalleeConn.Length > 0) await Clients.Client(live.CalleeConn).SendAsync("ended", msg);
		}

		/// An SDP offer/answer, an ICE candidate or a state change (mic, camera, sharing), passed to the other end only.
		public async Task Signal(int callId, string payload)
		{
			if (payload == null || payload.Length > 64_000) return;
			if (!_calls.TryGetValue(callId, out var live) || live.CalleeConn == null) return;
			string? to = Context.ConnectionId == live.CallerConn ? live.CalleeConn : Context.ConnectionId == live.CalleeConn ? live.CallerConn : null;
			if (!string.IsNullOrEmpty(to)) await Clients.Client(to).SendAsync("signal", new { callId, payload });
		}

		// ---- recording, with the other side's consent -------------------------------------------------------------

		private async Task<(Live Live, HospitalCallRecording Row)?> LiveRecAsync(int recId, int co)
		{
			var row = await _db.Set<HospitalCallRecording>().AsNoTracking().FirstOrDefaultAsync(r => r.ID == recId && r.CompanyId == co);
			return row != null && _calls.TryGetValue(row.CallId, out var live) && live.CalleeConn != null ? (live, row) : null;
		}
		private string? Other(Live live) => Context.ConnectionId == live.CallerConn ? live.CalleeConn : Context.ConnectionId == live.CalleeConn ? live.CallerConn : null;

		/// A participant asks to record; the OTHER side gets the consent window. Nothing is recorded until it agrees.
		/// <paramref name="picture"/>: a camera or a shared screen is live in the call now, so a voice call is recorded with its picture.
		public async Task<object> RecordRequest(int callId, bool picture)
		{
			if (await MeAsync() is not var (me, co) || !_calls.TryGetValue(callId, out var live) || live.CalleeConn == null) return new { ok = false, error = "failed" };
			string? to = Other(live);
			if (to == null) return new { ok = false, error = "failed" };
			// from the app: the browser at the other end records (a phone cannot mix both sides); two phones cannot record
			int? recorder = null;
			if (IsApp(Context.ConnectionId))
			{
				if (IsApp(to)) return new { ok = false, error = NeedsComputer };
				recorder = to == live.CallerConn ? live.CallerId : live.CalleeId;
			}
			var (row, error) = await RecSvc.AskAsync(co, callId, me, DateTime.UtcNow, picture, recorder);
			if (row == null) return new { ok = false, error };
			var s = await Svc.SettingsAsync(co);
			var by = (await Svc.PeopleAsync(co, DateTime.UtcNow, new[] { me })).FirstOrDefault();
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstAsync(c => c.ID == callId);
			var patient = call.PatientId is int pid ? await Svc.PatientCardAsync(co, pid) : null;
			await Clients.Client(to).SendAsync("recask", new { recId = row.ID, callId, kind = row.Kind, days = s.RecordingDays, by = by == null ? null : Card(by), patient });
			return new { ok = true, recId = row.ID, kind = row.Kind };
		}

		/// The other side answers. Agreeing starts it on BOTH screens (the requester's browser records); refusing tells the requester.
		public async Task RecordAnswer(int recId, bool agree)
		{
			if (await MeAsync() is not var (me, co) || await LiveRecAsync(recId, co) is not var (live, _)) return;
			var row = await RecSvc.AnswerAsync(co, recId, me, agree ? "agree" : "refuse", DateTime.UtcNow);
			if (row == null) return;
			if (row.Status == HospitalCallRecordingStatus.Recording)
			{
				var msg = new { recId, callId = live.CallId, kind = row.Kind, requesterId = row.RequestedByEmployeeId, recorderId = HospitalCallRecordingRules.RecorderOf(row), agreedAt = row.AgreedAt };
				await Clients.Client(live.CallerConn).SendAsync("recstart", msg);
				await Clients.Client(live.CalleeConn!).SendAsync("recstart", msg);
			}
			else await Clients.Clients(new[] { live.CallerConn, live.CalleeConn! }).SendAsync("recclosed", new { recId, refused = true });
		}

		/// The requester withdraws the question before it is answered.
		public async Task RecordWithdraw(int recId)
		{
			if (await MeAsync() is not var (me, co) || await LiveRecAsync(recId, co) is not var (live, _)) return;
			if (await RecSvc.AnswerAsync(co, recId, me, "cancel", DateTime.UtcNow) == null) return;
			await Clients.Clients(new[] { live.CallerConn, live.CalleeConn! }).SendAsync("recclosed", new { recId, refused = false });
		}

		/// Either side stops it; the requester's browser then sends the last piece and the file is kept.
		public async Task RecordStop(int recId)
		{
			if (await MeAsync() is not var (me, co) || await LiveRecAsync(recId, co) is not var (live, row)) return;
			if (Other(live) == null || row.Status != HospitalCallRecordingStatus.Recording) return;
			await RecSvc.MarkStoppedAsync(co, recId, me);
			var msg = new { recId, byId = me };
			await Clients.Client(live.CallerConn).SendAsync("recstop", msg);
			await Clients.Client(live.CalleeConn!).SendAsync("recstop", msg);
		}

		/// A tab that was in an ANSWERED two-person call reconnects (a network drop, or the server restarted and forgot every
		/// live call): the call is rebuilt from its record and this connection takes its place in it.
		public async Task<object> Resume(int callId)
		{
			if (await MeAsync() is not var (me, co)) return new { ok = false };
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == co && !c.IsGroup);
			if (call == null || call.Status != HospitalCallStatus.Answered || (call.CallerEmployeeId != me && call.CalleeEmployeeId != me)) return new { ok = false };
			var live = _calls.GetOrAdd(callId, _ => new Live { CallId = callId, CompanyId = co, CallerId = call.CallerEmployeeId, CalleeId = call.CalleeEmployeeId, CallerConn = "", CalleeConn = "", Video = call.IsVideo, CallerAway = DateTime.UtcNow, CalleeAway = DateTime.UtcNow });
			if (me == live.CallerId) { live.CallerConn = Context.ConnectionId; live.CallerAway = null; } else { live.CalleeConn = Context.ConnectionId; live.CalleeAway = null; }
			_busy[me] = callId;
			await Clients.Group(Co(co)).SendAsync("presence", new { employeeId = me, state = "busy" });
			string other = me == live.CallerId ? live.CalleeConn ?? "" : live.CallerConn;
			if (other.Length > 0) await Clients.Client(other).SendAsync("peerback", new { callId });
			// a recording in progress goes on: its pieces arrive over HTTP and its row is in the database
			return new { ok = true, peerHere = other.Length > 0 };
		}

		/// Presence of the people a screen shows (the directory asks once, then follows "presence" events).
		public async Task<object> PresenceOf(int[] employeeIds)
		{
			if (await MeAsync() is not var (_, co)) return Array.Empty<object>();
			return (employeeIds ?? Array.Empty<int>()).Take(500).Where(e => !_company.TryGetValue(e, out var c) || c == co)
				.Select(e => new { employeeId = e, state = Presence(e) }).ToList();
		}
	}
}
