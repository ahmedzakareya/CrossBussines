using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Calendar;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Uat
{
	// ==========================================================================================
	// UAT DATASET SEEDER — TASKS, CHECKLISTS, DEPENDENCIES, TEMPLATES, TIMESHEETS, CALENDAR
	//
	// ONE COMPANY PER INVOCATION, AND THE COMPANY IS NOT A PARAMETER.
	//
	//   The company comes from the resolved BusinessContext, never from the wire. That is the project's
	//   standing rule ("a request-supplied companyId is compatibility-only: validate it, never coerce it"),
	//   and it also happens to be the only shape that works: BusinessEventService honours a company override
	//   ONLY for a System context, and a System context cannot be produced from an HTTP request. So a task
	//   created for company 65 from a company-1 session would write the TASK into 65 and its BUSINESS EVENT
	//   into 1 — a split-brain row. Seeding the second company is therefore a SECOND CALL under a
	//   company-65 session, not a parameter on the first.
	//
	// EVERYTHING GOES THROUGH THE REAL SERVICES.
	//
	//   Tasks are created by ITaskService.SaveAsync and moved by ChangeStatusAsync, so the business events,
	//   the notifications, the status-transition rules and the in-transaction event discipline are the
	//   product's own — not a parallel implementation that happens to fill the same tables. That is why the
	//   dataset produces 400+ genuine BusinessEvents rather than 400 hand-written rows: the events are a
	//   CONSEQUENCE of the seeding, which is the only way a monitor screen shows something true.
	//
	//   Two tables have no service and are written through the DbContext directly, stated rather than
	//   implied: CalendarResource / CalendarEventResource (no CRUD service exists) and Notification
	//   (INotificationService is a writer whose dedup only matches UNREAD rows, so it cannot be used as an
	//   idempotency key — see the notification section in the Platform partial).
	//
	// IDEMPOTENCY IS SKIP-IF-PRESENT, NOT UPSERT.
	//
	//   Each step reads the rows its marker already owns, and creates only what is missing. It does NOT
	//   update what it finds. An upsert would re-anchor every relative due date on a second run, and each
	//   re-anchoring writes a Task.DueDateChanged event with a NEW dedup key — so the "append-only tables
	//   grew" delta would be real, and the second-run proof would be muddier for no gain. The cost is
	//   stated instead: the relative dates (overdue / today / tomorrow) are anchored to the day of the FIRST
	//   run. To re-anchor them, clean up and reseed.
	// ==========================================================================================
	public sealed partial class UatDatasetSeeder : IUatDatasetSeeder
	{
		private readonly CrossDbContext _db;
		private readonly IWebHostEnvironment _env;
		private readonly IConfiguration _config;
		private readonly IBusinessContextAccessor _contexts;
		private readonly ITaskService _tasks;
		private readonly ITaskChecklistService _checklist;
		private readonly ITaskDependencyService _dependencies;
		private readonly ITaskTemplateService _templates;
		private readonly ITaskGeneratorService _autoRules;
		private readonly ITaskScheduleMatcher _matcher;
		private readonly ITimesheetService _timesheets;
		private readonly ICalendarService _calendar;
		private readonly ICalendarSchedulingService _scheduling;
		private readonly IReportLibraryService _reportLibrary;
		private readonly IReportTemplateService _reportTemplates;
		private readonly IReportHistoryService _reportHistory;
		private readonly ILogger<UatDatasetSeeder> _log;

		public UatDatasetSeeder(
			CrossDbContext db, IWebHostEnvironment env, IConfiguration config,
			IBusinessContextAccessor contexts,
			ITaskService tasks, ITaskChecklistService checklist, ITaskDependencyService dependencies,
			ITaskTemplateService templates, ITaskGeneratorService autoRules, ITaskScheduleMatcher matcher,
			ITimesheetService timesheets,
			ICalendarService calendar, ICalendarSchedulingService scheduling,
			IReportLibraryService reportLibrary, IReportTemplateService reportTemplates,
			IReportHistoryService reportHistory,
			ILogger<UatDatasetSeeder> log)
		{
			_db = db; _env = env; _config = config; _contexts = contexts;
			_tasks = tasks; _checklist = checklist; _dependencies = dependencies; _templates = templates;
			_autoRules = autoRules; _matcher = matcher; _timesheets = timesheets;
			_calendar = calendar; _scheduling = scheduling;
			_reportLibrary = reportLibrary; _reportTemplates = reportTemplates; _reportHistory = reportHistory;
			_log = log;
		}

		// A deterministic pseudo-random source. Seeded from a CONSTANT, so "vary the priorities" produces the
		// same variation on every machine and every run — a dataset a reviewer cannot reproduce is a dataset
		// two people cannot discuss.
		private static Random Rng() => new(20260812);

		public Task<UatSeedPreflight> PreflightAsync(CancellationToken ct = default)
			=> UatSeedGuard.EvaluateAsync(_db, _env, _config, ct);

		// -------------------------------------------------------------------------------------------
		// SEED
		// -------------------------------------------------------------------------------------------
		public async Task<UatSeedReport> SeedAsync(CancellationToken ct = default)
		{
			var started = DateTime.UtcNow;
			var preflight = await PreflightAsync(ct);
			var notes = new List<string>();
			var created = new Dictionary<string, int>();

			if (!preflight.Allowed)
				return new UatSeedReport
				{
					RunId = UatMarkers.RunId, Mode = "seed", Ok = false, Preflight = preflight,
					Notes = preflight.Refusals,
				};

			// The company and the acting employee come from the resolved context. An unresolved context
			// writes nothing — fail closed, exactly as every other write path in this codebase does.
			var context = await _contexts.TryGetCurrentAsync(ct);
			if (context is not { CompanyId: > 0 } || context.EmployeeId is not > 0)
				return new UatSeedReport
				{
					RunId = UatMarkers.RunId, Mode = "seed", Ok = false, Preflight = preflight,
					Notes = new[]
					{
						"REFUSED: no BusinessContext (company + employee) could be resolved for this request. " +
						"The seeder never defaults to a company. Call /api/uat/prime first to establish a session, " +
						"then re-call with the same cookie jar (see the HM-D59 note in CLAUDE.md).",
					},
				};

			int companyId = context.CompanyId;
			int actor = context.EmployeeId!.Value;
			bool primary = companyId == PrimaryCompanyId;
			var plan = primary ? UatPlan.Primary : UatPlan.Secondary;

			notes.Add($"company={companyId} actor={actor} plan={(primary ? "primary" : "secondary")}");

			var employees = await AssigneePoolAsync(companyId, actor, ct);
			if (employees.Count == 0)
				return new UatSeedReport
				{
					RunId = UatMarkers.RunId, Mode = "seed", Ok = false, Preflight = preflight,
					Notes = new[] { $"REFUSED: company {companyId} has no active employee to assign work to." },
				};
			notes.Add($"assignee pool = {employees.Count} active employees: {string.Join(",", employees)}");

			// ---- Tasks and everything hanging off them --------------------------------------------
			var taskIds = await SeedTasksAsync(companyId, actor, employees, plan, created, notes, ct);
			await SeedChecklistsAsync(companyId, actor, taskIds, plan, created, notes, ct);
			await SeedDependenciesAsync(companyId, actor, taskIds, plan, created, notes, ct);
			await SeedTemplatesAsync(companyId, actor, employees, plan, created, notes, ct);
			await SeedAutoRulesAsync(companyId, employees, created, notes, ct);
			await SeedTimesheetsAsync(companyId, taskIds, employees, plan, created, notes, ct);
			await RunMatcherAsync(companyId, created, notes, ct);

			// ---- Calendar --------------------------------------------------------------------------
			var resourceIds = await SeedResourcesAsync(companyId, actor, plan, created, notes, ct);
			var eventIds = await SeedCalendarEventsAsync(companyId, actor, employees, plan, created, notes, ct);
			await SeedEventResourcesAsync(companyId, eventIds, resourceIds, plan, created, notes, ct);
			await SeedRecurrenceAsync(companyId, actor, eventIds, plan, created, notes, ct);

			// ---- Platform: notifications, reporting ------------------------------------------------
			await SeedNotificationsAsync(companyId, actor, employees, taskIds, plan, created, notes, ct);
			await SeedReportingAsync(companyId, context, plan, created, notes, ct);

			var counts = await CountMarkedAsync(companyId, ct);
			return new UatSeedReport
			{
				RunId = UatMarkers.RunId, Mode = "seed", Ok = true, Preflight = preflight,
				Counts = counts, Created = created, Notes = notes,
				DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
			};
		}

		public async Task<UatSeedReport> CountsAsync(CancellationToken ct = default)
		{
			var preflight = await PreflightAsync(ct);
			var context = await _contexts.TryGetCurrentAsync(ct);
			int companyId = context?.CompanyId ?? 0;

			return new UatSeedReport
			{
				RunId = UatMarkers.RunId, Mode = "counts", Ok = companyId > 0, Preflight = preflight,
				Counts = companyId > 0 ? await CountMarkedAsync(companyId, ct) : new Dictionary<string, int>(),
				Notes = companyId > 0
					? new[] { $"company={companyId}" }
					: new[] { "no company resolved — nothing to count" },
			};
		}

		internal const int PrimaryCompanyId = 1;

		// The employees UAT work may be assigned to.
		//
		// TWO DELIBERATE EXCLUSIONS, both required by the brief rather than incidental:
		//   * inactive employees — an inactive person must not appear as a live assignee;
		//   * EMPTY-STATE EMPLOYEE (the no-role development clerk). The brief requires at least one context
		//     that genuinely has no assigned tasks, no favourites and no recent reports, so that the empty
		//     states remain testable on a dense database. Round-robin assignment would silently destroy it.
		private async Task<List<int>> AssigneePoolAsync(int companyId, int actor, CancellationToken ct)
		{
			var excluded = await EmptyStateEmployeeIdsAsync(companyId, ct);

			var pool = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.IsActive)
				.OrderBy(e => e.ID)
				.Select(e => e.ID)
				.ToListAsync(ct);

			pool.RemoveAll(excluded.Contains);

			// The acting employee first, and it stays first: "my tasks" is the screen the owner opens, so the
			// weighting below gives the caller the densest share.
			if (pool.Remove(actor)) pool.Insert(0, actor);
			return pool;
		}

		/// The employee(s) kept deliberately empty. Resolved by user name so the intent survives an id change.
		private async Task<HashSet<int>> EmptyStateEmployeeIdsAsync(int companyId, CancellationToken ct)
		{
			var ids = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.Email == "dev.clerk@crossbuy.local")
				.Select(e => e.ID)
				.ToListAsync(ct);
			return ids.ToHashSet();
		}

		// ==========================================================================================
		// TASKS
		//
		// The date distribution is the point of this method, not the count. A hundred tasks all due today
		// exercises one badge; the slots below are what make the overdue badge, the due-today grouping, the
		// "this week" filter and the no-due-date row all appear on the same screen.
		// ==========================================================================================
		private async Task<List<int>> SeedTasksAsync(
			int companyId, int actor, List<int> employees, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			// Everything already owned by this run, by title. Title is the natural key WITHIN the marked set;
			// the seeder guarantees distinct titles, so a present title means "this row already exists".
			//
			// ORDERED: the returned id list is consumed positionally by the checklist, dependency and timesheet
			// steps. See OwnedTaskIdsAsync for what an unordered version cost.
			var existing = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && t.Category != null
							&& t.Category.StartsWith(UatMarkers.RunId))
				.OrderBy(t => t.ID)
				.Select(t => new { t.ID, t.Title })
				.ToListAsync(ct);

			var byTitle = existing.ToDictionary(x => x.Title, x => x.ID, StringComparer.Ordinal);
			var ids = existing.Select(x => x.ID).ToList();
			int madeTasks = 0, statusMoves = 0;

			var today = DateTime.Today;
			var rng = Rng();
			var specs = BuildTaskSpecs(plan, today, employees, rng);

			foreach (var spec in specs)
			{
				ct.ThrowIfCancellationRequested();

				if (byTitle.ContainsKey(spec.Title)) continue;

				var (ok, error, id) = await _tasks.SaveAsync(companyId, new TaskSaveInput
				{
					Title = spec.Title,
					Description = spec.Description,
					AssigneeEmployeeId = spec.AssigneeId,
					Priority = spec.Priority,
					DueDate = spec.DueDate,
					EstimatedHours = spec.EstimatedHours,
					ProgressPct = spec.ProgressPct,
					Category = spec.Category,
					IsScheduled = spec.IsScheduled,
					ExpectedEntityType = spec.ExpectedEntityType,
					ExpectedPartyId = spec.ExpectedPartyId,
					ExpectedFrom = spec.ExpectedFrom,
					ExpectedTo = spec.ExpectedTo,
				}, actor);

				if (!ok)
				{
					notes.Add($"task refused: '{Trim(spec.Title)}' — {error}");
					continue;
				}

				ids.Add(id);
				byTitle[spec.Title] = id;
				madeTasks++;

				// A task is BORN "New" — that is TaskService's rule, and the seeder does not reach around it.
				// A Done task therefore walks New -> InProgress -> Done, which is also what makes its activity
				// history worth opening on the detail screen: three events instead of one.
				if (spec.TargetStatus == "InProgress")
				{
					statusMoves += await MoveAsync(companyId, id, "InProgress", actor, notes);
				}
				else if (spec.TargetStatus == "Done")
				{
					if (spec.ThroughInProgress) statusMoves += await MoveAsync(companyId, id, "InProgress", actor, notes);
					statusMoves += await MoveAsync(companyId, id, "Done", actor, notes);
				}
			}

			created["tasks"] = madeTasks;
			created["taskStatusTransitions"] = statusMoves;
			notes.Add($"tasks: {madeTasks} created, {ids.Count} owned in total, {statusMoves} status transitions");
			return ids;
		}

		private async Task<int> MoveAsync(int companyId, int taskId, string status, int actor, List<string> notes)
		{
			var (ok, error) = await _tasks.ChangeStatusAsync(companyId, taskId, status, actor);
			if (ok) return 1;
			notes.Add($"status move refused: task {taskId} -> {status} — {error}");
			return 0;
		}

		private sealed class TaskSpec
		{
			public required string Title { get; init; }
			public string? Description { get; init; }
			public required int AssigneeId { get; init; }
			public required string Priority { get; init; }
			public DateTime? DueDate { get; init; }
			public required string Category { get; init; }
			public required string TargetStatus { get; init; }
			public bool ThroughInProgress { get; init; }
			public decimal? EstimatedHours { get; init; }
			public int ProgressPct { get; init; }
			public bool IsScheduled { get; init; }
			public string? ExpectedEntityType { get; init; }
			public int? ExpectedPartyId { get; init; }
			public DateTime? ExpectedFrom { get; init; }
			public DateTime? ExpectedTo { get; init; }
		}

		// ---- the two facts the focused tests need, exposed and nothing else --------------------
		//
		// TASK TITLE DISTINCTNESS IS THE IDEMPOTENCY CONTRACT. Skip-if-present keys on the title within the
		// marked set, so two specs sharing a title would silently collapse into one row and the counts would
		// disagree with the plan forever. It is asserted rather than assumed.
		//
		// CATEGORY PREFIX IS THE OWNERSHIP CONTRACT. A spec whose Category did not start with the run id would
		// produce a row that counting cannot see and cleanup cannot delete — an orphan on a cloned database.
		public static IReadOnlyList<(string Title, string Category)> PlannedTaskRows(
			UatPlan plan, DateTime anchorDate, IReadOnlyList<int> employees)
			=> BuildTaskSpecs(plan, anchorDate, employees.ToList(), Rng())
				.Select(s => (s.Title, s.Category)).ToList();

		/// The seeded calendar rows, for the same two reasons: a distinct title is the idempotency key and the
		/// description marker is the ownership rule.
		public static IReadOnlyList<(string Title, string? Description)> PlannedEventRows(
			UatPlan plan, DateTime anchorDate, IReadOnlyList<int> employees, int actor)
			=> BuildEventSpecs(plan, anchorDate, employees.ToList(), actor)
				.Select(s => (s.Title, s.Description)).ToList();

		private static List<TaskSpec> BuildTaskSpecs(UatPlan plan, DateTime today, List<int> employees, Random rng)
		{
			var specs = new List<TaskSpec>();
			var scenarios = UatContent.TaskScenarios;
			string[] priorities = { "Urgent", "High", "Normal", "Normal", "Low" };

			// The due-date slots, as (label, generator). Weighted by the plan so the primary company is dense
			// and the secondary one is deliberately thin — a count that differs is half the isolation proof.
			var slots = new (string Label, int Count, Func<int, DateTime?> Due)[]
			{
				("overdue",   plan.TasksOverdue,   i => today.AddDays(-(1 + i % 30)).AddHours(11)),
				("today",     plan.TasksToday,     i => today.AddHours(9 + i % 9)),
				("tomorrow",  plan.TasksTomorrow,  i => today.AddDays(1).AddHours(10 + i % 6)),
				("thisweek",  plan.TasksThisWeek,  i => today.AddDays(2 + i % 6).AddHours(13)),
				("next30",    plan.TasksNext30,    i => today.AddDays(8 + i % 22).AddHours(12)),
				("far",       plan.TasksFar,       i => today.AddDays(31 + i % 30).AddHours(15)),
				("nodue",     plan.TasksNoDue,     _ => null),
			};

			int seq = 0;
			foreach (var slot in slots)
			{
				for (int i = 0; i < slot.Count; i++)
				{
					var scenario = scenarios[seq % scenarios.Length];
					int assignee = employees[PickAssignee(seq, employees.Count)];
					string priority = priorities[rng.Next(priorities.Length)];
					string status = StatusFor(slot.Label, seq);

					specs.Add(new TaskSpec
					{
						// The sequence number is what guarantees a distinct natural key while keeping the title
						// readable — "دفعة 12" is how these are actually referred to in a real backlog.
						Title = $"{scenario.Ar} — دفعة {++seq}",
						Description = scenario.Desc,
						AssigneeId = assignee,
						Priority = priority,
						DueDate = slot.Due(i),
						Category = UatMarkers.TaskCategoryPrefix + scenario.Area,
						TargetStatus = status,
						ThroughInProgress = status == "Done" && seq % 2 == 0,
						EstimatedHours = seq % 4 == 0 ? null : 2m + seq % 12,
						ProgressPct = status switch
						{
							"Done" => 100,
							"InProgress" => 20 + seq * 7 % 70,
							_ => 0,
						},
					});
				}
			}

			// ---- the layout edge case -------------------------------------------------------------
			specs.Add(new TaskSpec
			{
				Title = UatContent.LongTaskTitle,
				Description = UatContent.LongDescription,
				AssigneeId = employees[0],
				Priority = "Urgent",
				DueDate = today.AddDays(3).AddHours(14),
				Category = UatMarkers.TaskCategoryPrefix + "المشتريات",
				TargetStatus = "InProgress",
				EstimatedHours = 40m,
				ProgressPct = 45,
			});

			// ---- one fully complete, one heavily in progress, both explicitly ---------------------
			//
			// The brief asks for these by name. They are added as their own specs rather than hoped for out of
			// the distribution above, because "at least one" is a guarantee and a distribution is not.
			specs.Add(new TaskSpec
			{
				Title = "الإقفال الشهري للحسابات — يوليو (مكتمل)",
				Description = "أُقفلت الفترة واعتُمد ميزان المراجعة. محفوظ كمرجع للفترة القادمة.",
				AssigneeId = employees[0],
				Priority = "High",
				DueDate = today.AddDays(-6).AddHours(16),
				Category = UatMarkers.TaskCategoryPrefix + "المالية",
				TargetStatus = "Done",
				ThroughInProgress = true,
				EstimatedHours = 24m,
				ProgressPct = 100,
			});
			specs.Add(new TaskSpec
			{
				Title = "مراجعة شاملة لمطابقة المشتريات — قيد التنفيذ",
				Description = UatContent.LongDescription,
				AssigneeId = employees[0],
				Priority = "Urgent",
				DueDate = today.AddDays(2).AddHours(15),
				Category = UatMarkers.TaskCategoryPrefix + "المشتريات",
				TargetStatus = "InProgress",
				EstimatedHours = 36m,
				ProgressPct = 65,
			});

			// ---- scheduled tasks, for the real matcher -------------------------------------------
			//
			// These carry EXPECTED criteria and no link. The matcher then finds the real movements itself: the
			// brief's rule is to exercise the matching mechanism, not to insert rows into its output table.
			foreach (var (entityType, partyId, label, from, to) in plan.ScheduledExpectations)
			{
				specs.Add(new TaskSpec
				{
					Title = $"مطابقة حركة متوقّعة — {label} (مجدولة {specs.Count})",
					Description = "مهمة مجدولة بانتظار حركة مطابقة؛ يربطها المطابِق تلقائيًا أو يعرض المرشّحين للمراجعة.",
					AssigneeId = employees[PickAssignee(specs.Count, employees.Count)],
					Priority = "Normal",
					DueDate = today.AddDays(5).AddHours(12),
					Category = UatMarkers.TaskCategoryPrefix +
							   (entityType == "PurchaseInvoice" ? "المشتريات" : "المبيعات"),
					TargetStatus = "New",
					IsScheduled = true,
					ExpectedEntityType = entityType,
					ExpectedPartyId = partyId,
					// The narrow, ABSOLUTE window from the plan. See UatPlan.ScheduledExpectations for why it is
					// absolute and why its width is the suggestion-count control.
					ExpectedFrom = from,
					ExpectedTo = to,
				});
			}

			return specs;
		}

		// The acting employee takes roughly a third of the work so "my tasks", "my agenda" and the board's
		// "mine" scope are all dense for whoever ran the seeder; the rest round-robins over the others.
		//
		// THE CLAMP IS NOT DEFENSIVE PADDING — it is the single-employee company. This returned
		// `1 + seq % Math.Max(1, poolSize - 1)`, which for a pool of ONE evaluates to `1 + seq % 1` = 1 and
		// indexed past the end. Company 65 has exactly one active employee, so seeding the secondary company —
		// the company whose whole purpose is to prove isolation — died with an IndexOutOfRangeException on its
		// first task. A pool of one means every task goes to that one person, which is the correct answer.
		private static int PickAssignee(int seq, int poolSize)
		{
			if (poolSize <= 1) return 0;
			return seq % 3 == 0 ? 0 : 1 + seq % (poolSize - 1);
		}

		// STATUS MUST NOT CORRELATE WITH ASSIGNEE, and it did.
		//
		// PickAssignee gives the acting employee every task where `seq % 3 == 0`, and this method used to read
		// `seq % 3` as well — so EVERY task the acting employee received landed on the same arm of the switch.
		// The result on a real screen: employee 5 held 36 New, 7 InProgress and 8 Done, so /Tasks/Board — which
		// defaults to scope=mine — had two columns under the ten-card floor the brief asks for, while the
		// company-wide board (62 / 46 / 33) looked fine. A distribution that is correct in aggregate and wrong
		// per person is worse than an obviously wrong one, because the board looks plausible.
		//
		// `seq / 3` advances once per THREE tasks, so it is independent of `seq % 3` and the two choices no
		// longer move together.
		private static string StatusFor(string slot, int seq) => slot switch
		{
			// An overdue task that is Done is not overdue, so this slot never produces one — otherwise the
			// overdue count on the KPI strip would not match the badges on the rows.
			"overdue" => seq % 2 == 0 ? "New" : "InProgress",
			"nodue" => (seq / 3 % 3) switch { 0 => "Done", 1 => "InProgress", _ => "New" },
			"far" => "New",
			_ => (seq / 3 % 3) switch { 0 => "New", 1 => "InProgress", _ => "Done" },
		};

		// ==========================================================================================
		// CHECKLISTS
		// ==========================================================================================
		private async Task SeedChecklistsAsync(
			int companyId, int actor, List<int> taskIds, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			if (taskIds.Count == 0) { created["checklistItems"] = 0; return; }

			var targets = taskIds.Take(plan.ChecklistTasks).ToList();
			int lines = 0, completed = 0, reordered = 0;

			for (int t = 0; t < targets.Count; t++)
			{
				int taskId = targets[t];
				var already = await _checklist.ForTaskAsync(companyId, taskId, ct);
				var have = already.Select(a => a.Title).ToHashSet(StringComparer.Ordinal);

				// Four shapes across the target tasks: all open, partially done, fully done, and one long line.
				int size = 3 + t % 3;
				var wanted = new List<string>();
				for (int i = 0; i < size; i++)
					wanted.Add(UatContent.ChecklistPool[(t * 3 + i) % UatContent.ChecklistPool.Length]);
				if (t == 0) wanted.Add(UatContent.LongChecklistLine);

				var newIds = new List<int>();
				foreach (var title in wanted.Distinct(StringComparer.Ordinal))
				{
					if (have.Contains(title)) continue;
					var (ok, error, id) = await _checklist.AddAsync(companyId, taskId, title, actor, ct);
					if (!ok) { notes.Add($"checklist line refused on task {taskId}: {error}"); continue; }
					newIds.Add(id);
					lines++;
				}

				// completion pattern: t%3==0 all open · t%3==1 partial · t%3==2 fully complete
				var all = await _checklist.ForTaskAsync(companyId, taskId, ct);
				int doneCount = t % 3 switch { 0 => 0, 1 => all.Count / 2, _ => all.Count };
				for (int i = 0; i < doneCount; i++)
				{
					var (ok, _) = await _checklist.SetDoneAsync(companyId, all[i].ID, true, actor, ct);
					if (ok) completed++;
				}

				// ONE reordered checklist, through the real ReorderAsync so line identity is preserved the way
				// the service preserves it — a drag must not reset who ticked a line.
				if (t == 1 && newIds.Count >= 3)
				{
					var order = all.Select(a => a.ID).Reverse().ToList();
					var (ok, error) = await _checklist.ReorderAsync(companyId, taskId, order, ct);
					if (ok) reordered++; else notes.Add($"checklist reorder refused on task {taskId}: {error}");
				}
			}

			created["checklistItems"] = lines;
			notes.Add($"checklist: {lines} lines created across {targets.Count} tasks, " +
					  $"{completed} ticked, {reordered} reordered");
		}

		// ==========================================================================================
		// DEPENDENCIES
		//
		// A valid graph, built through the real service so the cycle guard runs on every edge. The shapes are
		// chosen so the screen has something to draw: simple chains, a branch, and a multi-level path.
		// ==========================================================================================
		private async Task SeedDependenciesAsync(
			int companyId, int actor, List<int> taskIds, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			created["dependencies"] = 0;
			if (taskIds.Count < 8) { notes.Add("dependencies: not enough tasks"); return; }

			var edges = new List<(int Pred, int Succ, string Kind, int Lag)>();
			var t = taskIds;

			// Chains of four:  A -> B -> C -> D. Three of them.
			for (int c = 0; c < plan.DependencyChains && (c * 4) + 3 < t.Count; c++)
			{
				int b = c * 4;
				edges.Add((t[b], t[b + 1], TaskDependencyKinds.FinishToStart, 0));
				edges.Add((t[b + 1], t[b + 2], TaskDependencyKinds.FinishToStart, 1));
				edges.Add((t[b + 2], t[b + 3], TaskDependencyKinds.FinishToStart, 0));
			}

			// A branch: one predecessor fans out to three successors.
			int fanBase = plan.DependencyChains * 4;
			if (fanBase + 3 < t.Count)
			{
				edges.Add((t[fanBase], t[fanBase + 1], TaskDependencyKinds.FinishToStart, 0));
				edges.Add((t[fanBase], t[fanBase + 2], TaskDependencyKinds.StartToStart, 0));
				edges.Add((t[fanBase], t[fanBase + 3], TaskDependencyKinds.FinishToFinish, 2));
			}

			// A deeper path, so the blocked-by chain on the detail screen is more than one hop.
			int deep = fanBase + 4;
			for (int i = 0; i < plan.DependencyDeepHops && deep + i + 1 < t.Count; i++)
				edges.Add((t[deep + i], t[deep + i + 1], TaskDependencyKinds.FinishToStart, i % 2));

			// Cross-links between separate chains — still acyclic because they always point FORWARD in the
			// list, and the service would refuse anything that closed a loop anyway.
			for (int i = 0; i < plan.DependencyCrossLinks; i++)
			{
				int p = i * 3, s = i * 3 + 7;
				if (s < t.Count) edges.Add((t[p], t[s], TaskDependencyKinds.FinishToStart, 0));
			}

			int made = 0, refused = 0;
			foreach (var e in edges.Take(plan.DependencyTarget))
			{
				var (ok, error, _) = await _dependencies.AddAsync(companyId, e.Pred, e.Succ, e.Kind, e.Lag, actor, ct);
				if (ok) { made++; continue; }
				refused++;
				// "already exists" is the idempotent path and is not worth a note on every re-run.
				if (error != null && !error.Contains("موجودة بالفعل")) notes.Add($"dependency refused: {error}");
			}

			int owned = await _db.TaskDependencies.AsNoTracking()
				.CountAsync(d => d.CompanyId == companyId && taskIds.Contains(d.SuccessorTaskId), ct);

			created["dependencies"] = made;
			notes.Add($"dependencies: {made} created, {refused} refused (duplicates on a re-run), {owned} owned");
		}

		// ==========================================================================================
		// TEMPLATES
		// ==========================================================================================
		private async Task SeedTemplatesAsync(
			int companyId, int actor, List<int> employees, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			var existing = await _db.TaskTemplates.AsNoTracking()
				.Where(x => x.CompanyId == companyId && x.Name.StartsWith(UatMarkers.RunId))
				.Select(x => x.Name)
				.ToListAsync(ct);
			var have = existing.ToHashSet(StringComparer.Ordinal);

			int made = 0, items = 0;
			for (int i = 0; i < plan.Templates && i < UatContent.Templates.Length; i++)
			{
				var def = UatContent.Templates[i];
				string name = UatMarkers.NamePrefix + def.Ar;
				if (have.Contains(name)) continue;

				var lines = UatContent.ItemsFor(i);
				var template = new TaskTemplate
				{
					CompanyId = companyId,
					Name = name,
					NameEn = $"{def.En} [{UatMarkers.RunId}]",
					Description = def.Desc,
					// One template is deliberately INACTIVE so the active/inactive filter on /Tasks/Templates
					// has something to filter, and so "apply" correctly refuses it.
					IsActive = i != plan.Templates - 1,
					Items = lines.Select((l, idx) => new TaskTemplateItem
					{
						CompanyId = companyId,
						Title = l.Ar,
						TitleEn = l.En,
						Priority = l.Priority,
						DueOffsetDays = l.Offset,
						SortOrder = idx + 1,
						EstimatedHours = 2m + idx * 2,
						// A predecessor chain inside the template, by sort order. The template service validates
						// that the order exists and is not self-referential, so this exercises that check too.
						PredecessorSortOrder = idx == 0 ? null : idx,
						DefaultAssigneeEmployeeId = idx % 2 == 0 ? employees[idx % employees.Count] : null,
						ChecklistLines = l.Checklist,
					}).ToList(),
				};

				var (ok, error, _) = await _templates.SaveAsync(companyId, template, actor, ct);
				if (!ok) { notes.Add($"template refused '{Trim(name)}': {error}"); continue; }
				made++;
				items += lines.Length;
			}

			created["taskTemplates"] = made;
			created["taskTemplateItems"] = items;
			notes.Add($"templates: {made} created with {items} items");
		}

		// ==========================================================================================
		// AUTO RULES
		//
		// A RECORDED DEVIATION, not an omission. The brief asks for "10+ rules"; TaskAutoRule.RuleType is a
		// CLOSED catalogue of exactly five (LowStock, OverdueInvoice, WorkOrderQc, DeliveryReady,
		// NewEmployeeOnboard) declared in TaskGeneratorService, and /Tasks/AutoRules renders one row per
		// catalogue entry. A sixth row would need an invented trigger type, which the brief forbids in the
		// same paragraph. So the screen is exercised at its real maximum — five rules with varied assignees
		// and a mixed enabled state — and the shortfall against the target is reported rather than faked.
		// ==========================================================================================
		private async Task SeedAutoRulesAsync(
			int companyId, List<int> employees, Dictionary<string, int> created, List<string> notes,
			CancellationToken ct)
		{
			string[] types = { "LowStock", "OverdueInvoice", "WorkOrderQc", "DeliveryReady", "NewEmployeeOnboard" };
			int touched = 0;

			for (int i = 0; i < types.Length; i++)
			{
				// One rule left disabled on purpose, so the toggle has both states on screen.
				bool active = i != 2;
				int? assignee = i % 2 == 0 ? employees[i % employees.Count] : null;
				var (ok, error) = await _autoRules.SetRuleAsync(companyId, types[i], active, assignee);
				if (ok) touched++; else notes.Add($"auto rule '{types[i]}' refused: {error}");
			}

			int rows = await _db.TaskAutoRules.AsNoTracking().CountAsync(r => r.CompanyId == companyId, ct);
			created["autoRulesConfigured"] = touched;
			notes.Add($"auto rules: {touched} configured, {rows} rows for this company. " +
					  "DEVIATION: RuleType is a closed 5-value catalogue, so 10+ per company is not reachable " +
					  "without inventing a trigger type. Not done.");
		}

		// ==========================================================================================
		// TIMESHEETS — the source the Hours Report and the productivity KPIs actually read
		//
		// Through ITimesheetService.AddManualAsync, which is what maintains TaskItem.ActualHours. Writing the
		// rows directly would leave ActualHours stale and every hours figure on a task would disagree with
		// its own timesheet — the brief's "reuse the real supported source" rule, concretely.
		// ==========================================================================================
		private async Task SeedTimesheetsAsync(
			int companyId, List<int> taskIds, List<int> employees, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			created["timesheetEntries"] = 0;
			if (taskIds.Count == 0) return;

			var targets = taskIds.Take(plan.TimesheetTasks).ToList();
			var existing = await _db.TimesheetEntries.AsNoTracking()
				.Where(e => e.CompanyId == companyId && targets.Contains(e.TaskId))
				.Select(e => new { e.TaskId, e.EmployeeId, e.WorkDate })
				.ToListAsync(ct);
			var have = existing.Select(e => $"{e.TaskId}|{e.EmployeeId}|{e.WorkDate:yyyy-MM-dd}").ToHashSet();

			var today = DateTime.Today;
			int made = 0;
			var descriptions = new[]
			{
				"مراجعة المستندات والمطابقة", "زيارة ميدانية", "إعداد ورقة العمل",
				"اتصال ومتابعة مع الطرف الآخر", "Data collection and cross-check", "إدخال ومراجعة القيود",
			};

			for (int i = 0; i < targets.Count; i++)
			{
				int taskId = targets[i];
				// Two or three days of work per task, spread across the previous 30 days so the Hours Report
				// has more than one date and a range filter has something to exclude.
				int days = 2 + i % 2;
				for (int d = 0; d < days; d++)
				{
					int empId = employees[(i + d) % employees.Count];
					var workDate = today.AddDays(-(2 + (i * 3 + d * 5) % 28));
					string key = $"{taskId}|{empId}|{workDate:yyyy-MM-dd}";
					if (have.Contains(key)) continue;

					decimal hours = 1.5m + (i + d) % 6;
					var (ok, error) = await _timesheets.AddManualAsync(
						companyId, taskId, empId, workDate, hours, descriptions[(i + d) % descriptions.Length]);
					if (ok) { made++; have.Add(key); }
					else notes.Add($"timesheet refused (task {taskId}): {error}");
				}
			}

			created["timesheetEntries"] = made;
			notes.Add($"timesheets: {made} entries across {targets.Count} tasks");
		}

		// ==========================================================================================
		// MATCH SUGGESTIONS — produced by the REAL matcher, never inserted
		// ==========================================================================================
		private async Task RunMatcherAsync(
			int companyId, Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			var summary = await _matcher.RunAsync(companyId);
			int pending = await _db.TaskMatchSuggestions.AsNoTracking()
				.CountAsync(s => s.CompanyId == companyId && s.ResolvedAt == null, ct);

			created["matchSuggestions"] = summary.Suggested;
			notes.Add($"matcher: scanned {summary.Scanned}, auto-linked {summary.AutoLinked}, " +
					  $"suggested {summary.Suggested}; {pending} suggestions pending review");
		}

		// ==========================================================================================
		// CALENDAR RESOURCES
		//
		// Written through the DbContext: CalendarResource has no CRUD service in the product today. The
		// unique index on (CompanyId, Name) is the idempotency key, so a second run cannot double them even
		// if the pre-read were wrong.
		// ==========================================================================================
		private async Task<List<int>> SeedResourcesAsync(
			int companyId, int actor, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			// ORDERED BY ID, AND THE ORDER IS THE IDEMPOTENCY CONTRACT — not a tidiness choice.
			//
			// This query had no OrderBy, and SQL Server gives no order without one. The returned list is used
			// positionally further down (`resourceIds[0]` is the resource the deliberate double-booking uses,
			// and `resourceIds[i / 3 % Count]` picks the room for each event), so a different row order on the
			// second run picked DIFFERENT resources, produced 30 new (event, resource) pairs that the pre-read
			// had not seen, and doubled the bookings from 30 to 60. It is the same defect the calendar day-grid
			// comment in CalendarSchedulingService already warns about, in a place nobody had looked.
			var existing = await _db.CalendarResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId && r.Name.StartsWith(UatMarkers.ResourceNamePrefix))
				.OrderBy(r => r.ID)
				.Select(r => new { r.ID, r.Name })
				.ToListAsync(ct);

			var have = existing.ToDictionary(x => x.Name, x => x.ID, StringComparer.Ordinal);
			var ids = existing.Select(x => x.ID).ToList();
			int made = 0;

			for (int i = 0; i < plan.Resources && i < UatContent.Resources.Length; i++)
			{
				var def = UatContent.Resources[i];
				string name = UatMarkers.ResourceNamePrefix + def.Ar;
				if (have.ContainsKey(name)) continue;

				var row = new CalendarResource
				{
					CompanyId = companyId,
					Name = name,
					NameEn = $"{def.En} {UatMarkers.ResourceNameEnTag}",
					Kind = def.Kind,
					Capacity = def.Capacity,
					// One resource inactive, so the ResourceView's active filter is exercised.
					IsActive = i != plan.Resources - 1,
					CreatedAt = DateTime.UtcNow,
					CreatedByEmployeeId = actor,
				};
				_db.CalendarResources.Add(row);
				await _db.SaveChangesAsync(ct);
				ids.Add(row.ID);
				made++;
			}

			created["calendarResources"] = made;
			notes.Add($"calendar resources: {made} created, {ids.Count} owned");
			return ids;
		}

		// ==========================================================================================
		// CALENDAR EVENTS
		//
		// Through ICalendarService.SaveAsync — the real service, which owns the visibility rule and the
		// attendee replacement. Deliberately NOT through CalendarController.Save: that action additionally
		// EMAILS an .ics invite to every attendee with an address (SendInvitesAsync). Seeding must not queue
		// mail, so the seeder stops at the service boundary and the notification is written by the platform
		// notification step instead. That is also the honest reading of "run the real application service":
		// the service is the calendar; the email is the controller.
		//
		// TIMES ARE LOCAL WALL-CLOCK, matching what the product writes today (CalendarService stores the
		// posted value and emits ISO with no offset, BL/CalendarService.cs:129). Storing UTC here would make
		// /Calendar draw every event three hours off on this host.
		// ==========================================================================================
		private async Task<List<int>> SeedCalendarEventsAsync(
			int companyId, int actor, List<int> employees, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			// Ordered for the same reason as the resources above: the id list is consumed positionally by the
			// resource-booking and recurrence steps.
			var existing = await _db.CalendarEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.DeletedAt == null
							&& e.Description != null && e.Description.Contains(UatMarkers.CalendarDescriptionTag))
				.OrderBy(e => e.Id)
				.Select(e => new { e.Id, e.Title })
				.ToListAsync(ct);

			var have = existing.Select(x => x.Title).ToHashSet(StringComparer.Ordinal);
			var ids = existing.Select(x => x.Id).ToList();
			int made = 0, withAttendees = 0;

			var today = DateTime.Today;
			var specs = BuildEventSpecs(plan, today, employees, actor);
			int attendeeDelta = 0;

			foreach (var spec in specs)
			{
				ct.ThrowIfCancellationRequested();
				if (have.Contains(spec.Title)) continue;

				attendeeDelta += spec.Attendees.Count;
				int id = await _calendar.SaveAsync(companyId, spec.OwnerId, new CalEventInput
				{
					Title = spec.Title,
					Description = spec.Description,
					Location = spec.Location,
					AllDay = spec.AllDay,
					StartAt = spec.Start,
					EndAt = spec.End,
					Scope = spec.Scope,
					Attendees = spec.Attendees,
				});

				ids.Add(id);
				have.Add(spec.Title);
				made++;
				if (spec.Attendees.Count > 0) withAttendees++;
			}

			int attendeeRows = ids.Count == 0
				? 0
				: await _db.CalendarEventAttendees.AsNoTracking().CountAsync(a => ids.Contains(a.EventId), ct);

			created["calendarEvents"] = made;
			// A DELTA, not the total. It read the owned total, which made a second run look as though it had
			// created 191 attendee rows when it created none — the one number an idempotency proof must not
			// get wrong. The total is still reported, in the note and in Counts.
			created["calendarAttendees"] = attendeeDelta;
			notes.Add($"calendar: {made} events created ({withAttendees} with attendees), " +
					  $"{ids.Count} owned, {attendeeRows} attendee rows in total");
			return ids;
		}

		private sealed class EventSpec
		{
			public required string Title { get; init; }
			public string? Description { get; init; }
			public string? Location { get; init; }
			public required bool AllDay { get; init; }
			public required DateTime Start { get; init; }
			public DateTime? End { get; init; }
			public required string Scope { get; init; }
			public required int OwnerId { get; init; }
			public List<int> Attendees { get; init; } = new();
		}

		private static List<EventSpec> BuildEventSpecs(UatPlan plan, DateTime today, List<int> employees, int actor)
		{
			var specs = new List<EventSpec>();
			var pool = UatContent.EventScenarios;
			int seq = 0;

			// (label, count, day offset generator)
			var bands = new (string Label, int Count, Func<int, int> Offset)[]
			{
				("past",     plan.EventsPast,     i => -(1 + i % 30)),
				("today",    plan.EventsToday,    _ => 0),
				("tomorrow", plan.EventsTomorrow, _ => 1),
				("thisweek", plan.EventsThisWeek, i => 2 + i % 6),
				("nextweek", plan.EventsNextWeek, i => 8 + i % 7),
				("nextmonth",plan.EventsNextMonth,i => 16 + i % 30),
			};

			foreach (var band in bands)
			{
				for (int i = 0; i < band.Count; i++)
				{
					var def = pool[seq % pool.Length];
					int owner = employees[seq % employees.Count];

					// Hours are laid out so the SAME day carries overlapping meetings AND meetings that merely
					// touch. A pair that touches at an hour boundary (10:00-11:00 then 11:00-12:00) is the case
					// the conflict engine must NOT report; a pair that straddles it must be reported. Both are
					// present on today's date on purpose.
					int startHour = 8 + seq * 2 % 10;
					int startMinute = seq % 4 == 0 ? 30 : 0;
					var start = today.AddDays(band.Offset(i)).AddHours(startHour).AddMinutes(startMinute);

					bool allDay = band.Label != "today" && seq % 11 == 0;
					var end = allDay ? start.Date : start.AddMinutes(def.Minutes);

					var attendees = new List<int>();
					int attendeeCount = seq % 5;                     // 0..4, so single-attendee and none both occur
					for (int a = 0; a < attendeeCount; a++)
						attendees.Add(employees[(seq + a + 1) % employees.Count]);
					if (seq % 7 == 0) attendees = new List<int> { employees[(seq + 1) % employees.Count] };

					specs.Add(new EventSpec
					{
						Title = $"{def.Ar} — {seq + 1:00}",
						Description = Describe(def.En, seq),
						Location = def.Location,
						AllDay = allDay,
						Start = start,
						End = end,
						Scope = def.Scope,
						OwnerId = owner,
						Attendees = attendees.Distinct().ToList(),
					});
					seq++;
				}
			}

			// ---- the deliberate same-day scenarios, added explicitly ------------------------------
			//
			// The distribution above produces overlaps by accident; these three produce them BY NAME, so the
			// conflict/availability engine can be tested against a case a reviewer can point at.
			var slotOwner = employees[0];
			var slotGuest = employees.Count > 1 ? employees[1] : employees[0];

			specs.Add(new EventSpec
			{
				Title = "اجتماع أ — يتقاطع مع اجتماع ب (تعارض مقصود)",
				Description = Describe("Deliberate overlap A", 900),
				Location = "قاعة الاجتماعات أ", AllDay = false,
				Start = today.AddHours(10), End = today.AddHours(11).AddMinutes(30),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = new List<int> { slotGuest },
			});
			specs.Add(new EventSpec
			{
				Title = "اجتماع ب — يتقاطع مع اجتماع أ (تعارض مقصود)",
				Description = Describe("Deliberate overlap B", 901),
				Location = "قاعة الاجتماعات أ", AllDay = false,
				Start = today.AddHours(11), End = today.AddHours(12),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = new List<int> { slotGuest },
			});
			specs.Add(new EventSpec
			{
				Title = "اجتماع ج — ملاصق ولا يجب أن يُعدّ تعارضًا",
				Description = Describe("Adjacent, must NOT conflict", 902),
				Location = "قاعة الاجتماعات ب", AllDay = false,
				Start = today.AddHours(12), End = today.AddHours(13),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = new List<int> { slotGuest },
			});
			specs.Add(new EventSpec
			{
				Title = "حجز مغلق لأعمال إدارية (وقت مشغول)",
				Description = Describe("Busy block", 903),
				Location = null, AllDay = false,
				Start = today.AddHours(14), End = today.AddHours(16),
				Scope = "Personal", OwnerId = slotOwner,
				Attendees = new List<int>(),
			});
			specs.Add(new EventSpec
			{
				Title = "اجتماع قصير (١٥ دقيقة)",
				Description = Describe("Short meeting", 904),
				Location = "قاعة التدريب", AllDay = false,
				Start = today.AddHours(17), End = today.AddHours(17).AddMinutes(15),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = new List<int> { slotGuest },
			});
			specs.Add(new EventSpec
			{
				Title = UatContent.LongEventSubject,
				Description = Describe(UatContent.LongEventSubject, 905),
				Location = "قاعة المؤتمرات", AllDay = false,
				Start = today.AddDays(4).AddHours(9), End = today.AddDays(4).AddHours(13),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = employees.Take(6).ToList(),
			});
			specs.Add(new EventSpec
			{
				Title = "يوم جرد كامل (يوم كامل)",
				Description = Describe("Full-day stocktake", 906),
				Location = "المستودع الرئيسي", AllDay = true,
				Start = today.AddDays(6), End = today.AddDays(6),
				Scope = "Company", OwnerId = slotOwner,
				Attendees = employees.Take(4).ToList(),
			});

			return specs;
		}

		/// Every seeded event's description ENDS with the run marker. The narrative text comes first so the
		/// event still reads like an event on the detail popup.
		private static string Describe(string? english, int seq)
			=> (string.IsNullOrWhiteSpace(english)
					? "حدث ضمن بيانات اختبار القبول."
					: $"{english}. حدث ضمن بيانات اختبار القبول.")
			   + $"\n{UatMarkers.CalendarDescriptionTag}#{seq}";

		// ==========================================================================================
		// RESOURCE BOOKINGS — including a deliberate resource double-booking
		// ==========================================================================================
		private async Task SeedEventResourcesAsync(
			int companyId, List<int> eventIds, List<int> resourceIds, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			created["calendarEventResources"] = 0;
			if (eventIds.Count == 0 || resourceIds.Count == 0) return;

			var existing = await _db.CalendarEventResources.AsNoTracking()
				.Where(r => r.CompanyId == companyId && eventIds.Contains(r.EventId))
				.Select(r => new { r.EventId, r.ResourceId })
				.ToListAsync(ct);
			var have = existing.Select(x => $"{x.EventId}|{x.ResourceId}").ToHashSet();

			// THE PAIR SET IS DECIDED FIRST, THEN THE MISSING ONES ARE INSERTED.
			//
			// This used to be one loop whose cap was `made < plan.EventResourceLinks`, where `made` counted only
			// NEW rows. On a re-run every planned pair was already present, so the cap was never reached and the
			// loop kept walking FORWARD through the event list looking for more events to book — adding six
			// bookings that were never in the plan, every single run. The count grew 30 -> 36 -> 42.
			//
			// Deciding the intended set up front makes the plan a fact rather than a side effect of how much
			// already existed, which is what "idempotent" has to mean here.
			var planned = new List<(int EventId, int ResourceId)>();
			for (int i = 0; i < eventIds.Count && planned.Count < plan.EventResourceLinks; i += 3)
			{
				// Every third event books a room, so the resource rows have gaps as well as content.
				planned.Add((eventIds[i], resourceIds[i / 3 % resourceIds.Count]));
			}

			int made = 0;
			foreach (var (eventId, resourceId) in planned)
			{
				string key = $"{eventId}|{resourceId}";
				if (have.Contains(key)) continue;

				_db.CalendarEventResources.Add(new CalendarEventResource
				{
					CompanyId = companyId, EventId = eventId, ResourceId = resourceId,
				});
				have.Add(key);
				made++;
			}

			// The deliberate RESOURCE conflict: the two overlapping "تعارض مقصود" events already share a
			// location, and now they share a bookable resource as well — which is what the conflict engine
			// actually reads.
			var overlapping = await _db.CalendarEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.DeletedAt == null && e.Title.Contains("تعارض مقصود"))
				.Select(e => e.Id)
				.ToListAsync(ct);

			foreach (var id in overlapping)
			{
				string key = $"{id}|{resourceIds[0]}";
				if (have.Contains(key)) continue;
				_db.CalendarEventResources.Add(new CalendarEventResource
				{
					CompanyId = companyId, EventId = id, ResourceId = resourceIds[0],
				});
				have.Add(key);
				made++;
			}

			if (made > 0) await _db.SaveChangesAsync(ct);
			created["calendarEventResources"] = made;
			notes.Add($"resource bookings: {made} created (including a deliberate double-booking of " +
					  $"resource {resourceIds[0]} by the two overlapping events)");
		}

		// ==========================================================================================
		// RECURRENCE — through the real scheduling service, using the real recurrence model
		//
		// NO EXPANDED OCCURRENCES ARE WRITTEN. CalendarSchedulingService expands at READ time
		// (ExpandWindowAsync), so materialising occurrences would produce a second, divergent answer to
		// "when does this repeat". One schedule row per series, exactly as the model intends.
		// ==========================================================================================
		private async Task SeedRecurrenceAsync(
			int companyId, int actor, List<int> eventIds, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			created["calendarSchedules"] = 0;
			if (eventIds.Count == 0) return;

			var already = await _db.CalendarEventSchedules.AsNoTracking()
				.Where(s => s.CompanyId == companyId && eventIds.Contains(s.EventId))
				.Select(s => s.EventId)
				.ToListAsync(ct);
			var have = already.ToHashSet();

			var today = DateTime.Today;

			// EVERY SERIES CARRIES AN EXPLICIT TIME ZONE, AND THAT IS A WORKAROUND FOR A PRODUCT DEFECT.
			//
			// CalendarEventSchedule.TimeZoneId documents null as "the company default, resolved at read time —
			// never silently treated as UTC", and SaveScheduleAsync accepts null happily (it validates the zone
			// only when one is supplied). But the READ path does not honour that contract:
			//
			//     CalendarSchedulingService.Expand (line ~287):
			//         var zone = TaskCalendarTime.ResolveZone(schedule.TimeZoneId);
			//
			// ResolveZone's two-argument overload exists precisely to take a company default; Expand calls the
			// one-argument form, so a null TimeZoneId throws TimeZoneUnresolvedException. ExpandWindowAsync has
			// no catch, so ONE such row turns /Calendar/Timeline and /Calendar/ResourceView into a hard 500 for
			// the ENTIRE company — not a missing row, the whole screen. Verified: seeding five null-zone series
			// broke both screens; setting the zone fixed both.
			//
			// It is recorded for the Calendar owner and NOT fixed here — Expand is product business logic and
			// this tab does not change it. The dataset avoids the defect instead, so the recurrence feature can
			// actually be reviewed. Note the defect was unreachable before this run: CalendarEventSchedules had
			// zero rows, so nothing had ever been expanded.
			const string CompanyZone = "Asia/Riyadh";

			// Each entry ends by DATE or by COUNT — never neither, never both. The service refuses anything
			// else, so these are the shapes the model actually supports.
			var series = new List<(string Kind, int Interval, string? Weekdays, DateTime? Until, int? Count, string? Zone, string? Exceptions, string Why)>
			{
				(RecurrenceKinds.Daily,   1, null,          null,                  10,   CompanyZone, null, "daily, ends after 10"),
				(RecurrenceKinds.Weekly,  1, "Sun,Tue,Thu", today.AddDays(60),     null, CompanyZone, null, "weekly on three days, ends by date"),
				(RecurrenceKinds.Monthly, 1, null,          null,                  6,    CompanyZone, null, "monthly, ends after 6"),
				(RecurrenceKinds.Weekly,  2, "Mon",         today.AddDays(90),     null, CompanyZone, null, "every second Monday"),
				(RecurrenceKinds.Daily,   1, null,          today.AddDays(21),     null, CompanyZone,
					$"{today.AddDays(3):yyyy-MM-dd},{today.AddDays(7):yyyy-MM-dd}", "daily with two cancelled occurrences"),
				// WALL-CLOCK SEMANTICS. The zone is stored as an id, not an offset, so "09:00 every week" stays
				// 09:00 through a DST change. Asia/Riyadh has no DST, so this one uses a zone that actually
				// moves — that is the whole reason the column is a zone id and not an offset.
				(RecurrenceKinds.Weekly,  1, "Wed",         null,                  8,    "Europe/London", null,
					"weekly in a DST zone — proves local wall-clock is preserved across the clock change"),
			};

			int made = 0;
			for (int i = 0; i < series.Count && i < plan.RecurringSeries && i < eventIds.Count; i++)
			{
				// Spread the schedules over the event list rather than the first N, so a recurring series is
				// not always the first row on every screen.
				int eventId = eventIds[i * 5 % eventIds.Count];
				if (have.Contains(eventId)) continue;

				var s = series[i];
				var (ok, error) = await _scheduling.SaveScheduleAsync(companyId, new CalendarEventSchedule
				{
					EventId = eventId,
					TimeZoneId = s.Zone,
					RecurrenceKind = s.Kind,
					Interval = s.Interval,
					ByWeekdays = s.Weekdays,
					UntilLocalDate = s.Until,
					OccurrenceCount = s.Count,
					ExceptionDates = s.Exceptions,
				}, actor, ct);

				if (!ok) { notes.Add($"recurrence refused on event {eventId} ({s.Why}): {error}"); continue; }
				have.Add(eventId);
				made++;
			}

			created["calendarSchedules"] = made;
			notes.Add($"recurrence: {made} series created (daily/weekly/monthly, end-by-count and end-by-date, " +
					  "one with cancelled occurrences, one in a DST zone for wall-clock semantics)");
		}

		private static string Trim(string s) => s.Length <= 48 ? s : s[..48] + "…";
	}
}
