using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H89 — physiotherapy. A doctor refers; the therapist assesses (the pain and one standard outcome measure);
	// sessions are recorded; the progress is judged against the measure's minimal clinically important difference (the
	// change a patient actually notices), never by the therapist ticking "improved"; the episode is discharged with why.
	// ==========================================================================================
	public sealed class HospitalRehabReferInput
	{
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public string? Region { get; set; }
		public string? Problem { get; set; }
		public string? Precautions { get; set; }
		public int PlannedSessions { get; set; }
		public int PerWeek { get; set; }
	}

	public sealed class HospitalRehabAssessInput
	{
		public string? MeasureCode { get; set; }
		public decimal? BaselineScore { get; set; }
		public int? BaselinePain { get; set; }
		public string? Assessment { get; set; }
		public List<string>? Goals { get; set; }
	}

	public sealed class HospitalRehabSessionInput
	{
		public int EpisodeId { get; set; }
		public DateTime? Date { get; set; }
		public string? Attendance { get; set; }
		public int? Minutes { get; set; }
		public List<string>? Interventions { get; set; }
		public int? PainBefore { get; set; }
		public int? PainAfter { get; set; }
		public decimal? Score { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalRehabGoalInput
	{
		public int EpisodeId { get; set; }
		public string? Text { get; set; }
		public DateTime? TargetDate { get; set; }
	}

	public sealed class HospitalRehabEndInput
	{
		public bool Cancel { get; set; }
		public string? Reason { get; set; }
		public string? Summary { get; set; }
	}

	public static class HospitalRehabWarning
	{
		public const string Unassessed = "Unassessed";
		public const string TwoNoShows = "TwoNoShows";
		public const string NoProgress = "NoProgress";
		public const string OverPlan = "OverPlan";
		public const string PainWorse = "PainWorse";
		public const string Worse = "Worse";
	}

	/// Improved / unchanged / worse, by the measure's MCID.
	public static class HospitalRehabTrend
	{
		public const string Improved = "Improved";
		public const string Same = "Same";
		public const string Worse = "Worse";
	}

	public sealed record HospitalRehabProgress(int Attended, int NoShows, int Cancelled, int? AttendancePct, decimal? LatestScore, decimal? Change, string? Trend, int? LatestPain, int? PainChange,
		IReadOnlyList<string> Warnings);
	public sealed record HospitalRehabRow(HospitalRehabEpisode Episode, string Name, string NameEn, string Mrn, HospitalRehabProgress Progress, DateTime? LastSession);
	public sealed record HospitalRehabPage(HospitalRehabEpisode Episode, string Name, string NameEn, string Mrn, string? Photo, IReadOnlyList<HospitalRehabGoal> Goals,
		IReadOnlyList<HospitalRehabSession> Sessions, HospitalRehabProgress Progress, IReadOnlyDictionary<int, string> People);

	public static class HospitalRehabRules
	{
		public const int NoProgressAfter = 6;
		public const int UnassessedDays = 3;

		/// The improvement from the baseline, positive when better whichever way the measure runs.
		public static decimal Improvement(HospitalRehabMeasureDef m, decimal baseline, decimal current) => m.HigherIsBetter ? current - baseline : baseline - current;

		public static string Trend(HospitalRehabMeasureDef m, decimal improvement) =>
			improvement >= m.Mcid ? HospitalRehabTrend.Improved : improvement <= -m.Mcid ? HospitalRehabTrend.Worse : HospitalRehabTrend.Same;

		public static HospitalRehabProgress Progress(HospitalRehabEpisode e, IReadOnlyList<HospitalRehabSession> sessions, DateTime today)
		{
			var ordered = sessions.OrderBy(s => s.SessionDate).ThenBy(s => s.ID).ToList();
			var attended = ordered.Where(s => s.Attendance == HospitalRehabAttendance.Attended).ToList();
			int noShows = ordered.Count(s => s.Attendance == HospitalRehabAttendance.NoShow);
			int cancelled = ordered.Count(s => s.Attendance == HospitalRehabAttendance.Cancelled);
			int? pct = attended.Count + noShows == 0 ? null : (int)Math.Round(attended.Count * 100m / (attended.Count + noShows));
			var def = HospitalRehabMeasure.Of(e.MeasureCode);
			var latest = attended.LastOrDefault(s => s.Score != null)?.Score;
			decimal? change = def != null && e.BaselineScore != null && latest != null ? Improvement(def, e.BaselineScore.Value, latest.Value) : null;
			string? trend = def != null && change != null ? Trend(def, change.Value) : null;
			var latestPain = attended.LastOrDefault(s => s.PainBefore != null)?.PainBefore;
			int? painChange = e.BaselinePain != null && latestPain != null ? e.BaselinePain - latestPain : null;

			var w = new List<string>();
			if (e.Status == HospitalRehabStatus.Referred && (today - e.ReferredAt.Date).TotalDays > UnassessedDays) w.Add(HospitalRehabWarning.Unassessed);
			if (e.Status == HospitalRehabStatus.Active)
			{
				var kept = ordered.Where(s => s.Attendance != HospitalRehabAttendance.Cancelled).ToList();
				if (kept.Count >= 2 && kept.TakeLast(2).All(s => s.Attendance == HospitalRehabAttendance.NoShow)) w.Add(HospitalRehabWarning.TwoNoShows);
				if (attended.Count >= NoProgressAfter && trend != HospitalRehabTrend.Improved) w.Add(HospitalRehabWarning.NoProgress);
				if (attended.Count > e.PlannedSessions) w.Add(HospitalRehabWarning.OverPlan);
				if (painChange <= -(int)HospitalRehabMeasure.PainMcid) w.Add(HospitalRehabWarning.PainWorse);
				if (trend == HospitalRehabTrend.Worse) w.Add(HospitalRehabWarning.Worse);
			}
			return new HospitalRehabProgress(attended.Count, noShows, cancelled, pct, latest, change, trend, latestPain, painChange, w);
		}

		public static string? ReferRefusal(HospitalRehabReferInput i)
		{
			if (!HospitalRehabRegion.All.Contains(i.Region ?? "")) return "Choose the body region";
			if (string.IsNullOrWhiteSpace(i.Problem)) return "Write the problem to treat";
			if (i.PlannedSessions is < 1 or > 60) return "The planned sessions are between 1 and 60";
			if (i.PerWeek is < 1 or > 7) return "The sessions a week are between 1 and 7";
			return null;
		}

		public static string? AssessRefusal(HospitalRehabEpisode e, HospitalRehabAssessInput i)
		{
			if (e.Status != HospitalRehabStatus.Referred) return "This referral is already assessed or closed";
			var def = HospitalRehabMeasure.Of(i.MeasureCode);
			if (def == null) return "Choose the outcome measure";
			if (i.BaselineScore is not decimal s) return "Write the baseline score";
			if (s < def.Min || s > def.Max) return "The score is outside the measure's range";
			if (i.BaselinePain is < 0 or > 10) return "The pain is between 0 and 10";
			if (i.Goals != null && i.Goals.Any(g => g != null && g.Trim().Length > 300)) return "A goal is 300 characters at most";
			return null;
		}

		public static string? SessionRefusal(HospitalRehabEpisode e, HospitalRehabSessionInput i, DateTime day, DateTime today)
		{
			if (e.Status != HospitalRehabStatus.Active) return "Sessions are recorded on an active episode";
			if (day > today) return "A session is recorded on its day or after it";
			if (day < today.AddDays(-14)) return "A session can be written back two weeks at most";
			if (e.AssessedAt != null && day < HospitalNursingService.UtcToWard(e.AssessedAt.Value).Date) return "The day is before the assessment";
			if (!HospitalRehabAttendance.All.Contains(i.Attendance ?? "")) return "Choose whether the patient came";
			if (i.Attendance != HospitalRehabAttendance.Attended)
				return i.Minutes != null || i.Score != null || i.PainBefore != null || (i.Interventions?.Count ?? 0) > 0 ? "A missed session has no treatment to record" : null;
			if (i.Minutes is not int m || m < 5 || m > 240) return "Write the minutes of the session (5 to 240)";
			if (i.Interventions == null || i.Interventions.Count == 0) return "Choose what was done";
			if (i.Interventions.Any(x => !HospitalRehabIntervention.All.Contains(x))) return "Choose what was done from the list";
			if (i.PainBefore is < 0 or > 10 || i.PainAfter is < 0 or > 10) return "The pain is between 0 and 10";
			var def = HospitalRehabMeasure.Of(e.MeasureCode);
			if (i.Score != null && def != null && (i.Score < def.Min || i.Score > def.Max)) return "The score is outside the measure's range";
			return null;
		}

		public static string? EndRefusal(HospitalRehabEpisode e, HospitalRehabEndInput i)
		{
			if (i.Cancel)
			{
				if (e.Status != HospitalRehabStatus.Referred) return "Only a referral not yet assessed can be cancelled";
				if (string.IsNullOrWhiteSpace(i.Summary)) return "Write why the referral is cancelled";
				return null;
			}
			if (e.Status != HospitalRehabStatus.Active) return "Only an active episode is discharged";
			if (!HospitalRehabDischarge.All.Contains(i.Reason ?? "")) return "Choose why the patient is discharged";
			if (string.IsNullOrWhiteSpace(i.Summary)) return "Write the discharge summary";
			return null;
		}
	}

	public sealed class HospitalRehabService
	{
		public const string NoTables = "Physiotherapy is not installed yet (hospital_h89_rehab.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalRehabService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		// H119: with the billing service a completed session is charged; without it (a test's hand-made service) it is not
		private readonly HospitalBillingService? _billing;
		[ActivatorUtilitiesConstructor]
		public HospitalRehabService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) : this(db, access) { _billing = billing; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalRehabEpisode> Episodes(BusinessContext ctx) => _db.Set<HospitalRehabEpisode>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalRehabGoal> Goals(BusinessContext ctx) => _db.Set<HospitalRehabGoal>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalRehabSession> Sessions(BusinessContext ctx) => _db.Set<HospitalRehabSession>().Where(x => x.CompanyId == ctx.CompanyId);

		public async Task<bool> CanTreatAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
		public Task<bool> CanReferAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		public async Task<HospitalResult> ReferAsync(BusinessContext ctx, HospitalRehabReferInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanReferAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalRehabRules.ReferRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				if (!await _db.HospitalPatients.AsNoTracking().AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct)) return HospitalResult.Fail("not_found");
				if (input.AdmissionId is int aid && !await _db.HospitalAdmissions.AsNoTracking().AnyAsync(a => a.ID == aid && a.CompanyId == ctx.CompanyId && a.PatientId == input.PatientId, ct)) return HospitalResult.Fail("not_found");
				if (await Episodes(ctx).AnyAsync(x => x.PatientId == input.PatientId && x.Region == input.Region && (x.Status == HospitalRehabStatus.Referred || x.Status == HospitalRehabStatus.Active), ct))
					return HospitalResult.Fail("The patient already has an open episode for this region");
				var nos = await Episodes(ctx).Select(x => x.EpisodeNo).ToListAsync(ct);
				var row = new HospitalRehabEpisode
				{
					CompanyId = ctx.CompanyId, EpisodeNo = HospitalDentalPracticeRules.NextNo(nos, "PT-", 6), PatientId = input.PatientId, AdmissionId = input.AdmissionId, Status = HospitalRehabStatus.Referred,
					Region = input.Region!, Problem = Clip(input.Problem, 300)!, Precautions = Clip(input.Precautions, 300), PlannedSessions = input.PlannedSessions, PerWeek = input.PerWeek,
					ReferredAt = nowUtc, ReferredByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AssessAsync(BusinessContext ctx, int id, HospitalRehabAssessInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var e = await Episodes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				if (HospitalRehabRules.AssessRefusal(e, input) is string why) return HospitalResult.Fail(why);
				e.Status = HospitalRehabStatus.Active; e.AssessedAt = nowUtc; e.TherapistEmployeeId = employeeId; e.MeasureCode = input.MeasureCode; e.BaselineScore = input.BaselineScore;
				e.BaselinePain = input.BaselinePain; e.Assessment = Clip(input.Assessment, 1000);
				foreach (var g in (input.Goals ?? new()).Where(g => !string.IsNullOrWhiteSpace(g)))
					_db.Add(new HospitalRehabGoal { CompanyId = ctx.CompanyId, EpisodeId = e.ID, PatientId = e.PatientId, Text = g.Trim(), Status = HospitalRehabGoalStatus.Open, CreatedAt = nowUtc });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(e.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AddSessionAsync(BusinessContext ctx, HospitalRehabSessionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var e = await Episodes(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.EpisodeId, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				var today = Today(nowUtc); var day = (input.Date ?? today).Date;
				if (HospitalRehabRules.SessionRefusal(e, input, day, today) is string why) return HospitalResult.Fail(why);
				if (await Sessions(ctx).AnyAsync(s => s.EpisodeId == e.ID && s.SessionDate == day, ct)) return HospitalResult.Fail("A session is already recorded that day");
				bool came = input.Attendance == HospitalRehabAttendance.Attended;
				var row = new HospitalRehabSession
				{
					CompanyId = ctx.CompanyId, EpisodeId = e.ID, PatientId = e.PatientId, SessionDate = day, Attendance = input.Attendance!, Minutes = came ? input.Minutes : null,
					Interventions = came ? string.Join(",", input.Interventions!.Distinct()) : null, PainBefore = came ? input.PainBefore : null, PainAfter = came ? input.PainAfter : null,
					Score = came ? input.Score : null, Note = Clip(input.Note, 500), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				// H119: a session attended is charged with it; a no-show or a cancellation is not
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				await _db.SaveChangesAsync(ct);
				if (came && _billing != null && await HospitalSessionCharges.ChargeAsync(_db, _billing, ctx, e.PatientId, HospitalSessionServiceCodes.Physio, employeeId, nowUtc, ct) is { } line)
				{ row.BillLineId = line.ID; await _db.SaveChangesAsync(ct); }
				await tx.CommitAsync();
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AddGoalAsync(BusinessContext ctx, HospitalRehabGoalInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(input.Text)) return HospitalResult.Fail("Write the goal");
			if (input.TargetDate is DateTime t && t.Date < Today(nowUtc)) return HospitalResult.Fail("The target date has passed");
			try
			{
				var e = await Episodes(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.EpisodeId, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				if (e.Status != HospitalRehabStatus.Active) return HospitalResult.Fail("Goals are added to an active episode");
				var row = new HospitalRehabGoal { CompanyId = ctx.CompanyId, EpisodeId = e.ID, PatientId = e.PatientId, Text = Clip(input.Text, 300)!, TargetDate = input.TargetDate?.Date, Status = HospitalRehabGoalStatus.Open, CreatedAt = nowUtc };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> MarkGoalAsync(BusinessContext ctx, int goalId, string? status, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (status is not (HospitalRehabGoalStatus.Met or HospitalRehabGoalStatus.NotMet)) return HospitalResult.Fail("Choose met or not met");
			try
			{
				var g = await Goals(ctx).FirstOrDefaultAsync(x => x.ID == goalId, ct);
				if (g == null) return HospitalResult.Fail("not_found");
				if (g.Status != HospitalRehabGoalStatus.Open) return HospitalResult.Fail("This goal is already closed");
				g.Status = status; g.ClosedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(g.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> EndAsync(BusinessContext ctx, int id, HospitalRehabEndInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var e = await Episodes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				if (HospitalRehabRules.EndRefusal(e, input) is string why) return HospitalResult.Fail(why);
				e.Status = input.Cancel ? HospitalRehabStatus.Cancelled : HospitalRehabStatus.Discharged; e.EndedAt = nowUtc; e.EndReason = input.Cancel ? null : input.Reason; e.Summary = Clip(input.Summary, 1000);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(e.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The episodes: by default the open ones (waiting for assessment first, then the warnings), searched and paged.
		public async Task<(IReadOnlyList<HospitalRehabRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Today(nowUtc);
			try
			{
				var query = from e in Episodes(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on e.PatientId equals p.ID
							select new { e, p.Name, p.NameEn, p.Mrn, p.Phone };
				query = filter == "ended" ? query.Where(x => x.e.Status == HospitalRehabStatus.Discharged || x.e.Status == HospitalRehabStatus.Cancelled)
					: query.Where(x => x.e.Status == HospitalRehabStatus.Referred || x.e.Status == HospitalRehabStatus.Active);
				if (filter == "referred") query = query.Where(x => x.e.Status == HospitalRehabStatus.Referred);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.e.EpisodeNo.Contains(s) || x.Name.Contains(s) || x.NameEn.Contains(s) || x.Mrn.Contains(s) || (x.Phone != null && x.Phone.Contains(s)) || x.e.Problem.Contains(s)); }
				var all = await query.ToListAsync(ct);
				var ids = all.Select(x => x.e.ID).ToList();
				var sessions = await Sessions(ctx).AsNoTracking().Where(s => ids.Contains(s.EpisodeId)).ToListAsync(ct);
				var rows = all.Select(x =>
				{
					var mine = sessions.Where(s => s.EpisodeId == x.e.ID).ToList();
					return new HospitalRehabRow(x.e, x.Name, x.NameEn, x.Mrn, HospitalRehabRules.Progress(x.e, mine, today), mine.Count == 0 ? null : mine.Max(s => s.SessionDate));
				});
				if (filter == "warning") rows = rows.Where(r => r.Progress.Warnings.Count > 0);
				var ordered = (filter == "ended" ? rows.OrderByDescending(r => r.Episode.EndedAt) : rows.OrderBy(r => r.Episode.Status == HospitalRehabStatus.Referred ? 0 : 1)
					.ThenByDescending(r => r.Progress.Warnings.Count).ThenBy(r => r.Name)).ToList();
				return (ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalRehabRow>(), 0, false); }
		}

		public async Task<HospitalRehabPage?> PageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var e = await Episodes(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (e == null) return null;
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == e.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstAsync(ct);
				var goals = await Goals(ctx).AsNoTracking().Where(g => g.EpisodeId == id).OrderBy(g => g.ID).ToListAsync(ct);
				var sessions = await Sessions(ctx).AsNoTracking().Where(s => s.EpisodeId == id).OrderBy(s => s.SessionDate).ThenBy(s => s.ID).ToListAsync(ct);
				var eids = sessions.Where(s => s.CreatedByEmployeeId != null).Select(s => s.CreatedByEmployeeId!.Value)
					.Concat(new[] { e.ReferredByEmployeeId, e.TherapistEmployeeId }.Where(x => x != null).Select(x => x!.Value)).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(x => eids.Contains(x.ID) && x.EmpCompanyID == ctx.CompanyId)
					.Select(x => new { x.ID, x.FullName, x.FullNameEn }).ToDictionaryAsync(x => x.ID, x => DisplayName.Of(x.FullName ?? "", x.FullNameEn), ct);
				return new HospitalRehabPage(e, p.Name, p.NameEn, p.Mrn, p.PhotoPath, goals, sessions, HospitalRehabRules.Progress(e, sessions, Today(nowUtc)), people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
