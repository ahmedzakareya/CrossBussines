using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalRegistryOutreachInput { public int PatientId { get; set; } public string? Registry { get; set; } public string? Note { get; set; } }

	public sealed record HospitalRegistryRow(int PatientId, string Name, string? NameEn, string? Photo, string Mrn, int? Age, string? Sex, HospitalRegistryFacts Facts,
		IReadOnlyList<HospitalRegistryGap> Gaps, HospitalRegistryOutreach? LastOutreach, HospitalFace? OutreachBy);
	public sealed record HospitalRegistryCount(string Registry, int Members, int WithGaps);
	public sealed record HospitalRegistryPage(bool Ready, string Registry, IReadOnlyList<HospitalRegistryCount> Counts, IReadOnlyList<HospitalRegistryRow> Rows, int Total, IReadOnlyDictionary<string, int> GapCounts);

	// ==========================================================================================
	// HOSPITAL H262 — the registries read the record of every active patient (clinical read, `read`) and record the
	// outreach (`book`: the desk or a nurse calls the patient). Nothing about membership is stored.
	// ==========================================================================================
	public sealed class HospitalRegistryService
	{
		public const string NoTables = "Disease registries are not installed yet (hospital_h262_registries.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRegistryService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private async Task<Dictionary<int, HospitalRegistryFacts>> FactsAsync(int companyId, IReadOnlyDictionary<int, (DateTime? Birth, string? Sex)> people, DateTime nowUtc, CancellationToken ct)
		{
			var pids = people.Keys.ToList();
			var codes = new Dictionary<int, HashSet<string>>();
			void AddCode(int pid, string? c) { if (string.IsNullOrWhiteSpace(c)) return; if (!codes.TryGetValue(pid, out var s)) codes[pid] = s = new(StringComparer.OrdinalIgnoreCase); s.Add(c.Trim()); }
			foreach (var p in await _db.Set<HospitalProblem>().AsNoTracking().Where(x => x.CompanyId == companyId && x.Status == HospitalProblemStatus.Active && x.Code != null).Select(x => new { x.PatientId, x.Code }).ToListAsync(ct)) AddCode(p.PatientId, p.Code);
			foreach (var d in await (from x in _db.HospitalEncounterDiagnoses.AsNoTracking()
									 join e in _db.HospitalEncounters.AsNoTracking() on x.EncounterId equals e.ID
									 where e.CompanyId == companyId && e.Status == HospitalEncounterStatus.Signed
									 select new { e.PatientId, x.Code }).ToListAsync(ct)) AddCode(d.PatientId, d.Code);
			var chronic = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == companyId && p.ChronicConditions != null).ToDictionaryAsync(p => p.ID, p => p.ChronicConditions, ct);
			var labs = await (from r in _db.HospitalLabResults.AsNoTracking()
							  join o in _db.HospitalLabOrders.AsNoTracking() on r.LabOrderId equals o.ID
							  join a in _db.HospitalLabAnalytes.AsNoTracking() on r.AnalyteId equals a.ID
							  where o.CompanyId == companyId && o.VerifiedAt != null && r.Value != null && (a.Code == "HBA1C" || a.Code == "CREA" || a.Loinc == "4548-4" || a.Loinc == "2160-0")
							  select new { o.PatientId, Kind = a.Code == "HBA1C" || a.Loinc == "4548-4" ? "A1C" : "CREA", Value = r.Value!.Value, At = o.VerifiedAt!.Value }).ToListAsync(ct);
			var bps = await _db.HospitalEncounters.AsNoTracking().Where(e => e.CompanyId == companyId && e.BpSystolic != null)
				.Select(e => new { e.PatientId, e.BpSystolic, e.BpDiastolic, At = e.SignedAt ?? e.StartedAt }).ToListAsync(ct);
			var res = new Dictionary<int, HospitalRegistryFacts>();
			foreach (var pid in pids)
			{
				var a1c = labs.Where(l => l.PatientId == pid && l.Kind == "A1C").OrderByDescending(l => l.At).ToList();
				var crea = labs.Where(l => l.PatientId == pid && l.Kind == "CREA").OrderByDescending(l => l.At).FirstOrDefault();
				var bp = bps.Where(b => b.PatientId == pid).OrderByDescending(b => b.At).FirstOrDefault();
				var (birth, sex) = people[pid];
				res[pid] = new HospitalRegistryFacts(codes.TryGetValue(pid, out var cs) ? cs : new HashSet<string>(), chronic.GetValueOrDefault(pid),
					a1c.FirstOrDefault()?.Value, a1c.FirstOrDefault()?.At, a1c.Count == 0 ? null : a1c.Max(x => x.Value),
					crea?.At, crea == null ? null : HospitalRegistryRules.Egfr(crea.Value, HospitalPatientService.AgeOn(birth, crea.At), sex),
					bp?.BpSystolic, bp?.BpDiastolic, bp?.At);
			}
			return res;
		}

		public async Task<HospitalRegistryPage?> PageAsync(BusinessContext ctx, string? registry, string? gap, string? q, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct)) return null;
			registry = HospitalRegistryKind.All.Contains(registry ?? "") ? registry! : HospitalRegistryKind.Diabetes;
			var people = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.DeceasedAt == null)
				.Select(p => new { p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn, p.BirthDate, p.Sex }).ToListAsync(ct);
			var hidden = await HospitalRestrictedLists.HiddenAsync(_db, _access, ctx, ct);   // H267: a restricted file is not swept into the list
			people = people.Where(p => !hidden.Contains(p.ID)).ToList();
			var facts = await FactsAsync(ctx.CompanyId, people.ToDictionary(p => p.ID, p => ((DateTime?)p.BirthDate, (string?)p.Sex)), nowUtc, ct);
			var counts = HospitalRegistryKind.All.Select(k =>
			{
				var m = people.Where(p => HospitalRegistryRules.Member(k, facts[p.ID])).ToList();
				return new HospitalRegistryCount(k, m.Count, m.Count(p => HospitalRegistryRules.Gaps(k, facts[p.ID], nowUtc).Count > 0));
			}).ToList();
			var members = people.Where(p => HospitalRegistryRules.Member(registry, facts[p.ID]))
				.Select(p => (p, gaps: HospitalRegistryRules.Gaps(registry, facts[p.ID], nowUtc))).ToList();
			var gapCounts = members.SelectMany(m => m.gaps).GroupBy(g => g.Code).ToDictionary(g => g.Key, g => g.Count());
			if (!string.IsNullOrWhiteSpace(gap)) members = gap == "none" ? members.Where(m => m.gaps.Count == 0).ToList() : members.Where(m => m.gaps.Any(g => g.Code == gap)).ToList();
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				members = members.Where(m => m.p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (m.p.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || m.p.Mrn.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
			}
			int total = members.Count;
			var pageRows = members.OrderByDescending(m => m.gaps.Count).ThenBy(m => m.p.Name).Skip((Math.Max(1, page) - 1) * size).Take(size).ToList();
			var ids = pageRows.Select(m => m.p.ID).ToList();
			Dictionary<int, HospitalRegistryOutreach> last = new(); bool ready = true;
			try
			{
				last = (await _db.Set<HospitalRegistryOutreach>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.Registry == registry && ids.Contains(o.PatientId)).ToListAsync(ct))
					.GroupBy(o => o.PatientId).ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.At).First());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { ready = false; }
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, last.Values.Select(o => o.ByEmployeeId), ct);
			var rows = pageRows.Select(m => new HospitalRegistryRow(m.p.ID, m.p.Name, m.p.NameEn, m.p.PhotoPath, m.p.Mrn, HospitalPatientService.AgeOn(m.p.BirthDate, nowUtc), m.p.Sex, facts[m.p.ID], m.gaps,
				last.GetValueOrDefault(m.p.ID), last.GetValueOrDefault(m.p.ID)?.ByEmployeeId is int e ? faces.GetValueOrDefault(e) : null)).ToList();
			return new HospitalRegistryPage(ready, registry, counts, rows, total, gapCounts);
		}

		public async Task<HospitalResult> OutreachAsync(BusinessContext ctx, HospitalRegistryOutreachInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalRegistryRules.OutreachRefusal(i.Registry, i.Note) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == i.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var o = new HospitalRegistryOutreach { CompanyId = ctx.CompanyId, PatientId = i.PatientId, Registry = i.Registry!, Note = i.Note!.Trim()[..Math.Min(500, i.Note.Trim().Length)], At = nowUtc, ByEmployeeId = ctx.EmployeeId };
				_db.Add(o); await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(o.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
