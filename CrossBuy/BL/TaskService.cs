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
		// Title is the DISPLAY title: resolved for the current culture before this DTO leaves the service,
		// exactly like AssigneeName, AssigneeJobTitle, CustomerName and ExpectedPartyName already are. Every
		// caller therefore stays language-agnostic and no view has to know the rule.
		public string Title { get; set; } = "";
		// The two STORED values, kept alongside the resolved one. An editor must round-trip what is in the
		// database, not what the current culture happened to display: without TitleAr, opening the edit form
		// in an English UI would prefill the English twin into the primary Title box and the next save would
		// overwrite the Arabic title with it.
		public string TitleAr { get; set; } = "";
		public string? TitleEn { get; set; }
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
		public string? TitleEn { get; set; }      // optional English twin; blank stores null and falls back to Title
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
		/// The assignee picker. `companyId` is REQUIRED and is filtered in the query — see the
		/// implementation for why this may not be done in the view.
		Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId);

		// ---- ON HOLD / SLA PAUSE -------------------------------------------------------------------
		//
		// FIVE OPERATIONS, FOUR EVENTS. Approving a request and a manager's own direct hold both end in
		// ON HOLD and both record Task.HoldApproved, distinguished by `direct` in the payload; a fifth
		// event type would describe one fact twice. They stay two methods because only one of them can
		// be a self-approval.
		Task<(bool ok, string? error)> RequestHoldAsync(int companyId, int id, int actorEmployeeId, string? reason, CancellationToken cancellationToken = default);
		Task<(bool ok, string? error)> ApproveHoldAsync(int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default);
		Task<(bool ok, string? error)> RejectHoldAsync(int companyId, int id, int actorEmployeeId, string? decisionReason, CancellationToken cancellationToken = default);
		Task<(bool ok, string? error)> DirectHoldAsync(int companyId, int id, int actorEmployeeId, string? reason, CancellationToken cancellationToken = default);
		Task<(bool ok, string? error)> ResumeHoldAsync(int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default);

		/// The same authority question the operations ask, exposed so a screen can render the right
		/// actions instead of offering ones the service will refuse.
		Task<bool> CanDecideHoldAsync(int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default);
	}

	public class TaskService : ITaskService
	{
		private readonly CrossDbContext _db;
		// Tasks & Calendar integration (TAB 4). Both are REQUIRED, not optional: a transition that silently
		// skipped its event or its notification because a dependency was missing would be the worst of both
		// worlds — the behaviour would look wired and would not be.
		private readonly CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher _events;
		private readonly CrossBuy.BL.TasksCalendar.ITaskNotificationService _notify;

		// ---- HOLD DEPENDENCIES, ALL OPTIONAL ON PURPOSE --------------------------------------------
		//
		// Hold needs four things the rest of this service does not: who the caller really is, whether
		// they hold Manage, who the assignee's manager is, and a durable place to record the decision.
		// They arrive through a SECOND CONSTRUCTOR rather than by widening the existing one, so every
		// existing call site - and every existing test - keeps compiling unchanged.
		//
		// NULL IS A REFUSAL, NEVER A BYPASS. A Hold operation whose dependencies are absent returns an
		// error; it never proceeds on a weaker check. The events in particular are not optional in
		// behaviour: the row keeps CURRENT state only, so who asked, who decided and why exists nowhere
		// else, and a hold that cannot record its event must not happen.
		private readonly CrossBuy.BL.Platform.IOrgHierarchy? _hierarchy;
		private readonly CrossBuy.BL.Platform.IBusinessEventService? _businessEvents;
		private readonly CrossBuy.BL.ITasksAccessService? _access;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor? _contexts;

		public TaskService(CrossDbContext db,
			CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher events,
			CrossBuy.BL.TasksCalendar.ITaskNotificationService notify)
		{
			_db = db; _events = events; _notify = notify;
		}

		public TaskService(CrossDbContext db,
			CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher events,
			CrossBuy.BL.TasksCalendar.ITaskNotificationService notify,
			CrossBuy.BL.Platform.IBusinessEventService? businessEvents,
			CrossBuy.BL.Platform.IBusinessContextAccessor? contexts,
			CrossBuy.BL.ITasksAccessService? access,
			CrossBuy.BL.Platform.IOrgHierarchy? hierarchy)
			: this(db, events, notify)
		{
			_businessEvents = businessEvents; _contexts = contexts;
			_access = access; _hierarchy = hierarchy;
		}

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
			if (!string.IsNullOrWhiteSpace(q))
			{
				var term = q.Trim();
				// BOTH titles, whichever language the searcher typed. Matching Title alone meant an
				// English reader could not find a task by the English name in front of them.
				query = query.Where(t => t.Title.Contains(term)
					|| (t.TitleEn != null && t.TitleEn.Contains(term))
					|| (t.Description != null && t.Description.Contains(term)));
			}
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
					Id = t.ID, Title = t.Title, TitleAr = t.Title, TitleEn = t.TitleEn, Description = t.Description,
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
				// THE TASK'S OWN TITLE WAS THE ONE BILINGUAL FIELD NOBODY RESOLVED. TaskItems.TitleEn exists
				// in the entity and in the database, and nothing read it: the projection never selected it and
				// the grid printed t.Title raw, so an English UI showed Arabic titles beside English column
				// headers even for a task that DID carry an English title. Resolved here, next to the employee
				// name and the customer name, on the same isEn flag and with the same "fall back when the twin
				// is empty" rule the rest of this method uses.
				if (isEn && !string.IsNullOrWhiteSpace(r.TitleEn)) r.Title = r.TitleEn!;
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
			if (string.IsNullOrWhiteSpace(input.Title)) return (false, "Task title is required", 0);
			if (input.AssigneeEmployeeId <= 0) return (false, "An assignee must be chosen for the task", 0);
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
			if (input.IsScheduled && !scheduled) return (false, "A scheduled task needs an expected transaction type and a party (supplier/customer)", 0);
			string? expType = scheduled ? input.ExpectedEntityType : null;
			string? expPartyType = scheduled ? (input.ExpectedEntityType == "PurchaseInvoice" ? "Supplier" : "Customer") : null;
			int? expPartyId = scheduled ? input.ExpectedPartyId : null;
			DateTime? expFrom = scheduled ? input.ExpectedFrom : null;
			DateTime? expTo = scheduled ? input.ExpectedTo : null;

			if (input.Id > 0)
			{
				var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == input.Id && x.CompanyId == companyId);
				if (t == null) return (false, "Task not found", 0);

				// Capture BEFORE mutating: an event and a notification both need the previous value, and
				// after the assignment below it is gone.
				int previousAssignee = t.AssigneeEmployeeId;
				DateTime? previousDue = t.DueDate;

				t.Title = input.Title.Trim();
				t.TitleEn = string.IsNullOrWhiteSpace(input.TitleEn) ? null : input.TitleEn.Trim();
				t.Description = input.Description;
				t.AssigneeEmployeeId = input.AssigneeEmployeeId; t.Priority = priority;
				t.DueDate = input.DueDate; t.EstimatedHours = input.EstimatedHours;
				t.ProgressPct = Math.Clamp(input.ProgressPct, 0, 100);
				t.Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim();
				t.EntityType = entType; t.EntityId = entId;
				t.IsBillable = billable; t.BillRate = billRate; t.CustomerId = custId;
				// TM-9: update scheduled criteria (never touch MatchedAt here — only the TM-9-ب matcher sets it)
				t.IsScheduled = scheduled; t.ExpectedEntityType = expType; t.ExpectedPartyType = expPartyType;
				t.ExpectedPartyId = expPartyId; t.ExpectedFrom = expFrom; t.ExpectedTo = expTo;

				bool assigneeChanged = previousAssignee != t.AssigneeEmployeeId;
				bool dueChanged = previousDue != t.DueDate;
				var correlation = Guid.NewGuid();

				// The state change and its events are ONE commit. RecordAsync runs inside this transaction,
				// so a failure anywhere below rolls the events back with the change — there is no path that
				// leaves an event describing something that did not happen.
				await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
				{
					await _db.SaveChangesAsync();

					if (assigneeChanged)
					{
						if (previousAssignee > 0)
							await _events.TaskReassignedAsync(t, previousAssignee, currentEmployeeId, correlation);
						else
							await _events.TaskAssignedAsync(t, currentEmployeeId, correlation);
					}
					if (dueChanged)
						await _events.TaskDueDateChangedAsync(t, previousDue, currentEmployeeId, correlation);

					await tx.CommitAsync();
				}

				// POST-COMMIT. A notification is a message about something that has happened; sending it
				// inside the transaction would announce a change that could still roll back.
				if (assigneeChanged)
				{
					if (previousAssignee > 0)
						await _notify.TaskReassignedAsync(t.ID, previousAssignee, currentEmployeeId, correlation);
					else
						await _notify.TaskAssignedAsync(t.ID, currentEmployeeId, correlation);
				}
				if (dueChanged)
					await _notify.TaskDueDateChangedAsync(t.ID, previousDue, currentEmployeeId, correlation);

				return (true, null, t.ID);
			}
			var nt = new TaskItem
			{
				CompanyId = companyId, Title = input.Title.Trim(),
				TitleEn = string.IsNullOrWhiteSpace(input.TitleEn) ? null : input.TitleEn.Trim(),
				Description = input.Description,
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

			var newCorrelation = Guid.NewGuid();
			await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
			{
				// Saved first so the event carries a real EntityId rather than 0.
				await _db.SaveChangesAsync();
				await _events.TaskCreatedAsync(nt, currentEmployeeId, newCorrelation);
				if (nt.AssigneeEmployeeId > 0)
					await _events.TaskAssignedAsync(nt, currentEmployeeId, newCorrelation);
				await tx.CommitAsync();
			}

			// Post-commit. Task.Created notifies nobody by design — creating a task is a timeline fact, and
			// notifying on it would bury the assignment that actually needs someone's attention.
			if (nt.AssigneeEmployeeId > 0)
				await _notify.TaskAssignedAsync(nt.ID, currentEmployeeId, newCorrelation);

			return (true, null, nt.ID);
		}

		public async Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId)
		{
			if (!Statuses.Contains(status)) return (false, "Unknown status");
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (t == null) return (false, "Task not found");
			if (t.Status == status) return (true, null);
			// A REFUSED transition returns before anything is written, so no event and no notification is
			// produced for a change that did not happen. Same for the unchanged-status early return above.
			if (!Transitions.TryGetValue(t.Status, out var allowed) || !allowed.Contains(status))
				return (false, $"Transition not allowed: {t.Status} → {status}");

			string previousStatus = t.Status;
			t.Status = status;
			t.CompletedAt = status == "Done" ? DateTime.UtcNow : (DateTime?)null;
			if (status == "Done") t.ProgressPct = 100;

			bool completed = status == "Done";
			bool reopened = previousStatus == "Done" && status != "Done";

			// COMPLETING A TASK ENDS ANY HOLD ON IT, and ends it SILENTLY. The work is finished, so the
			// pause has nothing left to pause and leaving the columns set would show a Done task as On
			// Hold forever. No Task.HoldResumed is recorded and NO DUE DATE IS EXTENDED: extending the
			// deadline of a task that was just completed would move a date nobody is waiting on, and
			// Task.Completed is already the event that says what happened.
			if (completed) ClearHoldState(t);

			var correlation = Guid.NewGuid();

			await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
			{
				await _db.SaveChangesAsync();

				// StatusChanged is always recorded; Completed/Reopened are the transitions people act on, so
				// they are recorded in addition rather than instead — a consumer that only cares about
				// completion should not have to parse a status pair to find it.
				await _events.TaskStatusChangedAsync(t, previousStatus, currentEmployeeId, correlation);
				if (completed) await _events.TaskCompletedAsync(t, previousStatus, currentEmployeeId, correlation);
				if (reopened) await _events.TaskReopenedAsync(t, previousStatus, currentEmployeeId, correlation);

				await tx.CommitAsync();
			}

			// Post-commit. StatusChanged notifies nobody by design (timeline only).
			if (completed) await _notify.TaskCompletedAsync(t.ID, currentEmployeeId, correlation);
			if (reopened) await _notify.TaskReopenedAsync(t.ID, currentEmployeeId, correlation);

			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (t == null) return (false, "Task not found");
			_db.TaskItems.Remove(t);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// THE COMPANY IS A PARAMETER, NOT AN ASSUMPTION.
		//
		// This read had NO company filter at all: it returned every Employee row in the database, so the
		// assignee picker on every Tasks screen listed other companies' staff by name. A company-65 user
		// saw all 23 employees; company 65 has one. That is a cross-company disclosure of personal data
		// through a dropdown, and it cannot be fixed in the view — hiding a name that was already sent to
		// the browser is not isolation. The filter belongs here, in the query.
		//
		// It fails CLOSED: an unresolved company lists nobody rather than everybody. The empty list is the
		// safe answer, because a picker with no options blocks an assignment while a picker with every
		// company's staff invites a cross-company one.
		public async Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId)
		{
			if (companyId <= 0) return new List<(int, string)>();

			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var list = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.FullName != null && e.FullName != "")
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToListAsync();
			return list
				.Select(e => (Id: e.ID, Name: (isEn && !string.IsNullOrWhiteSpace(e.FullNameEn)) ? e.FullNameEn! : e.FullName!))
				.OrderBy(x => x.Name, StringComparer.CurrentCulture)
				.ToList();
		}

		// =========================================================================================
		// ON HOLD / SLA PAUSE
		//
		// WHAT A HOLD IS. A task whose clock has been stopped by somebody entitled to stop it. The work
		// stays owned, stays assigned and keeps its status; the ONLY things that change are that the
		// sweeps stop acting on it, and that on resume the deadline moves by exactly the time granted.
		//
		// THERE IS NO OnHold STATUS, and everything below follows from that. The state is DERIVED from
		// which of two timestamps are set - see TaskItem's five hold columns and TaskStatuses.HoldState -
		// exactly as Overdue has always been derived rather than stored.
		//
		// THE EVENTS ARE THE AUDIT TRAIL, not decoration. The row keeps CURRENT state only, so who
		// asked, who decided, why and when exists NOWHERE else - there is no hold log table and no
		// second timeline. An operation that cannot record its event therefore REFUSES rather than
		// making a change nobody can account for, and every write below runs inside ScopedTx with
		// RecordAsync before the commit so the fact and its event share one fate (ADR-001).
		// =========================================================================================

		private const string TaskModule = "Tasks";

		/// The longest reason a hold may carry. Stated once; both write paths ask it, and
		/// deploy/sql/tasks_hold.sql declares the column NVARCHAR(500) to match.
		public const int HoldReasonMaxLength = 500;

		// REFUSED, NEVER TRUNCATED, and never stored blank. A silently shortened reason is a different
		// reason, and an empty one defeats the only thing that makes a pause reviewable afterwards.
		private static (bool ok, string? error, string? reason) NormalizeHoldReason(string? reason)
		{
			var trimmed = (reason ?? string.Empty).Trim();
			if (trimmed.Length == 0) return (false, "A hold reason is required", null);
			if (trimmed.Length > HoldReasonMaxLength)
				return (false, $"A hold reason may not exceed {HoldReasonMaxLength} characters", null);
			return (true, null, trimmed);
		}

		private static void ClearHoldState(TaskItem t)
		{
			t.HoldRequestedAtUtc = null;
			t.HoldRequestedByEmployeeId = null;
			t.HoldReason = null;
			t.HoldApprovedAtUtc = null;
			t.HoldApprovedByEmployeeId = null;
		}

		private async Task<TaskItem?> LoadHoldTargetAsync(int companyId, int id, CancellationToken ct)
		{
			// Company comes from the caller's resolved scope and is applied IN THE QUERY, so a foreign id
			// reads as absent - the same answer a missing id gives, which is what stops an id being probed.
			if (companyId <= 0 || id <= 0) return null;
			return await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId, ct);
		}

		// WHO MAY DECIDE A HOLD: company-wide task administration, or the CURRENT assignee's direct
		// manager. Both are resolved HERE rather than at the endpoint, because only one of them is a
		// permission - "is this caller the manager of the person doing this work" is a question about
		// the org tree at this instant, and the org tree has no permission key to gate on.
		//
		// MANAGE IS ASKED AT MODULE LEVEL - the company as target, no task id - and that is deliberate.
		// TasksAccessService answers a TASK-targeted question with `related` while a company is
		// BOOTSTRAP-OPEN, which would hand the assignee authority over their own hold; the module-level
		// question refuses Manage under bootstrap-open instead. Fail closed is the right default for the
		// right that grants a pause.
		//
		// EVERY MISSING DEPENDENCY IS A NO. Without the hierarchy there is no way to ask who the manager
		// is, and a decision that cannot resolve its own authority must refuse rather than assume.
		private async Task<bool> CanDecideHoldAsync(int companyId, TaskItem task, int actorEmployeeId, CancellationToken ct)
		{
			if (companyId <= 0 || actorEmployeeId <= 0) return false;

			// THE ACTOR MUST BE THE RESOLVED CALLER, and it is checked BEFORE EITHER authority rather
			// than only before the permission one. The manager branch below reads nothing but the
			// employee id it was handed, so without this a caller could pass a colleague's id and borrow
			// that person's place in the org tree. It is also what refuses an EXTERNAL PORTAL caller
			// here as well as at the endpoint: a portal context carries no employee at all, so it can
			// equal no actor. No context accessor at all is likewise a refusal, not a bypass.
			if (_contexts == null) return false;

			var context = await _contexts.TryGetCurrentAsync(ct);
			if (context == null || context.CompanyId != companyId || context.EmployeeId != actorEmployeeId)
				return false;

			if (_access != null && await _access.CanAsync(context, TasksActions.Manage,
					new CrossBuy.Models.Platform.PermissionTarget { CompanyId = companyId }, ct))
				return true;

			// THE DIRECT MANAGER OF THE CURRENT ASSIGNEE, RESOLVED NOW. Nothing about the manager is
			// stored on the task, so a hierarchy change takes effect immediately in both directions: the
			// new manager may decide, and the former one may not merely because they held the chair when
			// the request was made. DirectManagerAsync intersects with the company, which is what refuses
			// a manager who sits above the employee's node but belongs to another tenant.
			if (_hierarchy == null || task.AssigneeEmployeeId <= 0) return false;
			var manager = await _hierarchy.DirectManagerAsync(companyId, task.AssigneeEmployeeId, ct);
			return manager != null && manager.Value == actorEmployeeId;
		}

		/// The same question, asked by the screen. A missing or foreign task answers false rather than
		/// throwing: the caller then renders nothing, and the operations refuse it anyway.
		public async Task<bool> CanDecideHoldAsync(
			int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default)
		{
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			return t != null && await CanDecideHoldAsync(companyId, t, actorEmployeeId, cancellationToken);
		}

		// ONE TRANSACTION PER OPERATION: the row change is saved, the event is recorded inside the same
		// ScopedTx, and only then is it committed. A RecordAsync that throws takes the state change down
		// with it, so no event can claim a transition that did not happen - and no transition can happen
		// without its event.
		private async Task RecordHoldAsync(TaskItem task, string eventType, int actorEmployeeId,
			string occurrence, object payload, CancellationToken ct)
		{
			var correlation = Guid.NewGuid();
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			await _db.SaveChangesAsync(ct);
			await _businessEvents!.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.TasksCalendar.TaskCalendarEntityCodes.Task,
				EntityId = task.ID,
				EventType = eventType,
				Payload = payload,
				PayloadVersion = 1,
				Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,
				CorrelationId = correlation,
				CompanyIdOverride = task.CompanyId,
				ActorEmployeeIdOverride = actorEmployeeId,
				OccurredAt = CrossBuy.BL.TasksCalendar.TaskCalendarTime.UtcNow(),
				// Deterministic per (entity, event, occurrence): a retried save records ONE event, and
				// each hold CYCLE is its own occurrence.
				DedupKey = $"{eventType}:{task.CompanyId}:{task.ID}:{occurrence}"
			}, ct);
			await tx.CommitAsync();
		}

		private static DateTime HoldNowUtc() => CrossBuy.BL.TasksCalendar.TaskCalendarTime.UtcNow();

		private static DateTime? HoldUtc(DateTime? value) =>
			value.HasValue ? CrossBuy.BL.TasksCalendar.TaskCalendarTime.AssumeUtc(value.Value) : null;

		// FULL PRECISION, UNLIKE THE PUBLISHER'S SECOND-RESOLUTION STAMP, and the difference is the
		// thing being stamped. The publisher keys an occurrence on a DUE DATE - a date somebody chose,
		// where two events a second apart are the same missed deadline. A hold occurrence is keyed on an
		// INSTANT the system recorded, and two hold cycles on one task genuinely can open within the
		// same second. At second resolution the second cycle's event collapses into the first cycle's
		// dedup key and is silently never recorded - a pause with no audit row.
		//
		// RETRY PROTECTION IS NOT WEAKENED, because approve, reject and resume all stamp a value ALREADY
		// STORED ON THE ROW (the request instant, or the approval instant), so a retried decision
		// computes the same key. Only the request stamps its own new instant, and a repeated request is
		// refused as a no-op before it ever reaches an event.
		private static string HoldStamp(DateTime value) =>
			CrossBuy.BL.TasksCalendar.TaskCalendarTime.AssumeUtc(value)
				.ToString("yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);

		// ---- REQUEST -----------------------------------------------------------------------------
		//
		// ASKING IS NOT BEING GRANTED. This writes the request pair and the reason and NOTHING else: no
		// approval timestamp, so the SLA keeps running, the overdue sweep keeps sweeping and escalation
		// keeps escalating until somebody with authority decides. That is the whole point of asking.
		public async Task<(bool ok, string? error)> RequestHoldAsync(
			int companyId, int id, int actorEmployeeId, string? reason, CancellationToken cancellationToken = default)
		{
			if (actorEmployeeId <= 0) return (false, "No acting employee");
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			if (t == null) return (false, "Task not found");
			if (TaskStatuses.IsTerminal(t.Status)) return (false, "A completed task cannot be put on hold");

			// IDEMPOTENT, AND THE FIRST REQUEST STANDS. A second submission must not restamp the request,
			// re-attribute it, or replace the reason the approver is about to read. Already held is the
			// same no-op: there is nothing left to ask for.
			if (t.HoldApprovedAtUtc != null || t.HoldRequestedAtUtc != null) return (true, null);

			var (reasonOk, reasonError, normalized) = NormalizeHoldReason(reason);
			if (!reasonOk) return (false, reasonError);
			if (_businessEvents == null) return (false, "Hold operations require the business event service");

			var nowUtc = HoldNowUtc();
			t.HoldRequestedAtUtc = nowUtc;
			t.HoldRequestedByEmployeeId = actorEmployeeId;
			t.HoldReason = normalized;

			await RecordHoldAsync(t, CrossBuy.BL.Platform.TaskEvents.HoldRequested,
				actorEmployeeId, $"requested:{HoldStamp(nowUtc)}", new
				{
					holdState = TaskStatuses.HoldStateRequested,
					requestedAtUtc = nowUtc,
					requestedByEmployeeId = actorEmployeeId,
					reason = normalized,
					newDueAt = HoldUtc(t.DueDate),
					newAssigneeId = t.AssigneeEmployeeId,
					sourceModule = TaskModule
				}, cancellationToken);

			return (true, null);
		}

		// ---- APPROVE -----------------------------------------------------------------------------
		//
		// THE MOMENT THE PAUSE STARTS. HoldApprovedAtUtc is not a decoration on the decision: it is the
		// clock the resume extension is measured from, which is why a repeat approval must never move it.
		public async Task<(bool ok, string? error)> ApproveHoldAsync(
			int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default)
		{
			if (actorEmployeeId <= 0) return (false, "No acting employee");
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			if (t == null) return (false, "Task not found");
			if (!await CanDecideHoldAsync(companyId, t, actorEmployeeId, cancellationToken))
				return (false, "You may not decide a hold on this task");

			// Already on hold: a no-op that leaves the approval timestamp exactly where it was. Moving it
			// forward would silently shorten the time given back at resume.
			if (t.HoldApprovedAtUtc != null) return (true, null);
			if (t.HoldRequestedAtUtc == null) return (false, "There is no pending hold request on this task");

			// THE RULE THAT SURVIVES EVERY AUTHORITY. The person who asked does not decide - not even
			// holding Manage, and not even being the assignee's own manager. A pause the requester can
			// grant themselves is not a control.
			if (t.HoldRequestedByEmployeeId == actorEmployeeId)
				return (false, "The employee who requested this hold may not approve it");

			if (_businessEvents == null) return (false, "Hold operations require the business event service");

			var nowUtc = HoldNowUtc();
			var requestedAt = t.HoldRequestedAtUtc.Value;
			t.HoldApprovedAtUtc = nowUtc;
			t.HoldApprovedByEmployeeId = actorEmployeeId;
			// The request pair and the reason are PRESERVED: they are the record of why this hold exists.

			await RecordHoldAsync(t, CrossBuy.BL.Platform.TaskEvents.HoldApproved,
				actorEmployeeId, $"approved:{HoldStamp(requestedAt)}", new
				{
					holdState = TaskStatuses.HoldStateOnHold,
					direct = false,
					requestedAtUtc = HoldUtc(t.HoldRequestedAtUtc),
					requestedByEmployeeId = t.HoldRequestedByEmployeeId,
					approvedAtUtc = nowUtc,
					approvedByEmployeeId = actorEmployeeId,
					reason = t.HoldReason,
					newDueAt = HoldUtc(t.DueDate),
					newAssigneeId = t.AssigneeEmployeeId,
					sourceModule = TaskModule
				}, cancellationToken);

			return (true, null);
		}

		// ---- REJECT ------------------------------------------------------------------------------
		//
		// Returns the task to NORMAL by clearing the request columns. The APPROVAL columns are not
		// touched and must already be null - rejecting a hold that has started is refused rather than
		// quietly becoming a resume, because those give back different amounts of time.
		//
		// THE DECISION REASON LIVES IN THE EVENT AND NOWHERE ELSE. HoldReason holds the REQUESTER's
		// words; overwriting them with the approver's would destroy the thing being decided about, and a
		// sixth column is not authorized. The event payload already carries decision context.
		public async Task<(bool ok, string? error)> RejectHoldAsync(
			int companyId, int id, int actorEmployeeId, string? decisionReason, CancellationToken cancellationToken = default)
		{
			if (actorEmployeeId <= 0) return (false, "No acting employee");
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			if (t == null) return (false, "Task not found");
			if (!await CanDecideHoldAsync(companyId, t, actorEmployeeId, cancellationToken))
				return (false, "You may not decide a hold on this task");

			if (t.HoldApprovedAtUtc != null)
				return (false, "This task is already on hold; it can be resumed but not rejected");
			if (t.HoldRequestedAtUtc == null)
				return (false, "There is no pending hold request on this task");
			if (_businessEvents == null) return (false, "Hold operations require the business event service");

			var requestedAt = t.HoldRequestedAtUtc.Value;
			var requestedBy = t.HoldRequestedByEmployeeId;
			var requestReason = t.HoldReason;
			var note = (decisionReason ?? string.Empty).Trim();
			if (note.Length > HoldReasonMaxLength) note = note.Substring(0, HoldReasonMaxLength);

			t.HoldRequestedAtUtc = null;
			t.HoldRequestedByEmployeeId = null;
			t.HoldReason = null;

			await RecordHoldAsync(t, CrossBuy.BL.Platform.TaskEvents.HoldRejected,
				actorEmployeeId, $"rejected:{HoldStamp(requestedAt)}", new
				{
					holdState = TaskStatuses.HoldStateNormal,
					requestedAtUtc = HoldUtc(requestedAt),
					requestedByEmployeeId = requestedBy,
					reason = requestReason,
					decisionReason = note.Length == 0 ? null : note,
					rejectedByEmployeeId = actorEmployeeId,
					newDueAt = HoldUtc(t.DueDate),
					newAssigneeId = t.AssigneeEmployeeId,
					sourceModule = TaskModule
				}, cancellationToken);

			return (true, null);
		}

		// ---- DIRECT HOLD -------------------------------------------------------------------------
		//
		// The manager's own act: no request pair is written, because none was made. The reason is still
		// mandatory - a pause nobody has to justify is a pause nobody can review - and it is the
		// MANAGER's reason, which is the one case where HoldReason is not a requester's words.
		//
		// NO FABRICATED Task.HoldRequested. Recording a request that never happened would put a person's
		// name against words they never wrote. The single Task.HoldApproved carries direct = true.
		//
		// A PENDING REQUEST BLOCKS IT, deliberately. With a request outstanding the right question is
		// approve or reject, and allowing a direct hold instead would let a Manage-holding requester
		// reach ON HOLD on their own request by another name - the exact thing self-approval forbids.
		public async Task<(bool ok, string? error)> DirectHoldAsync(
			int companyId, int id, int actorEmployeeId, string? reason, CancellationToken cancellationToken = default)
		{
			if (actorEmployeeId <= 0) return (false, "No acting employee");
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			if (t == null) return (false, "Task not found");
			if (!await CanDecideHoldAsync(companyId, t, actorEmployeeId, cancellationToken))
				return (false, "You may not decide a hold on this task");

			if (TaskStatuses.IsTerminal(t.Status)) return (false, "A completed task cannot be put on hold");
			if (t.HoldApprovedAtUtc != null) return (true, null);   // already held: no-op, timestamp unmoved
			if (t.HoldRequestedAtUtc != null)
				return (false, "A hold request is pending on this task; approve or reject it");

			var (reasonOk, reasonError, normalized) = NormalizeHoldReason(reason);
			if (!reasonOk) return (false, reasonError);
			if (_businessEvents == null) return (false, "Hold operations require the business event service");

			var nowUtc = HoldNowUtc();
			t.HoldReason = normalized;
			t.HoldApprovedAtUtc = nowUtc;
			t.HoldApprovedByEmployeeId = actorEmployeeId;

			await RecordHoldAsync(t, CrossBuy.BL.Platform.TaskEvents.HoldApproved,
				actorEmployeeId, $"direct:{HoldStamp(nowUtc)}", new
				{
					holdState = TaskStatuses.HoldStateOnHold,
					direct = true,
					requestedAtUtc = (DateTime?)null,
					requestedByEmployeeId = (int?)null,
					approvedAtUtc = nowUtc,
					approvedByEmployeeId = actorEmployeeId,
					reason = normalized,
					newDueAt = HoldUtc(t.DueDate),
					newAssigneeId = t.AssigneeEmployeeId,
					sourceModule = TaskModule
				}, cancellationToken);

			return (true, null);
		}

		// ---- RESUME ------------------------------------------------------------------------------
		//
		// GIVES BACK EXACTLY THE TIME THAT WAS TAKEN: resume minus approval, added to whatever the due
		// date is NOW. If somebody edited the due date during the hold, that edit stands and the granted
		// time is added on top of it - reconstructing the pre-edit date would silently undo a deliberate
		// decision.
		//
		// NO DUE DATE, NO INVENTION. A task that legitimately has no deadline has nothing to extend, so
		// resume clears the hold and leaves DueDate null rather than manufacturing one.
		//
		// IT CANNOT EXTEND TWICE, because the approval timestamp it measures from is cleared with
		// everything else - a second resume finds nothing on hold and is a no-op.
		public async Task<(bool ok, string? error)> ResumeHoldAsync(
			int companyId, int id, int actorEmployeeId, CancellationToken cancellationToken = default)
		{
			if (actorEmployeeId <= 0) return (false, "No acting employee");
			var t = await LoadHoldTargetAsync(companyId, id, cancellationToken);
			if (t == null) return (false, "Task not found");
			if (!await CanDecideHoldAsync(companyId, t, actorEmployeeId, cancellationToken))
				return (false, "You may not decide a hold on this task");

			// Not on hold: nothing to resume and nothing to extend. A repeated submission is harmless.
			if (t.HoldApprovedAtUtc == null) return (true, null);

			var approvedAt = CrossBuy.BL.TasksCalendar.TaskCalendarTime.AssumeUtc(t.HoldApprovedAtUtc.Value);
			var resumeUtc = HoldNowUtc();
			var held = resumeUtc - approvedAt;
			// A negative duration means the two timestamps disagree about the clock. Refuse rather than
			// SHORTEN the deadline: a repair is somebody's decision, not an arithmetic side effect.
			if (held < TimeSpan.Zero) return (false, "The hold cannot end before it began");
			if (_businessEvents == null) return (false, "Hold operations require the business event service");

			var previousDue = t.DueDate;
			if (t.DueDate.HasValue) t.DueDate = t.DueDate.Value + held;
			ClearHoldState(t);

			await RecordHoldAsync(t, CrossBuy.BL.Platform.TaskEvents.HoldResumed,
				actorEmployeeId, $"resumed:{HoldStamp(approvedAt)}", new
				{
					holdState = TaskStatuses.HoldStateNormal,
					approvedAtUtc = approvedAt,
					resumedAtUtc = resumeUtc,
					resumedByEmployeeId = actorEmployeeId,
					heldSeconds = (long)Math.Round(held.TotalSeconds),
					previousDueAt = HoldUtc(previousDue),
					newDueAt = HoldUtc(t.DueDate),
					newAssigneeId = t.AssigneeEmployeeId,
					sourceModule = TaskModule
				}, cancellationToken);

			return (true, null);
		}
	}
}
