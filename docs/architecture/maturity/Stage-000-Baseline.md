# Stage 000 — Platform Maturity Baseline (as-built)

**Measured:** 2026-08-03, retrospectively, against the repository state **before Stage 0 (Slice-003) Batch A began**.
**Model:** [Platform-Maturity-Model.md](../Platform-Maturity-Model.md) — weights fixed, 10-point steps (R9).
**Evidence basis:** corrected. Incorporates [CORRECTION-001](../CORRECTION-001-Manufacturing-Permission-Finding.md)
and [CORRECTION-002](../CORRECTION-002-Evidence-Scanner-Defects.md).

> **Immutable (R8).** This record is not edited after publication. A later correction is a new record.

---

## Score

| # | Dimension | Weight | Level | % | Score |
|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 — Established | 60 | **12.00** |
2 | Security and Company Isolation | 15 | 2 — Partial | 40 | **6.00** |
3 | Platform Kernel and Audit | 10 | 3 — Established | 60 | **6.00** |
4 | Business Object Coverage | 10 | 1 — Present | 20 | **2.00** |
5 | Task and Work Management | 10 | 3 — Established | 60 | **6.00** |
6 | Workflow and Approvals | 10 | 2 — Partial | 40 | **4.00** |
7 | Communication and Collaboration | 10 | 2 — Partial | 40 | **4.00** |
8 | Search, AI and Intelligence | 7 | 1 — Present | 20 | **1.40** |
9 | Testing and Reliability | 5 | 2 — Partial | 40 | **2.00** |
10 | Deployment and Operations | 3 | 1 — Present | 20 | **0.60** |
| | **Total** | **100** | | | **44.00** |

---

## Evidence per dimension

### 1 · ERP Business Coverage — 60 (level 3)

**For:** the main ERP surfaces exist and post correctly through the two sanctioned writers.
`JournalEntryService` is the only GL writer, `StockService` the only stock writer — verified in
[10-Controller-Service-Transaction-Map.md](../10-Controller-Service-Transaction-Map.md). Real, working modules:
Accounting (AR/AP/GL, chart of accounts, cost centres, fixed assets + depreciation runs, multi-currency with
revaluation, fiscal periods, statements, aging, cash flow), Inventory (warehouses, bins, transfers, counts,
weighted-average costing, reconcile log), Manufacturing (BOM, work orders, WIP, routing, work centres, variance to
520109), POS/Restaurant (orders, KDS, delivery, shifts, tips, voids, offline sync), HR (employees, leave, attendance,
appraisals, training, recruitment, final settlement), CRM (leads, opportunities, configurable pipelines, campaigns,
activities, custom fields), Projects & Contracting (contracts, progress billing, retention, subcontract, variation
orders, equipment depreciation), Retail pricing (price lists, cost-plus, margin, promotions, discount approval),
Tasks (TM1–TM9).

**Against level 4:** multi-tenancy is **nominal**. `DefaultCompanyId = 1` hard-coded in 8 controllers and
`CompanyId = 1` in 4 access services ([19-Current-Gaps.md](../19-Current-Gaps.md) B2, Critical). `FiscalPeriod` has
no `CompanyID` column at all ([06-Business-Object-Catalog.md](../06-Business-Object-Catalog.md)). No consolidated
multi-company reporting. No budgeting, no procurement-to-pay approval chain.

### 2 · Security and Company Isolation — 40 (level 2)

**Exactly level 2:** a permission mechanism exists and covers a **minority** of mutating actions; tenancy is manual.

- **75 of 381** mutating actions (POST/PUT/DELETE/PATCH) carry an action-level module permission — **20%**.
  **306 do not.** `evidence/Permission-Coverage.csv`.
- **No EF global query filters** for `CompanyID` or `DeletedAt`; isolation is hand-written in ~1000 queries
  (B1, Critical).
- **Four access services, three action vocabularies, two employee-resolution styles** (B3, High) — no generic
  authorization is possible.
- `AccountingAccessService` returns `true` for every action when its role table is empty company-wide
  ("open when unconfigured").
- **Uploaded files are served as unguarded static URLs** — `wwwroot/uploads/library/<company>/<guid>.ext`, retrieval
  bypasses every controller (A4, High: HR documents readable by URL).
- Duplicated session gate with divergent bypass lists (A9).
- **For:** 323 of 381 mutating actions carry anti-forgery (85%); the four permission attributes are real
  `IAsyncActionFilter`s; the kernel's own reads are company- and visibility-filtered.

### 3 · Platform Kernel and Audit — 60 (level 3)

**For:** slice 1 + slice 2 merged. Transactional outbox (`BusinessEvents` written inside the caller's `ScopedTx`,
before commit, no swallowing catch — ADR-001); **per-consumer** dispatch state with atomic claiming, bounded retry
and stale reclaim (ADR-003/007); four visibility tiers with an own-actor exception (ADR-004); legacy timeline
reconstruction at read time with no backfill (ADR-005); **two** consumers — timeline projection and notification
projection (ADR-006). 91 tests.

**Against level 4:** **no operator observability of the queue and no guarded retry path** — a failed consumer could
only be found and re-queued with hand-written SQL against production. `JournalEntryService.ReverseAsync`, the only
accounting-correction primitive, emitted **no** event, so the single most important correction in the system left no
durable fact. No deployment-state tracking.

### 4 · Business Object Coverage — 20 (level 1)

- **8 of 58** catalogued business objects registered in `IEntityRegistry` — **14%**.
- `_DocEventTimeline` wired to **4** objects (SalesInvoice, PurchaseInvoice, Customer, ManufWorkOrder) — **7%**.
- `_DocTimeline` (comments) wired to **3 screens**, and `DocComment.EntityType` accepts **free text**.
- **Object-scoped files: 0.** No `(EntityType, EntityId)` file table exists.
- **Followers: 0. Relations: 0.** No such tables exist.

Per R2, the existence of the shared partials is not coverage. `evidence/Business-Object-Capabilities.csv` (58 rows),
[07-Business-Object-Capability-Matrix.md](../07-Business-Object-Capability-Matrix.md).

### 5 · Task and Work Management — 60 (level 3)

**For:** the Tasks module is substantial and real — tasks with assignment and due dates (TM1), document links (TM2),
timesheets (TM3), labour cost (TM4), billing roll-up (TM5), auto-generation rules (TM7), scheduled tasks and match
suggestions (TM9/TM9b), progress tracking. Two background workers drive it.

**Against level 4:** no unified work inbox across modules — `/Approvals` unions three approval silos and does not
include tasks. Tasks are **not** registry citizens: no timeline, no comments, no entity-addressed notifications.

### 6 · Workflow and Approvals — 40 (level 2)

**Exactly level 2:** several independent mechanisms and a read-only unified inbox.
[14-Approval-Workflow-Landscape.md](../14-Approval-Workflow-Landscape.md) documents **four silos**:

| Silo | State | Shape |
|---|---|---|
Leave | works | `LeaveRequest.Status` int + `LeaveApprovalStep` |
Employee request | works | `EmployeeRequest.Status` int + `EmployeeRequestStep` — **identical shape**, separate code |
Inventory | works | `InventoryApproval` with `Status` str + `Amount` + `PayloadJson` + `DocType` |
Discount | **no table, no record, no approver** | `PricingService.EvaluateLineDiscountsAsync(..., canApprove)` returns `(block, warn)` synchronously |

`ApprovalsController` is a **read-only** union of silos 1–3. There is no engine, no delegation, no escalation, and
the discount silo leaves no audit trail of who approved what.

### 7 · Communication and Collaboration — 40 (level 2)

**For:** notifications (real-time via `NotificationsHub`, company-scoped groups, mute preferences), internal chat
(1:1 + group, reactions, read receipts, typing, soft delete), SMTP email outbox with a Metronic inbox,
announcements, document comments, event timeline, calendar, and a SharePoint-style file library.

**Against level 3 ("entity-addressable"):** most of it is **standalone** —
[12-Communication-Collaboration-Map.md](../12-Communication-Collaboration-Map.md) verdict table: chat, email,
announcements, calendar and the file library have **no entity linkage at all**. `CommMessage` has no
`EntityType`/`EntityId` despite the design specifying them. Comments are entity-linked but with free-text types and
wired to 3 screens. **Failed emails are never retried and are invisible** (A3) — no reliability at all on the one
channel that leaves the building.

### 8 · Search, AI and Intelligence — 20 (level 1)

- `IEntityRegistry.SearchAsync` resolves **9 codes** and is **not exposed in any global search UI**; every screen
  has its own local search.
- AI is `AiInsightsService` with **3 fixed company-level aggregate endpoints** (journal anomalies, cash flow,
  inventory). **0 object-scoped AI context** — [07](../07-Business-Object-Capability-Matrix.md) line 71.
- Per-object AI context is blocked on B3 (one authorization contract) — C8, Medium.
- No AI client timeout: a hung Python service holds request threads ~100 s (F5).

### 9 · Testing and Reliability — 40 (level 2)

**Exactly level 2:** the platform layer is tested, **including real-database concurrency** — `CrossBuy.Tests`, 91
tests, xUnit, SQLite in-memory with real transactions plus opt-in SQL Server integration tests gated by
`CROSSBUY_TEST_SQL` (which **refuses** a connection string naming a real CrossBuy database).

**Against level 3:** **zero tests** for the financial writers' business logic or for any business module —
Accounting, Inventory, POS, HR, CRM, Projects. No CI runs anything.

### 10 · Deployment and Operations — 20 (level 1)

- **Two `deploy/sql` folders**, 105+ scripts, **no manifest**, **no applied-scripts table** (B5, High).
  *"Is this database current?" is unanswerable.*
- **4 script names exist in both trees with different content** — "apply `pos_setup.sql`" is ambiguous.
- Ordering is tribal knowledge. No runbook, no smoke-test procedure, no rollback statement.
- **Six background workers with no single-process control**, four of which duplicate their output if two processes
  run them.
- Kernel SQL merged but **not deployed** while its code is live (A2, Critical) — and `RecordAsync` has no swallowing
  catch, so a missing `BusinessEvents` table fails a real sale with SQL 208.

---

## Notes on the baseline itself

**Why the baseline is measured retrospectively.** The model was defined in Batch B. Measuring the pre-Batch-A state
now is the only way to have a defensible starting point, and it is honest as long as the same rubric and the same
corrected evidence are used throughout — which they are.

**Why the corrected evidence is used (R7).** This baseline scores Security against **306** unguarded mutating
actions, not the 267 originally published. CORRECTION-002 established 306 as the true pre-existing figure; using the
old number would have flattered the baseline and then shown a fake improvement when the correction landed. The
system was never at 267.

**What CORRECTION-001 changed here.** The withdrawn manufacturing finding was published as the system's single
highest-severity security risk. Withdrawing it does **not** raise this baseline: the manufacturing writes were always
guarded, so the baseline was always what it is. What changed is that the *reason* Security scores 40 is the 306
unguarded actions elsewhere and the absent tenancy enforcement — not a false claim about work orders.