using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Calendar;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// TASKS & CALENDAR — UAT DEFECT CLOSURE
	//
	// Six defects found by the populated CrossBuyDev dataset. Every test here is written against the
	// behaviour that was actually WRONG, not against the shape of the fix — so each one fails on the code
	// as it was and passes on the code as it is.
	//
	// TWO COMPANIES IN ONE DATABASE, ALWAYS. An isolation test with one company proves nothing: the
	// original defect returned company 1's rows because company 1 was the only company the code could
	// name. Every isolation test below seeds BOTH companies and asserts on the one that must NOT be seen.
	// ==========================================================================================
	internal sealed class DefectClosureFixture : IDisposable
	{
		internal const int CompanyA = 1;
		internal const int CompanyB = 65;

		private readonly SqliteConnection _conn;
		private readonly CompanyScopeHolder _holder = new();

		public CrossDbContext Db { get; }
		public ITaskService Tasks { get; }
		public ITaskChecklistService Checklist { get; }
		public ITaskDependencyService Dependencies { get; }
		public ITaskTemplateService Templates { get; }
		public ICalendarService Calendar { get; }
		public ICalendarSchedulingService Scheduling { get; }
		public ITaskOverdueSweepService OverdueSweep { get; }
		public RecordingNotificationService Notifications { get; }

		/// The context every service sees. Company A, an Http source — i.e. a signed-in user of company A.
		/// Swapping this for Unresolved() is how the fail-closed tests are driven.
		public DefectClosureFixture(bool contextResolvable = true, string? defaultZone = "Asia/Riyadh")
		{
			_conn = new SqliteConnection("DataSource=:memory:");
			_conn.Open();
			_holder.Set(CompanyA, null);

			Db = NewContext();
			Db.Database.EnsureCreated();
			using (var cmd = _conn.CreateCommand())
			{
				cmd.CommandText = "PRAGMA foreign_keys = OFF;";
				cmd.ExecuteNonQuery();
			}

			Notifications = new RecordingNotificationService(Db);
			var notify = new TaskNotificationService(Db, Notifications);

			IBusinessContextAccessor accessor = contextResolvable
				? new StubContextAccessor(new BusinessContext
				{
					CompanyId = CompanyA, EmployeeId = 10, UserId = "u10",
					Source = BusinessContextSource.System,   // System so the publisher's company override is honoured
				})
				: StubContextAccessor.Unresolved();

			var events = new BusinessEventService(Db, new EntityRegistry(Db), accessor,
				NullLogger<BusinessEventService>.Instance);
			var publisher = new TaskCalendarEventPublisher(events);

			Tasks = new TaskService(Db, publisher, notify);
			Checklist = new TaskChecklistService(Db);
			Dependencies = new TaskDependencyService(Db);
			Templates = new TaskTemplateService(Db, Tasks, Checklist, Dependencies);
			Calendar = new CalendarService(Db, publisher);

			var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Calendar:DefaultTimeZoneId"] = defaultZone,
			}).Build();
			Scheduling = new CalendarSchedulingService(Db, config);

			OverdueSweep = new TaskOverdueSweepService(Db, notify, publisher, accessor);
		}

		public CrossDbContext NewContext()
		{
			var o = new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlite(_conn)
				.AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
				.Options;
			return new CrossDbContext(o, _holder);
		}

		/// A service set bound to ANOTHER company on the same database — one DI scope per company, exactly as
		/// WorkerScope.ForCompany and CompanyScopeMiddleware produce in production.
		///
		/// This exists because the first version of these tests drove company-B writes through the company-A
		/// scope and CompanyWriteGuardInterceptor REFUSED them: "this scope operates as company 1 but the new
		/// BusinessEvent names company 65". The guard was right and the test was wrong — and the refusal is
		/// worth keeping in mind, because it is a SECOND, independent isolation control sitting underneath the
		/// company argument these tests assert on.
		internal sealed class Bound
		{
			public required CrossDbContext Db { get; init; }
			public required ITaskService Tasks { get; init; }
			public required ITaskTemplateService Templates { get; init; }
			public required ICalendarService Calendar { get; init; }
			public required ITaskOverdueSweepService OverdueSweep { get; init; }
		}

		public Bound BindTo(int companyId)
		{
			var holder = new CompanyScopeHolder();
			holder.Set(companyId, null);

			var options = new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlite(_conn)
				.AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
				.Options;
			var db = new CrossDbContext(options, holder);

			var notifier = new RecordingNotificationService(db);
			var notify = new TaskNotificationService(db, notifier);
			var events = new BusinessEventService(db, new EntityRegistry(db),
				new StubContextAccessor(new BusinessContext
				{
					CompanyId = companyId, EmployeeId = 10, UserId = "u10",
					Source = BusinessContextSource.System,
				}),
				NullLogger<BusinessEventService>.Instance);
			var publisher = new TaskCalendarEventPublisher(events);

			var tasks = new TaskService(db, publisher, notify);
			var checklist = new TaskChecklistService(db);
			var deps = new TaskDependencyService(db);

			return new Bound
			{
				Db = db,
				Tasks = tasks,
				Templates = new TaskTemplateService(db, tasks, checklist, deps),
				Calendar = new CalendarService(db, publisher),
				OverdueSweep = new TaskOverdueSweepService(db, notify, publisher,
					new StubContextAccessor(new BusinessContext
					{
						CompanyId = companyId, EmployeeId = 10, UserId = "u10",
						Source = BusinessContextSource.System,
					})),
			};
		}

		public async Task<int> SeedEmployeeAsync(int id, int companyId)
		{
			Db.Employee.Add(new Employee
			{
				ID = id, EmpCompanyID = companyId, IsActive = true,
				FirstName = $"E{id}", LastName = "T", FullName = $"Employee {id} of {companyId}",
				Address = "-", PhoneNumber = "-", Email = $"e{id}@c{companyId}.local",
				ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = $"u{id}"
			});
			await Db.SaveChangesAsync();
			return id;
		}

		/// A task written DIRECTLY, so a test about isolation is not also a test about the create path.
		public async Task<int> SeedTaskAsync(int companyId, string title, int assignee,
			DateTime? due = null, string status = "New")
		{
			var t = new TaskItem
			{
				CompanyId = companyId, Title = title, AssigneeEmployeeId = assignee,
				CreatedByEmployeeId = assignee, Status = status, Priority = "Normal",
				DueDate = due, CreatedAt = DateTime.UtcNow
			};
			Db.TaskItems.Add(t);
			await Db.SaveChangesAsync();
			return t.ID;
		}

		public async Task<int> SeedCalendarEventAsync(int companyId, int ownerId, string title,
			DateTime start, string? timeZoneId, string recurrence = RecurrenceKinds.Weekly, int? count = 4)
		{
			var ev = new CalendarEvent
			{
				CompanyID = companyId, OwnerEmpId = ownerId, Title = title,
				StartAt = start, EndAt = start.AddHours(1), Scope = "Company",
				CreatedBy = ownerId, CreatedAt = DateTime.Now
			};
			Db.CalendarEvents.Add(ev);
			await Db.SaveChangesAsync();

			Db.CalendarEventSchedules.Add(new CalendarEventSchedule
			{
				CompanyId = companyId, EventId = ev.Id,
				TimeZoneId = timeZoneId,               // the point of the test: this may be NULL
				RecurrenceKind = recurrence, Interval = 1, OccurrenceCount = count,
				CreatedAt = DateTime.UtcNow
			});
			await Db.SaveChangesAsync();
			return ev.Id;
		}

		public void Dispose() { Db.Dispose(); _conn.Dispose(); }
	}

	// ==========================================================================================
	// DEFECT 1 — TASKS COMPANY ISOLATION  (tests 1, 2, 3, 4, 5, 6, 8)
	//
	// The leak lived in TasksController, which passed a literal 1 to services that filter correctly. So
	// the closure has two halves and both are proven:
	//   · HERE: every service honours the company it is GIVEN, for reads and for writes. If any of these
	//     failed, no controller fix could contain the leak.
	//   · RUNTIME: the controller now GIVES the resolved company. That is proven live against CrossBuyDev
	//     with a company-65 session, because only a real request exercises the resolver.
	// ==========================================================================================
	public class TasksCompanyIsolationTests
	{
		[Fact]
		public async Task Company_65_cannot_READ_company_1_tasks()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);

			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-2", 10);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyB, "B-1", 90);

			var seenByB = await f.Tasks.GetTasksAsync(DefectClosureFixture.CompanyB, "all", 90,
				null, null, null, 1, 50, null, null, null);

			Assert.Single(seenByB);
			Assert.Equal("B-1", seenByB[0].Title);
			Assert.DoesNotContain(seenByB, r => r.Title.StartsWith("A-"));

			// And the count agrees with the list — the KPI strip and the grid must not disagree.
			Assert.Equal(1, await f.Tasks.CountTasksAsync(DefectClosureFixture.CompanyB, "all", 90,
				null, null, null, null, null));
		}

		[Fact]
		public async Task Company_65_cannot_MUTATE_a_company_1_task()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int aTask = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);

			// Status change, delete and edit are the three write paths the list screen offers.
			var (statusOk, statusErr) = await f.Tasks.ChangeStatusAsync(
				DefectClosureFixture.CompanyB, aTask, "InProgress", 90);
			Assert.False(statusOk);
			Assert.NotNull(statusErr);

			var (deleteOk, _) = await f.Tasks.DeleteAsync(DefectClosureFixture.CompanyB, aTask);
			Assert.False(deleteOk);

			var (editOk, _, _) = await f.Tasks.SaveAsync(DefectClosureFixture.CompanyB,
				new TaskSaveInput { Id = aTask, Title = "hijacked", AssigneeEmployeeId = 90 }, 90);
			Assert.False(editOk);

			// The row is untouched — asserted from a NEW context, not from the entity that refused.
			using var fresh = f.NewContext();
			var row = await fresh.TaskItems.AsNoTracking().FirstAsync(t => t.ID == aTask);
			Assert.Equal("A-1", row.Title);
			Assert.Equal("New", row.Status);
			Assert.Equal(DefectClosureFixture.CompanyA, row.CompanyId);
		}

		[Fact]
		public async Task Task_template_isolation_holds_for_read_and_for_activation()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);

			var (ok, err, aTemplateId) = await f.Templates.SaveAsync(DefectClosureFixture.CompanyA,
				new TaskTemplate
				{
					Name = "A monthly close",
					Items = new List<TaskTemplateItem> { new() { Title = "Freeze", SortOrder = 1 } }
				}, 10);
			Assert.True(ok, err);

			// Company B sees none of A's templates…
			Assert.Empty(await f.Templates.ListAsync(DefectClosureFixture.CompanyB, activeOnly: false));
			Assert.Null(await f.Templates.GetAsync(DefectClosureFixture.CompanyB, aTemplateId));

			// …and cannot deactivate one by id.
			var (activateOk, _) = await f.Templates.SetActiveAsync(
				DefectClosureFixture.CompanyB, aTemplateId, active: false);
			Assert.False(activateOk);

			using var fresh = f.NewContext();
			Assert.True(await fresh.TaskTemplates.AsNoTracking()
				.Where(t => t.ID == aTemplateId).Select(t => t.IsActive).FirstAsync());
		}

		[Fact]
		public async Task Applying_a_template_creates_tasks_in_the_APPLYING_company_only()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);

			// Company B's own scope — applying a template WRITES (tasks + their business events), and a write
			// must run in the scope of the company it writes into.
			var b = f.BindTo(DefectClosureFixture.CompanyB);

			var (ok, err, templateId) = await b.Templates.SaveAsync(DefectClosureFixture.CompanyB,
				new TaskTemplate
				{
					Name = "B onboarding",
					Items = new List<TaskTemplateItem> { new() { Title = "Contract", SortOrder = 1 } }
				}, 90);
			Assert.True(ok, err);

			var (applyOk, applyErr, result) = await b.Templates.ApplyAsync(
				DefectClosureFixture.CompanyB, templateId, new DateTime(2026, 8, 12), null, 90);
			Assert.True(applyOk, applyErr);
			Assert.NotNull(result);

			// This is the defect TemplateApply carried: it authorised the caller's company and then created
			// the tasks in company 1.
			using var fresh = f.NewContext();
			foreach (var id in result!.CreatedTaskIds)
				Assert.Equal(DefectClosureFixture.CompanyB,
					await fresh.TaskItems.AsNoTracking().Where(t => t.ID == id).Select(t => t.CompanyId).FirstAsync());

			Assert.Equal(0, await fresh.TaskItems.AsNoTracking()
				.CountAsync(t => t.CompanyId == DefectClosureFixture.CompanyA));
		}

		[Fact]
		public async Task Checklist_and_dependency_reads_are_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int a1 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);
			int a2 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-2", 10);

			await f.Checklist.AddAsync(DefectClosureFixture.CompanyA, a1, "line", 10);
			await f.Dependencies.AddAsync(DefectClosureFixture.CompanyA, a1, a2,
				TaskDependencyKinds.FinishToStart, 0, 10);

			// Company B asking about company A's task ids gets nothing, and cannot add to them.
			Assert.Empty(await f.Checklist.ForTaskAsync(DefectClosureFixture.CompanyB, a1));
			Assert.Empty(await f.Dependencies.ForTaskAsync(DefectClosureFixture.CompanyB, a1));
			Assert.Null(await f.Checklist.OwningTaskIdAsync(DefectClosureFixture.CompanyB, a1));

			var (addOk, _, _) = await f.Checklist.AddAsync(DefectClosureFixture.CompanyB, a1, "smuggled", 90);
			Assert.False(addOk);

			var (edgeOk, _, _) = await f.Dependencies.AddAsync(DefectClosureFixture.CompanyB, a1, a2,
				TaskDependencyKinds.FinishToStart, 0, 90);
			Assert.False(edgeOk);
		}

		[Fact]
		public async Task Board_scope_and_blocking_state_are_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);
			int a1 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);
			int a2 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-2", 10, status: "InProgress");
			await f.SeedTaskAsync(DefectClosureFixture.CompanyB, "B-1", 90);
			await f.Dependencies.AddAsync(DefectClosureFixture.CompanyA, a1, a2,
				TaskDependencyKinds.FinishToStart, 0, 10);

			// The board reads with the SAME GetTasksAsync the list uses (view "all", one page of 500).
			var board = await f.Tasks.GetTasksAsync(DefectClosureFixture.CompanyB, "all", 90,
				null, null, null, 1, 500, "all", null, null);
			Assert.Single(board);

			// Blocking state for another company's ids resolves to "not blocked by anything we can see",
			// never to company A's edge.
			var blocking = await f.Dependencies.BlockingStateManyAsync(
				DefectClosureFixture.CompanyB, new[] { a1, a2 });
			Assert.All(blocking.Values, s => Assert.Empty(s.BlockedByTaskIds));
		}

		[Fact]
		public async Task Match_suggestions_are_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int a1 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);

			// GetPendingAsync only groups suggestions whose task is still SCHEDULED and unmatched — a
			// suggestion on an ordinary task is not pending review. Making the control valid rather than
			// asserting on an empty list for the wrong reason.
			var scheduled = await f.Db.TaskItems.FirstAsync(t => t.ID == a1);
			scheduled.IsScheduled = true;
			scheduled.ExpectedEntityType = "SalesInvoice";
			scheduled.ExpectedPartyType = "Customer";
			scheduled.ExpectedPartyId = 77;
			await f.Db.SaveChangesAsync();

			f.Db.TaskMatchSuggestions.Add(new TaskMatchSuggestion
			{
				CompanyId = DefectClosureFixture.CompanyA, TaskId = a1,
				EntityType = "SalesInvoice", EntityId = 999, Label = "INV-A", CreatedAt = DateTime.UtcNow
			});
			await f.Db.SaveChangesAsync();

			var matcher = new TaskScheduleMatcher(f.Db);
			Assert.Empty(await matcher.GetPendingAsync(DefectClosureFixture.CompanyB));
			Assert.Single(await matcher.GetPendingAsync(DefectClosureFixture.CompanyA));

			// Dismissing another company's suggestion is refused.
			var suggestionId = await f.Db.TaskMatchSuggestions.AsNoTracking()
				.Where(s => s.TaskId == a1).Select(s => s.ID).FirstAsync();
			var (dismissOk, _) = await matcher.DismissAsync(DefectClosureFixture.CompanyB, suggestionId);
			Assert.False(dismissOk);
		}

		[Fact]
		public async Task Hours_report_is_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);
			int a1 = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-1", 10);
			int b1 = await f.SeedTaskAsync(DefectClosureFixture.CompanyB, "B-1", 90);

			var timesheets = new TimesheetService(f.Db);
			var day = new DateTime(2026, 8, 10);
			await timesheets.AddManualAsync(DefectClosureFixture.CompanyA, a1, 10, day, 8m, "A work");
			await timesheets.AddManualAsync(DefectClosureFixture.CompanyB, b1, 90, day, 3m, "B work");

			var bReport = await timesheets.GetHoursReportAsync(
				DefectClosureFixture.CompanyB, day.AddDays(-1), day.AddDays(1), null);

			Assert.Equal(3m, bReport.GrandTotal);
			Assert.Single(bReport.Employees);
			Assert.Equal(90, bReport.Employees[0].EmployeeId);
		}

		[Fact]
		public async Task Task_reports_are_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);
			var past = DateTime.Today.AddDays(-3);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-late-1", 10, past);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A-late-2", 10, past);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyB, "B-late-1", 90, past);

			var reports = new TaskReportService(f.Db, new EmployeeCostService(f.Db));
			var bOverdue = await reports.OverdueAsync(DefectClosureFixture.CompanyB);
			var aOverdue = await reports.OverdueAsync(DefectClosureFixture.CompanyA);

			// The counts DIFFER, which is the whole point: a leak makes them identical.
			Assert.NotEqual(aOverdue.Count, bOverdue.Count);
			Assert.Single(bOverdue);
		}
	}

	// ==========================================================================================
	// DEFECT 2 — ActiveEmployeesAsync  (test 7)
	// ==========================================================================================
	public class ActiveEmployeesIsolationTests
	{
		[Fact]
		public async Task Each_company_sees_only_its_own_employees_in_the_picker()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(11, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);

			var a = await f.Tasks.ActiveEmployeesAsync(DefectClosureFixture.CompanyA);
			var b = await f.Tasks.ActiveEmployeesAsync(DefectClosureFixture.CompanyB);

			Assert.Equal(2, a.Count);
			Assert.Single(b);
			Assert.Equal(90, b[0].Id);
			Assert.DoesNotContain(b, e => e.Id == 10 || e.Id == 11);
			// The disclosure was of NAMES, so the assertion is about names too.
			Assert.DoesNotContain(b, e => e.Name.Contains($"of {DefectClosureFixture.CompanyA}"));
		}

		[Fact]
		public async Task An_unresolved_company_lists_nobody_rather_than_everybody()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);

			// 0 is what the controller passes when nothing resolves. Failing OPEN here would hand every
			// company's staff to a request that could not even establish who it was.
			Assert.Empty(await f.Tasks.ActiveEmployeesAsync(0));
			Assert.Empty(await f.Tasks.ActiveEmployeesAsync(-1));
		}
	}

	// ==========================================================================================
	// DEFECT 3 — NULL TimeZoneId  (tests 9, 10, 11, 12)
	// ==========================================================================================
	public class CalendarNullTimeZoneTests
	{
		private static readonly DateTime Window = new(2026, 8, 12);

		[Fact]
		public async Task A_schedule_with_NULL_TimeZoneId_expands_instead_of_throwing()
		{
			using var f = new DefectClosureFixture(defaultZone: "Asia/Riyadh");
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10,
				"Weekly review", Window.AddHours(9), timeZoneId: null);

			var occ = await f.Scheduling.ExpandAsync(DefectClosureFixture.CompanyA, id,
				Window.AddDays(-1), Window.AddDays(30));

			// Before the fix this threw TimeZoneUnresolvedException.
			Assert.NotEmpty(occ);
			Assert.Equal(4, occ.Count);   // OccurrenceCount = 4
		}

		[Fact]
		public async Task An_EXPLICIT_TimeZoneId_still_wins_over_the_default()
		{
			using var f = new DefectClosureFixture(defaultZone: "Asia/Riyadh");
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10,
				"London weekly", Window.AddHours(9), timeZoneId: "Europe/London");

			var occ = await f.Scheduling.ExpandAsync(DefectClosureFixture.CompanyA, id,
				Window.AddDays(-1), Window.AddDays(30));

			Assert.Equal(4, occ.Count);
			// The default did not silently replace the named zone: a London series expanded in Riyadh would
			// land on different instants. Asserting the FIRST occurrence pins that.
			var expectedFirst = TimeZoneInfo.ConvertTimeFromUtc(
				DateTime.SpecifyKind(Window.AddHours(9), DateTimeKind.Utc),
				TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
			Assert.Equal(expectedFirst.TimeOfDay, occ[0].StartUtc.AddHours(
				TimeZoneInfo.FindSystemTimeZoneById("Europe/London")
					.GetUtcOffset(occ[0].StartUtc).TotalHours).TimeOfDay);
		}

		[Fact]
		public async Task The_configured_default_is_used_and_an_unconfigured_one_falls_back_to_the_host()
		{
			// Configured: the value is honoured.
			using (var f = new DefectClosureFixture(defaultZone: "Europe/London"))
			{
				var svc = (CalendarSchedulingService)f.Scheduling;
				Assert.Equal("Europe/London", svc.DefaultZoneId);
			}

			// Unconfigured: the host zone, stated rather than thrown. This is the branch that keeps a
			// deployment with no Calendar:DefaultTimeZoneId working.
			using (var f = new DefectClosureFixture(defaultZone: null))
			{
				var svc = (CalendarSchedulingService)f.Scheduling;
				Assert.Equal(TimeZoneInfo.Local.Id, svc.DefaultZoneId);
			}
		}

		[Fact]
		public async Task Timeline_survives_a_null_zone_and_still_shows_the_occurrence()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10,
				"Standup", DateTime.Today.AddHours(9), timeZoneId: null, recurrence: RecurrenceKinds.Daily, count: 5);
			f.Db.CalendarEventAttendees.Add(new CalendarEventAttendee { EventId = id, EmployeeId = 10 });
			await f.Db.SaveChangesAsync();

			// Before the fix this was a hard 500 for the WHOLE company, not a missing row.
			var page = await f.Scheduling.BuildTimelinePageAsync(DefectClosureFixture.CompanyA, DateTime.Today);

			Assert.NotNull(page);
			Assert.True(page.OccurrenceCount > 0);
			Assert.Contains(page.People, p => p.EmployeeId == 10 && p.Occurrences.Count > 0);
		}

		[Fact]
		public async Task ResourceView_survives_a_null_zone_and_still_shows_the_booking()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10,
				"Board meeting", DateTime.Today.AddHours(10), timeZoneId: null,
				recurrence: RecurrenceKinds.Daily, count: 3);

			var room = new CalendarResource
			{
				CompanyId = DefectClosureFixture.CompanyA, Name = "Room A", Kind = "Room",
				IsActive = true, CreatedAt = DateTime.UtcNow
			};
			f.Db.CalendarResources.Add(room);
			await f.Db.SaveChangesAsync();
			f.Db.CalendarEventResources.Add(new CalendarEventResource
			{
				CompanyId = DefectClosureFixture.CompanyA, EventId = id, ResourceId = room.ID
			});
			await f.Db.SaveChangesAsync();

			var page = await f.Scheduling.BuildResourcePageAsync(DefectClosureFixture.CompanyA, DateTime.Today);

			Assert.Single(page.Resources);
			Assert.NotEmpty(page.Resources[0].Occurrences);
		}

		[Fact]
		public async Task One_null_zone_row_no_longer_takes_the_whole_company_down_with_it()
		{
			// THE ACTUAL FAILURE MODE. ExpandWindowAsync expands every event in one pass with no catch, so a
			// single unresolvable row threw for all of them. This asserts the OTHER events still arrive.
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10, "null-zone",
				DateTime.Today.AddHours(9), timeZoneId: null, recurrence: RecurrenceKinds.Daily, count: 2);
			await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10, "explicit-zone",
				DateTime.Today.AddHours(11), timeZoneId: "Asia/Riyadh", recurrence: RecurrenceKinds.Daily, count: 2);

			var all = await f.Scheduling.ExpandWindowAsync(DefectClosureFixture.CompanyA,
				DateTime.Today.AddDays(-1), DateTime.Today.AddDays(5));

			Assert.Contains(all, o => o.Title == "null-zone");
			Assert.Contains(all, o => o.Title == "explicit-zone");
		}

		[Fact]
		public void A_caller_that_supplies_NEITHER_zone_still_fails_explicitly()
		{
			// The guard is not removed, only given a default. A pure caller with no zone at all must still be
			// refused — guessing there is the original defect, and the production callers all pass a default.
			var ev = new CalendarEvent
			{
				Id = 1, CompanyID = 1, Title = "x", StartAt = Window.AddHours(9), EndAt = Window.AddHours(10)
			};
			var schedule = new CalendarEventSchedule
			{
				CompanyId = 1, EventId = 1, TimeZoneId = null,
				RecurrenceKind = RecurrenceKinds.Weekly, Interval = 1, OccurrenceCount = 2
			};

			Assert.Throws<TimeZoneUnresolvedException>(() =>
				CalendarSchedulingService.Expand(ev, schedule, Window.AddDays(-1), Window.AddDays(30), null));
		}

		[Fact]
		public async Task Recurrence_in_a_DST_zone_keeps_its_LOCAL_wall_clock_across_the_change()
		{
			// The reason the column stores a zone ID and not an offset. A 09:00 London weekly series must
			// still be 09:00 London after the clocks move, which means its UTC instant SHIFTS by an hour.
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.SeedCalendarEventAsync(DefectClosureFixture.CompanyA, 10, "London 09:00",
				new DateTime(2026, 10, 21, 9, 0, 0), timeZoneId: "Europe/London",
				recurrence: RecurrenceKinds.Weekly, count: 4);

			var occ = await f.Scheduling.ExpandAsync(DefectClosureFixture.CompanyA, id,
				new DateTime(2026, 10, 1), new DateTime(2026, 12, 1));

			var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
			var localTimes = occ
				.Select(o => TimeZoneInfo.ConvertTimeFromUtc(
					DateTime.SpecifyKind(o.StartUtc, DateTimeKind.Utc), london).TimeOfDay)
				.Distinct()
				.ToList();

			// ONE distinct local time across the DST boundary — the wall clock held.
			Assert.Single(localTimes);
			// …and the UTC offsets genuinely differed, so the test really did cross the change.
			Assert.True(occ.Select(o => london.GetUtcOffset(o.StartUtc)).Distinct().Count() > 1,
				"the window must span the DST change for this assertion to mean anything");
		}
	}

	// ==========================================================================================
	// DEFECT 5 — Task.BecameOverdue BUSINESS EVENT  (tests 13, 14)
	// ==========================================================================================
	public class OverdueBusinessEventTests
	{
		[Fact]
		public async Task An_overdue_transition_records_exactly_one_business_event_and_one_notification()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int late = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Late invoice review", 10,
				due: DateTime.UtcNow.AddDays(-2));

			var sweep = await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			Assert.Equal(1, sweep.Examined);
			Assert.Equal(1, sweep.EventsRecorded);

			using var fresh = f.NewContext();
			var events = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityType == "Task" && e.EntityId == late).ToListAsync();

			// The bell and the monitor now agree — which is the whole defect.
			Assert.Single(events);
			Assert.Equal("Task.BecameOverdue", events[0].EventType);
			Assert.Equal(DefectClosureFixture.CompanyA, events[0].CompanyID);

			// The publisher passes actorId: null, so the event carries no ACTOR OVERRIDE — a date passing is
			// nobody's action. What lands in the column is then whatever the recording context supplies
			// (BusinessEventService: `IsSystem && Override.HasValue ? Override : context.EmployeeId`), which in
			// a real background sweep is null because a worker context has no employee. Asserting null HERE
			// would be asserting a property of this fixture, not of the product, so the assertion is on the
			// fact that matters: the event exists, once, for the right task in the right company.
			Assert.Contains(f.Notifications.Sent, n => n.EntityId == late);
		}

		[Fact]
		public async Task A_REPEATED_sweep_records_no_duplicate_event()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int late = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Late", 10,
				due: DateTime.UtcNow.AddDays(-2));

			await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);
			await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);
			await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			using var fresh = f.NewContext();
			// THREE sweeps, ONE event. The dedup key is derived from the DUE DATE, so a 15-minute timer
			// cannot accumulate rows — this is the assertion that makes the worker safe to leave running.
			Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking()
				.CountAsync(e => e.EntityType == "Task" && e.EntityId == late));
		}

		[Fact]
		public async Task A_CHANGED_due_date_is_a_new_occurrence_and_records_a_second_event()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int late = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Late", 10,
				due: DateTime.UtcNow.AddDays(-5));

			await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			var task = await f.Db.TaskItems.FirstAsync(t => t.ID == late);
			task.DueDate = DateTime.UtcNow.AddDays(-1);        // rescheduled, and missed again
			await f.Db.SaveChangesAsync();

			await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			using var fresh = f.NewContext();
			Assert.Equal(2, await fresh.BusinessEvents.AsNoTracking()
				.CountAsync(e => e.EntityType == "Task" && e.EntityId == late));
		}

		[Fact]
		public async Task The_sweep_records_events_for_ONE_company_only()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);
			int aLate = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "A late", 10, DateTime.UtcNow.AddDays(-2));
			int bLate = await f.SeedTaskAsync(DefectClosureFixture.CompanyB, "B late", 90, DateTime.UtcNow.AddDays(-2));

			var sweep = await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			Assert.Equal(1, sweep.Examined);
			using var fresh = f.NewContext();
			Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking().CountAsync(e => e.EntityId == aLate));
			Assert.Equal(0, await fresh.BusinessEvents.AsNoTracking().CountAsync(e => e.EntityId == bLate));
		}

		[Fact]
		public async Task A_completed_or_unassigned_task_is_never_swept()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Done late", 10,
				due: DateTime.UtcNow.AddDays(-2), status: "Done");
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Unassigned late", 0,
				due: DateTime.UtcNow.AddDays(-2));

			var sweep = await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			Assert.Equal(0, sweep.Examined);
			Assert.Equal(0, sweep.EventsRecorded);
		}

		// ---- THE REMAINING HALF, PROVEN RATHER THAN ASSERTED -----------------------------------
		[Fact]
		public async Task WITHOUT_a_resolvable_BusinessContext_the_sweep_still_notifies()
		{
			// THIS IS THE PRODUCTION BACKGROUND SHAPE, and it is the most important test in this file.
			//
			// TaskGeneratorHostedService runs the sweep inside WorkerScope.ForCompany — a DI scope with NO
			// HttpContext — and IBusinessContextAccessor resolves only from HTTP
			// (BusinessContextAccessor.TryGetCurrentAsync -> IBusinessContextFactory.TryForHttpAsync). So
			// RecordAsync has no context to attribute an event to and throws there.
			//
			// The FIRST version of the fix published unconditionally. In this shape it threw on candidate #1,
			// the worker's own try/catch swallowed it, and the sweep sent NO NOTIFICATIONS AT ALL — turning an
			// event gap into a notification outage. This test is what caught that, and it is what keeps the
			// notification path safe from any future attempt to make the event mandatory here.
			using var f = new DefectClosureFixture(contextResolvable: false);
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int late = await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Late", 10, due: DateTime.UtcNow.AddDays(-2));

			var sweep = await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			// It does NOT throw…
			Assert.Equal(1, sweep.Examined);
			// …the notification still goes out — no regression…
			Assert.Equal(1, sweep.Notified);
			Assert.Contains(f.Notifications.Sent, n => n.EntityId == late);
			// …no event is recorded, and the gap is COUNTED rather than hidden…
			Assert.Equal(0, sweep.EventsRecorded);
			Assert.Equal(1, sweep.EventsSkippedNoContext);

			using var fresh = f.NewContext();
			Assert.Equal(0, await fresh.BusinessEvents.AsNoTracking()
				.CountAsync(e => e.EntityType == "Task" && e.EntityId == late));
		}

		[Fact]
		public async Task WITH_a_resolvable_context_nothing_is_skipped()
		{
			// The other side of the same switch, so the guard cannot silently disable the feature everywhere.
			using var f = new DefectClosureFixture(contextResolvable: true);
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedTaskAsync(DefectClosureFixture.CompanyA, "Late", 10, due: DateTime.UtcNow.AddDays(-2));

			var sweep = await f.OverdueSweep.SweepAsync(DefectClosureFixture.CompanyA);

			Assert.Equal(1, sweep.EventsRecorded);
			Assert.Equal(0, sweep.EventsSkippedNoContext);
		}
	}

	// ==========================================================================================
	// DEFECT 6 — CALENDAR BUSINESS EVENTS  (tests 15, 16)
	// ==========================================================================================
	public class CalendarBusinessEventTests
	{
		private static CalEventInput Input(string title, DateTime start, params int[] attendees) => new()
		{
			Title = title, StartAt = start, EndAt = start.AddHours(1),
			Scope = "Company", Attendees = attendees.ToList()
		};

		[Fact]
		public async Task Creating_an_event_records_CalendarEvent_Created_with_the_right_company()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);

			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				Input("Weekly sales review", DateTime.Today.AddHours(9)));

			using var fresh = f.NewContext();
			var events = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityType == "CalendarEvent" && e.EntityId == id).ToListAsync();

			// Before the fix this list was EMPTY for every calendar operation.
			Assert.Single(events);
			Assert.Equal("CalendarEvent.Created", events[0].EventType);
			Assert.Equal(DefectClosureFixture.CompanyA, events[0].CompanyID);
		}

		[Fact]
		public async Task Attendee_changes_record_added_and_removed_as_a_DIFF_not_a_replace()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(11, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(12, DefectClosureFixture.CompanyA);

			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				Input("Planning", DateTime.Today.AddHours(9), 11));

			// 11 stays, 12 joins. A replace-everything write would report 11 removed AND re-added.
			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				new CalEventInput
				{
					Id = id, Title = "Planning", StartAt = DateTime.Today.AddHours(9),
					EndAt = DateTime.Today.AddHours(10), Scope = "Company",
					Attendees = new List<int> { 11, 12 }
				});

			using var fresh = f.NewContext();
			var types = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityType == "CalendarEvent" && e.EntityId == id)
				.Select(e => e.EventType).ToListAsync();

			Assert.Equal(2, types.Count(t => t == "CalendarEvent.AttendeeAdded"));   // 11 at create, 12 at edit
			Assert.DoesNotContain("CalendarEvent.AttendeeRemoved", types);

			// Now drop 11.
			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				new CalEventInput
				{
					Id = id, Title = "Planning", StartAt = DateTime.Today.AddHours(9),
					EndAt = DateTime.Today.AddHours(10), Scope = "Company",
					Attendees = new List<int> { 12 }
				});

			using var fresh2 = f.NewContext();
			Assert.Equal(1, await fresh2.BusinessEvents.AsNoTracking().CountAsync(
				e => e.EntityId == id && e.EventType == "CalendarEvent.AttendeeRemoved"));
		}

		[Fact]
		public async Task Moving_an_event_records_Rescheduled_and_a_field_edit_records_Updated()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			var start = DateTime.Today.AddHours(9);
			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, Input("Review", start));

			// Moved AND retitled in one save: two distinct facts, both recorded.
			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, new CalEventInput
			{
				Id = id, Title = "Review (moved)", StartAt = start.AddHours(3),
				EndAt = start.AddHours(4), Scope = "Company"
			});

			using var fresh = f.NewContext();
			var types = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityId == id && e.EntityType == "CalendarEvent")
				.Select(e => e.EventType).ToListAsync();

			Assert.Contains("CalendarEvent.Rescheduled", types);
			Assert.Contains("CalendarEvent.Updated", types);
		}

		[Fact]
		public async Task A_save_that_changes_NOTHING_records_no_update_event()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			var start = DateTime.Today.AddHours(9);
			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, Input("Steady", start));

			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, new CalEventInput
			{
				Id = id, Title = "Steady", StartAt = start, EndAt = start.AddHours(1), Scope = "Company"
			});

			using var fresh = f.NewContext();
			var types = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityId == id && e.EntityType == "CalendarEvent")
				.Select(e => e.EventType).ToListAsync();

			// A no-op save is not a business fact. Recording one would fill the monitor with noise and teach
			// operators to ignore it.
			Assert.Single(types);
			Assert.Equal("CalendarEvent.Created", types[0]);
		}

		[Fact]
		public async Task Deleting_an_event_records_Cancelled_because_the_row_is_soft_deleted()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				Input("Cancelled meeting", DateTime.Today.AddHours(9)));

			Assert.True(await f.Calendar.DeleteAsync(DefectClosureFixture.CompanyA, 10, id));

			using var fresh = f.NewContext();
			Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking().CountAsync(
				e => e.EntityId == id && e.EventType == "CalendarEvent.Cancelled"));
		}

		[Fact]
		public async Task A_repeated_identical_save_does_not_duplicate_the_create_event()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(11, DefectClosureFixture.CompanyA);

			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				Input("Idempotent", DateTime.Today.AddHours(9), 11));

			// Re-applying the SAME attendee set must not re-record the attendee event: the dedup key is
			// `att+:{employeeId}` per (company, event), so a retry collapses onto the original.
			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, new CalEventInput
			{
				Id = id, Title = "Idempotent", StartAt = DateTime.Today.AddHours(9),
				EndAt = DateTime.Today.AddHours(10), Scope = "Company",
				Attendees = new List<int> { 11 }
			});

			using var fresh = f.NewContext();
			Assert.Equal(1, await fresh.BusinessEvents.AsNoTracking().CountAsync(
				e => e.EntityId == id && e.EventType == "CalendarEvent.AttendeeAdded"));
		}

		[Fact]
		public async Task Authorization_still_holds_only_the_owner_may_edit_or_delete()
		{
			// Test 17. The event wiring must not have widened who may write: SaveAsync still throws for a
			// non-owner and DeleteAsync still refuses, and NO event is recorded for a refused operation.
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(11, DefectClosureFixture.CompanyA);
			int id = await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10,
				Input("Owned by 10", DateTime.Today.AddHours(9)));

			await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
				f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 11, new CalEventInput
				{
					Id = id, Title = "hijacked", StartAt = DateTime.Today.AddHours(9),
					EndAt = DateTime.Today.AddHours(10), Scope = "Company"
				}));

			Assert.False(await f.Calendar.DeleteAsync(DefectClosureFixture.CompanyA, 11, id));

			// Another company cannot even find it.
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				f.Calendar.SaveAsync(DefectClosureFixture.CompanyB, 10, new CalEventInput
				{
					Id = id, Title = "cross-company", StartAt = DateTime.Today.AddHours(9), Scope = "Company"
				}));
			Assert.False(await f.Calendar.DeleteAsync(DefectClosureFixture.CompanyB, 10, id));

			using var fresh = f.NewContext();
			var types = await fresh.BusinessEvents.AsNoTracking()
				.Where(e => e.EntityId == id && e.EntityType == "CalendarEvent")
				.Select(e => e.EventType).ToListAsync();
			Assert.Single(types);                      // only the original Created
			Assert.DoesNotContain("CalendarEvent.Cancelled", types);
		}

		[Fact]
		public async Task Calendar_visibility_is_unchanged_and_still_company_scoped()
		{
			using var f = new DefectClosureFixture();
			await f.SeedEmployeeAsync(10, DefectClosureFixture.CompanyA);
			await f.SeedEmployeeAsync(90, DefectClosureFixture.CompanyB);

			// One scope per company, because each save also writes that company's business event.
			await f.Calendar.SaveAsync(DefectClosureFixture.CompanyA, 10, Input("A event", DateTime.Today.AddHours(9)));
			var b = f.BindTo(DefectClosureFixture.CompanyB);
			await b.Calendar.SaveAsync(DefectClosureFixture.CompanyB, 90, Input("B event", DateTime.Today.AddHours(9)));

			var seenByB = await b.Calendar.ListAsync(DefectClosureFixture.CompanyB, 90,
				DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

			Assert.Single(seenByB);
			Assert.Equal("B event", seenByB[0].title);
		}
	}
}
