# Stage 003 — Stage 1 Batch A (BusinessContext and session-free permissions)

**Measured:** 2026-08-03, at the completion of Stage 1 Batch A.
**Previous record:** [Stage-002-Stage-0-Result.md](Stage-002-Stage-0-Result.md) — 50.20.
**Model:** [Platform-Maturity-Model.md](../Platform-Maturity-Model.md) — weights fixed, 10-point steps (R9).
**Correction issued during this batch:** [CORRECTION-003](../CORRECTION-003-Permission-Attribute-Recognition.md).

> **Immutable (R8).** Not edited after publication.

---

## Score

| # | Dimension | Weight | Level | % | Score | Δ vs 002 |
|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — |
2 | Security and Company Isolation | 15 | 3 | **60** | **9.00** | **+1.50** |
3 | Platform Kernel and Audit | 10 | 4+ | **90** | **9.00** | **+1.00** |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 | — |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — |
9 | Testing and Reliability | 5 | 2+ | 50 | 2.50 | — |
10 | Deployment and Operations | 3 | 3 | 60 | 1.80 | — |
| | **Total** | **100** | | | **52.70** | **+2.50** |

**Overall maturity before: 50.20. After: 52.70. Net gain: +2.50 percentage points. Relative improvement: +4.98%.**

Tests: **230 → 283**, all passing (241 passed + 42 skipped without `CROSSBUY_TEST_SQL`).

---

## What moved, and why

### 2 · Security and Company Isolation: 50 → 60 (+1.50) — a full level, from two separate causes

Level 3 reads *"most mutating actions gated by a single mechanism; `CompanyID` enforced centrally"*. Both criteria
moved, and **they moved for different reasons — one is work, one is a measurement correction. Per R7 they are
reported separately rather than pooled.**

**(a) Coverage is no longer a minority — and that is a CORRECTION, not work.**
[CORRECTION-003](../CORRECTION-003-Permission-Attribute-Recognition.md) established that **195 of 384** mutating
actions (51%) carry a module permission, not the 76 of 381 (20%) previously published. Two scanner defects hid 85 of
173 permission attributes: fully-qualified names (`[CrossBuy.Models.AccPerm("post")]`) were discarded, and
`PosLaneActivityGuard` / `DevOnly` were absent from the recognised-guard list. **Batch A did not add a single
permission attribute.** The system was always at 51%; what we knew was wrong.

**(b) Tenancy is no longer manual for identity, company and permission decisions — and that IS the work.**

- **The company-1 fallback is deleted.** `BusinessContextAccessor.FallbackCompanyId = 1` silently substituted
  company 1 for any request whose company could not be resolved. It fired on a live path: `PosAppController`'s KDS
  and delivery screens wrote a session blob containing `FullName`/`Email`/`ProfileImage` and **nothing else**, so a
  kitchen screen on a branch belonging to company 71 operated as company 1 for every event, notification and
  timeline read. Both writers now state the real employee, branch and branch company, **and** the fallback is gone —
  fixing only the writers would leave the next incomplete blob doing the same thing quietly.
- **`const int CompanyId = 1` removed from four shared security services** (Accounting, Inventory, CRM,
  InventoryApproval). Roles were read for company 1 whatever company the user belonged to, which on a second company
  made a module either wrongly open or wrongly closed. `InventoryApprovalService` was the worst of the four: an
  approval raised by a company-2 user was stored under company 1, notified company 1's managers, and on approval
  **replayed its held stock/PO command into company 1**.
- **Company isolation is now enforced in the permission gate, before any module is consulted.**
  `PlatformPermissionProvider` resolves the record through `IEntityRegistry` (the same company-scoped query the
  record picker uses) and denies a cross-company target. A ChiefAccountant in company 2 holding `manage` can no
  longer be granted a company-1 invoice, because the module's role check knows nothing about which record is asked
  about.
- **`IsSystem ⇒ Allow` is gone.** A single boolean used to bypass every module permission for every action,
  including `ViewRestricted`. It is replaced by `SystemContextPolicy` — a declared allow-list, currently `View`
  only — and a system context is still confined to its own company.

**The named residual that keeps this at 60 rather than 70:** the **query layer is untouched**. There are still no EF
global query filters, and 767 `DefaultCompanyId` / `HrCompanyId` call sites remain across 8 controllers. Isolation is
enforced at the *permission* boundary, not at the *data* boundary. That is Batch B, by design.

### 3 · Platform Kernel and Audit: 80 → 90 (+1.00)

Level 4 was already fully met at Stage 002 (observability + guarded retry + deployment-state tracking). Two of level
5's characteristics are now partially present, and the half-step names them:

**The kernel's authorization is now enforced rather than nominal.** ADR-006 shipped with a documented limitation:
`NotificationProjectionConsumer` built a per-recipient `BusinessContext` and the adapters **discarded it**, reading
the employee from `Session["Employee"]` (absent in a dispatcher) and the company from a constant. The loop ran; it
decided nothing. It now genuinely excludes unauthorized recipients, cross-company recipients, inactive employees and
employees with no company — proven by a test that captures every context the provider is asked about and asserts one
question per recipient carrying that recipient's own identity.

**The system-context bypass is governed.** The highest-value bypass in the platform — one boolean — is now a short,
explicit allow-list whose widening is a visible code change with a reason attached. This matters out of proportion to
its size because Workflow, Search, AI Context and the Workspace are all specified to reuse this provider.

**Named residuals for level 5:** no retention or archival policy for `BusinessEvents`; the registry still covers 9 of
58 objects, so "every correction primitive" is only true of the ones that exist.

---

## What did not move, and why not

| Dimension | Why unchanged |
|---|---|
**1 · ERP Business Coverage** | No business function was added. Multi-tenancy is still nominal: 767 `DefaultCompanyId = 1` call sites remain, and `FiscalPeriod` still has no `CompanyID`. Level 4 needs genuine multi-tenancy. |
**4 · Business Object Coverage** | Still 9 of 58 registered, timeline on 4. Batch A added no entity. |
**5 · Task and Work Management** | Untouched. Still no unified work inbox; tasks are still not registry citizens. |
**6 · Workflow and Approvals** | `InventoryApprovalService` became company-correct, which is scored under Security. **The four silos are unchanged** — no engine, no delegation, no escalation, and the discount silo still leaves no record. |
**7 · Communication and Collaboration** | The notification consumer now excludes unauthorized and cross-company recipients — that is scored under **Kernel** enforcement and deliberately not double-counted. No channel became entity-addressable; `CommMessage` still has no `EntityType`. |
**8 · Search, AI and Intelligence** | Untouched. `IModuleAccessService` is the contract AI Context needs, but **R3 forbids points for a prerequisite that nothing uses yet**. |
**9 · Testing and Reliability** | **283 tests is +53 in one batch, and it moves nothing.** Level 3 requires the financial writers and the main business flows to have regression tests. All 53 new tests exercise context resolution, permission evaluation and notification authorization. **Accounting, Inventory, POS, HR, CRM and Projects still have zero tests**, and neither `JournalEntryService` nor `StockService` has a test of its accounting or costing behaviour. Counting test volume as maturity is the inflation R1/R5 exist to prevent. |
**10 · Deployment and Operations** | Batch A is code-only: no SQL, no schema change, no deployment coupling. |

---

## Regressions (R6)

**None.** No dimension decreased, no test was removed or weakened.

Three behaviour changes are recorded here because "no regression" has to mean something:

1. **An unresolved request is now DENIED where it used to be evaluated as company 1.** The legacy
   `CanAsync(action)` methods return `false` when no context resolves, and `CrmAccessService.VisibleOwnerIdsAsync()`
   returns an empty set rather than `null` (which meant *unrestricted*). This is the intended fix — an anonymous or
   broken request must not borrow another company's authorization — but code that relied on the old permissiveness
   will now be refused.
2. **Bootstrap-open is evaluated per company.** `AnyRoleConfiguredAsync` used to ask about company 1. It now asks
   about the caller's own company, which is the only meaningful answer on a multi-company install — but it means a
   company-2 user is now subject to company 2's configuration rather than company 1's.
3. **Inactive employees are excluded from notification audiences.** A notification to a leaver is a dead link and a
   data leak. This required `IsActive = true` to be added to the slice-2 notification test fixtures, which modelled
   current staff without saying so.

## Measurement corrections (R7)

**One, and it is large.** CORRECTION-003: the security backlog is **189**, not 306 — the published figure was
**overstated by 62%**.

| Metric | CORRECTION-002 | **Corrected** |
|---|---|---|
Mutating actions | 381 | **384** |
— with a module permission (any level) | 76 | **195** |
— **without** | **305/306** | **189** |
`AccPerm` / `CrmPerm` actions | 20 / **0** | **55 / 37** |

Earlier stage records are **not** retro-edited (R8). Stages 000–002 were all scored on the same wrong basis, so the
deltas between them are unaffected — the error was a constant across the series. What the correction does change is
the *reasoning* behind dimension 2's absolute level, and §"What moved" above separates the correction's contribution
from Batch A's work rather than pooling them.

**The shape of the problem also changed.** 132 of the 189 sit in **HR, Projects and Tasks/Communication — modules
with no access service at all.** The fix for those is building three access services (Stage 1 Batch C), not adding
attributes. That is why Batch A was scoped to making the four *existing* services context-aware.

## Deployment reality (R4)

Unchanged from Stage 002, and Batch A adds nothing to it: **no SQL, no schema change, no deployment coupling.**
`platform_business_events_slice_002.sql`, `comm_outbox_slice_003.sql` and `platform_schema_history.sql` remain
pending controlled manual deployment to CrossBuyDB2. Slice 1 is applied. **Nothing was executed against CrossBuyDB2
in this batch.**
