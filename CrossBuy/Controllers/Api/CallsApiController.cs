using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// HOSPITAL H176 — internal calls for the MOBILE APP. The ringing, the answer and the media are the same hub as the
	// browser (/hubs/calls, which already accepts the app's JWT); these are the screens' reads the browser gets from
	// HospitalController.Calls, behind the app's token instead of the browser session.
	//
	// Same rules as the browser, never a second set:
	//   * the employee and company are the resolved BusinessContext's, never ids from the request;
	//   * calls belong to the whole system (owner, 2026-11-18): every action needs an employee of the company, as the chat
	//     does — only the patient search keeps the hospital's "read" action;
	//   * a recording opens for the people in its call, the named reviewers, or the "call-recordings" action — which is
	//     never bootstrap-open; every first playback is logged;
	//   * a record that does not exist and one the caller may not see answer identically (404).
	// ==========================================================================================
	[ApiController]
	[Route("api/calls")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public class CallsApiController : ControllerBase
	{
		private readonly IBusinessContextAccessor _contexts;
		private readonly IHospitalAccessService _access;
		private readonly IServiceProvider _sp;
		public CallsApiController(IBusinessContextAccessor contexts, IHospitalAccessService access, IServiceProvider sp) { _contexts = contexts; _access = access; _sp = sp; }

		private HospitalCallService Svc => ActivatorUtilities.CreateInstance<HospitalCallService>(_sp);
		private HospitalCallRecordingService RecSvc => ActivatorUtilities.CreateInstance<HospitalCallRecordingService>(_sp);
		private static object Missing => new { ok = false, code = "not_found" };

		/// Deliberately NOT async: the language set here must stay for the rest of the action (a culture set inside an
		/// async method is undone when it returns to its caller).
		private Task<BusinessContext?> MeAsync(CancellationToken ct)
		{
			// names the services pick by language (DisplayName.Of) follow the app's language, which it sends as
			// Accept-Language; the site no longer negotiates it, so this API reads it for its own replies (UI culture only)
			if (Request.Headers.AcceptLanguage.ToString().StartsWith("ar", StringComparison.OrdinalIgnoreCase))
				System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
			return ResolveAsync(ct);
		}

		private async Task<BusinessContext?> ResolveAsync(CancellationToken ct)
		{
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			return ctx is { CompanyId: > 0, EmployeeId: > 0 } ? ctx : null;
		}

		private static object Card(HospitalCallPerson p) => new
		{
			id = p.EmployeeId, name = p.Name, nameEn = p.NameEn, photo = p.Photo, initials = HospitalUi.Initials(p.NameEn),
			title = p.Title, titleEn = p.TitleEn, department = p.Department, departmentEn = p.DepartmentEn, onCall = p.OnCall,
			state = HospitalCallHub.Presence(p.EmployeeId),
		};

		/// GET /api/calls/me — what this person can do with calls here, and how many missed calls wait for a call back.
		[HttpGet("me")]
		public async Task<IActionResult> Me(CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var svc = Svc;
			if (!await svc.InstalledAsync(ct)) return Ok(new { ok = true, installed = false });
			var s = await svc.SettingsAsync(me.CompanyId, ct);
			var card = (await svc.PeopleAsync(me.CompanyId, DateTime.UtcNow, new[] { me.EmployeeId!.Value }, ct)).FirstOrDefault();
			var (mayRecord, recKind, days) = card == null ? (false, "voice", 0) : await RecSvc.OptionsAsync(me.CompanyId, card, true, ct);
			return Ok(new
			{
				ok = true, installed = true, me = card == null ? null : Card(card),
				settings = new
				{
					video = s.VideoEnabled, share = s.ScreenShareEnabled, ringSeconds = s.RingSeconds, maxHeight = s.MaxVideoHeight,
					group = s.GroupEnabled, groupMax = HospitalCallGroupRules.Cap(s.GroupMax, HospitalCallGroupRules.SfuReady(s.SfuUrl, s.SfuApiKey, s.SfuApiSecret)),
					recording = s.RecordingEnabled, mayRecord, recKind, recordingDays = days,
				},
				missed = await svc.MissedWaitingAsync(me.CompanyId, me.EmployeeId!.Value, ct),
			});
		}

		/// GET /api/calls/people — everyone this person may see in the directory, available first, then busy, then offline.
		[HttpGet("people")]
		public async Task<IActionResult> People(string? q, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var svc = Svc;
			if (!await svc.InstalledAsync(ct)) return Ok(new { ok = true, people = Array.Empty<object>() });
			var all = await svc.PeopleAsync(me.CompanyId, DateTime.UtcNow, null, ct);
			var term = (q ?? "").Trim();
			var rank = new Dictionary<string, int> { ["on"] = 0, ["busy"] = 1, ["off"] = 2 };
			var people = all.Where(p => p.EmployeeId != me.EmployeeId)
				.Where(p => term.Length == 0 || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || p.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase)
					|| (p.Department ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) || (p.DepartmentEn ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
					|| (p.Title ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) || (p.TitleEn ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
				.OrderBy(p => rank[HospitalCallHub.Presence(p.EmployeeId)]).ThenBy(p => p.NameEn, StringComparer.OrdinalIgnoreCase)
				.Take(300).Select(Card).ToList();
			return Ok(new { ok = true, people });
		}

		/// GET /api/calls/ice — the hospital's STUN/TURN for the next call, with a relay password valid 12 hours.
		[HttpGet("ice")]
		public async Task<IActionResult> Ice(CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var s = await Svc.SettingsAsync(me.CompanyId, ct);
			var urls = HospitalCallRules.ParseUrls(s.TurnUrls);
			var stun = urls.Where(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var turn = urls.Where(u => !u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var servers = new List<object>();
			if (stun.Count > 0) servers.Add(new { urls = stun });
			if (turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret))
			{
				var (user, cred) = HospitalCallRules.TurnCredential(s.TurnSecret, me.EmployeeId!.Value, DateTime.UtcNow, TimeSpan.FromHours(12));
				servers.Add(new { urls = turn, username = user, credential = cred });
				if (stun.Count == 0) servers.Add(new { urls = turn.Where(u => u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)).Select(u => "stun:" + u[5..].Split('?')[0]).ToList() });
			}
			return Ok(new { ok = true, iceServers = servers, relay = turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret) });
		}

		/// GET /api/calls/log — my calls, newest first (missed ones flagged), with who, about which patient, and the recording.
		[HttpGet("log")]
		public async Task<IActionResult> Log(string? q, int page = 1, string? only = null, CancellationToken ct = default)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var svc = Svc;
			if (!await svc.InstalledAsync(ct)) return Ok(new { ok = true, rows = Array.Empty<object>(), total = 0, page = 1, pageSize = 20 });
			var now = DateTime.UtcNow;
			await svc.SweepAsync(me.CompanyId, now, ct);
			var log = await svc.LogAsync(me.CompanyId, me.EmployeeId!.Value, q, page, 20, now, ct);
			var recs = await RecSvc.ForCallsAsync(me.CompanyId, log.Rows.Select(r => r.Call.ID), ct);
			var groupIds = log.Rows.Where(r => r.Call.IsGroup).Select(r => r.Call.ID).ToList();
			var parts = groupIds.Count == 0 ? new Dictionary<int, List<HospitalCallParticipantRow>>()
				: await ActivatorUtilities.CreateInstance<HospitalCallGroupService>(_sp).ForCallsAsync(me.CompanyId, groupIds, ct);
			var gpeople = parts.Count == 0 ? new Dictionary<int, HospitalCallPerson>()
				: (await svc.PeopleAsync(me.CompanyId, now, parts.Values.SelectMany(v => v.Select(x => x.EmployeeId)).Distinct(), ct)).ToDictionary(x => x.EmployeeId);
			var rows = log.Rows.Select(r =>
			{
				var c = r.Call;
				recs.TryGetValue(c.ID, out var rec);
				bool missedForMe = !r.Outgoing && c.Status == HospitalCallStatus.Missed;
				return new
				{
					id = c.ID, outgoing = r.Outgoing, status = c.Status, missedWhy = c.MissedWhy, missedForMe, video = c.IsVideo, group = c.IsGroup,
					startedAt = DateTime.SpecifyKind(c.StartedAt, DateTimeKind.Utc), seconds = c.DurationSeconds, reply = c.Reply, note = c.Note,
					other = r.Other == null ? null : Card(r.Other),
					patient = r.Patient == null ? null : new { name = r.Patient, nameEn = r.PatientEn, mrn = r.Mrn },
					members = c.IsGroup && parts.TryGetValue(c.ID, out var ps) ? ps.Where(p => p.EmployeeId != me.EmployeeId && gpeople.ContainsKey(p.EmployeeId)).Select(p => Card(gpeople[p.EmployeeId])).ToList() : null,
					recording = rec == null || rec.Status != HospitalCallRecordingStatus.Saved ? null : new { id = rec.ID, kind = rec.Kind, seconds = rec.DurationSeconds },
				};
			}).Where(x => only != "missed" || x.missedForMe).Where(x => only != "recorded" || x.recording != null).ToList();
			return Ok(new { ok = true, rows, total = log.Total, page = log.Page, pageSize = log.PageSize });
		}

		/// GET /api/calls/patients?q= — the patient a group call is about (the picker of the group-call screen): by name or
		/// MRN, this company's, at most 15. The same card the hub sends with a call.
		[HttpGet("patients")]
		public async Task<IActionResult> Patients(string? q, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null || !await _access.CanAsync(me, HospitalActions.Read, null, ct)) return NotFound(Missing);
			var term = (q ?? "").Trim();
			if (term.Length < 2) return Ok(new { ok = true, patients = Array.Empty<object>() });
			var db = _sp.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var rows = await db.HospitalPatients.AsNoTracking()
				.Where(p => p.CompanyId == me.CompanyId && (p.Mrn.Contains(term) || p.Name.Contains(term) || (p.NameEn != null && p.NameEn.Contains(term))))
				.OrderBy(p => p.Name).Take(15)
				.Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			return Ok(new { ok = true, patients = rows.Select(p => new { id = p.ID, name = p.Name, nameEn = p.NameEn, mrn = p.Mrn, photo = p.PhotoPath }) });
		}

		public sealed class NoteInput { public string? Note { get; set; } public int? PatientId { get; set; } }

		/// POST /api/calls/{id}/note — a note (and the patient) written during the call; only a person in that call.
		[HttpPost("{id:int}/note")]
		public async Task<IActionResult> Note(int id, [FromBody] NoteInput input, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null || input == null) return NotFound(Missing);
			var comm = _sp.GetRequiredService<ICommunicationAccessService>();   // the chat's own rule: may send in Communication
			if (!await comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			var r = await Svc.NoteAsync(me.CompanyId, id, me.EmployeeId!.Value, input.Note, input.PatientId, ct);
			return r.Ok ? Ok(new { ok = true }) : r.Error == "not_found" ? NotFound(Missing) : Ok(new { ok = false, error = r.Error });
		}

		private async Task<bool> ManagerAsync(BusinessContext me, CancellationToken ct) =>
			await _access.CanAsync(me, HospitalActions.CallRecordings, null, ct)   // never bootstrap-open
			|| HospitalCallRecordingRules.ParseIds((await Svc.SettingsAsync(me.CompanyId, ct)).RecordingReviewers).Contains(me.EmployeeId!.Value);

		/// GET /api/calls/recording/{id} — the player's facts.
		[HttpGet("recording/{id:int}")]
		public async Task<IActionResult> Recording(int id, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var d = await RecSvc.DetailAsync(me.CompanyId, id, me.EmployeeId!.Value, await ManagerAsync(me, ct), DateTime.UtcNow, ct);
			return d == null ? NotFound(Missing) : Ok(new { ok = true, recording = d });
		}

		/// GET /api/calls/recording/{id}/summary — the call's summary as it stands; POST starts making it (owner 2026-11-19).
		[HttpGet("recording/{id:int}/summary")]
		public async Task<IActionResult> Summary(int id, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound(Missing);
			var v = await ActivatorUtilities.CreateInstance<CrossBuy.BL.Communication.CallSummarizer>(_sp, RecSvc).GetAsync(me.CompanyId, me.EmployeeId!.Value, id, await ManagerAsync(me, ct), ct);
			return v == null ? NotFound(Missing) : Ok(new { ok = true, summary = v });
		}

		[HttpPost("recording/{id:int}/summary")]
		public async Task<IActionResult> SummaryStart(int id, CancellationToken ct)
		{
			var me = await MeAsync(ct);
			var comm = _sp.GetRequiredService<ICommunicationAccessService>();
			if (me == null || !await comm.CanAsync(me, CommunicationActions.Read, null, ct)) return NotFound(Missing);
			var v = await ActivatorUtilities.CreateInstance<CrossBuy.BL.Communication.CallSummarizer>(_sp, RecSvc).StartAsync(me.CompanyId, me.EmployeeId!.Value, id, await ManagerAsync(me, ct), ct);
			return v == null ? NotFound(Missing) : Ok(new { ok = v.Code == null, summary = v });
		}

		/// GET /api/calls/recording/{id}/media — the recording, seekable; the first request of a playback (log=1) is logged.
		[HttpGet("recording/{id:int}/media")]
		public async Task<IActionResult> Media(int id, int log = 0, CancellationToken ct = default)
		{
			var me = await MeAsync(ct);
			if (me == null) return NotFound();
			var f = await RecSvc.OpenAsync(me.CompanyId, id, me.EmployeeId!.Value, await ManagerAsync(me, ct), DateTime.UtcNow, log == 1, ct);
			if (f == null) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return File(f.Content, f.ContentType, enableRangeProcessing: true);
		}
	}
}
