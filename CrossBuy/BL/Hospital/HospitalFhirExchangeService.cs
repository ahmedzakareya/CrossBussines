using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalGatewayInput { public string? BaseUrl { get; set; } public string? KeyHeader { get; set; } public string? ApiKey { get; set; } public bool Enabled { get; set; } }

	public sealed record HospitalFhirMessageRow(HospitalFhirMessage Message, string PatientName, string? PatientNameEn, string? Photo, string? Mrn, string Payer, string PayerEn, string? PreauthNo);
	public sealed record HospitalPayerGatewayRow(HospitalPayer Payer, HospitalPayerGateway? Gateway);
	public sealed record HospitalFhirPage(bool Ready, IReadOnlyList<HospitalFhirMessageRow> Rows, int Total, IReadOnlyList<HospitalPayerGatewayRow> Payers);

	// ==========================================================================================
	// HOSPITAL H242 — eligibility checks and pre-authorisation requests as FHIR messages. The biller (`bill`) checks
	// and requests; the administrator (`manage`) enters a payer's gateway. With an enabled gateway the message is sent
	// and its answer read at once; otherwise the file is kept for download and the payer's answer file is loaded back.
	// A pre-authorisation answer goes through HospitalInsuranceService.DecidePreauthAsync — the same checks as by hand.
	// ==========================================================================================
	public sealed class HospitalFhirExchangeService
	{
		public const string NoTables = "FHIR eligibility is not installed yet (hospital_h242_fhir_eligibility.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalInsuranceService _insurance;
		private readonly HospitalPricingService? _pricing;
		public HospitalFhirExchangeService(CrossDbContext db, IHospitalAccessService access, HospitalInsuranceService insurance, HospitalPricingService? pricing = null)
		{ _db = db; _access = access; _insurance = insurance; _pricing = pricing; }

		/// Test seam: how a message reaches the gateway (url, header, key, json) → (status, body). Null = HTTP.
		public Func<string, string?, string?, string, CancellationToken, Task<(int Status, string Body)>>? Transport { get; set; }

		private Task<bool> Bill(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Bill, null, ct);

		public async Task<HospitalFhirPage?> PageAsync(BusinessContext ctx, string? q, string? kind, int page, int size, CancellationToken ct = default)
		{
			if (!await Bill(ctx, ct)) return null;
			var payers = await _db.Set<HospitalPayer>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).OrderBy(p => p.Code).ToListAsync(ct);
			try
			{
				var gws = await _db.Set<HospitalPayerGateway>().AsNoTracking().Where(g => g.CompanyId == ctx.CompanyId).ToDictionaryAsync(g => g.PayerId, ct);
				var query = from m in _db.Set<HospitalFhirMessage>().AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on m.PatientId equals p.ID
							join py in _db.Set<HospitalPayer>().AsNoTracking() on m.PayerId equals py.ID
							where m.CompanyId == ctx.CompanyId
							select new { m, p.Name, p.NameEn, p.PhotoPath, p.Mrn, Payer = py.Name, PayerEn = py.NameEn };
				if (HospitalFhirMessageKind.All.Contains(kind ?? "")) query = query.Where(x => x.m.Kind == kind);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					query = query.Where(x => x.Name.Contains(t) || (x.NameEn ?? "").Contains(t) || x.Mrn.Contains(t) || x.Payer.Contains(t) || x.PayerEn.Contains(t));
				}
				int total = await query.CountAsync(ct);
				var rows = await query.OrderByDescending(x => x.m.At).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var paIds = rows.Where(r => r.m.PreauthId != null).Select(r => r.m.PreauthId!.Value).ToList();
				var paNo = await _db.Set<HospitalPreauth>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && paIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.RequestNo, ct);
				return new HospitalFhirPage(true, rows.Select(r => new HospitalFhirMessageRow(r.m, r.Name, r.NameEn, r.PhotoPath, r.Mrn, r.Payer, r.PayerEn, r.m.PreauthId is int i ? paNo.GetValueOrDefault(i) : null)).ToList(),
					total, payers.Select(p => new HospitalPayerGatewayRow(p, gws.GetValueOrDefault(p.ID))).ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return new HospitalFhirPage(false, Array.Empty<HospitalFhirMessageRow>(), 0, payers.Select(p => new HospitalPayerGatewayRow(p, null)).ToList());
			}
		}

		public async Task<HospitalResult> SaveGatewayAsync(BusinessContext ctx, int payerId, HospitalGatewayInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Set<HospitalPayer>().AnyAsync(p => p.ID == payerId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var g = await _db.Set<HospitalPayerGateway>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PayerId == payerId, ct);
				// an empty key keeps the one already entered: the key is never shown back
				var key = string.IsNullOrWhiteSpace(i.ApiKey) ? g?.ApiKey : i.ApiKey.Trim();
				if (HospitalFhirEligibility.GatewayRefusal(i.BaseUrl, i.KeyHeader, key) is string why) return HospitalResult.Fail(why);
				if (g == null) _db.Add(g = new HospitalPayerGateway { CompanyId = ctx.CompanyId, PayerId = payerId });
				g.BaseUrl = i.BaseUrl!.Trim().TrimEnd('/'); g.KeyHeader = string.IsNullOrWhiteSpace(i.KeyHeader) ? null : i.KeyHeader.Trim(); g.ApiKey = key;
				g.Enabled = i.Enabled; g.UpdatedAt = nowUtc; g.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(g.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		private async Task<(int, string)> PostAsync(HospitalPayerGateway g, string resource, string json, CancellationToken ct)
		{
			var url = g.BaseUrl + "/" + resource;
			if (Transport != null) return await Transport(url, g.KeyHeader, g.ApiKey, json, ct);
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/fhir+json") };
			if (g.KeyHeader != null && g.ApiKey != null) req.Headers.TryAddWithoutValidation(g.KeyHeader, g.ApiKey);
			req.Headers.TryAddWithoutValidation("Accept", "application/fhir+json");
			using var res = await http.SendAsync(req, ct);
			return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
		}

		private async Task<HospitalPayerGateway?> GatewayAsync(BusinessContext ctx, int payerId, CancellationToken ct) =>
			await _db.Set<HospitalPayerGateway>().AsNoTracking().FirstOrDefaultAsync(g => g.CompanyId == ctx.CompanyId && g.PayerId == payerId && g.Enabled, ct);

		/// Builds the eligibility request for the patient's active policy; sends it when the payer's gateway is enabled.
		public async Task<HospitalResult> CheckEligibilityAsync(BusinessContext ctx, int patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Bill(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			var (policy, payer, _) = await _insurance.EligibilityAsync(ctx, patientId, nowUtc, null, ct);
			if (policy == null || payer == null) return HospitalResult.Fail("The patient has no insurance policy");
			try
			{
				var m = new HospitalFhirMessage { CompanyId = ctx.CompanyId, Kind = HospitalFhirMessageKind.Eligibility, PatientId = p.ID, PayerId = payer.ID, At = nowUtc, ByEmployeeId = employeeId, Request = "" };
				_db.Add(m); await _db.SaveChangesAsync(ct);
				m.Request = HospitalFhirEligibility.EligibilityRequest(m.ID, p, policy, payer, ctx.CompanyId, nowUtc);
				await _db.SaveChangesAsync(ct);
				var g = await GatewayAsync(ctx, payer.ID, ct);
				if (g != null) await SendAndReadAsync(ctx, m, g, "CoverageEligibilityRequest", nowUtc, ct);
				return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Builds the pre-authorisation request for a waiting request; sends it when the payer's gateway is enabled.
		public async Task<HospitalResult> RequestPreauthAsync(BusinessContext ctx, int preauthId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Bill(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.Set<HospitalPreauth>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == preauthId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalPreauthStatus.Pending) return HospitalResult.Fail("The payer already answered this request");
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == a.PatientId, ct);
			var policy = await _db.Set<HospitalPatientPolicy>().AsNoTracking().FirstAsync(x => x.ID == a.PolicyId, ct);
			var payer = await _db.Set<HospitalPayer>().AsNoTracking().FirstAsync(x => x.ID == a.PayerId, ct);
			var s = await _db.HospitalServices.AsNoTracking().FirstAsync(x => x.ID == a.ServiceId, ct);
			string currency = _pricing == null ? "KWD" : (await _pricing.CurrencyOfAsync(ctx, null, ct)).Code ?? "KWD";
			try
			{
				var m = new HospitalFhirMessage
				{
					CompanyId = ctx.CompanyId, Kind = HospitalFhirMessageKind.Preauth, PatientId = p.ID, PayerId = payer.ID, PreauthId = a.ID, At = nowUtc, ByEmployeeId = employeeId,
					Request = HospitalFhirEligibility.PreauthClaim(a, p, policy, payer, s.Code, string.IsNullOrWhiteSpace(s.NameEn) ? s.Name : s.NameEn, currency, ctx.CompanyId),
				};
				_db.Add(m); await _db.SaveChangesAsync(ct);
				var g = await GatewayAsync(ctx, payer.ID, ct);
				if (g != null) await SendAndReadAsync(ctx, m, g, "Claim", nowUtc, ct);
				return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		private async Task SendAndReadAsync(BusinessContext ctx, HospitalFhirMessage m, HospitalPayerGateway g, string resource, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var (status, body) = await PostAsync(g, resource, m.Request, ct);
				m.Sent = true;
				if (status is < 200 or >= 300)
				{
					m.Outcome = "Error"; m.Detail = Clip("HTTP " + status + (string.IsNullOrWhiteSpace(body) ? "" : ": " + body), 500); m.AnsweredAt = nowUtc;
					await _db.SaveChangesAsync(ct); return;
				}
				await ApplyAsync(ctx, m, body, null, nowUtc, ct);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
			{
				m.Sent = false; m.Outcome = "Error"; m.Detail = Clip("The gateway did not answer: " + ex.Message, 500); m.AnsweredAt = nowUtc;
				await _db.SaveChangesAsync(ct);
			}
		}

		/// Loads the payer's answer file for a message kept for download.
		public async Task<HospitalResult> LoadAnswerAsync(BusinessContext ctx, int messageId, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Bill(ctx, ct)) return HospitalResult.Fail("forbidden");
			var m = await _db.Set<HospitalFhirMessage>().FirstOrDefaultAsync(x => x.ID == messageId && x.CompanyId == ctx.CompanyId, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			if (m.Outcome is "InForce" or "NotInForce" or "Approved" or "Rejected") return HospitalResult.Fail("This message is already answered");
			return await ApplyAsync(ctx, m, raw, employeeId, nowUtc, ct);
		}

		private async Task<HospitalResult> ApplyAsync(BusinessContext ctx, HospitalFhirMessage m, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			if (m.Kind == HospitalFhirMessageKind.Eligibility)
			{
				var (ans, why) = HospitalFhirEligibility.ParseEligibility(raw);
				if (ans == null) return HospitalResult.Fail(why!);
				m.Response = raw; m.AnsweredAt = nowUtc;
				m.Outcome = ans.Error ? "Error" : ans.InForce == true ? "InForce" : ans.InForce == false ? "NotInForce" : "Pending";
				m.Detail = Clip(ans.Detail, 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(m.ID);
			}
			var (pa, why2) = HospitalFhirEligibility.ParsePreauth(raw);
			if (pa == null) return HospitalResult.Fail(why2!);
			if (HospitalFhirEligibility.PreauthIdOf(pa.Request) is int named && named != m.PreauthId) return HospitalResult.Fail("This answer is for another request");
			m.Response = raw; m.AnsweredAt = nowUtc;
			if (pa.Error || pa.Approved == null)
			{
				m.Outcome = pa.Error ? "Error" : "Pending"; m.Detail = Clip(pa.Reason, 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(m.ID);
			}
			var decided = await _insurance.DecidePreauthAsync(ctx, m.PreauthId!.Value, pa.Approved.Value, pa.ApprovalRef, pa.Reason, employeeId ?? m.ByEmployeeId, nowUtc, ct);
			m.Outcome = pa.Approved.Value ? "Approved" : "Rejected";
			m.Detail = Clip(decided.Ok ? (pa.Approved.Value ? pa.ApprovalRef : pa.Reason) : "Not applied: " + decided.Error, 500);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(m.ID);
		}

		public async Task<HospitalFhirMessage?> MessageAsync(BusinessContext ctx, int id, CancellationToken ct = default) =>
			await Bill(ctx, ct) ? await _db.Set<HospitalFhirMessage>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct) : null;

		private static string? Clip(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..n];
	}
}
