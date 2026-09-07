# ADR-033 — Communication Visibility and Thread Permissions

**Status:** Accepted
**Related:** ADR-004 (business event visibility), ADR-010 (session-free permission evaluation), ADR-029
(tasks/communication permission models), ADR-030, CPS-001 §5 (modules 8, 9, 23, 24)

---

## Context

Conversations need permissions of their own: a private HR note on an employee record, a confidential margin
discussion on an invoice, a public note a customer may read. This phase must **not** modify Authorization.

## Decision

### §1 — THE INVARIANT

> **A communication permission can never widen access to a business record.**

Entity-level `View` is asked of the existing `IPlatformPermissionProvider` **first**. Only then does thread state
(participation, grants, visibility) decide whether the caller may read or write *this conversation*. A thread grant
can open a restricted note to a colleague who can already open the invoice; it can never open the invoice.

This platform registers **no** permission provider, **no** module access service and **no** role. The invariant is
therefore structural rather than aspirational. Proven by
`CommAccessPolicyTests.A_thread_grant_cannot_substitute_for_entity_view`.

### §2 — Its own visibility set, mapping downward onto the kernel's

`CommVisibility` = **Public** | Internal | Confidential | Restricted.

The kernel's set is frozen at Internal | Confidential | Restricted | System (ADR-004) and has **no tier for "a
customer may read this"** — it never needed one, because a business event is always staff-facing. Communication does
need one: an internal note and a public note are the *same feature* with different audiences, and two tables would
duplicate every comment behaviour (revisions, mentions, attachments, reactions) twice.

Adding `Public` to the kernel's frozen set **from outside the kernel** is exactly the cross-team edit CLAUDE.md
forbids. So `CommVisibility.ToBusinessEventVisibility` maps `Public → Internal` when an event is bridged. That is a
**narrowing** — a public note's event is staff-visible, not portal-visible — which is the safe direction. Asserted
for every value by `Communication_visibility_maps_downward_onto_the_kernels_frozen_set`.

### §3 — Elevated tiers reuse the kernel's own actions

`Confidential` is gated by `PlatformActions.ViewConfidential`; `Restricted` by `PlatformActions.ViewRestricted`. No
new action vocabulary, so a user's rights mean the same thing on a comment as on an event.

### §4 — The readable-visibility SET and the manager FLAG are separate values

`CommVisibilitySet` carries `Allowed` (what the SQL filter may pass) **and** `MayReadAnyRestricted` (the manager
grant). `Allowed` contains `Restricted` even for an ordinary user — purely so their **own** restricted comments can
be fetched — and `CanReadComment` then drops everybody else's, row by row.

Collapsing the two into one value grants every viewer every restricted comment. That is not hypothetical: it is the
bug `TimelineProjectionService` documents in its own source ("Collapsing the two into one value silently grants
every viewer every restricted event"). This platform copies the two-value shape for exactly that reason.

### §5 — The own-actor exception extends to the thread row

An employee who *opens* a Restricted note must be able to read the thread they just created. Opening a thread does
not by itself make somebody a participant, so without this they would be locked out of their own note. This mirrors
what the kernel's timeline does for a restricted event (`r.ActorEmployeeId != context.EmployeeId`) — the thread's
creator **is** its actor. Found while writing `The_creator_of_a_restricted_thread_can_read_it`.

### §6 — Authorable visibility is an INTERSECTION of two opposing bounds

A tier may be posted at when it is **both**:

- **no more open than the thread** — a Confidential thread may not carry an Internal comment; the ceiling is the
  promise made to whoever opened it at that tier;
- **readable by this caller** — offering Confidential to somebody who cannot read Confidential would let them write
  something they immediately lose sight of.

The bounds pull in **opposite directions**, which is why a single "max visibility" value cannot express them. The
first implementation here returned the *more restrictive* of the two and refused anything more open — which rejected
an Internal comment on an Internal thread, i.e. the ordinary case. The tests caught it, and the fix is the
`AuthorVisibilities` set plus `MayAuthorAt`. Both bounds are asserted separately
(`Author_visibilities_exclude_a_tier_the_caller_cannot_read`,
`Author_visibilities_exclude_tiers_more_open_than_the_thread`).

Restricted is authorable by any resolved employee: writing a private note about a record you can open is normal, and
the author can always read their own.

### §7 — Thread grants

`CommThreadPermissions`: `(PrincipalKind, PrincipalId | PrincipalKey) → Read | Comment | Moderate`, additive within
the entity. Group principals are expanded through **the same resolver mentions use** (ADR-032 §1).

- Raising or lowering a level **updates the existing row**, so grant resolution never reconciles two rows for one
  principal.
- Revoking sets `RevokedAt` — **not** a delete. "Who could read what when the decision was taken" is a fact an
  investigation needs; the row leaves the filtered index and stops affecting decisions.
- A grant to a principal nothing can expand is **refused**, not stored (ADR-032 §5).
- Listing a thread's permissions requires **Moderate**, not Read: the participant list of a restricted HR note is
  itself a fact about an investigation.

### §8 — Moderation is not conferred by creating a thread

A thread is created implicitly by the first commenter, so creator-moderates would hand moderation to whoever
happened to type first. Moderation requires an explicit `Moderate` grant or the elevated module right.

### §9 — Locking is a state, not a denial

A locked thread still **reads**. `CanComment` is false for it, so a UI does not offer a compose box — but the
service checks the lock **before** the access gate, so the caller is told `thread_locked` rather than being told
they lack permission. Ordering matters here: with the access check first, a lock surfaced as 403 "denied", which is
untrue and unactionable. Found by `A_locked_thread_still_reads_but_accepts_no_new_comment`.

### §10 — Company is checked on the row, explicitly

None of this platform's tables is in the kernel's `CompanyQueryFilters` pilot set, so **no global filter protects
them**. Every read filters `CompanyID` explicitly, and the tests prove it. Claiming filter coverage this platform
does not have would be the "a query filter is not an authorization control" mistake CLAUDE.md already records.

Absent, other-company and soft-deleted answer **identically** (`CommNotFoundException`), so a thread or comment id
cannot be used as a cross-tenant existence oracle — the rule `CommunicationAccessService` already applies to
conversations.

### §11 — Capabilities are returned, not inferred

`CommThreadCapabilities` and `CommCommentCapabilities` are computed by the policy and returned in every DTO, so a UI
**renders a decision** rather than making one. CLAUDE.md: *"Hiding a UI control is not a control."*

### §12 — Denials are audited

A read that returns nothing and a read that was refused look identical in a log otherwise, and *"why can this user
not see the thread"* is a real support question. The `Reason` is written to `CommAuditEntries` and **never** returned
to the caller — a denial that explains itself leaks facts about content the caller cannot read.

## Consequences

**Positive.** No change to Authorization. Public notes exist without touching the kernel's frozen vocabulary. The
restricted-content leak the kernel documents cannot recur here. Every denial is diagnosable.

**Negative / accepted.** Two visibility vocabularies in the product, joined by one mapping function. A grant
resolution may expand group principals per thread read — bounded, and short-circuited once `Moderate` is found.
Recipient-side elevation is not reconstructed in the notification path (CPS-001 gap G11), which withholds rather
than leaks.
