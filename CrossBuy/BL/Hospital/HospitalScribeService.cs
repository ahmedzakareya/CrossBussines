using System.Text.Json;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalScribeResult(bool Ok, string? Code, string? Transcript, string? Language, HospitalScribeDraft? Draft);

	// ==========================================================================================
	// HOSPITAL H254 — AMBIENT DOCUMENTATION. The doctor records the visit on the consultation screen; the recording is
	// turned into words by the hospital's own speech service (the same internal Whisper and the same egress purpose as a
	// voice message — SpeechTranscription, PersonalData, kept inside the building) and drafted into the SOAP fields by
	// HospitalScribeRules. The recording itself is never stored. What was heard and drafted is logged (who, which visit,
	// when, the transcript) so a note can be traced; the note is saved only by the doctor, as always.
	// ==========================================================================================
	public sealed class HospitalScribeService
	{
		public const int MaxAudioBytes = 1_400_000;   // the speech service takes about a minute and a half per piece
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providers;
		private readonly IConfiguration _config;
		public HospitalScribeService(CrossDbContext db, IHospitalAccessService access, IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config)
		{ _db = db; _access = access; _ai = ai; _egress = egress; _providers = providers; _config = config; }

		public async Task<HospitalScribeResult> TranscribeAsync(BusinessContext ctx, int encounterId, byte[] audio, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return new(false, "forbidden", null, null, null);
			var e = await _db.Set<HospitalEncounter>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == encounterId && x.CompanyId == ctx.CompanyId, ct);
			if (e == null) return new(false, "not_found", null, null, null);
			if (e.Status != HospitalEncounterStatus.Draft) return new(false, "signed", null, null, null);
			if (audio.Length == 0) return new(false, "empty", null, null, null);
			if (audio.Length > MaxAudioBytes) return new(false, "too_long", null, null, null);

			var payload = new { audioBase64 = Convert.ToBase64String(audio), language = (string?)null };
			var scope = OpenAiOptions.FromConfiguration(_config).Scope;
			var decision = await _egress.EvaluateAsync(new AiEgressRequest
			{
				Purpose = AiEgressPurpose.SpeechTranscription,
				Destination = AiDestinationResolver.Resolve(_config, AiEgressPurpose.SpeechTranscription, _providers.Assess(DateTime.UtcNow, scope).State),
				ProviderScope = scope,
				Classification = AiDataClassification.PersonalData,
				DataCompanyId = ctx.CompanyId,
				PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
			}, ct);
			if (!decision.Allowed || decision.Approval == null) return new(false, "not_available", null, null, null);

			AiProxyResult r;
			try { r = await _ai.PostAsync(decision.Approval, "/speech/transcribe", payload, ct); }
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, "not_available", null, null, null); }
			if (r.Status == 413) return new(false, "too_long", null, null, null);
			if (r.Status != 200) return new(false, "not_available", null, null, null);

			using var doc = JsonDocument.Parse(r.Json);
			var j = doc.RootElement;
			string text = j.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
			string? lang = j.TryGetProperty("language", out var l) ? l.GetString() : null;
			var draft = HospitalScribeRules.Draft(text);
			try
			{
				_db.Add(new HospitalScribeLog { CompanyId = ctx.CompanyId, EncounterId = e.ID, PatientId = e.PatientId, Transcript = text.Length > 8000 ? text[..8000] : text, Language = lang, At = nowUtc, ByEmployeeId = ctx.EmployeeId });
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); }   // the draft still helps; the trail waits for the slice
			return new(true, null, text, lang, draft);
		}
	}
}
