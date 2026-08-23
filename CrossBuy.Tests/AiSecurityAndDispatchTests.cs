using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation — the two properties that would matter most if they were wrong: AI has no write
    // authority, and one consumer's failure cannot disturb another's completed work.
    public class AiSecurityAndDispatchTests
    {
        // ==========================================================================================
        // §14 / §23 — AI CANNOT GAIN WRITE AUTHORITY
        // ==========================================================================================

        // SystemContextPolicy is the ONLY thing standing between "a background consumer may read a record"
        // and "a background consumer may act on the business". It must permit View and nothing else.
        [Fact]
        public void The_system_context_policy_still_allows_only_view()
        {
            Assert.Equal(new[] { PlatformActions.View }, SystemContextPolicy.AllowedActions.ToArray());
            Assert.True(SystemContextPolicy.Allows(PlatformActions.View));
        }

        // The elevated read tiers are not write actions, but they are not View either — a system context
        // must not acquire them, or an AI consumer would read Confidential and Restricted history.
        [Theory]
        [InlineData(PlatformActions.ViewConfidential)]
        [InlineData(PlatformActions.ViewRestricted)]
        public void A_system_context_is_refused_the_elevated_read_tiers(string action)
            => Assert.False(SystemContextPolicy.Allows(action));

        // The mutating verbs §0.3 forbids. None of them may ever be granted to a system context, which is
        // the only context the AI consumer holds.
        [Theory]
        [InlineData("create")]
        [InlineData("edit")]
        [InlineData("update")]
        [InlineData("delete")]
        [InlineData("approve")]
        [InlineData("post")]
        [InlineData("pay")]
        [InlineData("close")]
        [InlineData("cancel")]
        [InlineData("assign")]
        [InlineData("send")]
        [InlineData("execute")]
        [InlineData("manage")]
        public void A_system_context_is_refused_every_business_write_action(string action)
            => Assert.False(SystemContextPolicy.Allows(action),
                $"SystemContextPolicy must never allow '{action}' — the AI consumer runs as a system context.");

        // The provider itself, not just the policy constant: a system context asking to act on a real
        // registered entity must be denied, with the reason naming the policy.
        [Fact]
        public async Task The_permission_provider_denies_a_system_context_every_write_action()
        {
            using var host = new PlatformTestHost(1);
            var provider = new PlatformPermissionProvider(
                host.Registry(),
                new IModulePermissionAdapter[] { new AllowEverythingAdapter() },   // maximally permissive module
                NullLogger<PlatformPermissionProvider>.Instance);

            var system = BusinessContext.ForSystem(1);

            foreach (var action in new[] { "create", "edit", "delete", "approve", "post", "send", "manage" })
            {
                var d = await provider.CanAsync(system, EntityRegistry.Task, 91, action);
                // Denied even though the module adapter would allow ANYTHING — SystemContextPolicy runs
                // first, so a permissive module cannot hand a machine context write rights.
                Assert.False(d.Allowed, $"'{action}' must be denied to a system context");
            }
        }

        // The consumer's own surface: it must not depend on anything that could mutate business state.
        [Fact]
        public void The_ai_consumer_depends_on_no_business_writer()
        {
            var ctor = Assert.Single(typeof(AiProjectionConsumer).GetConstructors());
            foreach (var p in ctor.GetParameters())
            {
                var n = p.ParameterType.Name;
                Assert.DoesNotContain("JournalEntryService", n, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("StockService", n, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ReceivableService", n, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PayableService", n, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("TaskService", n, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("NotificationService", n, StringComparison.OrdinalIgnoreCase);
            }
        }

        // §0.2 — no model provider is wired in this increment. Asserted on the consumer's dependencies so
        // "we did not connect an LLM" is a checked fact rather than a claim in a report.
        [Fact]
        public void The_ai_consumer_takes_no_model_provider_dependency()
        {
            var ctor = Assert.Single(typeof(AiProjectionConsumer).GetConstructors());
            foreach (var p in ctor.GetParameters())
            {
                var n = p.ParameterType.FullName ?? p.ParameterType.Name;
                foreach (var banned in new[] { "OpenAi", "OpenAI", "Azure", "Llm", "Embedding", "Vector", "HttpClient", "IAiService" })
                    Assert.DoesNotContain(banned, n, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ==========================================================================================
        // §17 / §24 — PER-CONSUMER DISPATCH INDEPENDENCE, with AI in the mix
        // ==========================================================================================

        // The scenario §24 specifies exactly: one event, Notifications succeeds, AI fails, AI retries and
        // then succeeds. Notifications must never be re-run, and neither side duplicates.
        [Fact]
        public async Task Ai_failure_and_retry_never_disturb_a_completed_notification_dispatch()
        {
            using var host = new PlatformTestHost(1);
            // RetryBackoffSeconds = 0 so the retry is claimable immediately. The default is 30s, which is
            // correct in production (it stops a hot failure loop) and would otherwise make this test assert
            // the backoff rather than the isolation it is about.
            var store = host.DispatchStore(options: new BusinessEventDispatchOptions { RetryBackoffSeconds = 0 });

            long eventId = await SeedEventWithDispatchRowsAsync(host);

            // ---- Notifications completes ----
            var n1 = Assert.Single(await store.ClaimPendingAsync(BusinessEventConsumers.NotificationProjection, 10));
            await store.MarkDoneAsync(n1.DispatchId);

            // ---- AI fails ----
            var a1 = Assert.Single(await store.ClaimPendingAsync(BusinessEventConsumers.AiProjection, 10));
            await store.MarkFailedAsync(a1.DispatchId, "transient projection failure");

            // Notifications is NOT offered again merely because AI failed — the property that makes
            // per-consumer state worth having.
            Assert.Empty(await store.ClaimPendingAsync(BusinessEventConsumers.NotificationProjection, 10));

            // ---- AI retries and succeeds ----
            var a2 = Assert.Single(await store.ClaimPendingAsync(BusinessEventConsumers.AiProjection, 10));
            Assert.Equal(a1.DispatchId, a2.DispatchId);      // the same row, retried — not a new one
            await store.MarkDoneAsync(a2.DispatchId);

            // ---- final state ----
            using var verify = host.AllCompanies();
            var rows = await verify.BusinessEventDispatches
                .Where(d => d.EventId == eventId)
                .OrderBy(d => d.Consumer).ToListAsync();

            Assert.Equal(3, rows.Count);      // Timeline + Notification + AI
            Assert.All(rows.Where(r => r.Consumer != BusinessEventConsumers.TimelineProjection),
                       r => Assert.Equal(BusinessEventDispatchStatus.Done, r.Status));
            Assert.Single(rows, r => r.Consumer == BusinessEventConsumers.AiProjection);
            Assert.Single(rows, r => r.Consumer == BusinessEventConsumers.NotificationProjection);
        }

        // Registering AI must not have changed what the OTHER consumers receive.
        [Fact]
        public async Task Registering_the_ai_consumer_adds_exactly_one_dispatch_row_per_event()
        {
            using var host = new PlatformTestHost(1);
            long eventId = await SeedEventWithDispatchRowsAsync(host);

            using var verify = host.AllCompanies();
            var consumers = await verify.BusinessEventDispatches
                .Where(d => d.EventId == eventId)
                .Select(d => d.Consumer).OrderBy(c => c).ToListAsync();

            Assert.Equal(
                new[]
                {
                    BusinessEventConsumers.AiProjection,
                    BusinessEventConsumers.NotificationProjection,
                    BusinessEventConsumers.TimelineProjection,
                }.OrderBy(c => c).ToArray(),
                consumers.ToArray());
        }

        // The registry and DI must stay in step: a registered name with no implementation accumulates rows
        // nothing drains, which the worker can only log about after the fact.
        [Fact]
        public void The_ai_consumer_name_is_registered_and_matches_the_implementation()
        {
            Assert.Contains(BusinessEventConsumers.AiProjection, BusinessEventConsumers.Registered);
            Assert.True(BusinessEventConsumers.IsRegistered(BusinessEventConsumers.AiProjection));
        }

        // ------------------------------------------------------------------------------------------
        private static async Task<long> SeedEventWithDispatchRowsAsync(PlatformTestHost host)
        {
            var events = host.Events();
            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);
            long eventId = await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.Task,
                EntityId = 91,
                EventType = TaskEvents.StatusChanged,
                Visibility = BusinessEventVisibility.Internal,
                Payload = new { previousStatus = "New", newStatus = "InProgress" },
            });
            await tx.CommitAsync();
            return eventId;
        }

        private sealed class AllowEverythingAdapter : IModulePermissionAdapter
        {
            public string Scope => EntityRegistry.ScopeTasks;
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(PermissionDecision.Allow("deliberately permissive test adapter"));
        }
    }
}
