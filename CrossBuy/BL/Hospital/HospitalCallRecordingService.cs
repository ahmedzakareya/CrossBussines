using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H176 — recording a call, with every side's consent INSIDE the call. Design: the approved prototype (#/calls,
	// #/calls-setup — the record button, the consent window, the red REC badge, the player in the log, the settings card).
	//
	// THE CONSENT IS THE GATE, NOT A FORMALITY. A recording row is born "Asked"; only the OTHER participant can move it to
	// "Recording"; nothing is accepted for a recording in any other state, so no byte can be kept without that answer. Who
	// asked, who agreed and when are kept on the row, and every opening of the file afterwards is logged.
	//
	// WHERE THE BYTES GO. The requester's browser holds both sides of the call, so it records them (voice mixed, the two
	// pictures drawn together) and sends the file in pieces, in order, to a private temporary file outside the web root.
	// When it stops, the whole file becomes a document on the HospitalCall record of the central document platform (owner
	// rule: every file in the File Manager) and the temporary file is deleted. The platform lets only an administrator
	// read it directly; the two participants read it through the calls screen, which checks them.
	//
	// RETENTION. A saved recording carries the day it is deleted (the settings' days, or never). The sweep deletes its
	// bytes then and keeps the row: that a recording existed, and who agreed, stays on record.
	// ==========================================================================================
	public static class HospitalCallRecordingRules
	{
		/// The groups that may ask when the settings name none: doctors and administration.
		public static readonly IReadOnlyList<string> DefaultGroups = new[] { HospitalCallGroups.Doctors, HospitalCallGroups.Administration };
		public const int MaxChunkBytes = 8 * 1024 * 1024;
		public const long MaxBytes = 2L * 1024 * 1024 * 1024;
		public static readonly IReadOnlyList<int> RetentionChoices = new[] { 30, 90, 365, 0 };

		public static IReadOnlyList<string> ParseGroups(string? stored)
		{
			if (string.IsNullOrWhiteSpace(stored)) return DefaultGroups;
			if (stored.Trim() == "-") return Array.Empty<string>();
			return stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(g => HospitalCallGroups.All.Contains(g)).Distinct().ToList();
		}

		public static string FormatGroups(IEnumerable<string>? groups)
		{
			var g = (groups ?? Array.Empty<string>()).Where(x => HospitalCallGroups.All.Contains(x)).Distinct().ToList();
			return g.Count == 0 ? "-" : string.Join(",", g);   // none at all is stored explicitly: empty would mean the default
		}

		/// Whether this person may ask to record: recording is on and one of their groups is allowed to ask.
		public static bool MayAsk(bool enabled, string? storedGroups, IEnumerable<string> personGroups)
			=> enabled && personGroups.Any(g => ParseGroups(storedGroups).Contains(g));

		/// What the file holds: the picture only when the settings allow it AND there is a picture — a video call, or a
		/// camera or shared screen live when recording is asked (a voice call where someone shows their screen).
		public static string KindFor(string? mode, bool callIsVideo, bool pictureNow = false) => mode == HospitalCallRecordingMode.Both && (callIsVideo || pictureNow) ? "video" : "voice";

		public static DateTime? DeleteAfter(DateTime savedUtc, int days) => days > 0 ? savedUtc.AddDays(days) : null;

		/// A WebM/Matroska file starts with the EBML header 1A 45 DF A3 — what MediaRecorder writes. Anything else is refused.
		public static bool IsWebm(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;

		/// Why this piece cannot be taken, or null: the right status, the next piece in order, a sane size, a WebM start.
		public static string? ChunkRefusal(string status, int expectedSeq, int seq, long size, long soFar, bool first, bool isWebm)
		{
			if (status != HospitalCallRecordingStatus.Recording) return "This recording is not running";
			if (seq != expectedSeq) return "A piece of the recording arrived out of order";
			if (size <= 0 || size > MaxChunkBytes) return "A piece of the recording is too large";
			if (soFar + size > MaxBytes) return "The recording is too long";
			if (first && !isWebm) return "The recording is not in a supported format";
			return null;
		}

		/// The status a recording moves to, or null when that is not allowed now.
		public static string? Next(string status, string action, bool byRequester) => (status, action) switch
		{
			(HospitalCallRecordingStatus.Asked, "agree") when !byRequester => HospitalCallRecordingStatus.Recording,
			(HospitalCallRecordingStatus.Asked, "refuse") when !byRequester => HospitalCallRecordingStatus.Refused,
			(HospitalCallRecordingStatus.Asked, "cancel") => HospitalCallRecordingStatus.Refused,
			(HospitalCallRecordingStatus.Recording, "save") when byRequester => HospitalCallRecordingStatus.Saved,
			(HospitalCallRecordingStatus.Recording, "fail") => HospitalCallRecordingStatus.Failed,
			(HospitalCallRecordingStatus.Saved, "expire") => HospitalCallRecordingStatus.Deleted,
			_ => null,
		};

		public static string FileName(int callId, int recId) => $"CALL-{callId:000000}-{recId}.webm";

		/// The named reviewers, as stored (ids, comma separated); anything not a positive number is dropped.
		public static IReadOnlyList<int> ParseIds(string? stored) =>
			(stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct().ToList();

		public static string? FormatIds(IEnumerable<int>? ids)
		{
			var l = (ids ?? Array.Empty<int>()).Where(i => i > 0).Distinct().Take(40).ToList();
			return l.Count == 0 ? null : string.Join(",", l);
		}
	}

	/// The recordings on a call's platform record: only an EXPLICITLY assigned administrator reads them THROUGH THE PLATFORM (the participants
	/// read through the calls screen, which checks they were in the call); a participant uploads through that screen too.
	public sealed class HospitalCallDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.HospitalCall;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.Set<HospitalCall>().AsNoTracking().Where(c => c.ID == entityId && entityId > 0).Select(c => (int?)c.CompanyId).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId) => PermissionTarget.ForEntity(EntityRegistry.HospitalCall, entityId);
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.Upload => HospitalActions.Read,
			// never bootstrap-open (HospitalActions.CallRecordings): the generic document screens show nobody else's call
			DocumentAction.View or DocumentAction.Download or DocumentAction.Replace or DocumentAction.Delete or DocumentAction.Manage => HospitalActions.CallRecordings,
			_ => "hospital-document-unsupported",
		};
	}

	public sealed record HospitalCallRecordingOpen(Stream Content, string ContentType, string FileName);

	public sealed class HospitalCallRecordingService
	{
		public const string Metadata = "{\"hospital\":\"call-recording\"}";   // the platform takes a JSON object
		public const string NoTables = "Call recording is not installed yet (hospital_h176_call_recording.sql).";
		private readonly CrossDbContext _db;
		private readonly IPlatformDocumentService _docs;
		private readonly IDocumentStorage _storage;
		private readonly HospitalCallService _calls;

		public HospitalCallRecordingService(CrossDbContext db, IPlatformRoleDirectory roles, IPlatformDocumentService docs, IDocumentStorage storage)
		{ _db = db; _docs = docs; _storage = storage; _calls = new HospitalCallService(db, roles); }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalCallRecording> Recs(int companyId) => _db.Set<HospitalCallRecording>().Where(r => r.CompanyId == companyId);
		private static string TempPath(string dir, string name) => Path.Combine(dir, name + ".part");

		/// What a participant of this call may do about recording: ask, and what would be recorded.
		public async Task<(bool MayAsk, string Kind, int Days)> OptionsAsync(int companyId, HospitalCallPerson person, bool callIsVideo, CancellationToken ct = default)
		{
			var s = await _calls.SettingsAsync(companyId, ct);
			return (HospitalCallRecordingRules.MayAsk(s.RecordingEnabled, s.RecordingGroups, person.Groups), HospitalCallRecordingRules.KindFor(s.RecordingMode, callIsVideo), s.RecordingDays);
		}

		/// A participant of an ANSWERED call asks to record it. Null + reason when it may not be asked now.
		public async Task<(HospitalCallRecording? Row, string? Error)> AskAsync(int companyId, int callId, int employeeId, DateTime nowUtc, bool pictureNow = false, CancellationToken ct = default)
		{
			try
			{
				var call = await _db.Set<HospitalCall>().AsNoTracking().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == companyId, ct);
				if (call == null) return (null, "not_found");
				bool inIt = call.IsGroup
					? await _db.Set<HospitalCallParticipant>().AnyAsync(p => p.CallId == callId && p.EmployeeId == employeeId && (p.State == HospitalCallParticipantState.Host || p.State == HospitalCallParticipantState.Joined), ct)
					: call.CallerEmployeeId == employeeId || call.CalleeEmployeeId == employeeId;
				if (!inIt) return (null, "not_found");
				if (call.Status != HospitalCallStatus.Answered) return (null, "Recording starts only once the call is answered");
				var me = (await _calls.PeopleAsync(companyId, nowUtc, new[] { employeeId }, ct)).FirstOrDefault();
				var s = await _calls.SettingsAsync(companyId, ct);
				if (me == null || !HospitalCallRecordingRules.MayAsk(s.RecordingEnabled, s.RecordingGroups, me.Groups)) return (null, "You may not ask to record calls (call settings)");
				if (await Recs(companyId).AnyAsync(r => r.CallId == callId && (r.Status == HospitalCallRecordingStatus.Asked || r.Status == HospitalCallRecordingStatus.Recording), ct))
					return (null, "This call is already being recorded or asked to be");
				var row = new HospitalCallRecording
				{
					CompanyId = companyId, CallId = callId, RequestedByEmployeeId = employeeId, RequestedAt = nowUtc,
					Status = HospitalCallRecordingStatus.Asked, Kind = HospitalCallRecordingRules.KindFor(s.RecordingMode, call.IsVideo, pictureNow),
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return (row, null);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return (null, NoTables); }
		}

		/// The OTHER participant agrees or refuses; the requester may withdraw while it is only asked.
		public async Task<HospitalCallRecording?> AnswerAsync(int companyId, int recId, int employeeId, string action, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Recs(companyId).FirstOrDefaultAsync(r => r.ID == recId, ct);
			if (row == null) return null;
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstOrDefaultAsync(c => c.ID == row.CallId && c.CompanyId == companyId, ct);
			if (call == null || (call.CallerEmployeeId != employeeId && call.CalleeEmployeeId != employeeId)) return null;
			var next = HospitalCallRecordingRules.Next(row.Status, action, row.RequestedByEmployeeId == employeeId);
			if (next == null) return null;
			row.Status = next;
			if (next == HospitalCallRecordingStatus.Recording)
			{
				if (call.Status != HospitalCallStatus.Answered) return null;   // the call ended while the question was open
				row.AgreedByEmployeeId = employeeId; row.AgreedAt = nowUtc; row.StartedAt = nowUtc;
				row.TempName = Guid.NewGuid().ToString("N");
			}
			await _db.SaveChangesAsync(ct);
			return row;
		}

		/// A GROUP recording starts once every person in the call has agreed (the hub counts the consents), or is refused
		/// the moment anyone says no. Null when the recording is not waiting for that answer.
		public async Task<HospitalCallRecording?> SettleGroupAsync(int companyId, int recId, bool start, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Recs(companyId).FirstOrDefaultAsync(r => r.ID == recId && r.Status == HospitalCallRecordingStatus.Asked, ct);
			if (row == null) return null;
			if (start) { row.Status = HospitalCallRecordingStatus.Recording; row.StartedAt = nowUtc; row.AgreedAt = nowUtc; row.TempName = Guid.NewGuid().ToString("N"); }
			else row.Status = HospitalCallRecordingStatus.Refused;
			await _db.SaveChangesAsync(ct);
			return row;
		}

		public async Task MarkStoppedAsync(int companyId, int recId, int employeeId, CancellationToken ct = default)
		{
			var row = await Recs(companyId).FirstOrDefaultAsync(r => r.ID == recId && r.Status == HospitalCallRecordingStatus.Recording, ct);
			if (row == null || row.StoppedByEmployeeId != null) return;
			row.StoppedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
		}

		/// The requester's browser sends the next piece, in order. Appended to the private temporary file.
		public async Task<HospitalResult> ChunkAsync(int companyId, int recId, int employeeId, int seq, Stream body, string tempDir, CancellationToken ct = default)
		{
			try
			{
				var row = await Recs(companyId).FirstOrDefaultAsync(r => r.ID == recId, ct);
				if (row == null || row.RequestedByEmployeeId != employeeId || row.TempName == null) return HospitalResult.Fail("not_found");
				using var buffer = new MemoryStream();
				var limited = new byte[81920]; int n; long total = 0;
				while ((n = await body.ReadAsync(limited, ct)) > 0)
				{
					total += n;
					if (total > HospitalCallRecordingRules.MaxChunkBytes) return HospitalResult.Fail("A piece of the recording is too large");
					buffer.Write(limited, 0, n);
				}
				bool webm = HospitalCallRecordingRules.IsWebm(buffer.GetBuffer().AsSpan(0, (int)Math.Min(4, buffer.Length)));   // no span local in an async method (C# 12)
				if (HospitalCallRecordingRules.ChunkRefusal(row.Status, row.Chunks, seq, buffer.Length, row.SizeBytes ?? 0, seq == 0, webm) is string why) return HospitalResult.Fail(why);
				Directory.CreateDirectory(tempDir);
				await using (var f = new FileStream(TempPath(tempDir, row.TempName), seq == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
				{
					buffer.Position = 0;
					await buffer.CopyToAsync(f, ct);
				}
				row.Chunks++; row.SizeBytes = (row.SizeBytes ?? 0) + buffer.Length;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The requester's browser stopped: the whole file becomes a document on the call's record; the temp file goes.
		public async Task<HospitalResult> FinishAsync(BusinessContext ctx, int recId, int seconds, string tempDir, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Recs(ctx.CompanyId).FirstOrDefaultAsync(r => r.ID == recId, ct);
			if (row == null || row.RequestedByEmployeeId != ctx.EmployeeId) return HospitalResult.Fail("not_found");
			if (row.Status == HospitalCallRecordingStatus.Saved) return HospitalResult.Success(row.ID);   // sent twice, done once
			if (row.Status != HospitalCallRecordingStatus.Recording || row.TempName == null) return HospitalResult.Fail("This recording is not running");
			var path = TempPath(tempDir, row.TempName);
			if (!File.Exists(path) || (row.SizeBytes ?? 0) == 0)
			{
				row.Status = HospitalCallRecordingStatus.Failed; row.EndedAt = nowUtc; row.TempName = null;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Fail("Nothing of the recording arrived");
			}
			var s = await _calls.SettingsAsync(ctx.CompanyId, ct);
			DocumentResult doc;
			await using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				doc = await _docs.UploadAsync(new DocumentUploadRequest
				{
					EntityType = EntityRegistry.HospitalCall, EntityId = row.CallId, FileName = HospitalCallRecordingRules.FileName(row.CallId, row.ID),
					ContentType = row.Kind == "video" ? "video/webm" : "audio/webm", Metadata = Metadata, Reason = "Call recording (both sides agreed)",
				}, f, ct);
			}
			if (!doc.Ok) return HospitalResult.Fail(doc.ReasonCode is "store_failed" ? "The change could not be saved" : "forbidden");
			row.Status = HospitalCallRecordingStatus.Saved; row.DocumentId = doc.DocumentId; row.EndedAt = nowUtc;
			row.DurationSeconds = Math.Clamp(seconds, 0, 24 * 3600);
			row.DeleteAfter = HospitalCallRecordingRules.DeleteAfter(nowUtc, s.RecordingDays);
			row.TempName = null;
			await _db.SaveChangesAsync(ct);
			try { File.Delete(path); } catch (IOException) { }
			return HospitalResult.Success(row.ID);
		}

		private async Task<bool> MaySeeAsync(int companyId, HospitalCallRecording row, int employeeId, bool isManager, CancellationToken ct)
		{
			if (isManager) return true;
			if (await _db.Set<HospitalCall>().AsNoTracking().AnyAsync(c => c.ID == row.CallId && c.CompanyId == companyId && (c.CallerEmployeeId == employeeId || c.CalleeEmployeeId == employeeId), ct)) return true;
			// a group call's recording is theirs too who took part in the call (joined at some point)
			return await new HospitalCallGroupService(_db).TookPartAsync(companyId, row.CallId, employeeId, ct);
		}

		/// The file, for a participant or an administrator — and the opening is logged. Null when missing or refused alike.
		public async Task<HospitalCallRecordingOpen?> OpenAsync(int companyId, int recId, int employeeId, bool isManager, DateTime nowUtc, bool logView, CancellationToken ct = default)
		{
			var row = await Recs(companyId).AsNoTracking().FirstOrDefaultAsync(r => r.ID == recId && r.Status == HospitalCallRecordingStatus.Saved, ct);
			if (row?.DocumentId is not long d || !await MaySeeAsync(companyId, row, employeeId, isManager, ct)) return null;
			var v = await (from doc in _db.Set<PlatformDocument>().AsNoTracking()
						   join ver in _db.Set<PlatformDocumentVersion>().AsNoTracking() on doc.CurrentVersionId equals ver.Id
						   where doc.Id == d && doc.CompanyID == companyId && doc.EntityType == EntityRegistry.HospitalCall && doc.EntityId == row.CallId
						   select new { ver.StorageKey, ver.ContentType, ver.FileName }).FirstOrDefaultAsync(ct);
			if (v == null || !StorageKey.TryParse(v.StorageKey, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			if (s == null) return null;
			if (logView)
			{
				_db.Add(new HospitalCallRecordingView { CompanyId = companyId, RecordingId = row.ID, EmployeeId = employeeId, At = nowUtc });
				await _db.SaveChangesAsync(ct);
			}
			return new HospitalCallRecordingOpen(s, v.ContentType, v.FileName);
		}

		/// The player's facts: the call, who asked and who agreed, size, length, deletion day, the file, who opened it.
		public async Task<object?> DetailAsync(int companyId, int recId, int employeeId, bool isManager, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Recs(companyId).AsNoTracking().FirstOrDefaultAsync(r => r.ID == recId, ct);
			if (row == null || !await MaySeeAsync(companyId, row, employeeId, isManager, ct)) return null;
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstAsync(c => c.ID == row.CallId && c.CompanyId == companyId, ct);
			var views = await _db.Set<HospitalCallRecordingView>().AsNoTracking().Where(v => v.CompanyId == companyId && v.RecordingId == recId)
				.OrderByDescending(v => v.At).Take(20).ToListAsync(ct);
			var consents = new List<HospitalCallRecordingConsent>();
			try { consents = await _db.Set<HospitalCallRecordingConsent>().AsNoTracking().Where(c => c.CompanyId == companyId && c.RecordingId == recId).OrderBy(c => c.At).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var ids = new[] { call.CallerEmployeeId, call.CalleeEmployeeId, row.RequestedByEmployeeId }.Concat(views.Select(v => v.EmployeeId)).Concat(consents.Select(c => c.EmployeeId)).Concat(row.AgreedByEmployeeId is int a ? new[] { a } : Array.Empty<int>()).Distinct();
			var people = (await _calls.PeopleAsync(companyId, nowUtc, ids, ct)).ToDictionary(p => p.EmployeeId);
			object? P(int id) => people.TryGetValue(id, out var p) ? new { id, name = DisplayName.Of(p.Name, p.NameEn), photo = p.Photo, initials = HospitalUi.Initials(p.NameEn) } : null;
			var patient = call.PatientId is int pid ? await _calls.PatientCardAsync(companyId, pid, ct) : null;
			return new
			{
				id = row.ID, callId = call.ID, status = row.Status, kind = row.Kind, outgoing = call.CallerEmployeeId == employeeId,
				other = P(call.CallerEmployeeId == employeeId ? call.CalleeEmployeeId : call.CallerEmployeeId), caller = P(call.CallerEmployeeId), callee = P(call.CalleeEmployeeId),
				startedAt = call.StartedAt, requestedBy = P(row.RequestedByEmployeeId), requestedAt = row.RequestedAt, agreedBy = row.AgreedByEmployeeId is int ab ? P(ab) : null, agreedAt = row.AgreedAt,
				seconds = row.DurationSeconds, size = row.SizeBytes, deleteAfter = row.DeleteAfter, deletedAt = row.DeletedAt,
				file = HospitalCallRecordingRules.FileName(row.CallId, row.ID), folder = "CALL-" + call.ID.ToString("000000"), patient,
				views = views.Select(v => new { who = P(v.EmployeeId), at = v.At }),
				group = call.IsGroup,
				consents = consents.Select(c => new { who = P(c.EmployeeId), at = c.At, via = c.Via }),
			};
		}

		/// The latest saved recording of each call (for the log's mark and play button).
		public async Task<Dictionary<int, HospitalCallRecording>> ForCallsAsync(int companyId, IEnumerable<int> callIds, CancellationToken ct = default)
		{
			var ids = callIds.Distinct().ToList();
			try
			{
				var rows = await Recs(companyId).AsNoTracking().Where(r => ids.Contains(r.CallId) && (r.Status == HospitalCallRecordingStatus.Saved || r.Status == HospitalCallRecordingStatus.Deleted)).ToListAsync(ct);
				return rows.GroupBy(r => r.CallId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ID).First());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(); }
		}

		/// Retention and leftovers: an expired recording's bytes are deleted (its row stays); a question nobody answered in
		/// ten minutes is closed; a recording whose browser vanished is finished from what arrived (when it is mine) or failed.
		public async Task SweepAsync(BusinessContext ctx, string tempDir, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var expired = await Recs(ctx.CompanyId).Where(r => r.Status == HospitalCallRecordingStatus.Saved && r.DeleteAfter != null && r.DeleteAfter < nowUtc).ToListAsync(ct);
				foreach (var r in expired)
				{
					if (r.DocumentId is long d)
					{
						var keys = await _db.Set<PlatformDocumentVersion>().AsNoTracking().Where(v => v.DocumentId == d && v.CompanyID == ctx.CompanyId).Select(v => v.StorageKey).ToListAsync(ct);
						foreach (var k in keys) if (StorageKey.TryParse(k, out var key)) { try { await _storage.DeleteAsync(key, ct); } catch (Exception) { } }
					}
					r.Status = HospitalCallRecordingStatus.Deleted; r.DeletedAt = nowUtc;
				}
				var unanswered = await Recs(ctx.CompanyId).Where(r => r.Status == HospitalCallRecordingStatus.Asked && r.RequestedAt < nowUtc.AddMinutes(-10)).ToListAsync(ct);
				foreach (var r in unanswered) r.Status = HospitalCallRecordingStatus.Refused;
				if (expired.Count + unanswered.Count > 0) await _db.SaveChangesAsync(ct);

				// a recording still "running" after its call ended more than two minutes ago
				var stale = await (from r in Recs(ctx.CompanyId)
								   join c in _db.Set<HospitalCall>() on r.CallId equals c.ID
								   where r.Status == HospitalCallRecordingStatus.Recording && c.CompanyId == ctx.CompanyId && c.Status != HospitalCallStatus.Answered && c.EndedAt < nowUtc.AddMinutes(-2)
								   select new { r.ID, r.RequestedByEmployeeId, c.DurationSeconds }).ToListAsync(ct);
				foreach (var s in stale)
				{
					if (s.RequestedByEmployeeId == ctx.EmployeeId) { await FinishAsync(ctx, s.ID, s.DurationSeconds ?? 0, tempDir, nowUtc, ct); continue; }
					var r = await Recs(ctx.CompanyId).FirstAsync(x => x.ID == s.ID, ct);
					if (r.StartedAt < nowUtc.AddHours(-6))
					{
						if (r.TempName != null) { try { File.Delete(TempPath(tempDir, r.TempName)); } catch (IOException) { } }
						r.Status = HospitalCallRecordingStatus.Failed; r.EndedAt = nowUtc; r.TempName = null;
						await _db.SaveChangesAsync(ct);
					}
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); }
		}
	}
}
