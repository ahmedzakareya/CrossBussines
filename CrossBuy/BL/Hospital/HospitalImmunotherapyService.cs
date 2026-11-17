using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H97 — allergen immunotherapy: the build-up schedule (vials 1:1000 → 1:1, 0.05 → 0.50 mL on each), the next
	// dose the schedule allows, giving it (and its 30-minute observation), its reaction, a course held by a systemic
	// reaction and resumed by the doctor two steps lower.
	// H174 — register rule "immunotherapy" (H171): how many steps lower a held course resumes (2) and the wheal that makes
	// a large local reaction (25 mm) are the clinic's approved protocol values, not constants.
	// ==========================================================================================
	/// H174: the clinic's approved protocol values; the defaults are the published ones.
	public sealed record HospitalImmunoProtocol(int RestartStepsDown = 2, int LargeLocalMm = 25);
	public sealed record HospitalImmunoNext(string? Vial, decimal? DoseMl, string? Refusal, object[]? Args);
	public sealed record HospitalImmunoCourseView(HospitalImmunoCourse Course, IReadOnlyList<HospitalImmunoDose> Doses, HospitalImmunoNext Next);

	public sealed class HospitalImmunoReactionInput
	{
		public string? Reaction { get; set; }
		public int? WhealMm { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalImmunoRules
	{
		public static readonly IReadOnlyList<string> Vials = new[] { "1:1000", "1:100", "1:10", "1:1" };
		public static readonly IReadOnlyList<decimal> Steps = new[] { 0.05m, 0.10m, 0.15m, 0.20m, 0.30m, 0.40m, 0.50m };
		public const int LargeLocalMm = 25;
		public static readonly TimeSpan Observation = TimeSpan.FromMinutes(30);

		/// The schedule as one line: (vial, dose) in order.
		public static IReadOnlyList<(string Vial, decimal Ml)> Schedule() => Vials.SelectMany(v => Steps.Select(s => (v, s))).ToList();

		private static int IndexOf(string vial, decimal ml) => Schedule().ToList().FindIndex(x => x.Vial == vial && x.Ml == ml);

		/// The next dose the schedule allows, or why none can be given now.
		public static HospitalImmunoNext Next(HospitalImmunoCourse c, IReadOnlyList<HospitalImmunoDose> doses, HospitalImmunoProtocol? protocol = null)
		{
			protocol ??= new HospitalImmunoProtocol();
			if (c.Status == HospitalImmunoStatus.Stopped) return new(null, null, "This course is stopped", null);
			if (c.Status == HospitalImmunoStatus.Held) return new(null, null, "This course is held after a systemic reaction: the doctor must resume it first", null);
			var sched = Schedule();
			var last = doses.OrderBy(d => d.Seq).LastOrDefault();
			if (last == null) return new(sched[0].Vial, sched[0].Ml, null, null);
			if (last.Reaction == HospitalImmunoReaction.Pending) return new(null, null, "Record the reaction to dose {0} first", new object[] { last.Seq });
			int i = IndexOf(last.Vial, last.DoseMl);
			if (i < 0) i = 0;
			int next = last.Reaction switch
			{
				HospitalImmunoReaction.LargeLocal => i,               // repeat the same dose
				HospitalImmunoReaction.Systemic => Math.Max(0, i - protocol.RestartStepsDown), // resumed: the approved number of steps lower
				_ => Math.Min(i + 1, sched.Count - 1),              // up the schedule; at the top it stays (maintenance)
			};
			return new(sched[next].Vial, sched[next].Ml, null, null);
		}

		/// The reaction as recorded: a local wheal of 25 mm or more is a large local reaction.
		public static (string? Reaction, string? Error) Classify(HospitalImmunoReactionInput i, DateTime nowUtc, HospitalImmunoDose d, HospitalImmunoProtocol? protocol = null)
		{
			protocol ??= new HospitalImmunoProtocol();
			if (d.Reaction != HospitalImmunoReaction.Pending) return (null, "This dose's reaction is already recorded");
			switch (i.Reaction)
			{
				case HospitalImmunoReaction.Systemic:
					if (string.IsNullOrWhiteSpace(i.Note)) return (null, "Describe the systemic reaction and its treatment");
					return (HospitalImmunoReaction.Systemic, null);
				case HospitalImmunoReaction.None:
					if (nowUtc < d.ObserveUntil) return (null, "The 30-minute observation is not over: the patient stays until {0}");
					return (HospitalImmunoReaction.None, null);
				case HospitalImmunoReaction.Local:
					if (i.WhealMm is not (> 0 and <= 200)) return (null, "Write the local wheal in mm");
					if (nowUtc < d.ObserveUntil) return (null, "The 30-minute observation is not over: the patient stays until {0}");
					return (i.WhealMm >= protocol.LargeLocalMm ? HospitalImmunoReaction.LargeLocal : HospitalImmunoReaction.Local, null);
				default: return (null, "Choose the reaction");
			}
		}
	}

	public sealed class HospitalImmunotherapyService
	{
		public const string NoTables = "Immunotherapy is not installed yet (hospital_h97_allergy.sql).";

		/// H174: the clinic's approved protocol values (register rule "immunotherapy").
		private async Task<HospitalImmunoProtocol> ProtocolAsync(BusinessContext ctx, CancellationToken ct) => new(
			(int)await HospitalClinicalRuleCatalog.ParamAsync(_db, ctx.CompanyId, HospitalClinicalRuleCatalog.Immunotherapy, "restartStepsDown", ct),
			(int)await HospitalClinicalRuleCatalog.ParamAsync(_db, ctx.CompanyId, HospitalClinicalRuleCatalog.Immunotherapy, "largeLocalMm", ct));
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalImmunotherapyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private Task<bool> CanAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		public async Task<(IReadOnlyList<HospitalImmunoCourseView> Courses, bool Ready)> PatientAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try
			{
				var cs = await Q<HospitalImmunoCourse>(ctx).Where(c => c.PatientId == patientId).OrderByDescending(c => c.ID).ToListAsync(ct);
				var ids = cs.Select(c => c.ID).ToList();
				var ds = await Q<HospitalImmunoDose>(ctx).Where(d => ids.Contains(d.CourseId)).OrderBy(d => d.Seq).ToListAsync(ct);
				var pr = await ProtocolAsync(ctx, ct);
				return (cs.Select(c => { var mine = ds.Where(d => d.CourseId == c.ID).ToList(); return new HospitalImmunoCourseView(c, mine, HospitalImmunoRules.Next(c, mine, pr)); }).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalImmunoCourseView>(), false); }
		}

		public async Task<HospitalResult> StartAsync(BusinessContext ctx, int patientId, string? extract, string? extractEn, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(extract) || string.IsNullOrWhiteSpace(extractEn)) return HospitalResult.Fail("Write the extract, in Arabic and in English");
			try
			{
				if (!await Q<HospitalPatient>(ctx).AnyAsync(p => p.ID == patientId, ct)) return HospitalResult.Fail("not_found");
				var name = extract.Trim();
				if (await Q<HospitalImmunoCourse>(ctx).AnyAsync(c => c.PatientId == patientId && c.Extract == name && c.Status != HospitalImmunoStatus.Stopped, ct))
					return HospitalResult.Fail("This patient already has a course of this extract");
				var c = new HospitalImmunoCourse { CompanyId = ctx.CompanyId, PatientId = patientId, Extract = Clip(extract, 150)!, ExtractEn = Clip(extractEn, 150)!, StartedAt = nowUtc, StartedByEmployeeId = employeeId };
				_db.Add(c);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Gives the dose the schedule allows now (never another); its observation runs 30 minutes. Returns the dose id.
		public async Task<HospitalResult> GiveAsync(BusinessContext ctx, int courseId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var c = await Q<HospitalImmunoCourse>(ctx).FirstOrDefaultAsync(x => x.ID == courseId, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				var doses = await Q<HospitalImmunoDose>(ctx).Where(d => d.CourseId == c.ID).ToListAsync(ct);
				var next = HospitalImmunoRules.Next(c, doses, await ProtocolAsync(ctx, ct));
				if (next.Refusal != null) return HospitalResult.Fail(next.Refusal, next.Args ?? Array.Empty<object>());
				var d = new HospitalImmunoDose
				{
					CompanyId = ctx.CompanyId, CourseId = c.ID, PatientId = c.PatientId, Seq = doses.Count == 0 ? 1 : doses.Max(x => x.Seq) + 1, Vial = next.Vial!, DoseMl = next.DoseMl!.Value,
					GivenAt = nowUtc, GivenByEmployeeId = employeeId, ObserveUntil = nowUtc + HospitalImmunoRules.Observation,
				};
				_db.Add(d);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ReactionAsync(BusinessContext ctx, int doseId, HospitalImmunoReactionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var d = await Q<HospitalImmunoDose>(ctx).FirstOrDefaultAsync(x => x.ID == doseId, ct);
				if (d == null) return HospitalResult.Fail("not_found");
				var (kind, err) = HospitalImmunoRules.Classify(input, nowUtc, d, await ProtocolAsync(ctx, ct));
				if (err != null) return HospitalResult.Fail(err, HospitalNursingService.UtcToWard(d.ObserveUntil).ToString("HH:mm"));
				d.Reaction = kind!; d.WhealMm = kind is HospitalImmunoReaction.Local or HospitalImmunoReaction.LargeLocal ? input.WhealMm : null;
				d.ReactionNote = Clip(input.Note, 300); d.ReactionAt = nowUtc; d.ReactionByEmployeeId = employeeId;
				if (kind == HospitalImmunoReaction.Systemic)
				{
					var c = await Q<HospitalImmunoCourse>(ctx).FirstAsync(x => x.ID == d.CourseId, ct);
					c.Status = HospitalImmunoStatus.Held; c.StatusNote = Clip(input.Note, 300); c.UpdatedAt = nowUtc; c.UpdatedByEmployeeId = employeeId;
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The doctor resumes a held course (two steps lower) or stops a course — with the reason.
		public async Task<HospitalResult> DecideAsync(BusinessContext ctx, int courseId, bool resume, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write the reason");
			try
			{
				var c = await Q<HospitalImmunoCourse>(ctx).FirstOrDefaultAsync(x => x.ID == courseId, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				if (resume && c.Status != HospitalImmunoStatus.Held) return HospitalResult.Fail("Only a held course is resumed");
				if (!resume && c.Status == HospitalImmunoStatus.Stopped) return HospitalResult.Fail("This course is stopped");
				c.Status = resume ? HospitalImmunoStatus.Active : HospitalImmunoStatus.Stopped; c.StatusNote = Clip(note, 300); c.UpdatedAt = nowUtc; c.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
