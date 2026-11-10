using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H45 — incident reports & patient safety. The rules are HospitalIncidentRules' (no database).
	//   · Reporting (`read` — any staff member): kind, harm, where, the patient if any, what happened. Anonymous keeps
	//     no reporter at all. A moderate or severe harm goes straight to root-cause analysis, due in 45 days.
	//   · Quality management (`manage`): review, open an analysis, close — an analysis closes only with its root cause
	//     and its actions; anything else closes with its outcome.
	//   · The month's figures: reports, medication errors, falls per 1,000 patient-days (the bed stays' days in the
	//     month), open analyses and the nearest deadline.
	// ==========================================================================================
	public sealed class HospitalIncidentInput
	{
		public string Kind { get; set; } = ""; public string Harm { get; set; } = ""; public string Place { get; set; } = "";
		public int? WardId { get; set; } public int? PatientId { get; set; } public string? Description { get; set; } public bool Anonymous { get; set; }
	}

	public sealed record HospitalIncidentRow(HospitalIncident Row, string? Ward, string? WardEn, string? Patient, string? PatientEn, string? Mrn,
		string? Reporter, string? ReporterEn, string? ReporterPhoto, IReadOnlyList<HospitalIncidentFollowUp>? FollowUps = null);

	/// H54: a follow-up task of an incident, as the task module holds it now.
	public sealed record HospitalIncidentFollowUp(int TaskId, string Title, string Status, DateTime? Due, bool Overdue, string? Assignee, string? AssigneeEn, string? Photo);

	public sealed class HospitalIncidentFollowUpInput { public string? Title { get; set; } public int AssigneeEmployeeId { get; set; } public DateTime? DueOn { get; set; } }

	public sealed record HospitalIncidentBoard(IReadOnlyList<HospitalIncidentRow> Rows, int ThisMonth, int LastMonth, int MedicationErrors, int Falls,
		decimal PatientDays, decimal? FallsPer1000, int OpenRca, DateTime? NearestDue);

	public static class HospitalIncidentRules
	{
		public const int RcaDays = 45;

		public static bool Serious(string harm) => harm is HospitalIncidentHarm.Moderate or HospitalIncidentHarm.Severe;

		/// Where a new report starts: a serious harm is analysed at once.
		public static string InitialStatus(string harm) => Serious(harm) ? HospitalIncidentStatus.Rca : HospitalIncidentStatus.New;

		public static string? ReportRefusal(string kind, string harm, string place, int? wardId, string? description)
		{
			if (!HospitalIncidentKind.All.Contains(kind)) return "Choose the incident type";
			if (!HospitalIncidentHarm.All.Contains(harm)) return "Choose the harm level";
			if (!HospitalIncidentPlace.All.Contains(place)) return "Choose where it happened";
			if (place == HospitalIncidentPlace.Ward && wardId == null) return "Choose the ward";
			if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 10) return "Write what happened (a sentence at least)";
			return null;
		}

		/// Quality management's steps.
		public static bool CanMove(string from, string to, string harm) => (from, to) switch
		{
			(HospitalIncidentStatus.New, HospitalIncidentStatus.Review) => true,
			(HospitalIncidentStatus.New or HospitalIncidentStatus.Review, HospitalIncidentStatus.Rca) => true,
			(HospitalIncidentStatus.New or HospitalIncidentStatus.Review, HospitalIncidentStatus.Closed) => !Serious(harm),
			(HospitalIncidentStatus.Rca, HospitalIncidentStatus.Closed) => true,
			_ => false,
		};

		/// Why it cannot close: an analysis needs its root cause and actions, anything else its outcome.
		public static string? CloseRefusal(string status, string? rootCause, string? actions, string? outcome)
		{
			if (status == HospitalIncidentStatus.Rca)
			{
				if (string.IsNullOrWhiteSpace(rootCause)) return "Write the root cause before closing the analysis";
				if (string.IsNullOrWhiteSpace(actions)) return "Write the actions agreed before closing the analysis";
				return null;
			}
			return string.IsNullOrWhiteSpace(outcome) ? "Write the outcome before closing" : null;
		}

		/// The days of a stay that fall inside [from, to).
		public static decimal DaysIn(DateTime start, DateTime? end, DateTime from, DateTime to)
		{
			var a = start > from ? start : from; var b = (end ?? to) < to ? (end ?? to) : to;
			return b > a ? (decimal)(b - a).TotalDays : 0;
		}

		public static decimal? Per1000(int count, decimal patientDays) => patientDays <= 0 ? null : Math.Round(count * 1000m / patientDays, 1);

		public static string Number(int n) => "INC-" + n.ToString("0000");
	}

	public sealed class HospitalIncidentService
	{
		public const string NoTables = "The incident tables are not created yet (hospital_h45_incidents.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		private readonly CrossBuy.BL.ITaskService? _tasks;
		public HospitalIncidentService(CrossDbContext db, IHospitalAccessService access, CrossBuy.BL.ITaskService? tasks = null) { _db = db; _access = access; _tasks = tasks; }

		/// H54: why a follow-up task cannot be opened, or null.
		public static string? FollowUpRefusal(string status, string? title, int assigneeEmployeeId, DateTime? dueOn, DateTime todayWard)
		{
			if (status != HospitalIncidentStatus.Rca) return "Follow-up tasks are opened during the root-cause analysis";
			if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3) return "Write what is to be done";
			if (assigneeEmployeeId <= 0) return "Choose who will do it";
			if (dueOn is not { } d || d.Date < todayWard.Date) return "Give a due date from today on";
			return null;
		}

		/// H54: opens a task of the task module for an action the analysis agreed, and keeps the link. The title carries
		/// the incident's number, never the patient: the task list is seen more widely than the incident.
		public async Task<HospitalResult> AddFollowUpAsync(BusinessContext ctx, int id, HospitalIncidentFollowUpInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (_tasks == null || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var inc = await Incidents(ctx).AsNoTracking().FirstOrDefaultAsync(i => i.ID == id, ct);
			if (inc == null) return HospitalResult.Fail("not_found");
			if (FollowUpRefusal(inc.Status, input.Title, input.AssigneeEmployeeId, input.DueOn, HospitalNursingService.UtcToWard(nowUtc)) is string why) return HospitalResult.Fail(why);
			if (!await _db.Employee.AnyAsync(e => e.ID == input.AssigneeEmployeeId && e.EmpCompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var title = input.Title!.Trim();
			var (ok, error, taskId) = await _tasks.SaveAsync(ctx.CompanyId, new CrossBuy.BL.TaskSaveInput
			{
				Title = inc.IncidentNo + " — " + title[..Math.Min(180, title.Length)],
				Description = "Follow-up of the root-cause analysis of incident " + inc.IncidentNo + ".",
				AssigneeEmployeeId = input.AssigneeEmployeeId, DueDate = input.DueOn!.Value.Date, Priority = "High", Category = "Quality",
			}, me);
			if (!ok) return HospitalResult.Fail(error ?? "The task could not be created");
			try
			{
				_db.Add(new HospitalIncidentTask { CompanyId = ctx.CompanyId, IncidentId = id, TaskId = taskId, CreatedAt = nowUtc, CreatedByEmployeeId = me });
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			return HospitalResult.Success(taskId);
		}

		private async Task<Dictionary<int, List<HospitalIncidentFollowUp>>> FollowUpsAsync(BusinessContext ctx, IReadOnlyCollection<int> incidentIds, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var links = await _db.Set<HospitalIncidentTask>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && incidentIds.Contains(l.IncidentId)).ToListAsync(ct);
				var tids = links.Select(l => l.TaskId).ToList();
				var tasks = await _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && tids.Contains(t.ID))
					.Select(t => new { t.ID, t.Title, t.Status, t.DueDate, t.AssigneeEmployeeId }).ToDictionaryAsync(t => t.ID, ct);
				var eids = tasks.Values.Select(t => t.AssigneeEmployeeId).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
				var today = HospitalNursingService.UtcToWard(nowUtc).Date;
				return links.Where(l => tasks.ContainsKey(l.TaskId)).GroupBy(l => l.IncidentId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.ID).Select(l =>
				{
					var t = tasks[l.TaskId]; var p = people.GetValueOrDefault(t.AssigneeEmployeeId);
					return new HospitalIncidentFollowUp(t.ID, t.Title, t.Status, t.DueDate, t.Status != "Done" && t.DueDate?.Date < today, p?.FullName, p?.FullNameEn ?? p?.FullName, photos.GetValueOrDefault(t.AssigneeEmployeeId));
				}).ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }
		}

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalIncident> Incidents(BusinessContext ctx) => _db.Set<HospitalIncident>().Where(i => i.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<(HospitalIncidentBoard Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var local = HospitalNursingService.UtcToWard(nowUtc);
			DateTime monthWard = new(local.Year, local.Month, 1);
			DateTime from = HospitalNursingService.WardToUtc(monthWard), to = HospitalNursingService.WardToUtc(monthWard.AddMonths(1)), prev = HospitalNursingService.WardToUtc(monthWard.AddMonths(-1));
			// patient-days this month: every bed stay's days inside it
			var stays = await _db.HospitalBedStays.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.StartedAt < to && (s.EndedAt == null || s.EndedAt > from))
				.Select(s => new { s.StartedAt, s.EndedAt }).ToListAsync(ct);
			decimal days = stays.Sum(s => HospitalIncidentRules.DaysIn(s.StartedAt, s.EndedAt, from, nowUtc < to ? nowUtc : to));
			try
			{
				var month = await Incidents(ctx).AsNoTracking().Where(i => i.ReportedAt >= prev && i.ReportedAt < to).Select(i => new { i.Kind, i.ReportedAt }).ToListAsync(ct);
				var open = await Incidents(ctx).AsNoTracking().Where(i => i.Status == HospitalIncidentStatus.Rca).Select(i => i.RcaDueOn).ToListAsync(ct);
				var rows = await Incidents(ctx).AsNoTracking().OrderByDescending(i => i.ID).Take(100).ToListAsync(ct);
				var wids = rows.Where(r => r.WardId != null).Select(r => r.WardId!.Value).Distinct().ToList();
				var wards = await _db.HospitalWards.AsNoTracking().Where(w => wids.Contains(w.ID)).ToDictionaryAsync(w => w.ID, ct);
				var pids = rows.Where(r => r.PatientId != null).Select(r => r.PatientId!.Value).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID) && p.CompanyId == ctx.CompanyId).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
				var eids = rows.Where(r => r.ReporterEmployeeId != null).Select(r => r.ReporterEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
				var follow = await FollowUpsAsync(ctx, rows.Select(r => r.ID).ToList(), nowUtc, ct);   // H54
				var list = rows.Select(r =>
				{
					var w = r.WardId is int wid ? wards.GetValueOrDefault(wid) : null; var p = r.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					var e = r.ReporterEmployeeId is int eid ? people.GetValueOrDefault(eid) : null;
					return new HospitalIncidentRow(r, w?.Name, w?.NameEn ?? w?.Name, p?.Name, p?.NameEn, p?.Mrn, e?.FullName, e?.FullNameEn ?? e?.FullName,
						r.ReporterEmployeeId is int x ? photos.GetValueOrDefault(x) : null, follow.GetValueOrDefault(r.ID));
				}).ToList();
				var cur = month.Where(i => i.ReportedAt >= from).ToList();
				int falls = cur.Count(i => i.Kind == HospitalIncidentKind.Fall);
				return (new HospitalIncidentBoard(list, cur.Count, month.Count - cur.Count, cur.Count(i => i.Kind == HospitalIncidentKind.MedicationError), falls,
					Math.Round(days, 1), HospitalIncidentRules.Per1000(falls, days), open.Count, open.Where(d => d != null).Min()), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				return (new HospitalIncidentBoard(Array.Empty<HospitalIncidentRow>(), 0, 0, 0, 0, Math.Round(days, 1), null, 0, null), false);
			}
		}

		/// A report from any staff member; anonymous keeps no reporter.
		public async Task<HospitalResult> ReportAsync(BusinessContext ctx, HospitalIncidentInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Read, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalIncidentRules.ReportRefusal(input.Kind, input.Harm, input.Place, input.WardId, input.Description) is string why) return HospitalResult.Fail(why);
			int? ward = input.Place == HospitalIncidentPlace.Ward ? input.WardId : null;
			if (ward is int w && !await _db.HospitalWards.AnyAsync(x => x.ID == w && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.PatientId is int p && !await _db.HospitalPatients.AnyAsync(x => x.ID == p && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				for (int attempt = 0; ; attempt++)
				{
					var n = await Incidents(ctx).CountAsync(ct) + 1 + attempt;
					var status = HospitalIncidentRules.InitialStatus(input.Harm);
					var i = new HospitalIncident
					{
						CompanyId = ctx.CompanyId, IncidentNo = HospitalIncidentRules.Number(n), Kind = input.Kind, Harm = input.Harm, Status = status, Place = input.Place, WardId = ward,
						PatientId = input.PatientId, Description = Clip(input.Description, 2000)!, ReportedAt = nowUtc, Anonymous = input.Anonymous,
						ReporterEmployeeId = input.Anonymous ? null : employeeId,
						RcaDueOn = status == HospitalIncidentStatus.Rca ? HospitalNursingService.UtcToWard(nowUtc).Date.AddDays(HospitalIncidentRules.RcaDays) : null,
					};
					_db.Add(i);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(i.ID); }
					catch (DbUpdateException) when (attempt < 3) { _db.ChangeTracker.Clear(); }   // two reports took the same number: the next one
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Quality management moves a report on: review, analysis, close (with what closing needs).
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, string to, string? rootCause, string? actions, string? outcome, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var i = await Incidents(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (i == null) return HospitalResult.Fail("not_found");
				if (!HospitalIncidentRules.CanMove(i.Status, to, i.Harm))
					return HospitalResult.Fail(to == HospitalIncidentStatus.Closed && HospitalIncidentRules.Serious(i.Harm) ? "A serious harm is closed only through its root-cause analysis" : "This report cannot move to that step from where it is");
				if (to == HospitalIncidentStatus.Closed && HospitalIncidentRules.CloseRefusal(i.Status, rootCause, actions, outcome) is string why) return HospitalResult.Fail(why);
				switch (to)
				{
					case HospitalIncidentStatus.Review: i.ReviewedAt = nowUtc; i.ReviewedByEmployeeId = employeeId; break;
					case HospitalIncidentStatus.Rca: i.RcaDueOn = HospitalNursingService.UtcToWard(nowUtc).Date.AddDays(HospitalIncidentRules.RcaDays); i.ReviewedAt ??= nowUtc; i.ReviewedByEmployeeId ??= employeeId; break;
					case HospitalIncidentStatus.Closed:
						i.RootCause = Clip(rootCause, 2000) ?? i.RootCause; i.Actions = Clip(actions, 2000) ?? i.Actions; i.Outcome = Clip(outcome, 1000);
						i.ClosedAt = nowUtc; i.ClosedByEmployeeId = employeeId; break;
				}
				i.Status = to;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(i.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The analysis's notes, saved while it is open.
		public async Task<HospitalResult> SaveRcaAsync(BusinessContext ctx, int id, string? rootCause, string? actions, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var i = await Incidents(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (i == null) return HospitalResult.Fail("not_found");
				if (i.Status != HospitalIncidentStatus.Rca) return HospitalResult.Fail("Only an open analysis is written");
				i.RootCause = Clip(rootCause, 2000); i.Actions = Clip(actions, 2000);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(i.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
