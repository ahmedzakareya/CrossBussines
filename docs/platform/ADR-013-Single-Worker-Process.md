# ADR-013 — One process runs the background workers, enforced by a SQL Server session application lock, failing open with an alarm

**Status:** Accepted, implemented in Stage 0 (Slice-003) Batch B.

## Context

Six `BackgroundService` workers run inside the web application. Two of them — the business-event dispatcher and the
email dispatcher — claim work atomically (`UPDATE … WITH (ROWLOCK, READPAST, UPDLOCK)`, ADR-003/007), so two copies
partition the queue instead of duplicating it. **The other four do not:**

| Worker | Second copy does what |
|---|---|
`TaskGeneratorHostedService` | creates **duplicate tasks** — user-visible, not undoable |
`CrmReminderHostedService` | sends **duplicate reminders** — user-visible |
`TaskScheduleMatchHostedService` | duplicate match suggestions |
`IntegrityCheckHostedService` | duplicate integrity-log classifications |

Every one of the six was written assuming it is the only copy running. That assumption was invisible and unenforced,
and it is broken by changes nobody thinks of as deployment changes: an IIS application pool with `maxProcesses > 1`
(a web garden), an overlapped recycle where the old process is still draining, a second server behind a load
balancer, or someone running the app from the CLI while IIS also has it up — routine on this project.

## Decision

`Runtime:RequireSingleWorkerProcess` (default **`true`**) makes exactly one process the worker primary, enforced by a
**SQL Server session application lock** held on a dedicated connection for the process lifetime:

```
sp_getapplock @Resource = 'CrossBuy.BackgroundWorkers',
              @LockMode = 'Exclusive',
              @LockOwner = 'Session',
              @LockTimeout = 0
```

All six workers `await IWorkerGate.WaitUntilAllowedAsync(...)` after their startup delay. `IRuntimeInstanceInfo`
exposes instance name, instance id, machine, PID, process start time, version, environment, worker role and safety
mode.

### Why an application lock and not a heartbeat table

- No new table, no polling, no leader-election protocol.
- **SQL Server releases it automatically** when the process dies or its connection drops. This is exactly what a
  home-grown heartbeat row gets wrong: a crashed process leaves a stale row, and every timeout you pick is either too
  short (two primaries) or too long (no primary).
- The arbiter is the shared database, which is the only thing all instances already agree on.

Two parameters are load-bearing:

- `@LockOwner = 'Session'`, **not `'Transaction'`** — a transaction-owned lock is released the moment the acquiring
  transaction ends, which is useless for a process-lifetime lease.
- `@LockTimeout = 0` — a standby must find out immediately rather than blocking a worker's startup.

### The lease is taken once per process, not once per worker

All six workers share one `IWorkerGate` singleton. The first caller decides; the rest observe the same answer. If each
worker took its own lock, five of six would be refused and a process would become a standby of itself.

### Three states, and the fail-open decision

| Role | Meaning | Workers run? | Log level |
|---|---|---|---|
**Primary** | this process holds the lease | yes | Information |
**Standby** | another process holds it | **no** — re-checks every `LeaseRetrySeconds` | Information |
**Unrestricted** | the lease could not be **evaluated** | **yes** | **Error** |

`Standby` is the intended steady state of a second instance and is deliberately not logged as an incident.

`Unrestricted` covers: no `DefaultConnection`, insufficient permission to execute `sp_getapplock`, database
unreachable. **The workers run, and the condition is an Error plus a red banner on the Business Event Monitor plus a
field in the diagnostics JSON.**

This is a judgement call and it is made explicitly:

- **Failing closed** would mean a permissions mistake silently stops the audit outbox *and* every scheduled job, with
  nothing user-visible to notice. That is a **new** failure mode the system does not have today.
- **Failing open** means a configuration mistake can, at worst, reproduce today's behaviour — which is what the system
  does right now, everywhere. The risk is **bounded by the status quo**.

So the cost of failing open is bounded and the cost of failing closed is not. What failing open must never do is stay
quiet.

An **explicit opt-out** (`RequireSingleWorkerProcess: false`) also reports `Unrestricted` but with **no warning** — it
is a decision, not a fault. The two are distinguishable in `WorkerSafetyMode`
(`"not enforced (Runtime:RequireSingleWorkerProcess = false)"` versus
`"UNENFORCED — the lease could not be evaluated…"`), and the startup logger says so at Warning, because the absence of
a message is a bad way to communicate an unsafe posture.

### Graceful handover

`WorkerGate.DisposeAsync` calls `sp_releaseapplock` explicitly. SQL Server would release the lock anyway when the
connection closed, but releasing eagerly lets a rolling restart hand over promptly instead of waiting for a timeout.

## Consequences

**Behaviour change on multi-process deployments.** The non-primary process now runs **no** background work where
previously it ran all of it. That is the fix; but anyone who believed two processes were sharing the load was relying
on duplication.

**A standby does no background work at all.** The gate is per-process, not per-worker, so you cannot currently run the
dispatcher on one box and the task generator on another. Splitting roles needs per-worker enable flags — Stage 1.

**Failover is not instant.** If the primary's lease connection drops, a standby picks it up on its next retry, up to
`LeaseRetrySeconds` later. Irrelevant for a queue-driven outbox; it would matter for a tightly-scheduled job.

**The lock is database-scoped**, so two different databases on one SQL Server instance each get their own primary —
correct, they are separate deployments. Changing `WorkerLeaseName` means *no* mutual exclusion between the groups,
which is the reverse of what someone trying to "separate a worker host" usually wants; the docs say so explicitly.

**It does not make the four non-atomic workers idempotent.** It removes the *concurrency* case — the one a deployment
change causes by accident. A worker that would duplicate its own output when run twice in sequence still would. That
is Stage 1 work.

**IIS guidance follows from it:** application-pool Maximum Worker Processes must be 1 (a web garden now yields
standbys serving HTTP and doing no background work — a wasted process, not a design), and overlapped recycle is now
safe.

## Alternatives rejected

**A config flag that only logs.** Rejected outright. A setting called `RequireSingleWorkerProcess` that requires
nothing is a lie, and it is worse than no setting because it creates false confidence.

**A `WorkerLease` heartbeat table with a timestamp and a TTL.** Rejected — see above: crash detection becomes a
timeout you cannot choose correctly, and it adds a table, a writer and a poller.

**Moving the workers into a separate Windows service or Hangfire.** Rejected for Stage 0 scope: it changes the
deployment topology and the DI story for six workers, and Stage 0 is stabilization. The lease makes the current
topology safe; a topology change can be considered on its own merits later.

**A distributed lock in Redis.** Rejected — a new infrastructure dependency for something the existing database does
natively and durably.

**Per-worker leases** (one lock per worker name). Rejected as premature: it enables role-splitting nobody has asked
for, at the cost of six locks, six connections and six failure modes. The single lease can be split later without
changing any worker's code, because every worker already calls the same gate.

## Verification

- `Slice3RuntimeGateTests` (12, in-process) — identity is real and process-specific (process start time, not "now");
  a blank configured `InstanceName` falls back to machine/PID; before evaluation the role is `Unknown` and workers are
  **not** enabled; the opt-out opens immediately and does **not** claim enforcement; a missing connection string and
  an unreachable database both fail **open with a warning that names the cause**; six concurrent callers evaluate the
  lease once; the reported mode never claims enforcement that is not happening; the defaults are the safe posture.
- `SqlServer/WorkerLeaseTests` (6, real SQL Server, disposable database) — two processes, one primary and one standby
  with **no** warning; the lease survives unrelated database work (proving `LockOwner='Session'`); a standby takes over
  after the primary releases; six workers in one process share one lock, verified from an outside session with
  `APPLOCK_TEST`; different lease names do not contend; and the opt-out takes **no** lock, so it cannot leave a stray
  lease behind that a later enforced process would be refused by.