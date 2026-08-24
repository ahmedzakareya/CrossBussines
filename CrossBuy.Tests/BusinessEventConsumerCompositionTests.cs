using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the OUTBOX FAN-OUT COMPOSITION — that every consumer the kernel dispatches to actually runs.
	//
	// THE DEFECT THIS GUARDS. BusinessEventConsumers.Registered named three consumers while Program.cs
	// registered none of them, then only the AI one. A dispatch row is created per REGISTERED NAME inside
	// the business transaction, so a name with no DI implementation accumulates rows nothing drains —
	// BusinessEventDispatchWorker logs an error per row and the outbox grows silently. The inverse is just
	// as bad: an implementation with no name is never dispatched to at all, so it looks wired and does
	// nothing.
	//
	// Both directions are asserted, because each failed independently in this codebase.
	//
	// WHAT THESE DO NOT TEST. Claim/retry/idempotency/failure-isolation semantics belong to the dispatch
	// store and worker, and have their own suites. Composition is the only subject here.
	// =================================================================================================
	public class BusinessEventConsumerCompositionTests
	{
		// The name → implementation contract. Kept explicit rather than derived by string convention: a
		// convention would silently "pass" a renamed class by matching a name that no longer exists.
		private static readonly (string Name, Type Implementation)[] Expected =
		{
			(BusinessEventConsumers.TimelineProjection, typeof(TimelineProjectionConsumer)),
			(BusinessEventConsumers.NotificationProjection, typeof(NotificationProjectionConsumer)),
			(BusinessEventConsumers.AiProjection, typeof(CrossBuy.BL.Platform.Ai.AiProjectionConsumer)),
		};

		[Fact]
		public void Every_registered_consumer_name_has_a_di_implementation()
		{
			// The direction that was broken: three names, nothing registered.
			var program = ProgramSource();

			foreach (var name in BusinessEventConsumers.Registered)
			{
				var expected = Expected.SingleOrDefault(e => e.Name == name);
				Assert.False(expected.Implementation == null,
					$"'{name}' is in BusinessEventConsumers.Registered but this test knows no implementation " +
					"for it. Either the consumer was added without a registration, or this list is stale — " +
					"both are the defect, not a test problem.");

				Assert.Contains(
					$"<CrossBuy.BL.Platform.IBusinessEventConsumer, {expected.Implementation!.FullName}>",
					program);
			}
		}

		[Fact]
		public void Every_registered_implementation_is_a_name_the_kernel_dispatches_to()
		{
			// The inverse direction: a consumer wired in DI but absent from Registered never receives a
			// dispatch row, so it is inert while looking active.
			foreach (var (name, _) in Expected)
				Assert.Contains(name, BusinessEventConsumers.Registered);
		}

		[Fact]
		public void All_three_consumers_are_registered_and_none_was_lost()
		{
			var program = ProgramSource();

			Assert.Equal(3, BusinessEventConsumers.Registered.Length);
			Assert.Equal(3, BusinessEventConsumers.Registered.Distinct(StringComparer.Ordinal).Count());

			// Counted, not just probed: a future edit that replaces one registration with another would
			// still satisfy three individual Contains assertions.
			var registrations = program.Split("CrossBuy.BL.Platform.IBusinessEventConsumer, ").Length - 1;
			Assert.Equal(3, registrations);
		}

		[Fact]
		public void Each_consumer_declares_the_name_it_is_registered_under()
		{
			// The Consumer property is what the worker matches a dispatch row against. A class registered
			// as one name while declaring another would drain nothing and log an error per row.
			foreach (var (name, implementation) in Expected)
			{
				Assert.True(typeof(IBusinessEventConsumer).IsAssignableFrom(implementation),
					$"{implementation.Name} must implement IBusinessEventConsumer");

				var property = implementation.GetProperty(nameof(IBusinessEventConsumer.Consumer));
				Assert.NotNull(property);

				// Read without constructing: these consumers take database and permission dependencies, and
				// the declared name is a constant expression, so the source is the honest place to check it.
				var source = ConsumerSource(implementation.Name);
				Assert.Contains($"BusinessEventConsumers.{NameOf(name)}", source);
			}
		}

		[Fact]
		public void The_notification_consumers_mapper_is_registered()
		{
			// NotificationProjectionConsumer's only dependency HEAD did not already provide. Without it the
			// consumer cannot be constructed at all, and ValidateOnBuild refuses to start the host.
			Assert.Contains(
				"<CrossBuy.BL.Platform.IBusinessEventNotificationMapper, CrossBuy.BL.Platform.BusinessEventNotificationMapper>",
				ProgramSource());
		}

		[Fact]
		public void The_timeline_read_path_has_a_non_empty_adapter_set()
		{
			// TimelineProjectionService takes IEnumerable<ILegacyTimelineAdapter>. An empty collection
			// resolves perfectly and silently serves NO pre-kernel history — the timeline would look
			// working and simply be missing the past. Four adapters are registered; all four are named so
			// dropping one fails here.
			var program = ProgramSource();

			foreach (var adapter in new[]
			{
				"SalesInvoiceLegacyTimelineAdapter", "CustomerLegacyTimelineAdapter",
				"PurchaseInvoiceLegacyTimelineAdapter", "ManufWorkOrderLegacyTimelineAdapter",
			})
				Assert.Contains($"ILegacyTimelineAdapter, CrossBuy.BL.Platform.{adapter}>", program);

			var count = program.Split("ILegacyTimelineAdapter, ").Length - 1;
			Assert.True(count >= 4, $"expected at least four legacy timeline adapters, found {count}");
		}

		[Fact]
		public void The_ai_service_credential_is_never_attached_empty()
		{
			// `?? ""` sent an EMPTY X-AI-Secret, which is fail-open: the request left the estate and was
			// merely refused at the far end. The header must be conditional on a usable secret, and the
			// rule must be the committed egress one rather than a second copy.
			var program = ProgramSource();

			Assert.DoesNotContain("\"X-AI-Secret\", cfg[\"AiService:Secret\"] ?? \"\"", program);
			Assert.Contains("AiEgressPolicy.IsUsableSecret(secret)", program);

			// And no fabricated fallback crept in beside it.
			Assert.DoesNotContain("X-AI-Secret\", \"", program);
		}

		// ---------------------------------------------------------------------------------------------

		private static string NameOf(string value) => value switch
		{
			"TimelineProjection" => nameof(BusinessEventConsumers.TimelineProjection),
			"NotificationProjection" => nameof(BusinessEventConsumers.NotificationProjection),
			"AiProjection" => nameof(BusinessEventConsumers.AiProjection),
			_ => value,
		};

		private static string ProgramSource() => RepoFile("CrossBuy", "Program.cs");

		private static string ConsumerSource(string typeName)
		{
			foreach (var folder in new[] { "Platform", "Platform/Ai" })
			{
				var parts = new List<string> { "CrossBuy", "BL" };
				parts.AddRange(folder.Split('/'));
				parts.Add(typeName + ".cs");
				var found = TryRepoFile(parts.ToArray());
				if (found != null) return found;
			}

			Assert.Fail($"source for {typeName} could not be located; the declared-name check cannot run.");
			return string.Empty;
		}

		private static string RepoFile(params string[] parts)
		{
			var found = TryRepoFile(parts);
			if (found != null) return found;

			Assert.Fail($"{string.Join("/", parts)} could not be located from the test assembly directory. " +
						"The composition guards cannot run, and a sweep over an empty string would pass vacuously.");
			return string.Empty;
		}

		private static string? TryRepoFile(params string[] parts)
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
				if (File.Exists(candidate)) return File.ReadAllText(candidate);
			}

			return null;
		}
	}
}
