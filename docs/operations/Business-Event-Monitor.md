# Business Event Monitor — operations guide

**Screen:** Administration → System setup → **Business event monitor** (`/BusinessEventMonitor`).
**Introduced:** Stage 0 (Slice-003), Batch B.
**Code:** `BL/Platform/BusinessEventMonitorService.cs`, `Controllers/BusinessEventMonitorController.cs`,
`Views/BusinessEventMonitor/`. **Retry:** `IEventDispatchStore.RetryAsync`.

---

## What it is for

The Platform Kernel writes every durable business fact to `BusinessEvents` inside the transaction that produced it,
then fans it out through `BusinessEventDispatch` — **one row per (event, consumer)**. Until this screen existed,
diagnosing that queue meant querying two tables by hand, and the only way to retry a failed consumer was an
`UPDATE` statement typed against production.

This screen answers four questions:

1. **Did the fact get recorded?** Find the event by entity, type, correlation id or uid.
2. **Did every consumer process it?** One row per consumer, with its own status, attempts and error.
3. **Why did a consumer fail?** The error text, and the payload it was handed.
4. **Can I safely make it try again?** One guarded action, on one consumer's row.

It is an **operator** tool, not an end-user one. The document timeline widget (`_DocEventTimeline`) is the end-user
view of the same data and has a different audience and a different gate.

---

## Access

`[SessionValidation]` + `[PlatformOps]`. `PlatformOpsAttribute` allows:

- an ASP.NET Identity role in `Admin`, `Administrator`, `SuperAdmin`, `PlatformOps` — **or**
- accounting **`manage`** (the ChiefAccountant tier: the highest existing business right, which already governs
  period close, year-end and role assignment).

**Elevated** rights (cross-company viewing, Restricted/System payloads, the max-attempts override) require the
**admin role specifically** — the accounting tier is not enough.

### Two limitations you must know

**1. "Open when unconfigured".** `AccountingAccessService.CanAsync` returns `true` for every action when its role
table is empty company-wide. On such an install **this gate is open to any authenticated employee**. That is a
property of the existing access service, not of this screen, and it is why Stage 1's access-service work is the real
fix. On any install where accounting roles are configured, the gate is real.

**2. There is no platform-operations role table yet.** Creating one is Stage 1 RBAC work. This attribute composes the
two strongest signals that already exist rather than inventing a third and pretending it is governed.

---

## Reading the screen

### The runtime banner

Above the queue, because a backlog means something completely different depending on whether *any* process is
dispatching:

| Banner | Meaning | What to do |
|---|---|---|
grey/green **PRIMARY** | this process is dispatching | normal |
amber **STANDBY** | another process is dispatching | normal on a second instance. Check *that* process if the queue is not draining. |
red **UNENFORCED** | the single-worker lease could not be evaluated | read the warning text; see [Single-Worker-Process.md](../deployment/Single-Worker-Process.md) |

Diagnosing a standby as an outbox bug is the specific mistake this banner exists to prevent.

### The six cards

Counted over **the same filtered predicate as the grid**, in the database, and they move with the filters. A filtered
grid under whole-range counts would read as a data bug.

| Card | Meaning |
|---|---|
**Pending** | queued, waiting for a dispatcher pass |
**Claimed** | a worker holds it right now — or held it and died (see *stale claims*) |
**Failed** | the consumer threw. `Error` holds the reason. Retryable until `MaxAttempts`. |
**Done** | this consumer finished |
**Exhausted** | `Failed` **and** `Attempts >= MaxAttempts` — the dispatcher will not pick it up again |
**Events** | distinct events in the filtered range (the other five count dispatch **rows**) |

### Row status vs. event completion

A row's badge is **that consumer's** status. The small green *event done* marker means `BusinessEvents.CompletedAt`
is set — i.e. **every** consumer finished. One consumer `Done` while the event is not complete is normal and must
not be read as an inconsistency.

### Filters

Entity type, entity #, event type (contains), consumer, dispatch status, date from/to, min attempts, correlation id,
event uid, branch, errors-only, page size — and **company**, rendered only for an elevated operator.

An unregistered value for entity type, consumer or status returns **nothing** rather than being ignored. The
dangerous failure mode for a filter is "unknown value ⇒ filter dropped ⇒ everything returned", so unknown values are
translated to a false predicate.

Paging is server-side, capped at 100 per page, ordered `CreatedAt desc, EventId desc, DispatchId` — two unique keys
after the timestamp, so paging cannot repeat or skip a row when many events share a timestamp (which they do: events
written in one transaction share a `CreatedAt`).

### Payload visibility

Opening this screen does **not** grant sight of every payload.

| Visibility | Payload shown? |
|---|---|
`Internal`, `Confidential` | yes, to any operator who can open the screen |
`Restricted`, `System` | **masked** unless the caller is elevated (admin role) |

Masked means the payload is *absent from the response*, not hidden by CSS. The metadata still shows, so the row stays
diagnosable without it. Payloads over 8 KB are truncated with an explicit marker, and the real byte size is still
reported. A payload that is not valid JSON is shown **as stored** rather than throwing the screen away — a corrupt
payload is exactly what an operator needs to see.

---

## Retry — what it can and cannot do

**One button, one consumer's row, one reason required.** There is deliberately **no** endpoint that edits an event,
edits a payload, deletes a row, or sets a status directly.

### Eligibility

| Row state | Retryable? | Why |
|---|---|---|
`Failed`, attempts < max | **yes** | the ordinary case |
`Failed`, attempts >= max | yes, **with an elevated override** | the ceiling exists for a reason; overriding it is an admin act with a recorded reason |
`Claimed` and **stale** (older than `StaleClaimMinutes`) | **yes** | its worker died |
`Claimed` and **fresh** | no — `HeldByWorker` | a worker is processing it right now |
`Pending` | no — `AlreadyPending` | already queued; "retry" would be a no-op that looks like an action |
`Done` | **no — never** | replaying a completed consumer duplicates its side effects (a second email) |
another company's row | no — reported as **not found** | see below |

The grid computes eligibility server-side and renders it as flags. The screen never re-implements the rule, and the
store enforces the same rule again — a test asserts the two agree row by row.

### Attempts are never reset

A retry re-queues the row and **keeps `Attempts`**. The failure history is audit information; clearing it would let a
row be retried forever without trace.

An elevated override grants **one** more attempt by moving the ceiling, not by rewriting the past: `Attempts` becomes
`MaxAttempts - 1`. After that one attempt the row is dark again. The override is not a licence.

The previous `Error` is **preserved** until the outcome is known, so a retry that fails the same way is visibly not a
one-off. `MarkFailedAsync` overwrites it on the next genuine failure; `MarkDoneAsync` clears it on success.

### Cross-company probing

A retry against another company's dispatch row returns **byte-identical text to a genuinely missing row**. A probe
must not be able to tell "exists elsewhere" from "does not exist". A test asserts the two messages are equal.

### The race with a live worker

Retry is a **single conditional UPDATE** carrying the whole row state that was read — `Status`, `Attempts` **and**
`UpdatedAt` — and it applies only if none of them moved:

```sql
UPDATE BusinessEventDispatch
   SET Status = 'Pending', Attempts = @newAttempts, UpdatedAt = @now
 WHERE ID = @id AND Status = @status AND Attempts = @attempts
   AND ((UpdatedAt IS NULL AND @updatedAt IS NULL) OR UpdatedAt = @updatedAt);
```

Matching on `Status` alone is **not sufficient**, and this is the subtle part: when a worker **reclaims a stale
`Claimed` row**, the status stays `Claimed` and only `Attempts`/`UpdatedAt` change. A status-only guard would still
match, flip a row a worker had just taken back to `Pending`, and let a second worker claim work already in flight.

Zero rows affected ⇒ `RaceLost`, and the operator is told to refresh. **The loser is always told it lost**, rather
than getting a success that did nothing.

> Note for anyone editing this: the `@updatedAt` parameter must be `DbType.DateTime2`. ADO.NET infers plain
> `datetime` for an untyped `DateTime`, which rounds to ~3.33 ms; compared against the `datetime2(7)` column the
> equality guard then never matches and **every** retry returns `RaceLost`. This was caught by the SQL Server tests.

### Outcomes

| Outcome | Meaning |
|---|---|
`Requeued` | re-queued; the dispatcher will pick it up next pass |
`ReasonRequired` | no reason supplied — the audit trail is the point of the action |
`NotFound` | no such row, **or** it belongs to another company |
`AlreadyDone` / `AlreadyPending` | refused; nothing changed |
`HeldByWorker` | a worker holds a fresh claim |
`AttemptsExhausted` | needs an elevated override |
`NotAuthorized` | a non-admin asked for the override. **Refused, not silently downgraded** — a silent downgrade would look like it worked. |
`RaceLost` | the row changed underneath; refresh and retry |

### Audit

Every retry is logged at Information — **including refusals**, because a refused elevated attempt is exactly what an
auditor wants to see:

```
Business Event Monitor retry: dispatch=812 event=4471 consumer=NotificationProjection
  outcome=Requeued override=False actor=7 company=1 reason=smtp restored
```

The screen itself records **no** business event. Recording an event about reading events would feed the queue it
exists to observe.

---

## Common tasks

**"A notification never arrived."** Filter by entity type + entity #. If there is no event, the producer never
recorded one (the entity may not be onboarded — that is a gap, not a bug). If the event exists and
`NotificationProjection` is `Done`, the notification was written; look at delivery. If it is `Failed`, read the
error.

**"The queue is not draining."** Check the runtime banner first. `STANDBY` ⇒ another process is dispatching; look
there. Red ⇒ the lease is unevaluated but workers are running, so the cause is elsewhere. `PRIMARY` with a growing
`Pending` count ⇒ look at `Failed` and at the application log for `Business event dispatch pass failed`.

**"Everything is Exhausted after an outage."** Fix the cause, then retry the rows with the override, giving the
reason. Each gets one attempt. Retry per row deliberately — there is no bulk retry, because "retry 400 rows" is
exactly the action you want an operator to have to justify one at a time.

**"I need to see a Restricted payload."** You need the admin role. If you have it and the payload is still masked,
the elevated flag is not being granted — check role membership, not this screen.

---

## What this screen deliberately does not do

| Not provided | Why |
|---|---|
edit or delete an event | `BusinessEvents` is append-only. It is the audit log. |
edit a payload | it is the recorded fact; editing it would make the log fiction |
set a status directly | the only sanctioned transition from outside the dispatcher is `Failed`/stale-`Claimed` → `Pending`, through `RetryAsync` |
reset a `Done` consumer | duplicates side effects that cannot be taken back |
bulk retry | see above — the friction is the feature |
purge or archive | retention is a separate decision with its own design; nothing is deleted today |

---

## Tests

| Suite | Count | Covers |
|---|---|---|
`Slice3EventMonitorTests` | 30 | company isolation (including that a hand-edited `CompanyId` cannot widen scope); details indistinguishable from not-found across companies; Restricted/System masking both ways; oversized and corrupt payloads; server paging stable across a shared timestamp; page-size cap; unregistered filter values return nothing; summary follows the filter; every retry refusal; attempts and error preserved; the override grants exactly one attempt; reason mandatory; sibling consumers untouched; the grid's flags match what the store accepts, row by row |
`SqlServer/EventMonitorRetryConcurrencyTests` | 9 | a retry that loses to a worker reclaim is **rejected**, proven by injecting the reclaim between the read and the write; the same setup uncontended still succeeds; two simultaneous retries apply once; a fresh claim is never stolen; a re-queued row is claimed by the real dispatcher; exhausted rows are dark until an override; siblings untouched on the real schema; company isolation on SQL Server; and a 25-round contention loop asserting the safety invariant every time |