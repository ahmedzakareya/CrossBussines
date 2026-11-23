using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalQuestionnaireSendInput { public int PatientId { get; set; } public string? Instrument { get; set; } public DateTime? DueDate { get; set; } }
	public sealed class HospitalQuestionnaireAnswerInput { public List<int>? Answers { get; set; } }

	public sealed record HospitalQuestionnaireRow(HospitalQuestionnaire Q, string PatientName, string? PatientNameEn, string? Photo, string? Mrn);

	// ==========================================================================================
	// HOSPITAL H250 — sending, answering and reviewing questionnaires. The clinician (`consult`) sends and reviews; the
	// patient answers on the portal for any of the account's people. An answered questionnaire is final.
	// ==========================================================================================
	public sealed class HospitalQuestionnaireService
	{
		public const string NoTables = "Questionnaires are not installed yet (hospital_h250_questionnaires.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalQuestionnaireService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		public async Task<HospitalResult> SendAsync(BusinessContext ctx, HospitalQuestionnaireSendInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalQuestionnaireRules.Find(i.Instrument) == null) return HospitalResult.Fail("Choose the questionnaire");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == i.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				if (await _db.Set<HospitalQuestionnaire>().AnyAsync(q => q.CompanyId == ctx.CompanyId && q.PatientId == i.PatientId && q.Instrument == i.Instrument && q.Status == HospitalQuestionnaireStatus.Pending, ct))
					return HospitalResult.Fail("This questionnaire is already waiting for the patient");
				var q = new HospitalQuestionnaire { CompanyId = ctx.CompanyId, PatientId = i.PatientId, Instrument = i.Instrument!, SentAt = nowUtc, SentByEmployeeId = ctx.EmployeeId, DueDate = i.DueDate?.Date };
				_db.Add(q); await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(q.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<(bool Ready, IReadOnlyList<HospitalQuestionnaireRow> Rows, int Total, int Urgent)?> ListAsync(BusinessContext ctx, string? q, string? view, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			try
			{
				var b = from x in _db.Set<HospitalQuestionnaire>().AsNoTracking()
						join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID
						where x.CompanyId == ctx.CompanyId
						select new { x, p.Name, p.NameEn, p.PhotoPath, p.Mrn };
				int urgent = await b.CountAsync(r => r.x.Urgent && r.x.ReviewedAt == null, ct);
				b = view switch
				{
					"pending" => b.Where(r => r.x.Status == HospitalQuestionnaireStatus.Pending),
					"all" => b,
					_ => b.Where(r => r.x.Status == HospitalQuestionnaireStatus.Completed && r.x.ReviewedAt == null),
				};
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); b = b.Where(r => r.Name.Contains(s) || (r.NameEn ?? "").Contains(s) || r.Mrn.Contains(s) || r.x.Instrument.Contains(s)); }
				int total = await b.CountAsync(ct);
				var rows = await b.OrderByDescending(r => r.x.Urgent && r.x.ReviewedAt == null).ThenByDescending(r => r.x.CompletedAt ?? r.x.SentAt).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				return (true, rows.Select(r => new HospitalQuestionnaireRow(r.x, r.Name, r.NameEn, r.PhotoPath, r.Mrn)).ToList(), total, urgent);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (false, Array.Empty<HospitalQuestionnaireRow>(), 0, 0); }
		}

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var x = await _db.Set<HospitalQuestionnaire>().FirstOrDefaultAsync(q => q.ID == id && q.CompanyId == ctx.CompanyId, ct);
			if (x == null) return HospitalResult.Fail("not_found");
			if (x.Status != HospitalQuestionnaireStatus.Completed) return HospitalResult.Fail("Only an answered questionnaire is reviewed");
			x.ReviewedAt ??= nowUtc; x.ReviewedByEmployeeId ??= ctx.EmployeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(x.ID);
		}

		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var x = await _db.Set<HospitalQuestionnaire>().FirstOrDefaultAsync(q => q.ID == id && q.CompanyId == ctx.CompanyId, ct);
			if (x == null) return HospitalResult.Fail("not_found");
			if (x.Status != HospitalQuestionnaireStatus.Pending) return HospitalResult.Fail("Only a waiting questionnaire is withdrawn");
			x.Status = HospitalQuestionnaireStatus.Cancelled; await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(x.ID);
		}

		// ---------------------------------------------------------------- the portal's side

		public async Task<IReadOnlyList<HospitalQuestionnaire>> ForPeopleAsync(int companyId, IReadOnlyCollection<int> people, CancellationToken ct = default)
		{
			try
			{
				return await _db.Set<HospitalQuestionnaire>().AsNoTracking().Where(q => q.CompanyId == companyId && people.Contains(q.PatientId) && q.Status != HospitalQuestionnaireStatus.Cancelled)
					.OrderBy(q => q.Status == HospitalQuestionnaireStatus.Pending ? 0 : 1).ThenByDescending(q => q.SentAt).Take(50).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalQuestionnaire>(); }
		}

		public async Task<HospitalResult> AnswerAsync(int companyId, int accountId, IReadOnlyCollection<int> people, int id, HospitalQuestionnaireAnswerInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			var x = await _db.Set<HospitalQuestionnaire>().FirstOrDefaultAsync(q => q.ID == id && q.CompanyId == companyId, ct);
			if (x == null || !people.Contains(x.PatientId)) return HospitalResult.Fail("not_found");
			if (x.Status != HospitalQuestionnaireStatus.Pending) return HospitalResult.Fail("This questionnaire is already answered");
			var ins = HospitalQuestionnaireRules.Find(x.Instrument)!;
			if (HospitalQuestionnaireRules.AnswerRefusal(ins, i.Answers) is string why) return HospitalResult.Fail(why);
			bool female = await _db.HospitalPatients.Where(p => p.ID == x.PatientId).Select(p => p.Sex == HospitalPatientSex.Female).FirstOrDefaultAsync(ct);
			var (score, sev, urgent) = HospitalQuestionnaireRules.Score(ins, i.Answers!, female);
			x.Answers = string.Join(",", i.Answers!); x.Score = score; x.Severity = sev; x.Urgent = urgent;
			x.Status = HospitalQuestionnaireStatus.Completed; x.CompletedAt = nowUtc; x.CompletedByAccountId = accountId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(x.ID);
		}
	}
}
