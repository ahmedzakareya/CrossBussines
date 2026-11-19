using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H217 — endoscopy. Two things a unit is inspected on, kept together:
	//   · SCOPE REPROCESSING (AAMI ST91 / SGNA): after every use a scope is leak-tested, cleaned by hand and given
	//     high-level disinfection (agent and minutes); a cycle passes only with all three. A scope may be used on a
	//     patient only when its LAST cycle passed, came AFTER its last use, and is less than 7 days old (hang time).
	//     The procedure records which cycle cleared the scope, so any patient can be traced to the cycle and the
	//     cycle to the patients after it.
	//   · THE PROCEDURE: started with the scope, the indication and the sedation; completed with the findings
	//     (required), interventions, complications and recommendations; for a colonoscopy the quality figures
	//     (caecum reached, withdrawal time — 6 minutes or more — and the Boston bowel preparation score 0-9).
	//     Biopsies taken open a pathology case (H216) from endoscopy, ready for the laboratory to receive.
	// ==========================================================================================
	public sealed class HospitalEndoStartInput
	{
		public int PatientId { get; set; }
		public string? Kind { get; set; }
		public int ScopeId { get; set; }
		public string? Indication { get; set; }
		public string? Sedation { get; set; }
	}

	public sealed class HospitalEndoCompleteInput
	{
		public string? Findings { get; set; }
		public string? Interventions { get; set; }
		public string? Complications { get; set; }
		public string? Recommendations { get; set; }
		public bool? CecumReached { get; set; }
		public int? WithdrawalMinutes { get; set; }
		public int? BowelPrepScore { get; set; }
		public int Biopsies { get; set; }
		public string? BiopsySites { get; set; }
	}

	public sealed class HospitalScopeCycleInput
	{
		public bool LeakTestPassed { get; set; }
		public bool ManualClean { get; set; }
		public string? Agent { get; set; }
		public int Minutes { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalScopeRow(HospitalScope Scope, HospitalScopeCycle? LastCycle, DateTime? LastUsedAt, bool Ready, string? NotReady);
	public sealed record HospitalEndoRow(HospitalEndoscopy Proc, string Patient, string PatientEn, string Mrn, string Scope, string? PathCaseNo, bool QualityMet, HospitalFace? Endoscopist = null);

	public static class HospitalEndoRules
	{
		public const int HangDays = 7, MinWithdrawal = 6;
		public const string NeverCleaned = "This scope has no reprocessing cycle: reprocess it before use";
		public const string LastFailed = "The scope's last reprocessing failed: reprocess it again";
		public const string UsedSince = "The scope was used after its last reprocessing: reprocess it before the next patient";
		public const string HangExpired = "The scope's reprocessing is older than 7 days: reprocess it before use";

		/// Why the scope may not be used now (null = ready).
		public static string? NotReady(HospitalScopeCycle? last, DateTime? lastUsedAt, DateTime nowUtc)
		{
			if (last == null) return NeverCleaned;
			if (!last.Passed) return LastFailed;
			if (lastUsedAt is DateTime u && u >= last.At) return UsedSince;
			return nowUtc > last.At.AddDays(HangDays) ? HangExpired : null;
		}

		public static bool CyclePasses(HospitalScopeCycleInput i) => i.LeakTestPassed && i.ManualClean && i.Minutes > 0 && !string.IsNullOrWhiteSpace(i.Agent);

		public static string? CycleRefusal(HospitalScopeCycleInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Agent)) return "Write the disinfectant used";
			if (i.Minutes is < 0 or > 240) return "Write the disinfection time in minutes";
			return (i.Note?.Trim().Length ?? 0) > 300 || i.Agent.Trim().Length > 60 ? "The text is too long" : null;
		}

		public static string? StartRefusal(HospitalEndoStartInput i)
		{
			if (string.IsNullOrEmpty(i.Kind) || !HospitalEndoKind.All.Contains(i.Kind)) return "Choose the procedure";
			if (string.IsNullOrEmpty(i.Sedation) || !HospitalEndoSedation.All.Contains(i.Sedation)) return "Choose the sedation";
			if (string.IsNullOrWhiteSpace(i.Indication)) return "Write the indication";
			return i.Indication.Trim().Length > 500 ? "The text is too long" : null;
		}

		public static string? CompleteRefusal(HospitalEndoscopy p, HospitalEndoCompleteInput i)
		{
			if (p.Status != HospitalEndoStatus.Started) return "This procedure is already completed";
			if (string.IsNullOrWhiteSpace(i.Findings)) return "Write the findings";
			if (i.Biopsies is < 0 or > 50) return "The biopsies must be between 0 and 50";
			if (i.Biopsies > 0 && string.IsNullOrWhiteSpace(i.BiopsySites)) return "Write where the biopsies were taken";
			if (p.Kind == HospitalEndoKind.Colonoscopy)
			{
				if (i.CecumReached == null) return "Say whether the caecum was reached";
				if (i.BowelPrepScore is not (>= 0 and <= 9)) return "Write the Boston bowel preparation score (0 to 9)";
				if (i.CecumReached == true && i.WithdrawalMinutes is not (>= 1 and <= 120)) return "Write the withdrawal time in minutes";
			}
			return (i.Findings.Trim().Length > 4000 || (i.Interventions?.Length ?? 0) > 1000 || (i.Recommendations?.Length ?? 0) > 1000 || (i.Complications?.Length ?? 0) > 500) ? "The text is too long" : null;
		}

		/// A colonoscopy met the quality standard: caecum reached, withdrawal 6 minutes or more, preparation adequate (Boston ≥ 6).
		public static bool QualityMet(HospitalEndoscopy p) =>
			p.Kind != HospitalEndoKind.Colonoscopy || (p.CecumReached == true && p.WithdrawalMinutes >= MinWithdrawal && p.BowelPrepScore >= 6);
	}

	public sealed class HospitalEndoscopyService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalEndoscopyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		private async Task<(HospitalScopeCycle? Last, DateTime? LastUsed)> StateAsync(int companyId, int scopeId, CancellationToken ct)
		{
			var last = await _db.Set<HospitalScopeCycle>().AsNoTracking().Where(c => c.CompanyId == companyId && c.ScopeId == scopeId).OrderByDescending(c => c.At).ThenByDescending(c => c.ID).FirstOrDefaultAsync(ct);
			var used = await _db.Set<HospitalEndoscopy>().AsNoTracking().Where(p => p.CompanyId == companyId && p.ScopeId == scopeId).MaxAsync(p => (DateTime?)p.StartedAt, ct);
			return (last, used);
		}

		public async Task<HospitalResult> AddScopeAsync(BusinessContext ctx, string? serial, string? kind, string? model, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(serial) || serial.Trim().Length > 40) return HospitalResult.Fail("Write the scope's serial number");
			if (string.IsNullOrEmpty(kind) || !HospitalEndoKind.All.Contains(kind)) return HospitalResult.Fail("Choose the procedure");
			var s = serial.Trim();
			if (await _db.Set<HospitalScope>().AnyAsync(x => x.CompanyId == ctx.CompanyId && x.Serial == s, ct)) return HospitalResult.Fail("This serial number is already registered");
			var sc = new HospitalScope { CompanyId = ctx.CompanyId, Serial = s, Kind = kind, Model = Clip(model, 100), IsActive = true, CreatedAt = nowUtc };
			_db.Add(sc);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(sc.ID);
		}

		public async Task<HospitalResult> CycleAsync(BusinessContext ctx, int scopeId, HospitalScopeCycleInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Set<HospitalScope>().AnyAsync(x => x.ID == scopeId && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (HospitalEndoRules.CycleRefusal(input) is string why) return HospitalResult.Fail(why);
			var c = new HospitalScopeCycle
			{
				CompanyId = ctx.CompanyId, ScopeId = scopeId, LeakTestPassed = input.LeakTestPassed, ManualClean = input.ManualClean, Agent = Clip(input.Agent, 60)!, Minutes = input.Minutes,
				Passed = HospitalEndoRules.CyclePasses(input), Note = Clip(input.Note, 300), At = nowUtc, ByEmployeeId = employeeId,
			};
			_db.Add(c);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> StartAsync(BusinessContext ctx, HospitalEndoStartInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalEndoRules.StartRefusal(input) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var scope = await _db.Set<HospitalScope>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.ScopeId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (scope == null) return HospitalResult.Fail("Choose the scope");
			var (last, used) = await StateAsync(ctx.CompanyId, scope.ID, ct);
			if (HospitalEndoRules.NotReady(last, used, nowUtc) is string notReady) return HospitalResult.Fail(notReady);
			var nos = await _db.Set<HospitalEndoscopy>().Where(p => p.CompanyId == ctx.CompanyId && p.ProcNo.StartsWith("ENDO-")).Select(p => p.ProcNo).ToListAsync(ct);
			int n = 0; foreach (var no in nos) if (int.TryParse(no.AsSpan(5), out var v) && v > n) n = v;
			var proc = new HospitalEndoscopy
			{
				CompanyId = ctx.CompanyId, ProcNo = "ENDO-" + (n + 1).ToString("000000"), PatientId = input.PatientId, Kind = input.Kind!, ScopeId = scope.ID, CycleId = last!.ID,
				Indication = Clip(input.Indication, 500)!, Sedation = input.Sedation!, Status = HospitalEndoStatus.Started, StartedAt = nowUtc, EndoscopistEmployeeId = employeeId,
			};
			_db.Add(proc);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(proc.ID);
		}

		public async Task<HospitalResult> CompleteAsync(BusinessContext ctx, HospitalPathologyService path, int id, HospitalEndoCompleteInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.Set<HospitalEndoscopy>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (HospitalEndoRules.CompleteRefusal(p, input) is string why) return HospitalResult.Fail(why);
			p.Findings = Clip(input.Findings, 4000); p.Interventions = Clip(input.Interventions, 1000); p.Complications = Clip(input.Complications, 500); p.Recommendations = Clip(input.Recommendations, 1000);
			bool colon = p.Kind == HospitalEndoKind.Colonoscopy;
			p.CecumReached = colon ? input.CecumReached : null; p.WithdrawalMinutes = colon ? input.WithdrawalMinutes : null; p.BowelPrepScore = colon ? input.BowelPrepScore : null;
			p.Biopsies = input.Biopsies; p.Status = HospitalEndoStatus.Completed; p.CompletedAt = nowUtc;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateConcurrencyException) { return HospitalResult.Fail("Someone else changed this procedure: open it again"); }
			if (input.Biopsies > 0)
			{
				var r = await path.RequestAsync(ctx, new HospitalPathRequestInput
				{
					PatientId = p.PatientId, Source = HospitalPathSource.Endoscopy, SourceRefId = p.ID, Containers = input.Biopsies,
					Specimen = (p.Kind + " biopsies").Length <= 200 ? p.Kind + " biopsies" : "Endoscopic biopsies", Site = Clip(input.BiopsySites, 200),
					ClinicalHistory = Clip(p.Indication + (p.Findings == null ? "" : " — " + p.Findings), 1000),
				}, employeeId, nowUtc, ct, trusted: true);
				if (r.Ok) { p.PathCaseId = r.Id; await _db.SaveChangesAsync(ct); }
			}
			return HospitalResult.Success(p.ID);
		}

		public async Task<IReadOnlyList<HospitalScopeRow>> ScopesAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var scopes = await _db.Set<HospitalScope>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).OrderBy(x => x.Kind).ThenBy(x => x.Serial).ToListAsync(ct);
				var rows = new List<HospitalScopeRow>();
				foreach (var s in scopes)
				{
					var (last, used) = await StateAsync(ctx.CompanyId, s.ID, ct);
					var why = HospitalEndoRules.NotReady(last, used, nowUtc);
					rows.Add(new HospitalScopeRow(s, last, used, s.IsActive && why == null, why));
				}
				return rows;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalScopeRow>(); }
		}

		public async Task<(IReadOnlyList<HospitalEndoRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalEndoRow>(), 0, true);
			try
			{
				var query = from p in _db.Set<HospitalEndoscopy>().AsNoTracking()
							join pt in _db.HospitalPatients.AsNoTracking() on p.PatientId equals pt.ID
							join s in _db.Set<HospitalScope>().AsNoTracking() on p.ScopeId equals s.ID
							where p.CompanyId == ctx.CompanyId && pt.CompanyId == ctx.CompanyId
							select new { p, pt, s.Serial };
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.p.Status == status);
				if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.p.ProcNo.Contains(t) || x.pt.Name.Contains(t) || x.pt.NameEn.Contains(t) || x.pt.Mrn.Contains(t) || x.Serial.Contains(t)); }
				int total = await query.CountAsync(ct);
				var rows = await query.OrderByDescending(x => x.p.Status == HospitalEndoStatus.Started).ThenByDescending(x => x.p.ID).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var caseIds = rows.Where(x => x.p.PathCaseId != null).Select(x => x.p.PathCaseId!.Value).ToList();
				var cases = await _db.Set<HospitalPathCase>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && caseIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CaseNo, ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(x => x.p.EndoscopistEmployeeId), ct);
				return (rows.Select(x => new HospitalEndoRow(x.p, x.pt.Name, x.pt.NameEn, x.pt.Mrn, x.Serial, x.p.PathCaseId is int c ? cases.GetValueOrDefault(c) : null, HospitalEndoRules.QualityMet(x.p),
					x.p.EndoscopistEmployeeId is int e ? faces.GetValueOrDefault(e) : null)).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalEndoRow>(), 0, false); }
		}
	}
}
