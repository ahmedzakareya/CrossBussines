using CrossBuy.Models.Context;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.TestSupport
{
	// TAB-5 test support — a disposable SQL Server probe database for the committed UAT acceptance suite.
	//
	// WHY A TAB-5-OWNED FIXTURE INSTEAD OF THE EXISTING ONE. PosInventoryManufacturingAcceptanceTests was
	// written against CrossBuy.Tests.SqlServer.SqlServerFixture, which is untracked. Landing that fixture is not
	// possible without also landing uncommitted PRODUCTION source: it exposes a Comm dispatch-store factory over
	// `CrossBuy.BL.Comm.ICommMessageDispatchStore` and `SqlCommMessageDispatchStore`, and at HEAD there are ZERO
	// files under CrossBuy/BL/Comm — those types do not exist in the repository yet. Absorbing Communication
	// production code to make a UAT test compile is exactly the wrong trade, so this provides only the five
	// things the acceptance suite actually needs:
	//
	//   scratch database creation · the schema the test requires · a connection string · cleanup ·
	//   xUnit collection serialisation
	//
	// AND NOTHING ELSE. In particular it fakes NO application service. Every service the acceptance suite
	// exercises (StockService, ManufService, JournalEntryService) is the real registered implementation, built
	// over a context this fixture hands out.
	//
	// IT ALSO OWNS NO SHARED DATABASE. The original fixture kept one long-lived database per test run and had to
	// offer a fingerprint so schema-generating families could prove they had not polluted it. Here every family
	// gets its own probe and drops it, so there is nothing shared to pollute and no fingerprint to check.
	public sealed class UatSqlProbeFixture : IAsyncLifetime
	{
		public const string ConnectionStringVariable = "CROSSBUY_TEST_SQL";

		// Defence in depth, not the primary control. The PRIMARY control is structural: CreateProbeDatabaseAsync
		// always overwrites Initial Catalog with a freshly generated CrossBuyProbe_* name, so this fixture cannot
		// address a real database even if the environment variable names one. This list refuses obvious mistakes
		// early, with a clear message, rather than silently ignoring them.
		//
		// The authoritative shared list lives in SqlEvidenceGuards (untracked at the time of writing). This is a
		// deliberate minimal duplicate so the committed suite is self-contained; collapse the two when that lands.
		private static readonly string[] RefusedCatalogs =
			{ "CrossBuy", "CrossBuyDB", "CrossBuyDB2", "CrossBuyDev", "CrossBuyCert" };

		public string? SkipReason { get; private set; }
		public bool Available => SkipReason == null;

		private string _masterConnectionString = "";

		/// A database this fixture created and will drop, plus the connection string that reaches it.
		public sealed record ProbeDatabase(string Name, string ConnectionString);

		public Task InitializeAsync()
		{
			var raw = Environment.GetEnvironmentVariable(ConnectionStringVariable);
			if (string.IsNullOrWhiteSpace(raw))
			{
				SkipReason =
					$"SQL Server UAT acceptance needs the {ConnectionStringVariable} environment variable — a " +
					"connection string to a SQL Server INSTANCE this fixture may create a scratch database on, " +
					@"e.g. ""Server=localhost;Integrated Security=true;TrustServerCertificate=true"". " +
					"The suite is SKIPPED rather than run against another database.";
				return Task.CompletedTask;
			}

			SqlConnectionStringBuilder builder;
			try { builder = new SqlConnectionStringBuilder(raw); }
			catch (Exception ex)
			{
				SkipReason = $"{ConnectionStringVariable} is not a valid connection string: {ex.Message}";
				return Task.CompletedTask;
			}

			var named = builder.InitialCatalog;
			if (!string.IsNullOrWhiteSpace(named)
				&& RefusedCatalogs.Any(c => string.Equals(c, named, StringComparison.OrdinalIgnoreCase)))
			{
				SkipReason =
					$"{ConnectionStringVariable} names catalogue '{named}', which is a real CrossBuy database. " +
					"Point it at the INSTANCE and let the fixture create its own scratch database.";
				return Task.CompletedTask;
			}

			builder.InitialCatalog = "master";
			_masterConnectionString = builder.ConnectionString;
			return Task.CompletedTask;
		}

		public Task DisposeAsync() => Task.CompletedTask;

		/// Create a database named CrossBuyProbe_<prefix>_<guid> and give it the schema the EF model describes.
		public async Task<ProbeDatabase> CreateProbeDatabaseAsync(string prefix)
		{
			if (!Available) throw new InvalidOperationException("the fixture is unavailable: " + SkipReason);

			var name = $"CrossBuyProbe_{prefix}_{Guid.NewGuid():N}";
			var builder = new SqlConnectionStringBuilder(_masterConnectionString) { InitialCatalog = name };
			var probe = new ProbeDatabase(name, builder.ConnectionString);

			await ExecuteOnMasterAsync($"CREATE DATABASE [{name}];");
			try { await ApplyModelSchemaAsync(probe); }
			catch { await DropProbeDatabaseAsync(probe); throw; }
			return probe;
		}

		public async Task DropProbeDatabaseAsync(ProbeDatabase probe)
		{
			try
			{
				SqlConnection.ClearAllPools();
				await ExecuteOnMasterAsync(
					$"IF DB_ID(N'{probe.Name}') IS NOT NULL BEGIN " +
					$"ALTER DATABASE [{probe.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
					$"DROP DATABASE [{probe.Name}]; END");
			}
			catch
			{
				// A leaked probe is inert and named so it is obvious. Throwing here would replace the real test
				// result with a teardown error, which is how a genuine failure gets lost.
			}
		}

		/// A context bound to the probe, scoped to one company so the global company filters resolve.
		public CrossDbContext ContextFor(ProbeDatabase probe, int companyId = 1)
		{
			var holder = new CrossBuy.BL.Platform.CompanyScopeHolder();
			holder.Set(companyId, null);
			return new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>()
				.UseSqlServer(probe.ConnectionString)
				.EnableSensitiveDataLogging()
				.Options, holder);
		}

		// ---- schema ---------------------------------------------------------------------------------------
		//
		// The schema comes from the SAME EF model production runs, via GenerateCreateScript, so a column the
		// model has and a hand-written test DDL would have forgotten cannot go missing. Two deliberate details:
		//
		//  * FOREIGN KEYS ARE STRIPPED. Applying the model's own DDL to SQL Server fails on
		//    FK_Branches_CountriesLookup_CountryID — "may cause cycles or multiple cascade paths". That is
		//    pre-existing model/engine drift, not something this fixture can fix, and the acceptance suite
		//    asserts quantities, values and company scoping, none of which need the lookup FK graph. Declared
		//    rather than quietly worked around.
		//  * BATCHES ARE APPLIED ONE AT A TIME and "already exists" is tolerated, so the script cannot abort
		//    half-way and leave a partially built database that fails later for a confusing reason.
		private async Task ApplyModelSchemaAsync(ProbeDatabase probe)
		{
			string script;
			using (var model = ContextFor(probe)) script = StripForeignKeys(model.Database.GenerateCreateScript());

			await using var connection = new SqlConnection(probe.ConnectionString);
			await connection.OpenAsync();
			foreach (var batch in SplitScript(script))
			{
				try
				{
					await using var cmd = new SqlCommand(batch, connection) { CommandTimeout = 300 };
					await cmd.ExecuteNonQueryAsync();
				}
				catch (SqlException ex) when (IsAlreadyExists(ex)) { /* idempotent by intent */ }
			}
		}

		private static string StripForeignKeys(string script)
		{
			var noInline = System.Text.RegularExpressions.Regex.Replace(
				script,
				@",\s*CONSTRAINT \[FK_[^\]]+\] FOREIGN KEY[^,\r\n]*(\([^)]*\))?\s*REFERENCES[^,\r\n]*(\([^)]*\))?(\s+ON DELETE [A-Z ]+)?(\s+ON UPDATE [A-Z ]+)?",
				string.Empty,
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);

			return System.Text.RegularExpressions.Regex.Replace(
				noInline,
				@"ALTER TABLE[^;]*ADD CONSTRAINT \[FK_[^\]]+\][^;]*;",
				string.Empty,
				System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
		}

		private static IEnumerable<string> SplitScript(string script)
		{
			var batches = System.Text.RegularExpressions.Regex.Split(
				script, @"^\s*GO\s*;?\s*$",
				System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			foreach (var b in batches)
				if (!string.IsNullOrWhiteSpace(b)) yield return b;
		}

		private static bool IsAlreadyExists(SqlException ex) =>
			ex.Number is 2714 or 1913 or 15530
			|| ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
			|| ex.Message.Contains("There is already an object", StringComparison.OrdinalIgnoreCase);

		private async Task ExecuteOnMasterAsync(string sql)
		{
			await using var connection = new SqlConnection(_masterConnectionString);
			await connection.OpenAsync();
			await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 300 };
			await cmd.ExecuteNonQueryAsync();
		}

		/// How many probes this fixture's naming scheme still has on the instance. Used by the cleanup assertion.
		public async Task<int> CountProbesAsync(string prefix)
		{
			await using var connection = new SqlConnection(_masterConnectionString);
			await connection.OpenAsync();
			await using var cmd = new SqlCommand(
				"SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p", connection);
			cmd.Parameters.AddWithValue("@p", $"CrossBuyProbe_{prefix}_%");
			return (int)(await cmd.ExecuteScalarAsync() ?? 0);
		}
	}

	[CollectionDefinition(UatSqlProbeCollection.Name)]
	public sealed class UatSqlProbeCollection : ICollectionFixture<UatSqlProbeFixture>
	{
		// The acceptance suite asserts stock balances and locking behaviour, so its tests must not run in
		// parallel against one another. Sharing a collection serialises them.
		public const string Name = "uat-sql-probe";
	}
}
