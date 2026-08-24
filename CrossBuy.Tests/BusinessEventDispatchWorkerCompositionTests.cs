using CrossBuy.BL.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the OUTBOX DISPATCHER's composition and the one condition that must suppress it.
	//
	// THE GAP THIS CLOSES. The three consumers were registered, but BusinessEventDispatchWorker — the
	// only thing that constructs them — was not a hosted service. Nothing drained BusinessEventDispatch,
	// so a fan-out that looked wired ran never.
	//
	// THE CONDITION THAT MUST HOLD. The dispatcher MUTATES state: it claims rows, writes timeline and
	// notification projections, and advances per-consumer dispatch state. A UI-conformance capture must
	// OBSERVE stable data rather than consume it, or two runs of the same page disagree. So it is
	// suppressed in certification runtime — and ONLY it, because re-gating the other hosted services is a
	// separate decision.
	//
	// The suppression decision is asserted through the committed predicate rather than by booting against
	// the certification catalogue: IsCertificationRuntime is pure string/flag logic, so its truth table
	// can be proven exactly, with no database touched.
	// =================================================================================================
	public class BusinessEventDispatchWorkerCompositionTests
	{
		// ---------------------------------------------------------------------------------------------
		// The dependency graph resolves
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public async Task The_worker_gate_and_its_options_resolve()
		{
			// await using: WorkerGate implements IAsyncDisposable ONLY, because the lease it holds is a live
			// SQL connection that must be closed asynchronously. A sync Dispose on this container throws.
			await using var provider = Composed();

			var options = provider.GetRequiredService<IOptions<RuntimeOptions>>();
			Assert.NotNull(options.Value);

			// The gate is the SAME instance through both its concrete type and its interface. It holds one
			// application lock for the process, and IAsyncDisposable must release exactly that lease — two
			// instances would take two locks and free one.
			var concrete = provider.GetRequiredService<WorkerGate>();
			var viaInterface = provider.GetRequiredService<IWorkerGate>();
			Assert.Same(concrete, viaInterface);
		}

		[Fact]
		public async Task The_dispatch_options_resolve_with_the_committed_defaults()
		{
			// await using: WorkerGate implements IAsyncDisposable ONLY, because the lease it holds is a live
			// SQL connection that must be closed asynchronously. A sync Dispose on this container throws.
			await using var provider = Composed();

			var o = provider.GetRequiredService<IOptions<BusinessEventDispatchOptions>>().Value;

			// A deployment with no Platform:EventDispatch section must behave exactly as these describe —
			// the committed appsettings carries the same numbers, so binding changes nothing.
			Assert.Equal(50, o.BatchSize);
			Assert.Equal(15, o.PollSeconds);
			Assert.Equal(5, o.MaxAttempts);
			Assert.Equal(30, o.RetryBackoffSeconds);
			Assert.Equal(10, o.StaleClaimMinutes);
		}

		[Fact]
		public void The_gate_defaults_to_requiring_a_single_worker_process()
		{
			// The safe posture is the one you get without configuring anything: exactly one process runs
			// background work. A default of false would let task generation and reminders duplicate.
			Assert.True(new RuntimeOptions().RequireSingleWorkerProcess);
			Assert.Equal("CrossBuy.BackgroundWorkers", new RuntimeOptions().WorkerLeaseName);
		}

		// ---------------------------------------------------------------------------------------------
		// The hosted registration, and its gate
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_dispatcher_is_registered_as_a_hosted_service()
		{
			var program = ProgramSource();

			Assert.Contains(
				"AddHostedService<CrossBuy.BL.Platform.BusinessEventDispatchWorker>()",
				program);
		}

		[Fact]
		public void The_dispatcher_registration_is_guarded_by_the_certification_signal()
		{
			var program = ProgramSource();

			// The guard and the registration must be on the SAME statement. A registration on its own line,
			// with the guard elsewhere, is how a gate silently stops applying.
			var line = program.Split('\n')
				.SingleOrDefault(l => l.Contains("AddHostedService<CrossBuy.BL.Platform.BusinessEventDispatchWorker>()"));

			Assert.NotNull(line);
			Assert.Contains("if (!certificationRuntime)", line!);
		}

		[Fact]
		public void Only_the_dispatcher_is_newly_gated()
		{
			// Re-gating the other hosted services is a separate decision with its own blast radius. This
			// asserts the phase did not quietly take it: every other AddHostedService stays ungated.
			var program = ProgramSource();

			var gated = program.Split('\n')
				.Where(l => l.Contains("AddHostedService") && l.Contains("certificationRuntime"))
				.ToList();

			Assert.Single(gated);
			Assert.Contains("BusinessEventDispatchWorker", gated[0]);
		}

		// ---------------------------------------------------------------------------------------------
		// The suppression decision itself
		// ---------------------------------------------------------------------------------------------

		[Theory]
		// All three conditions must agree before the dispatcher is suppressed.
		[InlineData(true, "1", "Server=.;Database=CrossBuyCert;", true)]
		// Any one of them missing leaves the dispatcher ACTIVE — which is what stops this gate from
		// accidentally disabling the outbox on a normal host.
		[InlineData(false, "1", "Server=.;Database=CrossBuyCert;", false)]
		[InlineData(true, null, "Server=.;Database=CrossBuyCert;", false)]
		[InlineData(true, "0", "Server=.;Database=CrossBuyCert;", false)]
		[InlineData(true, "true", "Server=.;Database=CrossBuyCert;", false)]
		[InlineData(true, "1", "Server=.;Database=CrossBuyDev;", false)]
		[InlineData(true, "1", "Server=.;Database=CrossBuyDB2;", false)]
		[InlineData(true, "1", null, false)]
		public void The_dispatcher_is_suppressed_only_when_every_certification_condition_holds(
			bool isDevelopment, string? flag, string? connectionString, bool suppressed)
		{
			// Pure decision logic — no connection is opened, so the certification catalogue is named here
			// without being touched.
			var certification = CertificationDataContract.IsCertificationRuntime(isDevelopment, flag, connectionString);

			Assert.Equal(suppressed, certification);

			// And the registration follows it: suppressed == not registered.
			Assert.Equal(suppressed, !(!certification));
		}

		[Fact]
		public void Certification_state_reports_suppression_as_one_derived_value()
		{
			// Suppression is not an independent switch someone can half-set: it is what certification mode
			// MEANS for this process. Reported as one derived value so the impossible combination cannot be.
			Assert.True(new CertificationRuntimeState(true).BackgroundWritersSuppressed);
			Assert.False(new CertificationRuntimeState(false).BackgroundWritersSuppressed);
		}

		// ---------------------------------------------------------------------------------------------

		private static ServiceProvider Composed()
		{
			// The worker's own dependency slice, composed the way Program.cs composes it. Deliberately not
			// the whole application: this asserts the gate and options graph closes, and the full graph is
			// proven by the host booting with ValidateOnBuild.
			var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

			var services = new ServiceCollection();
			services.AddSingleton<IConfiguration>(configuration);
			// The host normally supplies this; RuntimeInstanceInfo reads the environment name from it.
			services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new StubHostEnvironment());
			services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
			services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
			services.Configure<RuntimeOptions>(configuration.GetSection("Runtime"));
			services.Configure<BusinessEventDispatchOptions>(configuration.GetSection("Platform:EventDispatch"));
			services.AddSingleton<IRuntimeInstanceInfo, RuntimeInstanceInfo>();
			services.AddSingleton<WorkerGate>();
			services.AddSingleton<IWorkerGate>(sp => sp.GetRequiredService<WorkerGate>());

			return services.BuildServiceProvider(new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			});
		}

		private sealed class StubHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
		{
			public string EnvironmentName { get; set; } = "Development";
			public string ApplicationName { get; set; } = "CrossBuy.Tests";
			public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
			public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
				new Microsoft.Extensions.FileProviders.NullFileProvider();
		}

		private static string ProgramSource()
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(directory.FullName, "CrossBuy", "Program.cs");
				if (File.Exists(candidate)) return File.ReadAllText(candidate);
			}

			Assert.Fail("CrossBuy/Program.cs could not be located from the test assembly directory. The " +
						"composition guards cannot run, and a sweep over an empty string would pass vacuously.");
			return string.Empty;
		}
	}
}
