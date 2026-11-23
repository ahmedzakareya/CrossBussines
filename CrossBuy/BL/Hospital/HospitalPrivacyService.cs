using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalPrivacyReviewInput { public string? Status { get; set; } public string? Note { get; set; } }
	public sealed record HospitalPrivacyRow(HospitalPrivacyAlert Alert, HospitalFace? Employee, string? PatientName, string? PatientNameEn, string? PatientPhoto, string? Mrn, HospitalFace? Reviewer);
	public sealed record HospitalPrivacyPage(bool Ready, IReadOnlyList<HospitalPrivacyRow> Rows, int Total, int Open, IReadOnlyDictionary<string, int> OpenByKind);

	// ==========================================================================================
	// HOSPITAL H255 — scanning the access log into alerts (the last 14 days, each alert once) and the privacy officer's
	// review. The hospital administrator (`manage`) is the officer here. Scanning runs when the screen opens (no worker).
	//   · NO CARE LINK is looked for only for doctors (practitioners): a doctor who opened a file with no appointment,
	//     encounter, stay as attending, or laboratory order of theirs for that patient within 30 days either side —
	//     and the patient not in the emergency department or admitted then (shared care). Nurses and desks work across
	//     the ward and are left to the other patterns.
	// ==========================================================================================
	public sealed class HospitalPrivacyService
	{
		public const string NoTables = "Privacy monitoring is not installed yet (hospital_h255_privacy.sql).";
		public const int WindowDays = 14;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPrivacyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		public async Task<int> ScanAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var from = nowUtc.AddDays(-WindowDays - 30);
			var logs = await _db.HospitalPatientAccessLogs.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.EmployeeId != null && l.At >= from && l.Action == HospitalPatientAccessActions.Open)
				.OrderByDescending(l => l.At).Take(50000).Select(l => new { Emp = l.EmployeeId!.Value, l.PatientId, l.At }).ToListAsync(ct);
			if (logs.Count == 0) return 0;
			var windowStart = nowUtc.AddDays(-WindowDays);
			var recent = logs.Where(l => l.At >= windowStart).ToList();
			var have = (await _db.Set<HospitalPrivacyAlert>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Day >= Ward(windowStart).Date.AddDays(-1))
				.Select(a => new { a.EmployeeId, a.PatientId, a.Kind, a.Day }).ToListAsync(ct)).Select(a => (a.EmployeeId, a.PatientId, a.Kind, a.Day)).ToHashSet();
			var add = new List<HospitalPrivacyAlert>();
			void Add(int emp, int? pid, string kind, DateTime day, int count)
			{
				if (have.Add((emp, pid, kind, day.Date)))
					add.Add(new HospitalPrivacyAlert { CompanyId = ctx.CompanyId, EmployeeId = emp, PatientId = pid, Kind = kind, Day = day.Date, Count = count, CreatedAt = nowUtc });
			}

			var empIds = recent.Select(l => l.Emp).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			var pids = recent.Select(l => l.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn }).ToDictionaryAsync(p => p.ID, ct);

			// family name
			foreach (var g in recent.GroupBy(l => (l.Emp, l.PatientId, Day: Ward(l.At).Date)))
				if (emps.TryGetValue(g.Key.Emp, out var e) && pats.TryGetValue(g.Key.PatientId, out var p) && HospitalPrivacyRules.SameFamily(e.FullName, e.FullNameEn, p.Name, p.NameEn))
					Add(g.Key.Emp, g.Key.PatientId, HospitalPrivacyKind.SameFamily, g.Key.Day, g.Count());

			// volume, against the employee's own previous 30 days
			var perDay = logs.GroupBy(l => (l.Emp, Day: Ward(l.At).Date)).ToDictionary(g => g.Key, g => g.Select(x => x.PatientId).Distinct().Count());
			foreach (var k in perDay.Keys.Where(k => k.Day >= Ward(windowStart).Date))
			{
				var prev = Enumerable.Range(1, 30).Select(i => perDay.GetValueOrDefault((k.Emp, k.Day.AddDays(-i)))).ToList();
				if (HospitalPrivacyRules.VolumeAnomaly(perDay[k], prev)) Add(k.Emp, null, HospitalPrivacyKind.Volume, k.Day, perDay[k]);
			}

			// a restricted file opened after hours
			List<int> restricted;
			try { restricted = await _db.Set<HospitalRestrictedFile>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LiftedAt == null && pids.Contains(r.PatientId)).Select(r => r.PatientId).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { restricted = new(); }
			foreach (var g in recent.Where(l => restricted.Contains(l.PatientId) && HospitalPrivacyRules.AfterHours(Ward(l.At))).GroupBy(l => (l.Emp, l.PatientId, Day: Ward(l.At).Date)))
				Add(g.Key.Emp, g.Key.PatientId, HospitalPrivacyKind.AfterHours, g.Key.Day, g.Count());

			// no care link (doctors only)
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.EmployeeId != null && empIds.Contains(d.EmployeeId!.Value)).Select(d => new { d.ID, Emp = d.EmployeeId!.Value }).ToListAsync(ct);
			if (docs.Count > 0)
			{
				var docIds = docs.Select(d => d.ID).ToList(); var docEmps = docs.Select(d => d.Emp).ToList();
				var linkFrom = windowStart.AddDays(-30);
				var appts = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && docIds.Contains(a.PractitionerId) && pids.Contains(a.PatientId) && a.AppointmentDate >= linkFrom.Date)
					.Select(a => new { a.PractitionerId, a.PatientId }).ToListAsync(ct);
				var stays = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && pids.Contains(a.PatientId) && (a.DischargedAt == null || a.DischargedAt >= linkFrom))
					.Select(a => new { a.PatientId, a.AttendingEmployeeId, a.AdmittedAt, a.DischargedAt }).ToListAsync(ct);
				var labs = await _db.Set<HospitalLabOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && pids.Contains(o.PatientId) && o.OrderedByEmployeeId != null && docEmps.Contains(o.OrderedByEmployeeId!.Value) && o.OrderedAt >= linkFrom)
					.Select(o => new { o.PatientId, Emp = o.OrderedByEmployeeId!.Value }).ToListAsync(ct);
				var er = await _db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && v.PatientId != null && pids.Contains(v.PatientId!.Value) && v.ArrivedAt >= linkFrom)
					.Select(v => new { PatientId = v.PatientId!.Value, v.ArrivedAt }).ToListAsync(ct);
				foreach (var g in recent.Where(l => docEmps.Contains(l.Emp)).GroupBy(l => (l.Emp, l.PatientId, Day: Ward(l.At).Date)))
				{
					var docId = docs.First(d => d.Emp == g.Key.Emp).ID;
					var at = g.Min(x => x.At);
					bool linked = appts.Any(a => a.PractitionerId == docId && a.PatientId == g.Key.PatientId)
						|| labs.Any(o => o.Emp == g.Key.Emp && o.PatientId == g.Key.PatientId)
						|| stays.Any(s => s.PatientId == g.Key.PatientId && (s.AttendingEmployeeId == g.Key.Emp || (s.AdmittedAt <= at.AddDays(1) && (s.DischargedAt == null || s.DischargedAt >= at.AddDays(-1)))))
						|| er.Any(v => v.PatientId == g.Key.PatientId && Math.Abs((v.ArrivedAt - at).TotalDays) <= 2);
					if (!linked) Add(g.Key.Emp, g.Key.PatientId, HospitalPrivacyKind.NoCareLink, g.Key.Day, g.Count());
				}
			}

			if (add.Count == 0) return 0;
			_db.AddRange(add);
			try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }
			return add.Count;
		}

		public async Task<HospitalPrivacyPage?> PageAsync(BusinessContext ctx, string? q, string? view, string? kind, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			try
			{
				await ScanAsync(ctx, nowUtc, ct);
				var all = _db.Set<HospitalPrivacyAlert>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId);
				var open = await all.Where(a => a.Status == HospitalPrivacyStatus.Open).GroupBy(a => a.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var qq = view == "reviewed" ? all.Where(a => a.Status != HospitalPrivacyStatus.Open) : all.Where(a => a.Status == HospitalPrivacyStatus.Open);
				if (HospitalPrivacyKind.All.Contains(kind ?? "")) qq = qq.Where(a => a.Kind == kind);
				var list = await qq.OrderByDescending(a => a.Day).ThenBy(a => a.Kind).Take(2000).ToListAsync(ct);
				var empIds = list.Select(a => (int?)a.EmployeeId).Concat(list.Select(a => a.ReviewedByEmployeeId)).ToList();
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, empIds, ct);
				var pids = list.Where(a => a.PatientId != null).Select(a => a.PatientId!.Value).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
				var rows = list.Select(a =>
				{
					var p = a.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					return new HospitalPrivacyRow(a, faces.GetValueOrDefault(a.EmployeeId), p?.Name, p?.NameEn, p?.PhotoPath, p?.Mrn, a.ReviewedByEmployeeId is int r ? faces.GetValueOrDefault(r) : null);
				});
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					rows = rows.Where(r => (r.Employee?.Name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Employee?.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
						|| (r.PatientName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.PatientNameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Mrn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
				}
				var ordered = rows.ToList();
				return new HospitalPrivacyPage(true, ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), ordered.Count, open.Values.Sum(), open);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return new HospitalPrivacyPage(false, Array.Empty<HospitalPrivacyRow>(), 0, 0, new Dictionary<string, int>());
			}
		}

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int id, HospitalPrivacyReviewInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.Set<HospitalPrivacyAlert>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.EmployeeId == ctx.EmployeeId) return HospitalResult.Fail("An alert about you is reviewed by another officer");
			if (HospitalPrivacyRules.ReviewRefusal(a.Status, i.Status, i.Note) is string why) return HospitalResult.Fail(why);
			a.Status = i.Status!; a.Note = i.Note!.Trim()[..Math.Min(500, i.Note.Trim().Length)]; a.ReviewedAt = nowUtc; a.ReviewedByEmployeeId = ctx.EmployeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(a.ID);
		}
	}
}
