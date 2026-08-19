using CrossBuy.Models.Context;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Uat
{
	// ==========================================================================================
	// UAT DATASET SEEDER — CONTRACTS, MARKERS AND THE THREE GUARDS
	//
	// WHAT THIS IS FOR
	//
	//   Populating CrossBuyDev with a dense, interconnected, believable dataset so every newly built
	//   screen can be exercised by hand before Final UI Certification. It is a TEST HARNESS, not a
	//   product feature: nothing in the product calls it, it is registered but only reachable through a
	//   [DevOnly] controller, and it runs only when a human asks it to.
	//
	// WHY THE GUARDS ARE IN THIS FILE AND NOT IN THE CONTROLLER
	//
	//   A [DevOnly] attribute on a controller is one gate, and it is the gate that can be forgotten the
	//   next time somebody adds an endpoint. The refusals that MATTER — the environment, the database
	//   name, and outbound mail — belong to the seeder itself, so every future caller inherits them:
	//
	//     1. ENVIRONMENT.  Not Development -> refuse. There is no override flag, deliberately: a flag is
	//        the thing that gets set in a deployment script by accident.
	//     2. DATABASE NAME.  The catalogue actually resolved by the running DbContext must be
	//        `CrossBuyDev`. CrossBuyDB2 is refused BY NAME, and so is any catalogue whose name carries a
	//        production marker, because "it happened to be pointed somewhere else" is exactly how a
	//        development tool damages real data. Note this reads the LIVE connection, not configuration —
	//        configuration is what somebody thinks is in force; the connection is what is.
	//     3. OUTBOUND MAIL.  Smtp:Enabled must be false. Seeding creates calendar events and
	//        notifications, and CrossBuyDev is a one-way clone that inherits CrossBuyDB2's CommMessages
	//        outbox INCLUDING rows still Queued (see appsettings.Development.json). A seeder that ran with
	//        the dispatcher live could post somebody else's queued mail. This is verified BEFORE the first
	//        write, not after.
	//
	//   All three are reported as a preflight so a refusal names its reason instead of being a 404.
	//
	// WHY MARKERS AND NOT TIMESTAMPS
	//
	//   Cleanup must be able to delete exactly what this seeder made and nothing else, on a database that
	//   is a CLONE FULL OF REAL DATA. A "created after X" rule would sweep up anything the owner did while
	//   testing. So every row this seeder writes carries a deterministic marker in a column that is part of
	//   the row's own meaning, and every child row is owned transitively through its parent. The markers
	//   are declared ONCE, here, and both the seeder and the cleanup read them from this class — two copies
	//   of an ownership rule is how a cleanup deletes the wrong thing.
	// ==========================================================================================

	public static class UatMarkers
	{
		/// The single run identifier. One value, so every marker below is derived rather than invented.
		public const string RunId = "UAT-20260812";

		/// TaskItem.Category — "UAT-20260812 · <business area>". A marker AND the chip the board draws, so
		/// the data is visibly UAT data on screen rather than only in the database.
		public const string TaskCategoryPrefix = RunId + " · ";

		/// TaskTemplate.Name and ReportTemplate.Name. Both tables carry a unique index on (company, name),
		/// which makes the marker double as the idempotency key — the database refuses a second copy.
		public const string NamePrefix = RunId + " · ";

		/// CalendarEvent.Description ends with this. Title and Location stay realistic; a calendar subject
		/// reading "UAT-20260812 Weekly sales review" would defeat the point of testing realistic layout.
		public const string CalendarDescriptionTag = "[" + RunId + "]";

		/// CalendarResource.Name prefix. Resource names are drawn as row headers in /Calendar/ResourceView,
		/// so the prefix is short; the full run id rides in NameEn where the grid does not show it.
		public const string ResourceNamePrefix = "UAT-";
		public const string ResourceNameEnTag = "[" + RunId + "]";

		/// Notification.DedupKey. Already an idempotency key by design, so it is the natural marker.
		public const string NotificationDedupPrefix = RunId + ":";

		/// ReportRun.ParametersJson carries this property. A run row has no name and no marker column;
		/// its parameter set is its own description, and a uatRunId inside it is honest about what the run was.
		public const string ReportRunParameterTag = "\"uatRunId\":\"" + RunId + "\"";

		/// Employee.Email domain. Chosen over a name prefix so a seeded person still LOOKS like a person in
		/// the assignee picker and the attendee list — which is what those screens are being tested for.
		public const string EmployeeEmailDomain = "@uat.crossbuy.local";

		/// The one catalogue this seeder may ever write to.
		public const string RequiredCatalog = "CrossBuyDev";

		/// Refused by name. CrossBuyDB2 is the production catalogue the governed pipeline already refuses
		/// (governance/tools/apply-sql-slices.ps1); it is refused here too rather than relying on the
		/// positive check alone, so a rename of the required catalogue cannot silently open it.
		public static readonly string[] ForbiddenCatalogs = { "CrossBuyDB2", "alprimedb_prod" };
	}

	/// The outcome of the preflight. Reported rather than thrown so a caller can show the owner exactly
	/// which guard refused and what it saw.
	public sealed class UatSeedPreflight
	{
		public required bool Allowed { get; init; }
		public required string EnvironmentName { get; init; }
		public required string ResolvedCatalog { get; init; }
		public required string ResolvedServer { get; init; }
		public required bool SmtpEnabled { get; init; }
		public required bool WhatsAppEnabled { get; init; }
		public required string RunId { get; init; }
		public IReadOnlyList<string> Refusals { get; init; } = Array.Empty<string>();
		public IReadOnlyList<string> Confirmations { get; init; } = Array.Empty<string>();
	}

	/// A seed / count / cleanup outcome. Counts are per domain so the owner can reconcile them against the
	/// volume targets without running SQL.
	public sealed class UatSeedReport
	{
		public required string RunId { get; init; }
		public required string Mode { get; init; }
		public required bool Ok { get; init; }
		public UatSeedPreflight? Preflight { get; init; }

		/// domain -> count. For a seed run these are the rows PRESENT after the run (not created by it),
		/// so running twice and comparing the two reports is a straight idempotency proof.
		public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

		/// domain -> rows this invocation actually created. A second run should report zero everywhere.
		public IReadOnlyDictionary<string, int> Created { get; init; } = new Dictionary<string, int>();

		public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
		public int DurationMs { get; init; }
	}

	public interface IUatDatasetSeeder
	{
		/// Runs the three guards and reports what they saw. Writes nothing.
		Task<UatSeedPreflight> PreflightAsync(CancellationToken ct = default);

		/// Idempotent. Creates only what its markers say is missing.
		Task<UatSeedReport> SeedAsync(CancellationToken ct = default);

		/// Counts the marked rows. Writes nothing.
		Task<UatSeedReport> CountsAsync(CancellationToken ct = default);

		/// Deletes ONLY marked rows, in FK order. `domain` limits it to one domain so cleanup can be proven
		/// on a disposable subset without destroying the dataset under review.
		Task<UatSeedReport> CleanupAsync(string? domain = null, CancellationToken ct = default);
	}

	// ==========================================================================================
	// THE GUARDS
	// ==========================================================================================
	public static class UatSeedGuard
	{
		public static async Task<UatSeedPreflight> EvaluateAsync(
			CrossDbContext db, IWebHostEnvironment env, IConfiguration config, CancellationToken ct = default)
		{
			ArgumentNullException.ThrowIfNull(db);
			ArgumentNullException.ThrowIfNull(env);
			ArgumentNullException.ThrowIfNull(config);

			var refusals = new List<string>();
			var confirmations = new List<string>();

			// ---- 1. environment ------------------------------------------------------------------
			if (!env.IsDevelopment())
				refusals.Add($"REFUSED (environment): ASPNETCORE_ENVIRONMENT is '{env.EnvironmentName}', not 'Development'. " +
							 "The UAT seeder writes only in Development and has no override.");
			else
				confirmations.Add($"environment = Development ('{env.EnvironmentName}')");

			// ---- 2. database name, read from the LIVE connection ---------------------------------
			//
			// GetDbConnection().Database is what the context will actually open. Reading the configured
			// string instead would prove what somebody INTENDED, which is the thing that goes wrong.
			var connection = db.Database.GetDbConnection();
			string catalog = connection.Database ?? "";
			string server = connection.DataSource ?? "";

			// A connection that has never been opened can report an empty catalogue; fall back to parsing
			// its own connection string (still the live one, not configuration) rather than passing on nothing.
			if (string.IsNullOrWhiteSpace(catalog))
			{
				try
				{
					var parsed = new SqlConnectionStringBuilder(connection.ConnectionString);
					catalog = parsed.InitialCatalog;
					if (string.IsNullOrWhiteSpace(server)) server = parsed.DataSource;
				}
				catch (ArgumentException) { /* leave it empty — the check below refuses an empty catalogue */ }
			}

			if (!string.Equals(catalog, UatMarkers.RequiredCatalog, StringComparison.OrdinalIgnoreCase))
				refusals.Add($"REFUSED (database): the live connection resolves to catalogue '{catalog}', " +
							 $"not '{UatMarkers.RequiredCatalog}'.");
			else
				confirmations.Add($"catalogue = {catalog} on {server}");

			foreach (var forbidden in UatMarkers.ForbiddenCatalogs)
				if (catalog.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
					refusals.Add($"REFUSED (database): '{forbidden}' is a protected catalogue and is refused by name.");

			// A name that merely LOOKS production-like is refused too. Cheap, and it costs nothing on a
			// database legitimately called CrossBuyDev.
			if (catalog.Contains("prod", StringComparison.OrdinalIgnoreCase)
				|| catalog.Contains("live", StringComparison.OrdinalIgnoreCase))
				refusals.Add($"REFUSED (database): catalogue '{catalog}' carries a production marker in its name.");

			// ---- 3. outbound integrations --------------------------------------------------------
			//
			// Verified BEFORE any write, because the risk is not what the seeder sends — it sends nothing —
			// but what a LIVE dispatcher would drain out of the outbox this clone inherited.
			bool smtpEnabled = config.GetValue("Smtp:Enabled", true);
			if (smtpEnabled)
				refusals.Add("REFUSED (outbound mail): Smtp:Enabled is true. CrossBuyDev inherits CrossBuyDB2's " +
							 "CommMessages outbox including Queued rows, so the dispatcher must be off before seeding.");
			else
				confirmations.Add("Smtp:Enabled = false — the dispatcher stops at its SMTP check and claims nothing");

			// WhatsApp / external messaging: absent configuration means absent integration, which is the
			// state we want. Reported either way so "nothing was triggered" is an observation, not a hope.
			bool whatsapp = config.GetValue("WhatsApp:Enabled", false);
			if (whatsapp)
				refusals.Add("REFUSED (outbound messaging): WhatsApp:Enabled is true.");
			else
				confirmations.Add("WhatsApp:Enabled = false / unconfigured");

			// Prove the catalogue is actually reachable, so a later failure is not mistaken for a guard.
			if (refusals.Count == 0)
			{
				bool reachable = await db.Database.CanConnectAsync(ct);
				if (!reachable) refusals.Add($"REFUSED (database): '{catalog}' is not reachable.");
				else confirmations.Add("connection verified");
			}

			return new UatSeedPreflight
			{
				Allowed = refusals.Count == 0,
				EnvironmentName = env.EnvironmentName,
				ResolvedCatalog = catalog,
				ResolvedServer = server,
				SmtpEnabled = smtpEnabled,
				WhatsAppEnabled = whatsapp,
				RunId = UatMarkers.RunId,
				Refusals = refusals,
				Confirmations = confirmations,
			};
		}
	}
}
