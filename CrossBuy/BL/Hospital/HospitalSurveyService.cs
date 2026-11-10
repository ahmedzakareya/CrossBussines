using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H65 — satisfaction surveys. Questions (the administrator); a survey taken for one recent episode (anyone
	// who reads the hospital hands the tablet over); the results — NPS, the average, each question's average — and the
	// follow-up a low survey opens (the administrator closes it with what was done).
	// ==========================================================================================

	public sealed class HospitalSurveyQuestionInput { public string? Code { get; set; } public string? Kind { get; set; } public string? Text { get; set; } public string? TextEn { get; set; } public int SortOrder { get; set; } public bool IsActive { get; set; } = true; }

	public sealed class HospitalSurveyInput
	{
		public string? Kind { get; set; } public int EpisodeId { get; set; }
		public Dictionary<int, int> Scores { get; set; } = new();
		public int? Recommend { get; set; } public string? Comment { get; set; } public bool Anonymous { get; set; }
	}

	/// An episode a survey can be taken for: the kind, its id, the patient, when it ended, and who cared.
	public sealed record HospitalSurveyEpisode(string Kind, int Id, int PatientId, string PatientName, string PatientNameEn, string Mrn, DateTime EndedAt, string Label);

	public sealed record HospitalSurveyRow(HospitalSurveyResponse Response, string PatientName, string PatientNameEn, string Mrn, string? Collector, string? CollectorEn, string? FollowedBy, string? FollowedByEn);

	public sealed record HospitalSurveyStats(int Responses, int? Nps, decimal? Average, int OpenFollowUps, IReadOnlyList<(HospitalSurveyQuestion Question, decimal? Average, int Count)> ByQuestion);

	public static class HospitalSurveyRules
	{
		/// How long after an episode a survey may still be taken, and when a survey needs a follow-up.
		public const int WindowDays = 14;
		public const decimal LowAverage = 2.5m; public const int LowRecommend = 6;

		public static string? QuestionRefusal(HospitalSurveyQuestionInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Code) || i.Code.Trim().Length > 20) return "Give the question a code of up to 20 characters";
			if (!HospitalSurveyKind.All.Contains(i.Kind ?? "")) return "Choose the kind of care";
			if (string.IsNullOrWhiteSpace(i.Text) || string.IsNullOrWhiteSpace(i.TextEn)) return "Write the question in Arabic and English";
			if (i.Text!.Trim().Length > 300 || i.TextEn!.Trim().Length > 300) return "A text is too long";
			return null;
		}

		/// Why the answers cannot be kept (null = they can): every active question of the kind answered 1–5, the
		/// recommend score 0–10.
		public static string? AnswerRefusal(HospitalSurveyInput i, IReadOnlyCollection<int> questionIds)
		{
			if (questionIds.Count == 0) return "There are no questions for this kind of care yet";
			if (questionIds.Any(q => !i.Scores.TryGetValue(q, out var s) || s is < 1 or > 5)) return "Answer every question";
			if (i.Scores.Keys.Any(k => !questionIds.Contains(k))) return "not_found";
			if (i.Recommend is not (>= 0 and <= 10)) return "Answer how likely you are to recommend us";
			if ((i.Comment?.Length ?? 0) > 1000) return "A text is too long";
			return null;
		}

		public static decimal Average(IEnumerable<int> scores) { var l = scores.ToList(); return l.Count == 0 ? 0 : Math.Round((decimal)l.Average(), 2); }
		public static bool NeedsFollowUp(decimal average, int recommend) => average <= LowAverage || recommend <= LowRecommend;

		/// The net promoter score: promoters (9–10) less detractors (0–6), in percent of all answers.
		public static int? Nps(IReadOnlyCollection<int> recommends) =>
			recommends.Count == 0 ? null : (int)Math.Round(100m * (recommends.Count(r => r >= 9) - recommends.Count(r => r <= 6)) / recommends.Count);

		/// The standard questions per kind (the administrator adds them in one step and edits them after).
		public static IReadOnlyList<HospitalSurveyQuestionInput> Standard() => new[]
		{
			Q("V1", HospitalSurveyKind.Visit, 1, "كيف تقيّم سهولة حجز الموعد؟", "How easy was it to book your appointment?"),
			Q("V2", HospitalSurveyKind.Visit, 2, "كيف تقيّم مدة الانتظار قبل دخولك للطبيب؟", "How was the waiting time before you saw the doctor?"),
			Q("V3", HospitalSurveyKind.Visit, 3, "هل شرح لك الطبيب حالتك وعلاجك بوضوح؟", "Did the doctor explain your condition and treatment clearly?"),
			Q("V4", HospitalSurveyKind.Visit, 4, "كيف تقيّم تعامل موظفي الاستقبال؟", "How were you treated at the front desk?"),
			Q("V5", HospitalSurveyKind.Visit, 5, "كيف تقيّم نظافة العيادة؟", "How clean was the clinic?"),
			Q("S1", HospitalSurveyKind.Stay, 1, "كيف تقيّم رعاية التمريض أثناء إقامتك؟", "How was the nursing care during your stay?"),
			Q("S2", HospitalSurveyKind.Stay, 2, "هل أبقاك الأطباء على علم بخطة علاجك؟", "Did the doctors keep you informed about your treatment plan?"),
			Q("S3", HospitalSurveyKind.Stay, 3, "كيف تقيّم نظافة الغرفة وراحتها؟", "How clean and comfortable was your room?"),
			Q("S4", HospitalSurveyKind.Stay, 4, "كيف تقيّم الوجبات؟", "How were the meals?"),
			Q("S5", HospitalSurveyKind.Stay, 5, "هل شُرحت لك تعليمات الخروج والأدوية بوضوح؟", "Were your discharge instructions and medicines explained clearly?"),
			Q("E1", HospitalSurveyKind.Emergency, 1, "كيف تقيّم سرعة استقبالك وفرزك؟", "How quickly were you received and triaged?"),
			Q("E2", HospitalSurveyKind.Emergency, 2, "كيف تقيّم مدة الانتظار قبل رؤية الطبيب؟", "How was the waiting time before you saw a doctor?"),
			Q("E3", HospitalSurveyKind.Emergency, 3, "هل شُرح لك ما حدث وما عليك فعله بعد الخروج؟", "Was it explained what happened and what to do after you left?"),
		};

		private static HospitalSurveyQuestionInput Q(string code, string kind, int order, string ar, string en) => new() { Code = code, Kind = kind, SortOrder = order, Text = ar, TextEn = en, IsActive = true };
	}

	public sealed class HospitalSurveyService
	{
		public const string NoTables = "Satisfaction surveys are not installed yet (hospital_h65_surveys.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSurveyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalSurveyQuestion> Questions(BusinessContext ctx) => _db.Set<HospitalSurveyQuestion>().Where(q => q.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalSurveyResponse> Responses(BusinessContext ctx) => _db.Set<HospitalSurveyResponse>().Where(r => r.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		// ---------------- questions ----------------

		public async Task<(IReadOnlyList<HospitalSurveyQuestion> Questions, bool Ready)> QuestionsAsync(BusinessContext ctx, string? kind, bool activeOnly, CancellationToken ct = default)
		{
			try { return (await Questions(ctx).AsNoTracking().Where(q => (kind == null || q.Kind == kind) && (!activeOnly || q.IsActive)).OrderBy(q => q.Kind).ThenBy(q => q.SortOrder).ThenBy(q => q.Code).ToListAsync(ct), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalSurveyQuestion>(), false); }
		}

		public async Task<HospitalResult> SaveQuestionAsync(BusinessContext ctx, int? id, HospitalSurveyQuestionInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalSurveyRules.QuestionRefusal(input) is string why) return HospitalResult.Fail(why);
			var code = input.Code!.Trim().ToUpperInvariant();
			try
			{
				if (await Questions(ctx).AnyAsync(q => q.Code == code && q.ID != (id ?? 0), ct)) return HospitalResult.Fail("Another question has this code");
				HospitalSurveyQuestion row;
				if (id is int qid) { var f = await Questions(ctx).FirstOrDefaultAsync(q => q.ID == qid, ct); if (f == null) return HospitalResult.Fail("not_found"); row = f; }
				else { row = new HospitalSurveyQuestion { CompanyId = ctx.CompanyId }; _db.Add(row); }
				row.Code = code; row.Kind = input.Kind!; row.Text = input.Text!.Trim(); row.TextEn = input.TextEn!.Trim(); row.SortOrder = input.SortOrder; row.IsActive = input.IsActive;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another question has this code"); }
		}

		public async Task<HospitalResult> AddStandardAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var have = await Questions(ctx).Select(q => q.Code).ToListAsync(ct);
				var add = HospitalSurveyRules.Standard().Where(s => !have.Contains(s.Code!)).ToList();
				foreach (var s in add) _db.Add(new HospitalSurveyQuestion { CompanyId = ctx.CompanyId, Code = s.Code!, Kind = s.Kind!, Text = s.Text!, TextEn = s.TextEn!, SortOrder = s.SortOrder, IsActive = true });
				await _db.SaveChangesAsync(ct);
				return new HospitalResult(true, null, null, new object[] { add.Count });
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		// ---------------- episodes ----------------

		/// The recent episodes not surveyed yet (newest first): signed visits, discharged stays, closed emergency visits.
		public async Task<IReadOnlyList<HospitalSurveyEpisode>> EpisodesAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			int c = ctx.CompanyId; var since = nowUtc.AddDays(-HospitalSurveyRules.WindowDays);
			var done = await Responses(ctx).AsNoTracking().Select(r => new { r.EncounterId, r.AdmissionId, r.ErVisitId }).ToListAsync(ct);
			var enc = done.Where(d => d.EncounterId != null).Select(d => d.EncounterId!.Value).ToHashSet();
			var adm = done.Where(d => d.AdmissionId != null).Select(d => d.AdmissionId!.Value).ToHashSet();
			var er = done.Where(d => d.ErVisitId != null).Select(d => d.ErVisitId!.Value).ToHashSet();
			var visits = await (from e in _db.HospitalEncounters.AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on e.PatientId equals p.ID
								where e.CompanyId == c && e.Status == HospitalEncounterStatus.Signed && e.SignedAt >= since
								select new { e.ID, e.PatientId, p.Name, p.NameEn, p.Mrn, At = e.SignedAt!.Value }).ToListAsync(ct);
			var stays = await (from a in _db.HospitalAdmissions.AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							   where a.CompanyId == c && a.DischargedAt != null && a.DischargedAt >= since
							   select new { a.ID, a.PatientId, p.Name, p.NameEn, p.Mrn, At = a.DischargedAt!.Value }).ToListAsync(ct);
			var ers = await (from v in _db.Set<HospitalErVisit>().AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on v.PatientId equals p.ID
							 where v.CompanyId == c && v.ClosedAt != null && v.ClosedAt >= since && v.Outcome != HospitalErOutcome.Deceased && v.Outcome != HospitalErOutcome.LeftWithoutBeingSeen
							 select new { v.ID, PatientId = p.ID, p.Name, p.NameEn, p.Mrn, At = v.ClosedAt!.Value, v.VisitNo }).ToListAsync(ct);
			return visits.Where(v => !enc.Contains(v.ID)).Select(v => new HospitalSurveyEpisode(HospitalSurveyKind.Visit, v.ID, v.PatientId, v.Name, v.NameEn, v.Mrn, v.At, ""))
				.Concat(stays.Where(s => !adm.Contains(s.ID)).Select(s => new HospitalSurveyEpisode(HospitalSurveyKind.Stay, s.ID, s.PatientId, s.Name, s.NameEn, s.Mrn, s.At, "")))
				.Concat(ers.Where(e => !er.Contains(e.ID)).Select(e => new HospitalSurveyEpisode(HospitalSurveyKind.Emergency, e.ID, e.PatientId, e.Name, e.NameEn, e.Mrn, e.At, e.VisitNo)))
				.OrderByDescending(e => e.EndedAt).ToList();
		}

		// ---------------- responses ----------------

		public async Task<HospitalResult> TakeAsync(BusinessContext ctx, HospitalSurveyInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Read, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (!HospitalSurveyKind.All.Contains(input.Kind ?? "")) return HospitalResult.Fail("not_found");
			var episode = (await EpisodesAsync(ctx, nowUtc, ct)).FirstOrDefault(e => e.Kind == input.Kind && e.Id == input.EpisodeId);
			if (episode == null) return HospitalResult.Fail("This visit was already surveyed, or is too old to survey");
			List<int> qids;
			try { qids = await Questions(ctx).Where(q => q.Kind == input.Kind && q.IsActive).Select(q => q.ID).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			if (HospitalSurveyRules.AnswerRefusal(input, qids) is string why) return HospitalResult.Fail(why);
			decimal avg = HospitalSurveyRules.Average(qids.Select(q => input.Scores[q]));
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				var nos = await Responses(ctx).Select(r => r.ResponseNo).ToListAsync(ct);
				var row = new HospitalSurveyResponse
				{
					CompanyId = ctx.CompanyId, ResponseNo = HospitalDentalPracticeRules.NextNo(nos, "SV-", 6), Kind = input.Kind!, PatientId = episode.PatientId,
					EncounterId = input.Kind == HospitalSurveyKind.Visit ? episode.Id : null, AdmissionId = input.Kind == HospitalSurveyKind.Stay ? episode.Id : null,
					ErVisitId = input.Kind == HospitalSurveyKind.Emergency ? episode.Id : null, Recommend = input.Recommend!.Value, Average = avg, Comment = Clip(input.Comment, 1000),
					Anonymous = input.Anonymous, At = nowUtc, CollectedByEmployeeId = me,
					FollowUp = HospitalSurveyRules.NeedsFollowUp(avg, input.Recommend.Value) ? HospitalSurveyFollowUp.Open : HospitalSurveyFollowUp.None,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				foreach (var q in qids) _db.Add(new HospitalSurveyAnswer { CompanyId = ctx.CompanyId, ResponseId = row.ID, QuestionId = q, Score = input.Scores[q] });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(row.ID);
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("This visit was already surveyed, or is too old to survey"); }
		}

		public async Task<HospitalResult> CloseFollowUpAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write what was done");
			int n = await Responses(ctx).Where(r => r.ID == id && r.FollowUp == HospitalSurveyFollowUp.Open).ExecuteUpdateAsync(u => u.SetProperty(r => r.FollowUp, HospitalSurveyFollowUp.Done)
				.SetProperty(r => r.FollowedUpAt, nowUtc).SetProperty(r => r.FollowedUpByEmployeeId, me).SetProperty(r => r.FollowUpNote, Clip(note, 500)), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This survey has no open follow-up");
		}

		/// The figures over a period (the whole set, never one page) and each question's average.
		public async Task<HospitalSurveyStats> StatsAsync(BusinessContext ctx, string? kind, DateTime fromUtc, CancellationToken ct = default)
		{
			try
			{
				var rs = await Responses(ctx).AsNoTracking().Where(r => r.At >= fromUtc && (kind == null || r.Kind == kind)).Select(r => new { r.ID, r.Recommend, r.Average, r.FollowUp }).ToListAsync(ct);
				var ids = rs.Select(r => r.ID).ToList();
				var answers = await _db.Set<HospitalSurveyAnswer>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && ids.Contains(a.ResponseId)).ToListAsync(ct);
				var (qs, _) = await QuestionsAsync(ctx, kind, false, ct);
				var by = qs.Select(q => { var s = answers.Where(a => a.QuestionId == q.ID).Select(a => a.Score).ToList(); return (q, s.Count == 0 ? (decimal?)null : HospitalSurveyRules.Average(s), s.Count); }).ToList();
				int open = await Responses(ctx).CountAsync(r => r.FollowUp == HospitalSurveyFollowUp.Open, ct);
				return new HospitalSurveyStats(rs.Count, HospitalSurveyRules.Nps(rs.Select(r => r.Recommend).ToList()), rs.Count == 0 ? null : Math.Round(rs.Average(r => r.Average), 2), open, by);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalSurveyStats(0, null, null, 0, Array.Empty<(HospitalSurveyQuestion, decimal?, int)>()); }
		}

		public async Task<(IReadOnlyList<HospitalSurveyRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? kind, string? state, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			var raw = await (from r in Responses(ctx).AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							 where (kind == null || r.Kind == kind) && (state == null || (state == "followup" ? r.FollowUp == HospitalSurveyFollowUp.Open : r.FollowUp != HospitalSurveyFollowUp.Open))
							 orderby r.FollowUp == HospitalSurveyFollowUp.Open descending, r.At descending
							 select new { r, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct);
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			// an anonymous survey is found by its number or comment only — never by the patient's name
			var hit = raw.Where(x => terms.All(t => x.r.ResponseNo.Contains(t, StringComparison.OrdinalIgnoreCase) || (x.r.Comment ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
				|| (!x.r.Anonymous && (x.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || x.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase) || x.Mrn.Contains(t, StringComparison.OrdinalIgnoreCase))))).ToList();
			var pageRows = hit.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var eids = pageRows.SelectMany(x => new int?[] { x.r.CollectedByEmployeeId, x.r.FollowedUpByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			string? N(int? id, bool en) => id is int i && people.TryGetValue(i, out var e) ? (en ? e.FullNameEn ?? e.FullName : e.FullName) : null;
			return (pageRows.Select(x => new HospitalSurveyRow(x.r, x.Name, x.NameEn, x.Mrn, N(x.r.CollectedByEmployeeId, false), N(x.r.CollectedByEmployeeId, true), N(x.r.FollowedUpByEmployeeId, false), N(x.r.FollowedUpByEmployeeId, true))).ToList(), hit.Count);
		}
	}
}
