using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Hubs;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H176: internal calls. The calls screen (who is available, who is on call now, my call log), the
	// relay password a browser needs to start a call, the note and patient written during a call, and the settings
	// (what is ready, who may call whom). The ringing itself is HospitalCallHub; the media goes browser to browser.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalCallService? _callSvc;
		private HospitalCallService CallSvc => _callSvc ??= ActivatorUtilities.CreateInstance<HospitalCallService>(HttpContext.RequestServices);
		private HospitalCallRecordingService? _recSvc;
		private HospitalCallRecordingService RecSvc => _recSvc ??= ActivatorUtilities.CreateInstance<HospitalCallRecordingService>(HttpContext.RequestServices);
		/// The private temporary folder for recordings being sent (outside wwwroot; never a client-supplied path).
		private string RecTempDir => Path.Combine(HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "call-recordings-temp");
		public sealed class HospitalCallFinishInput { public int Seconds { get; set; } }

		/// Named in the call settings to review every recording (an explicit list; never a default).
		private async Task<bool> IsReviewerAsync(int companyId, int employeeId, CancellationToken ct)
			=> HospitalCallRecordingRules.ParseIds((await CallSvc.SettingsAsync(companyId, ct)).RecordingReviewers).Contains(employeeId);

		public sealed record HospitalCallsPage(IReadOnlyList<HospitalCallPerson> People, HospitalCallLogPage Log, HospitalCallStats Stats, string? Q, bool Installed, int MeId);
		public sealed class HospitalCallNoteInput { public string? Note { get; set; } public int? PatientId { get; set; } }

		[HospitalReceptionAllowed]   // the desk calls too (administrative, no clinical content)
		[HttpGet("Calls")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Calls(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow;
			bool installed = await CallSvc.InstalledAsync(ct);
			if (installed) await CallSvc.SweepAsync(ctx.CompanyId, now, ct);
			var people = await CallSvc.PeopleAsync(ctx.CompanyId, now, null, ct);
			if (installed) await RecSvc.SweepAsync(ctx, RecTempDir, now, ct);
			var log = await CallSvc.LogAsync(ctx.CompanyId, me, q, page, 10, now, ct);
			ViewBag.Recordings = await RecSvc.ForCallsAsync(ctx.CompanyId, log.Rows.Select(r => r.Call.ID), ct);
			// group calls: who was in each, by face (H176 group calls)
			var groupIds = log.Rows.Where(r => r.Call.IsGroup).Select(r => r.Call.ID).ToList();
			var parts = groupIds.Count == 0 ? new Dictionary<int, List<HospitalCallParticipantRow>>() : await ActivatorUtilities.CreateInstance<HospitalCallGroupService>(HttpContext.RequestServices).ForCallsAsync(ctx.CompanyId, groupIds, ct);
			ViewBag.GroupParts = parts;
			ViewBag.GroupPeople = parts.Count == 0 ? new Dictionary<int, HospitalCallPerson>()
				: (await CallSvc.PeopleAsync(ctx.CompanyId, now, parts.Values.SelectMany(v => v.Select(x => x.EmployeeId)).Distinct(), ct)).ToDictionary(x => x.EmployeeId);
			ViewBag.GroupOn = installed && (await CallSvc.SettingsAsync(ctx.CompanyId, ct)).GroupEnabled;
			var stats = await CallSvc.StatsAsync(ctx.CompanyId, me, now, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.BusyNow = HospitalCallHub.BusyCount(ctx.CompanyId);
			return View(new HospitalCallsPage(people.Where(p => p.EmployeeId != me).ToList(), log, stats, q, installed, me));
		}

		/// The ICE servers this browser may use for the next call: the hospital's STUN/TURN with a password valid 12 hours.
		[HospitalReceptionAllowed]
		[HttpGet("Calls/Ice")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallIce(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			var s = await CallSvc.SettingsAsync(ctx.CompanyId, ct);
			var urls = HospitalCallRules.ParseUrls(s.TurnUrls);
			var stun = urls.Where(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var turn = urls.Where(u => !u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var servers = new List<object>();
			if (stun.Count > 0) servers.Add(new { urls = stun });
			if (turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret))
			{
				var (user, cred) = HospitalCallRules.TurnCredential(s.TurnSecret, me, DateTime.UtcNow, TimeSpan.FromHours(12));
				servers.Add(new { urls = turn, username = user, credential = cred });
				// a TURN address also answers STUN on the same port
				if (stun.Count == 0) servers.Add(new { urls = turn.Where(u => u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)).Select(u => "stun:" + u[5..].Split('?')[0]).ToList() });
			}
			return Json(new { ok = true, iceServers = servers, relay = turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret) });
		}

		[HospitalReceptionAllowed]
		[HttpPost("Calls/{id:int}/Note"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallNote(int id, [FromBody] HospitalCallNoteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CallSvc.NoteAsync(ctx.CompanyId, id, me, input.Note, input.PatientId, ct));
		}

		[HospitalReceptionAllowed]
		[HttpGet("Calls/Patient/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallPatient(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var card = await CallSvc.PatientCardAsync(ctx.CompanyId, id, ct);
			return card == null ? NotFound(new { ok = false, code = "not_found" }) : Json(new { ok = true, patient = card });
		}

		/// The people a call can be made to, with their live state — the "add people" window of a call reads it.
		[HospitalReceptionAllowed]
		[HttpGet("Calls/People")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallPeople(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			var people = await CallSvc.PeopleAsync(ctx.CompanyId, DateTime.UtcNow, null, ct);
			return Json(new
			{
				ok = true,
				people = people.Where(p => p.EmployeeId != me).Select(p => new
				{
					id = p.EmployeeId, name = DisplayName.Of(p.Name, p.NameEn), photo = p.Photo, initials = HospitalUi.Initials(p.NameEn),
					title = p.Title == null && p.TitleEn == null ? null : DisplayName.Of(p.Title ?? p.TitleEn!, p.TitleEn ?? p.Title!), state = HospitalCallHub.Presence(p.EmployeeId),
				}),
			});
		}

		/// The next piece of a recording, from the requester's browser (raw WebM bytes, in order).
		[HospitalReceptionAllowed]
		[HttpPost("Calls/Recording/{id:int}/Chunk"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		[RequestSizeLimit(HospitalCallRecordingRules.MaxChunkBytes + 65536)]
		public async Task<IActionResult> CallRecordingChunk(int id, int seq, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecSvc.ChunkAsync(ctx.CompanyId, id, me, seq, Request.Body, RecTempDir, ct));
		}

		[HospitalReceptionAllowed]
		[HttpPost("Calls/Recording/{id:int}/Finish"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallRecordingFinish(int id, [FromBody] HospitalCallFinishInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RecSvc.FinishAsync(ctx, id, input.Seconds, RecTempDir, DateTime.UtcNow, ct));
		}

		/// The player's facts (participants and administrators only).
		[HospitalReceptionAllowed]
		[HttpGet("Calls/Recording/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallRecording(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			bool platform = await _access.CanAsync(ctx, HospitalActions.CallRecordings, null, ct);   // never bootstrap-open
			bool manager = platform || await IsReviewerAsync(ctx.CompanyId, me, ct);
			var d = await RecSvc.DetailAsync(ctx.CompanyId, id, me, manager, DateTime.UtcNow, ct);
			// the File Manager (document platform) opens it only for the platform action, so the link is offered only then
			return d == null ? NotFound(new { ok = false, code = "not_found" }) : Json(new { ok = true, recording = d, fileManager = platform });
		}

		/// The recording itself, seekable. The FIRST request of a playback is logged as an opening; the player's range
		/// requests that follow (log=0) are not counted again.
		[HospitalReceptionAllowed]
		[HttpGet("Calls/Recording/{id:int}/Media")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CallRecordingMedia(int id, int log = 0, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound();
			bool manager = await _access.CanAsync(ctx, HospitalActions.CallRecordings, null, ct) || await IsReviewerAsync(ctx.CompanyId, me, ct);   // never bootstrap-open
			var f = await RecSvc.OpenAsync(ctx.CompanyId, id, me, manager, DateTime.UtcNow, log == 1, ct);
			if (f == null) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return File(f.Content, f.ContentType, enableRangeProcessing: true);
		}

		[HttpGet("CallSettings")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CallSettings(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Installed = await CallSvc.InstalledAsync(ct);
			// the people who may be named to review every recording (the company's active staff, by face)
			ViewBag.People = await CallSvc.PeopleAsync(ctx.CompanyId, DateTime.UtcNow, null, ct);
			return View(await CallSvc.SettingsAsync(ctx.CompanyId, ct));
		}

		/// The settings' readiness test of the media server: a token for a test room, so this browser can try to connect.
		[HttpGet("CallSettings/SfuTest")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CallSfuTest(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			var s = await CallSvc.SettingsAsync(ctx.CompanyId, ct);
			if (!HospitalCallGroupRules.SfuReady(s.SfuUrl, s.SfuApiKey, s.SfuApiSecret)) return Json(new { ok = false });
			return Json(new { ok = true, url = s.SfuUrl, token = HospitalCallSfu.Token(s.SfuApiKey!, s.SfuApiSecret!, me + "-test", null, "hcall-test-" + ctx.CompanyId, DateTime.UtcNow, TimeSpan.FromMinutes(5)) });
		}

		[HttpPost("CallSettings"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveCallSettings([FromBody] HospitalCallSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CallSvc.SaveSettingsAsync(ctx.CompanyId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
