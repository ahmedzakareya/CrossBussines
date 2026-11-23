using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalOperativeNoteInput
	{
		public string? PreOpDiagnosis { get; set; } public string? PostOpDiagnosis { get; set; } public string? ProcedurePerformed { get; set; } public string? Indication { get; set; }
		public string? Findings { get; set; } public string? Technique { get; set; } public string? Specimens { get; set; } public int? BloodLossMl { get; set; }
		public string? Drains { get; set; } public string? Complications { get; set; } public string? PostOpPlan { get; set; }
	}

	public sealed record HospitalOperativeNoteView(HospitalSurgery Case, HospitalOperativeNote? Note, string PatientName, string? PatientNameEn, string? Photo, string? Mrn,
		HospitalPractitioner? Surgeon, HospitalFace? SurgeonFace, HospitalFace? SignedBy, bool CanEdit, bool CanSign);
	public sealed record HospitalOperativeNoteRow(HospitalSurgery Case, HospitalOperativeNote? Note, string PatientName, string? PatientNameEn, string? Photo, string? Mrn, HospitalFace? Surgeon, bool Overdue);
	public sealed record HospitalOperativeNotePage(bool Ready, IReadOnlyList<HospitalOperativeNoteRow> Rows, int Total, int Unsigned, int Overdue);

	// ==========================================================================================
	// HOSPITAL H260 — the operative note: the theatre team (`consult`) drafts, the case's surgeon signs, and the worklist
	// shows the cases of the last 30 days with their note's state (unsigned first). The case's close checks the note
	// through SignedOrNotInstalledAsync.
	// ==========================================================================================
	public sealed class HospitalOperativeNoteService
	{
		public const string NoTables = "Operative notes are not installed yet (hospital_h260_operative_notes.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalOperativeNoteService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		private async Task<int?> SurgeonEmployeeAsync(int companyId, int surgeonId, CancellationToken ct) =>
			await _db.HospitalPractitioners.AsNoTracking().Where(p => p.ID == surgeonId && p.CompanyId == companyId).Select(p => p.EmployeeId).FirstOrDefaultAsync(ct);

		public async Task<HospitalOperativeNoteView?> GetAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct)) return null;
			var c = await _db.Set<HospitalSurgery>().AsNoTracking().FirstOrDefaultAsync(s => s.ID == surgeryId && s.CompanyId == ctx.CompanyId, ct);
			if (c == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == c.PatientId).Select(x => new { x.Name, x.NameEn, x.PhotoPath, x.Mrn }).FirstAsync(ct);
			HospitalOperativeNote? n = null;
			try { n = await _db.Set<HospitalOperativeNote>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.SurgeryId == surgeryId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var surgeon = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.SurgeonId, ct);
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, new[] { surgeon?.EmployeeId, n?.SignedByEmployeeId }, ct);
			bool consult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			bool canEdit = consult && HospitalOperativeNoteRules.EditRefusal(c.Status, n?.SignedAt, null) == null;
			return new HospitalOperativeNoteView(c, n, p.Name, p.NameEn, p.PhotoPath, p.Mrn, surgeon, surgeon?.EmployeeId is int se ? faces.GetValueOrDefault(se) : null,
				n?.SignedByEmployeeId is int sb ? faces.GetValueOrDefault(sb) : null, canEdit, canEdit && surgeon?.EmployeeId != null && surgeon.EmployeeId == ctx.EmployeeId);
		}

		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int surgeryId, HospitalOperativeNoteInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await _db.Set<HospitalSurgery>().AsNoTracking().FirstOrDefaultAsync(s => s.ID == surgeryId && s.CompanyId == ctx.CompanyId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			try
			{
				var n = await _db.Set<HospitalOperativeNote>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.SurgeryId == surgeryId, ct);
				if (HospitalOperativeNoteRules.EditRefusal(c.Status, n?.SignedAt, i.BloodLossMl) is string why) return HospitalResult.Fail(why);
				if (n == null) _db.Add(n = new HospitalOperativeNote { CompanyId = ctx.CompanyId, SurgeryId = c.ID, PatientId = c.PatientId });
				n.PreOpDiagnosis = Clip(i.PreOpDiagnosis, 300); n.PostOpDiagnosis = Clip(i.PostOpDiagnosis, 300); n.ProcedurePerformed = Clip(i.ProcedurePerformed, 300);
				n.Indication = Clip(i.Indication, 1000); n.Findings = Clip(i.Findings, 2000); n.Technique = Clip(i.Technique, 4000); n.Specimens = Clip(i.Specimens, 500);
				n.BloodLossMl = i.BloodLossMl; n.Drains = Clip(i.Drains, 300); n.Complications = Clip(i.Complications, 1000); n.PostOpPlan = Clip(i.PostOpPlan, 2000);
				n.UpdatedAt = nowUtc; n.UpdatedByEmployeeId = employeeId;
				try { await _db.SaveChangesAsync(ct); }
				catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
				return HospitalResult.Success(n.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Saves what is on the form, then the case's surgeon signs it: locked from here.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int surgeryId, HospitalOperativeNoteInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			var saved = await SaveAsync(ctx, surgeryId, i, ctx.EmployeeId, nowUtc, ct);
			if (!saved.Ok) return saved;
			var c = await _db.Set<HospitalSurgery>().AsNoTracking().FirstAsync(s => s.ID == surgeryId && s.CompanyId == ctx.CompanyId, ct);
			var n = await _db.Set<HospitalOperativeNote>().FirstAsync(x => x.CompanyId == ctx.CompanyId && x.SurgeryId == surgeryId, ct);
			if (HospitalOperativeNoteRules.SignRefusal(n, await SurgeonEmployeeAsync(ctx.CompanyId, c.SurgeonId, ct), ctx.EmployeeId) is string why)
				return HospitalResult.Fail(why, string.Join(" · ", HospitalOperativeNoteRules.Missing(n)));
			n.SignedAt = nowUtc; n.SignedByEmployeeId = ctx.EmployeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(n.ID);
		}

		/// The case's close asks this: true when the note is signed — or when this install has no operative notes yet.
		public async Task<bool> SignedOrNotInstalledAsync(int companyId, int surgeryId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalOperativeNote>().AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.SurgeryId == surgeryId && x.SignedAt != null, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return true; }
		}

		/// Cases of the last 30 days that started, with their note: unsigned (the default view) or signed.
		public async Task<HospitalOperativeNotePage?> WorklistAsync(BusinessContext ctx, string? q, string? view, int page, int size, DateTime now, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct)) return null;
			try
			{
				var since = now.Date.AddDays(-30);
				var notes = _db.Set<HospitalOperativeNote>().AsNoTracking().Where(n => n.CompanyId == ctx.CompanyId);
				var cases = from c in _db.Set<HospitalSurgery>().AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							where c.CompanyId == ctx.CompanyId && (c.Status == HospitalSurgeryStatus.InProgress || c.Status == HospitalSurgeryStatus.Done) && c.CaseDate >= since
							select new { c, p.Name, p.NameEn, p.PhotoPath, p.Mrn, Signed = notes.Any(n => n.SurgeryId == c.ID && n.SignedAt != null) };
				int unsigned = await cases.CountAsync(x => !x.Signed, ct);
				var overdueCut = now.AddHours(-24);
				int overdue = await cases.CountAsync(x => !x.Signed && x.c.StartedAt != null && x.c.StartedAt < overdueCut, ct);
				var query = view == "signed" ? cases.Where(x => x.Signed) : cases.Where(x => !x.Signed);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s) || x.c.CaseNo.Contains(s) || x.c.Name.Contains(s) || x.c.NameEn.Contains(s)); }
				int total = await query.CountAsync(ct);
				var rows = await query.OrderBy(x => x.c.StartedAt ?? x.c.CaseDate).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var ids = rows.Select(r => r.c.ID).ToList();
				var ns = await notes.Where(n => ids.Contains(n.SurgeryId)).ToDictionaryAsync(n => n.SurgeryId, ct);
				var surgeonIds = rows.Select(r => r.c.SurgeonId).Distinct().ToList();
				var emp = await _db.HospitalPractitioners.AsNoTracking().Where(d => surgeonIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, emp.Values.Select(d => d.EmployeeId), ct);
				HospitalFace? Face(int sid) => emp.TryGetValue(sid, out var d) ? (d.EmployeeId is int e && faces.TryGetValue(e, out var f) ? f : new HospitalFace(d.Name, d.NameEn, null)) : null;
				return new HospitalOperativeNotePage(true, rows.Select(r => new HospitalOperativeNoteRow(r.c, ns.GetValueOrDefault(r.c.ID), r.Name, r.NameEn, r.PhotoPath, r.Mrn, Face(r.c.SurgeonId),
					HospitalOperativeNoteRules.Overdue(r.c.StartedAt, ns.GetValueOrDefault(r.c.ID)?.SignedAt, now))).ToList(), total, unsigned, overdue);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalOperativeNotePage(false, Array.Empty<HospitalOperativeNoteRow>(), 0, 0, 0); }
		}
	}
}
