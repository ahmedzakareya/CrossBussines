# ADR-008 — Operator retry lives in the dispatch store, re-queues one consumer, and loses races loudly

**Status:** Accepted, implemented in Stage 0 (Slice-003) Batch B.

## Context

The outbox retries automatically: a `Failed` row becomes eligible again after `RetryBackoffSeconds` until
`MaxAttempts`, and a `Claimed` row abandoned by a dead worker is reclaimed after `StaleClaimMinutes` (ADR-003). That
covers transient failure. It does **not** cover the case an operator faces:

> A consumer failed five times because SMTP was down for an hour. The cause is fixed. The rows are `Failed` with
> `Attempts = 5`, so the dispatcher will never look at them again.

Before Batch B the only way to act on that was an `UPDATE` statement typed against production. That is unacceptable
for four separate reasons, and each one shapes the decision below:

1. **Nothing stops you resetting a `Done` row.** Replaying a completed consumer duplicates its side effects — a
   second email, a second notification. Not undoable.
2. **Nothing stops you stealing a row a worker is holding.** Two consumers then process the same event.
3. **Nothing records why.** A hand-typed `UPDATE` leaves no reason, no actor, no trace.
4. **Nothing stops you touching another company's row.**

The obvious place to put the fix — a controller action that sets `Status = 'Pending'` — is wrong, and that is the
substance of this ADR.

## Decision

**Retry is a method on `IEventDispatchStore`, not on a controller or a service.**

```csharp
Task<DispatchRetryResult> RetryAsync(
    long dispatchId, BusinessContext context, string reason, bool elevatedOverride, CancellationToken ct);
```

It lives in the store because **every rule it has to enforce is a storage concern**: which states are eligible, and
what happens when a live worker changes the row underneath you. A controller cannot enforce either without
re-implementing the claiming semantics, and a second implementation of claiming semantics is exactly how two
implementations drift.

### The rules

| Row state | Result | Why |
|---|---|---|
`Failed`, attempts < max | **Requeued** | the ordinary case |
`Failed`, attempts ≥ max | `AttemptsExhausted`, or **Requeued** with an elevated override | the ceiling exists for a reason; overriding it is an admin act with a recorded reason |
`Claimed`, stale | **Requeued** | its worker died |
`Claimed`, fresh | `HeldByWorker` | a worker is processing it right now |
`Pending` | `AlreadyPending` | already queued — "retry" would be a no-op that looks like an action |
`Done` | `AlreadyDone` — **never, not even with an override** | replaying duplicates side effects |
another company | `NotFound`, **byte-identical to a genuinely missing row** | a probe must not learn the row exists |
no reason supplied | `ReasonRequired` | the audit trail is the point of the action |

**Only the named consumer's row is touched.** Sibling consumers — especially ones that reached `Done` — are
untouched. This is the whole reason dispatch state is per-consumer (ADR-003); a retry that reset the event would
throw that away.

### Attempts are never reset

A retry re-queues the row and **keeps `Attempts`**. An elevated override grants **one** more attempt by setting
`Attempts = MaxAttempts - 1` — it moves the ceiling, it does not rewrite the past. After that attempt the row is dark
again.

Resetting `Attempts` to zero was the first design and it is wrong: it makes a permanently-failing row retryable
forever with no trace, and it destroys the only record of how many times this consumer has failed. The `Error` text
is preserved for the same reason — kept until the next outcome is known, so a retry that fails identically is visibly
not a one-off.

### The write is one conditional UPDATE carrying the whole observed row

```sql
UPDATE BusinessEventDispatch
   SET Status = 'Pending', Attempts = @newAttempts, UpdatedAt = @now
 WHERE ID = @id
   AND Status = @status
   AND Attempts = @attempts
   AND ((UpdatedAt IS NULL AND @updatedAt IS NULL) OR UpdatedAt = @updatedAt);
```

Zero rows affected ⇒ `RaceLost`, and the caller is told to refresh.

**Matching on `Status` alone is not sufficient, and this is the part that is easy to get wrong.** When a worker
**reclaims a stale `Claimed` row**, the claiming statement sets `Status = 'Claimed'` — which it already was — and
changes only `Attempts` and `UpdatedAt`. A status-only guard therefore still matches. It would flip a row a worker
had just taken back to `Pending`, and a second worker would then claim work already in flight.

**Load-then-`SaveChangesAsync` has the same hole from the other end.** EF would emit `WHERE ID = @id` with no guard
at all, so a reclaim landing between the read and the save is silently overwritten. Doing it as one statement closes
both windows.

## Consequences

**A retry can be refused for a reason the operator did not cause**, and that is intended. `RaceLost` is not an error
to be retried automatically; it means the world moved, and the operator should look again. The loser is always
**told** it lost rather than getting a success that did nothing.

**There is no bulk retry.** "Retry 400 rows" is precisely the action that should be justified one row at a time. The
friction is the feature.

**There is no endpoint that edits an event, edits a payload, deletes a row, or sets a status directly.** The only
sanctioned transition from outside the dispatcher is `Failed` or stale-`Claimed` → `Pending`, through this method.

**The `@updatedAt` parameter must be `DbType.DateTime2`.** ADO.NET infers plain `datetime` for an untyped `DateTime`,
which rounds to ~3.33 ms; compared against the `datetime2(7)` column, the equality guard then never matches and
**every** retry returns `RaceLost`. This was caught by the SQL Server tests, and it is called out here because the
next person to add a timestamp guard will hit it.

**The monitor service never writes dispatch state.** `BusinessEventMonitorService.RetryAsync` delegates to the store
and logs the outcome — success *and* refusal, because a refused elevated attempt is exactly what an auditor wants to
see. It records no `BusinessEvent`: an event about reading events would feed the queue it exists to observe.

## Alternatives rejected

**A controller action setting `Status = 'Pending'`.** Rejected — see the four failure modes above. It also
distributes the eligibility rules to every future caller.

**A `RetryRequests` table processed by the worker.** Rejected as a second queue to operate, monitor and fail. The
existing queue is the thing being retried.

**An EF `rowversion` concurrency token on `BusinessEventDispatch`.** Rejected: it needs a POCO change and a schema
change to solve something one conditional statement already solves, and the POCO entities are deliberately not
modified for platform concerns.

**Failing open on a race** (last-write-wins). Rejected — that is the double-processing bug this ADR exists to
prevent.

## Verification

- `Slice3EventMonitorTests` (30) — every refusal path; attempts and error preserved; the override grants exactly one
  attempt; cross-company refusal byte-identical to not-found; siblings untouched; and a row-by-row cross-check that
  what the grid offers is exactly what the store accepts.
- `SqlServer/EventMonitorRetryConcurrencyTests` (9) — the stale-claim race proven **deterministically**, by injecting
  a real worker reclaim between the read and the write through an EF command interceptor; the identical setup
  uncontended still succeeds (so `RaceLost` is not an artefact of the predicate); two simultaneous retries apply
  once; a fresh claim is never stolen; a re-queued row is claimed by the real dispatcher; exhausted rows stay dark
  until an override; and a 25-round contention loop asserting the safety invariant on every round.