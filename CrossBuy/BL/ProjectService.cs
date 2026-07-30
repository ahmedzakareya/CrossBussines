using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Per-project profitability row (from the GL Project dimension).
	public class ProjectPnlRow
	{
		public int ProjectId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public decimal Revenue { get; set; }      // Σ(credit−debit) on revenue (4xxx)
		public decimal Cost { get; set; }          // Σ(debit−credit) on cost/expense (5xxx, incl. 520108/520109)
		public decimal Profit => Math.Round(Revenue - Cost, 2);
		public decimal MarginPct => Revenue != 0 ? Math.Round(Profit / Revenue * 100m, 2) : 0m;
		public decimal? Budget { get; set; }
	}

	public interface IProjectService
	{
		Task<List<Project>> GetProjectsAsync(int companyId, string? q, bool? active);
		Task<Project?> GetAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveAsync(Project dto);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
		Task<List<(int id, string text)>> ForPickAsync(int companyId);
		Task<List<ProjectPnlRow>> ProfitabilityAsync(int companyId, DateTime fromDate, DateTime toDate);
		// ---- Projects & Contracting P0: user-defined activity-type lookup ----
		Task<List<ProjectActivityType>> GetActivityTypesAsync(int companyId, bool activeOnly = false);
		Task<(bool ok, string? error, int id)> SaveActivityTypeAsync(ProjectActivityType dto);
		Task<(bool ok, string? error)> DeleteActivityTypeAsync(int companyId, int id);
		Task<List<(int id, string text)>> ActivityTypesForPickAsync(int companyId);
	}

	public class ProjectService : IProjectService
	{
		private readonly CrossDbContext _db;
		public ProjectService(CrossDbContext db) { _db = db; }

		public Task<List<Project>> GetProjectsAsync(int companyId, string? q, bool? active)
		{
			var query = _db.Projects.AsNoTracking().Where(p => p.CompanyID == companyId);
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(p => p.Code.Contains(t) || p.Name.Contains(t) || p.NameEn.Contains(t)); }
			if (active.HasValue) query = query.Where(p => p.IsActive == active.Value);
			return query.OrderByDescending(p => p.ID).ToListAsync();
		}

		public Task<Project?> GetAsync(int companyId, int id) =>
			_db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);

		public async Task<(bool ok, string? error, int id)> SaveAsync(Project dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return (false, "الكود والاسم مطلوبان", 0);
			if (string.IsNullOrWhiteSpace(dto.NameEn)) return (false, "الاسم الإنجليزي مطلوب", 0);
			if (await _db.Projects.AnyAsync(p => p.CompanyID == dto.CompanyID && p.Code == dto.Code && p.ID != dto.ID)) return (false, "كود المشروع مستخدم من قبل", 0);
			if (dto.StartDate.HasValue && dto.EndDate.HasValue && dto.EndDate < dto.StartDate) return (false, "تاريخ النهاية قبل البداية", 0);
			Project e;
			if (dto.ID > 0) e = await _db.Projects.FirstOrDefaultAsync(p => p.ID == dto.ID && p.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("المشروع غير موجود");
			else { e = new Project { CompanyID = dto.CompanyID, CreatedAt = DateTime.UtcNow }; _db.Projects.Add(e); }
			e.Code = dto.Code.Trim(); e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn ?? ""; e.IsActive = dto.IsActive;
			e.StartDate = dto.StartDate; e.EndDate = dto.EndDate; e.Budget = dto.Budget;
			// contracting fields (all nullable, do not affect GL)
			e.CustomerId = dto.CustomerId; e.Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim();
			e.ContractValue = dto.ContractValue; e.ActivityTypeId = dto.ActivityTypeId; e.CostCenterId = dto.CostCenterId;
			e.AdvancePercent = dto.AdvancePercent; e.RetentionPercent = dto.RetentionPercent;   // P2 contract terms
			e.Status = string.IsNullOrWhiteSpace(dto.Status) ? (dto.ID > 0 ? e.Status : "Draft") : dto.Status.Trim();
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		// ---------- Projects & Contracting P0: activity-type lookup (user-defined) ----------
		public Task<List<ProjectActivityType>> GetActivityTypesAsync(int companyId, bool activeOnly = false) =>
			_db.ProjectActivityTypes.AsNoTracking()
				.Where(t => t.CompanyID == companyId && (!activeOnly || t.IsActive))
				.OrderBy(t => t.Code).ToListAsync();

		public async Task<(bool ok, string? error, int id)> SaveActivityTypeAsync(ProjectActivityType dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return (false, "الكود والاسم مطلوبان", 0);
			if (string.IsNullOrWhiteSpace(dto.NameEn)) return (false, "الاسم الإنجليزي مطلوب", 0);
			if (await _db.ProjectActivityTypes.AnyAsync(t => t.CompanyID == dto.CompanyID && t.Code == dto.Code && t.ID != dto.ID)) return (false, "الكود مستخدم من قبل", 0);
			ProjectActivityType e;
			if (dto.ID > 0) e = await _db.ProjectActivityTypes.FirstOrDefaultAsync(t => t.ID == dto.ID && t.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("نوع النشاط غير موجود");
			else { e = new ProjectActivityType { CompanyID = dto.CompanyID, CreatedAt = DateTime.UtcNow }; _db.ProjectActivityTypes.Add(e); }
			e.Code = dto.Code.Trim(); e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn ?? ""; e.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task<(bool ok, string? error)> DeleteActivityTypeAsync(int companyId, int id)
		{
			var e = await _db.ProjectActivityTypes.FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == companyId);
			if (e == null) return (false, "نوع النشاط غير موجود");
			if (await _db.Projects.AnyAsync(p => p.ActivityTypeId == id)) return (false, "لا يمكن الحذف: مستخدم في مشاريع (يمكن إيقافه)");
			_db.ProjectActivityTypes.Remove(e); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<(int id, string text)>> ActivityTypesForPickAsync(int companyId) =>
			(await _db.ProjectActivityTypes.AsNoTracking().Where(t => t.CompanyID == companyId && t.IsActive).OrderBy(t => t.Name)
				.Select(t => new { t.ID, t.Code, t.Name }).ToListAsync())
				.Select(t => (t.ID, t.Code + " — " + t.Name)).ToList();

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var e = await _db.Projects.FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);
			if (e == null) return (false, "المشروع غير موجود");
			if (await _db.JournalEntryLines.AnyAsync(l => l.ProjectId == id)) return (false, "لا يمكن الحذف: توجد قيود مرتبطة بالمشروع (يمكن إيقافه بدل الحذف)");
			_db.Projects.Remove(e); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<(int id, string text)>> ForPickAsync(int companyId) =>
			(await _db.Projects.AsNoTracking().Where(p => p.CompanyID == companyId && p.IsActive).OrderBy(p => p.Name)
				.Select(p => new { p.ID, p.Code, p.Name }).ToListAsync())
				.Select(p => (p.ID, p.Code + " — " + p.Name)).ToList();

		public async Task<List<ProjectPnlRow>> ProfitabilityAsync(int companyId, DateTime fromDate, DateTime toDate)
		{
			var start = fromDate.Date; var toEnd = toDate.Date.AddDays(1).AddTicks(-1);
			var rows = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.ID
							  where en.CompanyID == companyId && l.ProjectId != null && en.EntryDate >= start && en.EntryDate <= toEnd
							  select new { ProjectId = l.ProjectId.Value, a.Code, l.Debit, l.Credit }).ToListAsync();

			var projects = await _db.Projects.AsNoTracking().Where(p => p.CompanyID == companyId).ToListAsync();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var byProj = rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.ToList());
			var result = new List<ProjectPnlRow>();
			foreach (var p in projects)
			{
				if (!byProj.TryGetValue(p.ID, out var lines)) { if (p.Budget == null) continue; lines = new(); }
				decimal rev = lines.Where(x => x.Code.StartsWith("4")).Sum(x => x.Credit - x.Debit);
				decimal cost = lines.Where(x => x.Code.StartsWith("5")).Sum(x => x.Debit - x.Credit);
				var name = isAr ? p.Name : (string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn);
				result.Add(new ProjectPnlRow { ProjectId = p.ID, Code = p.Code, Name = name, Revenue = Math.Round(rev, 2), Cost = Math.Round(cost, 2), Budget = p.Budget });
			}
			return result.OrderByDescending(r => r.Revenue).ToList();
		}
	}
}
