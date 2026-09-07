# ADR-031 — The Universal Collaboration Surface

**Status:** Accepted
**Related:** ADR-002 (entity registry), ADR-030, CPS-001 §5 (modules 33–43)

---

## Context

The requirement: *every business entity must support Timeline, Comments, Mentions, Attachments and Activity History
**without duplicating code***, and specifically Task, CRM, Support, Accounting, Inventory, Project, HR and
Manufacturing comments (modules 36–43).

`IEntityRegistry` already owns the canonical entity vocabulary and carries capability flags — `SupportsComments`,
`SupportsFiles`, `SupportsFollowers`, `SupportsTimeline` — which its own comment says describe "what is WIRED
today". At the time of writing, exactly **three** codes carry `SupportsComments` (SalesInvoice, PurchaseInvoice,
Quotation) and **none** carries `SupportsFollowers`.

The two naive options were both bad:

- **Flip eight registry flags.** `EntityRegistry` is platform-kernel code owned by another work stream, and
  CLAUDE.md names it and its neighbours architectural invariants requiring owner coordination. Eight uncoordinated
  edits is precisely what that rule forbids.
- **Ignore the registry and accept any string.** That is `DocComment.EntityType` before Slice-003 — free text,
  which its own code calls "the one surviving instance of the problem ADR-002 exists to prevent".

## Decision

One gate — `ICommEntitySurface` — asked by every write path, deciding in three steps.

**1. EXISTENCE: the registry.** An unregistered code is refused, whatever configuration says. Configuration may
*onboard* a registered entity; it may never *invent* one. This is the ADR-002 rule, and the allow-list does not get
to override it.

**2. DENY LIST wins outright.** `BlockedEntityCodes` beats even a registry flag, so a deployment that discovers a
leak (a confidential note printed on a customer-facing document) can pull one entity out of the surface without a
code change and without waiting for a release.

**3. REGISTRY FLAG, else ALLOW LIST.** `EnabledEntityCodes` is **additive** on top of the flag.

Capability → flag mapping:

| Capability | Registry flag | Why |
|---|---|---|
| Comments | `SupportsComments` | |
| Mentions | `SupportsComments` | A mention exists only inside a comment. A separate flag would create a state — comments on, mentions off — that no code path can produce. |
| Attachments | `SupportsFiles` | That flag *is* the kernel's statement about whether the entity has a file surface at all. |
| Followers | `SupportsFollowers` | |
| Timeline | `SupportsTimeline` | |

### The divergence is published, not hidden

`GetOnboardingGap()` returns every code enabled by configuration whose registry flag is still false — the exact
list the registry owner must act on. `CommSurfaceDecision.GrantedByConfiguration` says the same thing per call.
Without this, "we onboarded it in config" would silently become "the registry and the platform disagree and nobody
knows".

### The allow-list has no authority over the kernel

`PlatformEventTimelineSource` reads `IEntityRegistry` **directly**, not the surface. A configuration entry cannot
make `ITimelineProjectionService` accept an entity the kernel has not onboarded.

This was an actual defect in this phase, caught by `CommTimelineAndDispatchTests`: routing that guard through the
surface made a configured-but-unregistered entity look eligible, so the kernel threw and the aggregator reported
`error:` — a working timeline reported as broken. The fix is one of the more useful things this ADR records,
because the mistake is natural: the surface *looks* like the right question to ask.

### Casing

Configuration entries are matched `OrdinalIgnoreCase`. An operator typing a code into `appsettings.json` will get
the casing wrong eventually, and silently ignoring their entry is worse than accepting it. The value **stored** is
always the registry's canonical casing.

## Consequences

**Positive.** Modules 36–43 need no code — they are configuration entries. A deployment onboards an entity today
and the registry catches up when its owner is ready, with the difference reported rather than buried. No cross-team
edit to a file this work stream does not own.

**Negative / accepted.** Two sources of truth for a capability, reconciled by a report rather than by the type
system. Modules 36 (Task) and 38 (Support) remain blocked because **no registry code exists** for them — a stated
boundary, not a workaround (CPS-001 gap G9).
