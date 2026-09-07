# Stage Reporting UI — 04 · The Business Events pilot experience

**Phase:** R3 Phase 4
**Dataset:** `Platform.BusinessEvents.Log` **v1.2** (was 1.1) · report `Platform.BusinessEventLog`
**Definition version:** 2 (was 1)
**File:** `BL/Reporting/BusinessEventsDataset.cs`

---

## 1. The filters the brief asked for

| Required filter | Parameter | Notes |
|---|---|---|
| event type | `EventType` | multi-value, e.g. `SalesInvoice.Created,SalesInvoice.Reversed` |
| entity type | `EntityType` | multi-value (existed) |
| date range | `From` / `To` | inclusive both ends (existed) |
| actor | `ActorEmployeeId` | employee picker (existed) |
| branch **where permitted** | `FilterBranchId` | narrows only — §3 |
| status / failure state | `DeliveryState` | Any · Failed · Pending · Delivered · Not yet delivered · No consumers |
| correlation id | `CorrelationId` | exact match on the GUID |
| consumer state | `Consumer` | narrows the delivery filter to one consumer |

Four new fields carry the answer back: `DeliveryState`, `FailedConsumers`, `DeliveryAttempts`,
`DeliveryError`.

The version bump is **minor** (1.1 → 1.2) because every addition is additive: no existing field changed type,
meaning or sensitivity, so a layout saved against 1.1 still resolves every column it names. The report's
`DefinitionVersion` bumps to **2** because that number's job is to explain an archived artifact — a PDF
produced last month legitimately has no Delivery column, and "it ran against definition v1" is the answer.

---

## 2. Delivery state, and the three ways to get it wrong

Delivery is derived from `BusinessEventDispatch` — one row per (event, consumer).

**Worst-first, not last-writer.** An event with four `Done` consumers and one `Failed` is **Failed**. Ranking
by recency or by majority is what lets a single broken consumer hide behind its healthy peers.
`An_event_with_one_failed_consumer_among_healthy_ones_reads_as_failed`.

**No consumers is not delivered.** `All(Done)` over an empty set is `true`, which would quietly classify every
event nobody subscribes to as successfully delivered — the opposite of the operational truth. `None` is its own
state, and the SQL predicate carries an `Any()` guard.
`An_event_with_no_dispatch_rows_reads_as_None_not_as_delivered`.

**`Claimed` is in flight, not done.** A dispatcher that died mid-batch leaves `Claimed` rows behind, and
classifying those as delivered would hide exactly the incident the screen exists to surface.
`A_claimed_dispatch_counts_as_pending_not_as_delivered`.

### The filter is applied in SQL, before the cap

Filtering delivery in memory would cap first and filter second, so "show me the failures" over a busy month
would search only the newest 25 000 events and report **no failures** for a visibly stuck queue. The predicates
are `Any()` subqueries against `BusinessEventDispatches`, evaluated before `Take(cap + 1)`.

### Isolation is inherited, not re-asserted

`BusinessEventDispatch` carries **no `CompanyID`**. It is reached only through event ids the company-filtered
parent query already returned. That is safe and it is why this must never become a standalone dispatch query —
the comment in the source says so at the join.

---

## 3. "Branch where permitted"

Permitted is decided by the **context**, not by the parameter.

```csharp
if (context.BranchId.HasValue) { …pin to the caller's branch… }
else { var requested = query.Parameters.GetInt("FilterBranchId"); … }
```

The parameter is only readable inside the `else`. A caller already pinned to a branch never gets to supply one,
so it can **narrow** but never **widen**. A head-office reader (no pinned branch) uses it to focus.

Proven in both directions:
`A_branch_pinned_caller_cannot_use_the_branch_filter_to_reach_another_branch` and
`A_head_office_caller_may_narrow_to_one_branch`.

### It is called `FilterBranchId`, and that is not cosmetic

`BranchId` is a **reserved system parameter**. `ReportParameterBinder.EnsureSystemParameter` fills it from the
resolved `BusinessContext` unconditionally and discards anything the caller sends. Declaring a user parameter
under that key would put a scope value and a filter value in one slot: the filter would be silently overwritten
by the context, and the input would appear to do nothing.

This was caught by the compiler and the reserved-key list during implementation, not by review.
`The_branch_filter_does_not_collide_with_the_reserved_system_parameter` keeps it caught.

---

## 4. A typo returns nothing, not everything

`CorrelationId` is a GUID column. An unparseable value returns **zero rows** rather than being ignored.

A filter the user can see in the box but which silently did not apply would show the whole log — and they would
conclude the operation touched every record in it.
`An_unparseable_correlation_id_returns_no_rows_rather_than_ignoring_the_filter`.

---

## 5. What stays hidden

| Field | Treatment | Why |
|---|---|---|
| `Payload` | Confidential, separately gated, hidden by default | A payload is a summary by contract, but a summary of a sales invoice still carries its total. |
| `DeliveryError` | **Confidential, same gate as Payload**, hidden by default | Not obvious, and it is the one judgement call here. A dispatch error is operational text — but it is an *exception message*, and exception messages quote the data that broke them: a constraint violation names the value, a serializer failure quotes the payload fragment. Treating it as ordinary operational metadata would leak, through the diagnostics column, exactly what the Payload gate withholds. |
| `DedupKey` | `Never` sensitivity — nobody, including an administrator | An idempotency token has no business meaning on a page, and exposing it would let a reader infer a producer's keying scheme. |

`The_delivery_error_field_is_gated_at_the_same_tier_as_the_payload` asserts the two keys match, so loosening
one without the other fails.

**A stale error on a now-delivered consumer is not reported.** Only *failing* consumers contribute to
`FailedConsumers` and `DeliveryError`; otherwise a delivered event would display error text from an attempt
that later succeeded and read as broken.
`A_stale_error_on_a_now_delivered_consumer_is_not_reported`.

---

## 6. Unchanged from R1

The visibility model is untouched: company · branch · the two-step visibility tiering with the own-actor
exception · the three independently-granted permission keys, all unmapped-is-denied. The `mayReadAnyRestricted`
split remains two values, not one, for the reason recorded in the R1 activation report.

The new delivery fields are **operational**, not visibility-bearing: they describe what the platform did with
an event the caller was already permitted to see. They add no path to a row the tiering would have excluded.
