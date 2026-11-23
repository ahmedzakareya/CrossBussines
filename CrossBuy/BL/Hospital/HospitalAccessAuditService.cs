using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalAccessRow(long Id, DateTime At, string Action, int PatientId, string PatientName, string? PatientNameEn, string? PatientPhoto, string Mrn, HospitalFace? Employee, int? EmployeeId);
	public sealed record HospitalAccessPage(IReadOnlyList<HospitalAccessRow> Rows, int Total, int People, int Files);

	// ==========================================================================================
	// HOSPITAL H264 — THE AUDIT VIEWER: every opening, creation and change of a patient file (the access log every
	// screen already writes) in one searchable list — by member of staff, by patient, by day range and by action — so
	// the privacy officer answers "who opened this file?" and "what did this person open?" without a database query.
	// Read-only, and only for the administrator (`manage`), the same as the privacy monitor (H255).
	// ==========================================================================================
	public static class HospitalAccessAuditRules
	{
		/// The day range searched: at most 92 days, the end not before the start (local dates, inclusive).
		public static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to, DateTime today)
		{
			var t = (to ?? today).Date; var f = (from ?? t.AddDays(-6)).Date;
			if (f > t) (f, t) = (t, f);
			if ((t - f).TotalDays > 91) f = t.AddDays(-91);
			return (f, t);
		}
	}

	public sealed class HospitalAccessAuditService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalAccessAuditService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		public async Task<HospitalAccessPage?> PageAsync(BusinessContext ctx, string? who, string? patient, DateTime fromLocal, DateTime toLocal, string? action, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			// the log keeps UTC; the range is the ward's days
			var offset = HospitalNursingService.UtcToWard(DateTime.UtcNow) - DateTime.UtcNow;
			var fromUtc = fromLocal.Date - offset; var toUtc = toLocal.Date.AddDays(1) - offset;
			var query = from l in _db.HospitalPatientAccessLogs.AsNoTracking()
						join p in _db.HospitalPatients.AsNoTracking() on l.PatientId equals p.ID
						join e0 in _db.Employee.AsNoTracking() on l.EmployeeId equals (int?)e0.ID into ej
						from e in ej.DefaultIfEmpty()
						where l.CompanyId == ctx.CompanyId && l.At >= fromUtc && l.At < toUtc
						select new { l, p.Name, p.NameEn, p.PhotoPath, p.Mrn, Emp = e == null ? null : e.FullName, EmpEn = e == null ? null : e.FullNameEn };
			if (action is HospitalPatientAccessActions.Open or HospitalPatientAccessActions.Create or HospitalPatientAccessActions.Update) query = query.Where(x => x.l.Action == action);
			if (!string.IsNullOrWhiteSpace(who)) { var w = who.Trim(); query = query.Where(x => (x.Emp ?? "").Contains(w) || (x.EmpEn ?? "").Contains(w)); }
			if (!string.IsNullOrWhiteSpace(patient)) { var s = patient.Trim(); query = query.Where(x => x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s)); }
			int total = await query.CountAsync(ct);
			int people = await query.Where(x => x.l.EmployeeId != null).Select(x => x.l.EmployeeId).Distinct().CountAsync(ct);
			int files = await query.Select(x => x.l.PatientId).Distinct().CountAsync(ct);
			var rows = await query.OrderByDescending(x => x.l.At).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(r => r.l.EmployeeId), ct);
			return new HospitalAccessPage(rows.Select(r => new HospitalAccessRow(r.l.ID, r.l.At, r.l.Action, r.l.PatientId, r.Name, r.NameEn, r.PhotoPath, r.Mrn,
				r.l.EmployeeId is int e ? faces.GetValueOrDefault(e) : null, r.l.EmployeeId)).ToList(), total, people, files);
		}
	}
}
