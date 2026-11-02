using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // REP-06 — THE CONFIGURATION KEY THE REFUSAL ALREADY NAMES.
    //
    // THE DEFECT THIS CLOSES, and it is a lie told to an operator rather than a crash.
    // ReportScheduleHostedService refuses with:
    //
    //     "the schedule worker is disabled (Reporting:ScheduleWorker:Enabled is false).
    //      Saved schedules will not fire."
    //
    // Nothing in the product read that key. ReportScheduleWorkerOptions was settable ONLY from code,
    // through ReportingPlatformOptions.RunScheduledReports(), so an operator who did exactly what the
    // message told them — set Reporting:ScheduleWorker:Enabled to true in appsettings — got no error,
    // no warning, the same message on the next restart, and schedules that still never fired. A
    // refusal that names a lever which does not exist is worse than one that names none, because it
    // sends someone to edit a file that cannot affect the outcome.
    //
    // WHY THIS IS A SEPARATE FILE AND NOT SIX LINES IN ReportingRegistration.cs. That file is
    // byte-identical to origin/integration and sits in the shared staged index TAB-0 is preserving;
    // editing it would put a reporting-only change into a landing unit that is already published.
    // This file adds the binding without touching it, so the host opts in with one line and nothing
    // else in the reporting graph moves.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // IT STILL DOES NOT TURN ANYTHING ON BY ITSELF, and that is deliberate. The worker remains OFF
    // unless BOTH of these are true:
    //
    //      1. a host calls BindScheduleWorker(configuration)   — opting into config control at all
    //      2. Reporting:ScheduleWorker:Enabled is true          — the operator's own decision
    //
    // Step 1 exists because turning unattended generation and delivery on is a decision about THIS
    // PROCESS, which is the same reason Program.cs registers the worker rather than
    // AddCrossBusinessReporting doing it. A library that could switch itself on from a config file
    // an operator edited for another reason would take that decision away from the host.
    // ============================================================================================
    public static class ReportingScheduleConfiguration
    {
        /// The key the hosted service's own refusal message names. Declared once, here, so the
        /// message and the binding can never drift apart again.
        public const string EnabledKey = "Reporting:ScheduleWorker:Enabled";

        public const string SweepIntervalMinutesKey = "Reporting:ScheduleWorker:SweepIntervalMinutes";
        public const string StartupDelaySecondsKey = "Reporting:ScheduleWorker:StartupDelaySeconds";

        /// <summary>
        /// Lets the operator decide, from configuration, whether saved schedules fire.
        /// Absent or false leaves the worker OFF, which is the shipped default.
        /// </summary>
        public static ReportingPlatformOptions BindScheduleWorker(
            this ReportingPlatformOptions options, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configuration);

            // FAIL CLOSED ON AN ABSENT KEY, AND ON AN UNPARSEABLE ONE — read as a string and parsed
            // here rather than through GetValue<bool>.
            //
            // GetValue<bool> does NOT return false for a bad value: its BooleanConverter THROWS, so
            // "yes", "1" or "on" in appsettings would take the application down at startup with a
            // FormatException out of Boolean.Parse. Those are the three things an operator is most
            // likely to type instead of "true", and crashing the host over a typo in an optional
            // switch is a worse failure than ignoring it. A value that is not recognisably true is
            // not a request to dispatch.
            options.ScheduleWorker.Enabled =
                bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;

            // THE INTERVALS ARE ONLY HONOURED WHEN POSITIVE. A zero or negative sweep interval makes
            // PeriodicTimer throw, which would take the worker down on startup with a stack trace
            // instead of a usable message — so a nonsense value leaves the shipped default standing.
            // Parsed the same defensive way and for the same reason: GetValue<double?> throws on a
            // non-numeric value, so "5 minutes" in appsettings would crash the host.
            if (TryReadNumber(configuration, SweepIntervalMinutesKey, out var sweepMinutes)
                && sweepMinutes > 0)
                options.ScheduleWorker.SweepInterval = TimeSpan.FromMinutes(sweepMinutes);

            // A zero startup delay is legitimate here — it means "sweep immediately" — so only a
            // negative value is refused, which Task.Delay would otherwise throw on.
            if (TryReadNumber(configuration, StartupDelaySecondsKey, out var startupSeconds)
                && startupSeconds >= 0)
                options.ScheduleWorker.StartupDelay = TimeSpan.FromSeconds(startupSeconds);

            return options;
        }

        /// Invariant culture on purpose: an appsettings file is not localised, and parsing "1.5"
        /// against an Arabic or French culture would read it as 15 or fail outright.
        private static bool TryReadNumber(IConfiguration configuration, string key, out double value) =>
            double.TryParse(configuration[key],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }
}
