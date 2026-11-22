using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =================================================================================================
    // PUSH TOKENS (module 21 / gap G3) — the vocabulary, the refusal, and the provisioning decision.
    //
    // WHAT IS AND IS NOT COVERED HERE, stated rather than left to be discovered.
    //
    // COVERED, and all of it without a database: which platform strings are accepted and what gets
    // stored; that the unprovisioned store refuses every member identically; and that the push
    // provisioning cache obeys the same rules as the task-link one, because it IS the same cache
    // (CommSchemaProvisioning) rather than a copy of it. That last one matters more than it looks —
    // the regression this platform is most likely to suffer is a second copy of the caching rule
    // drifting from the first, and the test below fails if push tokens ever grow their own.
    //
    // NOT COVERED HERE: that ProbingCommPushTokenStore routes to EfCommPushTokenStore rather than to
    // the unprovisioned one. Its constructor takes the concrete EF store, which takes a CrossDbContext,
    // so that assertion needs a database and belongs with the rest of the DI wiring. The gate itself —
    // which is where the risk lives — is the shared cache, and that is tested here.
    // =================================================================================================
    public class CommPushTokenStoreTests
    {
        private sealed class StubProbe : ICommPushTokenSchemaProbe
        {
            private readonly Queue<CommPushTokenSchemaStatus> _answers = new();
            private readonly CommPushTokenSchemaStatus _last;

            public StubProbe(params CommPushTokenSchemaStatus[] answers)
            {
                foreach (var a in answers) _answers.Enqueue(a);
                _last = answers[^1];
            }

            public int Calls;

            public Task<CommPushTokenSchemaStatus> CheckAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : _last);
            }
        }

        // -----------------------------------------------------------------------------------------
        // THE VOCABULARY. Three wire values, and the CHECK constraint in comm_push_tokens.sql spells
        // them lowercase, so what gets STORED has to be lowercase whatever the caller sent and
        // whatever the database's collation would have tolerated.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [InlineData("web")]
        [InlineData("android")]
        [InlineData("ios")]
        [InlineData("iOS")]        // a client's spelling, not a different platform
        [InlineData("  Android ")] // a wire payload with whitespace is not an unknown platform
        public void A_platform_this_product_can_address_is_accepted(string supplied)
        {
            Assert.True(CommPushPlatform.IsValid(supplied));

            var canonical = CommPushPlatform.Canonical(supplied);
            Assert.NotNull(canonical);
            Assert.Equal(canonical!.ToLowerInvariant(), canonical);
            Assert.Contains(canonical, CommPushPlatform.Values);
        }

        // THE DISCRIMINATOR. If Canonical ever started lowercasing its input before deciding whether it
        // knew it, every one of these would start passing and an unroutable token would reach a
        // credential table. The nulls are the assertion, not the false.
        [Theory]
        [InlineData("windows")]
        [InlineData("fcm")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void A_platform_with_no_transport_is_refused_rather_than_normalised(string? supplied)
        {
            Assert.False(CommPushPlatform.IsValid(supplied));
            Assert.Null(CommPushPlatform.Canonical(supplied));
        }

        // -----------------------------------------------------------------------------------------
        // THE UNPROVISIONED STORE REFUSES THE SAME WAY ON EVERY MEMBER.
        //
        // Not one of these throws. A caller that checked IsProvisioned never reaches them, and a
        // caller that did not gets an empty answer rather than a stack trace from a database driver
        // about a table that was never created — the HM-D44 shape, which is the whole reason this
        // type exists.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Without_the_table_every_member_answers_empty_and_none_of_them_throw()
        {
            ICommPushTokenStore store = new UnprovisionedCommPushTokenStore();

            Assert.False(store.IsProvisioned);
            Assert.Empty(await store.ForRecipientAsync(1, 5));
            Assert.Null(await store.RegisterAsync(1, 5, CommPushPlatform.Web, "endpoint", 5));

            // False, not true: a revoke that retired nothing must not read as success.
            Assert.False(await store.RevokeAsync(1, "endpoint", "signed out", 5));
        }

        // -----------------------------------------------------------------------------------------
        // PUSH TOKENS USE THE SHARED CACHE, NOT A SECOND COPY OF IT.
        //
        // This is the same rule CommTaskLinkProvisioningTests proves for task links, asserted here
        // through the push type. It passes only because both delegate to CommSchemaProvisioning: if
        // somebody gives push tokens their own caching logic and gets the inconclusive rule subtly
        // wrong — caching a failed probe, which disables the feature for a whole process lifetime over
        // a transient fault — this is what catches it.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void A_conclusive_answer_is_read_once_and_an_inconclusive_one_is_retried()
        {
            var clock = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
            var probe = new StubProbe(
                CommPushTokenSchemaStatus.NotProvisioned("the connection was refused"),
                new CommPushTokenSchemaStatus { Provisioned = true, Missing = Array.Empty<string>() });

            var provisioning = new CommPushTokenProvisioning(() => clock);

            // Inconclusive: answered no, and asked only once inside the cooldown.
            Assert.False(provisioning.IsProvisioned(probe));
            Assert.False(provisioning.IsProvisioned(probe));
            Assert.Equal(1, probe.Calls);

            // Past the cooldown it asks again, and this time the database answers.
            clock = clock.Add(CommSchemaProvisioning.InconclusiveCooldown).AddSeconds(1);
            Assert.True(provisioning.IsProvisioned(probe));
            Assert.Equal(2, probe.Calls);

            // And a conclusive answer sticks, however often it is asked.
            for (int i = 0; i < 10; i++) Assert.True(provisioning.IsProvisioned(probe));
            Assert.Equal(2, probe.Calls);
        }

        // An absent table is a real answer, so it is remembered — and the reason names what is missing
        // rather than leaving an operator to diff the DDL by hand.
        [Fact]
        public void An_absent_table_is_remembered_and_says_what_is_missing()
        {
            var probe = new StubProbe(new CommPushTokenSchemaStatus
            {
                Provisioned = false,
                Missing = new[] { "table " + CommPushTokenSchemaProbe.TableName },
            });

            var provisioning = new CommPushTokenProvisioning();

            for (int i = 0; i < 5; i++) Assert.False(provisioning.IsProvisioned(probe));

            Assert.Equal(1, probe.Calls);
            Assert.Contains(CommPushTokenSchemaProbe.TableName, provisioning.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // THE CONTRACT CARRIES NO EVIDENCE. CommPushToken is what an adapter receives; the row carries
        // RevokedAt, RevokedReason and who registered it, and none of that is an adapter's business.
        // Asserted structurally so adding a field to the row cannot quietly widen what is handed out.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_adapter_facing_contract_exposes_no_revocation_or_audit_fields()
        {
            var names = typeof(CommPushToken).GetProperties().Select(p => p.Name).ToArray();

            Assert.Equal(
                new[] { "EmployeeId", "LastSeenAt", "Platform", "RegisteredAt", "Token" },
                names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }
}
