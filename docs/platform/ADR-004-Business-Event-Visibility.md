# ADR-004 — Event visibility is a frozen vocabulary, filtered before anything reads it

**Status:** Accepted, implemented in Platform Kernel slice 1. Slice 2 extended the authorization pipeline to the
notification path and added a fourth entity scope; the vocabulary itself is unchanged.

## Context

A unified timeline concentrates history that was previously scattered across screens each with its own
authorization. On a single `Employee` or `Customer` object, the event log will eventually hold payroll,
appraisal, pricing, margin and credit facts side by side. Every downstream consumer inherits whatever filter
the timeline applies — and the AI Context Pipeline is specified to receive context from
*Timeline + Relations + Documents + Communication*.

`IAiService` already states the project's own rule:

> ".NET stays the source of truth and applies user permissions **BEFORE** calling here — the AI only proposes,
> never authorizes."

Without a per-event visibility model, the first "summarise this customer" feature is a data leak that
technically satisfies the module RBAC of the screen it was launched from.

A second risk: the platform had just banned free-text `EntityType`, then introduced `Visibility` as an
unconstrained `nvarchar`. The same mistake, one column over.

## Decision

**A frozen four-value vocabulary, enforced in three places, and applied at the projection — not at the consumer.**

| Value | Meaning | Who may read it |
|-------|---------|-----------------|
| `Internal` | ordinary document facts | anyone who may `View` the entity |
| `Confidential` | cost, margin, credit decisions | `ViewConfidential` (elevated module right) |
| `Restricted` | personal / sensitive facts | `ViewRestricted` (module manager) **or the actor of that event** |
| `System` | machine bookkeeping | same gate as `Restricted`; never surfaced below it |

1. Frozen in `BusinessEventVisibility` with `IsValid`, mirrored by `CK_BusinessEvents_Visibility`, and validated
   by `RecordAsync` before insert. Three layers, so an unrecognised value cannot enter from code, from SQL, or
   from a future producer.
2. The column is `NOT NULL`. There is no "unset" — a producer must decide.
3. Filtering happens in `ITimelineProjectionService`, which is the **only** read path to `BusinessEvents`. The
   UI never queries the table. Adding a consumer therefore cannot forget the filter, because the consumer does
   not do the filtering.
4. Elevated tiers resolve through `IPlatformPermissionProvider` (`ViewConfidential`, `ViewRestricted`), so the
   thresholds are the module's existing policy rather than a new one invented by the kernel.
5. `View` denial throws `PlatformAccessDeniedException` → HTTP **403**. "You may not view this record" must stay
   distinguishable from "this record has no history"; collapsing them makes the UI misreport a permission
   problem as an empty widget.

### The own-actor exception, and the bug it caused

`Restricted` is readable by its own actor. That is enforced in two halves: the SQL filter admits `Restricted`
rows so the caller's own can be found, then a row-level pass drops every row whose actor is someone else.

The first implementation derived "may read restricted" from *membership of `Restricted` in the allowed set* —
which the own-actor branch had just added. The row-level check therefore never fired and **every viewer could
read every restricted event**. The visibility test caught it before it shipped.

The two notions are now returned separately (`visibilities`, `mayReadAnyRestricted`) with a comment at both ends
explaining why they must not be collapsed. Recorded here because the mistake is easy to reintroduce and looks
correct while wrong.

## Consequences

* Producers must choose a visibility. `Internal` is the default on `BusinessEventRecord`; the pilot's
  invoice-issued and invoice-updated facts are legitimately `Internal` — everything in their payload is already
  visible to anyone who may open the invoice.
* Two extra permission checks per timeline read. Both hit the module access services, which are already
  per-request and cheap; if that ever matters, the decision is cacheable per (context, entity) with no contract
  change.
* Visibility is coarse (four tiers, not per-field). Sufficient for the timeline; a field-level model would need
  payload-schema awareness and belongs to whichever slice actually requires it.
* When AI Context arrives it calls the same provider and reads the same projection, so "AI never bypasses
  application authorization" is structural rather than aspirational.

## Alternatives rejected

* **No visibility column; rely on the entity's module RBAC.** Rejected: it is the entity that is authorized, not
  the fact. Every event on an invoice would be equally readable, so no cost or margin fact could ever be
  recorded there.
* **Free-text visibility / a lookup table.** Rejected: the filter must be exhaustive and compile-time checkable.
  An unrecognised value silently widens or hides history, and this is the exact mistake just banned for
  `EntityType`.
* **Filtering in each consumer.** Rejected: N places to forget one rule.
* **Per-field redaction inside the payload.** Rejected as premature; the payload is a summary with a 64 KB cap
  and no sensitive fields by contract.

## Slice 2 addendum

* **The same provider now gates notifications, not just reads.** `NotificationProjection` calls
  `IPlatformPermissionProvider` for `View` on every candidate recipient before creating a notification, so the
  platform never hands a user a link to a record they cannot open. The session-scoped limitation on that check
  is recorded in ADR-006 rather than glossed over.
* **A fourth scope, `Manufacturing`,** joined the routing table. It delegates to the inventory service because
  manufacturing has no RBAC of its own; the elevated tiers still map to real gates (`doc` / `manage`).
* **Every slice-2 event is `Internal`,** and that is a decision rather than a default: the three payloads carry
  document numbers, names, statuses, quantities and totals — all already visible to anyone who may open the
  record — and deliberately no cost, margin or credit figure. The first slice-2-era event to carry one will need
  `Confidential`, and until then the elevated tiers stay exercised by tests only. That gap is listed openly in
  `Slice-002` §8 instead of being presented as coverage.
* **Legacy (reconstructed) items are `Internal` too**, for the same reason: derived history carries no
  classification of its own, and `Internal` is the only honest value (ADR-005).

## Verification

* `Visibility_filtering_hides_events_the_caller_may_not_read` — plain viewer sees only `Internal`; each grant
  adds exactly one tier
* slice 2: `View_denial_blocks_the_timeline_for_every_pilot_entity` (theory over all three pilots) ·
  `Company_isolation_holds_for_every_pilot_entity` · `A_recipient_who_cannot_open_the_record_is_not_notified`
* `A_restricted_event_is_readable_by_its_own_actor` — own row visible, a colleague's is not
* `A_caller_denied_View_gets_an_access_error_not_an_empty_timeline`
* `Company_isolation_is_enforced_on_the_timeline`
* `Branch_scoped_events_stay_visible_to_a_caller_with_no_branch`
* `Unknown_entity_codes_and_bad_visibility_are_refused`
