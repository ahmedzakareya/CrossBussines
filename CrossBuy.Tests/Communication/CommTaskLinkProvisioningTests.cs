using CrossBuy.BL.Communication;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =================================================================================================
    // WHO DECIDES THAT TASK LINKING IS AVAILABLE.
    //
    // WHY THIS FILE EXISTS
    //
    // The decision used to be a configuration flag, which chained it to a machine-local connection
    // string: CommTaskLinks exists on CrossBuyDB2 and not on CrossBuyDev, so a committed
    // `TaskLinksProvisioned=true` without the committed connection override meant the EF store pointed
    // at a database with no table — a SQL-208 on first use for every clean checkout. The feature could
    // therefore only be switched on in a file nobody could share, and on 2026-10-27 that is exactly how
    // it silently switched itself off across all nine host screens when a process restarted.
    //
    // CommTaskLinkProvisioning moves the decision to the database and caches it. Everything that makes
    // that safe is here: the answer is read once, a failed read is not mistaken for an answer, and the
    // configuration flag can no longer grant what the catalogues deny.
    //
    // THESE TESTS OPEN NO DATABASE. The probe is an interface; every case below drives a stub of it and
    // a fake clock. That is deliberate and not merely convenient — the behaviour under test is the
    // CACHING RULE, and a real probe would make the number of catalogue reads a property of SQL Server's
    // availability rather than of the code. The delegation half (which inner store a given answer
    // selects) is asserted in CommunicationDiWiringTests against a real container.
    // =================================================================================================
    public class CommTaskLinkProvisioningTests
    {
        // Answers whatever it is told to, and counts how many times it was asked. The count is the point
        // of most of these tests: "cached" is not observable from the answer, only from the call.
        private sealed class StubProbe : ICommTaskLinkSchemaProbe
        {
            private readonly Queue<CommTaskLinkSchemaStatus> _answers = new();
            private readonly CommTaskLinkSchemaStatus _last;

            public StubProbe(params CommTaskLinkSchemaStatus[] answers)
            {
                foreach (var a in answers) _answers.Enqueue(a);
                _last = answers[^1];
            }

            public int Calls;

            public Task<CommTaskLinkSchemaStatus> CheckAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                // The final answer repeats, so a test that only cares about caching does not have to
                // enumerate every read it is about to make.
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : _last);
            }
        }

        private static CommTaskLinkSchemaStatus Provisioned() =>
            new() { Provisioned = true, Missing = Array.Empty<string>() };

        private static CommTaskLinkSchemaStatus Absent(params string[] missing) =>
            new() { Provisioned = false, Missing = missing };

        private static CommTaskLinkSchemaStatus Inconclusive(string reason) =>
            CommTaskLinkSchemaStatus.NotProvisioned(reason);

        // -----------------------------------------------------------------------------------------
        // A CONCLUSIVE ANSWER IS READ ONCE.
        //
        // IsProvisioned is a property, read as a gate by five call sites before they do anything else
        // (CommTaskIntegration.cs:411, 678, 763, 810 and TasksController.cs:2052). A page that shows a
        // conversation reads it several times. One catalogue round trip per read would be a per-request
        // tax on every screen that can link a task, so the count here is a performance contract, not a
        // stylistic one.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void A_provisioned_database_is_probed_once_however_often_it_is_asked()
        {
            var probe = new StubProbe(Provisioned());
            var provisioning = new CommTaskLinkProvisioning();

            for (int i = 0; i < 10; i++) Assert.True(provisioning.IsProvisioned(probe));

            Assert.Equal(1, probe.Calls);
        }

        [Fact]
        public void An_absent_table_is_also_remembered_after_one_read()
        {
            var probe = new StubProbe(Absent("table CommTaskLinks"));
            var provisioning = new CommTaskLinkProvisioning();

            for (int i = 0; i < 10; i++) Assert.False(provisioning.IsProvisioned(probe));

            Assert.Equal(1, probe.Calls);
            Assert.Contains("table CommTaskLinks", provisioning.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // THE CASE THE CACHE MUST NOT SWALLOW.
        //
        // CommTaskLinkSchemaStatus separates "checked, and the objects are absent" from "could not tell"
        // precisely because the remedies differ, and this is where that distinction earns its keep. The
        // probe fails closed on a provider without system catalogues, a connection that was not up yet,
        // or a query that threw — all of which arrive as Inconclusive. Caching one of those would
        // disable task linking for the entire lifetime of the process over a transient fault, with no
        // error anywhere and no way back except a restart. That is the 2026-10-27 failure shape all over
        // again, reached by a different road.
        //
        // The answer meanwhile is still false. Fail-closed is not negotiated away by uncertainty.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void An_inconclusive_probe_answers_no_but_is_not_remembered_as_no()
        {
            var clock = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
            var probe = new StubProbe(
                Inconclusive("the connection was refused"),
                Provisioned());
            var provisioning = new CommTaskLinkProvisioning(() => clock);

            Assert.False(provisioning.IsProvisioned(probe));
            Assert.Equal(1, probe.Calls);

            // Still inside the cooldown: answered from memory, and the answer is still no.
            Assert.False(provisioning.IsProvisioned(probe));
            Assert.Equal(1, probe.Calls);

            // Past it: asked again, and this time the database answers.
            clock = clock.Add(CommTaskLinkProvisioning.InconclusiveCooldown).AddSeconds(1);
            Assert.True(provisioning.IsProvisioned(probe));
            Assert.Equal(2, probe.Calls);

            // And THAT answer is conclusive, so it sticks.
            Assert.True(provisioning.IsProvisioned(probe));
            Assert.Equal(2, probe.Calls);
        }

        // The discriminator for the test above. If the cooldown were not honoured, an unreachable
        // database would be re-probed on every single gate read — a failing connection attempt per
        // property access, on a request path. This asserts the cooldown does bound it.
        [Fact]
        public void A_database_that_keeps_failing_is_not_re_probed_on_every_read()
        {
            var clock = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
            var probe = new StubProbe(Inconclusive("timeout"));
            var provisioning = new CommTaskLinkProvisioning(() => clock);

            for (int i = 0; i < 25; i++) Assert.False(provisioning.IsProvisioned(probe));

            Assert.Equal(1, probe.Calls);
        }

        // -----------------------------------------------------------------------------------------
        // CONCURRENCY. The first request to touch a Link button is frequently not alone — a dashboard
        // renders several conversation panels at once. Without the gate each of those would start its
        // own catalogue read against a database that has not answered the first one yet.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Twenty_simultaneous_callers_produce_one_catalogue_read()
        {
            var probe = new StubProbe(Provisioned());
            var provisioning = new CommTaskLinkProvisioning();

            var answers = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => provisioning.ResolveAsync(probe))));

            Assert.All(answers, a => Assert.True(a));
            Assert.Equal(1, probe.Calls);
        }

        // The async and synchronous entry points share one cache; a caller that awaited must not leave
        // the property to probe again.
        [Fact]
        public async Task The_async_resolve_and_the_property_share_the_same_answer()
        {
            var probe = new StubProbe(Provisioned());
            var provisioning = new CommTaskLinkProvisioning();

            Assert.True(await provisioning.ResolveAsync(probe));
            Assert.True(provisioning.IsProvisioned(probe));

            Assert.Equal(1, probe.Calls);
        }
    }
}
