using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H213 — restraint and seclusion, documented as the standards ask (CMS §482.13(e), JCI PC.03):
	//   · ORDERED by a doctor, for an admitted patient, with the reason and what was tried first (both required), the
	//     kind and device, and whether the family was told. Never "as needed".
	//   · TIME-LIMITED: an order lasts 24 hours; past that it is EXPIRED until the doctor renews it (another 24 hours,
	//     counted) or ends it.
	//   · CHECKED at its interval — every 2 hours for a physical or chemical restraint, every 15 minutes for seclusion:
	//     circulation, skin, fluids offered, toilet offered, behaviour, and whether it is still needed. A check past its
	//     time is OVERDUE on the list.
	//   · ENDED as early as possible, with the reason; an ended episode is never changed again.
	// ==========================================================================================
	public sealed class HospitalRestraintInput
	{
		public int AdmissionId { get; set; }
		public string? Kind { get; set; }
		public string? Device { get; set; }
		public string? Reason { get; set; }
		public string? Alternatives { get; set; }
		public bool FamilyInformed { get; set; }
	}

	public sealed class HospitalRestraintCheckInput
	{
		public bool CirculationOk { get; set; }
		public bool SkinOk { get; set; }
		public bool FluidsOffered { get; set; }
		public bool ToiletOffered { get; set; }
		public bool StillNeeded { get; set; }
		public string? Behaviour { get; set; }
	}

	public sealed record HospitalRestraintRow(HospitalRestraint Restraint, string Patient, string PatientEn, string Mrn, string? Bed, HospitalFace? OrderedBy,
		HospitalRestraintCheck? LastCheck, int Checks, DateTime NextCheckAt, bool CheckOverdue, bool Expired);

	public static class HospitalRestraintRules
	{
		public const int OrderHours = 24;
		public static int IntervalFor(string kind) => kind == HospitalRestraintKind.Seclusion ? 15 : 120;

		public static string? StartRefusal(HospitalRestraintInput i)
		{
			if (string.IsNullOrEmpty(i.Kind) || !HospitalRestraintKind.All.Contains(i.Kind)) return "Choose the kind of restraint";
			if (string.IsNullOrWhiteSpace(i.Reason)) return "Write why the restraint is needed";
			if (string.IsNullOrWhiteSpace(i.Alternatives)) return "Write what was tried before the restraint";
			if (i.Reason.Trim().Length > 500 || i.Alternatives.Trim().Length > 500 || (i.Device?.Trim().Length ?? 0) > 100) return "The text is too long";
			return null;
		}

		public static DateTime NextCheckAt(HospitalRestraint r, DateTime? lastCheckAt) => (lastCheckAt ?? r.StartedAt).AddMinutes(r.CheckEveryMinutes);
		public static bool CheckOverdue(HospitalRestraint r, DateTime? lastCheckAt, DateTime nowUtc) => r.EndedAt == null && nowUtc > NextCheckAt(r, lastCheckAt);
		public static bool Expired(HospitalRestraint r, DateTime nowUtc) => r.EndedAt == null && nowUtc > r.ExpiresAt;

		public static string? CheckRefusal(HospitalRestraint r, HospitalRestraintCheckInput i)
		{
			if (r.EndedAt != null) return "This restraint has ended";
			if (!i.CirculationOk || !i.SkinOk) return (i.Behaviour?.Trim().Length ?? 0) == 0 ? "Describe the circulation or skin problem in the note" : null;
			return (i.Behaviour?.Trim().Length ?? 0) > 300 ? "The text is too long" : null;
		}

		public static string? RenewRefusal(HospitalRestraint r) => r.EndedAt != null ? "This restraint has ended" : null;

		public static string? EndRefusal(HospitalRestraint r, string? reason)
		{
			if (r.EndedAt != null) return "This restraint has ended";
			if (string.IsNullOrWhiteSpace(reason)) return "Write why the restraint ends";
			return reason.Trim().Length > 300 ? "The text is too long" : null;
		}
	}

	public sealed class HospitalRestraintService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRestraintService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalRestraint> Restraints(BusinessContext ctx) => _db.Set<HospitalRestraint>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		public async Task<HospitalResult> StartAsync(BusinessContext ctx, HospitalRestraintInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalRestraintRules.StartRefusal(input) is string why) return HospitalResult.Fail(why);
			var adm = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active).Select(a => new { a.ID, a.PatientId }).FirstOrDefaultAsync(ct);
			if (adm == null) return HospitalResult.Fail("This admission is not active");
			if (await Restraints(ctx).AnyAsync(r => r.AdmissionId == adm.ID && r.EndedAt == null && r.Kind == input.Kind, ct)) return HospitalResult.Fail("This patient already has this restraint: renew or end it");
			var r = new HospitalRestraint
			{
				CompanyId = ctx.CompanyId, PatientId = adm.PatientId, AdmissionId = adm.ID, Kind = input.Kind!, Device = Clip(input.Device, 100), Reason = Clip(input.Reason, 500)!,
				Alternatives = Clip(input.Alternatives, 500)!, FamilyInformed = input.FamilyInformed, CheckEveryMinutes = HospitalRestraintRules.IntervalFor(input.Kind!),
				StartedAt = nowUtc, ExpiresAt = nowUtc.AddHours(HospitalRestraintRules.OrderHours), OrderedByEmployeeId = employeeId,
			};
			_db.Add(r);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		public async Task<HospitalResult> CheckAsync(BusinessContext ctx, int id, HospitalRestraintCheckInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Restraints(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalRestraintRules.CheckRefusal(r, input) is string why) return HospitalResult.Fail(why);
			var c = new HospitalRestraintCheck
			{
				CompanyId = ctx.CompanyId, RestraintId = r.ID, PatientId = r.PatientId, At = nowUtc, CirculationOk = input.CirculationOk, SkinOk = input.SkinOk,
				FluidsOffered = input.FluidsOffered, ToiletOffered = input.ToiletOffered, StillNeeded = input.StillNeeded, Behaviour = Clip(input.Behaviour, 300), ByEmployeeId = employeeId,
			};
			_db.Add(c);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> RenewAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Restraints(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalRestraintRules.RenewRefusal(r) is string why) return HospitalResult.Fail(why);
			int n = await Restraints(ctx).Where(x => x.ID == id && x.EndedAt == null && x.Renewals == r.Renewals).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.ExpiresAt, nowUtc.AddHours(HospitalRestraintRules.OrderHours)).SetProperty(x => x.Renewals, r.Renewals + 1).SetProperty(x => x.OrderedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("Someone else changed this restraint: open it again");
		}

		public async Task<HospitalResult> EndAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Restraints(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalRestraintRules.EndRefusal(r, reason) is string why) return HospitalResult.Fail(why);
			string t = Clip(reason, 300)!;
			int n = await Restraints(ctx).Where(x => x.ID == id && x.EndedAt == null).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.EndedAt, nowUtc).SetProperty(x => x.EndReason, t).SetProperty(x => x.EndedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("Someone else changed this restraint: open it again");
		}

		public async Task<(IReadOnlyList<HospitalRestraintRow> Rows, int Total, int Active, int Overdue, bool Ready)> ListAsync(BusinessContext ctx, string? q, bool ended, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalRestraintRow>(), 0, 0, 0, true);
			try
			{
				var query = from r in Restraints(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							where p.CompanyId == ctx.CompanyId && (ended ? r.EndedAt != null : r.EndedAt == null)
							select new { r, p };
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.p.Name.Contains(s) || x.p.NameEn.Contains(s) || x.p.Mrn.Contains(s)); }
				int total = await query.CountAsync(ct);
				page = Math.Max(1, page);
				var rows = await (ended ? query.OrderByDescending(x => x.r.EndedAt) : query.OrderBy(x => x.r.StartedAt)).Skip((page - 1) * size).Take(size).ToListAsync(ct);
				var ids = rows.Select(x => x.r.ID).ToList();
				var checks = await _db.Set<HospitalRestraintCheck>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && ids.Contains(c.RestraintId)).ToListAsync(ct);
				var adm = rows.Select(x => x.r.AdmissionId).ToList();
				var beds = (await (from st in _db.HospitalBedStays.AsNoTracking() join b in _db.HospitalBeds.AsNoTracking() on st.BedId equals b.ID
								   where st.CompanyId == ctx.CompanyId && st.EndedAt == null && adm.Contains(st.AdmissionId) select new { st.AdmissionId, b.Code }).ToListAsync(ct))
					.GroupBy(x => x.AdmissionId).ToDictionary(g => g.Key, g => g.First().Code);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(x => x.r.OrderedByEmployeeId), ct);
				// the counters cover every active episode, not only this page
				var active = await Restraints(ctx).AsNoTracking().Where(r => r.EndedAt == null).Select(r => new { r.ID, r.StartedAt, r.CheckEveryMinutes, r.ExpiresAt }).ToListAsync(ct);
				var activeIds = active.Select(a => a.ID).ToList();
				var lastAt = await _db.Set<HospitalRestraintCheck>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && activeIds.Contains(c.RestraintId))
					.GroupBy(c => c.RestraintId).Select(g => new { g.Key, At = g.Max(c => c.At) }).ToDictionaryAsync(x => x.Key, x => (DateTime?)x.At, ct);
				int overdue = active.Count(a => nowUtc > (lastAt.GetValueOrDefault(a.ID) ?? a.StartedAt).AddMinutes(a.CheckEveryMinutes) || nowUtc > a.ExpiresAt);
				return (rows.Select(x =>
				{
					var mine = checks.Where(c => c.RestraintId == x.r.ID).OrderByDescending(c => c.At).ToList();
					var last = mine.FirstOrDefault();
					return new HospitalRestraintRow(x.r, x.p.Name, x.p.NameEn, x.p.Mrn, beds.GetValueOrDefault(x.r.AdmissionId), x.r.OrderedByEmployeeId is int oid ? faces.GetValueOrDefault(oid) : null, last, mine.Count,
						HospitalRestraintRules.NextCheckAt(x.r, last?.At), HospitalRestraintRules.CheckOverdue(x.r, last?.At, nowUtc), HospitalRestraintRules.Expired(x.r, nowUtc));
				}).ToList(), total, active.Count, overdue, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalRestraintRow>(), 0, 0, 0, false); }
		}

		/// The patients in a bed now, to start a restraint for (bed, name, file).
		public async Task<IReadOnlyList<(int AdmissionId, string Bed, string Name, string NameEn, string Mrn)>> InpatientsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await (from st in _db.HospitalBedStays.AsNoTracking()
					join b in _db.HospitalBeds.AsNoTracking() on st.BedId equals b.ID
					join a in _db.HospitalAdmissions.AsNoTracking() on st.AdmissionId equals a.ID
					join p in _db.HospitalPatients.AsNoTracking() on st.PatientId equals p.ID
					where st.CompanyId == ctx.CompanyId && st.EndedAt == null && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
					orderby b.Code
					select new { st.AdmissionId, b.Code, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct))
				.Select(x => (x.AdmissionId, x.Code, x.Name, x.NameEn, x.Mrn)).ToList();
	}
}
