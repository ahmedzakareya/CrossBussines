using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// END-OF-SERVICE CLEARANCE (2026-11-09).
	//
	// When an employee's exit starts, every item of the company's clearance checklist (the rule pack's Clearance list — a
	// setting, edited on /Admin/HrRules) becomes ONE follow-up task linked to the employee: custody returned, system access
	// closed, work handed over, advances settled… The settlement screen shows each task's state, so nobody posts a final
	// settlement without seeing what is still open.
	//
	// Idempotent through TaskAutoLog ("HrClearance:<employee>:<item code>"): starting twice creates nothing the second time,
	// and an item added to the checklist later is created by the next start. Routed by TaskAutoRule "HrClearance"
	// (DefaultAssigneeEmployeeId; 0 = unassigned, for a manager to route) like the other HR follow-ups.
	// ==========================================================================================
	public sealed record ClearanceStatus(HrClearanceItem Item, int? TaskId, string? TaskStatus)
	{
		public bool Done => TaskStatus == "Done";
	}

	public sealed class HrClearanceService
	{
		public const string RuleType = "HrClearance";
		public const int DueDays = 7;
		private readonly CrossDbContext _db;
		public HrClearanceService(CrossDbContext db) { _db = db; }

		/// Used only when a company's pack carries no checklist of its own.
		public static readonly IReadOnlyList<HrClearanceItem> Defaults = new List<HrClearanceItem>
		{
			new() { Code = "custody", NameAr = "استلام العهد والأصول", NameEn = "Return custody and assets" },
			new() { Code = "access", NameAr = "إغلاق حسابات الأنظمة والبريد", NameEn = "Close system and email access" },
			new() { Code = "handover", NameAr = "تسليم المهام والملفات", NameEn = "Hand over work and files" },
			new() { Code = "finance", NameAr = "تسوية السلف والعهد المالية", NameEn = "Settle advances and cash custody" },
			new() { Code = "documents", NameAr = "استلام الوثائق وإلغاء الإقامة/التصاريح", NameEn = "Collect documents, cancel residence/permits" },
		};

		public static string Key(int employeeId, string code) => $"HrClearance:{employeeId}:{code}";

		private static string CodeOf(HrClearanceItem i) => string.IsNullOrWhiteSpace(i.Code) ? (i.NameEn ?? i.NameAr ?? "item").Trim().ToLowerInvariant().Replace(' ', '-') : i.Code.Trim();

		public async Task<IReadOnlyList<HrClearanceItem>> ChecklistAsync(int companyId, CancellationToken ct = default)
		{
			var pack = (await new HrRulePackService(_db).EffectiveAsync(companyId, ct))?.Rules;
			return pack is { Clearance.Count: > 0 } ? pack.Clearance : Defaults;
		}

		public async Task<List<ClearanceStatus>> StatusAsync(int companyId, int employeeId, CancellationToken ct = default)
		{
			var items = await ChecklistAsync(companyId, ct);
			var keys = items.Select(i => Key(employeeId, CodeOf(i))).ToList();
			var logs = await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey))
				.Select(l => new { l.RuleKey, l.TaskId }).ToListAsync(ct);
			var taskIds = logs.Select(l => l.TaskId).ToList();
			var tasks = await _db.TaskItems.AsNoTracking().Where(t => taskIds.Contains(t.ID)).Select(t => new { t.ID, t.Status }).ToListAsync(ct);
			return items.Select(i =>
			{
				var log = logs.FirstOrDefault(l => l.RuleKey == Key(employeeId, CodeOf(i)));
				var task = log == null ? null : tasks.FirstOrDefault(t => t.ID == log.TaskId);
				return new ClearanceStatus(i, task?.ID, task?.Status);
			}).ToList();
		}

		/// Creates the tasks not created yet; returns how many it created.
		public async Task<int> StartAsync(int companyId, int employeeId, int? byEmployeeId, CancellationToken ct = default)
		{
			var emp = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId && e.EmpCompanyID == companyId)
				.Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			if (emp == null) return 0;
			var rule = await _db.TaskAutoRules.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == RuleType, ct);
			var status = await StatusAsync(companyId, employeeId, ct);
			int created = 0;
			foreach (var s in status.Where(x => x.TaskId == null))
			{
				var code = CodeOf(s.Item);
				var who = emp.FullName ?? emp.FullNameEn ?? ("#" + employeeId);
				var whoEn = emp.FullNameEn ?? emp.FullName ?? ("#" + employeeId);
				var task = new TaskItem
				{
					CompanyId = companyId,
					Title = $"إخلاء طرف: {s.Item.NameAr} — {who}",
					TitleEn = $"Clearance: {s.Item.NameEn} — {whoEn}",
					AssigneeEmployeeId = rule?.DefaultAssigneeEmployeeId ?? 0,
					CreatedByEmployeeId = byEmployeeId ?? 0,
					Priority = "High",
					DueDate = DateTime.Today.AddDays(DueDays),
					Status = "New",
					ActualHours = 0m,
					EntityType = EntityRegistry.Employee,
					EntityId = employeeId,
					CreatedAt = DateTime.UtcNow,
				};
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				_db.TaskItems.Add(task);
				await _db.SaveChangesAsync(ct);
				_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = Key(employeeId, code), TaskId = task.ID, CreatedAt = DateTime.UtcNow });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				created++;
			}
			return created;
		}
	}
}
