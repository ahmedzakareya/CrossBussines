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
	// OVERDUE MANAGER ESCALATION
	//
	// The rule: a task still not Done a grace period after its due date escalates to the assignee's DIRECT
	// MANAGER. These tests drive the REAL services — OrgHierarchy for the manager, TaskNotificationService for
	// the bell, TaskCalendarEventPublisher over the real BusinessEventService for the durable fact — on
	// in-memory SQLite with real transactions. Nothing here is a stub except the notification sink, which
	// records AND persists so the service's own duplicate check runs for real.
	//
	// The org tree is the production shape: Hierarchical rows where H_Type == 5 means "an employee sits here"
	// and H_Parent links upward, with departments in between. That mix is the reason the resolver looks for the
	// nearest employee ANCESTOR rather than the immediate parent, and these tests build it that way on purpose.
	// ==========================================================================================
	internal sealed class EscalationFixture : IDisposable
	{
		public const int CompanyA = 1;
		public const int CompanyB = 65;

		private readonly SqliteConnection _conn;
		private readonly CompanyScopeHolder _holder = new();

		public CrossDbContext Db { get; }
		public UatRecordingNotificationService Notifications { get; }
		public ITaskEscalationService Escalation { get; }
		public IOrgHierarchy Hierarchy { get; }

		public EscalationFixture(bool contextResolvable = true, int graceHours = 24, int companyId = CompanyA)
		{
			_conn = new SqliteConnection("DataSource=:memory:");
			_conn.Open();
			_holder.Set(companyId, null);

			Db = NewContext();
			Db.Database.EnsureCreated();
			using (var cmd = _conn.CreateCommand())
			{
				cmd.CommandText = "PRAGMA foreign_keys = OFF;";
				cmd.ExecuteNonQuery();
			}

			Notifications = new UatRecordingNotificationService(Db);
			var notify = new TaskNotificationService(Db, Notifications);

			IBusinessContextAccessor accessor = contextResolvable
				? new StubContextAccessor(new BusinessContext
				{
					CompanyId = companyId, EmployeeId = 10, UserId = "u10",
					// System so the publisher's CompanyIdOverride is honoured, exactly as the sibling suites do.
					Source = BusinessContextSource.System,
				})
				: StubContextAccessor.Unresolved();

			var events = new BusinessEventService(Db, new EntityRegistry(Db), accessor,
				NullLogger<BusinessEventService>.Instance);
			var publisher = new TaskCalendarEventPublisher(events);

			Hierarchy = new OrgHierarchy(Db, NullLogger<OrgHierarchy>.Instance);

			var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Tasks:Escalation:GraceHours"] = graceHours.ToString(),
			}).Build();

			Escalation = new TaskEscalationService(Db, notify, publisher, Hierarchy, config, accessor);
		}

		public CrossDbContext NewContext()
		{
			var o = new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlite(_conn)
				.AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
				.Options;
			return new CrossDbContext(o, _holder);
		}

		// ---- arrangement helpers -------------------------------------------------------------------------
		private int _seq;

		public Employee AddEmployee(int companyId, string tag, bool active = true)
		{
			var e = new Employee
			{
				FirstName = "T", LastName = "T", FullName = "esc-" + tag, FullNameEn = "esc-" + tag,
				EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
				Email = $"esc-{tag}-{Guid.NewGuid():N}@example.com", ProfileImage = "", Gender = "M", MaritalStatus = "S",
				UserId = "user-esc-" + tag + "-" + Guid.NewGuid().ToString("N")[..8],
			};
			Db.Employee.Add(e);
			Db.SaveChanges();
			return e;
		}

		/// An org node. `employeeId` non-null makes it an EMPLOYEE node (H_Type 5); otherwise it is a
		/// department, which is what forces the resolver to keep climbing.
		public Hierarchical AddNode(int? parentNodeId, int? employeeId, string name)
		{
			var n = new Hierarchical
			{
				H_Name = name, H_NameEn = name, H_Parent = parentNodeId,
				H_Type = employeeId.HasValue ? 5 : 1, H_ObjectID = employeeId, IsActive = true, Sort = ++_seq,
			};
			Db.Hierarchicals.Add(n);
			Db.SaveChanges();
			return n;
		}

		public TaskItem AddTask(int companyId, int assigneeId, DateTime? dueUtc, string status = "New", string title = "esc task")
		{
			var t = new TaskItem
			{
				CompanyId = companyId, Title = title, AssigneeEmployeeId = assigneeId,
				CreatedByEmployeeId = assigneeId, Status = status, Priority = "Normal",
				DueDate = dueUtc, CreatedAt = DateTime.UtcNow,
			};
			Db.TaskItems.Add(t);
			Db.SaveChanges();
			return t;
		}

		public int EscalationNotifications(int taskId) =>
			Db.Notifications.AsNoTracking().Count(n =>
				n.Type == TaskNotificationKinds.Escalated && n.EntityId == taskId);

		public List<Models.Context.Platform.BusinessEvent> EscalationEvents(int taskId) =>
			Db.BusinessEvents.AsNoTracking()
				.Where(e => e.EventType == "Task.Escalated" && e.EntityId == taskId)
				.ToList();

		public void Dispose() { Db.Dispose(); _conn.Dispose(); }
	}

	public class TaskEscalationRuleTests
	{
		private static DateTime Now => TaskCalendarTime.UtcNow();

		/// The canonical arrangement: employee under a department under their manager, so the manager is an
		/// ANCESTOR and not the immediate parent.
		private static (EscalationFixture f, int assignee, int manager) Arrange(
			bool contextResolvable = true, int graceHours = 24)
		{
			var f = new EscalationFixture(contextResolvable, graceHours);
			var mgr = f.AddEmployee(EscalationFixture.CompanyA, "mgr");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");

			var mgrNode = f.AddNode(null, mgr.ID, "manager");
			var dept = f.AddNode(mgrNode.H_ID, null, "department");
			f.AddNode(dept.H_ID, emp.ID, "employee");

			return (f, emp.ID, mgr.ID);
		}

		// ---- 1. not overdue => no escalation ----------------------------------------------------------
		[Fact]
		public async Task A_task_that_is_not_yet_due_is_never_escalated()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(3));

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(0, r.Examined);
			Assert.Equal(0, r.Escalated);
			Assert.Equal(0, f.EscalationNotifications(t.ID));
			Assert.Empty(f.EscalationEvents(t.ID));

			var state = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);
			Assert.Equal(TaskEscalationState.NotDue, state.State);
		}

		[Fact]
		public async Task A_task_overdue_but_still_inside_the_grace_window_is_Overdue_not_escalated()
		{
			var (f, emp, _) = Arrange(graceHours: 24);
			using var _f = f;
			// two hours late, grace is 24 — real overdue, but not yet the manager's problem
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-2));

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Equal(0, r.Examined);
			Assert.Equal(0, f.EscalationNotifications(t.ID));

			var state = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);
			Assert.Equal(TaskEscalationState.Overdue, state.State);
			Assert.True(state.OverdueHours >= 1);
		}

		// ---- 2. overdue incomplete => escalation ------------------------------------------------------
		[Fact]
		public async Task An_overdue_incomplete_task_escalates_to_the_direct_manager()
		{
			var (f, emp, mgr) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-3));

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, r.Examined);
			Assert.Equal(1, r.Escalated);
			Assert.Equal(0, r.NoManager);

			// ---- 8. the notification is correct ----
			var sent = f.Notifications.Sent.Where(n => n.Type == TaskNotificationKinds.Escalated).ToList();
			Assert.Single(sent);
			Assert.Equal(mgr, sent[0].RecipientEmployeeId);          // 5. the DIRECT MANAGER, not the assignee
			Assert.Equal(EscalationFixture.CompanyA, sent[0].CompanyId);
			Assert.Equal(TaskCalendarEntityCodes.Task, sent[0].EntityType);
			Assert.Equal(t.ID, sent[0].EntityId);

			// ---- 10. the deep link is correct ----
			Assert.Equal($"/Tasks/Index?taskId={t.ID}", sent[0].Url);

			// the assignee is NOT told again by escalation — they already had task_became_overdue for this date
			Assert.DoesNotContain(sent, n => n.RecipientEmployeeId == emp);
		}

		// ---- 9. the BusinessEvent is correct ----------------------------------------------------------
		[Fact]
		public async Task Escalation_records_a_Task_Escalated_business_event_with_the_right_context()
		{
			var (f, emp, mgr) = Arrange();
			using var _f = f;
			var due = Now.AddDays(-2);
			var t = f.AddTask(EscalationFixture.CompanyA, emp, due);

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Equal(1, r.EventsRecorded);
			Assert.Equal(0, r.EventsSkippedNoContext);

			var events = f.EscalationEvents(t.ID);
			Assert.Single(events);
			var e = events[0];
			Assert.Equal("Task.Escalated", e.EventType);
			Assert.Equal(TaskCalendarEntityCodes.Task, e.EntityType);
			Assert.Equal(t.ID, e.EntityId);
			Assert.Equal(EscalationFixture.CompanyA, e.CompanyID);

			// the payload names the manager and the missed date — the two facts that make it actionable
			Assert.Contains("managerEmployeeId", e.Payload ?? "");
			Assert.Contains(mgr.ToString(), e.Payload ?? "");
			Assert.Contains("newDueAt", e.Payload ?? "");
		}

		[Fact]
		public async Task Without_a_resolvable_BusinessContext_the_manager_is_still_told_and_the_gap_is_counted()
		{
			// The overdue sweep's hard-won lesson: an event that cannot be attributed must never cost the
			// notification. The gap is COUNTED, not swallowed.
			var (f, emp, mgr) = Arrange(contextResolvable: false);
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-2));

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, r.Escalated);
			Assert.Equal(0, r.EventsRecorded);
			Assert.Equal(1, r.EventsSkippedNoContext);
			Assert.Equal(1, f.EscalationNotifications(t.ID));
			Assert.Empty(f.EscalationEvents(t.ID));
		}

		// ---- 3. completed => no escalation ------------------------------------------------------------
		[Fact]
		public async Task A_completed_task_is_never_escalated_however_late_it_was()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-30), status: "Done");

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(0, r.Examined);
			Assert.Equal(0, f.EscalationNotifications(t.ID));
			Assert.Empty(f.EscalationEvents(t.ID));
			var state = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);
			Assert.Equal(TaskEscalationState.NotDue, state.State);
		}

		// ---- 4. the same occurrence never escalates twice ---------------------------------------------
		[Fact]
		public async Task The_same_missed_due_date_escalates_once_however_often_the_sweep_runs()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-5));

			var first = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			var second = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			var third = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, first.Escalated);
			Assert.Equal(0, second.Escalated);
			Assert.Equal(0, third.Escalated);
			Assert.Equal(1, second.AlreadyEscalated);
			Assert.Equal(1, third.AlreadyEscalated);

			Assert.Equal(1, f.EscalationNotifications(t.ID));      // one bell, not three
			Assert.Single(f.EscalationEvents(t.ID));               // one durable fact, not three
		}

		// ---- 13. no duplicate escalation after a restart ----------------------------------------------
		[Fact]
		public async Task A_restart_does_not_re_escalate_because_the_evidence_is_in_the_database()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-4));

			Assert.Equal(1, (await f.Escalation.SweepAsync(EscalationFixture.CompanyA)).Escalated);

			// A NEW service instance over the SAME database is what a process restart looks like: nothing is
			// remembered in memory, so the only thing that can prevent a second escalation is the stored
			// evidence. That is precisely why no in-memory flag was used.
			var restarted = new TaskEscalationService(
				f.Db,
				new TaskNotificationService(f.Db, f.Notifications),
				new TaskCalendarEventPublisher(new BusinessEventService(f.Db, new EntityRegistry(f.Db),
					new StubContextAccessor(new BusinessContext
					{
						CompanyId = EscalationFixture.CompanyA, EmployeeId = 10, UserId = "u10",
						Source = BusinessContextSource.System,
					}), NullLogger<BusinessEventService>.Instance)),
				f.Hierarchy,
				new ConfigurationBuilder().AddInMemoryCollection(
					new Dictionary<string, string?> { ["Tasks:Escalation:GraceHours"] = "24" }).Build(),
				new StubContextAccessor(new BusinessContext
				{
					CompanyId = EscalationFixture.CompanyA, EmployeeId = 10, UserId = "u10",
					Source = BusinessContextSource.System,
				}));

			var after = await restarted.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(0, after.Escalated);
			Assert.Equal(1, after.AlreadyEscalated);
			Assert.Equal(1, f.EscalationNotifications(t.ID));
		}

		[Fact]
		public async Task Moving_the_due_date_and_missing_it_again_is_a_new_occurrence_that_escalates_again()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-5));
			Assert.Equal(1, (await f.Escalation.SweepAsync(EscalationFixture.CompanyA)).Escalated);

			// a re-committed deadline, missed again — a genuinely different fact, and it must reach the manager
			var live = await f.Db.TaskItems.FirstAsync(x => x.ID == t.ID);
			live.DueDate = Now.AddDays(-2);
			await f.Db.SaveChangesAsync();

			var again = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Equal(1, again.Escalated);
			Assert.Equal(2, f.EscalationNotifications(t.ID));
		}

		// ---- 5/6. manager resolution and the company boundary -----------------------------------------
		[Fact]
		public async Task The_direct_manager_is_the_nearest_employee_ancestor_not_the_immediate_parent()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var top = f.AddEmployee(EscalationFixture.CompanyA, "top");
			var mid = f.AddEmployee(EscalationFixture.CompanyA, "mid");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");

			var topNode = f.AddNode(null, top.ID, "top");
			var division = f.AddNode(topNode.H_ID, null, "division");
			var midNode = f.AddNode(division.H_ID, mid.ID, "mid");
			var dept = f.AddNode(midNode.H_ID, null, "department");
			f.AddNode(dept.H_ID, emp.ID, "emp");

			var manager = await f.Hierarchy.DirectManagerAsync(EscalationFixture.CompanyA, emp.ID);

			Assert.Equal(mid.ID, manager);       // the nearest EMPLOYEE ancestor
			Assert.NotEqual(top.ID, manager);    // not the one above them
		}

		[Fact]
		public async Task A_manager_belonging_to_another_company_is_refused_and_nothing_is_escalated()
		{
			var f = new EscalationFixture();
			using var _f = f;
			// The org tree carries no CompanyID, so it CAN span tenants. This is that data condition.
			var foreignMgr = f.AddEmployee(EscalationFixture.CompanyB, "foreign-mgr");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");

			var mgrNode = f.AddNode(null, foreignMgr.ID, "foreign manager");
			var dept = f.AddNode(mgrNode.H_ID, null, "department");
			f.AddNode(dept.H_ID, emp.ID, "employee");

			Assert.Null(await f.Hierarchy.DirectManagerAsync(EscalationFixture.CompanyA, emp.ID));

			var t = f.AddTask(EscalationFixture.CompanyA, emp.ID, Now.AddDays(-4));
			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, r.Examined);
			Assert.Equal(0, r.Escalated);
			Assert.Equal(1, r.NoManager);                          // honest unresolved state
			Assert.Equal(0, f.EscalationNotifications(t.ID));      // and NOTHING was sent to anyone
			Assert.Empty(f.EscalationEvents(t.ID));
			Assert.Empty(f.Notifications.Sent.Where(n => n.RecipientEmployeeId == foreignMgr.ID));
		}

		// ---- 7. no manager => safe behaviour ----------------------------------------------------------
		[Fact]
		public async Task An_employee_with_no_manager_is_counted_and_nobody_is_substituted()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "orphan");
			var admin = f.AddEmployee(EscalationFixture.CompanyA, "admin");   // exists, and must NOT be picked
			f.AddNode(null, emp.ID, "root employee");                          // at the top: nobody above

			var t = f.AddTask(EscalationFixture.CompanyA, emp.ID, Now.AddDays(-6));
			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, r.NoManager);
			Assert.Equal(0, r.Escalated);
			Assert.Empty(f.Notifications.Sent.Where(n => n.Type == TaskNotificationKinds.Escalated));
			Assert.DoesNotContain(f.Notifications.Sent, n => n.RecipientEmployeeId == admin.ID);

			var state = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);
			Assert.Equal(TaskEscalationState.EscalationPending, state.State);   // eligible, but unresolvable
		}

		[Fact]
		public async Task An_employee_not_placed_in_the_org_tree_resolves_no_manager()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "unplaced");
			Assert.Null(await f.Hierarchy.DirectManagerAsync(EscalationFixture.CompanyA, emp.ID));
		}

		[Fact]
		public async Task An_inactive_direct_manager_is_refused_rather_than_skipped_over()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var inactive = f.AddEmployee(EscalationFixture.CompanyA, "inactive-mgr", active: false);
			var above = f.AddEmployee(EscalationFixture.CompanyA, "above");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");

			var aboveNode = f.AddNode(null, above.ID, "above");
			var mgrNode = f.AddNode(aboveNode.H_ID, inactive.ID, "inactive manager");
			f.AddNode(mgrNode.H_ID, emp.ID, "emp");

			// Deliberately NOT "climb until somebody matches": the nearest employee ancestor IS the direct
			// manager, and escalating past them to their boss would notify somebody who does not manage them.
			var resolved = await f.Hierarchy.DirectManagerAsync(EscalationFixture.CompanyA, emp.ID);
			Assert.Null(resolved);
			Assert.NotEqual(above.ID, resolved);
		}

		// ---- 12. company isolation --------------------------------------------------------------------
		[Fact]
		public async Task A_sweep_of_one_company_never_escalates_another_companys_task()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var mgrA = f.AddEmployee(EscalationFixture.CompanyA, "mgrA");
			var empA = f.AddEmployee(EscalationFixture.CompanyA, "empA");
			var nA = f.AddNode(null, mgrA.ID, "mgrA");
			f.AddNode(nA.H_ID, empA.ID, "empA");

			var mgrB = f.AddEmployee(EscalationFixture.CompanyB, "mgrB");
			var empB = f.AddEmployee(EscalationFixture.CompanyB, "empB");
			var nB = f.AddNode(null, mgrB.ID, "mgrB");
			f.AddNode(nB.H_ID, empB.ID, "empB");

			var tA = f.AddTask(EscalationFixture.CompanyA, empA.ID, Now.AddDays(-4));
			var tB = f.AddTask(EscalationFixture.CompanyB, empB.ID, Now.AddDays(-4));

			var r = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			Assert.Equal(1, r.Examined);                       // only company A's task was even looked at
			Assert.Equal(1, r.Escalated);
			Assert.Equal(1, f.EscalationNotifications(tA.ID));
			Assert.Equal(0, f.EscalationNotifications(tB.ID));  // company B untouched
			Assert.DoesNotContain(f.Notifications.Sent, n => n.RecipientEmployeeId == mgrB.ID);
		}

		[Fact]
		public async Task An_unresolved_company_escalates_nothing_and_does_not_escalate_all()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var mgr = f.AddEmployee(EscalationFixture.CompanyA, "mgr");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");
			var n = f.AddNode(null, mgr.ID, "mgr");
			f.AddNode(n.H_ID, emp.ID, "emp");
			var t = f.AddTask(EscalationFixture.CompanyA, emp.ID, Now.AddDays(-4));

			var r = await f.Escalation.SweepAsync(0);

			Assert.Equal(0, r.Examined);
			Assert.Equal(0, r.Escalated);
			Assert.Equal(0, f.EscalationNotifications(t.ID));
		}

		[Fact]
		public async Task Evaluate_refuses_to_describe_a_task_belonging_to_another_company()
		{
			var f = new EscalationFixture();
			using var _f = f;
			var emp = f.AddEmployee(EscalationFixture.CompanyB, "empB");
			var t = f.AddTask(EscalationFixture.CompanyB, emp.ID, Now.AddDays(-9));

			// asked as company A about a company B task
			var many = await f.Escalation.EvaluateManyAsync(EscalationFixture.CompanyA, new[] { t.ID });
			Assert.Empty(many);

			var one = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);
			Assert.Equal(TaskEscalationState.NotDue, one.State);   // nothing is disclosed, not even "overdue"
		}

		// ---- the derived states the UI renders --------------------------------------------------------
		[Fact]
		public async Task The_escalated_state_carries_the_manager_and_the_time_for_the_screen()
		{
			var (f, emp, mgr) = Arrange();
			using var _f = f;
			var t = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-3));
			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			var s = await f.Escalation.EvaluateAsync(EscalationFixture.CompanyA, t.ID);

			Assert.Equal(TaskEscalationState.Escalated, s.State);
			Assert.True(s.IsEscalated);
			Assert.Equal(mgr, s.ManagerEmployeeId);
			Assert.False(string.IsNullOrWhiteSpace(s.ManagerName));
			Assert.NotNull(s.EscalatedAtUtc);
			Assert.True(s.OverdueHours >= 48);
		}

		[Fact]
		public async Task EvaluateMany_answers_for_a_board_in_one_call_with_per_task_states()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;
			var notDue = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(2), title: "not due");
			var overdue = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-2), title: "overdue");
			var pending = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-3), title: "pending");
			var done = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddDays(-3), status: "Done", title: "done");

			var before = await f.Escalation.EvaluateManyAsync(EscalationFixture.CompanyA,
				new[] { notDue.ID, overdue.ID, pending.ID, done.ID });

			Assert.Equal(TaskEscalationState.NotDue, before[notDue.ID].State);
			Assert.Equal(TaskEscalationState.Overdue, before[overdue.ID].State);
			Assert.Equal(TaskEscalationState.EscalationPending, before[pending.ID].State);
			Assert.Equal(TaskEscalationState.NotDue, before[done.ID].State);

			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			var after = await f.Escalation.EvaluateManyAsync(EscalationFixture.CompanyA,
				new[] { notDue.ID, overdue.ID, pending.ID, done.ID });
			Assert.Equal(TaskEscalationState.Escalated, after[pending.ID].State);
			Assert.Equal(TaskEscalationState.Overdue, after[overdue.ID].State);      // still inside grace
			Assert.Equal(TaskEscalationState.NotDue, after[done.ID].State);
		}

		// ---- 11. certification suppression ------------------------------------------------------------
		[Fact]
		public void In_a_certification_runtime_the_escalation_worker_suppresses_itself()
		{
			// A conformance capture must OBSERVE stable data. The worker asks the platform's own flag rather
			// than being left out of the DI graph, so the suppression holds however it is composed.
			var suppressed = new TaskEscalationHostedService(
				new DummyScopeFactory(), new DummyGate(),
				new CertificationRuntimeState(certificationMode: true),
				NullLogger<TaskEscalationHostedService>.Instance);
			Assert.True(suppressed.Suppressed);

			var normal = new TaskEscalationHostedService(
				new DummyScopeFactory(), new DummyGate(),
				new CertificationRuntimeState(certificationMode: false),
				NullLogger<TaskEscalationHostedService>.Instance);
			Assert.False(normal.Suppressed);
		}

		[Fact]
		public void The_certification_flag_is_the_platforms_own_and_needs_all_three_conditions()
		{
			// Re-asserted here because the worker's suppression is only as trustworthy as this flag: it must
			// not be possible to disable escalation on a normal host by setting one variable.
			Assert.True(new CertificationRuntimeState(true).BackgroundWritersSuppressed);
			Assert.False(new CertificationRuntimeState(false).BackgroundWritersSuppressed);
		}

		// ---- the DI graph, built for real -------------------------------------------------------------
		//
		// CLAUDE.md records the lesson the hard way: "A DI graph is not verified by unit tests that construct
		// services by hand. 112 green tests coexisted with an application that could not boot." Every test above
		// constructs by hand, so this one does the opposite — a real container with ValidateOnBuild and
		// ValidateScopes, which is what catches a singleton that captured a scoped service.
		//
		// TaskEscalationHostedService is a SINGLETON (AddHostedService), so it may only take singletons:
		// IServiceScopeFactory, IWorkerGate, CertificationRuntimeState and a logger. It creates its own scope
		// per company and resolves ITaskEscalationService inside it. If somebody later injects the scoped
		// escalation service straight into the constructor, ValidateScopes fails HERE rather than at boot.
		[Fact]
		public void The_escalation_worker_and_service_compose_in_a_real_validated_container()
		{
			var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
			services.AddLogging();
			services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

			// the singletons the worker is allowed to take
			services.AddSingleton<IWorkerGate, DummyGate>();
			services.AddSingleton(new CertificationRuntimeState(certificationMode: false));

			// the scoped graph the escalation service needs
			var conn = new SqliteConnection("DataSource=:memory:");
			conn.Open();
			var holder = new CompanyScopeHolder();
			holder.Set(EscalationFixture.CompanyA, null);
			services.AddScoped(_ => new CrossDbContext(
				new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(conn).Options, holder));
			services.AddScoped<INotificationService>(sp =>
				new UatRecordingNotificationService(sp.GetRequiredService<CrossDbContext>()));
			services.AddScoped<ITaskNotificationService, TaskNotificationService>();
			services.AddScoped<IEntityRegistry, EntityRegistry>();
			services.AddScoped<IBusinessContextAccessor>(_ => StubContextAccessor.Unresolved());
            services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<ITaskCalendarEventPublisher, TaskCalendarEventPublisher>();
			services.AddScoped<IOrgHierarchy, OrgHierarchy>();
			services.AddScoped<ITaskEscalationService, TaskEscalationService>();

			// the worker itself, exactly as AddHostedService registers it
			services.AddSingleton<TaskEscalationHostedService>();

			using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			});

			// the singleton resolves from the ROOT — which is what proves it captured nothing scoped
			var worker = provider.GetRequiredService<TaskEscalationHostedService>();
			Assert.NotNull(worker);
			Assert.False(worker.Suppressed);

			// and the scoped service resolves inside a scope, the way the worker asks for it
			using var scope = provider.CreateScope();
			Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITaskEscalationService>());

			conn.Dispose();
		}

		private sealed class DummyScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
		{
			public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
				throw new InvalidOperationException("a suppressed worker must never create a scope");
		}

		private sealed class DummyGate : IWorkerGate
		{
			public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken) =>
				throw new InvalidOperationException("a suppressed worker must never reach the worker gate");
		}
	}
}
