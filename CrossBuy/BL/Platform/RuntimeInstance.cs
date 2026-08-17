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
    // ---------------------------------------------------------------------------------------------
    public interface IWorkerGate
    {
        // Returns true when this process may run background work. Blocks (respecting the token) while this
        // process is a standby, so a worker's loop needs one line and no policy of its own.
        Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken);
    }

    public sealed class WorkerGate : IWorkerGate, IAsyncDisposable
    {
        private readonly RuntimeOptions _options;
        private readonly IRuntimeInstanceInfo _instance;
        private readonly ILogger<WorkerGate> _log;
        private readonly string? _connectionString;
        private readonly SemaphoreSlim _once = new(1, 1);

        // Held for the whole process lifetime. This connection is the lease: closing it releases the lock, which
        // is exactly the behaviour wanted on crash or shutdown.
        private SqlConnection? _leaseConnection;
        private bool _evaluated;

        public WorkerGate(IOptions<RuntimeOptions> options, IRuntimeInstanceInfo instance,
            IConfiguration configuration, ILogger<WorkerGate> log)
        {
            _options = options.Value;
            _instance = instance;
            _log = log;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
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

        // Acquires the lease at most once per process. Every worker calls this; the first one to arrive decides,
        // and the rest observe the same answer — so six workers do not take six locks.
        private async Task<(WorkerRole role, string? warning)> EvaluateAsync(CancellationToken cancellationToken)
        {
            await _once.WaitAsync(cancellationToken);
            try
            {
                if (_leaseConnection != null && _leaseConnection.State == ConnectionState.Open)
                    return (WorkerRole.Primary, null);
                if (_evaluated && _instance.WorkerRole == WorkerRole.Unrestricted)
                    return (WorkerRole.Unrestricted, _instance.WorkerSafetyWarning);

                if (string.IsNullOrWhiteSpace(_connectionString))
                {
                    _evaluated = true;
                    return (WorkerRole.Unrestricted,
                        "No DefaultConnection connection string, so the single-worker lease cannot be taken.");
                }

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
                    // 'Session' — NOT 'Transaction'. A transaction-owned lock would be released the moment the
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
                        _leaseConnection = connection;
                        connection = null;              // ownership transferred; do not dispose in the finally
                        _evaluated = true;
                        return (WorkerRole.Primary, null);
                    }
                    if (code == -1)
                    {
                        _evaluated = true;
                        return (WorkerRole.Standby, null);
                    }

                    _evaluated = true;
                    return (WorkerRole.Unrestricted,
                        $"sp_getapplock returned {code} for '{_options.WorkerLeaseName}', so single-process " +
                        "enforcement is not active. Workers are running unrestricted.");
                }
                catch (Exception ex)
                {
                    _evaluated = true;
                    return (WorkerRole.Unrestricted,
                        $"The single-worker lease could not be evaluated ({ex.GetType().Name}: {ex.Message}). " +
                        "Workers are running unrestricted.");
                }
                finally
                {
                    if (connection != null) await connection.DisposeAsync();
                }
            }
            finally { _once.Release(); }
        }

        private void MarkOnce(WorkerRole role, string? warning)
        {
            if (_instance.WorkerRole == role && _instance.WorkerSafetyWarning == warning) return;
            _instance.SetWorkerRole(role, warning);

            if (role == WorkerRole.Unrestricted && !string.IsNullOrEmpty(warning))
            {
                // Loud, because this is the fail-open path. It is a real operational condition, not a detail.
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
            // Explicit release on graceful shutdown. SQL Server would release it anyway when the connection
            // dropped, but releasing eagerly lets a rolling restart hand over without waiting for a timeout.
            if (_leaseConnection != null)
            {
                try
                {
                    await using var command = _leaseConnection.CreateCommand();
                    command.CommandText = "sp_releaseapplock";
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.AddWithValue("@Resource", _options.WorkerLeaseName);
                    command.Parameters.AddWithValue("@LockOwner", "Session");
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Releasing the background-worker lease failed; the connection close will release it.");
                }
                await _leaseConnection.DisposeAsync();
                _leaseConnection = null;
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
