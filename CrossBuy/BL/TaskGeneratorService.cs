using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-7: turns system state into tasks via toggleable RULES, run periodically by TaskGeneratorHostedService.
	// Operational only (creates TaskItems — NO GL/stock). A per-(rule,source) key in TaskAutoLog prevents duplicates,
	// so the same event never spawns the task twice (e.g. an overdue invoice won't regenerate every cycle).
	public class TaskAutoRuleDto { public int Id { get; set; } public string RuleType { get; set; } = ""; public string LabelAr { get; set; } = ""; public string LabelEn { get; set; } = ""; public bool IsActive { get; set; } public int? DefaultAssigneeEmployeeId { get; set; } }

	public interface ITaskGeneratorService
	{
		Task EnsureRulesAsync(int companyId);
		Task<List<TaskAutoRuleDto>> GetRulesAsync(int companyId);
		Task<(bool ok, string? error)> SetRuleAsync(int companyId, string ruleType, bool isActive, int? defaultAssigneeEmployeeId);
		Task<Dictionary<string, int>> RunAsync(int companyId);   // returns created-count per rule
	}

	public class TaskGeneratorService : ITaskGeneratorService
	{
		private readonly CrossDbContext _db;
		public TaskGeneratorService(CrossDbContext db) { _db = db; }
		private const int CapPerRule = 200;

		// the rule catalogue (type → key prefix, labels, task priority, linked entity type)
		private static readonly (string type, string prefix, string ar, string en, string priority, string entity)[] Rules =
		{
			("LowStock",           "LowStock",     "نقص مخزون → اطلب بضاعة",       "Low stock → order",          "High",   "Item"),
			("OverdueInvoice",     "OverdueInv",   "فاتورة متأخرة → حصّل",          "Overdue invoice → collect",  "High",   "SalesInvoice"),
			("WorkOrderQc",        "WoQc",         "أمر تشغيل اكتمل → راجع الجودة", "WO completed → QC",          "Normal", "ManufWorkOrder"),
			("DeliveryReady",      "Deliver",      "طلب توصيل جاهز → وصّل",         "Delivery ready → deliver",   "Urgent", "PosOrder"),
			("NewEmployeeOnboard", "Onboard",      "موظف جديد → جهّز الأوراق",      "New employee → onboard",     "Normal", "Employee"),
		};

		public async Task EnsureRulesAsync(int companyId)
		{
			var existing = await _db.TaskAutoRules.Where(r => r.CompanyId == companyId).Select(r => r.RuleType).ToListAsync();
			foreach (var r in Rules)
				if (!existing.Contains(r.type))
					_db.TaskAutoRules.Add(new TaskAutoRule { CompanyId = companyId, RuleType = r.type, IsActive = true, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
		}

		public async Task<List<TaskAutoRuleDto>> GetRulesAsync(int companyId)
		{
			await EnsureRulesAsync(companyId);
			var rows = await _db.TaskAutoRules.AsNoTracking().Where(r => r.CompanyId == companyId).ToListAsync();
			return Rules.Select(def =>
			{
				var row = rows.First(x => x.RuleType == def.type);
				return new TaskAutoRuleDto { Id = row.ID, RuleType = def.type, LabelAr = def.ar, LabelEn = def.en, IsActive = row.IsActive, DefaultAssigneeEmployeeId = row.DefaultAssigneeEmployeeId };
			}).ToList();
		}

		public async Task<(bool ok, string? error)> SetRuleAsync(int companyId, string ruleType, bool isActive, int? defaultAssigneeEmployeeId)
		{
			await EnsureRulesAsync(companyId);
			var r = await _db.TaskAutoRules.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.RuleType == ruleType);
			if (r == null) return (false, "Rule not found");
			r.IsActive = isActive; r.DefaultAssigneeEmployeeId = defaultAssigneeEmployeeId > 0 ? defaultAssigneeEmployeeId : null;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// candidate source records for a rule → (sourceId, title)
		// BOTH TITLES, composed at the same place and from the same subject.
		//
		// These titles are WRITTEN BY THE SYSTEM, so an English twin is not a translation someone has to
		// remember to type — it is the same sentence in the other language, and the subject (an invoice
		// number, a work-order number, an item, an employee) is carried across unchanged or taken from
		// that record's own English name. Before this, every generated task stored an Arabic sentence and
		// nothing else, so TaskItems.TitleEn was null on all 328 of them and an English UI had nothing to
		// fall back to.
		private async Task<List<(int id, string title, string titleEn)>> CandidatesAsync(int companyId, string ruleType)
		{
			var today = DateTime.Today;
			switch (ruleType)
			{
				case "LowStock":
				{
					// ReorderPoint lives per (item, warehouse) on ItemWarehouseSetting; an item low in ANY warehouse → one task (deduped by itemId)
					var settings = await _db.ItemWarehouseSettings.AsNoTracking().Where(s => s.ReorderPoint != null && s.ReorderPoint > 0).Select(s => new { s.ItemId, s.WarehouseId, Rop = s.ReorderPoint!.Value }).ToListAsync();
					if (settings.Count == 0) return new();
					var itemIds = settings.Select(s => s.ItemId).Distinct().ToList();
					var names = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive && itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Name);
					var namesEn = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive && itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.NameEn);
					var stock = (await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && itemIds.Contains(b.ItemId)).Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand }).ToListAsync())
						.ToDictionary(x => (x.ItemId, x.WarehouseId), x => x.QtyOnHand);
					var low = new HashSet<int>();
					foreach (var s in settings)
					{
						if (!names.ContainsKey(s.ItemId)) continue;   // not this company's active item
						var q = stock.TryGetValue((s.ItemId, s.WarehouseId), out var qty) ? qty : 0m;
						if (q < s.Rop) low.Add(s.ItemId);
					}
					return low.Take(CapPerRule).Select(id => (id,
						$"Order goods: {names[id]}",
						$"Reorder stock: {(namesEn.TryGetValue(id, out var ne) && !string.IsNullOrWhiteSpace(ne) ? ne : names[id])}")).ToList();
				}
				case "OverdueInvoice":
					return (await _db.SalesInvoices.AsNoTracking().Where(v => v.CompanyID == companyId && v.Status == "Posted" && v.InvoiceDate < today.AddDays(-30)).OrderBy(v => v.ID).Take(CapPerRule).Select(v => new { v.ID, v.InvoiceNo }).ToListAsync()).Select(v => (v.ID,
						$"Collect an overdue invoice: {v.InvoiceNo ?? ("#" + v.ID)}",
						$"Collect an overdue invoice: {v.InvoiceNo ?? ("#" + v.ID)}")).ToList();
				case "WorkOrderQc":
					return (await _db.ManufWorkOrders.AsNoTracking().Where(w => w.CompanyID == companyId && w.Status == "Completed").OrderBy(w => w.ID).Take(CapPerRule).Select(w => new { w.ID, w.WoNo }).ToListAsync()).Select(w => (w.ID,
						$"Check the quality of work order: {w.WoNo ?? ("#" + w.ID)}",
						$"Review work-order quality: {w.WoNo ?? ("#" + w.ID)}")).ToList();
				case "DeliveryReady":
					return (await _db.PosOrders.AsNoTracking().Where(o => o.CompanyId == companyId && o.OrderType == "Delivery" && o.DeliveryStatus == null && o.Status != "Voided").OrderBy(o => o.ID).Take(CapPerRule).Select(o => new { o.ID, o.ReceiptNo }).ToListAsync()).Select(o => (o.ID,
						$"Deliver the order: {o.ReceiptNo ?? ("#" + o.ID)}",
						$"Deliver the order: {o.ReceiptNo ?? ("#" + o.ID)}")).ToList();
				case "NewEmployeeOnboard":
					return (await _db.Employee.AsNoTracking().Where(e => e.DateOfJoining >= today.AddDays(-14)).OrderBy(e => e.ID).Take(CapPerRule).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync()).Select(e => (e.ID,
						$"Prepare the paperwork for a new employee: {e.FullName}",
						$"New employee paperwork: {(string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn)}")).ToList();
				default: return new();
			}
		}

		public async Task<Dictionary<string, int>> RunAsync(int companyId)
		{
			await EnsureRulesAsync(companyId);
			var summary = new Dictionary<string, int>();
			var activeRules = await _db.TaskAutoRules.AsNoTracking().Where(r => r.CompanyId == companyId && r.IsActive).ToListAsync();

			foreach (var def in Rules)
			{
				var rule = activeRules.FirstOrDefault(r => r.RuleType == def.type);
				if (rule == null) { summary[def.type] = 0; continue; }   // inactive → skip
				var candidates = await CandidatesAsync(companyId, def.type);
				if (candidates.Count == 0) { summary[def.type] = 0; continue; }

				var keys = candidates.Select(c => $"{def.prefix}:{c.id}").ToList();
				var existing = await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey)).Select(l => l.RuleKey).ToListAsync();
				var existingSet = new HashSet<string>(existing);
				int created = 0;
				foreach (var c in candidates)
				{
					var key = $"{def.prefix}:{c.id}";
					if (existingSet.Contains(key)) continue;   // already generated for this (rule, source) → dedupe
					var task = new TaskItem
					{
						CompanyId = companyId, Title = c.title, TitleEn = c.titleEn, Description = null,
						AssigneeEmployeeId = rule.DefaultAssigneeEmployeeId ?? 0,   // 0 = unassigned (manager routes it)
						CreatedByEmployeeId = 0,                                    // 0 = system/auto
						Priority = def.priority, Status = "New", ActualHours = 0m,
						EntityType = def.entity, EntityId = c.id, CreatedAt = DateTime.UtcNow
					};
					_db.TaskItems.Add(task);
					await _db.SaveChangesAsync();   // get task.ID
					_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = task.ID, CreatedAt = DateTime.UtcNow });
					await _db.SaveChangesAsync();
					created++;
				}
				summary[def.type] = created;
			}
			return summary;
		}
	}
}
