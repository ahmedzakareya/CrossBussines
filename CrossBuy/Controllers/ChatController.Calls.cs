using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// COMMUNICATION — chat and internal calls as ONE feature of the whole system (owner, 2026-11-18; prototype
	// design-output/comm-prototype, approved). The calls began in the hospital (H176); here they become every employee's:
	//   * the page opens INSIDE the module the person came from — its layout, its menu — so the modules never mix;
	//   * anyone in the company may call anyone in it (like the chat): HospitalCallService.MayCallAsync;
	//   * the endpoints the calls need (relay servers, people, a note, a recording's pieces) live here, behind the same
	//     employee check as the chat, instead of behind the hospital's permissions;
	//   * a call's patient stays a hospital thing: only someone with the hospital module sees it.
	// ==========================================================================================
	public partial class ChatController
	{
		private HospitalCallService CallSvc => ActivatorUtilities.CreateInstance<HospitalCallService>(HttpContext.RequestServices);
		private HospitalCallRecordingService RecSvc => ActivatorUtilities.CreateInstance<HospitalCallRecordingService>(HttpContext.RequestServices);
		private string RecTempDir => Path.Combine(_env.ContentRootPath, "App_Data", "call-recordings-temp");
		private const string LayoutCookie = "cb.comm.layout";

		// the module → its layout. Every layout falls back to its own menu, so choosing the layout brings the menu along.
		private static readonly Dictionary<string, string> Layouts = new(StringComparer.OrdinalIgnoreCase)
		{
			["hospital"] = "~/Views/Shared/_LayoutHospital.cshtml",
			["accounting"] = "~/Views/Shared/_LayoutAccounting.cshtml",
			["inventory"] = "~/Views/Shared/_LayoutInventory.cshtml",
			["manufacturing"] = "~/Views/Shared/_LayoutManufacturing.cshtml",
			["people"] = "~/Views/Shared/_LayoutPeople.cshtml",
			["backend"] = "~/Views/Shared/_LayoutBackend.cshtml",
		};
		// the page the person came from → its module (the views of each area use that area's layout)
		private static readonly (string Prefix, string Module)[] FromPath =
		{
			("/hospital", "hospital"), ("/people", "people"),
			("/accounting", "accounting"), ("/currency", "accounting"), ("/crm", "accounting"),
			("/inventory/manuf", "manufacturing"),
			("/inventory", "inventory"), ("/tasks", "inventory"), ("/workspace", "inventory"), ("/reports", "inventory"),
			("/calendar", "inventory"), ("/platformadministration", "inventory"), ("/restaurantintelligence", "inventory"),
		};

		/// The layout this page opens in: the module named in the address (?m=), else the module of the page the person came
		/// from, else the one remembered while moving inside Communication, else the back office.
		private string CommLayout(string? m)
		{
			// THE CHAT DRAWER (crossbuy-chat-drawer.js) loads /Chat?embed=1: the chat alone, no header, menu or footer. Only
			// the back-office layout knows embed mode, so a module's layout here put the WHOLE SYSTEM inside the drawer
			// (owner, 2026-11-19: «ال slide دي مهزله كاملة … تفتح النظام بالكامل»). In the drawer no layout is seen, so
			// the module does not matter — and the remembered module is left as it was.
			if (Request.Query["embed"] == "1") { ViewBag.CommModule = "backend"; return Layouts["backend"]; }
			string? module = m != null && Layouts.ContainsKey(m) ? m : null;
			if (module == null && Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var from) && from.Host == Request.Host.Host
				&& !from.AbsolutePath.StartsWith("/chat", StringComparison.OrdinalIgnoreCase))
				module = FromPath.Where(f => from.AbsolutePath.StartsWith(f.Prefix, StringComparison.OrdinalIgnoreCase)).Select(f => f.Module).FirstOrDefault() ?? "backend";
			if (module == null && Request.Cookies.TryGetValue(LayoutCookie, out var kept) && Layouts.ContainsKey(kept)) module = kept;
			module ??= "backend";
			Response.Cookies.Append(LayoutCookie, module, new Microsoft.AspNetCore.Http.CookieOptions { HttpOnly = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax, Secure = Request.IsHttps, IsEssential = true });
			ViewBag.CommModule = module;
			return Layouts[module];
		}

		/// Whether this person has the hospital module (a call's patient and the hospital's on-call list are for them only).
		private async Task<bool> HospitalOnAsync()
		{
			try
			{
				var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
				var access = HttpContext.RequestServices.GetService(typeof(IHospitalAccessService)) as IHospitalAccessService;
				return ctx != null && access != null && await access.CanAsync(ctx, HospitalActions.Read, null, HttpContext.RequestAborted);
			}
			catch (Exception) { return false; }
		}

		/// GET /Chat/CallIce — the company's STUN/TURN for the next call, with a relay password valid 12 hours.
		[HttpGet]
		public async Task<IActionResult> CallIce()
		{
			var me = await MeAsync(); if (me == null) return Unauthorized();
			var s = await CallSvc.SettingsAsync(me.Value.companyId);
			var urls = HospitalCallRules.ParseUrls(s.TurnUrls);
			var stun = urls.Where(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var turn = urls.Where(u => !u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToList();
			var servers = new List<object>();
			if (stun.Count > 0) servers.Add(new { urls = stun });
			if (turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret))
			{
				var (user, cred) = HospitalCallRules.TurnCredential(s.TurnSecret, me.Value.empId, DateTime.UtcNow, TimeSpan.FromHours(12));
				servers.Add(new { urls = turn, username = user, credential = cred });
				if (stun.Count == 0) servers.Add(new { urls = turn.Where(u => u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)).Select(u => "stun:" + u[5..].Split('?')[0]).ToList() });
			}
			return Json(new { ok = true, iceServers = servers, relay = turn.Count > 0 && !string.IsNullOrEmpty(s.TurnSecret) });
		}

		/// GET /Chat/CallPeople — everyone a call can be made to, with their live state (available first).
		[HttpGet]
		public async Task<IActionResult> CallPeople()
		{
			var me = await MeAsync(); if (me == null) return Unauthorized();
			var people = await CallSvc.PeopleAsync(me.Value.companyId, DateTime.UtcNow, null);
			var rank = new Dictionary<string, int> { ["on"] = 0, ["busy"] = 1, ["phone"] = 2, ["off"] = 3 };   // "phone": the app is closed but push rings it
			// the hospital's on-call list, at the top of People: for someone with the hospital module, when it is switched on
			bool onCallList = await HospitalOnAsync() && (await CallSvc.SettingsAsync(me.Value.companyId)).OnCallList;
			return Json(new
			{
				ok = true,
				onCall = onCallList ? people.Where(p => p.OnCall && p.EmployeeId != me.Value.empId).Select(p => p.EmployeeId) : Enumerable.Empty<int>(),
				people = people.Where(p => p.EmployeeId != me.Value.empId).OrderBy(p => rank.GetValueOrDefault(HospitalCallHub.Presence(p.EmployeeId), 3)).ThenBy(p => p.NameEn, StringComparer.OrdinalIgnoreCase).Select(p => new
				{
					id = p.EmployeeId, name = DisplayName.Of(p.Name, p.NameEn), photo = p.Photo, initials = HospitalUi.Initials(p.NameEn),
					title = p.Title == null && p.TitleEn == null ? null : DisplayName.Of(p.Title ?? p.TitleEn!, p.TitleEn ?? p.Title!),
					department = p.Department == null && p.DepartmentEn == null ? null : DisplayName.Of(p.Department ?? p.DepartmentEn!, p.DepartmentEn ?? p.Department!),
					state = HospitalCallHub.Presence(p.EmployeeId),
				}),
			});
		}

		public sealed class CallNoteInput { public string? Note { get; set; } public int? PatientId { get; set; } }

		/// POST /Chat/CallNote/{id} — a note (and, in the hospital, the patient) written during the call; only a person in it.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CallNote(int id, [FromBody] CallNoteInput input)
		{
			var me = await MeAsync(); if (me == null || input == null) return NotFound(new { ok = false });
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (ctx == null || !await comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			int? patient = input.PatientId is int pid && await HospitalOnAsync() ? pid : null;   // a patient only through the hospital module
			var r = await CallSvc.NoteAsync(me.Value.companyId, id, me.Value.empId, input.Note, patient);
			return Json(new { ok = r.Ok, error = r.Error });
		}

		/// POST /Chat/CallRecordingChunk/{id}?seq=n — the next piece of a recording, from the browser that records it.
		[HttpPost, ValidateAntiForgeryToken]
		[RequestSizeLimit(HospitalCallRecordingRules.MaxChunkBytes + 65536)]
		public async Task<IActionResult> CallRecordingChunk(int id, int seq)
		{
			var me = await MeAsync(); if (me == null) return NotFound(new { ok = false });
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (ctx == null || !await comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await RecSvc.ChunkAsync(me.Value.companyId, id, me.Value.empId, seq, Request.Body, RecTempDir, HttpContext.RequestAborted);
			return Json(new { ok = r.Ok, error = r.Error });
		}

		public sealed class CallFinishInput { public int Seconds { get; set; } }

		/// POST /Chat/CallRecordingFinish/{id} — the recording stopped: the whole file goes to the File Manager.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CallRecordingFinish(int id, [FromBody] CallFinishInput input)
		{
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0, EmployeeId: > 0 } || input == null) return NotFound(new { ok = false });
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (!await comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await RecSvc.FinishAsync(ctx, id, input.Seconds, RecTempDir, DateTime.UtcNow, HttpContext.RequestAborted);
			return Json(new { ok = r.Ok, error = r.Error });
		}

		/// May this person review every recording? The hospital's platform action (for someone with the hospital module), or
		/// being named a recording reviewer in the call settings.
		private async Task<bool> RecordingManagerAsync(int companyId, int empId)
		{
			try
			{
				var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
				var access = HttpContext.RequestServices.GetService(typeof(IHospitalAccessService)) as IHospitalAccessService;
				if (ctx != null && access != null && await access.CanAsync(ctx, HospitalActions.CallRecordings, null, HttpContext.RequestAborted)) return true;   // never bootstrap-open
			}
			catch (Exception) { }
			return HospitalCallRecordingRules.ParseIds((await CallSvc.SettingsAsync(companyId)).RecordingReviewers).Contains(empId);
		}

		/// GET /Chat/CallLog?q=&amp;filter=&amp;page= — my calls (made, received, group), newest first, ten a page, with today's figures.
		[HttpGet]
		public async Task<IActionResult> CallLog(string? q, string? filter, int page = 1)
		{
			var me = await MeAsync(); if (me == null) return Unauthorized();
			int co = me.Value.companyId, a = me.Value.empId; var now = DateTime.UtcNow;
			bool installed = await CallSvc.InstalledAsync();
			if (!installed) return Json(new { ok = true, installed = false, rows = Array.Empty<object>(), total = 0, page = 1, pageSize = 10 });
			await CallSvc.SweepAsync(co, now);
			bool hospital = await HospitalOnAsync();
			// the missed / recorded filters read the whole of my log, the page is cut after
			var all = await CallSvc.LogAsync(co, a, q, 1, filter is "missed" or "recorded" ? 500 : 10 * Math.Max(1, page), now);
			var recs = await RecSvc.ForCallsAsync(co, all.Rows.Select(r => r.Call.ID));
			IEnumerable<HospitalCallLogRow> rows = all.Rows.Where(r => r.Call.Status != HospitalCallStatus.Ringing);
			if (filter == "missed") rows = rows.Where(r => r.Call.Status == HospitalCallStatus.Missed && !r.Outgoing);
			if (filter == "recorded") rows = rows.Where(r => recs.TryGetValue(r.Call.ID, out var x) && x.Status == HospitalCallRecordingStatus.Saved);
			var list = rows.ToList();
			int total = filter is "missed" or "recorded" ? list.Count : all.Total;
			var pageRows = filter is "missed" or "recorded" ? list.Skip((Math.Max(1, page) - 1) * 10).Take(10) : list.Skip((Math.Max(1, page) - 1) * 10);
			var stats = await CallSvc.StatsAsync(co, a, now);
			return Json(new
			{
				ok = true, installed = true, total, page = Math.Max(1, page), pageSize = 10,
				stats = new { today = stats.Today, average = stats.AverageSeconds, missed = stats.MissedWaiting },
				rows = pageRows.Select(r =>
				{
					recs.TryGetValue(r.Call.ID, out var rec);
					var o = r.Other;
					return new
					{
						id = r.Call.ID, outgoing = r.Outgoing, video = r.Call.IsVideo, group = r.Call.IsGroup, status = r.Call.Status, missedWhy = r.Call.MissedWhy, reply = r.Call.Reply,
						at = DateTime.SpecifyKind(r.Call.StartedAt, DateTimeKind.Utc), seconds = r.Call.DurationSeconds, reason = r.Call.Reason,
						other = o == null ? null : new { id = o.EmployeeId, name = DisplayName.Of(o.Name, o.NameEn), photo = o.Photo, initials = HospitalUi.Initials(o.NameEn), title = o.Title == null && o.TitleEn == null ? null : DisplayName.Of(o.Title ?? o.TitleEn!, o.TitleEn ?? o.Title!), state = HospitalCallHub.Presence(o.EmployeeId) },
						patient = hospital && r.Patient != null ? new { name = DisplayName.Of(r.Patient, r.PatientEn ?? r.Patient), mrn = r.Mrn } : null,
						note = r.Call.Note,
						recordingId = rec != null && rec.Status == HospitalCallRecordingStatus.Saved ? rec.ID : (int?)null,
					};
				}),
			});
		}

		/// GET /Chat/CallRecording/{id} — the player's facts (the call's people and the recording reviewers only).
		[HttpGet]
		public async Task<IActionResult> CallRecording(int id)
		{
			var me = await MeAsync(); if (me == null) return NotFound(new { ok = false });
			bool manager = await RecordingManagerAsync(me.Value.companyId, me.Value.empId);
			var d = await RecSvc.DetailAsync(me.Value.companyId, id, me.Value.empId, manager, DateTime.UtcNow);
			return d == null ? NotFound(new { ok = false }) : Json(new { ok = true, recording = d });
		}

		// ---------------- call summaries: a recorded call's words and summary (owner 2026-11-19) ----------------
		private CrossBuy.BL.Communication.CallSummarizer Summaries => ActivatorUtilities.CreateInstance<CrossBuy.BL.Communication.CallSummarizer>(HttpContext.RequestServices, RecSvc);

		/// GET /Chat/CallSummary/{id} — the recording's summary as it stands (whoever may open the recording).
		[HttpGet]
		public async Task<IActionResult> CallSummary(int id)
		{
			var me = await MeAsync(); if (me == null) return NotFound(new { ok = false });
			var v = await Summaries.GetAsync(me.Value.companyId, me.Value.empId, id, await RecordingManagerAsync(me.Value.companyId, me.Value.empId), HttpContext.RequestAborted);
			return v == null ? NotFound(new { ok = false }) : Json(new { ok = true, summary = v });
		}

		/// POST /Chat/CallSummaryStart/{id} — make it: the recording goes to the internal AI service and the summary
		/// appears when it is ready (minutes, in the background).
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CallSummaryStart(int id)
		{
			var me = await MeAsync(); var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			// the chat's own authority to read, then the recording's own rule (inside the summarizer: the call's people
			// and the recording reviewers)
			if (me == null || ctx == null || !await comm.CanAsync(ctx, CommunicationActions.Read, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var v = await Summaries.StartAsync(me.Value.companyId, me.Value.empId, id, await RecordingManagerAsync(me.Value.companyId, me.Value.empId), HttpContext.RequestAborted);
			return v == null ? NotFound(new { ok = false }) : Json(new { ok = v.Code == null, summary = v });
		}

		/// GET /Chat/CallRecordingMedia/{id}?log=1 — the recording, seekable; the first request of a playback is logged as an opening.
		[HttpGet]
		public async Task<IActionResult> CallRecordingMedia(int id, int log = 0)
		{
			var me = await MeAsync(); if (me == null) return NotFound();
			bool manager = await RecordingManagerAsync(me.Value.companyId, me.Value.empId);
			var f = await RecSvc.OpenAsync(me.Value.companyId, id, me.Value.empId, manager, DateTime.UtcNow, log == 1);
			if (f == null) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return File(f.Content, f.ContentType, enableRangeProcessing: true);
		}

		// ------------------------------------------------------------------------------------------
		// SETTINGS — Communication's own page (the prototype's #/comm-settings). The administrator of Communication
		// (module-level manage-group: CommunicationAdministrator, never bootstrap-open) or the hospital's manager.
		// ------------------------------------------------------------------------------------------
		private async Task<bool> MayManageCallsAsync()
		{
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0, EmployeeId: > 0 }) return false;
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (await comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted)) return true;
			try
			{
				var hospital = HttpContext.RequestServices.GetService(typeof(IHospitalAccessService)) as IHospitalAccessService;
				return hospital != null && await hospital.CanAsync(ctx, HospitalActions.Manage, null, HttpContext.RequestAborted);
			}
			catch (Exception) { return false; }
		}

		/// GET /Chat/Settings — what calls may do, the relay and media servers, recording, and the hospital's options.
		[HttpGet]
		public async Task<IActionResult> Settings(string? m = null)
		{
			var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
			if (!await MayManageCallsAsync()) return RedirectToAction(nameof(Index));
			ViewBag.CommLayout = CommLayout(m);
			ViewBag.Installed = await CallSvc.InstalledAsync();
			ViewBag.Hospital = await HospitalOnAsync();
			ViewBag.People = await CallSvc.PeopleAsync(me.Value.companyId, DateTime.UtcNow, null);
			return View(await CallSvc.SettingsAsync(me.Value.companyId));
		}

		/// POST /Chat/SaveSettings — the settings page's save. Everyone may call everyone: the old matrix is kept as it is.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveSettings([FromBody] HospitalCallSettingsInput input)
		{
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0, EmployeeId: > 0 } || input == null) return NotFound(new { ok = false });
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			bool allowed = await comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync();
			if (!allowed) return NotFound(new { ok = false });
			input.KeepPolicy = true;
			var r = await CallSvc.SaveSettingsAsync(ctx.CompanyId, input, ctx.EmployeeId, DateTime.UtcNow);
			return Json(new { ok = r.Ok, error = r.Error });
		}

		/// GET /Chat/CallSfuTest — the settings' readiness test of the media server: a token for a test room.
		[HttpGet]
		public async Task<IActionResult> CallSfuTest()
		{
			var me = await MeAsync(); if (me == null || !await MayManageCallsAsync()) return NotFound(new { ok = false });
			var s = await CallSvc.SettingsAsync(me.Value.companyId);
			if (!HospitalCallGroupRules.SfuReady(s.SfuUrl, s.SfuApiKey, s.SfuApiSecret)) return Json(new { ok = false });
			return Json(new { ok = true, url = s.SfuUrl, token = HospitalCallSfu.Token(s.SfuApiKey!, s.SfuApiSecret!, me.Value.empId + "-test", null, "hcall-test-" + me.Value.companyId, DateTime.UtcNow, TimeSpan.FromMinutes(5)) });
		}

		/// GET /Chat/CallsWith?other=… — the calls between me and one person, to write them into our conversation.
		[HttpGet]
		public async Task<IActionResult> CallsWith(int other)
		{
			var me = await MeAsync(); if (me == null) return Unauthorized();
			int a = me.Value.empId, co = me.Value.companyId;
			try
			{
				if (!(await CallSvc.SettingsAsync(co)).CallsInChat) return Json(Array.Empty<object>());   // switched off in the settings
				var calls = await _db.Set<HospitalCall>().AsNoTracking()
					.Where(c => c.CompanyId == co && !c.IsGroup && ((c.CallerEmployeeId == a && c.CalleeEmployeeId == other) || (c.CallerEmployeeId == other && c.CalleeEmployeeId == a))
						&& c.Status != HospitalCallStatus.Ringing && c.Status != HospitalCallStatus.Answered)
					.OrderByDescending(c => c.StartedAt).Take(60)
					.Select(c => new { c.ID, c.CallerEmployeeId, c.IsVideo, c.Status, c.MissedWhy, c.StartedAt, c.DurationSeconds, c.Reply }).ToListAsync();
				var recs = await RecSvc.ForCallsAsync(co, calls.Select(c => c.ID));
				return Json(calls.Select(c =>
				{
					recs.TryGetValue(c.ID, out var rec);
					return new
					{
						id = c.ID, outgoing = c.CallerEmployeeId == a, video = c.IsVideo, status = c.Status, missedWhy = c.MissedWhy, reply = c.Reply,
						at = DateTime.SpecifyKind(c.StartedAt, DateTimeKind.Utc), seconds = c.DurationSeconds,
						recordingId = rec != null && rec.Status == HospitalCallRecordingStatus.Saved ? rec.ID : (int?)null,
					};
				}));
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return Json(Array.Empty<object>()); }   // calls not installed here
		}
	}
}
