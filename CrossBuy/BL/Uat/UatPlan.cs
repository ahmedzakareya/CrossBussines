namespace CrossBuy.BL.Uat
{
	// ==========================================================================================
	// UAT DATASET — THE VOLUMES
	//
	// TWO PLANS, AND THE DIFFERENCE BETWEEN THEM IS ITSELF A TEST.
	//
	//   Primary   — the company under review. Dense enough that every column of the board scrolls, every
	//               calendar day has something on it, and pagination is reached rather than described.
	//   Secondary — a real second company, deliberately THIN. It exists to prove three things a dense
	//               company cannot: that its rows are invisible from the primary company, that the counts on
	//               every screen differ correctly, and that a sparse calendar / short report history still
	//               renders. A second dense company would prove none of them.
	//
	// The numbers are directional targets from the brief, adjusted only where a domain constraint makes the
	// target unreachable — and where that happened it is recorded at the point of adjustment, not here.
	// ==========================================================================================
	// PUBLIC rather than internal for ONE reason: the focused tests assert the primary/secondary thinness
	// invariant directly against these numbers. A test that re-declared the expected volumes would be a
	// second copy of the plan, and the copy is what drifts.
	public sealed class UatPlan
	{
		// ---- tasks, by due-date slot -----------------------------------------------------------
		public required int TasksOverdue { get; init; }
		public required int TasksToday { get; init; }
		public required int TasksTomorrow { get; init; }
		public required int TasksThisWeek { get; init; }
		public required int TasksNext30 { get; init; }
		public required int TasksFar { get; init; }
		public required int TasksNoDue { get; init; }

		// ---- task ecosystem ---------------------------------------------------------------------
		public required int ChecklistTasks { get; init; }
		public required int DependencyChains { get; init; }
		public required int DependencyDeepHops { get; init; }
		public required int DependencyCrossLinks { get; init; }
		public required int DependencyTarget { get; init; }
		public required int Templates { get; init; }
		public required int TimesheetTasks { get; init; }

		/// (expected movement type, party id, label, window start, window end).
		///
		/// THE WINDOW IS THE VOLUME CONTROL, AND IT IS ABSOLUTE ON PURPOSE.
		///
		/// The matcher takes up to TWENTY candidates per scheduled task, so a wide window turns eight
		/// scheduled tasks into a hundred suggestions — which is what the first seeding run actually produced
		/// (98 rows against a 10–20 target). The fix is not to seed fewer tasks but to ask a NARROWER
		/// question: each expectation names one day on which its party has two to four real invoices, so the
		/// matcher finds a handful of genuine candidates and records them for review instead of drowning the
		/// screen.
		///
		/// The dates are ABSOLUTE rather than relative to today because they point at PRE-EXISTING invoices in
		/// the clone, whose dates are fixed facts. A relative window would drift off them.
		///
		/// Each pair below was counted in CrossBuyDev before being listed, and each has MORE THAN ONE
		/// candidate — a single candidate auto-links instead of suggesting, which would leave the review screen
		/// empty:
		///   customer 1  · 2026-07-01 (3) · 2026-07-23 (2)
		///   customer 23 · 2026-07-02 (3) · 2026-07-07 (2)
		///   customer 4031 · 2026-07-29 (4)   customer 4032 · 2026-07-30 (4)
		///   vendor 1013 · 2026-07-30 (2)     vendor 1 · 2026-08-02 (3)
		public required (string EntityType, int PartyId, string Label, DateTime From, DateTime To)[]
			ScheduledExpectations { get; init; }

		// ---- calendar ---------------------------------------------------------------------------
		public required int EventsPast { get; init; }
		public required int EventsToday { get; init; }
		public required int EventsTomorrow { get; init; }
		public required int EventsThisWeek { get; init; }
		public required int EventsNextWeek { get; init; }
		public required int EventsNextMonth { get; init; }
		public required int Resources { get; init; }
		public required int EventResourceLinks { get; init; }
		public required int RecurringSeries { get; init; }

		// ---- platform ---------------------------------------------------------------------------
		public required int NotificationsForActor { get; init; }
		public required int NotificationsPerSecondaryRecipient { get; init; }
		public required int ReportRuns { get; init; }
		public required int Layouts { get; init; }

		// Named helpers rather than inline `new DateTime(...)`: the END of a day is 23:59:59, not midnight, and
		// the matcher's window predicate is `InvoiceDate <= hi`. A `To` of midnight would silently exclude
		// every invoice stamped later in the day.
		private static DateTime Day(int y, int m, int d) => new(y, m, d);
		private static DateTime EndOf(int y, int m, int d) => new DateTime(y, m, d).AddDays(1).AddSeconds(-1);

		public static readonly UatPlan Primary = new()
		{
			// 130 tasks. Deliberately over the 100–150 band's midpoint: the board needs 10+ cards in every
			// column AFTER the status distribution splits them three ways.
			TasksOverdue = 26, TasksToday = 12, TasksTomorrow = 10, TasksThisWeek = 20,
			TasksNext30 = 34, TasksFar = 10, TasksNoDue = 18,

			ChecklistTasks = 12,          // × 3–5 lines = 48+ checklist items
			DependencyChains = 3,         // 3 chains of 4 = 9 edges
			DependencyDeepHops = 6,       // a 6-hop path
			DependencyCrossLinks = 6,
			DependencyTarget = 26,        // the cap; the shapes above produce slightly more candidates
			Templates = 10,
			TimesheetTasks = 22,          // × 2–3 days = 50+ timesheet entries, 8+ employees, 28 dates

			// Eight scheduled tasks, ~19 expected suggestions — inside the brief's 10–20 band.
			ScheduledExpectations = new[]
			{
				("SalesInvoice", 1, "عميل رقم ١ — ١ يوليو", Day(2026, 7, 1), EndOf(2026, 7, 1)),
				("SalesInvoice", 1, "عميل رقم ١ — ٢٣ يوليو", Day(2026, 7, 23), EndOf(2026, 7, 23)),
				("SalesInvoice", 23, "عميل نقدي — ٢ يوليو", Day(2026, 7, 2), EndOf(2026, 7, 2)),
				("SalesInvoice", 23, "عميل نقدي — ٧ يوليو", Day(2026, 7, 7), EndOf(2026, 7, 7)),
				("SalesInvoice", 4031, "عميل تزامن D6 — ٢٩ يوليو", Day(2026, 7, 29), EndOf(2026, 7, 29)),
				("SalesInvoice", 4032, "عميل تزامن D6 — ٣٠ يوليو", Day(2026, 7, 30), EndOf(2026, 7, 30)),
				("PurchaseInvoice", 1013, "مورّد ZZ-STEP0 — ٣٠ يوليو", Day(2026, 7, 30), EndOf(2026, 7, 30)),
				("PurchaseInvoice", 1, "مورد الطاقة — ٢ أغسطس", Day(2026, 8, 2), EndOf(2026, 8, 2)),
			},

			// 95 from the bands + 7 explicit same-day scenarios = 102 events.
			EventsPast = 30, EventsToday = 8, EventsTomorrow = 8, EventsThisWeek = 14,
			EventsNextWeek = 15, EventsNextMonth = 20,
			Resources = 10,
			EventResourceLinks = 28,
			RecurringSeries = 6,

			NotificationsForActor = 40,
			NotificationsPerSecondaryRecipient = 7,
			ReportRuns = 40,
			Layouts = 9,
		};

		public static readonly UatPlan Secondary = new()
		{
			// Thin ON PURPOSE. Every count here is small enough that a screen showing the primary company's
			// figures while claiming to be the secondary one is immediately obvious.
			TasksOverdue = 3, TasksToday = 2, TasksTomorrow = 1, TasksThisWeek = 3,
			TasksNext30 = 3, TasksFar = 1, TasksNoDue = 2,

			ChecklistTasks = 3,
			DependencyChains = 1,
			DependencyDeepHops = 2,
			DependencyCrossLinks = 0,
			DependencyTarget = 5,
			Templates = 2,
			TimesheetTasks = 3,

			// The secondary company has its OWN parties or none. Empty rather than borrowed: pointing a
			// company-65 task at company-1's customer 23 is exactly the cross-company id copy the brief
			// forbids, and the matcher's own company predicate would find nothing anyway.
			ScheduledExpectations = Array.Empty<(string, int, string, DateTime, DateTime)>(),

			EventsPast = 3, EventsToday = 1, EventsTomorrow = 1, EventsThisWeek = 2,
			EventsNextWeek = 1, EventsNextMonth = 1,
			Resources = 3,
			EventResourceLinks = 3,
			RecurringSeries = 1,

			NotificationsForActor = 5,
			NotificationsPerSecondaryRecipient = 0,
			ReportRuns = 6,
			Layouts = 2,
		};
	}
}
