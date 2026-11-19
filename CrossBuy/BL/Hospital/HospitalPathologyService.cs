using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H216 — anatomic pathology. A doctor REQUESTS a case for a tissue specimen (what, from where, the
	// clinical history, how many containers); the laboratory RECEIVES it (the containers counted against the request),
	// describes and cuts it (GROSS: the description and the number of blocks), and the pathologist reads it and SIGNS
	// OUT the report (microscopy, the diagnosis, and whether it is malignant). Each step follows the one before; a
	// signed report is never edited — a later finding is an AMENDMENT with its reason, printed beside it. A request not
	// yet received may be cancelled with its reason. A case received more than 3 days ago and not signed is LATE
	// (the usual biopsy turnaround is 2 to 3 working days).
	// ==========================================================================================
	public sealed class HospitalPathRequestInput
	{
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string? Source { get; set; }
		public int? SourceRefId { get; set; }
		public string? Specimen { get; set; }
		public string? Site { get; set; }
		public string? ClinicalHistory { get; set; }
		public int Containers { get; set; } = 1;
	}

	public sealed class HospitalPathStepInput
	{
		public string? To { get; set; }
		public int? Containers { get; set; }
		public string? Gross { get; set; }
		public int? Blocks { get; set; }
		public string? Microscopic { get; set; }
		public string? Diagnosis { get; set; }
		public bool Malignant { get; set; }
		public string? Reason { get; set; }
	}

	public sealed record HospitalPathRow(HospitalPathCase Case, string Patient, string PatientEn, string Mrn, HospitalFace? Pathologist, HospitalFace? Requester, int Amendments, bool Late);

	public static class HospitalPathRules
	{
		public const int LateDays = 3;

		public static string? RequestRefusal(HospitalPathRequestInput i)
		{
			if (string.IsNullOrEmpty(i.Source) || !HospitalPathSource.All.Contains(i.Source)) return "Choose where the specimen comes from";
			if (string.IsNullOrWhiteSpace(i.Specimen)) return "Write what the specimen is";
			if (i.Containers is < 1 or > 50) return "The containers must be between 1 and 50";
			if (i.Specimen.Trim().Length > 200 || (i.Site?.Trim().Length ?? 0) > 200 || (i.ClinicalHistory?.Trim().Length ?? 0) > 1000) return "The text is too long";
			return null;
		}

		/// Why the case cannot take that step now, or null. Steps: Received, Grossed, Reported, Cancelled.
		public static string? StepRefusal(HospitalPathCase c, HospitalPathStepInput i)
		{
			switch (i.To)
			{
				case HospitalPathStatus.Received:
					if (c.Status != HospitalPathStatus.Requested) return "This case is already received";
					return i.Containers != c.Containers ? "The containers received do not match the request: count them again" : null;
				case HospitalPathStatus.Grossed:
					if (c.Status != HospitalPathStatus.Received) return "Receive the specimen before describing it";
					if (string.IsNullOrWhiteSpace(i.Gross)) return "Write the gross description";
					if (i.Blocks is not (>= 1 and <= 200)) return "Write how many blocks were cut";
					return i.Gross.Trim().Length > 2000 ? "The text is too long" : null;
				case HospitalPathStatus.Reported:
					if (c.Status != HospitalPathStatus.Grossed) return "Describe the specimen before signing the report";
					if (string.IsNullOrWhiteSpace(i.Microscopic)) return "Write the microscopic description";
					if (string.IsNullOrWhiteSpace(i.Diagnosis)) return "Write the diagnosis";
					return i.Microscopic.Trim().Length > 4000 || i.Diagnosis.Trim().Length > 1000 ? "The text is too long" : null;
				case HospitalPathStatus.Cancelled:
					if (c.Status != HospitalPathStatus.Requested) return "Only a request not yet received is cancelled";
					return string.IsNullOrWhiteSpace(i.Reason) ? "Write why the request is cancelled" : null;
				default:
					return "not_found";
			}
		}

		public static string? AmendRefusal(HospitalPathCase c, string? text, string? reason)
		{
			if (c.Status != HospitalPathStatus.Reported) return "Only a signed report is amended";
			if (string.IsNullOrWhiteSpace(text)) return "Write the amendment";
			if (string.IsNullOrWhiteSpace(reason)) return "Write why the report is amended";
			return text.Trim().Length > 2000 || reason.Trim().Length > 300 ? "The text is too long" : null;
		}

		public static bool Late(HospitalPathCase c, DateTime nowUtc) =>
			c.ReceivedAt is DateTime r && c.Status is HospitalPathStatus.Received or HospitalPathStatus.Grossed && nowUtc > r.AddDays(LateDays);
	}

	public sealed class HospitalPathologyService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPathologyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalPathCase> Cases(BusinessContext ctx) => _db.Set<HospitalPathCase>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		/// A new request; the caller's own permission is checked unless trusted (an endoscopy's biopsies, already checked there).
		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, HospitalPathRequestInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default, bool trusted = false)
		{
			if (!trusted && !await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalPathRules.RequestRefusal(input) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var nos = await Cases(ctx).Where(c => c.CaseNo.StartsWith("PATH-")).Select(c => c.CaseNo).ToListAsync(ct);
			int n = 0; foreach (var no in nos) if (int.TryParse(no.AsSpan(5), out var v) && v > n) n = v;
			var c = new HospitalPathCase
			{
				CompanyId = ctx.CompanyId, CaseNo = "PATH-" + (n + 1).ToString("000000"), PatientId = input.PatientId, EncounterId = input.EncounterId, Source = input.Source!,
				SourceRefId = input.SourceRefId, Specimen = Clip(input.Specimen, 200)!, Site = Clip(input.Site, 200), ClinicalHistory = Clip(input.ClinicalHistory, 1000),
				Containers = input.Containers, Status = HospitalPathStatus.Requested, RequestedAt = nowUtc, RequestedByEmployeeId = employeeId,
			};
			_db.Add(c);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> StepAsync(BusinessContext ctx, int id, HospitalPathStepInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			bool cancel = input.To == HospitalPathStatus.Cancelled;
			if (!await _access.CanAsync(ctx, cancel ? HospitalActions.Consult : HospitalActions.Lab, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (HospitalPathRules.StepRefusal(c, input) is string why) return HospitalResult.Fail(why);
			switch (input.To)
			{
				case HospitalPathStatus.Received: c.ReceivedAt = nowUtc; c.ReceivedByEmployeeId = employeeId; break;
				case HospitalPathStatus.Grossed: c.Gross = Clip(input.Gross, 2000); c.Blocks = input.Blocks; c.GrossedAt = nowUtc; c.GrossedByEmployeeId = employeeId; break;
				case HospitalPathStatus.Reported: c.Microscopic = Clip(input.Microscopic, 4000); c.Diagnosis = Clip(input.Diagnosis, 1000); c.Malignant = input.Malignant; c.ReportedAt = nowUtc; c.ReportedByEmployeeId = employeeId; break;
				case HospitalPathStatus.Cancelled: c.CancelReason = Clip(input.Reason, 300); break;
			}
			c.Status = input.To!;
			// Status is a concurrency token: only the first step from the state it was read in lands (two hands on one case)
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateConcurrencyException) { return HospitalResult.Fail("Someone else changed this case: open it again"); }
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> AmendAsync(BusinessContext ctx, int id, string? text, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Lab, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (HospitalPathRules.AmendRefusal(c, text, reason) is string why) return HospitalResult.Fail(why);
			var a = new HospitalPathAmendment { CompanyId = ctx.CompanyId, CaseId = c.ID, Text = Clip(text, 2000)!, Reason = Clip(reason, 300)!, At = nowUtc, ByEmployeeId = employeeId };
			_db.Add(a);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(a.ID);
		}

		public async Task<IReadOnlyList<HospitalPathAmendment>> AmendmentsAsync(BusinessContext ctx, int caseId, CancellationToken ct = default) =>
			await _db.Set<HospitalPathAmendment>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.CaseId == caseId).OrderBy(a => a.At).ToListAsync(ct);

		public async Task<(IReadOnlyList<HospitalPathRow> Rows, int Total, int Open, int Late, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, int? patientId, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalPathRow>(), 0, 0, 0, true);
			try
			{
				var query = from c in Cases(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							where p.CompanyId == ctx.CompanyId
							select new { c, p };
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.c.Status == status);
				if (patientId is int pid) query = query.Where(x => x.c.PatientId == pid);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.c.CaseNo.Contains(s) || x.c.Specimen.Contains(s) || x.p.Name.Contains(s) || x.p.NameEn.Contains(s) || x.p.Mrn.Contains(s)); }
				int total = await query.CountAsync(ct);
				var openQ = Cases(ctx).Where(c => c.Status == HospitalPathStatus.Requested || c.Status == HospitalPathStatus.Received || c.Status == HospitalPathStatus.Grossed);
				int open = await openQ.CountAsync(ct);
				var lateFrom = nowUtc.AddDays(-HospitalPathRules.LateDays);
				int late = await openQ.CountAsync(c => c.Status != HospitalPathStatus.Requested && c.ReceivedAt < lateFrom, ct);
				var rows = await query.OrderBy(x => x.c.Status == HospitalPathStatus.Reported || x.c.Status == HospitalPathStatus.Cancelled).ThenByDescending(x => x.c.ID)
					.Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var ids = rows.Select(x => x.c.ID).ToList();
				var amends = await _db.Set<HospitalPathAmendment>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && ids.Contains(a.CaseId)).GroupBy(a => a.CaseId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.SelectMany(x => new[] { x.c.ReportedByEmployeeId, x.c.RequestedByEmployeeId }), ct);
				HospitalFace? F(int? id) => id is int i ? faces.GetValueOrDefault(i) : null;
				return (rows.Select(x => new HospitalPathRow(x.c, x.p.Name, x.p.NameEn, x.p.Mrn, F(x.c.ReportedByEmployeeId), F(x.c.RequestedByEmployeeId), amends.GetValueOrDefault(x.c.ID), HospitalPathRules.Late(x.c, nowUtc))).ToList(), total, open, late, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalPathRow>(), 0, 0, 0, false); }
		}
	}
}
