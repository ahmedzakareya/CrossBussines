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
	// HOSPITAL H163 — documents and communication at the desk (see HospitalPatientContact.cs), all administrative:
	//   · contact preferences and assistance needs; the SMS opt-out (no messages at all) stays HospitalSmsService's;
	//   · representatives (who speaks for the patient), added and stopped with a reason;
	//   · scans of the ID and insurance cards: documents on the patient's record in the document platform (the File
	//     Manager rule) — the platform authorises and keeps the bytes; a scan is opened only as this patient's scan;
	//   · the consents on file, read-only: the form and its version, the decision, who signed and as what, when;
	//   · a message to the patient from the visit, through the SMS outbox, and the log of every message sent.
	// ==========================================================================================

	public sealed class HospitalContactInput
	{
		public string? Channel { get; set; }
		public string? Language { get; set; }
		public string? BestTime { get; set; }
		public List<string>? Needs { get; set; }
		public string? InterpreterLanguage { get; set; }
		public string? NeedsNote { get; set; }
	}

	public sealed class HospitalRepresentativeInput
	{
		public string? Authority { get; set; }
		public string? Name { get; set; }
		public string? Relation { get; set; }
		public string? Phone { get; set; }
		public string? NationalId { get; set; }
		public DateTime? ValidTo { get; set; }
	}

	public static class HospitalScanKind
	{
		public const string IdCard = "id-card", InsuranceCard = "insurance-card", Other = "other";
		public static readonly IReadOnlyList<string> All = new[] { IdCard, InsuranceCard, Other };
		public static string Metadata(string kind) => "{\"hospital\":\"desk-scan\",\"kind\":\"" + kind + "\"}";
		public const string MetadataPrefix = "{\"hospital\":\"desk-scan\"";
		public static string KindOf(string? metadata) => All.FirstOrDefault(k => metadata == Metadata(k)) ?? Other;
	}

	public sealed record HospitalScanRow(long DocumentId, string Kind, string FileName, DateTime At, int Versions);
	public sealed record HospitalConsentLine(int Id, string ConsentNo, string Title, int Version, string Decision, string SignerKind, string SignerName, string? SignerRelation, DateTime SignedAt, DateTime? WithdrawnAt);
	public sealed record HospitalMessageLine(string MessageNo, string Kind, string Status, string? Error, string Body, DateTime QueuedAt, DateTime? SentAt, string? SourceType);
	public sealed record HospitalContactView(HospitalPatient Patient, HospitalContactPreference? Pref, bool OptedOut, IReadOnlyList<HospitalPatientRepresentative> Representatives,
		IReadOnlyList<HospitalScanRow> Scans, IReadOnlyList<HospitalConsentLine> Consents, IReadOnlyList<HospitalMessageLine> Messages);

	public static class HospitalPatientContactRules
	{
		public const long MaxScanBytes = 10 * 1024 * 1024;

		public static string? Refuse(HospitalContactInput i)
		{
			if (!HospitalContactChannel.All.Contains(i.Channel ?? "")) return "Choose how to reach the patient";
			if (i.Language is not ("ar" or "en")) return "Choose the patient's language";
			if (!HospitalBestTime.All.Contains(i.BestTime ?? "")) return "Choose the best time to reach the patient";
			if ((i.Needs ?? new()).Any(n => !HospitalAssistanceNeed.All.Contains(n))) return "Choose the help the patient needs from the list";
			if ((i.Needs ?? new()).Contains(HospitalAssistanceNeed.Interpreter) && string.IsNullOrWhiteSpace(i.InterpreterLanguage)) return "Write the language the interpreter must speak";
			if ((i.Needs ?? new()).Contains(HospitalAssistanceNeed.Other) && string.IsNullOrWhiteSpace(i.NeedsNote)) return "Write what other help the patient needs";
			return null;
		}

		public static string? Refuse(HospitalRepresentativeInput i, DateTime todayLocal)
		{
			if (!HospitalRepresentativeAuthority.All.Contains(i.Authority ?? "")) return "Choose what the representative is to the patient";
			if (string.IsNullOrWhiteSpace(i.Name)) return "Write the representative's name";
			if (string.IsNullOrWhiteSpace(i.Phone)) return "Write the representative's phone";
			if (i.Authority is HospitalRepresentativeAuthority.Guardian or HospitalRepresentativeAuthority.Attorney && string.IsNullOrWhiteSpace(i.NationalId))
				return "A guardian or an attorney needs an ID number";
			if (i.ValidTo is DateTime v && v.Date < todayLocal.Date) return "The end date cannot be in the past";
			return null;
		}

		/// The file's own first bytes: an image (JPEG, PNG) or a PDF; never its name.
		public static (string Ext, string ContentType)? Sniff(ReadOnlySpan<byte> h)
		{
			if (h.Length >= 4 && h[0] == 0x25 && h[1] == 0x50 && h[2] == 0x44 && h[3] == 0x46) return (".pdf", "application/pdf");
			return HospitalPatientPhotoRules.Sniff(h);
		}

		public static IReadOnlyList<string> NeedsOf(string? raw) => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		/// A representative who may act now: active and not past their end date.
		public static bool Current(HospitalPatientRepresentative r, DateTime todayLocal) => r.IsActive && (r.ValidTo == null || r.ValidTo.Value.Date >= todayLocal.Date);
	}

	public sealed class HospitalPatientContactService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IPlatformDocumentService? _docs;
		public HospitalPatientContactService(CrossDbContext db, IHospitalAccessService access, IPlatformDocumentService? docs = null) { _db = db; _access = access; _docs = docs; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
		private Task<bool> Mine(BusinessContext ctx, int patientId, CancellationToken ct) => _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct);

		public async Task<HospitalContactView?> ViewAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == cid, ct);
			if (p == null) return null;
			HospitalContactPreference? pref = null; var reps = new List<HospitalPatientRepresentative>();
			try
			{
				pref = await _db.Set<HospitalContactPreference>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.PatientId == p.ID, ct);
				reps = await _db.Set<HospitalPatientRepresentative>().AsNoTracking().Where(x => x.CompanyId == cid && x.PatientId == p.ID).OrderByDescending(x => x.IsActive).ThenByDescending(x => x.ID).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			bool optedOut = false; var msgs = new List<HospitalMessageLine>();
			try
			{
				optedOut = await _db.Set<HospitalSmsOptOut>().AnyAsync(o => o.CompanyId == cid && o.PatientId == p.ID, ct);
				msgs = await _db.Set<HospitalSmsMessage>().AsNoTracking().Where(m => m.CompanyId == cid && m.PatientId == p.ID).OrderByDescending(m => m.ID).Take(15)
					.Select(m => new HospitalMessageLine(m.MessageNo, m.Kind, m.Status, m.Error, m.Body, m.QueuedAt, m.SentAt, m.SourceType)).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var scans = new List<HospitalScanRow>();
			try
			{
				var docs = await _db.Set<PlatformDocument>().AsNoTracking()
					.Where(d => d.CompanyID == cid && d.EntityType == EntityRegistry.HospitalPatient && d.EntityId == p.ID && d.Status == "Active" && d.Metadata != null && d.Metadata.StartsWith(HospitalScanKind.MetadataPrefix))
					.OrderByDescending(d => d.Id).Select(d => new { d.Id, d.Metadata, d.CreatedAt, d.CurrentVersionId }).ToListAsync(ct);
				var vids = docs.Where(d => d.CurrentVersionId != null).Select(d => d.CurrentVersionId!.Value).ToList(); var dids = docs.Select(d => d.Id).ToList();
				var names = await _db.Set<PlatformDocumentVersion>().AsNoTracking().Where(v => v.CompanyID == cid && vids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.FileName, ct);
				var counts = await _db.Set<PlatformDocumentVersion>().AsNoTracking().Where(v => v.CompanyID == cid && dids.Contains(v.DocumentId)).GroupBy(v => v.DocumentId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				scans = docs.Select(d => new HospitalScanRow(d.Id, HospitalScanKind.KindOf(d.Metadata), d.CurrentVersionId is long v ? names.GetValueOrDefault(v) ?? "" : "", d.CreatedAt, counts.GetValueOrDefault(d.Id))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var consents = new List<HospitalConsentLine>();
			try
			{
				consents = await _db.Set<HospitalConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.PatientId == p.ID).OrderByDescending(c => c.SignedAt)
					.Select(c => new HospitalConsentLine(c.ID, c.ConsentNo, c.Title, c.FormVersion, c.Decision, c.SignerKind, c.SignerName, c.SignerRelation, c.SignedAt, c.WithdrawnAt)).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return new HospitalContactView(p, pref, optedOut, reps, scans, consents, msgs);
		}

		public async Task<HospitalResult> SavePreferenceAsync(BusinessContext ctx, int patientId, HospitalContactInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalPatientContactRules.Refuse(input);
			if (why != null) return HospitalResult.Fail(why);
			if (!await Mine(ctx, patientId, ct)) return HospitalResult.Fail("not_found");
			var row = await _db.Set<HospitalContactPreference>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PatientId == patientId, ct);
			if (row == null) { row = new HospitalContactPreference { CompanyId = ctx.CompanyId, PatientId = patientId }; _db.Add(row); }
			var needs = (input.Needs ?? new()).Distinct().ToList();
			row.Channel = input.Channel!; row.Language = input.Language!; row.BestTime = input.BestTime!;
			row.Needs = needs.Count == 0 ? null : string.Join(",", HospitalAssistanceNeed.All.Where(needs.Contains));
			row.InterpreterLanguage = needs.Contains(HospitalAssistanceNeed.Interpreter) ? Clip(input.InterpreterLanguage, 40) : null;
			row.NeedsNote = Clip(input.NeedsNote, 200); row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("The change could not be saved"); }   // a twin row at the same moment
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> AddRepresentativeAsync(BusinessContext ctx, int patientId, HospitalRepresentativeInput input, int? employeeId, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalPatientContactRules.Refuse(input, todayLocal);
			if (why != null) return HospitalResult.Fail(why);
			if (!await Mine(ctx, patientId, ct)) return HospitalResult.Fail("not_found");
			var row = new HospitalPatientRepresentative
			{
				CompanyId = ctx.CompanyId, PatientId = patientId, Authority = input.Authority!, Name = Clip(input.Name, 120)!, Relation = Clip(input.Relation, 60), Phone = Clip(input.Phone, 30),
				NationalId = Clip(input.NationalId, 30), ValidTo = input.ValidTo?.Date, IsActive = true, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> StopRepresentativeAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why the representative is stopped");
			var r = await _db.Set<HospitalPatientRepresentative>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (!r.IsActive) return HospitalResult.Fail("This representative is stopped already");
			r.IsActive = false; r.StoppedAt = nowUtc; r.StoppedByEmployeeId = employeeId; r.StopReason = Clip(reason, 200);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		/// A scan of a card, as a document on the patient's record (the platform authorises `register` on the patient).
		public async Task<HospitalResult> UploadScanAsync(BusinessContext ctx, int patientId, string? kind, Stream content, long length, CancellationToken ct = default)
		{
			if (_docs == null) return HospitalResult.Fail("The change could not be saved");
			if (!HospitalScanKind.All.Contains(kind ?? "")) return HospitalResult.Fail("Choose what the scan is");
			const string bad = "The scan must be an image or a PDF of up to 10 MB";
			if (length <= 0 || length > HospitalPatientContactRules.MaxScanBytes) return HospitalResult.Fail(bad);
			if (!await Mine(ctx, patientId, ct)) return HospitalResult.Fail("not_found");
			using var buffer = new MemoryStream();
			await content.CopyToAsync(buffer, ct);
			var k = HospitalPatientContactRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)Math.Min(12, buffer.Length)));
			if (k is not { } t) return HospitalResult.Fail(bad);
			buffer.Position = 0;
			var r = await _docs.UploadAsync(new DocumentUploadRequest
			{
				EntityType = EntityRegistry.HospitalPatient, EntityId = patientId, FileName = kind + "-" + patientId + t.Ext, ContentType = t.ContentType,
				Metadata = HospitalScanKind.Metadata(kind!), Reason = "Desk scan: " + kind,
			}, buffer, ct);
			if (!r.Ok) return HospitalResult.Fail(r.ReasonCode == DocumentAccessReasons.Allowed ? "The change could not be saved" : "forbidden");
			return HospitalResult.Success((int)r.DocumentId);
		}

		/// A scan's current bytes — only when the document is THIS patient's desk scan (an id in a URL never reaches another file).
		public async Task<DocumentContent?> OpenScanAsync(BusinessContext ctx, int patientId, long documentId, CancellationToken ct = default)
		{
			if (_docs == null) return null;
			bool mine = await _db.Set<PlatformDocument>().AsNoTracking().AnyAsync(d => d.Id == documentId && d.CompanyID == ctx.CompanyId && d.EntityType == EntityRegistry.HospitalPatient
				&& d.EntityId == patientId && d.Metadata != null && d.Metadata.StartsWith(HospitalScanKind.MetadataPrefix), ct);
			return mine ? await _docs.OpenCurrentAsync(documentId, ct) : null;
		}

		/// A message to the patient from the desk (the visit it is about, when there is one), through the SMS outbox.
		public async Task<HospitalResult> SendMessageAsync(BusinessContext ctx, int patientId, int? appointmentId, string? body, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(body)) return HospitalResult.Fail("Write the message");
			if (body.Trim().Length > 640) return HospitalResult.Fail("The message is too long");
			if (!await Mine(ctx, patientId, ct)) return HospitalResult.Fail("not_found");
			if (await _db.Set<HospitalSmsOptOut>().AnyAsync(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId, ct)) return HospitalResult.Fail("The patient asked for no messages");
			if (appointmentId is int aid && !await _db.HospitalAppointments.AnyAsync(a => a.ID == aid && a.CompanyId == ctx.CompanyId && a.PatientId == patientId, ct)) return HospitalResult.Fail("not_found");
			var id = await HospitalSmsService.QueueTextAsync(_db, ctx.CompanyId, patientId, body.Trim(), appointmentId != null ? "Appointment" : "Desk", appointmentId ?? patientId,
				$"desk:{patientId}:{nowUtc.Ticks}", employeeId, nowUtc, ct);
			return id is int m ? HospitalResult.Success(m) : HospitalResult.Fail("The change could not be saved");
		}

		/// For the card: the help needed and the representatives who may act now.
		public async Task<(IReadOnlyList<string> Needs, string? Interpreter, IReadOnlyList<HospitalPatientRepresentative> Representatives)> CardAsync(BusinessContext ctx, int patientId, DateTime todayLocal, CancellationToken ct = default)
		{
			try
			{
				var pref = await _db.Set<HospitalContactPreference>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PatientId == patientId, ct);
				var reps = (await _db.Set<HospitalPatientRepresentative>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == patientId && x.IsActive).ToListAsync(ct))
					.Where(r => HospitalPatientContactRules.Current(r, todayLocal)).ToList();
				return (HospitalPatientContactRules.NeedsOf(pref?.Needs), pref?.InterpreterLanguage, reps);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<string>(), null, Array.Empty<HospitalPatientRepresentative>()); }
		}
	}
}
