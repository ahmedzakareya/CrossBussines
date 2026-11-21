using System.Net.Http.Json;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalMohKind { public const string SickLeave = "SickLeave"; public const string Disease = "Disease"; }

	public class HospitalMohSubmission
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = HospitalMohKind.SickLeave;
		public int RecordId { get; set; }
		public int PatientId { get; set; }
		public string Status { get; set; } = "Failed";
		public int? HttpStatus { get; set; }
		public string? Reference { get; set; }
		public string? Error { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}
}

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H225 — the Ministry of Health gateway. When the hospital is connected (Hospital:MohGateway:Url and
	// Hospital:MohGateway:Key — the Ministry's endpoint, or the integrator's in front of it), a sick leave certificate or a
	// notifiable case is SENT from its screen instead of typed: the record goes as JSON, and the reference that comes back
	// is recorded through the SAME path as one typed by hand (its rules: once, never on a void certificate / closed case).
	// Every attempt is kept (HospitalMohSubmission) with what came back or why it failed. Not connected → the screens say
	// so and the reference is recorded by hand, as before. Nothing of the illness goes but what the Ministry needs: a sick
	// leave sends no diagnosis unless the doctor chose to print it.
	// ==========================================================================================
	public static class HospitalMohGatewayModel
	{
		public static void Configure(Microsoft.EntityFrameworkCore.ModelBuilder b)
		{
			b.Entity<HospitalMohSubmission>(e =>
			{
				e.ToTable("HospitalMohSubmissions");
				e.Property(x => x.Kind).HasMaxLength(12);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Reference).HasMaxLength(60);
				e.Property(x => x.Error).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Kind, x.RecordId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}

	public static class HospitalMohGatewayRules
	{
		/// The reference in the Ministry's answer: {"reference": "..."} (or "referenceNo" / "id"); null when none.
		public static string? ReferenceIn(string? body)
		{
			if (string.IsNullOrWhiteSpace(body)) return null;
			try
			{
				using var doc = JsonDocument.Parse(body);
				foreach (var name in new[] { "reference", "referenceNo", "reference_no", "id" })
					if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v))
					{
						var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
						if (!string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 60) return s.Trim();
					}
			}
			catch (JsonException) { }
			return null;
		}
	}

	public sealed class HospitalMohGatewayService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHttpClientFactory? _http;
		private readonly Microsoft.Extensions.Configuration.IConfiguration? _config;
		public HospitalMohGatewayService(CrossDbContext db, IHospitalAccessService access, IHttpClientFactory? http = null, Microsoft.Extensions.Configuration.IConfiguration? config = null)
		{ _db = db; _access = access; _http = http; _config = config; }

		public bool Connected => !string.IsNullOrWhiteSpace(_config?["Hospital:MohGateway:Url"]);

		public async Task<IReadOnlyList<HospitalMohSubmission>> HistoryAsync(BusinessContext ctx, string kind, IReadOnlyCollection<int> recordIds, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalMohSubmission>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Kind == kind && recordIds.Contains(s.RecordId)).OrderByDescending(s => s.At).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalMohSubmission>(); }
		}

		/// The certificate sent; the reference recorded through the sick leave's own rule.
		public async Task<HospitalResult> SendSickLeaveAsync(BusinessContext ctx, HospitalSickLeaveService leaves, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct)) return HospitalResult.Fail("forbidden");
			var l = await _db.Set<HospitalSickLeave>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			if (HospitalSickLeaveRules.MohRefusal(l, "pending") is string why) return HospitalResult.Fail(why);
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == l.PatientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.Name, x.NameEn, x.NationalId, x.BirthDate, x.Sex }).FirstAsync(ct);
			var doc = l.PractitionerId is int pid ? await _db.HospitalPractitioners.AsNoTracking().Where(x => x.ID == pid && x.CompanyId == ctx.CompanyId).Select(x => new { x.Name, x.NameEn, x.LicenseNumber }).FirstOrDefaultAsync(ct) : null;
			var payload = new
			{
				type = "SickLeave", certificateNo = l.LeaveNo, verifyCode = l.VerifyCode, issuedAt = l.IssuedAt, from = l.FromDate.ToString("yyyy-MM-dd"), to = l.ToDate.ToString("yyyy-MM-dd"), days = l.Days,
				recommendation = l.Kind, diagnosis = l.ShowDiagnosis ? l.Diagnosis : null,
				patient = new { civilId = p.NationalId, name = p.Name, nameEn = p.NameEn, birthDate = p.BirthDate?.ToString("yyyy-MM-dd"), sex = p.Sex },
				doctor = doc == null ? null : new { name = doc.Name, nameEn = doc.NameEn, licence = doc.LicenseNumber },
			};
			var (ok, reference, error, status) = await PostAsync("sick-leaves", payload, ct);
			await LogAsync(ctx, HospitalMohKind.SickLeave, l.ID, l.PatientId, ok, status, reference, error, employeeId, nowUtc, ct);
			if (!ok) return HospitalResult.Fail(error ?? "The Ministry's system did not answer");
			return await leaves.MohAuthAsync(ctx, l.ID, reference, employeeId, nowUtc, ct);
		}

		/// The notifiable case sent; the reference closes it through the case's own rule.
		public async Task<HospitalResult> SendDiseaseAsync(BusinessContext ctx, HospitalNotifiableService cases, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await _db.Set<HospitalDiseaseReport>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalDiseaseReportStatus.Due) return HospitalResult.Fail("This case is closed");
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == r.PatientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.Name, x.NameEn, x.NationalId, x.BirthDate, x.Sex, x.Phone }).FirstAsync(ct);
			var d = await _db.Set<HospitalNotifiableDisease>().AsNoTracking().Where(x => x.ID == r.DiseaseId && x.CompanyId == ctx.CompanyId).Select(x => new { x.CodePrefix, x.Name, x.NameEn, x.Urgency }).FirstOrDefaultAsync(ct);
			var doc = r.PractitionerId is int pid ? await _db.HospitalPractitioners.AsNoTracking().Where(x => x.ID == pid && x.CompanyId == ctx.CompanyId).Select(x => new { x.Name, x.NameEn, x.LicenseNumber }).FirstOrDefaultAsync(ct) : null;
			var payload = new
			{
				type = "NotifiableDisease", caseNo = r.ReportNo, icd10 = r.Code, diagnosis = r.DiagnosisNameEn ?? r.DiagnosisName, disease = d?.NameEn ?? d?.Name, group = d?.Urgency, diagnosedAt = r.DiagnosedAt,
				patient = new { civilId = p.NationalId, name = p.Name, nameEn = p.NameEn, birthDate = p.BirthDate?.ToString("yyyy-MM-dd"), sex = p.Sex, phone = p.Phone },
				doctor = doc == null ? null : new { name = doc.Name, nameEn = doc.NameEn, licence = doc.LicenseNumber },
			};
			var (ok, reference, error, status) = await PostAsync("notifiable-diseases", payload, ct);
			await LogAsync(ctx, HospitalMohKind.Disease, r.ID, r.PatientId, ok, status, reference, error, employeeId, nowUtc, ct);
			if (!ok) return HospitalResult.Fail(error ?? "The Ministry's system did not answer");
			return await cases.CloseAsync(ctx, r.ID, HospitalDiseaseReportStatus.Reported, reference, employeeId, nowUtc, ct);
		}

		private async Task<(bool Ok, string? Reference, string? Error, int? Status)> PostAsync(string path, object payload, CancellationToken ct)
		{
			if (!Connected || _http == null) return (false, null, "The hospital is not connected to the Ministry's system: record the reference by hand", null);
			try
			{
				var client = _http.CreateClient("hospital-moh");
				client.Timeout = TimeSpan.FromSeconds(30);
				using var req = new HttpRequestMessage(HttpMethod.Post, _config!["Hospital:MohGateway:Url"]!.TrimEnd('/') + "/" + path) { Content = JsonContent.Create(payload) };
				if (_config["Hospital:MohGateway:Key"] is { Length: > 0 } key) req.Headers.TryAddWithoutValidation("X-Api-Key", key);
				using var res = await client.SendAsync(req, ct);
				var body = await res.Content.ReadAsStringAsync(ct);
				if (!res.IsSuccessStatusCode) return (false, null, "The Ministry's system refused the record", (int)res.StatusCode);
				var reference = HospitalMohGatewayRules.ReferenceIn(body);
				return reference == null ? (false, null, "The Ministry's system answered without a reference", (int)res.StatusCode) : (true, reference, null, (int)res.StatusCode);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
			{ return (false, null, "The Ministry's system could not be reached", null); }
		}

		private async Task LogAsync(BusinessContext ctx, string kind, int recordId, int patientId, bool ok, int? status, string? reference, string? error, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				_db.Add(new HospitalMohSubmission { CompanyId = ctx.CompanyId, Kind = kind, RecordId = recordId, PatientId = patientId, Status = ok ? "Accepted" : "Failed", HttpStatus = status, Reference = reference, Error = error, At = nowUtc, ByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); }
		}
	}
}
