# CrossBuy — Project Rules (hypermarket track + shared platform)

This file is loaded every session. It records **our permanent rules** and the **shared-platform rules imposed by a
parallel team's uncommitted work**, so no phase re-discovers them. Snapshot of the parallel work:
`deploy/PARALLEL-WORK-SNAPSHOT.md`. Running deviations/decisions: `deploy/AUDIT-DEVIATIONS.md`.

## Our permanent rules (hypermarket track)

- **Two writers only.** `JournalEntryService` writes the GL; `StockService` writes stock. No other code posts a
  journal entry or a stock movement. No new "sale service."
- **Reads are DB truth.** Read fresh / `AsNoTracking`, never assert from a tracked entity; a locked read is a fact.
- **Prove from a new context.** Acceptance re-reads from the DB, not from the entity that wrote it.
- **Reverse, never delete.** Corrections are reversing entries (`ReverseAsync`), never row deletion — history stays whole.
- **Idempotent SQL in `deploy/sql`, NOT EF migrations.** The `Migrations/` folder is a dead snapshot.
- **Currency-aware rounding, explicit `AwayFromZero`** via `ICurrencyRounding`. Never bake a 2-decimal assumption.
  A **retail** price step (nearest 5/10 fils) is a *separate* commercial rounding — never routed through `ICurrencyRounding`.
- **No test leaves an `inv-test-integrity` deviation.** `failedCount` stays 0; new classifications are *counted*, not failing.
- **Every user-facing string via Resources** (or the file's own convention — e.g. `PricingService` errors are hardcoded Arabic).
- **Capabilities via `IsCapabilityEnabledAsync`**; test on `ZZ-*` / demo-seed entities only.
- **Company guard** at every gateway; cross-company operations are refused.
- **Selective commits.** Commit only our files. Shared files (CrossDbContext, Program.cs, resx) get **our lines only**
  via git plumbing (`git show HEAD:… > base; insert our lines; hash-object -w; update-index --cacheinfo`).
- **Build freshness before acceptance.** Before running any acceptance, verify the build is **fresh and complete**, not a
  prior binary in `bin/Debug`. If a shared-tree break (e.g. parallel WIP) forces acceptance on an **older binary**, that
  MUST be declared in the report, with exactly what the old binary did not cover and how any later change was otherwise
  verified. (Origin: HM-D52 — parallel WIP broke the tree right after an acceptance run.)

## Standing decisions

- **We do NOT raise platform business events in the hyper track yet (decided at HM-5).** Reason: raising one binds
  us to their event queue + dispatcher + consumers, all uncommitted in git — their change or rollback would reach our
  path. Events get adopted only after their work stabilises in git. Our sale still emits `SalesInvoice.Created`
  because it goes through *their* `ReceivableService.CreateSalesInvoiceAsync` — that is their call inside their code,
  not ours.
- **Acceptance precondition (mandatory):** any DB we run acceptance against must have **slice-1
  (`platform_business_events.sql`) applied FIRST** — else the sale path fails with SQL-208 (their `RecordAsync` has no
  swallowing catch). Verify the `BusinessEvents` table exists before seeding/acceptance.
- **Reversal now depends on the event platform (HM-D53).** The **only accounting-correction primitive**,
  `JournalEntryService.ReverseAsync`, calls `RecordAsync(JournalEntry.Reversed)` in-transaction before commit with no
  swallow. So **every correction path** — edit sales/purchase invoice, edit returns, **cancel a paid POS order (hyper
  void)**, reopen fiscal year, FX-revaluation reverse, manual reversal — fails completely if `BusinessEvents` is
  missing/schema-changed. Any environment we run acceptance or a deploy against needs **slice-1 applied before any
  reversal**. (Normal posting — `CreateAndPost`/`Post` — is NOT coupled; only reversal is.)
- **`JournalEntryService` and `StockService` are architectural invariants, not ordinary files.** They are our two
  writers. A change to either (ours or the parallel team's) must be pre-coordinated with the owner — the parallel team
  already kernel-wired `ReverseAsync` without coordination (HM-D53).

## Shared Platform Rules (from parallel work — Platform Kernel)

Authoritative source: `docs/platform/ADR-001…007`, `PKS-001`, `Slice-002`; code in `CrossBuy/BL/Platform/`.
**MANDATORY** = enforced in their code / a hard contract; **opt-in** = encouraged, gap-tracked, not enforced.

### Business events (if/when we raise one)
- **Raising an event is OPT-IN per entity — NOT required for a new document to function.** A new document (Quotation,
  a settlement doc) appears in the timeline/notifications only after: (1) its code is added to `EntityRegistry`,
  (2) `SupportsTimeline`/consumer wiring is on, (3) a producer calls `RecordAsync` at a real transition. Omission is
  tracked as a High-severity *gap*, not a rule breach. *(ADR-002 L50-53; ADR-003 L63-65; 09-Platform-Kernel Risks L285.)*
  The registry already carries `Quotation` and `JournalEntry` (their slice-3) — they are actively onboarding entities.
- **MANDATORY once you do raise one:**
  - Call `IBusinessEventService.RecordAsync` **inside the caller's ambient `ScopedTx`, immediately before `CommitAsync`,
    with no swallowing try/catch.** No ambient transaction → it **throws**. *(ADR-001 L34-46; enforced `BusinessEventService.cs:85-88`.)*
  - `RecordAsync` does **no side effects** (no notify/AI/search) — safe inside a financial tx; fan-out is via the outbox. *(ADR-001 L45.)*
  - `BusinessEventRecord` required fields: `EntityCode` (frozen registry code), `EntityId`, `EventType`. Choose a
    `Visibility` (default `Internal`); set a `DedupKey` for idempotency. *(BusinessEventContracts.cs:70-104.)*
  - Event name = **`<EntityCode>.<Action>`**, Action PascalCase, ≤80 chars, validated against the registry; the action
    must be a real transition. *(PKS-001 §4.3; BusinessEventTypes.cs.)*
- **Dispatch model (if we ever add a consumer):** one dispatch row per (event, consumer); claim by `Status` only; a
  dispatcher must **never** use `EventId > lastSeen`, `MAX(EventId)`, or any cursor (IDs are assigned at INSERT,
  visible at COMMIT — a cursor skips a late-committing lower ID). `CompletedAt` is reporting-only. SQL claim is
  `UPDATE TOP(n) … OUTPUT … WITH (ROWLOCK, READPAST, UPDLOCK)`. *(ADR-003; ADR-007; PKS-001 §6.)*
- Kernel writes **only** `BusinessEvents`/`BusinessEventDispatch`/`Notifications`; it reads financial tables
  `AsNoTracking` and writes **none**. It does **not** violate our two-writers rule.

### Notifications
- For an **onboarded business fact**, notify via **event → `BusinessEventNotificationMapper` (pure) →
  `NotificationProjectionConsumer` → `NotificationService.NotifyAsync`** (post-commit, in the dispatch worker). The
  replaced inline `NotifyRoleAsync` was **deleted** to avoid double-notifying. *(ADR-006 §1-2.)*
- **Direct `NotifyAsync`/`NotifyRoleAsync` is STILL allowed** where no business fact/event exists (data-quality
  warnings, rejected-operation blocks) or the entity is **not onboarded**. So we are not forbidden from notifying
  directly in the hyper track today — but if we ever onboard a hyper entity to events, its notifications must move to
  the projection. *(ADR-006 retained-producers table L56-60.)*
- `NotifyAsync`/`INotificationService` gained two **optional trailing params** `entityType`,`entityId` (additive; existing
  calls unchanged). Its `dedupKey` is an *unread-noise* guard, not true idempotency.

### DI / hosted services / schema (matches our conventions)
- Services `AddScoped`; the outbox dispatcher is the one `AddHostedService<BusinessEventDispatchWorker>` (a
  `BackgroundService` that takes `IServiceScopeFactory` and creates a **scope per batch** — no request-scoped leak).
  *(Program.cs Platform block L179-212.)*
- Kernel schema ships as **additive idempotent `deploy/sql/*.sql`, applied BEFORE the code** ("SQL before code"),
  **no EF migrations, no backfill**. Same discipline as ours. *(PKS-001 §1; Slice-002 §5-6.)*

### Feature-module conventions (Comm/Calendar/Library/Announcements/DocComments/FileManager)
- Interface + impl + DTOs **co-located in one file** per service; **one controller per module** (`[SessionValidation]`, thin).
  **Plural** DbSets and table names. Every table carries `CompanyID` (tenant filter everywhere) + `CreatedBy/At`,
  `UpdatedBy/At`, and **soft-delete `DeletedAt`** (they soft-delete, we reverse — different domains, no conflict).
- These modules **write only their own tables** — no GL/stock, and they **do not raise business events** (they are
  event *targets* via the registry, e.g. DocComments, not producers).
- **Their string convention is looser than ours:** only `CalendarController` uses `IStringLocalizer`; others use
  hardcoded bilingual literals, machine error codes (`"title_required"`), and `NameEn` twin columns + a culture check.
  This is theirs; **we keep every user-facing hyper string in Resources.**
- **Tests** (`CrossBuy.Tests`, xUnit) cover the kernel only, on in-memory SQLite (real transactions); SQL-Server
  concurrency tests are gated by `CROSSBUY_TEST_SQL` and **refuse a real CrossBuy DB name** — a discipline worth mirroring.

## Known Conflicts (from parallel work — do NOT "fix", just respect / raise with them)

1. **Our base can shift invisibly** — all of their work is uncommitted. `ReceivableService`/`PayableService`/`ManufService`
   (document writers we build on) now carry kernel wiring that isn't in any commit. **Action for the owner (not us):**
   ask them to commit, so our selective commits diff against a stable base. *(HM-D44.)*
2. **Kernel is a hard runtime dependency of the sale/purchase/manuf path.** `RecordAsync` (no swallowing catch) means a
   missing `BusinessEvents` table fails a real sale with SQL-208. Slice-1 (`platform_business_events.sql`) is **applied**;
   keep it applied on any DB we run acceptance against. *(HM-D44/D45.)*
2b. **Our shared layouts now embed their partials** (`_AnnouncementsBanner`, `_CalendarReminders`, Comm button in
   `_Layout{Inventory,Accounting,Backend,Manufacturing}`). Our screens render only if those untracked partials exist.
3. **A second notification path bypasses the outbox** (observed: event #1 fanned out one dispatch row yet a
   `sales_invoice` notification exists). Their internals; recorded, not traced. Risk: duplicate/divergent notifications. *(HM-D46.)*
4. **resx reordering collides with our plumbing.** They reordered `SharedResources.*` and added a tracked
   `SharedResources.en.resx`. Our plumbing (HEAD base + our keys) already ignores their reordering — keep using it; never
   whole-file `git add` a resx.
- **No genuine rule conflict found:** they do **not** write financial tables, do **not** touch our two writers, use
  idempotent SQL not migrations, and keep `NotifyAsync` additive. Their event-transaction rule (in-tx, before commit,
  no swallow) is the *same* all-or-nothing discipline we use.
</content>
