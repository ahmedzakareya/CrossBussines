using CrossBuy.BL.Reporting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // REP-06 — THE SCHEDULER'S ENABLEMENT PATH.
    //
    // The worker has always been registered and always been OFF. What was missing was any way for
    // an OPERATOR to turn it on: ReportScheduleWorkerOptions could only be set from code, while the
    // worker's own refusal told the reader to set "Reporting:ScheduleWorker:Enabled" — a key nothing
    // in the product read. These tests pin both halves: that the key now works when a host opts in,
    // and that it still does nothing when the host has not.
    // ============================================================================================
    public class ReportingScheduleConfigurationTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v =>
                    new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        // ---- the shipped default ---------------------------------------------------------------

        // ADR-037 §8's decision, asserted rather than trusted: unattended generation and delivery is
        // off until somebody says otherwise.
        [Fact]
        public void The_worker_is_off_before_anything_is_configured()
        {
            var options = new ReportingPlatformOptions();

            Assert.False(options.ScheduleWorker.Enabled);
            Assert.True(options.ScheduleWorker.SweepInterval > TimeSpan.Zero);
        }

        // An absent key is not a request to dispatch. This is the state of every appsettings file in
        // the repository today, so it is also the state of every deployment.
        [Fact]
        public void An_absent_key_leaves_the_worker_off()
        {
            var options = new ReportingPlatformOptions().BindScheduleWorker(Config());

            Assert.False(options.ScheduleWorker.Enabled);
        }

        // Neither is a typo. GetValue<bool> yields false for an unparseable value, and false is the
        // right answer: "maybe" must never mean "dispatch".
        [Theory]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("TRUE-ish")]
        [InlineData("")]
        public void An_unparseable_value_leaves_the_worker_off(string value)
        {
            var options = new ReportingPlatformOptions()
                .BindScheduleWorker(Config((ReportingScheduleConfiguration.EnabledKey, value)));

            Assert.False(options.ScheduleWorker.Enabled);
        }

        // ---- THE POSITIVE CONTROL: the documented key now does what it says ---------------------

        // THE DEFECT THIS CLOSES. Before BindScheduleWorker existed, this exact configuration — the
        // one the worker's refusal message instructs an operator to write — left Enabled false and
        // the schedules silent. Without this assertion the rest of the file passes against a binding
        // that is broken shut.
        [Fact]
        public void The_key_the_refusal_names_actually_enables_the_worker()
        {
            var options = new ReportingPlatformOptions()
                .BindScheduleWorker(Config((ReportingScheduleConfiguration.EnabledKey, "true")));

            Assert.True(options.ScheduleWorker.Enabled);
        }

        // The key in the binding and the key in the refusal message must be the same string. They
        // were written in two places and drifted once already; this is what stops it twice.
        [Fact]
        public void The_bound_key_is_the_one_the_refusal_message_prints()
        {
            Assert.Equal("Reporting:ScheduleWorker:Enabled", ReportingScheduleConfiguration.EnabledKey);
        }

        // ---- intervals ---------------------------------------------------------------------------

        [Fact]
        public void A_positive_sweep_interval_and_startup_delay_are_honoured()
        {
            var options = new ReportingPlatformOptions().BindScheduleWorker(Config(
                (ReportingScheduleConfiguration.EnabledKey, "true"),
                (ReportingScheduleConfiguration.SweepIntervalMinutesKey, "2"),
                (ReportingScheduleConfiguration.StartupDelaySecondsKey, "0")));

            Assert.Equal(TimeSpan.FromMinutes(2), options.ScheduleWorker.SweepInterval);
            Assert.Equal(TimeSpan.Zero, options.ScheduleWorker.StartupDelay);
        }

        // A NONSENSE INTERVAL MUST NOT REACH PeriodicTimer. Zero or negative makes its constructor
        // throw, which would take the worker down at startup with a stack trace rather than run on
        // the shipped cadence. The default standing is the safe outcome.
        //
        // THE NON-NUMERIC CASES ARE THE ONES THAT NEARLY SHIPPED A CRASH. The first implementation
        // used GetValue<double?>, whose converter THROWS on "5 minutes" — so a human-readable value
        // in appsettings would have taken the host down at startup rather than been ignored.
        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("5 minutes")]
        [InlineData("soon")]
        [InlineData("")]
        public void A_nonsense_sweep_interval_leaves_the_default_standing(string value)
        {
            var shipped = new ReportingPlatformOptions().ScheduleWorker.SweepInterval;

            var options = new ReportingPlatformOptions().BindScheduleWorker(Config(
                (ReportingScheduleConfiguration.EnabledKey, "true"),
                (ReportingScheduleConfiguration.SweepIntervalMinutesKey, value)));

            Assert.Equal(shipped, options.ScheduleWorker.SweepInterval);
        }

        // A negative startup delay would throw in Task.Delay for the same reason.
        [Fact]
        public void A_negative_startup_delay_leaves_the_default_standing()
        {
            var shipped = new ReportingPlatformOptions().ScheduleWorker.StartupDelay;

            var options = new ReportingPlatformOptions().BindScheduleWorker(Config(
                (ReportingScheduleConfiguration.StartupDelaySecondsKey, "-1")));

            Assert.Equal(shipped, options.ScheduleWorker.StartupDelay);
        }

        // ---- the host still decides ---------------------------------------------------------------

        // THE TRAP, PINNED. Configuration alone changes nothing: a host that never calls
        // BindScheduleWorker is unaffected by the key, which is the behaviour of the product as it
        // stands right now in Program.cs. Anyone who sets the key in appsettings and expects reports
        // to start firing is reading this test's answer, not a guess.
        [Fact]
        public void Without_the_host_opting_in_the_key_still_does_nothing()
        {
            var configuration = Config((ReportingScheduleConfiguration.EnabledKey, "true"));

            // Exactly what AddCrossBusinessReporting does today: build the options, never bind them.
            var options = new ReportingPlatformOptions();

            Assert.False(options.ScheduleWorker.Enabled);
            Assert.True(configuration.GetValue<bool>(ReportingScheduleConfiguration.EnabledKey));
        }

        // RunScheduledReports stays the code-side route and keeps working, so a host that already
        // used it is not broken by the arrival of the configuration path.
        [Fact]
        public void The_code_side_route_still_enables_the_worker()
        {
            var options = new ReportingPlatformOptions().RunScheduledReports(TimeSpan.FromMinutes(3));

            Assert.True(options.ScheduleWorker.Enabled);
            Assert.Equal(TimeSpan.FromMinutes(3), options.ScheduleWorker.SweepInterval);
        }
    }
}
