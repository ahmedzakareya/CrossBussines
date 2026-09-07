# Single-Worker-Process Control

**Introduced:** Stage 0 (Slice-003), Batch B.
**Code:** `CrossBuy/BL/Platform/RuntimeInstance.cs`. **Configuration:** `Runtime` section in `appsettings.json`.

---

## The problem

Six `BackgroundService` workers run inside the web application:

| Worker | Safe to run in two processes? | Why |
|---|---|---|
`BusinessEventDispatchWorker` | **Yes** | claims work with `UPDATE … WITH (ROWLOCK, READPAST, UPDLOCK)` — two copies partition the queue |
`CommMessageDispatcherHostedService` | **Yes** | same atomic claim on `CommMessages` |
`IntegrityCheckHostedService` | Mostly — it writes classifications | duplicate rows in the integrity log |
`CrmReminderHostedService` | **No** | **duplicate reminders**, user-visible |
`TaskGeneratorHostedService` | **No** | **duplicate tasks**, user-visible and not undoable |
`TaskScheduleMatchHostedService` | **No** | duplicate match suggestions |

Every one of them was written assuming it is the only copy running. That assumption was invisible and unenforced,
and it is broken by things nobody thinks of as a deployment change:

- an IIS application pool with `maxProcesses > 1` (a **web garden**);
- an **overlapped recycle** — the old worker process is still draining while the new one has started;
- a **second server** added behind a load balancer;
- someone running the app **from the CLI while IIS also has it up** — routine on this project.

## The control

`Runtime:RequireSingleWorkerProcess` (default **`true`**) makes exactly one process the *worker primary*, enforced by
a **SQL Server session application lock**:

```
sp_getapplock @Resource = 'CrossBuy.BackgroundWorkers',
              @LockMode = 'Exclusive',
              @LockOwner = 'Session',      -- NOT 'Transaction'
              @LockTimeout = 0             -- never block
```

The lock is taken on a **dedicated connection held for the process lifetime**. Only one session in the database can
hold it, so exactly one process wins.

### Why an application lock rather than a heartbeat table

- **No new table, no polling, no leader-election protocol.**
- **SQL Server releases it automatically** when the process dies or its connection drops. This is precisely the
  failure mode a home-grown "heartbeat row" gets wrong: a crashed process leaves a stale row, and every
  timeout you pick is either too short (two primaries) or too long (no primary).
- `@LockOwner = 'Session'` is load-bearing. `'Transaction'` would release the lock the moment the acquiring
  transaction ended — useless for a process-lifetime lease.
- `@LockTimeout = 0` is load-bearing too. A standby must find out immediately, not block a worker's startup.

### The lease is taken once per process, not once per worker

All six workers call the same `IWorkerGate`. The first caller decides; the rest observe the same answer. If each
worker took its own lock, five of the six would be refused — a process would become a standby of itself.

---

## The three states

| Role | Meaning | Workers run? | Log level |
|---|---|---|---|
**Primary** | this process holds the lease | yes | Information |
**Standby** | another process holds it | **no** — waits and re-checks every `LeaseRetrySeconds` | Information |
**Unrestricted** | the lease could not be *evaluated* | **yes** | **Error** |

`Standby` is a normal steady state for a second instance and is deliberately **not** logged as an incident.

### Unrestricted: the fail-open decision, and why

`Unrestricted` happens when the lease cannot be evaluated at all — no `DefaultConnection`, insufficient permission to
execute `sp_getapplock`, or the database is unreachable. In that case **the workers run anyway**, and the condition
is logged as an Error and shown as a red banner on the Business Event Monitor.

This is a judgement call, made explicitly:

- **Failing closed** would mean a permissions mistake silently stops the audit outbox *and* every scheduled job,
  with nothing user-visible to notice it. That is a new failure mode the system does not have today.
- **Failing open** means a configuration mistake can, at worst, reproduce today's behaviour — which is what the
  system does right now, everywhere. The risk is bounded by the status quo.

So the cost of failing open is bounded and the cost of failing closed is not. What failing open must never do is stay
quiet, which is why it is an Error, a red banner, and a field in the diagnostics JSON.

An **explicit opt-out** (`RequireSingleWorkerProcess: false`) also reports as `Unrestricted`, but with **no warning**
— it is a decision, not a fault. The two are distinguishable in `WorkerSafetyMode`:

```
"not enforced (Runtime:RequireSingleWorkerProcess = false)"      <- deliberate
"UNENFORCED — the lease could not be evaluated; workers ..."     <- fault
```

---

## Configuration

```json
"Runtime": {
  "RequireSingleWorkerProcess": true,
  "WorkerLeaseName": "CrossBuy.BackgroundWorkers",
  "LeaseRetrySeconds": 60,
  "InstanceName": ""
}
```

| Setting | Default | Notes |
|---|---|---|
`RequireSingleWorkerProcess` | `true` | The safe posture is what you get without configuring anything. |
`WorkerLeaseName` | `CrossBuy.BackgroundWorkers` | The lock is **database-scoped**, so two different databases on one instance each get their own primary — which is correct, they are separate deployments. Change it only to deliberately split a deployment. |
`LeaseRetrySeconds` | `60` | How often a standby re-tries. Long on purpose: a tight loop would add connection churn for a normal state. Floor of 5 s is enforced in code. |
`InstanceName` | `""` | Friendly name in logs and diagnostics (`web-01`, `cli-dev`). Blank ⇒ `MACHINE/PID`. |

---

## Startup logging

Every process logs one line at startup that answers "which process am I looking at, and is it the one running the
background jobs?" — the question every one of these incidents begins with:

```
CrossBuy runtime starting — instance=WEB01/8412 id=3f2a… machine=WEB01 pid=8412
  started=2026-08-03 09:14:02Z version=1.0.0.0 env=Production
  requireSingleWorkerProcess=True lease=CrossBuy.BackgroundWorkers
```

followed by exactly one of:

```
Background-worker lease 'CrossBuy.BackgroundWorkers' acquired by WEB01/8412 (PID 8412). This instance is PRIMARY.

TaskGeneratorHostedService: another process holds the background-worker lease '…'. This instance
  (WEB02/2210, PID 2210) stays on standby and will re-check in 60s.

SINGLE-WORKER ENFORCEMENT IS NOT ACTIVE on WEB01/8412 (PID 8412). <reason> If more than one process is
  running these workers, the non-outbox jobs (task generation, CRM reminders) can duplicate their output.
```

And on shutdown:

```
CrossBuy runtime stopping — instance=WEB01/8412 pid=8412 role=Primary
```

## Diagnostics

`GET /BusinessEventMonitor/Runtime` returns the same facts as JSON, behind the same `[PlatformOps]` gate as the
monitor screen (instance ids, PIDs and machine names are infrastructure detail, not public information):

```json
{
  "instance": "WEB01/8412", "instanceId": "3f2a…", "machine": "WEB01", "pid": 8412,
  "startedAtUtc": "2026-08-03T09:14:02Z", "version": "1.0.0.0", "environment": "Production",
  "workerRole": "Primary",
  "workerSafetyMode": "single-process enforced (this instance is PRIMARY)",
  "workersEnabled": true, "warning": null
}
```

The Business Event Monitor renders this as a banner above the queue: grey/green for `Primary`, amber for `Standby`
("background work is running in a different process"), red for a fault. That placement is intentional — a backlog of
`Pending` rows means something completely different depending on whether *any* process is dispatching, and diagnosing
a standby as an outbox bug is the mistake this banner prevents.

---

## Deployment guidance

### IIS

- **Application pool → Maximum Worker Processes must be 1.** A web garden is the most likely way to get two
  primaries in one deployment. With the lease on, the extra processes become standbys rather than duplicating work —
  but they serve HTTP while doing no background work, which is a waste of a process, not a design.
- **Overlapped recycle is safe now.** During the overlap the new process is a standby; it becomes primary when the
  old one's connection drops (or immediately, because graceful shutdown calls `sp_releaseapplock`).
- **`Idle Time-out` should be 0** and **`Preload Enabled` true** if you want the workers to run without a request
  first waking the app. This is unchanged by Batch B, but it is the other half of "are the workers running?".

### More than one server

Both servers may run the full application. Exactly one becomes worker primary; the other serves HTTP and stays a
standby. No configuration difference between them is needed — which is the point.

### Running from the CLI while IIS is up

This now behaves correctly: whichever started first is primary, and the other logs a standby line. Previously both
ran every worker.

### Deliberately separating a worker host

If you ever want a dedicated worker process that is *not* serving HTTP, give the web instances a different
`WorkerLeaseName`… **no** — do the opposite: leave the lease name the same and simply start the worker host first.
Different lease names mean *no* mutual exclusion, which is the reverse of what you want. The correct way to split
roles is a per-worker enable flag, and that does not exist yet — it is a Stage 1 item.

---

## Limits, stated plainly

1. **It is not a scheduler.** There is no fair handover, no priority, no "prefer this box". First to acquire wins,
   and it keeps the lease until the process ends.
2. **A standby does no background work at all.** The gate is per-process, not per-worker, so you cannot currently
   run the dispatcher on one box and the task generator on another.
3. **A network blip does not fail over instantly.** If the primary's lease connection drops, SQL Server releases the
   lock, and a standby picks it up on its next retry — up to `LeaseRetrySeconds` later. For a queue-driven outbox
   that is irrelevant; for a job with a tight schedule it would matter.
4. **`Unrestricted` is enforcement-off, not enforcement-failed-safe.** See the fail-open reasoning above. Treat a red
   banner as a real operational condition.
5. **It does not make the non-atomic workers idempotent.** It prevents *concurrent* duplication. A worker that would
   duplicate its own output when run twice in sequence still would. Making those workers idempotent is Stage 1 work;
   this control removes the concurrency case, which is the one a deployment change can cause by accident.

---

## Tests

| Test | Proves |
|---|---|
`Slice3RuntimeGateTests` (12) | identity is real and process-specific; the opt-out really opts out and does not claim enforcement; a missing connection string and an unreachable database both fail **open with a warning**; six concurrent callers evaluate the lease once; the reported mode never claims enforcement that is not happening; defaults are the safe posture |
`SqlServer/WorkerLeaseTests.Only_one_of_two_processes_becomes_the_worker_primary` | two processes, one primary; the standby reports `Standby` with **no** warning |
`…The_lease_is_held_for_the_process_lifetime_not_for_one_transaction` | `LockOwner='Session'` — ordinary database work does not drop the lease |
`…A_standby_can_take_over_after_the_primary_releases` | graceful handover on shutdown |
`…All_workers_in_one_process_share_a_single_lease` | six workers, one lock; verified from an outside session with `APPLOCK_TEST` |
`…Different_lease_names_do_not_contend` | separate deployments stay independent |
`…With_the_requirement_disabled_both_processes_run_workers` | the opt-out takes **no** lock, so it cannot leave a stray lease behind |