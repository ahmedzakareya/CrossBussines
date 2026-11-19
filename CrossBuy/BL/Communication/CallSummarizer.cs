using System.Text.Json;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// CALL SUMMARIES (owner, 2026-11-19: "since it is free, do it").
	//
	// A RECORDED call — recording already needs both sides' consent — becomes its words and a short summary (overview,
	// key points, decisions, tasks with who does them), made in the background by the internal AI service: Whisper for
	// the words, the local model already on the machine for the summary. Nothing leaves the building and nothing is
	// paid for.
	//
	// WHO. Exactly the people who may open the recording: the recording service's own rule (DetailAsync / OpenAsync —
	// the call's people and the recording reviewers) is asked, never re-implemented here.
	//
	// HOW, WITHOUT HOLDING A REQUEST. Starting uploads the recording to the service in pieces (each under the egress
	// policy's 2 MB, each its own approved egress: purpose CallSummary, PersonalData, Internal only) and returns at once.
	// Whoever looks at the recording afterwards asks for the status; when the service says done, the words and the
	// summary are stored here and the service is told to forget its copy.
	// ==========================================================================================
	public sealed class CallSummarizer
	{
		private const int Piece = 1_400_000;              // base64 grows it by a third: under the policy's 2 MB
		private const long MaxBytes = 300L * 1024 * 1024;

		private readonly CrossDbContext _db;
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providers;
		private readonly IConfiguration _config;
		private readonly HospitalCallRecordingService _recs;
		private readonly ILogger<CallSummarizer> _log;

		public CallSummarizer(CrossDbContext db, IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config, HospitalCallRecordingService recs, ILogger<CallSummarizer> log)
		{ _db = db; _ai = ai; _egress = egress; _providers = providers; _config = config; _recs = recs; _log = log; }

		public sealed record View(string Status, decimal? Progress, string? Code, object? Summary, string? Transcript, string? Language, decimal? AudioSeconds, string? RequestedBy, DateTime? At);

		/// The summary of this recording as it stands (refreshing a running one); null = not open to this person.
		public async Task<View?> GetAsync(int companyId, int meId, int recId, bool isManager, CancellationToken ct)
		{
			if (await _recs.DetailAsync(companyId, recId, meId, isManager, DateTime.UtcNow, ct) == null) return null;
			var row = await _db.Set<CallSummary>().FirstOrDefaultAsync(s => s.RecordingId == recId && s.CompanyId == companyId, ct);
			if (row == null) return new View("none", null, null, null, null, null, null, null, null);
			if (row.Status == "working") await RefreshAsync(row, ct);
			return await ViewOf(row, ct);
		}

		/// Start making it (or return the one already made / being made).
		public async Task<View?> StartAsync(int companyId, int meId, int recId, bool isManager, CancellationToken ct)
		{
			if (await _recs.DetailAsync(companyId, recId, meId, isManager, DateTime.UtcNow, ct) == null) return null;
			var row = await _db.Set<CallSummary>().FirstOrDefaultAsync(s => s.RecordingId == recId && s.CompanyId == companyId, ct);
			if (row != null && row.Status != "failed") { if (row.Status == "working") await RefreshAsync(row, ct); return await ViewOf(row, ct); }

			var rec = await _db.Set<HospitalCallRecording>().AsNoTracking().FirstAsync(r => r.ID == recId && r.CompanyId == companyId, ct);
			// the recording is opened through its own service — the same rule and the same opening log as playing it
			var file = await _recs.OpenAsync(companyId, recId, meId, isManager, DateTime.UtcNow, true, ct);
			if (file == null) return null;
			byte[] bytes;
			await using (var s = file.Content)
			{
				if (s.CanSeek && s.Length > MaxBytes) return Fail("too_large");
				using var ms = new MemoryStream();
				await s.CopyToAsync(ms, ct);
				if (ms.Length > MaxBytes) return Fail("too_large");
				bytes = ms.ToArray();
			}

			var names = await PeopleAsync(companyId, rec.CallId, ct);
			var create = await PostAsync(companyId, "/calls/jobs", new { language = (string?)null, participants = names }, AiDataClassification.PersonalData, ct);
			if (create == null || create.Status != 200) return Fail("not_available");
			string job = JsonDocument.Parse(create.Json).RootElement.GetProperty("job").GetString()!;
			for (int i = 0, k = 0; k < bytes.Length; i++, k += Piece)
			{
				var part = Convert.ToBase64String(bytes, k, Math.Min(Piece, bytes.Length - k));
				var r = await PostAsync(companyId, "/calls/jobs/chunk", new { job, index = i, dataBase64 = part }, AiDataClassification.PersonalData, ct);
				if (r == null || r.Status != 200) { await ForgetAsync(companyId, job, ct); return Fail("not_available"); }
			}
			var st = await PostAsync(companyId, "/calls/jobs/start", new { job }, AiDataClassification.OperationalMetadata, ct);
			if (st == null || st.Status != 200) { await ForgetAsync(companyId, job, ct); return Fail("not_available"); }

			var now = DateTime.UtcNow;
			if (row == null) { row = new CallSummary { RecordingId = recId, CompanyId = companyId, CallId = rec.CallId, CreatedAt = now }; _db.Add(row); }
			row.Status = "working"; row.JobId = job; row.Progress = 0; row.Error = null; row.RequestedByEmployeeId = meId; row.UpdatedAt = now;
			row.Transcript = null; row.SummaryJson = null;
			await _db.SaveChangesAsync(ct);
			return await ViewOf(row, ct);

			View Fail(string code) => new("none", null, code, null, null, null, null, null, null);
		}

		private async Task RefreshAsync(CallSummary row, CancellationToken ct)
		{
			if (row.JobId == null) return;
			var r = await PostAsync(row.CompanyId, "/calls/jobs/status", new { job = row.JobId }, AiDataClassification.OperationalMetadata, ct);
			if (r == null) return;                                   // the service is down: still working, ask again later
			if (r.Status == 404) { row.Status = "failed"; row.Error = "lost"; row.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); return; }
			if (r.Status != 200) return;
			var j = JsonDocument.Parse(r.Json).RootElement;
			var status = j.GetProperty("status").GetString();
			if (j.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number) row.Progress = Math.Round((decimal)p.GetDouble(), 3);
			if (status == "done")
			{
				row.Status = "done";
				row.Transcript = j.TryGetProperty("transcript", out var t) ? t.GetString() : null;
				row.SummaryJson = j.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetRawText() : null;
				row.Language = j.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
				row.Model = j.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
				row.AudioSeconds = j.TryGetProperty("audioSeconds", out var a) && a.ValueKind == JsonValueKind.Number ? (decimal)a.GetDouble() : null;
				row.WorkSeconds = j.TryGetProperty("workSeconds", out var w) && w.ValueKind == JsonValueKind.Number ? (decimal)w.GetDouble() : null;
				var job = row.JobId; row.JobId = null; row.Progress = 1;
				row.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
				await ForgetAsync(row.CompanyId, job!, ct);            // the service deletes its copy of the words
				return;
			}
			if (status == "failed")
			{
				var err = j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "failed";
				row.Status = "failed"; row.Error = err!.Length > 200 ? err[..200] : err; row.UpdatedAt = DateTime.UtcNow;
				await _db.SaveChangesAsync(ct);
				await ForgetAsync(row.CompanyId, row.JobId, ct);
				return;
			}
			row.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
		}

		private Task ForgetAsync(int companyId, string job, CancellationToken ct)
			=> PostAsync(companyId, "/calls/jobs/forget", new { job }, AiDataClassification.OperationalMetadata, ct);

		private async Task<View> ViewOf(CallSummary row, CancellationToken ct)
		{
			object? summary = null;
			if (row.SummaryJson != null) { try { summary = JsonSerializer.Deserialize<JsonElement>(row.SummaryJson); } catch (JsonException) { } }
			var by = await _db.Employee.AsNoTracking().Where(e => e.ID == row.RequestedByEmployeeId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			return new View(row.Status, row.Progress, row.Status == "failed" ? "failed" : null, summary, row.Transcript, row.Language, row.AudioSeconds,
				by == null ? null : EmployeeNames.Of(by.FullName, by.FullNameEn), DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));
		}

		/// The people in the call, by name — so the summary can say who is to do what.
		private async Task<List<string>> PeopleAsync(int companyId, int callId, CancellationToken ct)
		{
			var call = await _db.Set<HospitalCall>().AsNoTracking().FirstAsync(c => c.ID == callId && c.CompanyId == companyId, ct);
			var ids = new List<int> { call.CallerEmployeeId, call.CalleeEmployeeId };
			ids.AddRange(await _db.Set<HospitalCallParticipant>().AsNoTracking().Where(p => p.CallId == callId && p.CompanyId == companyId && p.JoinedAt != null).Select(p => p.EmployeeId).ToListAsync(ct));
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).Select(e => new { e.FullName, e.FullNameEn }).ToListAsync(ct);
			return people.Select(p => EmployeeNames.Of(p.FullName, p.FullNameEn)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(12).ToList();
		}

		/// One approved post to the AI service; null when the policy refused or the service did not answer.
		private async Task<AiProxyResult?> PostAsync(int companyId, string path, object payload, AiDataClassification cls, CancellationToken ct)
		{
			var scope = OpenAiOptions.FromConfiguration(_config).Scope;
			var decision = await _egress.EvaluateAsync(new AiEgressRequest
			{
				Purpose = AiEgressPurpose.CallSummary,
				Destination = AiDestinationResolver.Resolve(_config, AiEgressPurpose.CallSummary, _providers.Assess(DateTime.UtcNow, scope).State),
				ProviderScope = scope,
				Classification = cls,
				DataCompanyId = companyId,
				PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
			}, ct);
			if (!decision.Allowed || decision.Approval == null)
			{
				_log.LogInformation("Call summary step {Path} not sent: egress denied ({Reason}).", path, decision.Reason);
				return null;
			}
			try { return await _ai.PostAsync(decision.Approval, path, payload, ct); }
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
			{
				_log.LogWarning("Call summary step {Path}: the AI service did not answer ({Kind}).", path, e.GetType().Name);
				return null;
			}
		}
	}
}
