using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASK NOTIFICATIONS  (Phase 2)
	//
	// Before this, Tasks raised NO notification of any kind: assignment, reassignment, completion,
	// reopening and overdue were all silent. A task assigned to you told you never.
	//
	// WHAT THIS IS
	//   A thin producer over the EXISTING INotificationService. It is not a second notification engine:
	//   it creates no table, no queue, no delivery channel and no hub call. It decides WHO should be
	//   told WHAT, and hands that to the platform.
	//
	//   Direct NotifyAsync is the correct pattern here: ADR-006 retains direct producers for entities
	//   that are NOT onboarded to the event kernel, and Task is not onboarded (see the registry
	//   onboarding request in TaskCalendarIntegrationContracts.cs). When Task IS onboarded, these calls
	//   MUST be removed in the same change that adds the projection — leaving both double-notifies,
	//   which is precisely why ADR-006 records that the inline NotifyRoleAsync was deleted when its
	//   projection landed.
	//
	// IDEMPOTENCY — the requirement "no duplicate notifications for the same event/correlation"
	//   Notification.DedupKey is described in the platform as an UNREAD-noise guard: NotifyAsync skips
	//   when an *unread* row with the same key exists. That is not idempotency — once the recipient
	//   reads it, a retry would deliver a second copy.
	//   So this service checks the Notifications table for ANY row with the same dedup key, read or not,
	//   BEFORE calling NotifyAsync. That is a READ of a platform table, not a write, and it adds no
	//   schema. The key is deterministic per (task, event, recipient, occurrence) — so a worker retry,
	//   a double click and a re-run all collapse to one notification.
	//
	// AUTHORIZATION AND ISOLATION
	//   * A recipient must be an ACTIVE employee of the SAME company as the task. Company is read from
	//     the TASK ROW, never from a caller-supplied value.
	//   * A recipient must stand in a real relationship to the task: assignee, previous assignee, or
	//     creator. Those are the people the task itself authorizes; nobody else is notified by this
	//     service, so it cannot become a way to push a task's title to an arbitrary employee.
	//   * The ACTOR is never notified of their own action.
	//
	// WHAT IT DOES NOT CLAIM
	//   It persists an in-app notification and lets the platform push it. It does NOT send email, push
	//   or WhatsApp, and it reports no such delivery — there is no adapter here that could confirm one.
	// ==========================================================================================

	/// The task notification kinds this increment supports. String constants because they become the
	/// notification `type` and part of the dedup key, and a typo in either is silent.
	public static class TaskNotificationKinds
	{
		public const string Assigned = "task_assigned";
		public const string Reassigned = "task_reassigned";
		public const string DueDateChanged = "task_due_date_changed";
		public const string BecameOverdue = "task_became_overdue";
		public const string Completed = "task_completed";
		public const string Reopened = "task_reopened";
		public const string Cancelled = "task_cancelled";
		public const string Escalated = "task_escalated";

		public static readonly IReadOnlyList<string> All =
			new[] { Assigned, Reassigned, DueDateChanged, BecameOverdue, Completed, Reopened, Cancelled, Escalated };
	}

	/// The outcome of one notification attempt — auditable, and honest about what actually happened.
	public sealed class TaskNotificationOutcome
	{
		public required string Kind { get; init; }
		public required int TaskId { get; init; }
		public required int RecipientEmployeeId { get; init; }
		public required string DedupKey { get; init; }
		public required bool Delivered { get; init; }
		public required string Result { get; init; }   // Delivered | Duplicate | NotAuthorized | NoRecipient | SelfActor
		public DateTime OccurredAtUtc { get; init; }
		public Guid CorrelationId { get; init; }
	}

	public interface ITaskNotificationService
	{
		Task<IReadOnlyList<TaskNotificationOutcome>> TaskAssignedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskReassignedAsync(
			int taskId, int previousAssigneeId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskDueDateChangedAsync(
			int taskId, DateTime? previousDueUtc, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskBecameOverdueAsync(
			int taskId, DateTime dueUtc, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskCompletedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskReopenedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		Task<IReadOnlyList<TaskNotificationOutcome>> TaskCancelledAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default);

		// Tell the assignee's DIRECT MANAGER that a task is still not Done well past its due date. The caller
		// resolves the manager (company-checked) and passes it in; this method does not guess one.
		Task<IReadOnlyList<TaskNotificationOutcome>> TaskEscalatedAsync(
			int taskId, DateTime dueUtc, int managerEmployeeId, CancellationToken ct = default);
	}

	public sealed class TaskNotificationService : ITaskNotificationService
	{
		private const string Category = "Tasks";
		private const string SourceModule = "Tasks";

		private readonly CrossDbContext _db;
		private readonly INotificationService _notify;

		public TaskNotificationService(CrossDbContext db, INotificationService notify)
		{
			_db = db; _notify = notify;
		}

		// -------------------------------------------------------------------------------------
		// public surface
		// -------------------------------------------------------------------------------------
		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskAssignedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Assigned, actorEmployeeId, correlationId,
				recipients: t => new[] { t.AssigneeEmployeeId },
				occurrence: _ => "assign",
				titleAr: "مهمة جديدة مُسندة إليك", titleEn: "A task was assigned to you",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskReassignedAsync(
			int taskId, int previousAssigneeId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Reassigned, actorEmployeeId, correlationId,
				// BOTH sides are told: the new owner because it is now theirs, the previous owner because
				// it no longer is. Telling only one leaves somebody believing they still own it.
				recipients: t => new[] { t.AssigneeEmployeeId, previousAssigneeId },
				occurrence: _ => $"reassign:{previousAssigneeId}",
				titleAr: "تم تغيير إسناد المهمة", titleEn: "A task assignment changed",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskDueDateChangedAsync(
			int taskId, DateTime? previousDueUtc, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.DueDateChanged, actorEmployeeId, correlationId,
				recipients: t => new[] { t.AssigneeEmployeeId },
				// The occurrence key carries BOTH dates, so moving a due date twice notifies twice while
				// retrying one move notifies once.
				occurrence: t => $"due:{Stamp(previousDueUtc)}->{Stamp(t.DueDate)}",
				titleAr: "تغيّر تاريخ استحقاق المهمة", titleEn: "A task due date changed",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskBecameOverdueAsync(
			int taskId, DateTime dueUtc, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.BecameOverdue, actorEmployeeId: null,
				correlationId: DeterministicOverdueCorrelation(taskId, dueUtc),
				recipients: t => new[] { t.AssigneeEmployeeId },
				// Keyed on the DUE DATE, not on the sweep time. A worker that runs hourly therefore notifies
				// ONCE for a given missed date, and a due-date change produces a genuinely new occurrence.
				occurrence: _ => $"overdue:{Stamp(dueUtc)}",
				titleAr: "مهمة متأخرة", titleEn: "A task is overdue",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskCompletedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Completed, actorEmployeeId, correlationId,
				// The CREATOR is told, not the assignee: the assignee is usually the one who completed it.
				recipients: t => new[] { t.CreatedByEmployeeId },
				occurrence: _ => "completed",
				titleAr: "اكتملت المهمة", titleEn: "A task was completed",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskReopenedAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Reopened, actorEmployeeId, correlationId,
				recipients: t => new[] { t.AssigneeEmployeeId },
				occurrence: _ => "reopened",
				titleAr: "أُعيد فتح المهمة", titleEn: "A task was reopened",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskCancelledAsync(
			int taskId, int? actorEmployeeId, Guid correlationId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Cancelled, actorEmployeeId, correlationId,
				recipients: t => new[] { t.AssigneeEmployeeId, t.CreatedByEmployeeId },
				occurrence: _ => "cancelled",
				titleAr: "أُلغيت المهمة", titleEn: "A task was cancelled",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		public Task<IReadOnlyList<TaskNotificationOutcome>> TaskEscalatedAsync(
			int taskId, DateTime dueUtc, int managerEmployeeId, CancellationToken ct = default) =>
			SendAsync(taskId, TaskNotificationKinds.Escalated, actorEmployeeId: null,
				correlationId: DeterministicEscalationCorrelation(taskId, dueUtc, managerEmployeeId),
				// ONLY the manager. The assignee already received task_became_overdue for this same date; telling
				// them again that they are late is noise, and escalation is a message to the manager by definition.
				recipients: _ => new[] { managerEmployeeId },
				// Keyed on the MISSED DUE DATE and the manager, never on the sweep time. So an hourly worker
				// escalates once per missed date, a restart does not re-escalate, and moving the due date is a
				// genuinely new occurrence that may escalate again.
				occurrence: _ => $"escalated:{Stamp(dueUtc)}",
				titleAr: "تصعيد مهمة متأخرة", titleEn: "An overdue task was escalated to you",
				body: t => (Ar: t.Title, En: t.TitleEn ?? t.Title), ct: ct);

		// -------------------------------------------------------------------------------------
		// the one path every notification goes through
		// -------------------------------------------------------------------------------------
		private async Task<IReadOnlyList<TaskNotificationOutcome>> SendAsync(
			int taskId,
			string kind,
			int? actorEmployeeId,
			Guid correlationId,
			Func<TaskItem, IEnumerable<int>> recipients,
			Func<TaskItem, string> occurrence,
			string titleAr, string titleEn,
			Func<TaskItem, (string Ar, string En)> body,
			CancellationToken ct)
		{
			var outcomes = new List<TaskNotificationOutcome>();

			var task = await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(t => t.ID == taskId, ct);
			if (task == null) return outcomes;

			// Company comes from the TASK ROW. There is no caller-supplied company and no fallback.
			int companyId = task.CompanyId;
			if (companyId <= 0) return outcomes;

			var occurrenceKey = occurrence(task);
			var now = TaskCalendarTime.UtcNow();
			var (bodyAr, bodyEn) = body(task);

			// Distinct: a task whose assignee IS its creator must not be told twice by one event.
			foreach (var recipientId in recipients(task).Where(r => r > 0).Distinct())
			{
				var dedupKey = DedupKey(companyId, taskId, kind, recipientId, occurrenceKey);

				if (actorEmployeeId is int actor && actor == recipientId)
				{
					outcomes.Add(Outcome(kind, taskId, recipientId, dedupKey, false, "SelfActor", now, correlationId));
					continue;
				}

				if (!await RecipientIsEligibleAsync(companyId, recipientId, ct))
				{
					outcomes.Add(Outcome(kind, taskId, recipientId, dedupKey, false, "NotAuthorized", now, correlationId));
					continue;
				}

				// TRUE idempotency: any existing row with this key, read or unread.
				bool already = await _db.Notifications.AsNoTracking()
					.AnyAsync(n => n.DedupKey == dedupKey && n.RecipientEmployeeID == recipientId, ct);
				if (already)
				{
					outcomes.Add(Outcome(kind, taskId, recipientId, dedupKey, false, "Duplicate", now, correlationId));
					continue;
				}

				await _notify.NotifyAsync(
					recipientEmployeeId: recipientId,
					titleAr: titleAr, titleEn: titleEn,
					bodyAr: bodyAr, bodyEn: bodyEn,
					type: kind,
					refId: taskId,
					// A stable relative path, NOT a final UI route: Tasks has no per-task screen today, so
					// inventing /Tasks/Details/{id} would ship a dead link. The list + query is honest and
					// survives whatever detail route is added later.
					url: DeepLink(taskId),
					companyId: companyId,
					actorEmployeeId: actorEmployeeId,
					priority: kind is TaskNotificationKinds.BecameOverdue or TaskNotificationKinds.Escalated ? "High" : null,
					category: Category,
					dedupKey: dedupKey,
					entityType: TaskCalendarEntityCodes.Task,
					entityId: taskId);

				outcomes.Add(Outcome(kind, taskId, recipientId, dedupKey, true, "Delivered", now, correlationId));
			}

			return outcomes;
		}

		/// A recipient must be an ACTIVE employee of the task's own company. This is the company-isolation
		/// check and the authorization floor in one: an employee of another company is never told that a
		/// task exists, not even its title.
		private Task<bool> RecipientIsEligibleAsync(int companyId, int recipientId, CancellationToken ct) =>
			_db.Employee.AsNoTracking()
				.AnyAsync(e => e.ID == recipientId && e.EmpCompanyID == companyId && e.IsActive, ct);

		/// Deterministic, and it is the whole idempotency story. Same task + same kind + same recipient +
		/// same occurrence = same key = one notification, however many times the caller retries.
		internal static string DedupKey(int companyId, int taskId, string kind, int recipientId, string occurrence) =>
			$"task:{companyId}:{taskId}:{kind}:{recipientId}:{occurrence}";

		/// Deep-link CONTRACT, not a final route: a query against the existing list screen.
		internal static string DeepLink(int taskId) => $"/Tasks/Index?taskId={taskId}";

		/// Overdue has no user action, so its correlation id must be derived from the fact itself —
		/// otherwise every sweep would produce a new correlation for the same real event.
		internal static Guid DeterministicOverdueCorrelation(int taskId, DateTime dueUtc)
		{
			var seed = $"overdue:{taskId}:{Stamp(dueUtc)}";
			var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
			return new Guid(bytes);
		}

		/// Escalation has no user action either, and it must not share the overdue correlation: they are two
		/// different facts about the same missed date. Includes the manager so escalating to a different
		/// manager (a reorganised tree) is a distinguishable event rather than a silent duplicate.
		internal static Guid DeterministicEscalationCorrelation(int taskId, DateTime dueUtc, int managerEmployeeId)
		{
			var seed = $"escalated:{taskId}:{Stamp(dueUtc)}:{managerEmployeeId}";
			var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
			return new Guid(bytes);
		}

		private static string Stamp(DateTime? utc) =>
			utc.HasValue
				? TaskCalendarTime.AssumeUtc(utc.Value).ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture)
				: "none";

		private static TaskNotificationOutcome Outcome(
			string kind, int taskId, int recipientId, string dedupKey, bool delivered, string result,
			DateTime now, Guid correlationId) => new()
			{
				Kind = kind, TaskId = taskId, RecipientEmployeeId = recipientId, DedupKey = dedupKey,
				Delivered = delivered, Result = result, OccurredAtUtc = now, CorrelationId = correlationId
			};
	}
}
