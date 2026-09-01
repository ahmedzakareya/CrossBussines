# CrossBusiness Platform — Roadmap v2 — 03 Module Assessments

**27 modules reassessed from the current source tree, not from prior reports.**

Where a prior report and the tree disagreed, the tree won and the disagreement is recorded.

---

## 1 · Core Security

**Purpose.** Decide who may do what, per company, and prove it.

**Real capabilities.** Roslyn analyzer enforcing authorization classification at build time (CBA001–CBA006, 93/93 tests). Bootstrap access policy store with 6 CHECK constraints, a filtered unique active index and drift detection that throws. Action-aware bootstrap decisions at three converted sites. Platform grant writer with a privilege ceiling. Company isolation bypass — authorized, reasoned, scoped, audited.

**Screens.** None. The Security Console does not exist.

**Services/APIs.** `AccountingAccessService`, `InventoryAccessService`, `CrmAccessService`, `PosAccessService`, `ProjectsAccessService`, `TasksAccessService`, `HrAccessService`, `CommunicationAccessService`; `PlatformGrantsApiController` (3 mutating endpoints, in-body authorized).

**Entities.** `BootstrapAccessPolicy`, `PlatformRoleAssignment`.

**Permissions/isolation.** This *is* the isolation layer. `IgnoreQueryFilters()` forbidden outside the controlled bypass, enforced by test.

**Tests.** 32/32 focused B6 + 3/3 diagnosis + 93/93 analyzer + Batch A/B suites.

**Operational maturity.** High for the converted paths; **zero operator tooling** — every policy and grant is a database row today.

**Missing.** Security Console; bootstrap policy writer API; legacy role migration.

**Security gaps.** **143 unprotected mutating actions.** Two CRM sites still bootstrap-open.

**Debt.** Bootstrap policy SQL slice authored but applied nowhere.

**Decisions.** D-01, D-02, D-03, D-04, D-23.

**Next increment.** R3 — decide D-03/D-04, convert CRM, build the console, start the debt burn-down.

---

## 2 · Platform Framework

**Purpose.** The kernel every module depends on: context, events, entities, timeline, tenancy.

**Real capabilities.** BusinessContext resolution (fail-closed, no company-1 fallback). Transactional business events with a per-consumer dispatch model that never uses a cursor. Entity registry. Notification projection. Timeline projection plus legacy adapters. Company query filters on pilot entities. **Business Event Monitor — the one delivered platform screen.**

**Screens.** Business Event Monitor (3 views), Platform Timeline, Announcements/Brand (3).

**Workers.** `BusinessEventDispatchWorker`, `RuntimeStartupLogger`, `PermissionScopeStartupValidator`.

**Operational maturity.** Good, with one structural hazard: `RecordAsync` has **no swallowing catch**, so a missing `BusinessEvents` table fails a real sale, and `ReverseAsync` — the only correction primitive — depends on it. Every correction path in the product inherits that coupling.

**Missing.** Authoritative schema history (`platform_schema_history.sql` is itself unapplied — RSK-02).

**Next increment.** R1 — make schema history real and consolidate the SQL trees.

---

## 3 · Accounting

**Purpose.** The ledger, and the two-writer discipline around it.

**Real capabilities.** GL with `JournalEntryService` as the single writer; reversal-only corrections; receivables and payables; fixed assets; fiscal periods and closing; multi-currency with `ICurrencyRounding` and FX revaluation; financial statements.

**Screens.** 66 Accounting views + 4 Currency views — the largest and most mature screen set, and the declared visual identity reference.

**Tests.** Stage 1 + hyper suites.

**Missing.** Approval routing; consolidated multi-company statements; tax depth.

**Security gaps.** Carries part of the 143 debt.

**Decisions.** D-01 (read breadth).

**Next increment.** R10, with financial statements as the likely first reporting dataset (R4).

---

## 4 · Inventory and WMS

**Purpose.** Stock truth.

**Real capabilities.** `StockService` as the single stock writer; warehouses, bins and sections; keeper scoping now **exactly enforced** after B6 site 3; counts, batches, expiry; pricing with margin, cost-plus, discount approval and promotions.

**Screens.** 78 Inventory views — the largest set in the product.

**Operational maturity.** High transactionally, low on warehouse *operations*: no wave picking, no putaway strategy, no cycle-count planning.

**Security gaps.** `Inventory.read` exposes stock costs unfiltered (D-02). `Inventory.purchase` is one action gating a PO draft, a goods receipt (creates stock) and a PO-to-invoice conversion (accounting effect) — a real vocabulary defect, deliberately kept closed whole (D-23).

**Next increment.** R9.

---

## 5 · Purchasing

**Purpose.** Procure, receive, match, pay.

**Real capabilities.** `ProcurementService` (PO, goods receipt, PO-to-invoice), `ThreeWayMatchService`, GRNI handling.

**Screens.** Inside the Inventory workspace — no dedicated purchasing workspace.

**Missing.** Supplier scorecards; RFQ; contract purchasing; commitments (also a construction requirement).

**Next increment.** R10, with the D-23 permission split.

---

## 6 · CRM

**Purpose.** Customers, leads, pipeline.

**Real capabilities.** Accounts/contacts/leads/opportunities; automation rules with a registered reminder hosted service; per-company custom fields; bilingual fields.

**Screens.** 32 Crm views.

**Security gaps.** **The most significant open exposure in the product.** `CrmAccessService.cs:59` returns `true` for *every* CRM action on a company with no configured roles; `:98` returns `null`, meaning unrestricted owner visibility. Both intentionally unchanged pending D-03 and D-04.

**Next increment.** R3 (conversion) then R6 (workspace).

---

## 7 · POS

**Purpose.** Front-of-house selling across retail, restaurant and hypermarket.

**Real capabilities.** Order capture, payment, shift close, void, tips; KDS with stations and send-to-kitchen; delivery (slices C1–C3), reservations, table merges; sync log and conflict handling; the verified hypermarket lane with loyalty earn, pay idempotency and receipt recovery.

**Screens.** 16 Pos + 5 PosApp + 9 Hyper = 30 — second only to Inventory.

**Operational maturity.** The most operationally complete module set, and the one with the most SQL slices (~25).

**Missing.** Offline selling (deliberately out of scope); unified terminal management.

**Next increment.** R14.

---

## 8 · Manufacturing

**Purpose.** Convert raw stock into finished goods with correct cost.

**Real capabilities.** Work orders, raw consumption, output posting through `StockService`; variance schema.

**Screens.** Inside Inventory.

**Missing.** Routing, capacity, scheduling, WIP valuation depth.

**Next increment.** R9.

---

## 9 · Projects

**Purpose.** Project delivery and cost.

**Real capabilities.** Project master, members, dimensions, cost centres; progress capture and progress billing; material issue; labour and timesheets; budgets; equipment depreciation.

**Screens.** 16 Project views.

**Security.** `ProjectsAccessService` enforces that being a projects administrator is **not** a right over the ledger — `Projects.billing` delegates to `Accounting.post`, and that transitive closure now holds on unconfigured companies too.

**Next increment.** R7 — Projects is the base Construction builds on.

---

## 10 · Construction and Contracting

**Purpose.** What Projects is not: BOQ, subcontracts, certificates, variations, claims.

**Real capabilities.** Stable BOQ identity (CR-01), subcontract certification cap (CR-02), immutable commercial revisions with line snapshots (CR-03) — all three mutation-proven closed, 38/38.

**Screens.** **None.** C1 delivered services and DDL only.

**Database.** `construction_c1_commercial_foundation.sql` (8 tables) + measurement script — **authored, applied nowhere**, while the C1 services *are* registered in Program.cs. That gap is RSK-11.

**Missing.** Everything from C2 on: WBS, cost codes, budgets, commitments, site operations, DSR, RFI, document control, claims, delays, cash flow, UI.

**Decisions.** D-19, D-20, D-21, D-24, D-25, D-26.

**Next increment.** R7 — but the DDL rollout and measurement must come first.

---

## 11 · HR

**Purpose.** People, time, pay.

**Real capabilities.** Employee master, job titles, administrative structure; attendance; leave workflow, accrual, dashboard; payroll cost and final settlement; appraisals; recruitment; training.

**Screens.** 31 Admin + 12 People = 43.

**Operational maturity.** **Broad but shallow** — seven capability areas, thin implementation in each, and a meaningful share of the 143 debt.

**Security.** `Hr.payroll-manage` and `Hr.confidential-view` are Never-bootstrap-open.

**Next increment.** R8.

---

## 12 · Task Management

**Purpose.** Work assignment and tracking across modules.

**Prior reports called this "planned". It is not.** Tasks has `TasksController`, 5 views, `TaskService`, `TasksAccessService`, seven SQL slices (TM-1…TM-9) covering progress, links, timesheets, labour, billing, automation and scheduled matching, and **two registered hosted services** (`TaskGeneratorHostedService`, `TaskScheduleMatchHostedService`).

**Real capabilities.** Tasks with categories, progress, entity links, timesheets, labour cost, billing linkage, rule-driven generation, scheduled matching.

**Screens.** 5 views — a list and a detail. Not a work surface.

**Gaps.** Not registered as a Communication entity surface, so tasks have no threads. No integrated My Work. **And no tab owns it** (D-32) despite two live background workers.

**Next increment.** R5.

---

## 13 · Calendar

**Purpose.** Time-based scheduling across modules.

**Also not "planned".** `CalendarService`, `CalendarController`, `calendar.sql` and one view exist. It is the one feature module that uses `IStringLocalizer`.

**Gaps.** One view is not a calendar workspace. No cross-module event sourcing. **Unowned** (D-33).

**Next increment.** R5, shipped with Tasks as one workspace.

---

## 14 · Support and Customer Service

**Purpose.** Handle customer issues.

**Real capabilities.** Ticket capture with categories and bilingual description fields; 5 Service views.

**Gaps.** **No dedicated access service** — the only significant module without one. No SLA, no escalation, no customer visibility. Not registered in Communication, so tickets have no threads.

**Decisions.** D-13 (external principal), D-14 (customer-visible comments).

**Next increment.** R6.

---

## 15 · Reporting

**Purpose.** Turn business data into documents.

**Real capabilities.** Dataset registry, data source, shaper, parameter engine; report catalog, templates, favourites, shares, tags, run history, archive; HTML, CSV and Excel exporters; authorization service with mapped permissions. 221/221 tests, 12 tables, from-empty and second-apply idempotency proved.

**Screens.** **None.**

**The core problem.** A complete platform with **zero production datasets and zero users**. A foundation with no consumer drifts from real data shapes and cannot be validated. PDF renderer exists in code but no runtime; delivery has no transport; the scheduler has no hosted worker.

**Decisions.** D-09, D-10, D-11, D-35, D-16, D-17.

**Next increment.** R4 — bind one production dataset before adding any feature.

---

## 16 · Communication and Collaboration

**Purpose.** Threads, comments, mentions and notifications on any entity.

**Real capabilities.** Thread and comment services; mention resolution; notification channels, templates and preferences; timeline aggregation; audit writer; `CommEntityRef`/`ICommEntitySurface` as the universal surface; permission boundary mechanically proved. 274/274 tests, 14 tables.

**Screens.** **None.** The 3 `Views/Comm` files belong to the **legacy `BL/Comm` module** — a different thing that shares a prefix, and which *does* have a registered `CommMessageDispatcherHostedService`. Conflating the two would be the easiest false claim in this whole assessment.

**Activation.** `CommunicationPlatformRegistration` exists but **is not called in Program.cs**. No hosted worker for the platform. Slice authored, not applied.

**Gaps.** Task and Support unregistered; DocComments migration not executed, so two comment models coexist.

**Decisions.** D-05, D-06, D-07, D-12, D-13, D-14, D-15.

**Next increment.** R2.

---

## 17 · Files and Document Management

**Purpose.** Store and control documents.

**Real capabilities.** `FileManagerService`, one view, file attachment to records, HR document attachments, item images.

**Gaps.** No versioning, approval, controlled distribution or transmittals — all of which R7 construction needs.

**Next increment.** R1 (attachment surface), R7 (document control).

---

## 18 · Notifications

**Purpose.** Tell people things happened.

**Real capabilities.** `NotificationService` with optional entity linkage; SignalR `NotificationsHub` and `ChatHub`; mutes.

**Gaps.** **Two paths coexist** — legacy `NotifyAsync` and the event projection consumer — and a second path bypassing the outbox was observed during hyper work. Preferences will overlap Communication's own preference model once activated.

**Next increment.** R2, reconciled during Communication activation.

---

## 19 · Workflow and Approvals

**Purpose.** Route work for authorization.

**Real capabilities.** `InventoryApprovalService`, `LeaveWorkflowService`, an approvals inbox view, discount approval in pricing.

**Gaps.** No general engine. Each module implements approval its own way, which is why there are three unrelated implementations.

**Next increment.** R10.

---

## 20 · Search

**Nothing exists.** No service, no controller, no index. Zero source files match.

**Next increment.** R12, and it must honour per-module authorization at query time rather than filtering after retrieval (D-28).

---

## 21 · AI Platform

**Real capabilities.** `AiService`, `AiInsightsService`, `AiController`; a separate `crossbuy_ai` Python workspace with phase-0 verification scripts.

**Gaps.** **No AI-specific authorization.** An AI surface that reads business data inherits whatever the calling context can see, and that context still has 143 unprotected actions behind it.

**Decisions.** D-27 — recommendation is that AI reads the reporting dataset layer, never raw tables.

**Next increment.** R12.

---

## 22 · Integration Hub

**Nothing exists.** No connectors, no webhooks, no public API surface.

**Next increment.** R15.

---

## 23 · Identity and Access

**Real capabilities.** Login, session, employee session blob, token service, 5 Account views, `AuthApiController`, `MeApiController`; platform role directory and permission provider.

**Gaps.** No SSO, no MFA, no external principal model (which blocks portals — D-29).

**Next increment.** R1 hardening, R11 for external identity.

---

## 24 · Audit and Compliance

**Real capabilities.** Construction audit service (append-only, scoped); Communication audit writer (not activated); business events as a de-facto audit stream.

**Gaps.** No unified audit surface, no retention policy, no compliance reporting.

**Next increment.** R10.

---

## 25 · Master Data

**Nothing exists** as a governed capability. Employee, Customer, Item and Supplier are each owned informally by their originating module.

**Decision.** D-31 — the recommendation is per-entity ownership with a register, because those four entities have genuinely different natural owners.

**Next increment.** R3.

---

## 26 · Customer Portal

**Real capabilities.** `PortalController`, `StoreController`, `StoreCatalogService`, 3 views, public catalog reads pinned to the configured `Store:StoreCompanyId` through the constrained public scope.

**Gaps.** No external identity, no self-service transactions, no partner access.

**Decision.** D-29 — an external user is not an employee with fewer rights.

**Next increment.** R11.

---

## 27 · Mobile and Field Operations

**Real capabilities.** `crossbuy_mobile` Flutter app: 10 screens, services, providers, models, l10n. Mobile attendance exists.

**Gaps.** No mobile coverage in the test suite. No offline policy beyond POS sync. No field capture for construction.

**Decision.** D-30.

**Next increment.** R14.

---

## Cross-cutting observations

1. **Three complete platforms have no users.** Reporting, Communication and Construction C1 together represent 533 passing tests, 34 tables and zero delivered screens. That is the single largest theme of this reassessment.
2. **Two modules run background workers with no owner.** Tasks and Calendar.
3. **The deployment tree has split.** Two `deploy/sql` directories, 4 overlapping files, all 4 different.
4. **Security debt is concentrated, not diffuse.** 143 actions, heavily in HR, CRM and Accounting.
5. **The strongest modules are the oldest.** Accounting, Inventory and POS have the most screens and the most operational depth; the newest work has the most tests and the least reach.
