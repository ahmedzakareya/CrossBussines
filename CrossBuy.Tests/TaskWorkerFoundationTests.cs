using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// TASK WORKER FOUNDATION — overdue pipeline ownership, worker safety, company isolation.
	//
	// WHAT WAS WRONG. ITaskOverdueSweepService had an implementation, a dedup contract and tests, but no DI
	// registration and therefore no caller. Escalation ran; overdue detection did not. A task could pass its
	// due date, raise no Task.BecameOverdue event, send the assignee no notification, and then escalate to the
	// manager after the grace period — so the manager heard about it before the assignee did.
	//
	// Separately, TaskGeneratorHostedService and TaskScheduleMatchHostedService both WROTE rows while carrying
	// `const int CompanyId = 1`, no worker gate, no bound company scope and no certification suppression. Two
	// app instances duplicated their writes, and only company 1 was ever processed.
	//
	// These tests pin the corrected foundation. Several are written as MUTATION PROOFS: the doubles throw if a
	// guard is skipped, so deleting the guard fails the test rather than quietly passing it.
	// ==========================================================================================
	internal sealed class OverdueFixture : IDisposable
	{
		public const int CompanyA = 1;
		public const int CompanyB = 65;

		private readonly SqliteConnection _conn;
		private readonly CompanyScopeHolder _holder = new();

		public CrossDbContext Db { get; }
		public UatRecordingNotificationService Notifications { get; }
		public ITaskOverdueSweepService Overdue { get; }
		public ITaskEscalationService Escalation { get; }

		public OverdueFixture(bool contextResolvable = true, int companyId = CompanyA)
		{
			_conn = new SqliteConnection("DataSource=:memory:");
			_conn.Open();
			_holder.Set(companyId, null);

			Db = NewContext();
			Db.Database.EnsureCreated();
			using (var cmd = _conn.CreateCommand()) { cmd.CommandText = "PRAGMA foreign_keys = OFF;"; cmd.ExecuteNonQuery(); }

			Notifications = new UatRecordingNotificationService(Db);
			var notify = new TaskNotificationService(Db, Notifications);

			IBusinessContextAccessor accessor = contextResolvable
				? new StubContextAccessor(new BusinessContext
				{
					CompanyId = companyId, EmployeeId = 10, UserId = "u10", Source = BusinessContextSource.System,
				})
				: StubContextAccessor.Unresolved();

			var events = new BusinessEventService(Db, new EntityRegistry(Db), accessor, NullLogger<BusinessEventService>.Instance);
			var publisher = new TaskCalendarEventPublisher(events);

			Overdue = new TaskOverdueSweepService(Db, notify, publisher, accessor);

			var config = new ConfigurationBuilder().AddInMemoryCollection(
				new Dictionary<string, string?> { ["Tasks:Escalation:GraceHours"] = "24" }).Build();
			Escalation = new TaskEscalationService(Db, notify, publisher,
				new OrgHierarchy(Db, NullLogger<OrgHierarchy>.Instance), config, accessor);
		}

		public CrossDbContext NewContext() => new(
			new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlite(_conn)
				.AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
				.Options, _holder);

		/// A SECOND, independent sweep service over the SAME database — what a restart, or a second app
		/// instance that slipped past the gate, actually looks like. Nothing is remembered in memory, so only
		/// the persisted evidence can prevent a duplicate.
		public ITaskOverdueSweepService SecondInstance()
		{
			var notify = new TaskNotificationService(Db, Notifications);
			var accessor = new StubContextAccessor(new BusinessContext
			{
				CompanyId = CompanyA, EmployeeId = 10, UserId = "u10", Source = BusinessContextSource.System,
			});
			return new TaskOverdueSweepService(Db, notify,
				new TaskCalendarEventPublisher(new BusinessEventService(Db, new EntityRegistry(Db), accessor,
					NullLogger<BusinessEventService>.Instance)), accessor);
		}

		public Employee AddEmployee(int companyId, string tag)
		{
			var e = new Employee
			{
				FirstName = "T", LastName = "T", FullName = "wf-" + tag, FullNameEn = "wf-" + tag,
				EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
				Email = $"wf-{tag}-{Guid.NewGuid():N}@example.com", ProfileImage = "", Gender = "M", MaritalStatus = "S",
				UserId = "user-wf-" + tag + "-" + Guid.NewGuid().ToString("N")[..8],
			};
			Db.Employee.Add(e); Db.SaveChanges(); return e;
		}

		public TaskItem AddTask(int companyId, int assigneeId, DateTime? dueUtc, string status = "New")
		{
			var t = new TaskItem
			{
				CompanyId = companyId, Title = "wf task", AssigneeEmployeeId = assigneeId,
				CreatedByEmployeeId = assigneeId, Status = status, Priority = "Normal",
				DueDate = dueUtc, CreatedAt = DateTime.UtcNow,
			};
			Db.TaskItems.Add(t); Db.SaveChanges(); return t;
		}

		public int OverdueNotifications(int taskId) => Db.Notifications.AsNoTracking()
			.Count(n => n.Type == TaskNotificationKinds.BecameOverdue && n.EntityId == taskId);

		public int OverdueEvents(int taskId) => Db.BusinessEvents.AsNoTracking()
			.Count(e => e.EventType == "Task.BecameOverdue" && e.EntityId == taskId);

		public void Dispose() { Db.Dispose(); _conn.Dispose(); }
	}

	public class TaskWorkerFoundationTests
	{
		private static DateTime Now => TaskCalendarTime.UtcNow();

		// =====================================================================================
		// THE OVERDUE PIPELINE — first occurrence, then idempotency
		// =====================================================================================

		[Fact]                                              // 1. future task → no overdue
		public async Task A_task_due_in_the_future_produces_no_overdue_notification_or_event()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddDays(3));

			var r = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(0, r.Examined);
			Assert.Equal(0, r.Notified);
			Assert.Equal(0, f.OverdueNotifications(t.ID));
			Assert.Equal(0, f.OverdueEvents(t.ID));
		}

		[Fact]                                              // 2 + 3. one event AND one assignee notification
		public async Task An_overdue_incomplete_task_notifies_the_assignee_once_and_records_one_event()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-3));

			var r = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(1, r.Examined);
			Assert.Equal(1, r.Notified);
			Assert.Equal(1, r.EventsRecorded);
			Assert.Equal(0, r.EventsSkippedNoContext);

			// the ASSIGNEE is the recipient — escalation is what tells a manager, and only later
			var sent = f.Notifications.Sent.Where(n => n.Type == TaskNotificationKinds.BecameOverdue).ToList();
			Assert.Single(sent);
			Assert.Equal(emp.ID, sent[0].RecipientEmployeeId);
			Assert.Equal(OverdueFixture.CompanyA, sent[0].CompanyId);
			Assert.Equal($"/Tasks/Index?taskId={t.ID}", sent[0].Url);

			Assert.Equal(1, f.OverdueNotifications(t.ID));
			Assert.Equal(1, f.OverdueEvents(t.ID));
		}

		[Fact]                                              // 4. repeated sweep → no duplicate
		public async Task Repeated_sweeps_of_the_same_missed_due_date_add_nothing()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-5));

			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);
			var second = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);
			var third = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(0, second.Notified);
			Assert.Equal(0, third.Notified);
			Assert.Equal(1, second.AlreadyNotified);
			Assert.Equal(1, f.OverdueNotifications(t.ID));   // one bell
			Assert.Equal(1, f.OverdueEvents(t.ID));          // one durable fact
		}

		[Fact]                                              // 5. restart → no duplicate
		public async Task A_restart_re_reads_the_evidence_and_does_not_re_notify()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-6));
			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);

			var afterRestart = await f.SecondInstance().SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(0, afterRestart.Notified);
			Assert.Equal(1, f.OverdueNotifications(t.ID));
			Assert.Equal(1, f.OverdueEvents(t.ID));
		}

		[Fact]                                              // 6. two instances → no duplicate
		public async Task Two_worker_instances_sweeping_the_same_company_produce_one_notification()
		{
			// Belt AND braces: the worker gate is what stops two instances running at once, but the sweep must
			// also be safe if one ever slips past it — because idempotency here comes from persisted evidence,
			// not from being the only runner.
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-8));

			var first = f.Overdue;
			var second = f.SecondInstance();
			var a = await first.SweepAsync(OverdueFixture.CompanyA);
			var b = await second.SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(1, a.Notified + b.Notified);        // exactly one of them delivered
			Assert.Equal(1, f.OverdueNotifications(t.ID));
			Assert.Equal(1, f.OverdueEvents(t.ID));
		}

		[Fact]                                              // 17. a moved due date is a NEW occurrence
		public async Task Moving_the_due_date_and_missing_the_new_one_is_a_legitimate_new_occurrence()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddDays(-4));
			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);

			var live = await f.Db.TaskItems.FirstAsync(x => x.ID == t.ID);
			live.DueDate = Now.AddHours(-1);
			await f.Db.SaveChangesAsync();

			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);
			Assert.Equal(2, f.OverdueNotifications(t.ID));   // the accepted model: a re-committed date can miss again
		}

		[Fact]
		public async Task A_completed_task_is_never_swept_however_late_it_was()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddDays(-30), status: "Done");

			var r = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);
			Assert.Equal(0, r.Examined);
			Assert.Equal(0, f.OverdueNotifications(t.ID));
		}

		[Fact]                                              // 7. company A ignores company B
		public async Task A_sweep_of_one_company_never_touches_another_companys_task()
		{
			using var f = new OverdueFixture();
			var a = f.AddEmployee(OverdueFixture.CompanyA, "a");
			var b = f.AddEmployee(OverdueFixture.CompanyB, "b");
			var ta = f.AddTask(OverdueFixture.CompanyA, a.ID, Now.AddHours(-4));
			var tb = f.AddTask(OverdueFixture.CompanyB, b.ID, Now.AddHours(-4));

			var r = await f.Overdue.SweepAsync(OverdueFixture.CompanyA);

			Assert.Equal(1, r.Examined);                    // only A's task was even looked at
			Assert.Equal(1, f.OverdueNotifications(ta.ID));
			Assert.Equal(0, f.OverdueNotifications(tb.ID));
			Assert.DoesNotContain(f.Notifications.Sent, n => n.RecipientEmployeeId == b.ID);
		}

		[Fact]
		public async Task An_unresolved_company_sweeps_nothing_and_does_not_sweep_all()
		{
			using var f = new OverdueFixture();
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			var t = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-4));

			var r = await f.Overdue.SweepAsync(0);

			Assert.Equal(0, r.Examined);
			Assert.Equal(0, f.OverdueNotifications(t.ID));
		}

		// =====================================================================================
		// THE FULL LIFECYCLE — overdue first, then escalation after the grace
		// =====================================================================================

		[Fact]                                              // 12 + 13. escalation after grace, still idempotent
		public async Task The_pipeline_notifies_the_assignee_first_and_the_manager_only_after_the_grace()
		{
			using var f = new OverdueFixture();
			var mgr = f.AddEmployee(OverdueFixture.CompanyA, "mgr");
			var emp = f.AddEmployee(OverdueFixture.CompanyA, "emp");
			f.Db.Hierarchicals.Add(new Hierarchical { H_Name = "m", H_Type = 5, H_ObjectID = mgr.ID, IsActive = true });
			f.Db.SaveChanges();
			var mgrNode = f.Db.Hierarchicals.Single(h => h.H_ObjectID == mgr.ID);
			f.Db.Hierarchicals.Add(new Hierarchical { H_Name = "e", H_Parent = mgrNode.H_ID, H_Type = 5, H_ObjectID = emp.ID, IsActive = true });
			f.Db.SaveChanges();

			// inside the grace: the assignee is told, the manager is NOT
			var fresh = f.AddTask(OverdueFixture.CompanyA, emp.ID, Now.AddHours(-2));
			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);
			Assert.Equal(0, (await f.Escalation.SweepAsync(OverdueFixture.CompanyA)).Escalated);
			Assert.Equal(1, f.OverdueNotifications(fresh.ID));

			// past the grace: the manager is told exactly once, and repeating changes nothing
			var live = await f.Db.TaskItems.FirstAsync(x => x.ID == fresh.ID);
			live.DueDate = Now.AddDays(-3);
			await f.Db.SaveChangesAsync();

			Assert.Equal(1, (await f.Overdue.SweepAsync(OverdueFixture.CompanyA)).Notified);   // new occurrence
			Assert.Equal(1, (await f.Escalation.SweepAsync(OverdueFixture.CompanyA)).Escalated);
			Assert.Equal(0, (await f.Escalation.SweepAsync(OverdueFixture.CompanyA)).Escalated);

			var escalated = f.Notifications.Sent.Where(n => n.Type == TaskNotificationKinds.Escalated).ToList();
			Assert.Single(escalated);
			Assert.Equal(mgr.ID, escalated[0].RecipientEmployeeId);
		}

		// =====================================================================================
		// WORKER SAFETY — mutation proofs. Each double THROWS if the guard under test is skipped.
		// =====================================================================================

		/// Throws on any use: proves a suppressed or gate-denied worker never reaches DI.
		private sealed class ThrowingScopeFactory : IServiceScopeFactory
		{
			public IServiceScope CreateScope() =>
				throw new InvalidOperationException("a suppressed or gate-denied worker must never create a scope");
		}

		private sealed class ThrowingGate : IWorkerGate
		{
			public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken) =>
				throw new InvalidOperationException("a suppressed worker must never reach the worker gate");
		}

		// 9 + 10. THE WORKER GATE IS HONOURED, AND HONOURED BEFORE ANY WORK.
		//
		// Stated plainly: this is an ORDERING proof read from source, not a runtime one. A runtime proof is not
		// available cheaply here — ExecuteAsync waits out a multi-minute startup delay before it reaches the
		// gate, so a cancelled token returns at the delay and would make a runtime test pass while proving
		// nothing about the gate at all. Rather than ship a test that looks stronger than it is, the guarantee
		// is asserted where it is actually decidable: the gate call must exist, and it must come BEFORE the
		// first scope creation, in every writing Task worker.
		//
		// Deleting the gate call, or moving it after the first CreateScope, fails this test.
		[Theory]
		[InlineData("TaskGeneratorHostedService.cs")]
		[InlineData("TaskScheduleMatchHostedService.cs")]
		public void A_writing_task_worker_consults_the_worker_gate_before_it_creates_any_scope(string file)
		{
			var src = Source("CrossBuy", "BL", file);

			int gate = src.IndexOf("WaitUntilAllowedAsync", StringComparison.Ordinal);
			int firstScope = src.IndexOf("CreateScope", StringComparison.Ordinal);

			Assert.True(gate >= 0, $"{file} must consult IWorkerGate — without it two instances duplicate its writes");
			Assert.True(firstScope >= 0, $"{file} is expected to create a scope");
			Assert.True(gate < firstScope,
				$"{file} must acquire the worker gate BEFORE creating a scope; gate at {gate}, first scope at {firstScope}");
		}

		[Fact]
		public void The_escalation_worker_also_gates_before_creating_a_scope()
		{
			var src = Source("CrossBuy", "BL", "TasksCalendar", "TaskEscalationHostedService.cs");
			Assert.True(src.IndexOf("WaitUntilAllowedAsync", StringComparison.Ordinal)
						< src.IndexOf("CreateScope", StringComparison.Ordinal));
		}

		[Fact]
		public void Suppression_is_checked_before_the_gate_and_before_any_scope()
		{
			// This ordering IS runtime-proven below (both collaborators throw); asserted here too so the
			// intent is explicit: a certification host must not even wait for the gate.
			foreach (var f in new[]
					 {
						 Source("CrossBuy", "BL", "TaskGeneratorHostedService.cs"),
						 Source("CrossBuy", "BL", "TaskScheduleMatchHostedService.cs"),
						 Source("CrossBuy", "BL", "TasksCalendar", "TaskEscalationHostedService.cs"),
					 })
			{
				int sup = f.IndexOf("BackgroundWritersSuppressed)", StringComparison.Ordinal);
				int gate = f.IndexOf("WaitUntilAllowedAsync", StringComparison.Ordinal);
				Assert.True(sup >= 0 && sup < gate, "certification suppression must precede the worker gate");
			}
		}

		[Fact]                                              // 11. certification suppression, all three workers
		public void Every_task_worker_suppresses_its_writes_in_a_certification_runtime()
		{
			var cert = new CertificationRuntimeState(certificationMode: true);
			var normal = new CertificationRuntimeState(certificationMode: false);

			// ThrowingGate + ThrowingScopeFactory: suppression must short-circuit BEFORE either is touched.
			Assert.True(new TaskGeneratorHostedService(new ThrowingScopeFactory(), new ThrowingGate(), cert,
				NullLogger<TaskGeneratorHostedService>.Instance).Suppressed);
			Assert.True(new TaskScheduleMatchHostedService(new ThrowingScopeFactory(), new ThrowingGate(), cert,
				NullLogger<TaskScheduleMatchHostedService>.Instance).Suppressed);
			Assert.True(new TaskEscalationHostedService(new ThrowingScopeFactory(), new ThrowingGate(), cert,
				NullLogger<TaskEscalationHostedService>.Instance).Suppressed);

			Assert.False(new TaskGeneratorHostedService(new ThrowingScopeFactory(), new ThrowingGate(), normal,
				NullLogger<TaskGeneratorHostedService>.Instance).Suppressed);
			Assert.False(new TaskScheduleMatchHostedService(new ThrowingScopeFactory(), new ThrowingGate(), normal,
				NullLogger<TaskScheduleMatchHostedService>.Instance).Suppressed);
			Assert.False(new TaskEscalationHostedService(new ThrowingScopeFactory(), new ThrowingGate(), normal,
				NullLogger<TaskEscalationHostedService>.Instance).Suppressed);
		}

		[Fact]
		public async Task A_certification_runtime_worker_returns_without_touching_the_gate_or_a_scope()
		{
			// The strongest form of the suppression assertion: both collaborators throw, so if suppression were
			// removed this test would fail with the double's message instead of passing.
			var worker = new TaskGeneratorHostedService(new ThrowingScopeFactory(), new ThrowingGate(),
				new CertificationRuntimeState(certificationMode: true),
				NullLogger<TaskGeneratorHostedService>.Instance);

			await worker.StartAsync(CancellationToken.None);
			await worker.StopAsync(CancellationToken.None);
			Assert.True(worker.Suppressed);
		}

		// =====================================================================================
		// COMPANY ENUMERATION
		// =====================================================================================

		private sealed class FixedCompanyScope : IWorkerCompanyScope
		{
			private readonly List<int> _ids;
			public FixedCompanyScope(params int[] ids) { _ids = ids.ToList(); }
			public Task<List<int>> EligibleCompanyIdsAsync(CancellationToken cancellationToken = default) =>
				Task.FromResult(_ids);
		}

		[Fact]                                              // 8. empty enumeration processes nothing
		public async Task An_empty_company_list_processes_nothing_and_never_falls_back_to_company_1()
		{
			int touched = 0;
			var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
				new FixedCompanyScope(), NullLogger.Instance, "test",
				_ => { touched++; return Task.CompletedTask; }, CancellationToken.None);

			Assert.Equal(0, processed);
			Assert.Equal(0, failed);
			Assert.Equal(0, touched);      // NOT one iteration for company 1
		}

		[Fact]
		public async Task Each_company_is_visited_once_and_one_failure_does_not_stop_the_others()
		{
			var visited = new List<int>();
			var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
				new FixedCompanyScope(1, 65, 7), NullLogger.Instance, "test",
				companyId =>
				{
					visited.Add(companyId);
					if (companyId == 65) throw new InvalidOperationException("company 65 fails");
					return Task.CompletedTask;
				}, CancellationToken.None);

			Assert.Equal(new[] { 1, 65, 7 }, visited);   // 7 still ran after 65 threw
			Assert.Equal(2, processed);
			Assert.Equal(1, failed);
		}

		// =====================================================================================
		// SOURCE INVARIANTS — the mutation proofs the brief asks for, in the form that is practical:
		// reinstating the removed anti-patterns makes these fail.
		// =====================================================================================

		private static string RepoRoot()
		{
			var d = new DirectoryInfo(AppContext.BaseDirectory);
			while (d != null && !File.Exists(Path.Combine(d.FullName, "CrossBuy.sln"))) d = d.Parent;
			Assert.NotNull(d);
			return d!.FullName;
		}

		private static string Source(params string[] parts) =>
			File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

		[Fact]
		public void No_task_worker_hardcodes_a_company_or_falls_back_to_company_1()
		{
			foreach (var w in new[]
					 {
						 Source("CrossBuy", "BL", "TaskGeneratorHostedService.cs"),
						 Source("CrossBuy", "BL", "TaskScheduleMatchHostedService.cs"),
						 Source("CrossBuy", "BL", "TasksCalendar", "TaskEscalationHostedService.cs"),
					 })
			{
				Assert.DoesNotContain("const int CompanyId = 1", w, StringComparison.Ordinal);
				Assert.DoesNotContain("DefaultCompanyId", w, StringComparison.Ordinal);

				// each one enumerates explicitly and binds the scope
				Assert.Contains("WorkerCompanyRunner.ForEachCompanyAsync", w, StringComparison.Ordinal);
				Assert.Contains("WorkerScope.ForCompany", w, StringComparison.Ordinal);
				Assert.Contains("WaitUntilAllowedAsync", w, StringComparison.Ordinal);
				Assert.Contains("BackgroundWritersSuppressed", w, StringComparison.Ordinal);
			}
		}

		[Fact]
		public void Overdue_detection_has_exactly_one_owner_and_it_is_the_task_generator()
		{
			var gen = Source("CrossBuy", "BL", "TaskGeneratorHostedService.cs");
			Assert.Contains("ITaskOverdueSweepService", gen, StringComparison.Ordinal);
			Assert.Contains("SweepAsync", gen, StringComparison.Ordinal);

			// and nothing else sweeps overdue — no second engine
			var escalation = Source("CrossBuy", "BL", "TasksCalendar", "TaskEscalationHostedService.cs");
			Assert.DoesNotContain("ITaskOverdueSweepService", escalation, StringComparison.Ordinal);

			var matcher = Source("CrossBuy", "BL", "TaskScheduleMatchHostedService.cs");
			Assert.DoesNotContain("ITaskOverdueSweepService", matcher, StringComparison.Ordinal);
		}

		[Fact]
		public void The_shared_program_file_gains_exactly_one_tasks_calendar_registration_call()
		{
			var program = Source("CrossBuy", "Program.cs");
			var calls = program.Split('\n').Count(l => l.Contains("AddTasksCalendarWorkers", StringComparison.Ordinal));
            Assert.Equal(1, calls);

			// the certification invariant the repository already asserts must survive: exactly ONE gated
			// AddHostedService, the event dispatcher. Task workers suppress inside themselves instead.
			var gated = program.Split('\n')
				.Where(l => l.Contains("AddHostedService", StringComparison.Ordinal)
							&& l.Contains("certificationRuntime", StringComparison.Ordinal))
				.ToList();
			Assert.Single(gated);
			Assert.Contains("BusinessEventDispatchWorker", gated[0], StringComparison.Ordinal);
		}

		[Fact]
		public void The_overdue_sweep_resolves_from_a_real_validated_container_through_the_registration()
		{
			// CLAUDE.md: a DI graph is not verified by services constructed by hand. This resolves the sweep
			// the way the worker does — from a container built with ValidateOnBuild and ValidateScopes.
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

			var conn = new SqliteConnection("DataSource=:memory:");
			conn.Open();
			var holder = new CompanyScopeHolder();
			holder.Set(OverdueFixture.CompanyA, null);
			services.AddScoped(_ => new CrossDbContext(
				new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(conn).Options, holder));
			services.AddScoped<INotificationService>(sp => new UatRecordingNotificationService(sp.GetRequiredService<CrossDbContext>()));
			services.AddScoped<ITaskNotificationService, TaskNotificationService>();
			services.AddScoped<IEntityRegistry, EntityRegistry>();
			services.AddScoped<IBusinessContextAccessor>(_ => StubContextAccessor.Unresolved());
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<ITaskCalendarEventPublisher, TaskCalendarEventPublisher>();

			// THE REGISTRATION UNDER TEST
			services.AddTasksCalendarWorkers();

			using var provider = services.BuildServiceProvider(new ServiceProviderOptions
			{
				ValidateOnBuild = true, ValidateScopes = true,
			});
			using var scope = provider.CreateScope();
			Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITaskOverdueSweepService>());

			conn.Dispose();
		}

		[Fact]
		public void Calling_the_registration_twice_cannot_produce_a_duplicate_sweep_registration()
		{
			var services = new ServiceCollection();
			services.AddTasksCalendarWorkers();
			services.AddTasksCalendarWorkers();
			Assert.Single(services.Where(d => d.ServiceType == typeof(ITaskOverdueSweepService)));
		}
	}
}
