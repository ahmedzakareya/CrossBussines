using System.Diagnostics;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // PROCESS LIVENESS — step 4, and the component most able to do harm by being confident.
    //
    // TWO INFERENCES ARE FORBIDDEN HERE, and each one is a rule rather than a comment:
    //
    //   * A MISSING RunEnded DOES NOT MEAN THE RUN DIED. It also describes a run still in progress, a run
    //     whose manifest writer was degraded, and a manifest read while it was being appended to. Nothing
    //     below so much as looks at RunEnded - liveness is decided entirely from process evidence.
    //
    //   * A MATCHING PID DOES NOT MEAN THE RUN LIVES. Windows reuses process ids freely, and a test host
    //     that exited an hour ago can have its number worn by something started since. The start-time
    //     comparison is what turns a PID from a coincidence into evidence.
    //
    // WHEN THE EVIDENCE RUNS OUT, THE ANSWER IS Unknown - never Dead. Classifying a live run's database as
    // an orphan is the one mistake this detector could make that would eventually cost somebody real data.
    // =================================================================================================

    public enum RunLiveness
    {
        /// <summary>A process matching the run is running now.</summary>
        Alive,

        /// <summary>No process matches the run. Its databases are no longer being managed by anyone.</summary>
        Dead,

        /// <summary>Not determinable: another machine, unreadable metadata, or a missing timestamp.</summary>
        Unknown,
    }

    public enum ProcessProbe
    {
        /// <summary>The process id is in use and its metadata was readable.</summary>
        Running,

        /// <summary>No process holds that id.</summary>
        NotRunning,

        /// <summary>Something holds the id but its metadata could not be read.</summary>
        Unreadable,
    }

    public sealed record ProcessFact(int ProcessId, DateTime StartTimeUtc, string Name);

    /// <summary>
    /// Injected so the synthetic suite never touches a real process table: every one of the liveness
    /// scenarios - including PID reuse, which cannot be staged on a live machine - is driven through a fake.
    /// </summary>
    public interface IProcessInspector
    {
        ProcessProbe Probe(int processId, out ProcessFact? fact);
    }

    /// <summary>The real inspector. Used only by the live inventory test, never by the synthetic suite.</summary>
    public sealed class SystemProcessInspector : IProcessInspector
    {
        public ProcessProbe Probe(int processId, out ProcessFact? fact)
        {
            fact = null;
            if (processId <= 0) return ProcessProbe.NotRunning;

            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return ProcessProbe.NotRunning; }
            catch (Exception) { return ProcessProbe.Unreadable; }

            using (process)
            {
                try
                {
                    fact = new ProcessFact(processId, process.StartTime.ToUniversalTime(), process.ProcessName);
                    return ProcessProbe.Running;
                }
                catch (Exception)
                {
                    // The id is in use but we may not read its start time - on this machine that did not
                    // occur for any of 529 processes, but an elevated or protected process would land here,
                    // and a PID we cannot date is a PID we cannot trust.
                    return ProcessProbe.Unreadable;
                }
            }
        }
    }

    public static class ScratchRunLiveness
    {
        /// <summary>
        /// Allowance for clock movement between the manifest timestamp and the OS process start time. Both
        /// come from the same machine's clock, so the window is small on purpose: widening it would start
        /// classifying genuinely reused PIDs as the original run.
        /// </summary>
        public static readonly TimeSpan DefaultClockTolerance = TimeSpan.FromMinutes(2);

        public static RunLiveness Assess(
            ScratchRun run,
            IProcessInspector inspector,
            string localMachine,
            TimeSpan? clockTolerance = null)
        {
            var tolerance = clockTolerance ?? DefaultClockTolerance;

            // 1. ANOTHER MACHINE. A process id from a different host says nothing about this one's process
            //    table, and there is no independent way to verify a remote host from here.
            if (!string.Equals(run.Machine, localMachine, StringComparison.OrdinalIgnoreCase))
                return RunLiveness.Unknown;

            if (run.ProcessId <= 0) return RunLiveness.Unknown;

            // 2. WE HAVE NO START TIME TO COMPARE AGAINST, so a matching PID cannot be distinguished from a
            //    reused one. That is exactly the case the rules above forbid resolving.
            if (run.StartedUtc == default) return RunLiveness.Unknown;

            var probe = inspector.Probe(run.ProcessId, out var fact);

            switch (probe)
            {
                case ProcessProbe.NotRunning:
                    return RunLiveness.Dead;

                case ProcessProbe.Unreadable:
                    return RunLiveness.Unknown;

                case ProcessProbe.Running when fact is null:
                    return RunLiveness.Unknown;

                case ProcessProbe.Running:
                    // 3. PID REUSE. A process that started AFTER the run first wrote to its manifest cannot
                    //    be the process that wrote it. The original is gone and its id was handed on.
                    return fact!.StartTimeUtc > run.StartedUtc + tolerance
                        ? RunLiveness.Dead
                        : RunLiveness.Alive;

                default:
                    return RunLiveness.Unknown;
            }
        }
    }
}
