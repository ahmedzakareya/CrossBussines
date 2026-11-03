using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Platform
{
    // Stage 0 Batch B — runtime instance identity and single-worker-process control.
    //
    // THE PROBLEM THIS SOLVES
    //
    // Six BackgroundServices run in-process: the business-event dispatcher, the email dispatcher, the integrity
    // check, the CRM reminder, the task generator and the schedule matcher. Every one of them assumes it is the
    // only copy running. That assumption is invisible and unenforced, and it is broken by things nobody thinks of
    // as a deployment change:
    //
    //   * an IIS application pool with maxProcesses > 1 (web gardens);
    //   * an overlapped recycle, where the old worker process is still draining while the new one has started;
    //   * a second box added behind a load balancer;
    //   * someone running the app from the CLI while IIS also has it up (routine on this project).
    //
    // The two outbox dispatchers survive that: their claiming statement is atomic (UPDLOCK + READPAST), so two
    // copies partition the work instead of duplicating it. The other four do NOT. The task generator would create
    // duplicate tasks, and the CRM reminder duplicate reminders — user-visible, and not undoable.
    //
    // WHY sp_getapplock AND NOT A CONFIG FLAG
    //
    // A setting called RequireSingleWorkerProcess that only writes a log line would be a lie: nothing would be
    // required. Enforcement uses a SQL Server application lock held for the process lifetime on its own dedicated
    // connection, with LockOwner = 'Session'. Only one session in the database can hold it, so exactly one process
    // becomes the worker primary. It needs no new table, no polling, no leader-election protocol, and SQL Server
    // releases the lock automatically when the process dies or its connection drops — which is precisely the
    // failure mode a home-grown "heartbeat row" gets wrong.
    //
    // FAIL-OPEN, DELIBERATELY, AND ONLY FOR ERRORS
    //
    // Three outcomes are distinguished, and they are not the same thing:
    //
    //   Primary      — this process acquired the lease. It runs the workers.
    //   Standby      — another process holds it. This process runs NO workers and retries periodically. This is
    //                  the intended steady state of a second instance.
    //   Unrestricted — the lease could not be evaluated at all (no connection string, insufficient permission to
    //                  execute sp_getapplock, database unreachable). The workers RUN, and the condition is logged
    //                  as an error and surfaced in diagnostics.
    //
    // The last one is a judgement call, made explicitly: failing closed would mean a permissions mistake silently
    // stops the audit outbox and every scheduled job, with nothing user-visible to notice it. Failing open means a
    // configuration mistake can, at worst, reproduce today's behaviour — which is what the system does right now,
    // everywhere. So the risk of failing open is bounded by the status quo, while the risk of failing closed is
    // new. It is reported loudly rather than tolerated quietly.

    public sealed class RuntimeOptions
    {
        // Master switch. Default TRUE: the safe posture is the one you get without configuring anything.
        public bool RequireSingleWorkerProcess { get; set; } = true;

        // The application-lock resource name. Scoped to the database, so two DIFFERENT databases on the same
        // instance each get their own primary — which is correct: they are separate deployments.
        public string WorkerLeaseName { get; set; } = "CrossBuy.BackgroundWorkers";

        // How often a standby re-tries for the lease. Long by design: a standby is a normal state, not an error,
        // and a tight retry loop would just add connection churn.
        public int LeaseRetrySeconds { get; set; } = 60;

        // Optional friendly name for this instance in logs and diagnostics (e.g. "web-01", "cli-dev").
        public string? InstanceName { get; set; }
    }

    public enum WorkerRole
    {
        // The lease has not been evaluated yet (startup).
        Unknown = 0,
        Primary,
        Standby,
        Unrestricted,
    }

    public interface IRuntimeInstanceInfo
    {
        Guid InstanceId { get; }
        string InstanceName { get; }
        string MachineName { get; }
        int ProcessId { get; }
        DateTime StartedAtUtc { get; }
        string ApplicationVersion { get; }
        string EnvironmentName { get; }

        WorkerRole WorkerRole { get; }
        string WorkerSafetyMode { get; }
        bool WorkersEnabled { get; }

        // Non-null when the lease could not be evaluated — the Unrestricted case. This is what the diagnostic
        // surface shows as a warning, so a fail-open never stays quiet.
        string? WorkerSafetyWarning { get; }

        void SetWorkerRole(WorkerRole role, string? warning = null);
    }

    public sealed class RuntimeInstanceInfo : IRuntimeInstanceInfo
    {
        private readonly object _gate = new();
        private WorkerRole _role = WorkerRole.Unknown;
        private string? _warning;

        public RuntimeInstanceInfo(IOptions<RuntimeOptions> options, IHostEnvironment environment)
        {
            var o = options.Value;
            InstanceId = Guid.NewGuid();
            MachineName = Environment.MachineName;
            using var process = Process.GetCurrentProcess();
            ProcessId = process.Id;
            // The process's own start time, not "now": it is what lets two log lines from two processes be told
            // apart when both are on the same machine with recycled PIDs.
            StartedAtUtc = process.StartTime.ToUniversalTime();
            InstanceName = string.IsNullOrWhiteSpace(o.InstanceName)
                ? $"{MachineName}/{ProcessId}"
                : o.InstanceName!.Trim();
            ApplicationVersion = typeof(RuntimeInstanceInfo).Assembly.GetName().Version?.ToString() ?? "unknown";
            EnvironmentName = environment.EnvironmentName;
            RequireSingleWorkerProcess = o.RequireSingleWorkerProcess;
        }

        public Guid InstanceId { get; }
        public string InstanceName { get; }
        public string MachineName { get; }
        public int ProcessId { get; }
        public DateTime StartedAtUtc { get; }
        public string ApplicationVersion { get; }
        public string EnvironmentName { get; }
        public bool RequireSingleWorkerProcess { get; }

        public WorkerRole WorkerRole { get { lock (_gate) return _role; } }
        public string? WorkerSafetyWarning { get { lock (_gate) return _warning; } }

        public string WorkerSafetyMode
        {
            get
            {
                lock (_gate)
                {
                    return _role switch
                    {
                        WorkerRole.Primary => "single-process enforced (this instance is PRIMARY)",
                        WorkerRole.Standby => "single-process enforced (this instance is STANDBY — workers idle)",
                        WorkerRole.Unrestricted => RequireSingleWorkerProcess
                            ? "UNENFORCED — the lease could not be evaluated; workers are running anyway"
                            : "not enforced (Runtime:RequireSingleWorkerProcess = false)",
                        _ => "evaluating",
                    };
                }
            }
        }

        public bool WorkersEnabled
        {
            get { lock (_gate) return _role is WorkerRole.Primary or WorkerRole.Unrestricted; }
        }

        public void SetWorkerRole(WorkerRole role, string? warning = null)
        {
            lock (_gate) { _role = role; _warning = warning; }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The gate every hosted service awaits before doing any work.
    //
    // D-16 - THIS GATE USED TO FAIL OPEN, AND THEN LATCH. Two coupled defects, and the second made the
    // first permanent:
    //   * any exception while evaluating the lease returned Unrestricted, which means "run"; and
    //   * that answer was cached, so SQL was never consulted again for the life of the process.
    // One transient blip therefore pinned a process to "run unrestricted" for good. During an outage BOTH
    // processes evaluated Unrestricted and both ran; after it ended, neither re-evaluated. Duplicate
    // dispatches, reminders and journals - at exactly the moment nobody is able to observe them.
    //
    // THE RULE NOW: A FAILED CHECK IS NOT PERMISSION. A check that threw, or that came back in a shape
    // this code does not understand, yields Standby and is NEVER cached - the next ask re-evaluates.
    //
    // ONE FAIL-OPEN SURVIVES ON PURPOSE, and it is not a failed check: an ABSENT CONNECTION STRING is a
    // configuration statement. A developer box with no database configured would otherwise have every
    // scheduled job refuse to start, which is its own outage and the reason the original author chose
    // fail-open. That case still opens the gate, still shouts about it, and is the only one that latches
    // - because a connection string does not appear halfway through a process lifetime.
    // ---------------------------------------------------------------------------------------------

    /// What ONE attempt to take the lease found. Separated from the policy so the policy can be tested
    /// without a database - which is the whole reason the latch went unnoticed for so long.
    public enum WorkerLeaseOutcome
    {
        Acquired,        // this process now holds the lease
        HeldByAnother,   // somebody else holds it; stand by and ask again later
        NotConfigured,   // there is nothing to take - no connection string
        Failed,          // THE CHECK ITSELF FAILED. We do not know who holds it, so we must not assume we do.
    }

    /// The held lease. The SQL implementation is an open connection holding a session application lock, so
    /// IsHeld is "is that connection still open" - losing the connection IS losing the lease.
    public interface IWorkerLease : IAsyncDisposable
    {
        bool IsHeld { get; }
    }

    public readonly record struct WorkerLeaseAttempt(WorkerLeaseOutcome Outcome, string? Detail, IWorkerLease? Lease);

    public interface IWorkerGate
    {
        // Returns true when this process may run background work. Blocks (respecting the token) while this
        // process is a standby, so a worker's loop needs one line and no policy of its own.
        Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken);

        // THE PER-TICK RE-CHECK. Evaluates ONCE and returns; it never waits, so a worker can ask it at the
        // top of every tick without holding the tick open. False means "not right now", which is
        // RECOVERABLE - the caller must park and ask again, never exit. Exiting on false would end the
        // BackgroundService for good: if this process loses the lease to another that later dies, nothing
        // would ever re-acquire and no worker would run anywhere until a restart.
        //
        // The default implementation delegates to WaitUntilAllowedAsync so existing implementers - test
        // stubs, chiefly - keep compiling unchanged. A REAL gate must override it: the delegate BLOCKS, and
        // blocking is the one thing a per-tick check must not do.
        Task<bool> IsStillAllowedAsync(string workerName, CancellationToken cancellationToken)
            => WaitUntilAllowedAsync(workerName, cancellationToken);
    }

    public sealed class WorkerGate : IWorkerGate, IAsyncDisposable
    {
        private readonly RuntimeOptions _options;
        private readonly IRuntimeInstanceInfo _instance;
        private readonly ILogger<WorkerGate> _log;
        private readonly string? _connectionString;
        private readonly SemaphoreSlim _once = new(1, 1);

        // How the lease is taken. Production always uses the SQL path; a test supplies its own so that
        // "transient failure, then recovery" can be exercised without a database in the loop.
        private readonly Func<CancellationToken, Task<WorkerLeaseAttempt>> _attempt;

        // Held for the whole process lifetime. Losing it is losing primacy, which is why IsHeld is consulted
        // on every evaluation rather than trusted once.
        private IWorkerLease? _lease;

        // ONLY the no-connection-string answer latches. Deliberately not a general "_evaluated" flag: that
        // is what made a one-off failure permanent.
        private bool _notConfigured;

        public WorkerGate(IOptions<RuntimeOptions> options, IRuntimeInstanceInfo instance,
            IConfiguration configuration, ILogger<WorkerGate> log)
        {
            _options = options.Value;
            _instance = instance;
            _log = log;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _attempt = AcquireThroughSqlAsync;
        }

        /// TEST SEAM. Takes the lease-acquisition step as a function so the gate's POLICY - fail closed, do
        /// not latch, recover - can be proven without opening a database. DI never selects this constructor:
        /// the delegate is not a registered service, so the container cannot satisfy it.
        public WorkerGate(IOptions<RuntimeOptions> options, IRuntimeInstanceInfo instance,
            IConfiguration configuration, ILogger<WorkerGate> log,
            Func<CancellationToken, Task<WorkerLeaseAttempt>> attempt)
            : this(options, instance, configuration, log)
        {
            _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        }

        public async Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken)
        {
            if (!_options.RequireSingleWorkerProcess)
            {
                MarkOnce(WorkerRole.Unrestricted, null);
                return true;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var (role, warning) = await EvaluateAsync(cancellationToken);
                if (role != WorkerRole.Standby)
                {
                    MarkOnce(role, warning);
                    return true;
                }

                // Standby. Log once per wait, at Information: this is a normal state for a second instance and
                // must not read as an incident.
                _instance.SetWorkerRole(WorkerRole.Standby);
                _log.LogInformation(
                    "{Worker}: another process holds the background-worker lease '{Lease}'. This instance " +
                    "({Instance}, PID {Pid}) stays on standby and will re-check in {Retry}s.",
                    workerName, _options.WorkerLeaseName, _instance.InstanceName, _instance.ProcessId,
                    _options.LeaseRetrySeconds);

                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.LeaseRetrySeconds)), cancellationToken); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        /// ONE evaluation, no waiting. See IWorkerGate for why false must mean "park", never "exit".
        public async Task<bool> IsStillAllowedAsync(string workerName, CancellationToken cancellationToken)
        {
            if (!_options.RequireSingleWorkerProcess) return true;
            if (cancellationToken.IsCancellationRequested) return false;

            var wasPrimary = _instance.WorkerRole == WorkerRole.Primary;
            var (role, warning) = await EvaluateAsync(cancellationToken);

            if (role == WorkerRole.Standby)
            {
                _instance.SetWorkerRole(WorkerRole.Standby);
                if (wasPrimary)
                {
                    // WARNING, not Information. Standby AT STARTUP is routine; losing primacy MID-RUN is not,
                    // and it is the event an operator needs to see when output stops.
                    _log.LogWarning(
                        "{Worker}: this instance ({Instance}, PID {Pid}) NO LONGER holds the background-worker " +
                        "lease '{Lease}'. It stands down and will resume if it reacquires.",
                        workerName, _instance.InstanceName, _instance.ProcessId, _options.WorkerLeaseName);
                }
                return false;
            }

            MarkOnce(role, warning);
            return true;
        }

        private async Task<(WorkerRole role, string? warning)> EvaluateAsync(CancellationToken cancellationToken)
        {
            await _once.WaitAsync(cancellationToken);
            try
            {
                // STILL HOLDING IT. One cheap check, no database round trip - which is what makes a per-tick
                // re-check affordable for the process that is actually doing the work.
                if (_lease is { IsHeld: true }) return (WorkerRole.Primary, null);

                // HELD IT, LOST IT. Drop the dead handle before asking again; the old code left it in place
                // and leaked the connection on the next successful acquisition.
                if (_lease != null)
                {
                    var dead = _lease;
                    _lease = null;
                    try { await dead.DisposeAsync(); } catch { /* it is already gone; releasing is best effort */ }
                }

                // The one latched answer, and the only one that is not a failed check.
                if (_notConfigured)
                    return (WorkerRole.Unrestricted, _instance.WorkerSafetyWarning);

                var attempt = await _attempt(cancellationToken);
                switch (attempt.Outcome)
                {
                    case WorkerLeaseOutcome.Acquired:
                        _lease = attempt.Lease;
                        return (WorkerRole.Primary, null);

                    case WorkerLeaseOutcome.HeldByAnother:
                        return (WorkerRole.Standby, null);

                    case WorkerLeaseOutcome.NotConfigured:
                        _notConfigured = true;
                        return (WorkerRole.Unrestricted,
                            attempt.Detail ?? "No DefaultConnection connection string, so the single-worker lease cannot be taken.");

                    default:
                        // FAIL CLOSED. We asked and did not get an answer we understand, so we do not know
                        // whether another process is primary - and "we do not know" must never read as "yes".
                        // Not cached: the very next ask tries again, so connectivity returning restores the
                        // worker without a restart.
                        _log.LogError(
                            "The background-worker lease '{Lease}' could not be evaluated ({Detail}). This " +
                            "instance ({Instance}, PID {Pid}) stands down rather than assume it may run. It will " +
                            "re-check.",
                            _options.WorkerLeaseName, attempt.Detail, _instance.InstanceName, _instance.ProcessId);
                        return (WorkerRole.Standby, null);
                }
            }
            finally { _once.Release(); }
        }

        /// The production acquisition step. Everything SQL lives here and nowhere else.
        private async Task<WorkerLeaseAttempt> AcquireThroughSqlAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                return new WorkerLeaseAttempt(WorkerLeaseOutcome.NotConfigured,
                    "No DefaultConnection connection string, so the single-worker lease cannot be taken.", null);

            SqlConnection? connection = null;
            try
            {
                connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = "sp_getapplock";
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("@Resource", _options.WorkerLeaseName);
                command.Parameters.AddWithValue("@LockMode", "Exclusive");
                // 'Session' - NOT 'Transaction'. A transaction-owned lock would be released the moment the
                // acquiring transaction ended, which is useless for a process-lifetime lease.
                command.Parameters.AddWithValue("@LockOwner", "Session");
                command.Parameters.AddWithValue("@LockTimeout", 0);   // never block; a standby must return at once
                var result = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
                result.Direction = ParameterDirection.ReturnValue;

                await command.ExecuteNonQueryAsync(cancellationToken);
                int code = (int)(result.Value ?? -999);

                // 0 = granted, 1 = granted after waiting. Negative = not granted (-1 timeout, -2 cancelled,
                // -3 deadlock victim, -999 parameter/other error).
                if (code is 0 or 1)
                {
                    var lease = new SqlWorkerLease(connection, _options.WorkerLeaseName, _log);
                    connection = null;              // ownership transferred; do not dispose in the finally
                    return new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, lease);
                }
                if (code == -1)
                    return new WorkerLeaseAttempt(WorkerLeaseOutcome.HeldByAnother, null, null);

                // ANY OTHER CODE IS A FAILED CHECK, not a grant. This used to return Unrestricted.
                return new WorkerLeaseAttempt(WorkerLeaseOutcome.Failed,
                    $"sp_getapplock returned {code}", null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new WorkerLeaseAttempt(WorkerLeaseOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}", null);
            }
            finally
            {
                if (connection != null) await connection.DisposeAsync();
            }
        }

        /// The lease as an open SQL connection holding a session application lock. Closing it releases the
        /// lock, which is exactly the behaviour wanted on crash or shutdown.
        private sealed class SqlWorkerLease : IWorkerLease
        {
            private readonly SqlConnection _connection;
            private readonly string _leaseName;
            private readonly ILogger _log;

            public SqlWorkerLease(SqlConnection connection, string leaseName, ILogger log)
            { _connection = connection; _leaseName = leaseName; _log = log; }

            public bool IsHeld => _connection.State == ConnectionState.Open;

            public async ValueTask DisposeAsync()
            {
                // Explicit release on graceful shutdown. SQL Server would release it anyway when the
                // connection dropped, but releasing eagerly lets a rolling restart hand over without waiting
                // for a timeout.
                if (_connection.State == ConnectionState.Open)
                {
                    try
                    {
                        await using var command = _connection.CreateCommand();
                        command.CommandText = "sp_releaseapplock";
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@Resource", _leaseName);
                        command.Parameters.AddWithValue("@LockOwner", "Session");
                        await command.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Releasing the background-worker lease failed; the connection close will release it.");
                    }
                }
                await _connection.DisposeAsync();
            }
        }

        private void MarkOnce(WorkerRole role, string? warning)
        {
            if (_instance.WorkerRole == role && _instance.WorkerSafetyWarning == warning) return;
            _instance.SetWorkerRole(role, warning);

            if (role == WorkerRole.Unrestricted && !string.IsNullOrEmpty(warning))
            {
                // Loud, because this is the one surviving fail-open path. It is a real operational condition,
                // not a detail.
                _log.LogError(
                    "SINGLE-WORKER ENFORCEMENT IS NOT ACTIVE on {Instance} (PID {Pid}). {Warning} " +
                    "If more than one process is running these workers, the non-outbox jobs (task generation, " +
                    "CRM reminders) can duplicate their output.",
                    _instance.InstanceName, _instance.ProcessId, warning);
            }
            else if (role == WorkerRole.Primary)
            {
                _log.LogInformation(
                    "Background-worker lease '{Lease}' acquired by {Instance} (PID {Pid}). This instance is PRIMARY.",
                    _options.WorkerLeaseName, _instance.InstanceName, _instance.ProcessId);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_lease != null)
            {
                var lease = _lease;
                _lease = null;
                await lease.DisposeAsync();
            }
            _once.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Startup banner. One log entry that answers "which process am I looking at, and is it the one running the
    // background jobs?" — the question every one of these incidents starts with.
    // ---------------------------------------------------------------------------------------------
    public sealed class RuntimeStartupLogger : IHostedService
    {
        private readonly IRuntimeInstanceInfo _instance;
        private readonly RuntimeOptions _options;
        private readonly ILogger<RuntimeStartupLogger> _log;

        public RuntimeStartupLogger(IRuntimeInstanceInfo instance, IOptions<RuntimeOptions> options,
            ILogger<RuntimeStartupLogger> log)
        { _instance = instance; _options = options.Value; _log = log; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation(
                "CrossBuy runtime starting — instance={Instance} id={InstanceId} machine={Machine} pid={Pid} " +
                "started={StartedAt:u} version={Version} env={Environment} " +
                "requireSingleWorkerProcess={Require} lease={Lease}",
                _instance.InstanceName, _instance.InstanceId, _instance.MachineName, _instance.ProcessId,
                _instance.StartedAtUtc, _instance.ApplicationVersion, _instance.EnvironmentName,
                _options.RequireSingleWorkerProcess, _options.WorkerLeaseName);

            if (!_options.RequireSingleWorkerProcess)
            {
                // Explicitly opted out. Say so at Warning, because the opt-out is the unsafe posture and should
                // not be something a reader has to infer from the absence of a message.
                _log.LogWarning(
                    "Runtime:RequireSingleWorkerProcess is FALSE. Every background worker will run in this process " +
                    "regardless of how many other processes are also running them. The outbox dispatchers are safe " +
                    "under that (atomic claiming); task generation and CRM reminders are NOT.");
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("CrossBuy runtime stopping — instance={Instance} pid={Pid} role={Role}",
                _instance.InstanceName, _instance.ProcessId, _instance.WorkerRole);
            return Task.CompletedTask;
        }
    }
}
