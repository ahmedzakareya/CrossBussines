using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-1: task CRUD + status transitions. Operational ONLY — this service NEVER touches GL or stock.
	// (Accounting value — cost/billing/payroll — arrives in TM-4/TM-5/TM-6 via the EXISTING services, no new writer.)
	public class TaskRowDto
	{
		public int Id { get; set; }
		public string Title { get; set; } = "";
		public string? Description { get; set; }
		public int AssigneeEmployeeId { get; set; }
		public string AssigneeName { get; set; } = "";
		public string? AssigneePhoto { get; set; }
		public string? AssigneeJobTitle { get; set; }
		public int ProgressPct { get; set; }
		public string? Category { get; set; }
		public int CreatedByEmployeeId { get; set; }
		public string CreatedByName { get; set; } = "";
		public string Priority { get; set; } = "Normal";
		public DateTime? DueDate { get; set; }
		public string Status { get; set; } = "New";
		public bool IsOverdue { get; set; }          // derived: DueDate < now && Status != Done
		public decimal? EstimatedHours { get; set; }
		public decimal ActualHours { get; set; }
		public DateTime CreatedAt { get; set; }
		public string? EntityType { get; set; }   // TM-2
		public int? EntityId { get; set; }
		public bool IsBillable { get; set; }       // TM-5
		public decimal? BillRate { get; set; }
		public int? CustomerId { get; set; }
		public string? CustomerName { get; set; }
		// TM-9 (scheduled)
		public bool IsScheduled { get; set; }
		public string? ExpectedEntityType { get; set; }
		public string? ExpectedPartyType { get; set; }
		public int? ExpectedPartyId { get; set; }
		public string? ExpectedPartyName { get; set; }   // resolved for display
		public DateTime? ExpectedFrom { get; set; }
		public DateTime? ExpectedTo { get; set; }
		public DateTime? MatchedAt { get; set; }
	}

	public class TaskSaveInput
	{
		public int Id { get; set; }
		public string Title { get; set; } = "";
		public string? Description { get; set; }
		public int AssigneeEmployeeId { get; set; }
		public string Priority { get; set; } = "Normal";
		public DateTime? DueDate { get; set; }
		public decimal? EstimatedHours { get; set; }
		public int ProgressPct { get; set; }       // 0..100
		public string? Category { get; set; }
		public string? EntityType { get; set; }   // TM-2 — optional link (blank = unlinked)
		public int? EntityId { get; set; }
		public bool IsBillable { get; set; }       // TM-5
		public decimal? BillRate { get; set; }
		public int? CustomerId { get; set; }
		// TM-9 (scheduled) — ExpectedPartyType is DERIVED from ExpectedEntityType (not bound)
		public bool IsScheduled { get; set; }
		public string? ExpectedEntityType { get; set; }
		public int? ExpectedPartyId { get; set; }
		public DateTime? ExpectedFrom { get; set; }
		public DateTime? ExpectedTo { get; set; }
	}

	public class TaskKpiDto { public int Total { get; set; } public int Urgent { get; set; } public int Overdue { get; set; } public int InProgress { get; set; } public int Done { get; set; } }

	public interface ITaskService
	{
		Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId, string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null);
		Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status, string? priority, string? q, string? view = null, int? assignee = null);
		Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId);
		Task<TaskItem?> GetAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId);
		Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
		Task<List<(int Id, string Name)>> ActiveEmployeesAsync();
	}

	public class TaskService : ITaskService
	{
		private readonly CrossDbContext _db;
		public TaskService(CrossDbContext db) { _db = db; }

		private static readonly string[] Priorities = { "Low", "Normal", "High", "Urgent" };
		private static readonly string[] Statuses = { "New", "InProgress", "Done" };
		// allowed status transitions (New↔InProgress, InProgress→Done, Done→InProgress reopen, quick New→Done)
		private static readonly Dictionary<string, string[]> Transitions = new()
		{
			["New"] = new[] { "InProgress", "Done" },
			["InProgress"] = new[] { "New", "Done" },
			["Done"] = new[] { "InProgress" },
		};

		// shared WHERE builder for list + count (same filters → consistent paging). `view` = quick status/priority tab.
		private IQueryable<TaskItem> Filter(int companyId, string scope, int currentEmployeeId, string? status, string? priority, string? q, string? view = null, int? assignee = null)
		{
			var query = _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId);
			if (scope == "mine") query = query.Where(t => t.AssigneeEmployeeId == currentEmployeeId);
			if (assignee.HasValue && assignee.Value > 0) query = query.Where(t => t.AssigneeEmployeeId == assignee.Value);
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(t => t.Status == status);
			if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(t => t.Priority == priority);
			if (!string.IsNullOrWhiteSpace(q)) { var term = q.Trim(); query = query.Where(t => t.Title.Contains(term) || (t.Description != null && t.Description.Contains(term))); }
			var now = DateTime.Now;
			switch (view)
			{
				case "urgent": query = query.Where(t => t.Priority == "Urgent" && t.Status != "Done"); break;
				case "overdue": query = query.Where(t => t.DueDate != null && t.DueDate < now && t.Status != "Done"); break;
				case "inprogress": query = query.Where(t => t.Status == "InProgress" || t.Status == "New"); break;
				case "done": query = query.Where(t => t.Status == "Done"); break;
			}
			return query;
		}

		public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status, string? priority, string? q, string? view = null, int? assignee = null)
			=> Filter(companyId, scope, currentEmployeeId, status, priority, q, view, assignee).CountAsync();

		public async Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId)
		{
			var now = DateTime.Now;
			var q = _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId);
			if (scope == "mine") q = q.Where(t => t.AssigneeEmployeeId == currentEmployeeId);
			var rows = await q.Select(t => new { t.Status, t.Priority, t.DueDate }).ToListAsync();
			return new TaskKpiDto
			{
				Total = rows.Count,
				Urgent = rows.Count(x => x.Priority == "Urgent" && x.Status != "Done"),
				Overdue = rows.Count(x => x.DueDate != null && x.DueDate < now && x.Status != "Done"),
				InProgress = rows.Count(x => x.Status == "InProgress" || x.Status == "New"),
				Done = rows.Count(x => x.Status == "Done"),
			};
		}

		public async Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId, string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null)
		{
			if (page < 1) page = 1;
			if (pageSize < 1 || pageSize > 200) pageSize = 25;
			var baseQ = Filter(companyId, scope, currentEmployeeId, status, priority, q, view, assignee);
			// column sort (the header chevrons); "col_desc" = descending. Default = smart open-first ordering.
			bool desc = sort != null && sort.EndsWith("_desc");
			string col = sort?.Replace("_desc", "").Replace("_asc", "") ?? "";
			IOrderedQueryable<TaskItem> ordered = col switch
			{
				"progress" => desc ? baseQ.OrderByDescending(t => t.ProgressPct) : baseQ.OrderBy(t => t.ProgressPct),
				"due" => desc ? baseQ.OrderByDescending(t => t.DueDate ?? DateTime.MinValue) : baseQ.OrderBy(t => t.DueDate ?? DateTime.MaxValue),
				"priority" => desc
					? baseQ.OrderByDescending(t => t.Priority == "Urgent" ? 4 : t.Priority == "High" ? 3 : t.Priority == "Normal" ? 2 : 1)
					: baseQ.OrderBy(t => t.Priority == "Urgent" ? 4 : t.Priority == "High" ? 3 : t.Priority == "Normal" ? 2 : 1),
				"status" => desc ? baseQ.OrderByDescending(t => t.Status) : baseQ.OrderBy(t => t.Status),
				_ => baseQ.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.ID)   // natural recency order → varied colors like the mockup (not all-red urgent-first)
			};
			var rows = await ordered
				.Skip((page - 1) * pageSize).Take(pageSize)        // server-side paging (no more 1000-row dumps)
				.Select(t => new TaskRowDto
				{
					Id = t.ID, Title = t.Title, Description = t.Description,
					AssigneeEmployeeId = t.AssigneeEmployeeId, CreatedByEmployeeId = t.CreatedByEmployeeId,
					Priority = t.Priority, DueDate = t.DueDate, Status = t.Status,
					EstimatedHours = t.EstimatedHours, ActualHours = t.ActualHours, CreatedAt = t.CreatedAt,
					ProgressPct = t.ProgressPct, Category = t.Category,
					EntityType = t.EntityType, EntityId = t.EntityId,
					IsBillable = t.IsBillable, BillRate = t.BillRate, CustomerId = t.CustomerId,
						IsScheduled = t.IsScheduled, ExpectedEntityType = t.ExpectedEntityType, ExpectedPartyType = t.ExpectedPartyType,
						ExpectedPartyId = t.ExpectedPartyId, ExpectedFrom = t.ExpectedFrom, ExpectedTo = t.ExpectedTo, MatchedAt = t.MatchedAt
				}).ToListAsync();

			var empIds = rows.SelectMany(r => new[] { r.AssigneeEmployeeId, r.CreatedByEmployeeId }).Distinct().ToList();
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var emps = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage, e.JobTitleID }).ToListAsync();
			var names = emps.ToDictionary(e => e.ID, e => ((isEn && !string.IsNullOrWhiteSpace(e.FullNameEn)) ? e.FullNameEn : e.FullName) ?? "");
			var photos = emps.ToDictionary(e => e.ID, e => e.ProfileImage);
			var jobIds = emps.Select(e => e.JobTitleID).Distinct().ToList();
			var jobs = await _db.JobTitles.AsNoTracking().Where(j => jobIds.Contains(j.ID)).ToDictionaryAsync(j => j.ID, j => ((isEn && !string.IsNullOrWhiteSpace(j.Title)) ? j.Title : (string.IsNullOrWhiteSpace(j.TitleAr) ? j.Title : j.TitleAr)) ?? "");
			var empJob = emps.ToDictionary(e => e.ID, e => jobs.TryGetValue(e.JobTitleID, out var jt) ? jt : "");
			var custIds = rows.Where(r => r.CustomerId > 0).Select(r => r.CustomerId!.Value).Distinct().ToList();
			var custNames = custIds.Count == 0 ? new Dictionary<int, string>() : await _db.Customers.AsNoTracking().Where(c => custIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => (isEn && c.NameEn != null && c.NameEn != "" ? c.NameEn : c.Name) ?? "");
			// TM-9: resolve expected-party names for scheduled rows (Vendor / Customer)
			var vendSchedIds = rows.Where(r => r.IsScheduled && r.ExpectedPartyType == "Supplier" && r.ExpectedPartyId > 0).Select(r => r.ExpectedPartyId!.Value).Distinct().ToList();
			var custSchedIds = rows.Where(r => r.IsScheduled && r.ExpectedPartyType == "Customer" && r.ExpectedPartyId > 0).Select(r => r.ExpectedPartyId!.Value).Distinct().ToList();
			var vendSchedNames = vendSchedIds.Count == 0 ? new Dictionary<int, string>() : await _db.Vendors.AsNoTracking().Where(v => vendSchedIds.Contains(v.ID)).ToDictionaryAsync(v => v.ID, v => (isEn && v.NameEn != null && v.NameEn != "" ? v.NameEn : v.Name) ?? "");
			var custSchedNames = custSchedIds.Count == 0 ? new Dictionary<int, string>() : await _db.Customers.AsNoTracking().Where(c => custSchedIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => (isEn && c.NameEn != null && c.NameEn != "" ? c.NameEn : c.Name) ?? "");
			var now = DateTime.Now;
			foreach (var r in rows)
			{
				r.AssigneeName = names.TryGetValue(r.AssigneeEmployeeId, out var an) ? an : "";
				r.AssigneePhoto = photos.TryGetValue(r.AssigneeEmployeeId, out var ap) ? ap : null;
				r.AssigneeJobTitle = empJob.TryGetValue(r.AssigneeEmployeeId, out var jt2) ? jt2 : "";
				r.CreatedByName = names.TryGetValue(r.CreatedByEmployeeId, out var cn) ? cn : "";
				if (r.CustomerId > 0 && custNames.TryGetValue(r.CustomerId.Value, out var cnm)) r.CustomerName = cnm;
				if (r.IsScheduled && r.ExpectedPartyId > 0)
				{
					if (r.ExpectedPartyType == "Supplier" && vendSchedNames.TryGetValue(r.ExpectedPartyId.Value, out var vn)) r.ExpectedPartyName = vn;
					else if (r.ExpectedPartyType == "Customer" && custSchedNames.TryGetValue(r.ExpectedPartyId.Value, out var cn2)) r.ExpectedPartyName = cn2;
				}
				r.IsOverdue = r.DueDate.HasValue && r.DueDate.Value < now && r.Status != "Done";
			}
			return rows;
		}

		public async Task<TaskItem?> GetAsync(int companyId, int id) =>
			await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(t => t.ID == id && t.CompanyId == companyId);

		public async Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId)
		{
			if (string.IsNullOrWhiteSpace(input.Title)) return (false, "عنوان المهمة مطلوب", 0);
			if (input.AssigneeEmployeeId <= 0) return (false, "يجب اختيار المسؤول عن المهمة", 0);
			var priority = Priorities.Contains(input.Priority) ? input.Priority : "Normal";
			// TM-2: normalize the optional link — either BOTH set or BOTH null (an unlinked task is valid)
			var linkTypes = new[] { "SalesInvoice", "Customer", "ManufWorkOrder", "PosOrder", "Employee", "Project", "Item" };
			string? entType = (!string.IsNullOrWhiteSpace(input.EntityType) && input.EntityId > 0 && linkTypes.Contains(input.EntityType)) ? input.EntityType : null;
			int? entId = entType != null ? input.EntityId : null;
			// TM-5: normalize billing — only keep rate/customer when billable
			bool billable = input.IsBillable;
			decimal? billRate = billable && input.BillRate > 0 ? input.BillRate : null;
			int? custId = billable && input.CustomerId > 0 ? input.CustomerId : null;
			// TM-9: normalize scheduled criteria — a valid set (type + party) or fully cleared. Party type is derived.
			var schedTypes = new[] { "PurchaseInvoice", "SalesInvoice" };
			bool scheduled = input.IsScheduled && !string.IsNullOrWhiteSpace(input.ExpectedEntityType) && schedTypes.Contains(input.ExpectedEntityType) && input.ExpectedPartyId > 0;
			if (input.IsScheduled && !scheduled) return (false, "المهمة المجدولة تحتاج نوع حركة متوقّع + طرف (مورّد/عميل)", 0);
			string? expType = scheduled ? input.ExpectedEntityType : null;
			string? expPartyType = scheduled ? (input.ExpectedEntityType == "PurchaseInvoice" ? "Supplier" : "Customer") : null;
			int? expPartyId = scheduled ? input.ExpectedPartyId : null;
			DateTime? expFrom = scheduled ? input.ExpectedFrom : null;
			DateTime? expTo = scheduled ? input.ExpectedTo : null;

			if (input.Id > 0)
			{
				var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == input.Id && x.CompanyId == companyId);
				if (t == null) return (false, "المهمة غير موجودة", 0);
				t.Title = input.Title.Trim(); t.Description = input.Description;
				t.AssigneeEmployeeId = input.AssigneeEmployeeId; t.Priority = priority;
				t.DueDate = input.DueDate; t.EstimatedHours = input.EstimatedHours;
				t.ProgressPct = Math.Clamp(input.ProgressPct, 0, 100);
				t.Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim();
				t.EntityType = entType; t.EntityId = entId;
				t.IsBillable = billable; t.BillRate = billRate; t.CustomerId = custId;
				// TM-9: update scheduled criteria (never touch MatchedAt here — only the TM-9-ب matcher sets it)
				t.IsScheduled = scheduled; t.ExpectedEntityType = expType; t.ExpectedPartyType = expPartyType;
				t.ExpectedPartyId = expPartyId; t.ExpectedFrom = expFrom; t.ExpectedTo = expTo;
				await _db.SaveChangesAsync();
				return (true, null, t.ID);
			}
			var nt = new TaskItem
			{
				CompanyId = companyId, Title = input.Title.Trim(), Description = input.Description,
				AssigneeEmployeeId = input.AssigneeEmployeeId, CreatedByEmployeeId = currentEmployeeId,
				Priority = priority, DueDate = input.DueDate, EstimatedHours = input.EstimatedHours,
				ProgressPct = Math.Clamp(input.ProgressPct, 0, 100),
				Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
				EntityType = entType, EntityId = entId,
				IsBillable = billable, BillRate = billRate, CustomerId = custId,
				IsScheduled = scheduled, ExpectedEntityType = expType, ExpectedPartyType = expPartyType,
				ExpectedPartyId = expPartyId, ExpectedFrom = expFrom, ExpectedTo = expTo,
				Status = "New", ActualHours = 0m, CreatedAt = DateTime.UtcNow
			};
			_db.TaskItems.Add(nt);
			await _db.SaveChangesAsync();
			return (true, null, nt.ID);
		}

		public async Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId)
		{
			if (!Statuses.Contains(status)) return (false, "حالة غير معروفة");
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (t == null) return (false, "المهمة غير موجودة");
			if (t.Status == status) return (true, null);
			if (!Transitions.TryGetValue(t.Status, out var allowed) || !allowed.Contains(status))
				return (false, $"انتقال غير مسموح: {t.Status} → {status}");
			t.Status = status;
			t.CompletedAt = status == "Done" ? DateTime.UtcNow : (DateTime?)null;
			if (status == "Done") t.ProgressPct = 100;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (t == null) return (false, "المهمة غير موجودة");
			_db.TaskItems.Remove(t);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<(int Id, string Name)>> ActiveEmployeesAsync()
		{
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var list = await _db.Employee.AsNoTracking()
				.Where(e => e.FullName != null && e.FullName != "")
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToListAsync();
			return list
				.Select(e => (Id: e.ID, Name: (isEn && !string.IsNullOrWhiteSpace(e.FullNameEn)) ? e.FullNameEn! : e.FullName!))
				.OrderBy(x => x.Name, StringComparer.CurrentCulture)
				.ToList();
		}
	}
}
