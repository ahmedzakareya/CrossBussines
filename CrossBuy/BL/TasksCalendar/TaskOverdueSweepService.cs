using CrossBuy.BL;              // ScopedTx — the ambient transaction RecordAsync requires
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// OVERDUE DETECTION  (Phase 9)
	//
	// THE OWNERSHIP DECISION, and why there is no third worker.
	//
	//   Two hosted workers already exist and both are BackgroundService + IServiceScopeFactory:
	//     * TaskGeneratorHostedService (TM-7)      — 15-minute timer, IWorkerGate, per-company scope
	//     * TaskScheduleMatchHostedService (TM-9-ب) — 15-minute timer, IWorkerGate, per-company scope
	//
	//   TM-7 is the correct host and TM-9 is not, for one reason: TM-7 already exists to CREATE task-side
	//   facts on a timer per company, which is the same shape as "notice a due date has passed". TM-9
	//   exists to match a task to a MOVEMENT; overdue has nothing to do with a movement, and putting it
	//   there would make that worker about two unrelated things.
	//
	//   Adding a third worker was considered and REJECTED: three timers competing over TaskItems is how
	//   duplicate notifications start, and the brief forbids it outright.
	//
	// WHY THIS IS A SERVICE AND NOT LOOP CODE
	//   The worker gives it a company-bound scope; this service does the work. That keeps every rule
	//   testable without a host, and it means the worker's own responsibility stays "when and for whom",
	//   not "what".
	//
	// THE FOUR PROPERTIES THAT MAKE IT SAFE
	//   1. ONE OWNER          — only TaskGeneratorHostedService calls it.
	//   2. IDEMPOTENT         — the notification dedup key is derived from the task's DUE DATE, not from
	//                           the sweep time, so a 15-minute timer notifies ONCE for a missed date.
	//   3. SELF-CORRECTING    — a due-date change produces a different key, so the new date is a genuinely
	//                           new occurrence; a completed or cancelled task is excluded by the query.
	//   4. COMPANY-ISOLATED   — it sweeps ONE company per call, the company the worker's scope is bound to.
	//
	//   It holds no scoped service across ticks: the worker creates a scope per company per tick and this
	//   service lives and dies inside it.
	// ==========================================================================================

	public sealed class OverdueSweepResult
	{
		public required int CompanyId { get; init; }
		public required int Examined { get; init; }
		public required int Notified { get; init; }
		public required int AlreadyNotified { get; init; }
		public required int Skipped { get; init; }
		public required DateTime SweptAtUtc { get; init; }

		/// Business events recorded for this sweep. Counts the RecordAsync calls that were made, not the
		/// rows inserted: the kernel deduplicates on DedupKey, so a repeat sweep of the same missed date
		/// calls once per candidate and inserts nothing. Asserting on the BusinessEvents table is what
		/// proves "one transition, one event" — this number is for the worker's log.
		public int EventsRecorded { get; init; }

		/// Candidates that were NOTIFIED but whose business event could NOT be recorded, because no
		/// BusinessContext is resolvable in this scope.
		///
		/// THIS IS THE HALF THIS TAB DOES NOT OWN, AND IT IS COUNTED RATHER THAN HIDDEN. RecordAsync reads
		/// IBusinessContextAccessor, which resolves ONLY from HTTP (BusinessContextAccessor.TryGetCurrentAsync
		/// -> IBusinessContextFactory.TryForHttpAsync). TaskGeneratorHostedService runs this sweep inside
		/// WorkerScope.ForCompany — a DI scope with no HttpContext — so in the background there is nothing for
		/// RecordAsync to attribute the event to and it throws.
		///
		/// A NON-ZERO VALUE HERE MEANS THE MONITOR AND THE BELL STILL DISAGREE for those tasks. It is surfaced
		/// on the result and logged by the worker so the gap is visible in operation, not only in a document.
		/// CLAUDE.md already records the underlying limitation as HM-D58.
		public int EventsSkippedNoContext { get; init; }
	}

	public interface ITaskOverdueSweepService
	{
		Task<OverdueSweepResult> SweepAsync(int companyId, CancellationToken ct = default);
	}

	public sealed class TaskOverdueSweepService : ITaskOverdueSweepService
	{
		/// A safety bound per tick. A company with a very large backlog is swept across several ticks
		/// rather than producing one enormous burst — and the bound is stated, not silent.
		private const int MaxPerSweep = 500;

		private readonly CrossDbContext _db;
		private readonly ITaskNotificationService _notifications;
		// UAT DEFECT 4. The sweep notified and recorded NOTHING, so Business Event Monitor and the bell
		// disagreed about the same fact: a task_became_overdue notification existed with no
		// Task.BecameOverdue event behind it. TaskCalendarEventPublisher already declared the event and
		// already derived its dedup key from the DUE DATE — it simply had no caller. This is that caller.
		private readonly ITaskCalendarEventPublisher _events;

		// The PRECONDITION, not a catch. See RecordEventAsync: the event is attempted only when a context
		// exists to attribute it to, because RecordAsync throws otherwise and this sweep must never lose a
		// notification over a missing event.
		private readonly IBusinessContextAccessor? _contexts;

		public TaskOverdueSweepService(CrossDbContext db, ITaskNotificationService notifications,
			ITaskCalendarEventPublisher events, IBusinessContextAccessor? contexts = null)
		{
			_db = db; _notifications = notifications; _events = events; _contexts = contexts;
		}

		public async Task<OverdueSweepResult> SweepAsync(int companyId, CancellationToken ct = default)
		{
			var now = TaskCalendarTime.UtcNow();

			if (companyId <= 0)
				// An unresolved company sweeps nothing. It does not sweep "all".
				return new OverdueSweepResult
				{
					CompanyId = companyId, Examined = 0, Notified = 0,
					AlreadyNotified = 0, Skipped = 0, SweptAtUtc = now
				};

			// A completed task is never overdue. "Done" is the only terminal status TaskItem has today —
			// when a Cancelled status is added, it must be excluded here too (contract: Task.Cancelled).
			// The whole entity, not a projection: the publisher builds its payload from the task's own
			// fields (company, assignee) and its dedup key from the company and id. AsNoTracking still —
			// this sweep observes a transition, it does not edit the task.
			var candidates = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId
							&& t.DueDate != null
							&& t.DueDate < now
							&& t.Status != "Done"
							&& t.AssigneeEmployeeId > 0)
				.OrderBy(t => t.DueDate)
				.Take(MaxPerSweep)
				.ToListAsync(ct);

			int notified = 0, already = 0, skipped = 0, recorded = 0, noContext = 0;

			// Resolved ONCE for the sweep, not per candidate: it is a property of the scope, and asking 500
			// times would be 500 identical answers. Null means "this scope cannot attribute an event".
			bool canRecord = _contexts != null && await _contexts.TryGetCurrentAsync(ct) is { CompanyId: > 0 };

			foreach (var c in candidates)
			{
				ct.ThrowIfCancellationRequested();
				var dueUtc = TaskCalendarTime.AssumeUtc(c.DueDate!.Value);

				// THE EVENT FIRST, IN A TRANSACTION, THEN THE NOTIFICATION AFTER THE COMMIT.
				//
				// That order is the platform's rule, not a preference: RecordAsync refuses to run without an
				// ambient transaction (BusinessEventService.cs:85), and a notification sent before the commit
				// would announce a fact that could still roll back — which is the exact disagreement between
				// the bell and the monitor that this defect is.
				//
				// The transaction wraps ONE task, not the whole sweep. A sweep of 500 tasks in one transaction
				// would make a single bad row discard 499 good events, and would hold a write transaction open
				// across 500 round-trips inside a background worker.
				//
				// IDEMPOTENCY IS THE KERNEL'S, NOT A FLAG OF OURS. The dedup key is
				// "Task.BecameOverdue:{company}:{task}:overdue:{dueStamp}" — derived from the DUE DATE, so the
				// 15-minute timer records one event per missed date however often it runs, and a task whose due
				// date is changed produces a genuinely new key. RecordAsync returns the existing id and writes
				// nothing on a repeat.
				//
				// WHY THIS IS GUARDED RATHER THAN UNCONDITIONAL. Publishing unconditionally made the sweep throw
				// on its FIRST candidate in the background worker, and because the event is recorded before the
				// notification, the throw meant NO NOTIFICATION EITHER — for that task and every task behind it
				// in the batch. Closing an event gap by silently switching off overdue notifications is not a
				// fix; it is a worse defect wearing this one's clothes. Proven by
				// OverdueBusinessEventTests.WITHOUT_a_resolvable_BusinessContext_the_sweep_still_notifies.
				//
				// This is a PRECONDITION, not a swallowing try/catch: nothing is caught, nothing is hidden, and
				// the skip is counted and returned. The kernel's in-transaction-before-commit rule is obeyed
				// exactly whenever the event IS recorded.
				if (canRecord)
				{
					await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
					{
						await _events.TaskBecameOverdueAsync(c, dueUtc, ct);
						await tx.CommitAsync();
					}
					recorded++;
				}
				else noContext++;

				// The due date IS the occurrence. Passing it through means the dedup key is stable across
				// ticks, and a changed due date is a new occurrence rather than a repeat of the old one.
				var outcomes = await _notifications.TaskBecameOverdueAsync(c.ID, dueUtc, ct);

				if (outcomes.Any(o => o.Delivered)) notified++;
				else if (outcomes.Any(o => o.Result == "Duplicate")) already++;
				else skipped++;
			}

			return new OverdueSweepResult
			{
				CompanyId = companyId,
				Examined = candidates.Count,
				Notified = notified,
				AlreadyNotified = already,
				Skipped = skipped,
				EventsRecorded = recorded,
				EventsSkippedNoContext = noContext,
				SweptAtUtc = now
			};
		}
	}
}
