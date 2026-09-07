using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// OVERDUE MANAGER ESCALATION
	//
	// THE RULE. A task that is still not Done a stated grace period after its due date stops being only the
	// assignee's problem: the assignee's DIRECT MANAGER is told. Nothing else changes — the task is not
	// reassigned, its status is not rewritten, and no deadline moves.
	//
	// NO NEW DATABASE STATE, DELIBERATELY. TaskItem already records that "Overdue" is DERIVED and never
	// stored, and escalation is the same kind of fact: it is fully determined by (due date, status, now) plus
	// the evidence that a manager was already told. That evidence is the notification row the platform
	// already writes, whose dedup key is derived from the MISSED DUE DATE. So:
	//
	//   NotDue            no due date, or the due date is in the future
	//   Overdue           past due and not Done, but still inside the grace window
	//   EscalationPending past due + grace elapsed, not Done, and no manager has been told yet
	//   Escalated         a manager has been told about THIS missed due date
	//
	// A column would have added a second truth that could disagree with the notification, and a migration to
	// keep in step with it. The dedup key is already the idempotency the platform guarantees.
	//
	// WHY THAT MAKES IT SAFE TO RUN REPEATEDLY. The occurrence key is the due date, never the sweep time, so
	// an hourly worker escalates once per missed date; a process restart re-reads the same evidence and
	// escalates nothing; two workers racing collapse onto one row. Moving the due date is a genuinely new
	// occurrence, which is the correct behaviour — a re-committed deadline that is missed again escalates
	// again.
	//
	// MANAGER RESOLUTION IS NOT ALLOWED TO GUESS. IOrgHierarchy.DirectManagerAsync returns the nearest
	// employee ancestor in the org tree INTERSECTED with the task's own company. When it returns null the
	// task is counted as unresolved and reported — it is never sent to an administrator, a creator, or
	// company 1. An escalation delivered to the wrong person is worse than one not delivered, because it
	// looks like it worked.
	// ==========================================================================================

	public enum TaskEscalationState
	{
		NotDue = 0,
		Overdue,
		EscalationPending,
		Escalated,
	}

	/// What the UI shows, and what a later Workspace attention ranking can consume without re-deriving it.
	public sealed class TaskEscalationStatus
	{
		public required int TaskId { get; init; }
		public required TaskEscalationState State { get; init; }

		/// The manager who was told. Non-null only in the Escalated state.
		public int? ManagerEmployeeId { get; init; }
		public string? ManagerName { get; init; }
		public DateTime? EscalatedAtUtc { get; init; }

		/// How long the task had been overdue at the moment it was looked at. 0 when not overdue.
		public int OverdueHours { get; init; }

		public bool IsEscalated => State == TaskEscalationState.Escalated;
	}

	public sealed class EscalationSweepResult
	{
		public required int CompanyId { get; init; }
		public required int Examined { get; init; }
		public required int Escalated { get; init; }

		/// Already escalated for this missed due date — the idempotent path, and the common one.
		public required int AlreadyEscalated { get; init; }

		/// Overdue past the grace but NO valid direct manager resolves. Counted and returned rather than
		/// redirected to somebody else. This is the honest unresolved state the rule requires.
		public required int NoManager { get; init; }

		/// Notified but the business event could not be recorded because no BusinessContext resolves in this
		/// scope. Same precondition, and same reason for counting it, as the overdue sweep (HM-D58).
		public required int EventsSkippedNoContext { get; init; }

		public required int EventsRecorded { get; init; }
		public required DateTime SweptAtUtc { get; init; }
	}

	public interface ITaskEscalationService
	{
		/// Escalate every task in this company that is overdue past the grace and not yet escalated.
		Task<EscalationSweepResult> SweepAsync(int companyId, CancellationToken ct = default);

		/// The derived state of one task, for a detail screen.
		Task<TaskEscalationStatus> EvaluateAsync(int companyId, int taskId, CancellationToken ct = default);

		/// The derived state of many tasks in one round trip, for a list or a board.
		Task<IReadOnlyDictionary<int, TaskEscalationStatus>> EvaluateManyAsync(
			int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);
	}

	public sealed class TaskEscalationService : ITaskEscalationService
	{
		/// The same safety bound the overdue sweep uses, for the same reason: a company with a large backlog
		/// is escalated across several ticks instead of producing one enormous burst.
		private const int MaxPerSweep = 500;

		/// How long a task may stay overdue before the manager is involved. A grace period is what makes
		/// EscalationPending a real state rather than a synonym for Overdue, and it keeps a task that is a few
		/// minutes late out of a manager's inbox.
		internal const int DefaultGraceHours = 24;

		private readonly CrossDbContext _db;
		private readonly ITaskNotificationService _notifications;
		private readonly ITaskCalendarEventPublisher _events;
		private readonly IOrgHierarchy _hierarchy;
		private readonly IConfiguration? _config;

		// Same PRECONDITION as the overdue sweep, not a catch: RecordAsync throws without a resolvable
		// context, and escalation must never lose a manager notification over a missing event.
		private readonly IBusinessContextAccessor? _contexts;

		public TaskEscalationService(CrossDbContext db, ITaskNotificationService notifications,
			ITaskCalendarEventPublisher events, IOrgHierarchy hierarchy,
			IConfiguration? config = null, IBusinessContextAccessor? contexts = null)
		{
			_db = db; _notifications = notifications; _events = events;
			_hierarchy = hierarchy; _config = config; _contexts = contexts;
		}

		internal int GraceHours =>
			int.TryParse(_config?["Tasks:Escalation:GraceHours"], out var h) && h >= 0 ? h : DefaultGraceHours;

		// -------------------------------------------------------------------------------------
		// the sweep
		// -------------------------------------------------------------------------------------
		public async Task<EscalationSweepResult> SweepAsync(int companyId, CancellationToken ct = default)
		{
			var now = TaskCalendarTime.UtcNow();

			// An unresolved company escalates nothing. It does not escalate "all".
			if (companyId <= 0)
				return Empty(companyId, now);

			var threshold = now.AddHours(-GraceHours);

			// The whole entity, not a projection: the publisher builds its payload and dedup key from the
			// task's own fields. AsNoTracking — this sweep observes a transition, it never edits the task.
			var candidates = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId
							&& t.DueDate != null
							&& t.DueDate < threshold
							&& t.Status != "Done"
							&& t.AssigneeEmployeeId > 0)
				.OrderBy(t => t.DueDate)
				.Take(MaxPerSweep)
				.ToListAsync(ct);

			int escalated = 0, already = 0, noManager = 0, recorded = 0, noContext = 0;

			// Resolved ONCE for the sweep: it is a property of the scope, not of a candidate.
			bool canRecord = _contexts != null && await _contexts.TryGetCurrentAsync(ct) is { CompanyId: > 0 };

			foreach (var t in candidates)
			{
				ct.ThrowIfCancellationRequested();
				var dueUtc = TaskCalendarTime.AssumeUtc(t.DueDate!.Value);

				// The manager is resolved from the TASK'S company, which is the row's own value — never a
				// caller-supplied company and never a fallback.
				int? managerId = await _hierarchy.DirectManagerAsync(t.CompanyId, t.AssigneeEmployeeId, ct);
				if (managerId is not int manager || manager <= 0) { noManager++; continue; }

				// A manager who is also the assignee would mean telling somebody they are late twice. The org
				// tree should not produce this, but a self-parenting node is a data condition that exists.
				if (manager == t.AssigneeEmployeeId) { noManager++; continue; }

				// ALREADY ESCALATED? The evidence is the notification's dedup key, which encodes this exact
				// missed due date. Checked before any write so the common path costs one query and nothing else.
				if (await HasEscalationEvidenceAsync(t.CompanyId, t.ID, dueUtc, ct)) { already++; continue; }

				int overdueHours = (int)Math.Max(0, Math.Floor((now - dueUtc).TotalHours));

				// THE EVENT FIRST, IN A TRANSACTION, THEN THE NOTIFICATION AFTER THE COMMIT — the platform's
				// ordering rule. RecordAsync refuses to run without an ambient transaction, and a notification
				// sent before the commit would announce a fact that could still roll back.
				//
				// The transaction wraps ONE task. A sweep of 500 in one transaction would let a single bad row
				// discard 499 good events and would hold a write transaction open across 500 round trips.
				if (canRecord)
				{
					await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
					{
						await _events.TaskEscalatedAsync(t, dueUtc, manager, overdueHours, ct);
						await tx.CommitAsync();
					}
					recorded++;
				}
				else
				{
					noContext++;
				}

				var outcomes = await _notifications.TaskEscalatedAsync(t.ID, dueUtc, manager, ct);
				if (outcomes.Any(o => o.Delivered)) escalated++;
				else already++;      // the notification layer found its own dedup row: same fact, already told
			}

			return new EscalationSweepResult
			{
				CompanyId = companyId, Examined = candidates.Count, Escalated = escalated,
				AlreadyEscalated = already, NoManager = noManager,
				EventsRecorded = recorded, EventsSkippedNoContext = noContext, SweptAtUtc = now,
			};
		}

		// -------------------------------------------------------------------------------------
		// the derived state, for screens
		// -------------------------------------------------------------------------------------
		public async Task<TaskEscalationStatus> EvaluateAsync(int companyId, int taskId, CancellationToken ct = default)
		{
			var many = await EvaluateManyAsync(companyId, new[] { taskId }, ct);
			return many.TryGetValue(taskId, out var one)
				? one
				: new TaskEscalationStatus { TaskId = taskId, State = TaskEscalationState.NotDue };
		}

		public async Task<IReadOnlyDictionary<int, TaskEscalationStatus>> EvaluateManyAsync(
			int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
		{
			var result = new Dictionary<int, TaskEscalationStatus>();
			if (companyId <= 0 || taskIds.Count == 0) return result;

			var now = TaskCalendarTime.UtcNow();
			var threshold = now.AddHours(-GraceHours);

			// Company from the ROW, and the id filter on top of it: a task of another company is not described
			// here at all, not even as NotDue.
			var tasks = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && taskIds.Contains(t.ID))
				.Select(t => new { t.ID, t.DueDate, t.Status, t.AssigneeEmployeeId })
				.ToListAsync(ct);
			if (tasks.Count == 0) return result;

			// One query for every task's evidence, keyed by task id. The dedup key carries the due stamp, so
			// the row only counts as evidence for the due date the task has NOW.
			string prefix = $"task:{companyId}:";
			var evidence = await _db.Notifications.AsNoTracking()
				.Where(n => n.CompanyID == companyId
							&& n.Type == TaskNotificationKinds.Escalated
							&& n.EntityId != null
							&& taskIds.Contains(n.EntityId!.Value)
							&& n.DedupKey != null
							&& n.DedupKey.StartsWith(prefix))
				.Select(n => new { TaskId = n.EntityId!.Value, n.DedupKey, n.RecipientEmployeeID, n.CreatedAt })
				.ToListAsync(ct);

			var managerIds = evidence.Select(e => e.RecipientEmployeeID).Distinct().ToList();
			var managerNames = managerIds.Count == 0
				? new Dictionary<int, string>()
				: await _db.Employee.AsNoTracking()
					.Where(e => managerIds.Contains(e.ID) && e.EmpCompanyID == companyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .ToDictionaryAsync(e => e.ID, e => EmployeeNames.Of(e.FullName, e.FullNameEn), ct);

			foreach (var t in tasks)
			{
				bool done = string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase);
				if (done || t.DueDate == null)
				{
					result[t.ID] = new TaskEscalationStatus { TaskId = t.ID, State = TaskEscalationState.NotDue };
					continue;
				}

				var dueUtc = TaskCalendarTime.AssumeUtc(t.DueDate.Value);
				if (dueUtc >= now)
				{
					result[t.ID] = new TaskEscalationStatus { TaskId = t.ID, State = TaskEscalationState.NotDue };
					continue;
				}

				int overdueHours = (int)Math.Max(0, Math.Floor((now - dueUtc).TotalHours));
				string suffix = EscalationOccurrenceSuffix(dueUtc);
				var hit = evidence.FirstOrDefault(e => e.TaskId == t.ID && e.DedupKey!.EndsWith(suffix));

				if (hit != null)
				{
					result[t.ID] = new TaskEscalationStatus
					{
						TaskId = t.ID,
						State = TaskEscalationState.Escalated,
						ManagerEmployeeId = hit.RecipientEmployeeID,
						ManagerName = managerNames.TryGetValue(hit.RecipientEmployeeID, out var nm) && nm.Length > 0 ? nm : null,
						EscalatedAtUtc = hit.CreatedAt,
						OverdueHours = overdueHours,
					};
					continue;
				}

				result[t.ID] = new TaskEscalationStatus
				{
					TaskId = t.ID,
					State = dueUtc < threshold ? TaskEscalationState.EscalationPending : TaskEscalationState.Overdue,
					OverdueHours = overdueHours,
				};
			}

			return result;
		}

		// -------------------------------------------------------------------------------------
		// evidence
		// -------------------------------------------------------------------------------------
		/// The suffix the escalation dedup key ends with for a given missed due date. It deliberately does NOT
		/// include the manager, so "has anybody been told about this date" is answerable without knowing who.
		internal static string EscalationOccurrenceSuffix(DateTime dueUtc) =>
			":escalated:" + TaskCalendarTime.AssumeUtc(dueUtc)
				.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);

		private Task<bool> HasEscalationEvidenceAsync(int companyId, int taskId, DateTime dueUtc, CancellationToken ct)
		{
			string suffix = EscalationOccurrenceSuffix(dueUtc);
			return _db.Notifications.AsNoTracking()
				.AnyAsync(n => n.CompanyID == companyId
							&& n.Type == TaskNotificationKinds.Escalated
							&& n.EntityId == taskId
							&& n.DedupKey != null
							&& n.DedupKey.EndsWith(suffix), ct);
		}

		private static EscalationSweepResult Empty(int companyId, DateTime now) => new()
		{
			CompanyId = companyId, Examined = 0, Escalated = 0, AlreadyEscalated = 0,
			NoManager = 0, EventsRecorded = 0, EventsSkippedNoContext = 0, SweptAtUtc = now,
		};
	}
}
