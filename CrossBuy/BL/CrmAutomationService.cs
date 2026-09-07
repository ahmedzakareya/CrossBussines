using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// CRM 3-7b(ii) — automation engine. Evaluates rules on CRM triggers and runs actions. No GL impact.
	public interface ICrmAutomationService
	{
		Task<List<CrmAutomationRule>> GetRulesAsync(int companyId);
		Task<(bool ok, string? error, int id)> SaveRuleAsync(CrmAutomationRule dto);
		Task DeleteRuleAsync(int companyId, int id);
		/// Fire matching active rules for a trigger. Returns the number of actions executed.
		Task<int> RunAsync(int companyId, string triggerType, string? stage, int? leadId, int? oppId, int? ownerEmployeeId);
	}

	public class CrmAutomationService : ICrmAutomationService
	{
		private readonly CrossDbContext _db;
		private readonly INotificationService _notifications;
		public CrmAutomationService(CrossDbContext db, INotificationService notifications) { _db = db; _notifications = notifications; }

		public Task<List<CrmAutomationRule>> GetRulesAsync(int companyId) =>
			_db.CrmAutomationRules.AsNoTracking().Where(r => r.CompanyID == companyId).OrderBy(r => r.SortOrder).ThenBy(r => r.ID).ToListAsync();

		public async Task<(bool ok, string? error, int id)> SaveRuleAsync(CrmAutomationRule dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Rule name is required", 0);
			var trg = new[] { "LeadCreated", "OpportunityStageChanged" }.Contains(dto.TriggerType) ? dto.TriggerType : "LeadCreated";
			var act = new[] { "Notify", "CreateActivity" }.Contains(dto.ActionType) ? dto.ActionType : "CreateActivity";
			CrmAutomationRule e;
			if (dto.ID > 0) e = await _db.CrmAutomationRules.FirstOrDefaultAsync(r => r.ID == dto.ID && r.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("القاعدة غير موجودة");
			else { e = new CrmAutomationRule { CompanyID = dto.CompanyID }; _db.CrmAutomationRules.Add(e); }
			e.Name = dto.Name.Trim(); e.NameEn = string.IsNullOrWhiteSpace(dto.NameEn) ? null : dto.NameEn.Trim(); e.TriggerType = trg; e.StageFilter = string.IsNullOrWhiteSpace(dto.StageFilter) ? null : dto.StageFilter.Trim();
			e.ActionType = act; e.ActivityType = dto.ActivityType; e.Subject = dto.Subject; e.SubjectEn = string.IsNullOrWhiteSpace(dto.SubjectEn) ? null : dto.SubjectEn.Trim(); e.DueInDays = dto.DueInDays < 0 ? 0 : dto.DueInDays;
			e.NotifyTitle = dto.NotifyTitle; e.NotifyBody = dto.NotifyBody; e.IsActive = dto.IsActive; e.SortOrder = dto.SortOrder;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task DeleteRuleAsync(int companyId, int id)
		{
			var e = await _db.CrmAutomationRules.FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId);
			if (e != null) { _db.CrmAutomationRules.Remove(e); await _db.SaveChangesAsync(); }
		}

		public async Task<int> RunAsync(int companyId, string triggerType, string? stage, int? leadId, int? oppId, int? ownerEmployeeId)
		{
			var rules = await _db.CrmAutomationRules.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.IsActive && r.TriggerType == triggerType).OrderBy(r => r.SortOrder).ThenBy(r => r.ID).ToListAsync();
			int fired = 0;
			foreach (var r in rules)
			{
				if (triggerType == "OpportunityStageChanged" && !string.IsNullOrWhiteSpace(r.StageFilter)
					&& !string.Equals(r.StageFilter, stage, StringComparison.OrdinalIgnoreCase)) continue;

				if (r.ActionType == "CreateActivity")
				{
					_db.Activities.Add(new Activity
					{
						CompanyID = companyId,
						Type = string.IsNullOrWhiteSpace(r.ActivityType) ? "Task" : r.ActivityType!,
						Subject = string.IsNullOrWhiteSpace(r.Subject) ? r.Name : r.Subject!,
						SubjectEn = string.IsNullOrWhiteSpace(r.SubjectEn) ? r.NameEn : r.SubjectEn,
						DueDate = DateTime.UtcNow.AddDays(r.DueInDays),
						LeadId = leadId, OpportunityId = oppId,
						EntityType = leadId.HasValue ? "Lead" : (oppId.HasValue ? "Opportunity" : null),
						EntityId = leadId ?? oppId,
						OwnerEmployeeId = ownerEmployeeId, Done = false, CreatedAt = DateTime.UtcNow, CreatedBy = "automation"
					});
					await _db.SaveChangesAsync();
					fired++;
				}
				else if (r.ActionType == "Notify" && ownerEmployeeId.HasValue)
				{
					var title = string.IsNullOrWhiteSpace(r.NotifyTitle) ? r.Name : r.NotifyTitle!;
					var body = r.NotifyBody ?? "";
					await _notifications.NotifyAsync(ownerEmployeeId.Value, title, title, body, body, "crm_automation", leadId ?? oppId);
					fired++;
				}
			}
			return fired;
		}
	}
}
