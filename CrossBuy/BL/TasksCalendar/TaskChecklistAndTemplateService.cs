using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASK CHECKLIST + TASK TEMPLATES
	//
	// Two features, one file, because the template applier CREATES checklist lines and would otherwise
	// reach across a service boundary for three lines of code.
	//
	// Neither feature changes TaskItem's existing behaviour:
	//   * checklist completion does NOT drive ProgressPct — that column has always been a human's
	//     judgement, and quietly recomputing it would change what an existing number means;
	//   * the template applier creates tasks through the EXISTING ITaskService, so every rule already
	//     wired there (validation, events, notifications, transaction) applies to a templated task
	//     exactly as it does to a hand-made one. There is no second task-creation path.
	// ==========================================================================================

	public sealed class ChecklistProgress
	{
		public required int TaskId { get; init; }
		public required int Total { get; init; }
		public required int Done { get; init; }
		public int Percent => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total, MidpointRounding.AwayFromZero);
	}

	public interface ITaskChecklistService
	{
		Task<List<TaskChecklistItem>> ForTaskAsync(int companyId, int taskId, CancellationToken ct = default);
		Task<(bool ok, string? error, int id)> AddAsync(int companyId, int taskId, string title, int? actorEmployeeId, CancellationToken ct = default);
		Task<(bool ok, string? error)> SetDoneAsync(int companyId, int id, bool done, int? actorEmployeeId, CancellationToken ct = default);
		Task<(bool ok, string? error)> RenameAsync(int companyId, int id, string title, CancellationToken ct = default);
		Task<(bool ok, string? error)> RemoveAsync(int companyId, int id, CancellationToken ct = default);
		Task<(bool ok, string? error)> ReorderAsync(int companyId, int taskId, IReadOnlyList<int> orderedIds, CancellationToken ct = default);
		Task<Dictionary<int, ChecklistProgress>> ProgressManyAsync(int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);

		/// The task a line belongs to. A caller that authorises on a task id it was HANDED would authorise
		/// the wrong task — the check has to start from the row.
		Task<int?> OwningTaskIdAsync(int companyId, int lineId, CancellationToken ct = default);
	}

	public sealed class TaskChecklistService : ITaskChecklistService
	{
		private readonly CrossDbContext _db;
		public TaskChecklistService(CrossDbContext db) { _db = db; }

		public Task<List<TaskChecklistItem>> ForTaskAsync(int companyId, int taskId, CancellationToken ct = default) =>
			_db.TaskChecklistItems.AsNoTracking()
				.Where(c => c.CompanyId == companyId && c.TaskId == taskId)
				.OrderBy(c => c.SortOrder).ThenBy(c => c.ID)
				.ToListAsync(ct);

		public async Task<(bool ok, string? error, int id)> AddAsync(
			int companyId, int taskId, string title, int? actorEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "الشركة غير محددة (unresolved company)", 0);
			if (string.IsNullOrWhiteSpace(title)) return (false, "نص البند مطلوب (a checklist line needs text)", 0);

			// The task must belong to THIS company — the company comes from the task row, never the caller.
			if (!await _db.TaskItems.AnyAsync(t => t.ID == taskId && t.CompanyId == companyId, ct))
				return (false, "المهمة غير موجودة (task not found)", 0);

			int next = (await _db.TaskChecklistItems
				.Where(c => c.CompanyId == companyId && c.TaskId == taskId)
				.Select(c => (int?)c.SortOrder).MaxAsync(ct) ?? 0) + 1;

			var item = new TaskChecklistItem
			{
				CompanyId = companyId, TaskId = taskId, Title = title.Trim(),
				SortOrder = next, CreatedAt = TaskCalendarTime.UtcNow(), CreatedByEmployeeId = actorEmployeeId
			};
			_db.TaskChecklistItems.Add(item);
			await _db.SaveChangesAsync(ct);
			return (true, null, item.ID);
		}

		public async Task<(bool ok, string? error)> SetDoneAsync(
			int companyId, int id, bool done, int? actorEmployeeId, CancellationToken ct = default)
		{
			var item = await _db.TaskChecklistItems.FirstOrDefaultAsync(c => c.ID == id && c.CompanyId == companyId, ct);
			if (item == null) return (false, "البند غير موجود (checklist line not found)");

			if (item.IsDone == done) return (true, null);   // idempotent, and writes nothing

			item.IsDone = done;
			item.DoneAt = done ? TaskCalendarTime.UtcNow() : null;
			item.DoneByEmployeeId = done ? actorEmployeeId : null;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RenameAsync(int companyId, int id, string title, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(title)) return (false, "نص البند مطلوب (a checklist line needs text)");
			var item = await _db.TaskChecklistItems.FirstOrDefaultAsync(c => c.ID == id && c.CompanyId == companyId, ct);
			if (item == null) return (false, "البند غير موجود (checklist line not found)");

			item.Title = title.Trim();
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveAsync(int companyId, int id, CancellationToken ct = default)
		{
			var item = await _db.TaskChecklistItems.FirstOrDefaultAsync(c => c.ID == id && c.CompanyId == companyId, ct);
			if (item == null) return (false, "البند غير موجود (checklist line not found)");

			_db.TaskChecklistItems.Remove(item);
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Reorder by difference, not by delete-and-reinsert: the ids keep their identity, so a line's
		/// done-state and its author survive a drag. The same rule the construction track learned as CR-01.
		public async Task<(bool ok, string? error)> ReorderAsync(
			int companyId, int taskId, IReadOnlyList<int> orderedIds, CancellationToken ct = default)
		{
			var items = await _db.TaskChecklistItems
				.Where(c => c.CompanyId == companyId && c.TaskId == taskId)
				.ToListAsync(ct);
			if (items.Count == 0) return (true, null);

			var known = items.Select(i => i.ID).ToHashSet();
			if (orderedIds.Any(id => !known.Contains(id)))
				return (false, "ترتيب يحتوي بندًا لا يخص هذه المهمة (the order names a line that is not on this task)");

			int n = 1;
			foreach (var id in orderedIds)
				items.First(i => i.ID == id).SortOrder = n++;
			// Anything the caller did not mention keeps its relative order, after the named ones.
			foreach (var rest in items.Where(i => !orderedIds.Contains(i.ID)).OrderBy(i => i.SortOrder))
				rest.SortOrder = n++;

			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<int?> OwningTaskIdAsync(int companyId, int lineId, CancellationToken ct = default)
		{
			if (companyId <= 0) return null;
			return await _db.TaskChecklistItems.AsNoTracking()
				.Where(c => c.CompanyId == companyId && c.ID == lineId)
				.Select(c => (int?)c.TaskId).FirstOrDefaultAsync(ct);
		}

		public async Task<Dictionary<int, ChecklistProgress>> ProgressManyAsync(
			int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
		{
			var result = new Dictionary<int, ChecklistProgress>();
			if (companyId <= 0 || taskIds.Count == 0) return result;

			var rows = await _db.TaskChecklistItems.AsNoTracking()
				.Where(c => c.CompanyId == companyId && taskIds.Contains(c.TaskId))
				.Select(c => new { c.TaskId, c.IsDone })
				.ToListAsync(ct);

			foreach (var id in taskIds.Distinct())
			{
				var mine = rows.Where(r => r.TaskId == id).ToList();
				result[id] = new ChecklistProgress { TaskId = id, Total = mine.Count, Done = mine.Count(m => m.IsDone) };
			}
			return result;
		}
	}

	// ==========================================================================================
	// TEMPLATES
	// ==========================================================================================

	public sealed class TemplateApplyResult
	{
		public required int TemplateId { get; init; }
		public required IReadOnlyList<int> CreatedTaskIds { get; init; }
		public required int ChecklistLinesCreated { get; init; }
		public required int DependenciesCreated { get; init; }
	}

	public interface ITaskTemplateService
	{
		Task<List<TaskTemplate>> ListAsync(int companyId, bool activeOnly = true, CancellationToken ct = default);
		Task<TaskTemplate?> GetAsync(int companyId, int id, CancellationToken ct = default);
		Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskTemplate input, int? actorEmployeeId, CancellationToken ct = default);
		Task<(bool ok, string? error)> SetActiveAsync(int companyId, int id, bool active, CancellationToken ct = default);

		Task<(bool ok, string? error, TemplateApplyResult? result)> ApplyAsync(
			int companyId, int templateId, DateTime anchorLocalDate, int? assigneeOverride,
			int currentEmployeeId, CancellationToken ct = default);
	}

	public sealed class TaskTemplateService : ITaskTemplateService
	{
		private readonly CrossDbContext _db;
		private readonly ITaskService _tasks;
		private readonly ITaskChecklistService _checklist;
		private readonly ITaskDependencyService _dependencies;

		public TaskTemplateService(CrossDbContext db, ITaskService tasks,
			ITaskChecklistService checklist, ITaskDependencyService dependencies)
		{
			_db = db; _tasks = tasks; _checklist = checklist; _dependencies = dependencies;
		}

		public Task<List<TaskTemplate>> ListAsync(int companyId, bool activeOnly = true, CancellationToken ct = default) =>
			_db.TaskTemplates.AsNoTracking().Include(t => t.Items)
				.Where(t => t.CompanyId == companyId && (!activeOnly || t.IsActive))
				.OrderBy(t => t.Name).ToListAsync(ct);

		public Task<TaskTemplate?> GetAsync(int companyId, int id, CancellationToken ct = default) =>
			_db.TaskTemplates.AsNoTracking().Include(t => t.Items)
				.FirstOrDefaultAsync(t => t.ID == id && t.CompanyId == companyId, ct);

		public async Task<(bool ok, string? error, int id)> SaveAsync(
			int companyId, TaskTemplate input, int? actorEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "الشركة غير محددة (unresolved company)", 0);
			if (string.IsNullOrWhiteSpace(input.Name)) return (false, "اسم القالب مطلوب (a template needs a name)", 0);

			var items = (input.Items ?? new()).Where(i => !string.IsNullOrWhiteSpace(i.Title)).ToList();
			if (items.Count == 0) return (false, "القالب يحتاج بندًا واحدًا على الأقل (a template needs at least one task)", 0);

			// A predecessor must name a sort order that exists in this template, and never itself —
			// otherwise applying the template would produce an edge the dependency service must reject,
			// and the failure would surface at apply time instead of at save time.
			var orders = items.Select(i => i.SortOrder).ToHashSet();
			foreach (var i in items)
			{
				if (i.PredecessorSortOrder is not int p) continue;
				if (p == i.SortOrder)
					return (false, $"البند «{i.Title}» يعتمد على نفسه (template item '{i.Title}' depends on itself)", 0);
				if (!orders.Contains(p))
					return (false, $"البند «{i.Title}» يشير إلى ترتيب غير موجود (template item '{i.Title}' names a predecessor that is not in this template)", 0);
			}

			TaskTemplate entity;
			if (input.ID > 0)
			{
				entity = await _db.TaskTemplates.Include(t => t.Items)
					.FirstOrDefaultAsync(t => t.ID == input.ID && t.CompanyId == companyId, ct)
					?? throw new InvalidOperationException("template not found");
				_db.TaskTemplateItems.RemoveRange(entity.Items);
				entity.Items.Clear();
				entity.UpdatedAt = TaskCalendarTime.UtcNow();
				entity.UpdatedByEmployeeId = actorEmployeeId;
			}
			else
			{
				entity = new TaskTemplate
				{
					CompanyId = companyId,
					CreatedAt = TaskCalendarTime.UtcNow(),
					CreatedByEmployeeId = actorEmployeeId
				};
				_db.TaskTemplates.Add(entity);
			}

			entity.Name = input.Name.Trim();
			entity.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim();
			entity.Description = input.Description;
			entity.IsActive = input.IsActive;

			int order = 1;
			foreach (var i in items.OrderBy(i => i.SortOrder))
			{
				entity.Items.Add(new TaskTemplateItem
				{
					CompanyId = companyId,
					Title = i.Title.Trim(),
					TitleEn = string.IsNullOrWhiteSpace(i.TitleEn) ? null : i.TitleEn.Trim(),
					Description = i.Description,
					Priority = i.Priority,
					EstimatedHours = i.EstimatedHours,
					DueOffsetDays = i.DueOffsetDays,
					DefaultAssigneeEmployeeId = i.DefaultAssigneeEmployeeId,
					SortOrder = i.SortOrder,
					PredecessorSortOrder = i.PredecessorSortOrder,
					ChecklistLines = i.ChecklistLines
				});
				order++;
			}

			await _db.SaveChangesAsync(ct);
			return (true, null, entity.ID);
		}

		public async Task<(bool ok, string? error)> SetActiveAsync(int companyId, int id, bool active, CancellationToken ct = default)
		{
			var t = await _db.TaskTemplates.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId, ct);
			if (t == null) return (false, "القالب غير موجود (template not found)");
			t.IsActive = active;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Creates one task per template item THROUGH ITaskService, then its checklist lines, then the
		/// dependency edges between the tasks just created.
		///
		/// Tasks are created first and edges second on purpose: an edge needs both ids to exist, and the
		/// dependency service refuses an edge to a task that is not there — which is the behaviour we want
		/// everywhere else and would be a bug to bypass here.
		public async Task<(bool ok, string? error, TemplateApplyResult? result)> ApplyAsync(
			int companyId, int templateId, DateTime anchorLocalDate, int? assigneeOverride,
			int currentEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "الشركة غير محددة (unresolved company)", null);

			var template = await GetAsync(companyId, templateId, ct);
			if (template == null) return (false, "القالب غير موجود (template not found)", null);
			if (!template.IsActive) return (false, "القالب غير نشط (template is not active)", null);
			if (template.Items.Count == 0) return (false, "القالب بلا بنود (template has no items)", null);

			var createdBySortOrder = new Dictionary<int, int>();
			var createdIds = new List<int>();
			int checklistLines = 0, edges = 0;

			foreach (var item in template.Items.OrderBy(i => i.SortOrder))
			{
				int assignee = assigneeOverride ?? item.DefaultAssigneeEmployeeId ?? currentEmployeeId;

				var input = new TaskSaveInput
				{
					Title = item.Title,
					Description = item.Description,
					AssigneeEmployeeId = assignee,
					Priority = item.Priority,
					// The anchor is a LOCAL date the user picked; the offset is in whole days, so the
					// arithmetic stays on the date and never drifts by a timezone hour.
					DueDate = anchorLocalDate.Date.AddDays(item.DueOffsetDays),
					EstimatedHours = item.EstimatedHours
				};

				var (ok, err, id) = await _tasks.SaveAsync(companyId, input, currentEmployeeId);
				if (!ok)
					return (false, $"تعذّر إنشاء «{item.Title}»: {err} (could not create '{item.Title}')", null);

				createdIds.Add(id);
				createdBySortOrder[item.SortOrder] = id;

				foreach (var line in (item.ChecklistLines ?? "")
						 .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					var (cok, _, _) = await _checklist.AddAsync(companyId, id, line, currentEmployeeId, ct);
					if (cok) checklistLines++;
				}
			}

			foreach (var item in template.Items.Where(i => i.PredecessorSortOrder.HasValue))
			{
				if (!createdBySortOrder.TryGetValue(item.PredecessorSortOrder!.Value, out var predecessorId)) continue;
				if (!createdBySortOrder.TryGetValue(item.SortOrder, out var successorId)) continue;

				var (dok, _, _) = await _dependencies.AddAsync(
					companyId, predecessorId, successorId, TaskDependencyKinds.FinishToStart, 0,
					currentEmployeeId, ct);
				if (dok) edges++;
			}

			return (true, null, new TemplateApplyResult
			{
				TemplateId = templateId,
				CreatedTaskIds = createdIds,
				ChecklistLinesCreated = checklistLines,
				DependenciesCreated = edges
			});
		}
	}
}
