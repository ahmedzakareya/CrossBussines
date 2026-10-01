using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// TWO COMPANIES, TWO SCOPES, ONE DATABASE.
	//
	// EscalationFixture pins ONE CompanyScopeHolder, which is correct for almost every escalation test
	// and makes a two-company one impossible: CompanyWriteGuardInterceptor refuses the second company's
	// writes, and CompanyScopeHolder.Set refuses to be re-pointed - "Two companies must never share one
	// DI scope or DbContext instance - start a new scope instead."
	//
	// So this fixture does exactly that. One SQLite connection (one database), and per company its own
	// holder, its own DbContext and its own service graph - which is the shape a real request has. The
	// guard stays armed throughout; nothing here is bypassed to make a test pass.
	// ==========================================================================================
	internal sealed class TwoCompanyEscalationFixture : IDisposable
	{
		public const int CompanyA = 1;
		public const int CompanyB = 65;

		private readonly SqliteConnection _conn;
		private readonly Dictionary<int, Scope> _scopes = new();
		private int _seq;

		private sealed class Scope
		{
			public required CrossDbContext Db { get; init; }
			public required ITaskEscalationService Escalation { get; init; }
		}

		public TwoCompanyEscalationFixture()
		{
			_conn = new SqliteConnection("DataSource=:memory:");
			_conn.Open();

			// Created once, through the first scope; the database outlives every context because the
			// connection does.
			using (var seed = ContextFor(CompanyA))
			{
				seed.Database.EnsureCreated();
			}
			using (var cmd = _conn.CreateCommand())
			{
				cmd.CommandText = "PRAGMA foreign_keys = OFF;";
				cmd.ExecuteNonQuery();
			}
		}

		private CrossDbContext ContextFor(int companyId)
		{
			var holder = new CompanyScopeHolder();
			holder.Set(companyId, null);
			var options = new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlite(_conn)
				.AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
				.Options;
			return new CrossDbContext(options, holder);
		}

		private Scope ScopeFor(int companyId)
		{
			if (_scopes.TryGetValue(companyId, out var existing)) return existing;

			var db = ContextFor(companyId);
			var notify = new TaskNotificationService(db, new UatRecordingNotificationService(db));

			// System source so the publisher's CompanyIdOverride is honoured, exactly as the sibling suite
			// does - and named as THIS company, so the guard and the event agree.
			var accessor = new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 10, UserId = "u10",
				Source = BusinessContextSource.System,
			});
			var events = new BusinessEventService(db, new EntityRegistry(db), accessor,
				NullLogger<BusinessEventService>.Instance);

			var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Tasks:Escalation:GraceHours"] = "24",
			}).Build();

			var scope = new Scope
			{
				Db = db,
				Escalation = new TaskEscalationService(db, notify, new TaskCalendarEventPublisher(events),
					new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance), config, accessor),
			};
			_scopes[companyId] = scope;
			return scope;
		}

		public ITaskEscalationService EscalationFor(int companyId) => ScopeFor(companyId).Escalation;

		/// An employee, a manager above them in the org tree, and a task overdue well past the grace -
		/// everything the sweep needs, written through the company's OWN scope.
		public int ArrangeEscalatableTask(int companyId)
		{
			var db = ScopeFor(companyId).Db;

			var mgr = AddEmployee(db, companyId, "mgr");
			var emp = AddEmployee(db, companyId, "emp");

			var mgrNode = AddNode(db, null, mgr.ID, "manager");
			var dept = AddNode(db, mgrNode.H_ID, null, "department");
			AddNode(db, dept.H_ID, emp.ID, "employee");

			var task = new TaskItem
			{
				CompanyId = companyId, Title = "esc task", AssigneeEmployeeId = emp.ID,
				CreatedByEmployeeId = emp.ID, Status = "New", Priority = "Normal",
				DueDate = TaskCalendarTime.UtcNow().AddHours(-48), CreatedAt = DateTime.UtcNow,
			};
			db.TaskItems.Add(task);
			db.SaveChanges();
			return task.ID;
		}

		private Employee AddEmployee(CrossDbContext db, int companyId, string tag)
		{
			var e = new Employee
			{
				FirstName = "T", LastName = "T", FullName = $"two-{companyId}-{tag}",
				FullNameEn = $"two-{companyId}-{tag}", EmpCompanyID = companyId, IsActive = true,
				Address = "-", PhoneNumber = "-", Email = $"two-{Guid.NewGuid():N}@example.com",
				ProfileImage = "", Gender = "M", MaritalStatus = "S",
				UserId = "user-two-" + Guid.NewGuid().ToString("N")[..8],
			};
			db.Employee.Add(e);
			db.SaveChanges();
			return e;
		}

		private Hierarchical AddNode(CrossDbContext db, int? parentNodeId, int? employeeId, string name)
		{
			var n = new Hierarchical
			{
				H_Name = name, H_NameEn = name, H_Parent = parentNodeId,
				H_Type = employeeId.HasValue ? 5 : 1, H_ObjectID = employeeId, IsActive = true, Sort = ++_seq,
			};
			db.Hierarchicals.Add(n);
			db.SaveChanges();
			return n;
		}

		/// Counted through the OWNING COMPANY'S scope, because Notification IS one of the globally filtered
		/// entities - the only pilot one with a nullable CompanyID, filtered by NotificationCompanyPolicy.
		/// A first version of this helper read every count through company A and reported 0 for company B's
		/// notification, which is the global filter working exactly as designed.
		public int EscalationNotifications(int companyId, int taskId) =>
			ScopeFor(companyId).Db.Notifications.AsNoTracking()
				.Count(n => n.Type == TaskNotificationKinds.Escalated && n.EntityId == taskId);

		public void Dispose()
		{
			foreach (var scope in _scopes.Values) scope.Db.Dispose();
			_conn.Dispose();
		}
	}

	// ==========================================================================================
	// CurrentlyEscalatedTaskIdsAsync - THE SET-SHAPED ANSWER.
	//
	// WHAT THIS METHOD IS, AND WHY IT NEEDED NO NEW BEHAVIOUR. Escalation is already a capability of
	// this line: the sweep decides who is late, resolves the direct manager, records Task.Escalated and
	// notifies. The only thing missing was a way to ask "WHICH tasks are escalated" without first
	// knowing the candidate ids - EvaluateManyAsync needs the list you are trying to obtain. So this is
	// a read over the witness the sweep already writes and already reads, and it escalates nothing.
	//
	// EVERY TEST BELOW DRIVES THE REAL SWEEP FIRST. Nothing hand-writes a notification row: if the
	// evidence shape ever drifts from what the sweep produces, these fail, which a fabricated row would
	// hide. That is the whole reason the arrangement is expensive.
	//
	// ONE WITNESS ON THIS LINE. The programme line also reads a durable projection on TaskItem
	// (EscalationLevel / EscalatedAtUtc / EscalatedForDueUtc). Those columns do not exist here and
	// Stage4TaskOrchestrationEngineTests asserts their absence, so the notification is the whole record
	// available - and the non-vacuity assertions below prove it is a real one.
	// ==========================================================================================
	public class TaskEscalationSetQueryTests
	{
		private static DateTime Now => TaskCalendarTime.UtcNow();

		/// The canonical arrangement, matching the sibling suite: employee under a department under their
		/// manager, so the manager is an ANCESTOR rather than the immediate parent.
		private static (EscalationFixture f, int assignee, int manager) Arrange(int graceHours = 24)
		{
			var f = new EscalationFixture(graceHours: graceHours);
			var mgr = f.AddEmployee(EscalationFixture.CompanyA, "mgr");
			var emp = f.AddEmployee(EscalationFixture.CompanyA, "emp");

			var mgrNode = f.AddNode(null, mgr.ID, "manager");
			var dept = f.AddNode(mgrNode.H_ID, null, "department");
			f.AddNode(dept.H_ID, emp.ID, "employee");

			return (f, emp.ID, mgr.ID);
		}

		// ---- 11. a currently escalated task is included ------------------------------------------------
		[Fact]
		public async Task An_escalated_task_is_returned_and_a_merely_overdue_one_is_not()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			// PAST the grace: this one escalates.
			var escalated = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-48));
			// INSIDE the grace: overdue, but nobody has been told, so it is not escalated.
			var stillInGrace = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-2));

			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			var ids = await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA);

			Assert.Contains(escalated.ID, ids);
			Assert.DoesNotContain(stillInGrace.ID, ids);

			// NON-VACUITY: the sweep really did write the witness this method reads.
			Assert.Equal(1, f.EscalationNotifications(escalated.ID));
			Assert.Equal(0, f.EscalationNotifications(stillInGrace.ID));
		}

		// ---- 12. a task nobody escalated is excluded ---------------------------------------------------
		[Fact]
		public async Task A_task_that_was_never_escalated_is_excluded_even_when_long_overdue()
		{
			var f = new EscalationFixture();
			using var _f = f;

			// An assignee with NO manager node: the sweep finds nobody to tell, so no evidence is written.
			var orphan = f.AddEmployee(EscalationFixture.CompanyA, "orphan");
			var task = f.AddTask(EscalationFixture.CompanyA, orphan.ID, Now.AddHours(-500));

			var result = await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			// The sweep SAW it and declined - that is what makes this a real negative rather than an
			// arrangement that simply never ran.
			Assert.Equal(1, result.Examined);
			Assert.Equal(1, result.NoManager);
			Assert.Equal(0, f.EscalationNotifications(task.ID));

			Assert.DoesNotContain(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));
		}

		// ---- 13. evidence goes stale ------------------------------------------------------------------
		[Fact]
		public async Task Moving_the_deadline_makes_the_escalation_stale_and_drops_the_task()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			var task = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-48));
			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Contains(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));

			// THE DEADLINE MOVES. The old notification is about a date the task no longer has, so nobody
			// has been told about the CURRENT date - and the answer must say so.
			var tracked = f.Db.TaskItems.Single(t => t.ID == task.ID);
			tracked.DueDate = Now.AddHours(-1);
			f.Db.SaveChanges();

			Assert.DoesNotContain(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));

			// ...and the row is still there, so the exclusion is the STAMP rule and not a deleted witness.
			Assert.Equal(1, f.EscalationNotifications(task.ID));
		}

		// ---- a completed task is never escalated, however loud its history -----------------------------
		[Fact]
		public async Task Completing_an_escalated_task_removes_it_from_the_set()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			var task = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-48));
			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Contains(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));

			var tracked = f.Db.TaskItems.Single(t => t.ID == task.ID);
			tracked.Status = "Done";
			f.Db.SaveChanges();

			Assert.DoesNotContain(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));
			Assert.Equal(1, f.EscalationNotifications(task.ID));
		}

		// ---- 14. company isolation, ASYMMETRIC, TWO REAL SCOPES ------------------------------------------
		//
		// TWO SCOPES, NOT ONE, AND THAT IS THE PLATFORM'S OWN RULE. A first attempt swept both companies
		// through one fixture and was refused by CompanyWriteGuardInterceptor - "this scope operates as
		// company 1 but the new BusinessEvent names company 65". CompanyScopeHolder.Set says the same thing
		// out loud: "Two companies must never share one DI scope or DbContext instance - start a new scope
		// instead." So the test starts a second scope over the SAME database rather than bending the guard,
		// and both companies escalate through the real sweep.
		//
		// THIS IS WHAT MAKES THE ASSERTION NON-VACUOUS. With evidence in BOTH companies, dropping EITHER
		// company predicate - the one on Notifications or the one on TaskItems - changes an answer. With
		// evidence in only one, both mutants survive because the other side is empty either way.
		[Fact]
		public async Task A_task_escalated_in_another_company_is_never_returned()
		{
			using var two = new TwoCompanyEscalationFixture();

			var mine = two.ArrangeEscalatableTask(TwoCompanyEscalationFixture.CompanyA);
			var theirs = two.ArrangeEscalatableTask(TwoCompanyEscalationFixture.CompanyB);

			// Each company's OWN scope sweeps its OWN company. Neither write crosses a tenant.
			await two.EscalationFor(TwoCompanyEscalationFixture.CompanyA).SweepAsync(TwoCompanyEscalationFixture.CompanyA);
			await two.EscalationFor(TwoCompanyEscalationFixture.CompanyB).SweepAsync(TwoCompanyEscalationFixture.CompanyB);

			// NON-VACUITY: both companies genuinely escalated.
			Assert.Equal(1, two.EscalationNotifications(TwoCompanyEscalationFixture.CompanyA, mine));
			Assert.Equal(1, two.EscalationNotifications(TwoCompanyEscalationFixture.CompanyB, theirs));

			var a = await two.EscalationFor(TwoCompanyEscalationFixture.CompanyA)
				.CurrentlyEscalatedTaskIdsAsync(TwoCompanyEscalationFixture.CompanyA);
			var b = await two.EscalationFor(TwoCompanyEscalationFixture.CompanyB)
				.CurrentlyEscalatedTaskIdsAsync(TwoCompanyEscalationFixture.CompanyB);

			// ASYMMETRIC: each company sees its own and only its own. A predicate inverted anywhere in the
			// chain flips one of these four.
			Assert.Contains(mine, a);
			Assert.DoesNotContain(theirs, a);
			Assert.Contains(theirs, b);
			Assert.DoesNotContain(mine, b);
		}

		// ---- asking as the wrong company discloses nothing, from a scope that CAN see rows ---------------
		[Fact]
		public async Task Asking_as_another_company_returns_nothing_even_though_the_database_has_escalations()
		{
			using var two = new TwoCompanyEscalationFixture();

			var mine = two.ArrangeEscalatableTask(TwoCompanyEscalationFixture.CompanyA);
			await two.EscalationFor(TwoCompanyEscalationFixture.CompanyA).SweepAsync(TwoCompanyEscalationFixture.CompanyA);

			var service = two.EscalationFor(TwoCompanyEscalationFixture.CompanyA);

			// The database DOES contain an escalated task, proved by asking as its own company...
			Assert.Contains(mine, await service.CurrentlyEscalatedTaskIdsAsync(TwoCompanyEscalationFixture.CompanyA));

			// ...and asking the SAME service as a different company discloses none of it. The company comes
			// from the argument and the ROW, never from what the scope happens to be able to read.
			Assert.Empty(await service.CurrentlyEscalatedTaskIdsAsync(TwoCompanyEscalationFixture.CompanyB));
		}

		// ---- an unresolved company returns NOTHING, never everything -------------------------------------
		[Fact]
		public async Task An_unresolved_company_returns_nothing_rather_than_everything()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			var task = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-48));
			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			// The resolved company answers, so the refusals below are about the COMPANY and not about an
			// arrangement that escalated nothing.
			Assert.Contains(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));

			foreach (var unresolved in new[] { 0, -1 })
				Assert.Empty(await f.Escalation.CurrentlyEscalatedTaskIdsAsync(unresolved));
		}

		// ---- the set agrees with the per-task answer -----------------------------------------------------
		[Fact]
		public async Task The_set_agrees_with_EvaluateManyAsync_on_every_task()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			var escalated = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-48));
			var inGrace = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-2));
			var notDue = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(+48));

			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);

			var all = new[] { escalated.ID, inGrace.ID, notDue.ID };
			var set = await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA);
			var states = await f.Escalation.EvaluateManyAsync(EscalationFixture.CompanyA, all);

			// THE DRIFT GUARD. The two answers are computed differently - one from the witness, one from the
			// clock and the grace - so the day someone loosens either, this disagrees. Escalated is the only
			// state that may appear in the set.
			foreach (var id in all)
			{
				bool inSet = set.Contains(id);
				bool saysEscalated = states[id].State == TaskEscalationState.Escalated;
				Assert.True(inSet == saysEscalated,
					$"task {id}: set says {inSet}, EvaluateManyAsync says {states[id].State}");
			}

			// NON-VACUITY: the table contains both answers.
			Assert.Contains(all, id => set.Contains(id));
			Assert.Contains(all, id => !set.Contains(id));
		}

		// ---- the read writes nothing ---------------------------------------------------------------------
		[Fact]
		public async Task The_query_escalates_nothing_and_notifies_nobody()
		{
			var (f, emp, _) = Arrange();
			using var _f = f;

			// NEVER SWEPT. A task well past the grace with a real manager - the exact input the sweep would
			// act on - so if this method had any escalating behaviour in it, it would fire here.
			var task = f.AddTask(EscalationFixture.CompanyA, emp, Now.AddHours(-500));

			var before = f.Db.Notifications.Count();
			var beforeEvents = f.Db.BusinessEvents.Count();

			var ids = await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA);

			Assert.Empty(ids);
			Assert.Equal(before, f.Db.Notifications.Count());
			Assert.Equal(beforeEvents, f.Db.BusinessEvents.Count());
			Assert.Equal(0, f.EscalationNotifications(task.ID));

			// And the sweep still would have escalated it - so the emptiness above is "nobody has been told",
			// not "there was nothing to tell".
			await f.Escalation.SweepAsync(EscalationFixture.CompanyA);
			Assert.Contains(task.ID, await f.Escalation.CurrentlyEscalatedTaskIdsAsync(EscalationFixture.CompanyA));
		}
	}
}
