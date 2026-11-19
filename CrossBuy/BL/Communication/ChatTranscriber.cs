using System.Text.Json;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// SPEECH TO TEXT for voice messages (owner, 2026-11-19: approved, "start").
	//
	// The words of a voice message, ON REQUEST: someone who may read the message presses «النص»; the recording goes
	// to the internal AI service (crossbuy_ai /speech/transcribe, Whisper on this machine's processor) and the text
	// is stored, so the next reader gets it at once and the recording is never transcribed twice.
	//
	// The recording is PERSONAL DATA (a person's voice). It leaves .NET only through the egress policy, under its own
	// purpose (SpeechTranscription), and the policy lets that purpose — and only that one — carry personal data, and
	// only to an Internal destination. Who may ask is the caller's job: the controllers ask the chat's own authority
	// (communication "read" on that conversation) before calling here.
	// ==========================================================================================
	public sealed class ChatTranscriber
	{
		/// The policy carries at most 2 MB of JSON; base64 adds a third. 1.4 MB of opus is about three minutes.
		public const int MaxAudioBytes = 1_400_000;

		private readonly CrossDbContext _db;
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providers;
		private readonly IConfiguration _config;
		private readonly IWebHostEnvironment _env;
		private readonly ILogger<ChatTranscriber> _log;

		public ChatTranscriber(CrossDbContext db, IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config, IWebHostEnvironment env, ILogger<ChatTranscriber> log)
		{ _db = db; _ai = ai; _egress = egress; _providers = providers; _config = config; _env = env; _log = log; }

		public sealed record Result(bool Ok, string Status, string? Text, string? Language, string? Code);

		/// The stored words of these messages (for the messages a reader just loaded).
		public static async Task<Dictionary<int, ChatTranscript>> StoredAsync(CrossDbContext db, IReadOnlyCollection<int> ids)
			=> ids.Count == 0 ? new() : await db.Set<ChatTranscript>().AsNoTracking().Where(t => ids.Contains(t.MessageId)).ToDictionaryAsync(t => t.MessageId);

		public async Task<Result> TranscribeAsync(int companyId, int meId, int messageId, CancellationToken ct)
		{
			var have = await _db.Set<ChatTranscript>().AsNoTracking().FirstOrDefaultAsync(t => t.MessageId == messageId, ct);
			if (have != null && have.Status != "failed") return From(have);

			var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == messageId && x.DeletedAt == null)
				.Select(x => new { x.ConversationId, x.AttachmentPath, x.AttachmentType }).FirstOrDefaultAsync(ct);
			if (m == null || m.AttachmentType != "audio" || m.AttachmentPath == null) return new(false, "none", null, null, "not_a_voice_message");
			bool mine = await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == m.ConversationId && c.CompanyID == companyId, ct);
			if (!mine) return new(false, "none", null, null, "not_found");

			var full = await ChatAttachmentFiles.ResolveAsync(_db, _env, companyId, m.AttachmentPath, ct);
			if (full == null) return new(false, "none", null, null, "file_missing");
			var bytes = await File.ReadAllBytesAsync(full, ct);
			if (bytes.Length > MaxAudioBytes) return From(await SaveAsync(new ChatTranscript { MessageId = messageId, CompanyId = companyId, Status = "too_long", RequestedByEmployeeId = meId }, ct));

			var payload = new { audioBase64 = Convert.ToBase64String(bytes), language = (string?)null };
			var scope = OpenAiOptions.FromConfiguration(_config).Scope;
			var decision = await _egress.EvaluateAsync(new AiEgressRequest
			{
				Purpose = AiEgressPurpose.SpeechTranscription,
				Destination = AiDestinationResolver.Resolve(_config, AiEgressPurpose.SpeechTranscription, _providers.Assess(DateTime.UtcNow, scope).State),
				ProviderScope = scope,
				// A recording of a person's voice: the policy keeps it inside the building, for this purpose only.
				Classification = AiDataClassification.PersonalData,
				DataCompanyId = companyId,
				PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
			}, ct);
			if (!decision.Allowed || decision.Approval == null)
			{
				_log.LogInformation("Voice message {Id} not transcribed: egress denied ({Reason}).", messageId, decision.Reason);
				return new(false, "none", null, null, "not_available");
			}

			AiProxyResult r;
			try { r = await _ai.PostAsync(decision.Approval, "/speech/transcribe", payload, ct); }
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
			{
				// the service is down or took too long: nothing is stored, the next press tries again
				_log.LogWarning("Voice message {Id}: the speech service did not answer ({Kind}).", messageId, e.GetType().Name);
				return new(false, "none", null, null, "not_available");
			}
			if (r.Status == 503 || r.Status == 401 || r.Status >= 500) return new(false, "none", null, null, "not_available");
			// longer than the service will take on (about a minute and a half: the reply must come inside our 120 s)
			if (r.Status == 413) return From(await SaveAsync(new ChatTranscript { MessageId = messageId, CompanyId = companyId, Status = "too_long", RequestedByEmployeeId = meId }, ct));
			if (r.Status != 200)
				return From(await SaveAsync(new ChatTranscript { MessageId = messageId, CompanyId = companyId, Status = "failed", Error = ("http " + r.Status).Length > 200 ? null : "http " + r.Status, RequestedByEmployeeId = meId }, ct));

			using var doc = JsonDocument.Parse(r.Json);
			var j = doc.RootElement;
			var row = new ChatTranscript
			{
				MessageId = messageId, CompanyId = companyId, Status = "done", RequestedByEmployeeId = meId,
				Text = j.TryGetProperty("text", out var t) ? t.GetString() : "",
				Language = j.TryGetProperty("language", out var l) ? l.GetString() : null,
				Model = j.TryGetProperty("model", out var md) ? md.GetString() : null,
				AudioSeconds = j.TryGetProperty("duration", out var d) ? (decimal)d.GetDouble() : null,
				WorkSeconds = j.TryGetProperty("seconds", out var s) ? (decimal)s.GetDouble() : null,
			};
			return From(await SaveAsync(row, ct));
		}

		private async Task<ChatTranscript> SaveAsync(ChatTranscript row, CancellationToken ct)
		{
			row.CreatedAt = DateTime.UtcNow;
			var old = await _db.Set<ChatTranscript>().FirstOrDefaultAsync(t => t.MessageId == row.MessageId, ct);
			if (old != null) _db.Remove(old);
			_db.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException)
			{
				// two readers pressed at once: the other one's words are as good as these
				_db.ChangeTracker.Clear();
				return await _db.Set<ChatTranscript>().AsNoTracking().FirstAsync(t => t.MessageId == row.MessageId, ct);
			}
			return row;
		}

		private static Result From(ChatTranscript t) => new(t.Status == "done", t.Status, t.Text, t.Language, t.Status == "done" ? null : t.Status);
	}

	/// Where a chat attachment lives on disk: a plain /uploads/ path, or a File Manager item (…fileId=N) resolved
	/// to its stored path — inside wwwroot only. One place, for the app's file endpoint and the transcriber.
	public static class ChatAttachmentFiles
	{
		public static async Task<string?> ResolveAsync(CrossDbContext db, IWebHostEnvironment env, int companyId, string attachmentPath, CancellationToken ct)
		{
			string? rel = attachmentPath;
			var fid = System.Text.RegularExpressions.Regex.Match(rel, @"fileId=(\d+)");
			if (fid.Success)
			{
				int id = int.Parse(fid.Groups[1].Value);
				rel = await db.Set<CrossBuy.Models.Context.Library.LibraryItem>().AsNoTracking().Where(i => i.Id == id && i.CompanyID == companyId).Select(i => i.StoredPath).FirstOrDefaultAsync(ct);
			}
			if (string.IsNullOrEmpty(rel) || !rel.StartsWith("/uploads/", StringComparison.Ordinal) || rel.Contains("..")) return null;
			var root = Path.GetFullPath(env.WebRootPath);
			var full = Path.GetFullPath(Path.Combine(env.WebRootPath, rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
			return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
		}
	}
}
