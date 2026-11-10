using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H64 — restricted files and break-glass. Who may open a restricted file, the emergency opening, and the
	// administrator's review of every opening. The gate stands in front of the patient's FILE page; lists and searches
	// still show the patient's name and number (a restriction keeps the record out of reach, not its existence).
	// ==========================================================================================

	public static class HospitalFileAccess
	{
		public const string Open = "Open";              // not restricted
		public const string Privileged = "Privileged";  // the administrator or "view confidential"
		public const string CareTeam = "CareTeam";      // the attending doctor of an open stay / a recent visit's doctor
		public const string BreakGlass = "BreakGlass";  // an emergency opening still in its window
		public const string Denied = "Denied";
		public static bool Allows(string a) => a != Denied;
	}

	public sealed record HospitalBreakGlassRow(HospitalBreakGlass Entry, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Employee, string EmployeeEn, string? EmployeePhoto, string? Reviewer, string? ReviewerEn);

	public static class HospitalRestrictedRules
	{
		/// How long a break-glass opening lasts, and how far back a visit makes its doctor part of the care team.
		public const int WindowMinutes = 60, CareTeamDays = 30, MinReasonLength = 10;

		public static string Decide(bool restricted, bool privileged, bool careTeam, bool openGlass) =>
			!restricted ? HospitalFileAccess.Open : privileged ? HospitalFileAccess.Privileged : careTeam ? HospitalFileAccess.CareTeam
			: openGlass ? HospitalFileAccess.BreakGlass : HospitalFileAccess.Denied;

		public static string? BreakRefusal(string access, string? reason)
		{
			if (access == HospitalFileAccess.Open) return "This file is not restricted";
			if (access is HospitalFileAccess.Privileged or HospitalFileAccess.CareTeam) return "You may already open this file";
			if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinReasonLength) return "Write the emergency that needs this file (at least {0} characters)";
			if (reason.Trim().Length > 500) return "A text is too long";
			return null;
		}

		public static string? ReviewRefusal(HospitalBreakGlass g, int me, string? outcome, string? note)
		{
			if (g.ReviewedAt != null) return "This opening was already reviewed";
			if (g.EmployeeId == me) return "Someone else reviews your own opening";
			if (!HospitalBreakGlassOutcome.All.Contains(outcome ?? "")) return "Choose whether it was justified";
			if (outcome == HospitalBreakGlassOutcome.Unjustified && string.IsNullOrWhiteSpace(note)) return "Write why it was not justified";
			return null;
		}
	}

	public sealed class HospitalRestrictedAccessService
	{
		public const string NoTables = "Restricted files are not installed yet (hospital_h64_break_glass.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRestrictedAccessService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalRestrictedFile> Live(BusinessContext ctx) => _db.Set<HospitalRestrictedFile>().Where(r => r.CompanyId == ctx.CompanyId && r.LiftedAt == null);
		private IQueryable<HospitalBreakGlass> Glass(BusinessContext ctx) => _db.Set<HospitalBreakGlass>().Where(g => g.CompanyId == ctx.CompanyId);

		/// The live restriction of a file (null = not restricted, or the tables are not installed).
		public async Task<HospitalRestrictedFile?> RestrictionAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try { return await Live(ctx).AsNoTracking().FirstOrDefaultAsync(r => r.PatientId == patientId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// May this employee open this file now, and on what ground. `until` = the end of an open break-glass window.
		public async Task<(string Access, DateTime? Until)> AccessAsync(BusinessContext ctx, int patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var r = await RestrictionAsync(ctx, patientId, ct);
			if (r == null) return (HospitalFileAccess.Open, null);
			bool privileged = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct) || await _access.CanAsync(ctx, HospitalActions.ConfidentialView, null, ct);
			if (privileged || employeeId is not int me) return (HospitalRestrictedRules.Decide(true, privileged, false, false), null);
			var since = nowUtc.AddDays(-HospitalRestrictedRules.CareTeamDays);
			bool care = await _db.HospitalAdmissions.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.DischargedAt == null && a.AttendingEmployeeId == me, ct)
				|| await (from e in _db.HospitalEncounters join pr in _db.HospitalPractitioners on e.PractitionerId equals pr.ID
						  where e.CompanyId == ctx.CompanyId && e.PatientId == patientId && e.StartedAt >= since && pr.EmployeeId == me select e.ID).AnyAsync(ct);
			DateTime? until = care ? null : await Glass(ctx).Where(g => g.PatientId == patientId && g.EmployeeId == me && g.ExpiresAt > nowUtc)
				.OrderByDescending(g => g.ExpiresAt).Select(g => (DateTime?)g.ExpiresAt).FirstOrDefaultAsync(ct);
			return (HospitalRestrictedRules.Decide(true, false, care, until != null), until);
		}

		public async Task<HospitalResult> RestrictAsync(BusinessContext ctx, int patientId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why the file is restricted");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				if (await Live(ctx).AnyAsync(r => r.PatientId == patientId, ct)) return HospitalResult.Fail("This file is already restricted");
				var row = new HospitalRestrictedFile { CompanyId = ctx.CompanyId, PatientId = patientId, Reason = Clip(reason, 300)!, RestrictedAt = nowUtc, RestrictedByEmployeeId = me };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This file is already restricted"); }
		}

		public async Task<HospitalResult> LiftAsync(BusinessContext ctx, int patientId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			try
			{
				int n = await Live(ctx).Where(r => r.PatientId == patientId).ExecuteUpdateAsync(u => u.SetProperty(r => r.LiftedAt, nowUtc).SetProperty(r => r.LiftedByEmployeeId, me), ct);
				return n == 1 ? HospitalResult.Success(patientId) : HospitalResult.Fail("This file is not restricted");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Breaks the glass: the file opens to this employee for the window, and the opening waits for its review.
		public async Task<HospitalResult> BreakAsync(BusinessContext ctx, int patientId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var (access, _) = await AccessAsync(ctx, patientId, me, nowUtc, ct);
			if (access == HospitalFileAccess.BreakGlass) return HospitalResult.Success(patientId);   // its window is still open
			if (HospitalRestrictedRules.BreakRefusal(access, reason) is string why) return HospitalResult.Fail(why, HospitalRestrictedRules.MinReasonLength);
			try
			{
				var row = new HospitalBreakGlass { CompanyId = ctx.CompanyId, PatientId = patientId, EmployeeId = me, Reason = Clip(reason, 500)!, OpenedAt = nowUtc, ExpiresAt = nowUtc.AddMinutes(HospitalRestrictedRules.WindowMinutes) };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int id, string? outcome, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			HospitalBreakGlass? g;
			try { g = await Glass(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			if (g == null) return HospitalResult.Fail("not_found");
			if (HospitalRestrictedRules.ReviewRefusal(g, me, outcome, note) is string why) return HospitalResult.Fail(why);
			int n = await Glass(ctx).Where(x => x.ID == id && x.ReviewedAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.ReviewedAt, nowUtc)
				.SetProperty(x => x.ReviewedByEmployeeId, me).SetProperty(x => x.Outcome, outcome).SetProperty(x => x.ReviewNote, Clip(note, 300)), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This opening was already reviewed");
		}

		/// The review screen: openings waiting for review first (oldest first), then the reviewed (newest first);
		/// searched by patient, MRN or employee; one page and the totals.
		public async Task<(IReadOnlyList<HospitalBreakGlassRow> Rows, int Total, int Waiting, int Restricted)> ListAsync(BusinessContext ctx, string? state, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			var raw = await (from g in Glass(ctx).AsNoTracking()
							 join p in _db.HospitalPatients.AsNoTracking() on g.PatientId equals p.ID
							 select new { g, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			var eids = raw.SelectMany(r => new int?[] { r.g.EmployeeId, r.g.ReviewedByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
			(string, string) Who(int? id) => id is int i && people.TryGetValue(i, out var e) ? (e.FullName ?? "", e.FullNameEn ?? e.FullName ?? "") : ("", "");
			var rows = raw.Select(r =>
			{
				var (w, we) = Who(r.g.EmployeeId); var (v, ve) = Who(r.g.ReviewedByEmployeeId);
				return new HospitalBreakGlassRow(r.g, r.Name, r.NameEn, r.Mrn, r.PhotoPath, w, we, photos.GetValueOrDefault(r.g.EmployeeId), r.g.ReviewedAt == null ? null : v, r.g.ReviewedAt == null ? null : ve);
			}).ToList();
			int waiting = rows.Count(r => r.Entry.ReviewedAt == null);
			int restricted = await Live(ctx).CountAsync(ct);
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var hit = rows.Where(r => (state switch { "waiting" => r.Entry.ReviewedAt == null, "reviewed" => r.Entry.ReviewedAt != null, _ => true })
				&& terms.All(t => r.PatientName.Contains(t, StringComparison.OrdinalIgnoreCase) || r.PatientNameEn.Contains(t, StringComparison.OrdinalIgnoreCase)
					|| r.Mrn.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Employee.Contains(t, StringComparison.OrdinalIgnoreCase) || r.EmployeeEn.Contains(t, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(r => r.Entry.ReviewedAt == null ? 0 : 1).ThenBy(r => r.Entry.ReviewedAt == null ? r.Entry.OpenedAt : DateTime.MaxValue).ThenByDescending(r => r.Entry.ReviewedAt).ToList();
			return (hit.Skip((page - 1) * pageSize).Take(pageSize).ToList(), hit.Count, waiting, restricted);
		}
	}
}
