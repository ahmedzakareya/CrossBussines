# 22 — Architecture Decision Index

## Scope
Every architectural decision that is **recorded**, every decision that is **implicit in code but never written
down**, and every decision that is **still owed** before the next stages.

## A. Recorded decisions (formal ADRs)

All twelve live in `docs/platform/`. ADR-001…007 cover the Platform Kernel; ADR-008, 009, 012, 013 and 016 were
issued in Stage 0 (Slice-003) Batch B.

| ADR | Decision | Status | Slice |
|---|---|---|---|
[ADR-001](../platform/ADR-001-Transactional-Business-Events.md) | Business events are written **inside** the business transaction (transactional outbox); notifications stay after-commit best-effort | Accepted | 1, extended in 2 |
[ADR-002](../platform/ADR-002-Entity-Registry.md) | One entity registry, **promoted from `TaskLinkResolver`** rather than built in parallel; frozen PascalCase codes, ordinal comparison | Accepted | 1, expanded in 2 |
[ADR-003](../platform/ADR-003-Dispatch-State-Per-Consumer.md) | Dispatch state is **per consumer**; work is claimed **by status**, never by an `EventId` cursor | Accepted | 1, validated in 2 |
[ADR-004](../platform/ADR-004-Business-Event-Visibility.md) | Frozen four-value visibility vocabulary, filtered at the projection, not at consumers | Accepted | 1, extended in 2 |
[ADR-005](../platform/ADR-005-Legacy-Timeline-Adapters.md) | Legacy history is **reconstructed at read time**, per entity, and never fabricated (no backfill; no fact without an honest timestamp) | Accepted | 2 |
[ADR-006](../platform/ADR-006-Notification-Projection.md) | Notifications become an outbox consumer; the two legacy producers were **deleted** rather than flag-gated | Accepted | 2 |
[ADR-007](../platform/ADR-007-SQL-Server-Dispatch-Locking.md) | Dispatch locking is verified against a **real SQL Server on a disposable database**; skipped (never silently passed) without a connection string | Accepted | 2 |
[ADR-008](../platform/ADR-008-Operator-Dispatch-Retry.md) | Operator retry lives in the **dispatch store**, re-queues **one consumer**, never resets `Attempts`, and loses races **loudly** (`RaceLost`) via a conditional UPDATE carrying `Status` + `Attempts` + `UpdatedAt` | Accepted | 3 (Stage 0 B) |
[ADR-009](../platform/ADR-009-Platform-Operations-Authorization.md) | Platform-operations screens are gated by **composing existing rights** (admin role **or** accounting `manage`); elevation is admin-only; **opening the screen grants no `Restricted`/`System` payload** | Accepted | 3 (Stage 0 B) |
[ADR-012](../platform/ADR-012-Deployment-Manifest-And-Schema-History.md) | Deployment state is an **append-only log keyed by name and SHA-256**; ordering lives in a **generated** manifest; no migration framework; reconciliation is an **explicit baseline**, never an inference | Accepted | 3 (Stage 0 B) |
[ADR-013](../platform/ADR-013-Single-Worker-Process.md) | **One process** runs the background workers, enforced by a SQL Server **session** application lock; three states (Primary/Standby/Unrestricted); **fails open with an alarm**, and the reasoning for that is recorded | Accepted | 3 (Stage 0 B) |
[ADR-016](../platform/ADR-016-Reproducible-Architecture-Evidence.md) | Architecture metrics come from **checked-in scanners**; a measurement that cannot be re-run is not evidence; reconcile against an independent count before publishing; never widen a detector until it stops complaining | Accepted | 3 (Stage 0 B) |

Supporting specifications: [PKS-001](../platform/PKS-001-Platform-Kernel-Specification.md) (the kernel contract),
[Slice-002](../platform/Slice-002-Kernel-Expansion.md) (the expansion record) and
[Slice-003](../platform/Slice-003-Stage-0-Stabilization.md) (Stage 0 stabilization).

**Numbering note.** Section D below previously *proposed* ADR numbers 008–017 for the decisions it lists. Five of
those numbers have now been issued for Stage 0 Batch B decisions. Two of the five kept their proposed subject
(**012** deployment, **013** worker safety); three were reallocated (**008**, **009**, **016**). The still-owed
proposals have been renumbered from 018 upward in section D, and the old numbers are shown alongside so the earlier
recommendation is still traceable. The proposal was a list of subjects, not an allocation.

**Coverage note: 12 ADRs, still covering the Platform Kernel and its operations only.** The other 13 modules have no
recorded decisions.

## B. Implicit decisions — in code, never written down

These are real, load-bearing decisions discovered during this pass. They should be promoted to ADRs so they stop
being tribal knowledge.

| # | Implicit decision | Evidence | Why it matters |
|---|---|---|---|
B-1 | **`StockService` is the sole stock + GL writer**; every module routes stock through it | 15 `ScopedTx` sites; comments say "the sole stock + GL writer"; `ManufService` delegates | the main reason financial integrity holds; a future module must not bypass it |
B-2 | **`JournalEntryService` is the sole journal writer**; reversal is by mirror entry, never mutation | `ReverseAsync` creates `JournalType="Reversing"`, `SourceType="Reversal"` | audit correctness depends on it |
B-3 | **Migrations are abandoned in favour of idempotent SQL scripts** | empty `Migrations/`, 105 scripts, `docs/DEPLOYMENT.md` | a new developer will otherwise run `dotnet ef migrations add` |
B-4 | **Notifications are best-effort, after commit, in `try/catch`** | ~25 call sites, identical comment | now formalised for events by ADR-001, but never recorded for notifications themselves |
B-5 | **`ScopedTx` own-or-join is the only transaction primitive** | `BL/ScopedTx.cs`, 57 sites | the mechanism that lets outer services wrap inner writers |
B-6 | **Decimal precision is pinned per column so the EF model equals the DB on all 352 decimal properties** | `CrossDbContext` `pin` dictionary + integrity assertion | a silent 2dp rounding bug already happened once (HM-D27) |
B-7 | **Access services are "open when unconfigured"** to avoid lockout | `AnyRoleConfiguredAsync()` in 3 services | a security posture decision with no written rationale |
B-8 | **Company isolation is a column convention with no global filter** | no `HasQueryFilter` | the largest unrecorded risk acceptance in the system |
B-9 | **`DefaultCompanyId = 1`** is the de-facto tenancy model | 8 controllers | `BusinessContextAccessor` documents it as "the current reality, not a new decision" — the only place it is acknowledged |
B-10 | **All async work is in-process; there is no broker** | 5 hosted services | implies a single-node deployment that is nowhere documented |
B-11 | **Metronic-only UI, 11 layouts, one data-driven menu** | `MainMenu.cs` + `_MainMenu.cshtml` | recorded in project memory, not in an ADR |
B-12 | **Bilingual `*Ar`/`*En` columns rather than a translation table** | pervasive | affects every new entity |
B-13 | **Arabic-first culture with Latin digits forced** | `Program.cs FixNumbers` | subtle and easy to break |
B-14 | **Service methods return `(bool ok, string? error)` with Arabic strings** | pervasive | presentation in `BL/` |
B-15 | **`[DevOnly]` is the only barrier between `DevSeedController` and production** | attribute + `Program.cs` | a ~12.7k-line seeder with 176 writes |
B-16 | **Chat presence and typing are deliberately not persisted** | `ChatService`/`ChatHub`, stated in the Comm-Hub analysis | design intent, recorded only in a design doc |
B-17 | **`InventoryApproval` defers the document as `PayloadJson` and creates it on approval** | `InventoryApprovalService` | the single most consequential undocumented workflow decision (R6) |

## C. Decisions still owed — required before the next stages

| # | Open decision | Blocks | Owner input needed |
|---|---|---|---|
**C-1** | **Status normalisation policy.** Four shapes exist (`string`, `int` 0/1/2, `bool IsActive`, `Stage`). Does the Workflow Engine absorb all four, or are objects normalised first? | **Stage 3 (Workflow Engine)** | Architecture + product |
**C-2** | **Isolation mechanism.** Adopt EF global query filters, or keep manual predicates? | Stage 1, and every later stage | Architecture |
**C-3** | **Access-service contract.** One `CanAsync(BusinessContext, action)` for all four modules, or per-module contracts? | Stage 1 → Stage 6 (AI Context) | Architecture |
**C-4** | **Do Manufacturing / HR / Projects / Tasks get role tables**, or inherit a neighbour's? | Stage 1; several later stages | Product + architecture |
**C-5** | **Tenancy target.** Is `Tenant` above `Company` actually wanted, or is multi-company sufficient? | Stage 8; influences Stage 1 | Product |
**C-6** | **Deferred-command semantics.** Does the engine own versioned commands (absorbing silo 3), or does inventory approval stay separate forever? | Stage 3e | Architecture |
**C-7** | **`BusinessEvents` retention horizon** and archive destination | Stage 2 | Ops + product |
**C-8** | **Event coverage target.** Which write flows *must* emit events? Today 8 producers cover 4 objects; ~50 flows emit nothing | Stages 2, 5, 6 | Architecture |
**C-9** | **Are `Tags` in or out?** Present in Book 1 v1.1, absent from v2.0 | Stage 4 | Product |
**C-10** | **File storage strategy.** Keep local disk, or move to blob storage before `EntityFile`? | Stage 4 | Ops |
**C-11** | **API strategy.** Mobile-only, or a general versioned REST layer for all modules? | Stage 2+ | Product |
**C-12** | **Is `FiscalPeriod` intentionally global** across companies? | Stage 1 | Finance + architecture |

## D. Recommended new ADRs (in writing order)

| Priority | Proposed ADR | Captures |
|---|---|---|
1 | **ADR-018 — Company isolation mechanism** *(was proposed as 008)* | C-2 + B-8 + B-9. **The single highest-value decision still owed.** EF global query filters + removing `DefaultCompanyId = 1` |
2 | **ADR-019 — Sole-writer services** *(was proposed as 009)* | B-1 + B-2. Protects the integrity invariant from a future module. `JournalEntryService` and `StockService` still have no written protection |
3 | **ADR-010 — Permission resolution without the HTTP session** | C-3 + B-7. Prerequisite for Stages 3 and 6, and for per-object AI context |
4 | **ADR-011 — Status model policy** | C-1. Must precede the Workflow Engine |
5 | **ADR-014 — Deferred approval commands** | C-6 + B-17 (silo 3 executes a stored `PayloadJson` with no schema version) |
6 | ADR-015 — File storage and authorization | C-10 + R5 (uploads are readable by URL) |
7 | **ADR-020 — Notification delivery guarantees** *(was proposed as 016)* | B-4 + the email outbox. Batch A made email delivery reliable; the *guarantee* is still unwritten |
8 | ADR-017 — API versioning and envelope | C-11 |
9 | **ADR-021 — Business event retention and archival** | new: `BusinessEvents` grows without bound and nothing is deleted today |

~~ADR-012~~ and ~~ADR-013~~ are no longer owed — **issued** in Stage 0 Batch B (section A).

## Gaps
- ADRs exist for the Platform Kernel and its operations only — **0 of the other 13 modules**.
- 17 load-bearing decisions are implicit in code only. The two most dangerous remain **B-1/B-2, the sole-writer
  invariant**, which still has no written protection at all.
- **9 decisions are owed** (down from 12 — three were issued in Stage 0 Batch B) before the roadmap can proceed past
  Stage 1.

## Risks
Undocumented decisions get reversed by accident. Two concrete examples already visible: a design document
specified `CommMessage.EntityType/EntityId` and an SMTP retry dispatcher, **neither was built**, and reading the
document as architecture would produce wrong conclusions (19-I1, R21). The sole-writer invariant (B-1) has no
written protection at all.

## Dependencies
19 (gaps), 20 (risks), 21 (stage ordering — C-1..C-4 gate Stages 1 and 3).

## Recommendations
1. Write **ADR-018 (company isolation), ADR-010 (session-free permission resolution) and ADR-011 (status model)**
   before the Workflow Engine sprint — they are its actual prerequisites.
2. Promote B-1 and B-2 (sole-writer services) to **ADR-019** now; they are the invariants most likely to be broken by
   a well-meaning new module, and Stage 0 touched `JournalEntryService` without one existing.
3. Add a one-line "status: design, not implemented" banner to the design documents in `docs/` so they cannot be
   mistaken for as-built architecture.

---

## Stage 1 ADR additions (Batch B + Hotfix A.1)

| ADR | Title | Status | Stage |
|---|---|---|---|
| ADR-022 | BusinessContext resolution policy | Accepted | 1 / Batch A |
| ADR-023 | Controlled company-isolation bypass | Accepted | 1 / Batch B (B3) |
| ADR-024 | Pilot company isolation — read filters, write guard, raw-SQL boundary | Accepted | 1 / Batch B (B2/B4/B5) |
| **ADR-025** | **Accounting API security policy** | **Accepted** | **1 / Hotfix A.1** |

Corrections: [CORRECTION-004](CORRECTION-004-Permission-Basis-And-In-Body-Authorization.md) — the permission-coverage
basis, in both directions.

Design standards (documentation, no maturity credit):
[CrossBuy-Platform-UI-Standard.md](../design/CrossBuy-Platform-UI-Standard.md) ·
[Accounting-Visual-Identity-Reference.md](../design/Accounting-Visual-Identity-Reference.md) ·
[Platform-Screen-Checklist.md](../design/Platform-Screen-Checklist.md)

Planned platform capabilities added to [21-Modernization-Roadmap.md](21-Modernization-Roadmap.md), explicitly
**not implemented** and awarded no maturity points: **Stage 9** Architecture Validation Framework,
**Stage 10** Enterprise Financial Intelligence Platform.

---

## Stage 1 Batch C ADRs

| ADR | Title | Status |
|---|---|---|
| **ADR-026** | [One shared platform RBAC model](../platform/ADR-026-Shared-Platform-RBAC.md) | Accepted |
| **ADR-027** | [HR permission model](../platform/ADR-027-HR-Permission-Model.md) | Accepted |
| **ADR-028** | [Projects permission model](../platform/ADR-028-Projects-Permission-Model.md) | Accepted |
| **ADR-029** | [Tasks and Communication permission models](../platform/ADR-029-Tasks-Communication-Permission-Models.md) | Accepted |

Planned capabilities recorded in [21-Modernization-Roadmap.md](21-Modernization-Roadmap.md) Stage 11, awarded no
maturity points: Role Templates, Role Groups, Delegation Framework, field-level permissions, non-employee principals,
Tenant elevation, legacy role-table fold-in. POS remains a documented **permanent exception**.
