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
- **Every test must be RE-RUNNABLE — no drain, no collision.** A test that consumes stock or uses sequential numbers/fixed
  codes must survive repeated runs: **fill-to-floor** (`if (balance < floor) PostOpeningStock`), a **fixed `ZZ`/`MFGT`
  entity reused idempotently, a **unique-per-run** name (asserted by DELTA), or a **full rollback** (owner tx). A one-time
  `if (!AnyAsync) seed` drains; a fixed created-name asserted as "new" collides. We have found this THREE times (T6 batch
  name · hm1-b5b receipt numbers · HM-D12 manuf raws) — sweep for it, don't rediscover it a fourth. (Baseline note:
  `unbatched_inbound_tracked` = 19 after the HM-D12 MFGT stray-TrackExpiry repair, was 25.)
- **Every user-facing string via Resources** (or the file's own convention — e.g. `PricingService` errors are hardcoded English).
  - **Declared exception (HM-6):** `StockService` and `ItemService` carry **no localizer by design** — every message in
    them is a hardcoded literal (file convention). New messages there follow that convention. Injecting an `IStringLocalizer`
    into `StockService` (our stock writer) would itself be a **new constructor coupling that `writer_coupling` (HM-D53)
    flags** — so it is a deliberate exception, not an oversight. Controller-level and view strings still go via Resources.
  - **The literal in an unlocalized file is ENGLISH, not Arabic (i18n sweep, 2026-09-01).** This rule used to read
    "hardcoded Arabic", because Arabic was the default culture. It no longer is: `Program.cs` now defaults to `en`
    (see the *English is the presentation language* rule below), so a hardcoded Arabic message is a message an English
    operator cannot read, in the one place we deliberately have no localizer to save us. ~1,540 such literals were
    translated across the services, controllers, views and app JS. **Do not "restore" Arabic here** — the constraint
    the exception protects is *no localizer coupling in the writers*, not *the Arabic language*.
- **English is the presentation language; Arabic and French stay one click away.** `DefaultRequestCulture = en` and
  `Accept-Language` negotiation is removed, so only an explicit choice (`?culture=`, or the cookie the switcher writes)
  changes the language — a browser advertising `ar` no longer silently pulls the whole UI back to Arabic.
  - A **bilingual pair is never collapsed.** `T(ar,en)`, `tr(ar,en)`, `LabelAr/LabelEn`, `NameAr/NameEn`,
    `NotifyAsync(…, arTitle, enTitle, arBody, enBody)` all keep both halves. Translating the Arabic half of a pair is a
    regression, not a sweep — it silently deletes the Arabic UI.
  - **Read a bilingual column pair through `DisplayName.Or(preferred, fallback)`** (`BL/DisplayName.cs`), never a bare
    `isAr ? x.Name : x.NameEn`. The English column is nullable: with English as the default, an unfallbacked ternary
    renders a **blank** account/warehouse/unit rather than a name. 132 sites were repaired; keep new ones fallbacked.
    For an employee name use `EmployeeNames` — it adds the EF-translatable `Display()` that ORDER BY and search need.
  - **Every named master-data table carries a PAIR.** Ten tables had a single name column and now
    have an English twin (`deploy/sql/i18n_english_name_columns.sql`): ManufWorkCenters.NameEn,
    ManufPlans.NameEn, MaintenanceSchedules.TitleEn, PosTerminals.NameEn,
    BranchPaymentMethods.DisplayNameEn, Drivers.NameEn, CrmSlaPolicies.NameEn,
    ManufRoutingOps.OperationNameEn, TaskChecklistItems.TitleEn, Brands.TradeNameEn. A new named
    table follows the same shape: required Arabic column, nullable English twin, an optional input
    beside the Arabic one, and `DisplayName.Of` at every read.
  - **A snapshot column is NOT given a twin.** `PosOrderLines.ItemName`, `Payslips.EmployeeName`,
    `FinalSettlements.EmployeeName`, `PosOrderLineModifiers.Name` freeze what a document said when
    it was issued. They must not vary by UI language - same rule as `EmployeeNames.EnglishOf`.
  - **A code column holds a CODE, never prose.** `MaintenanceSchedules.Type` is
    `Preventive|Inspection|Calibration|Repair`; five seeded rows held Arabic prose there, so the
    value was outside its own domain and any future branch on it would have missed. Repaired in the
    same script. Check a code column's domain before translating what a screen shows.
  - **An assertion that matches on a message substring matches the ENGLISH text.** `DevSeedController`'s self-checks and
    `UatDatasetSeeder` grep service errors (`.Contains("منتهية")` → `.Contains("has expired")`); 21 were repointed. A new
    matcher pins the English wording, so changing a message means changing its matcher.
- **Capabilities via `IsCapabilityEnabledAsync`**; test on `ZZ-*` / demo-seed entities only.
- **Company guard** at every gateway; cross-company operations are refused.
- **Selective commits.** Commit only our files. Shared files (CrossDbContext, Program.cs, resx) get **our lines only**
  via git plumbing (`git show HEAD:… > base; insert our lines; hash-object -w; update-index --cacheinfo`).
- **Build freshness before acceptance.** Before running any acceptance, verify the build is **fresh and complete**, not a
  prior binary in `bin/Debug`. If a shared-tree break (e.g. parallel WIP) forces acceptance on an **older binary**, that
  MUST be declared in the report, with exactly what the old binary did not cover and how any later change was otherwise
  verified. (Origin: HM-D52 — parallel WIP broke the tree right after an acceptance run.)

## Platform engineering rules (Stage 1; each one cost a real defect to learn)

Detail lives in `docs/platform/ADR-023/024/025`, `docs/architecture/CORRECTION-004`, `docs/design/*`. Keep this list short.

- **Never capture a scoped service instance in an EF model expression.** EF caches the model once per context type, so
  a query filter closing over an injected object serves *every* request from the **first** request's value.
- **A global filter resolves its state through the executing `DbContext`** (`db.CompanyScope`) — that is what makes the
  value per-request. Also: EF does **not** short-circuit `x != null && x.Value`; it evaluates `.Value` while building
  parameters. Compare against a non-matching sentinel (`CompanyId ?? 0`) instead of a null check.
- **Every background worker binds an explicit company scope** (`WorkerScope.ForCompany`). An unbound worker reads
  nothing and passes by examining zero rows.
- **A hosted service is a SINGLETON — it may never inject a scoped service.** Take `IServiceScopeFactory` and create a
  scope per run. Batch C's own `PermissionScopeStartupValidator` broke this and stopped the app from starting.
- **A DI graph is not verified by unit tests that construct services by hand.** 112 green tests coexisted with an
  application that could not boot. Any new hosted service, or any service injecting `IEnumerable<IModuleAccessService>`,
  must be added to `Stage1DiWiringTests` — it builds the real graph with `ValidateOnBuild` + `ValidateScopes`.
- **A service registered as `IModuleAccessService` must not inject `IEnumerable<IModuleAccessService>`** — that is a
  circular dependency. Inject the concrete peer service instead (`ProjectsAccessService` → `AccountingAccessService`).
- **An unresolved company scope reads no company-scoped data and writes none.** Fail closed; never default to a company.
- **`IgnoreQueryFilters()` is forbidden** outside the bypass implementation — it removes isolation with no
  authorization and no audit line. Enforced by a test.
- **Cross-company access requires an explicit authorized, reasoned, scoped and audited bypass** (`ICompanyIsolationBypass`).
  Read-only bypass kinds may not write.
- **Public-company reads use the constrained public scope** (`BeginPublicCatalogRead`, pinned to configured
  `Store:StoreCompanyId`) — never the administrative bypass.
- **A custom build configuration must declare its compilation symbols.** `TestRun` was undeclared, so `DEBUG` was
  undefined and every acceptance compiled a different program than Debug.
- **Security and isolation metrics require independent reconciliation** before publication: reconcile source, scanner,
  grep and inventory, and never credit an attribute that checks no role (CORRECTION-004).
- **Authentication is not authorization, and a query filter is not an authorization control.** A request-supplied
  `companyId` is compatibility-only: validate it against the resolved `BusinessContext` and reject a mismatch — never
  coerce it. Hiding a UI control is not a control.
- **Accounting is the visual identity reference** for future platform and administrative tools: Metronic 8 `app-*`
  shell, the brand palette via Metronic tokens, RTL/LTR parity, `@Localizer` + ar/en/fr resx. Specialized operational
  screens (POS, KDS, manufacturing floor, mobile attendance, storefront) keep their domain UX.
  **NOTE:** the brand is ledger **green** `#13433a` + gold, not blue — `crossbuy-brand.css` overrides Metronic's blue
  on purpose. Open item: A6.2's "preserve the CrossBuy blue identity" contradicts the code and needs an owner decision.

## Standing decisions

- **We do NOT raise platform business events in the hyper track yet (decided at HM-5).** Reason: raising one binds
  us to their event queue + dispatcher + consumers, all uncommitted in git — their change or rollback would reach our
  path. Events get adopted only after their work stabilises in git. Our sale still emits `SalesInvoice.Created`
  because it goes through *their* `ReceivableService.CreateSalesInvoiceAsync` — that is their call inside their code,
  not ours.
- **Acceptance precondition (mandatory):** any DB we run acceptance against must have **slice-1
  (`platform_business_events.sql`) AND slice-2 (`platform_business_events_slice_002.sql`) applied FIRST** — else the
  sale/purchase path fails (their `RecordAsync` has no swallowing catch): slice-1 missing → SQL-208 (no `BusinessEvents`);
  slice-2 missing → SQL-207 `Invalid column name 'EntityId'` (they now write `Notifications.EntityType/EntityId`). The
  kernel advanced past slice-1 without notice — **slice-1 alone is no longer sufficient (HM-D57)**. Apply slice-2 with
  sqlcmd `-I` (QUOTED_IDENTIFIER ON) so its filtered index also builds. Verify `Notifications.EntityId` exists before acceptance.
  - **General rule (supersedes the slice-1-only rule): EVERY platform slice present in the working tree must be deployed
    to any DB we run acceptance against, and the list is re-reviewed BEFORE EACH PHASE — not pinned once.** The kernel keeps
    adding slices; a slice can be a new *column* on an existing table (SQL-207), not just a new table (SQL-208). The slices
    that gate OUR path (sale/purchase/reversal/stock) are the `platform_business_events*` family: **slice-1**
    (`platform_business_events.sql` → `BusinessEvents`/`BusinessEventDispatch`) and **slice-2**
    (`platform_business_events_slice_002.sql` → `Notifications.EntityType/EntityId`). Slices that do NOT gate our path (safe
    to lag): `comm_outbox_slice_003.sql` (Comm module, no GL/stock) and `platform_schema_history.sql` (a deploy-tracking
    table — ironically itself unapplied on CrossBuyDB2, so it cannot be relied on to tell us what is applied). Before each
    phase: `ls deploy/sql/platform_*` and confirm each `platform_business_events*` slice's tables+columns exist.
- **The kernel now hard-requires a signed-in BusinessContext on our purchase/sale/reversal path (HM-D58).** The parallel
  team removed the company-1 fallback; `RecordAsync` (their wiring inside our `CreatePurchaseInvoiceAsync`/sales/reversal)
  throws `BusinessContextUnresolvedException` when no signed-in employee/company resolves from the request. A production UI
  request always carries it; an **unauthenticated dev/acceptance endpoint that creates a document must seed the `"Employee"`
  session blob** (a real active company-1 employee, as `AccountController` login does) before the call — see `hm16-accept`.
  - **Permanent test rule:** every headless/curl-invoked dev endpoint that creates a document, posts a reversal, or syncs a
    paid order (i.e. reaches `RecordAsync` via a writer/`ReverseAsync`) MUST seed the `"Employee"` blob first. Today ~74 such
    calls exist across `DevSeedController` (25 sales-invoice, 16 reverse, 11 purchase-invoice, 11 POS-sync, plus returns/
    convert) and **only `hm16-accept` seeds the blob** — every other doc-creating dev endpoint throws
    `BusinessContextUnresolvedException` when hit without a signed-in session (they pass today only because they are driven
    from a signed-in browser). Any new acceptance endpoint follows the `hm16-accept` pattern.
  - **Dev-context seed filter (masks HM-D58 for TESTS only — NOT a production fix).** `DevSeedController.OnActionExecutionAsync`
    seeds the `"Employee"` session blob (a real active company-1 employee) when absent, so curl/automation hits of the ~38
    doc-creating dev endpoints resolve a BusinessContext instead of throwing. It is scoped to that one `[DevOnly]` controller
    — no middleware, no global filter. Production interactive paths are covered by the REAL signed-in session; **any NEW
    background path the parallel team adds would still throw `BusinessContextUnresolvedException` and no filter would save
    it — so monitoring background paths stays an OPEN item (HM-D58).** The filter is a test-harness plaster, not a cure.
  - **Single-shot curl acceptance is now BROKEN by parallel company-scope filters (HM-D59) — use a cookie jar.** The
    parallel team added `CompanyScopeMiddleware` + `CompanyQueryFilters` (a global `HasQueryFilter` on ~12 pilot entities).
    An unresolved scope returns ZERO rows, and the middleware resolves the scope BEFORE our action-filter dev-context seed —
    so a fresh single-shot curl reads nothing and 500s (hm16-accept, green earlier, now 500s single-shot too). **Run every
    curl acceptance with a persisted session: `curl -c cj culture-check` to prime (seeds the blob + Set-Cookie), then
    `curl -b cj <accept-endpoint>`** so the middleware resolves company 1 on the second call (mirrors a browser). No
    parallel-file touch. Recorded HM-D59.
  - **Verdict (HM-D58, read-only sweep): NO production background/hosted/consumer/hub path reaches `RecordAsync`.** No
    `HostedService`/`BackgroundService`/dispatch-worker/consumer/hub injects or calls a document writer, `ReverseAsync`, or
    the accessor; `NotificationService.NotifyAsync` never touches the accessor; `Publish`/`ForWorker`/`ForSystem` set the EF
    company-scope, NOT the accessor cache, so they would not rescue `RecordAsync` even if a background path called it. The
    two reversal producers the risk pointed at — `FxRevaluationService`, `ClosingService.ReopenYearAsync` — are invoked ONLY
    by controllers (`CurrencyController.PostRevaluation`, `AccountingController.ReopenYear`), i.e. inside a user request with
    an HTTP context. So the coupling is real but has **no silent production surface today**; it bites only headless tests.
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
