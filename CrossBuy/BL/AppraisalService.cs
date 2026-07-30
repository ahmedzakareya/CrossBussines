using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// HR-9 — performance appraisal engine. Weighted score = Σ(score/max × weight) / Σweight × 100. No GL impact.
	public interface IAppraisalService
	{
		// cycles
		Task<List<AppraisalCycle>> GetCyclesAsync(int companyId);
		Task<(bool ok, string? error)> SaveCycleAsync(AppraisalCycle dto);
		Task SetCycleStatusAsync(int companyId, int id, string status);
		// templates
		Task<List<AppraisalTemplate>> GetTemplatesAsync(int companyId);
		Task<AppraisalTemplate?> GetTemplateAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveTemplateAsync(AppraisalTemplate dto);
		Task DeleteTemplateAsync(int companyId, int id);
		// appraisals
		Task<List<Appraisal>> GetAppraisalsAsync(int companyId, int? cycleId);
		Task<Appraisal?> GetAppraisalAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> CreateAppraisalAsync(int companyId, int cycleId, int templateId, int employeeId, int managerId, int? userId);
		Task<(bool ok, string? error)> SaveScoresAsync(int companyId, int id, Dictionary<int, decimal> scores, Dictionary<int, string?> notes, string? managerComment);
		Task<(bool ok, string? error)> SubmitAsync(int companyId, int id);
		Task<(bool ok, string? error)> AcknowledgeAsync(int id, int employeeId, string? comment);
		Task<List<Appraisal>> MyAppraisalsAsync(int employeeId);
	}

	public class AppraisalService : IAppraisalService
	{
		private readonly CrossDbContext _db;
		private readonly INotificationService _notifications;
		public AppraisalService(CrossDbContext db, INotificationService notifications) { _db = db; _notifications = notifications; }

		private static decimal R(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		// ---- cycles ----
		public Task<List<AppraisalCycle>> GetCyclesAsync(int companyId) =>
			_db.AppraisalCycles.AsNoTracking().Where(c => c.CompanyID == companyId).OrderByDescending(c => c.Year).ThenByDescending(c => c.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SaveCycleAsync(AppraisalCycle dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم الدورة مطلوب");
			if (dto.ID > 0)
			{
				var e = await _db.AppraisalCycles.FirstOrDefaultAsync(c => c.ID == dto.ID && c.CompanyID == dto.CompanyID);
				if (e == null) return (false, "الدورة غير موجودة");
				e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.Year = dto.Year; e.StartDate = dto.StartDate; e.EndDate = dto.EndDate;
			}
			else { dto.CreatedAt = DateTime.UtcNow; _db.AppraisalCycles.Add(dto); }
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task SetCycleStatusAsync(int companyId, int id, string status)
		{
			var e = await _db.AppraisalCycles.FirstOrDefaultAsync(c => c.ID == id && c.CompanyID == companyId);
			if (e != null) { e.Status = status == "Closed" ? "Closed" : "Open"; await _db.SaveChangesAsync(); }
		}

		// ---- templates ----
		public Task<List<AppraisalTemplate>> GetTemplatesAsync(int companyId) =>
			_db.AppraisalTemplates.AsNoTracking().Where(t => t.CompanyID == companyId).OrderBy(t => t.Name).ToListAsync();

		public async Task<AppraisalTemplate?> GetTemplateAsync(int companyId, int id)
		{
			var t = await _db.AppraisalTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (t == null) return null;
			t.Criteria = await _db.AppraisalCriteria.AsNoTracking().Where(c => c.TemplateId == id).OrderBy(c => c.SortOrder).ToListAsync();
			return t;
		}

		public async Task<(bool ok, string? error, int id)> SaveTemplateAsync(AppraisalTemplate dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم النموذج مطلوب", 0);
			if (dto.Criteria == null || dto.Criteria.Count == 0) return (false, "أضف معيارًا واحدًا على الأقل", 0);
			AppraisalTemplate entity;
			if (dto.ID > 0)
			{
				entity = await _db.AppraisalTemplates.FirstOrDefaultAsync(t => t.ID == dto.ID && t.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("النموذج غير موجود");
				_db.AppraisalCriteria.RemoveRange(_db.AppraisalCriteria.Where(c => c.TemplateId == entity.ID));
			}
			else { entity = new AppraisalTemplate { CompanyID = dto.CompanyID, CreatedAt = DateTime.UtcNow }; _db.AppraisalTemplates.Add(entity); }
			entity.Name = dto.Name.Trim(); entity.NameEn = dto.NameEn; entity.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			int order = 1;
			foreach (var c in dto.Criteria)
			{
				if (string.IsNullOrWhiteSpace(c.Name)) continue;
				_db.AppraisalCriteria.Add(new AppraisalCriterion { TemplateId = entity.ID, Name = c.Name.Trim(), NameEn = c.NameEn, Weight = c.Weight < 0 ? 0 : c.Weight, MaxScore = c.MaxScore <= 0 ? 5 : c.MaxScore, SortOrder = order++ });
			}
			await _db.SaveChangesAsync();
			return (true, null, entity.ID);
		}

		public async Task DeleteTemplateAsync(int companyId, int id)
		{
			var e = await _db.AppraisalTemplates.FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == companyId);
			if (e == null) return;
			_db.AppraisalCriteria.RemoveRange(_db.AppraisalCriteria.Where(c => c.TemplateId == id));
			_db.AppraisalTemplates.Remove(e);
			await _db.SaveChangesAsync();
		}

		// ---- appraisals ----
		public Task<List<Appraisal>> GetAppraisalsAsync(int companyId, int? cycleId) =>
			_db.Appraisals.AsNoTracking().Where(a => a.CompanyID == companyId && (cycleId == null || a.CycleId == cycleId))
				.OrderByDescending(a => a.ID).ToListAsync();

		public async Task<Appraisal?> GetAppraisalAsync(int companyId, int id)
		{
			var a = await _db.Appraisals.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (a == null) return null;
			a.Lines = await _db.AppraisalLines.AsNoTracking().Where(l => l.AppraisalId == id).ToListAsync();
			return a;
		}

		public async Task<(bool ok, string? error, int id)> CreateAppraisalAsync(int companyId, int cycleId, int templateId, int employeeId, int managerId, int? userId)
		{
			if (cycleId <= 0 || templateId <= 0 || employeeId <= 0) return (false, "الدورة والنموذج والموظف مطلوبة", 0);
			var cycle = await _db.AppraisalCycles.AsNoTracking().FirstOrDefaultAsync(c => c.ID == cycleId && c.CompanyID == companyId);
			if (cycle == null) return (false, "الدورة غير موجودة", 0);
			if (cycle.Status == "Closed") return (false, "الدورة مقفلة", 0);
			if (await _db.Appraisals.AnyAsync(a => a.CompanyID == companyId && a.CycleId == cycleId && a.EmployeeID == employeeId))
				return (false, "يوجد تقييم لهذا الموظف في هذه الدورة", 0);
			var criteria = await _db.AppraisalCriteria.AsNoTracking().Where(c => c.TemplateId == templateId).OrderBy(c => c.SortOrder).ToListAsync();
			if (criteria.Count == 0) return (false, "النموذج بلا معايير", 0);

			var appr = new Appraisal { CompanyID = companyId, CycleId = cycleId, TemplateId = templateId, EmployeeID = employeeId, ManagerEmployeeID = managerId, Status = 0, CreatedAt = DateTime.UtcNow, CreatedBy = userId };
			_db.Appraisals.Add(appr); await _db.SaveChangesAsync();
			foreach (var c in criteria) _db.AppraisalLines.Add(new AppraisalLine { AppraisalId = appr.ID, CriterionId = c.ID, Score = 0 });
			await _db.SaveChangesAsync();
			return (true, null, appr.ID);
		}

		private async Task<decimal> ComputeScoreAsync(int appraisalId, int templateId)
		{
			var crit = await _db.AppraisalCriteria.AsNoTracking().Where(c => c.TemplateId == templateId).ToDictionaryAsync(c => c.ID);
			var lines = await _db.AppraisalLines.AsNoTracking().Where(l => l.AppraisalId == appraisalId).ToListAsync();
			decimal wsum = 0, acc = 0;
			foreach (var l in lines)
			{
				if (!crit.TryGetValue(l.CriterionId, out var c)) continue;
				var w = c.Weight <= 0 ? 0 : c.Weight;
				var max = c.MaxScore <= 0 ? 5 : c.MaxScore;
				var s = l.Score < 0 ? 0 : (l.Score > max ? max : l.Score);
				wsum += w; acc += (s / max) * w;
			}
			return wsum > 0 ? R(acc / wsum * 100m) : 0m;
		}

		public async Task<(bool ok, string? error)> SaveScoresAsync(int companyId, int id, Dictionary<int, decimal> scores, Dictionary<int, string?> notes, string? managerComment)
		{
			var appr = await _db.Appraisals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (appr == null) return (false, "التقييم غير موجود");
			if (appr.Status == 2) return (false, "التقييم مُقَرّ ولا يمكن تعديله");
			var lines = await _db.AppraisalLines.Where(l => l.AppraisalId == id).ToListAsync();
			foreach (var l in lines)
			{
				if (scores.TryGetValue(l.CriterionId, out var sc)) l.Score = sc < 0 ? 0 : sc;
				if (notes.TryGetValue(l.CriterionId, out var nt)) l.Note = nt;
			}
			appr.ManagerComment = managerComment;
			appr.TotalScore = await ComputeScoreAsync(id, appr.TemplateId);   // recompute in-flight (uses tracked line values on next save)
			await _db.SaveChangesAsync();
			appr.TotalScore = await ComputeScoreAsync(id, appr.TemplateId);   // recompute from persisted values
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SubmitAsync(int companyId, int id)
		{
			var appr = await _db.Appraisals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (appr == null) return (false, "التقييم غير موجود");
			if (appr.Status != 0) return (false, "تم إرسال التقييم مسبقًا");
			appr.TotalScore = await ComputeScoreAsync(id, appr.TemplateId);
			appr.Status = 1; appr.SubmittedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			await _notifications.NotifyAsync(appr.EmployeeID, "تقييم أداء بانتظار إقرارك", "A performance appraisal awaits your acknowledgment",
				$"صدر تقييم أداء لك بدرجة {appr.TotalScore:0.#}% — يرجى الاطلاع والإقرار.",
				$"Your performance appraisal ({appr.TotalScore:0.#}%) is ready — please review and acknowledge.",
				"appraisal_submitted", appr.ID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> AcknowledgeAsync(int id, int employeeId, string? comment)
		{
			var appr = await _db.Appraisals.FirstOrDefaultAsync(a => a.ID == id && a.EmployeeID == employeeId);
			if (appr == null) return (false, "التقييم غير موجود");
			if (appr.Status != 1) return (false, appr.Status == 2 ? "تم الإقرار مسبقًا" : "التقييم لم يُرسَل بعد");
			appr.Status = 2; appr.EmployeeComment = comment; appr.AcknowledgedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			await _notifications.NotifyAsync(appr.ManagerEmployeeID, "تم إقرار تقييم الأداء", "Appraisal acknowledged",
				"أقرّ الموظف تقييم الأداء.", "The employee acknowledged the performance appraisal.", "appraisal_acknowledged", appr.ID);
			return (true, null);
		}

		public Task<List<Appraisal>> MyAppraisalsAsync(int employeeId) =>
			_db.Appraisals.AsNoTracking().Where(a => a.EmployeeID == employeeId && a.Status >= 1).OrderByDescending(a => a.ID).ToListAsync();
	}
}
