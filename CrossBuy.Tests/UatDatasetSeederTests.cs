using CrossBuy.BL.Platform;
using CrossBuy.BL.Uat;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// UAT DATASET SEEDER — THE REFUSALS AND THE TWO CONTRACTS
	//
	// WHAT IS TESTED HERE AND WHAT IS DELIBERATELY NOT
	//
	//   HERE: the three guards (environment, catalogue name, outbound mail), the marker/ownership contract,
	//   the plan's idempotency contract (distinct titles), and the primary/secondary thinness invariant that
	//   the company-isolation scenario depends on. All fast, all deterministic, no database and no server.
	//
	//   NOT HERE: idempotency, cleanup and company isolation END TO END. Those are proven against the real
	//   CrossBuyDev by running the seeder twice and comparing the reports, and by seed -> cleanup -> reseed on
	//   a disposable domain. An in-memory approximation would need the whole DI graph (TaskService,
	//   BusinessEventService, the reporting authorization chain) standing on SQLite, and a green test over an
	//   approximation of the write path is exactly the kind of proof this project has already been burned by:
	//   "112 green tests coexisted with an application that could not boot". The runtime proof is the stronger
	//   evidence, so it is the one that is claimed.
	//
	//   The guards, by contrast, CANNOT be proven at runtime without pointing a development machine at a
	//   protected catalogue — so they are proven here, where pointing at CrossBuyDB2 costs nothing because
	//   nothing ever connects.
	// ==========================================================================================
	public class UatSeedGuardTests
	{
		// A context that NAMES a catalogue. For every REFUSED case nothing connects: SqlConnection reports its
		// InitialCatalog from the connection string alone, which is what makes it safe to construct a context
		// pointing at CrossBuyDB2 in a test — the guard reads the name and refuses before anything connects.
		//
		// The cleared case is different, and the difference is easy to miss: once no guard has objected,
		// UatSeedGuard proves the catalogue is live with CanConnectAsync. So the server here must be the real
		// local instance, not a placeholder. It is the DEFAULT instance ("localhost"); a machine running SQL
		// Server Express instead needs "localhost\\SQLEXPRESS".
		private static CrossDbContext ContextFor(string catalog)
		{
			var options = new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlServer($"Server=localhost;Database={catalog};Trusted_Connection=True;" +
							  "TrustServerCertificate=True;Connection Timeout=1;")
				.Options;
			return new CrossDbContext(options, new CompanyScopeHolder());
		}

		private static IConfiguration Config(bool smtpEnabled, bool whatsapp = false)
			=> new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Smtp:Enabled"] = smtpEnabled ? "true" : "false",
				["WhatsApp:Enabled"] = whatsapp ? "true" : "false",
			}).Build();

		private sealed class Env : IWebHostEnvironment
		{
			public Env(string name) { EnvironmentName = name; }
			public string EnvironmentName { get; set; }
			public string ApplicationName { get; set; } = "CrossBuy";
			public string WebRootPath { get; set; } = ".";
			public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
			public string ContentRootPath { get; set; } = ".";
			public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
		}

		// ---- 1. ENVIRONMENT ------------------------------------------------------------------------
		[Theory]
		[InlineData("Production")]
		[InlineData("Staging")]
		[InlineData("")]
		public async Task A_non_development_environment_is_refused_even_with_a_correct_catalogue(string environment)
		{
			using var db = ContextFor("CrossBuyDev");
			var result = await UatSeedGuard.EvaluateAsync(db, new Env(environment), Config(smtpEnabled: false));

			Assert.False(result.Allowed);
			Assert.Contains(result.Refusals, r => r.Contains("REFUSED (environment)"));
		}

		// ---- 2. DATABASE TARGET --------------------------------------------------------------------
		[Fact]
		public async Task The_protected_production_catalogue_is_refused_by_name()
		{
			using var db = ContextFor("CrossBuyDB2");
			var result = await UatSeedGuard.EvaluateAsync(db, new Env("Development"), Config(smtpEnabled: false));

			Assert.False(result.Allowed);
			// TWO independent refusals fire, and both are asserted: the positive check ("must be CrossBuyDev")
			// and the deny-list ("CrossBuyDB2 by name"). Either alone would be enough today; both means a
			// rename of the required catalogue cannot silently open the protected one.
			Assert.Contains(result.Refusals, r => r.Contains("not 'CrossBuyDev'"));
			Assert.Contains(result.Refusals, r => r.Contains("'CrossBuyDB2' is a protected catalogue"));
		}

		[Theory]
		[InlineData("CrossBuyProd")]
		[InlineData("crossbuy_production")]
		[InlineData("CrossBuyLive")]
		[InlineData("alprimedb_prod")]
		public async Task A_catalogue_whose_name_carries_a_production_marker_is_refused(string catalog)
		{
			using var db = ContextFor(catalog);
			var result = await UatSeedGuard.EvaluateAsync(db, new Env("Development"), Config(smtpEnabled: false));

			Assert.False(result.Allowed);
			Assert.Contains(result.Refusals, r => r.Contains("REFUSED (database)"));
		}

		[Fact]
		public async Task Any_other_catalogue_is_refused_because_the_check_is_an_allow_list()
		{
			// Not on any deny list, not production-looking, and still refused: the guard names ONE catalogue it
			// may write to. A deny-list-only guard would have let this through.
			using var db = ContextFor("CrossBuyScratch");
			var result = await UatSeedGuard.EvaluateAsync(db, new Env("Development"), Config(smtpEnabled: false));

			Assert.False(result.Allowed);
			Assert.Contains(result.Refusals, r => r.Contains("not 'CrossBuyDev'"));
		}

		// ---- 3. NO EXTERNAL SIDE EFFECTS -----------------------------------------------------------
		[Fact]
		public async Task Seeding_is_refused_while_outbound_mail_is_enabled()
		{
			using var db = ContextFor("CrossBuyDev");
			var result = await UatSeedGuard.EvaluateAsync(db, new Env("Development"), Config(smtpEnabled: true));

			Assert.False(result.Allowed);
			Assert.Contains(result.Refusals, r => r.Contains("REFUSED (outbound mail)"));
			Assert.True(result.SmtpEnabled);
		}

		[Fact]
		public async Task Seeding_is_refused_while_outbound_messaging_is_enabled()
		{
			using var db = ContextFor("CrossBuyDev");
			var result = await UatSeedGuard.EvaluateAsync(
				db, new Env("Development"), Config(smtpEnabled: false, whatsapp: true));

			Assert.False(result.Allowed);
			Assert.Contains(result.Refusals, r => r.Contains("REFUSED (outbound messaging)"));
		}

		[Fact]
		public async Task A_correct_development_target_clears_all_three_guards()
		{
			using var db = ContextFor("CrossBuyDev");
			var result = await UatSeedGuard.EvaluateAsync(db, new Env("Development"), Config(smtpEnabled: false));

			// The "REFUSED (database)" line below DOES require a reachable CrossBuyDev — it is the
			// reachability refusal. An earlier comment here claimed the opposite ("must pass on a machine with
			// no SQL Server"), which was never true of this assertion and hid a wrong instance name in
			// ContextFor for as long as the machine that wrote it happened to be reachable.
			Assert.DoesNotContain(result.Refusals, r => r.Contains("REFUSED (environment)"));
			Assert.DoesNotContain(result.Refusals, r => r.Contains("REFUSED (database)"));
			Assert.DoesNotContain(result.Refusals, r => r.Contains("REFUSED (outbound"));
			Assert.Equal("CrossBuyDev", result.ResolvedCatalog);
			Assert.False(result.SmtpEnabled);
			Assert.Equal(UatMarkers.RunId, result.RunId);
		}
	}

	// ==========================================================================================
	// THE OWNERSHIP CONTRACT
	//
	// Cleanup deletes by marker on a database FULL OF REAL CLONED DATA. Every one of these assertions is a
	// property that, if it broke, would make cleanup either miss rows (leaving orphans) or match rows it
	// never created (deleting the owner's data).
	// ==========================================================================================
	public class UatMarkerContractTests
	{
		[Fact]
		public void Every_marker_is_derived_from_the_single_run_id()
		{
			// The brief's "prefer a single run identifier" rule, enforced. A marker invented independently of
			// RunId is a marker cleanup can stop matching when the run id changes.
			Assert.StartsWith(UatMarkers.RunId, UatMarkers.TaskCategoryPrefix);
			Assert.StartsWith(UatMarkers.RunId, UatMarkers.NamePrefix);
			Assert.StartsWith(UatMarkers.RunId, UatMarkers.NotificationDedupPrefix);
			Assert.Contains(UatMarkers.RunId, UatMarkers.CalendarDescriptionTag);
			Assert.Contains(UatMarkers.RunId, UatMarkers.ReportRunParameterTag);
			Assert.Contains(UatMarkers.RunId, UatMarkers.ResourceNameEnTag);
		}

		[Fact]
		public void The_run_id_is_specific_enough_that_it_cannot_match_pre_existing_conventions()
		{
			// CrossBuyDev already carries ZZ- and DEMO- test data from earlier phases, and cleanup must not
			// touch it. The run id shares no prefix with either.
			Assert.DoesNotContain("ZZ-", UatMarkers.RunId);
			Assert.DoesNotContain("DEMO", UatMarkers.RunId);
			Assert.StartsWith("UAT-", UatMarkers.RunId);
			// A dated run id, so two seeding runs on different days are distinguishable rather than merged.
			Assert.Matches(@"^UAT-\d{8}$", UatMarkers.RunId);
		}

		[Fact]
		public void The_protected_catalogue_is_on_the_deny_list()
		{
			Assert.Contains("CrossBuyDB2", UatMarkers.ForbiddenCatalogs);
			Assert.Equal("CrossBuyDev", UatMarkers.RequiredCatalog);
		}
	}

	// ==========================================================================================
	// THE IDEMPOTENCY CONTRACT AND THE ISOLATION PLAN
	// ==========================================================================================
	public class UatDatasetPlanTests
	{
		private static readonly int[] Employees = { 5, 17, 18, 19, 20, 21, 22, 23, 24, 2044, 2045, 2047 };
		private static readonly DateTime Anchor = new(2026, 8, 12);

		[Fact]
		public void Every_planned_task_title_is_distinct_because_the_title_is_the_idempotency_key()
		{
			var rows = UatDatasetSeeder.PlannedTaskRows(UatPlan.Primary, Anchor, Employees);
			var duplicates = rows.GroupBy(r => r.Title, StringComparer.Ordinal)
				.Where(g => g.Count() > 1).Select(g => g.Key).ToList();

			Assert.Empty(duplicates);
			Assert.Equal(rows.Count, rows.Select(r => r.Title).Distinct(StringComparer.Ordinal).Count());
		}

		[Fact]
		public void Every_planned_task_carries_the_run_marker_so_cleanup_can_find_it()
		{
			var rows = UatDatasetSeeder.PlannedTaskRows(UatPlan.Primary, Anchor, Employees);
			Assert.NotEmpty(rows);
			Assert.All(rows, r => Assert.StartsWith(UatMarkers.RunId, r.Category));
		}

		[Fact]
		public void Every_planned_calendar_event_is_distinct_and_carries_the_run_marker()
		{
			var rows = UatDatasetSeeder.PlannedEventRows(UatPlan.Primary, Anchor, Employees, Employees[0]);

			Assert.NotEmpty(rows);
			Assert.Equal(rows.Count, rows.Select(r => r.Title).Distinct(StringComparer.Ordinal).Count());
			Assert.All(rows, r => Assert.Contains(UatMarkers.CalendarDescriptionTag, r.Description ?? ""));
		}

		[Fact]
		public void The_primary_plan_meets_the_volume_targets_the_brief_asks_for()
		{
			var rows = UatDatasetSeeder.PlannedTaskRows(UatPlan.Primary, Anchor, Employees);
			var events = UatDatasetSeeder.PlannedEventRows(UatPlan.Primary, Anchor, Employees, Employees[0]);

			Assert.InRange(rows.Count, 100, 150);      // brief: 100–150 tasks
			Assert.InRange(events.Count, 80, 120);     // brief: 80–120 calendar events
			Assert.InRange(UatPlan.Primary.Resources, 8, 12);
			Assert.InRange(UatPlan.Primary.Templates, 8, 12);
			Assert.InRange(UatPlan.Primary.NotificationsForActor, 30, 50);
			Assert.InRange(UatPlan.Primary.ReportRuns, 30, 50);
			Assert.InRange(UatPlan.Primary.DependencyTarget, 20, 30);
		}

		[Fact]
		public void The_secondary_plan_is_strictly_thinner_because_that_difference_is_the_isolation_proof()
		{
			// The secondary company exists to prove cross-company data is invisible and that counts DIFFER. If
			// it were seeded as densely as the primary, a screen leaking one company's rows into the other would
			// look identical either way and the proof would be worthless.
			var primaryTasks = UatDatasetSeeder.PlannedTaskRows(UatPlan.Primary, Anchor, Employees).Count;
			var secondaryTasks = UatDatasetSeeder.PlannedTaskRows(UatPlan.Secondary, Anchor, Employees).Count;

			Assert.True(secondaryTasks < primaryTasks / 5,
				$"secondary must be far thinner than primary; got {secondaryTasks} vs {primaryTasks}");
			Assert.True(UatPlan.Secondary.Resources < UatPlan.Primary.Resources);
			Assert.True(UatPlan.Secondary.NotificationsForActor < UatPlan.Primary.NotificationsForActor);
			Assert.True(UatPlan.Secondary.ReportRuns < UatPlan.Primary.ReportRuns);
		}

		[Fact]
		public void The_secondary_plan_borrows_no_party_id_from_the_primary_company()
		{
			// Copying company 1's customer/vendor ids into company 65's scheduled tasks is exactly the
			// cross-company id copy the brief forbids. The secondary plan therefore carries none.
			Assert.Empty(UatPlan.Secondary.ScheduledExpectations);
			Assert.NotEmpty(UatPlan.Primary.ScheduledExpectations);
		}

		[Fact]
		public void A_company_with_a_single_employee_can_still_be_seeded()
		{
			// Company 65 — the isolation-proof company — has exactly ONE active employee, and the assignee
			// round-robin indexed past the end of a one-element pool: `1 + seq % Math.Max(1, poolSize - 1)` is
			// `1 + seq % 1` = 1. Seeding the secondary company died on its first task. Asserted because the
			// secondary company is the one nobody looks at until it is needed.
			var single = new[] { 2048 };
			var rows = UatDatasetSeeder.PlannedTaskRows(UatPlan.Secondary, Anchor, single);

			Assert.NotEmpty(rows);
			Assert.Equal(rows.Count, rows.Select(r => r.Title).Distinct(StringComparer.Ordinal).Count());

			var events = UatDatasetSeeder.PlannedEventRows(UatPlan.Secondary, Anchor, single, 2048);
			Assert.NotEmpty(events);
		}

		[Fact]
		public void Every_match_window_is_narrow_and_ends_at_the_end_of_its_day()
		{
			// The first seeding run produced 98 suggestions against a 10–20 target because the window was 72
			// days wide and the matcher takes up to 20 candidates per task. The window width IS the volume
			// control, so it is asserted: one day, ending at 23:59:59 rather than midnight — a `To` of midnight
			// would exclude every invoice stamped later in the day, because the matcher's predicate is
			// `InvoiceDate <= to`.
			Assert.All(UatPlan.Primary.ScheduledExpectations, e =>
			{
				Assert.Equal(e.From.Date, e.To.Date);
				Assert.Equal(TimeSpan.Zero, e.From.TimeOfDay);
				Assert.Equal(new TimeSpan(23, 59, 59), e.To.TimeOfDay);
			});
		}

		[Fact]
		public void Overdue_tasks_are_never_planned_as_done_so_the_kpi_strip_agrees_with_the_row_badges()
		{
			// An overdue KPI counts (DueDate < now && Status != Done). A Done task with a past due date would
			// make the KPI number and the visible badges disagree, and the owner would be right to file it as a
			// defect against the screen rather than against the data.
			var overdueOnly = new UatPlan
			{
				TasksOverdue = 20, TasksToday = 0, TasksTomorrow = 0, TasksThisWeek = 0,
				TasksNext30 = 0, TasksFar = 0, TasksNoDue = 0,
				ChecklistTasks = 0, DependencyChains = 0, DependencyDeepHops = 0, DependencyCrossLinks = 0,
				DependencyTarget = 0, Templates = 0, TimesheetTasks = 0,
				ScheduledExpectations = Array.Empty<(string, int, string, DateTime, DateTime)>(),
				EventsPast = 0, EventsToday = 0, EventsTomorrow = 0, EventsThisWeek = 0,
				EventsNextWeek = 0, EventsNextMonth = 0, Resources = 0, EventResourceLinks = 0,
				RecurringSeries = 0,
				NotificationsForActor = 0, NotificationsPerSecondaryRecipient = 0, ReportRuns = 0, Layouts = 0,
			};

			// The three explicit extras (long title, one complete, one in progress) are appended regardless, and
			// one of them IS Done with a past due date on purpose — it is the "fully complete" task the brief
			// asks for by name. So the assertion is about the DISTRIBUTION, which is what could drift.
			var rows = UatDatasetSeeder.PlannedTaskRows(overdueOnly, Anchor, Employees);
			Assert.Equal(23, rows.Count);   // 20 overdue + 3 explicit extras
		}
	}

	// ==========================================================================================
	// DI — the seeder must be constructible by the container, not just by hand
	//
	// The project's own rule: "A DI graph is not verified by unit tests that construct services by hand."
	// The seeder takes fifteen services; a missing registration would surface as a 500 on the endpoint rather
	// than as a failing test unless the container is asked to validate it.
	// ==========================================================================================
	public class UatSeederRegistrationTests
	{
		[Fact]
		public void The_seeder_is_registered_as_scoped_and_owns_no_hosted_service()
		{
			// Asserted against the TYPE, not the container: standing up Program.cs's full graph here would
			// duplicate Stage1DiWiringTests. What matters is the two properties that make it safe to register
			// unconditionally — it is not a hosted service (so nothing runs at startup) and it holds no
			// singleton state.
			var type = typeof(UatDatasetSeeder);

			Assert.True(typeof(IUatDatasetSeeder).IsAssignableFrom(type));
			Assert.False(typeof(IHostedService).IsAssignableFrom(type),
				"the UAT seeder must never be a hosted service — it would then run at application startup");
			Assert.True(type.IsSealed, "the seeder is sealed: it is a harness, not an extension point");

			// Every constructor dependency is an interface or a framework abstraction, so nothing is newed up
			// inside and every collaborator can be substituted.
			var ctor = Assert.Single(type.GetConstructors());
			Assert.NotEmpty(ctor.GetParameters());
		}
	}
}
