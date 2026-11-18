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
			bool manager = await _access.CanAsync(ctx, HospitalActions.CallRecordings, null, ct);   // never bootstrap-open
			var d = await RecSvc.DetailAsync(ctx.CompanyId, id, me, manager, DateTime.UtcNow, ct);
			return d == null ? NotFound(new { ok = false, code = "not_found" }) : Json(new { ok = true, recording = d });
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
			bool manager = await _access.CanAsync(ctx, HospitalActions.CallRecordings, null, ct);   // never bootstrap-open
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
			return View(await CallSvc.SettingsAsync(ctx.CompanyId, ct));
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
