using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalAbxRow(HospitalMedOrder Order, string PatientName, string PatientNameEn, string Mrn, string? Photo, bool Restricted,
		string State, double Hours, HospitalAbxReview? LastReview);
	public sealed record HospitalAbxUse(string Drug, int Dot, decimal? Per1000);
	public sealed record HospitalStewardshipPage(bool Ready, IReadOnlyList<HospitalAbxRow> Rows, int Total, IReadOnlyDictionary<string, int> Counts,
		IReadOnlyList<HospitalAbxUse> Use, int PatientDays, decimal? TotalPer1000);

	// ==========================================================================================
	// HOSPITAL H235 — the stewardship team's worklist: every running antimicrobial order with where it stands
	// (HospitalStewardshipRules.State), the approval of a restricted one and the 48–72 h time-out; and the usage of
	// the last 30 days in days of therapy per 1 000 patient-days. The team is the infection doctor (`consult`) and the
	// pharmacist (`pharmacy`); "Stop" stops the order through the ward's own stop (which asks `consult`).
	// ==========================================================================================
	public sealed class HospitalStewardshipService
	{
		public const string NoTables = "Stewardship is not installed yet (hospital_h235_stewardship.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalStewardshipService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private async Task<bool> TeamAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);

		private async Task<List<HospitalDrugFacts>> AntimicrobialsAsync(int companyId, CancellationToken ct) =>
			(await _db.Set<HospitalDrug>().AsNoTracking().Where(d => d.CompanyId == companyId && d.IsAntimicrobial)
				.Select(d => new { d.GenericName, d.GenericNameEn, d.Atc, d.PregnancyRisk, d.LactationRisk, d.IsAntimicrobial, d.FormularyStatus }).ToListAsync(ct))
			.Select(d => new HospitalDrugFacts(d.GenericName, d.GenericNameEn, d.Atc, d.PregnancyRisk, d.LactationRisk, d.IsAntimicrobial, d.FormularyStatus)).ToList();

		public async Task<HospitalStewardshipPage?> PageAsync(BusinessContext ctx, string? q, string? state, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await TeamAsync(ctx, ct)) return null;
			try
			{
				var abx = await AntimicrobialsAsync(ctx.CompanyId, ct);
				var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.Status == HospitalMedOrderStatus.Active).ToListAsync(ct);
				var mine = orders.Select(o => (o, f: HospitalMedSafetyRules.Find(o.Drug, abx))).Where(x => x.f != null).ToList();
				var ids = mine.Select(x => x.o.ID).ToList();
				var reviews = await _db.Set<HospitalAbxReview>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.MedOrderId)).ToListAsync(ct);
				var pids = mine.Select(x => x.o.PatientId).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToDictionaryAsync(p => p.ID, ct);
				var rows = mine.Select(x =>
				{
					var rs = reviews.Where(r => r.MedOrderId == x.o.ID).OrderBy(r => r.At).ToList();
					var approval = rs.LastOrDefault(r => r.Kind == HospitalAbxReviewKind.Approval)?.Decision;
					bool restricted = x.f!.FormularyStatus == HospitalFormularyStatus.Restricted;
					var st = HospitalStewardshipRules.State(restricted, x.o.StartAt, nowUtc, approval, rs.Any(r => r.Kind == HospitalAbxReviewKind.TimeOut));
					var p = pats.GetValueOrDefault(x.o.PatientId);
					return new HospitalAbxRow(x.o, p?.Name ?? "", p?.NameEn ?? "", p?.Mrn ?? "", p?.PhotoPath, restricted, st, Math.Round((nowUtc - x.o.StartAt).TotalHours, 1), rs.LastOrDefault());
				}).ToList();
				var counts = HospitalAbxState.All.ToDictionary(s => s, s => rows.Count(r => r.State == s));
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					rows = rows.Where(r => r.Order.Drug.Contains(t, StringComparison.OrdinalIgnoreCase) || r.PatientName.Contains(t) || r.PatientNameEn.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Mrn.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
				}
				if (HospitalAbxState.All.Contains(state ?? "")) rows = rows.Where(r => r.State == state).ToList();
				rows = rows.OrderBy(r => Array.IndexOf(HospitalAbxState.All.ToArray(), r.State)).ThenByDescending(r => r.Hours).ToList();
				var (use, days, total) = await UsageAsync(ctx.CompanyId, abx, nowUtc.ToLocalTime().Date.AddDays(-29), nowUtc.ToLocalTime().Date.AddDays(1), ct);
				return new HospitalStewardshipPage(true, rows.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), rows.Count, counts, use, days, total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return new HospitalStewardshipPage(false, Array.Empty<HospitalAbxRow>(), 0, new Dictionary<string, int>(), Array.Empty<HospitalAbxUse>(), 0, null);
			}
		}

		/// Days of therapy of each antimicrobial in a ward-time window, per 1 000 patient-days of the stays in it.
		public async Task<(IReadOnlyList<HospitalAbxUse> Use, int PatientDays, decimal? Total)> UsageAsync(int companyId, IReadOnlyList<HospitalDrugFacts> abx, DateTime fromLocal, DateTime toExclusiveLocal, CancellationToken ct)
		{
			DateTime fromUtc = DateTime.SpecifyKind(fromLocal, DateTimeKind.Local).ToUniversalTime(), toUtc = DateTime.SpecifyKind(toExclusiveLocal, DateTimeKind.Local).ToUniversalTime();
			var stays = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == companyId && a.AdmittedAt < toUtc && (a.DischargedAt == null || a.DischargedAt >= fromUtc))
				.Select(a => new { a.AdmittedAt, a.DischargedAt }).ToListAsync(ct);
			int days = stays.Sum(s => HospitalStewardshipRules.DaysOfTherapy(s.AdmittedAt.ToLocalTime(), s.DischargedAt?.ToLocalTime(), fromLocal, toExclusiveLocal));
			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == companyId && o.StartAt < toUtc && (o.StoppedAt == null || o.StoppedAt >= fromUtc))
				.Select(o => new { o.Drug, o.StartAt, o.StoppedAt }).ToListAsync(ct);
			var use = orders.Select(o => (o, f: HospitalMedSafetyRules.Find(o.Drug, abx))).Where(x => x.f != null)
				.GroupBy(x => x.f!.GenericNameEn ?? x.f.GenericName)
				.Select(g => { int dot = g.Sum(x => HospitalStewardshipRules.DaysOfTherapy(x.o.StartAt.ToLocalTime(), x.o.StoppedAt?.ToLocalTime(), fromLocal, toExclusiveLocal)); return new HospitalAbxUse(g.Key, dot, HospitalStewardshipRules.Per1000(dot, days)); })
				.Where(u => u.Dot > 0).OrderByDescending(u => u.Dot).ToList();
			return (use, days, HospitalStewardshipRules.Per1000(use.Sum(u => u.Dot), days));
		}

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int orderId, string? kind, string? decision, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await TeamAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalStewardshipRules.ReviewRefusal(kind ?? "", decision, note) is string why) return HospitalResult.Fail(why);
			try
			{
				var o = await _db.Set<HospitalMedOrder>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == ctx.CompanyId, ct);
				if (o == null) return HospitalResult.Fail("not_found");
				if (o.Status != HospitalMedOrderStatus.Active) return HospitalResult.Fail("This order is already stopped");
				var f = HospitalMedSafetyRules.Find(o.Drug, await AntimicrobialsAsync(ctx.CompanyId, ct));
				if (f == null) return HospitalResult.Fail("This medicine is not marked as an antimicrobial");
				if (kind == HospitalAbxReviewKind.Approval && f.FormularyStatus != HospitalFormularyStatus.Restricted) return HospitalResult.Fail("Only a restricted antimicrobial needs approval");
				if (decision == HospitalAbxDecision.Stop)
				{
					var stop = await new HospitalNursingService(_db, _access).StopOrderAsync(ctx, o.ID, employeeId, nowUtc, ct);
					if (!stop.Ok) return stop;
				}
				var row = new HospitalAbxReview { CompanyId = ctx.CompanyId, MedOrderId = o.ID, PatientId = o.PatientId, AdmissionId = o.AdmissionId, Kind = kind!, Decision = decision!, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), At = nowUtc, ByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return HospitalResult.Fail(NoTables); }
		}
	}
}
