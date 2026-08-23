using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the AI RUNTIME COMPOSITION — the two integration gaps that left committed AI code inert.
	//
	// WHAT WAS WRONG. AiProjection and AiEgressAudit were committed entities with committed stores, and
	// the whole AI Foundation service graph was committed — but CrossDbContext never mapped the two
	// entities and Program.cs never registered the graph. So `db.Set<AiProjection>()` threw at runtime,
	// and every AI service was unreachable. Committed code that nothing can construct is not a feature;
	// it is a claim.
	//
	// WHY THESE TESTS AND NOT A BOOT. The host booting proves the graph is SATISFIABLE, and
	// ValidateOnBuild proves it loudly — removing one registration makes the host refuse to start. But a
	// boot is not a regression guard: nothing fails in CI if a future edit drops a registration or a
	// mapping. These assert the two things a boot cannot leave behind.
	//
	// WHAT THESE DO NOT TEST. Provider approval, ZDR evidence, egress refusal and the OpenAI adapter are
	// covered by the committed AI governance suites; duplicating them here would create a second place to
	// keep in step. Composition is the only subject.
	// =================================================================================================
	public class AiRuntimeCompositionTests
	{
		// ---------------------------------------------------------------------------------------------
		// EF MODEL — the entities are actually mapped
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_committed_ai_entities_are_part_of_the_ef_model()
		{
			using var host = new PlatformTestHost();

			var projection = host.Db.Model.FindEntityType(typeof(AiProjection));
			var audit = host.Db.Model.FindEntityType(typeof(AiEgressAudit));

			Assert.NotNull(projection);
			Assert.NotNull(audit);

			// The table names matter as much as the presence: the structure ships as SQL
			// (deploy/sql/platform_ai_projections.sql, platform_ai_egress_audit.sql) and EF must agree with
			// it, because migrations are disabled and nothing reconciles a mismatch.
			Assert.Equal("AiProjections", projection!.GetTableName());
			Assert.Equal("AiEgressAudits", audit!.GetTableName());
		}

		[Fact]
		public void The_ai_entities_are_reachable_through_Set()
		{
			// This is the call that used to throw. `Set<T>()` on an unmapped type raises
			// InvalidOperationException, which is how committed AI persistence was inert at runtime while
			// every unit test that never touched a DbContext still passed.
			using var host = new PlatformTestHost();

			Assert.Empty(host.Db.Set<AiProjection>().ToList());
			Assert.Empty(host.Db.Set<AiEgressAudit>().ToList());
		}

		[Fact]
		public void The_egress_audit_has_no_payload_column()
		{
			// A standing governance property, asserted where the model is asserted: the classification
			// matrix decides what may leave the estate, so storing the prompt would recreate that
			// retention inside CrossBuy. If a payload column is ever added, this fails first.
			using var host = new PlatformTestHost();

			var audit = host.Db.Model.FindEntityType(typeof(AiEgressAudit));
			Assert.NotNull(audit);

			var columns = audit!.GetProperties().Select(p => p.Name).ToList();
			foreach (var forbidden in new[] { "Payload", "PayloadJson", "Prompt", "Request", "Response", "Completion" })
				Assert.DoesNotContain(forbidden, columns);
		}

		// ---------------------------------------------------------------------------------------------
		// DI COMPOSITION — a regression guard the boot cannot provide
		// ---------------------------------------------------------------------------------------------

		[Theory]
		// Increment 1 — the read-only projection boundary.
		[InlineData("CrossBuy.BL.Platform.IBusinessEventConsumer", "CrossBuy.BL.Platform.Ai.AiProjectionConsumer")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiConsumerGrants", "CrossBuy.BL.Platform.Ai.AiConsumerGrants")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProjectionStore", "CrossBuy.BL.Platform.Ai.AiProjectionStore")]
		// Increment 2 — the secure read side and revocation.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProjectionShapeRegistry", "CrossBuy.BL.Platform.Ai.AiProjectionShapeRegistry")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProjectionReader", "CrossBuy.BL.Platform.Ai.AiProjectionReader")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProjectionRevocationService", "CrossBuy.BL.Platform.Ai.AiProjectionRevocationService")]
		// Increment 3 — retention.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiRetentionPolicyRegistry", "CrossBuy.BL.Platform.Ai.AiRetentionPolicyRegistry")]
		// Increment 4.5 — cost, volume and failure controls.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiRateLimiter", "CrossBuy.BL.Platform.Ai.AiRateLimiter")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiPricingProvider", "CrossBuy.BL.Platform.Ai.AiConfiguredPricingProvider")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiUsageGuard", "CrossBuy.BL.Platform.Ai.AiUsageGuard")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProviderSwitchboard", "CrossBuy.BL.Platform.Ai.AiProviderSwitchboard")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiCircuitBreaker", "CrossBuy.BL.Platform.Ai.AiCircuitBreaker")]
		// Increment 4.6 — the durable egress audit.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiEgressAuditStore", "CrossBuy.BL.Platform.Ai.SqlAiEgressAuditStore")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiEgressAuditSink", "CrossBuy.BL.Platform.Ai.AiEgressAuditSink")]
		// The adapter. Resolvable and unreachable: SendAsync needs an approval only AiEgressPolicy mints.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiExternalProvider", "CrossBuy.BL.Platform.Ai.OpenAiProviderAdapter")]
		// The governance pair the earlier integration hotfix landed; asserted so it cannot be lost either.
		[InlineData("CrossBuy.BL.Platform.Ai.IAiProviderAuthority", "CrossBuy.BL.Platform.Ai.AiProviderAuthority")]
		[InlineData("CrossBuy.BL.Platform.Ai.IAiEgressPolicy", "CrossBuy.BL.Platform.Ai.AiEgressPolicy")]
		public void Program_registers_the_committed_ai_runtime_service(string service, string implementation)
		{
			var program = ProgramSource();
			Assert.Contains($"<{service}, {implementation}>", program);
		}

		[Fact]
		public void The_projection_builder_collection_is_not_vacuous()
		{
			// AiProjectionConsumer takes IEnumerable<IAiProjectionBuilder>. An empty collection resolves
			// perfectly and silently produces no projections at all — the consumer would drain its outbox
			// rows and write nothing, which looks like "AI is quiet" rather than "AI is unwired". At least
			// two builders are registered, and both are named here so dropping one fails.
			var program = ProgramSource();

			foreach (var builder in new[] { "TaskLifecycleProjectionBuilder", "CalendarSchedulingProjectionBuilder" })
				Assert.Contains($"IAiProjectionBuilder, CrossBuy.BL.Platform.Ai.{builder}>", program);

			var count = program.Split("IAiProjectionBuilder, ").Length - 1;
			Assert.True(count >= 2, $"expected at least two projection builders, found {count}");
		}

		[Fact]
		public void The_ai_consumer_is_registered_in_di_and_named_in_the_outbox_registry()
		{
			// BusinessEventConsumers.Registered and the DI container must stay in step in BOTH directions:
			// a name with no implementation accumulates dispatch rows nothing drains (the worker logs an
			// error), and an implementation with no name is never dispatched to at all.
			Assert.Contains(BusinessEventConsumers.AiProjection, BusinessEventConsumers.Registered);

			Assert.Contains(
				"<CrossBuy.BL.Platform.IBusinessEventConsumer, CrossBuy.BL.Platform.Ai.AiProjectionConsumer>",
				ProgramSource());
		}

		// ---------------------------------------------------------------------------------------------

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
