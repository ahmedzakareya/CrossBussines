# Stage 1 — Batch D1 — Phase 1: Inventory Reconciliation

**Requirement status: NOT STARTED** (no production code modified; this is the pre-implementation gate).
**Date:** 2026-08-04.

---

## 1. Corrected endpoint counts

Five independent sources, reconciled — not averaged.

| # | Source | Result |
|---|---|---|
| 1 | **Source inspection** — controller files on disk | **39** controllers |
| 2 | **Controller-action scanner** — `Permission-Coverage.csv` | 1065 actions, **388** mutating |
| 3 | **Raw grep** `[HttpPost\|HttpPut\|HttpDelete\|HttpPatch]` | **389** occurrences (388 `HttpPost` + 1 `HttpPut`) |
| 4 | **Endpoint inventory** — `Endpoint-Inventory.csv` | 1065 rows, **388** mutating |
| 5 | **In-body authorization detection** — verified per row | **54** in-body, **150** module-attribute, **183** backlog |

### 1.1 The 389 ÷ 388 discrepancy — explained, not smoothed

Source 3 exceeds sources 2 and 4 by exactly one. The cause is a **commented-out attribute**:
`CrossBuy/Controllers/AccountController.cs:183` → `//[HttpPost]`. Raw grep counts text; the scanner counts actions.

```
389 raw occurrences
 −1 commented out (AccountController.cs:183)
388 mutating actions   ← matches sources 2 and 4 exactly
```

The single `HttpPut` (`HrApiController.UpdateJobTitle`, `PUT jobtitles/{id:int}`) **is** present in the inventory as
`mutating=True`, so it is not a scanner miss. Controller counts agree: **39 on disk = 39 distinct in the CSV.**

### 1.2 The classification correction I had to make

My first pass produced **150 / 184**, not the accepted **151 / 183**. The cause was my own omission: the module-permission
list must include **`PlatformOps`**, an 8th attribute. The single row it accounts for is
`BusinessEventMonitorController` — which is precisely what `PlatformOps` exists to guard.

`PlatformOps` was then **verified to check a real role** before being credited, per CORRECTION-004's rule that no
attribute may be credited if it checks nothing: it requires an Identity role in
`{Admin, Administrator, SuperAdmin, PlatformOps}`, **or** accounting `manage`, and denies otherwise
(`PlatformOpsAttribute.cs`). It is a genuine authorization control.

**Not counted as authorization**, per the brief: `SessionValidation` (257 mutating actions),
`PosLaneActivityGuard` (47), `Authorize(...)` (18), `ValidateAntiForgeryToken`, `DevOnly`.

### 1.3 Accepted baseline — CONFIRMED unchanged

| Metric | Baseline | Reconciled today | Δ |
|---|---|---|---|
| Mutating actions | 388 | **388** | 0 |
| Module-attribute protected | 151 | **151** | 0 |
| Verified in-body protected | 54 | **54** | 0 |
| **Backlog** | 183 | **183** | 0 |

`388 = 151 + 54 + 183` reconciles. `Stage1PermissionBacklogTests` passes with its pinned figures untouched.

---

## 2. Concurrent additions or removals

**None since Batch C.1.** Verified independently rather than assumed: the raw-grep count over the *current* working
tree is 388 mutating actions, so no endpoint was added or removed after the CSV was generated. The parallel team's
uncommitted controllers (`AnnouncementsController`, `CommController`, `CommentsController`, `CalendarController`,
`FileManagerController`, `BusinessEventMonitorController`, `PlatformTimelineController`) were **already** captured in
the baseline and are already inside the 183.

Nine tracked controllers carry parallel modifications (`AccountingApiController`, `DevSeedController`,
`ChatController`, `HomeController`, `PeopleController`, `PosAppController`, `ProjectController`, `StoreController`,
`TasksController`). None changed the mutating count. **Our base can still shift invisibly** — all of their work is
uncommitted — so the count is re-derived at the start of each wave, not pinned once.

**Other mutating surfaces checked and excluded:** zero minimal-API `MapPost/Put/Delete/Patch` in `Program.cs`; the 3
hubs are a separate surface, and the only client-supplied-room hub method was closed in C.1 (`ChatHub`). `PosHub`
joins a branch group derived from the account, `NotificationsHub` per-employee/per-company groups from the resolved
employee — neither takes a client-supplied room id.

---

## 3. THE FINDING THAT REFRAMES THIS BATCH — hardcoded company 1 in production controllers

This was not in the brief's expected findings and is more consequential than the backlog count.

**Twelve controllers hardcode the company as a compile-time constant:**

| Controller | Constant | References |
|---|---|---|
| `InventoryController` | `DefaultCompanyId = 1` | **267** |
| `AccountingController` | `DefaultCompanyId = 1` | **199** |
| `CrmController` | `DefaultCompanyId = 1` | **97** |
| `ProjectController` | `DefaultCompanyId = 1` | **83** |
| `PosController` | `DefaultCompanyId = 1` | 40 |
| `TasksController` | `DefaultCompanyId = 1` | 33 |
| `CurrencyController` | `DefaultCompanyId = 1` | 9 |
| `BrandController` | `DefaultCompanyId = 1` | 7 |
| `AdminController` | `HrCompanyId = 1` | — |
| `HyperPosController` | `PosCompanyId = 1` | — |
| `PosAppController` | `PosCompanyId = 1` | — |
| `HyperController` | `PosCompanyId = 1` | — |
| `Api/InventoryApiController` | `CompanyId = 1` | — |

**Why this matters more than the 183.** It means the company is **not derived from the caller** on these paths — it
is a literal. And the blast radius includes the endpoints already counted as protected:

```
242 of 388 mutating actions (62%) live in the 8 DefaultCompanyId controllers
  150  attribute-protected   ← ALL 150 of the platform's attribute-protected mutating actions
    1  in-body protected
   91  backlog
```

So **every** attribute-protected mutating action in the system checks a role but resolves its company from a
constant. On the current single-company install that is invisible. On a multi-company install it means an employee of
company 2 either operates on **company 1's** data or is denied everything — and *"a request-supplied `companyId` is
compatibility-only… never coerce it"* has a sibling defect nobody had counted: a **compile-time** company.

This directly violates a standing rule already on the books — *"never use CompanyID = 1 as an implicit fallback"* and
*"an unresolved company scope reads no company-scoped data and writes none. Fail closed; never default to a
company."* It is in production code today, and the coverage metric does not see it because the metric asks *"is a
role checked?"* and not *"where did the company come from?"*

**Consequence for the metric.** The reported "151 attribute-protected" is honest about role checks and **silent about
company source**. It therefore **overstates real protection**. This is a measurement correction of the same class as
CORRECTION-004 and is recorded as **CORRECTION-005** (see §9).

**Scope decision.** Repointing 735+ references is far larger than D1's remit and would touch calculation-bearing code
in the two financial controllers. D1 will:

* fix the company source **on every endpoint it remediates** (resolve from `BusinessContext`, validate the persisted
  record's company, reject tampering) — the brief already requires this per endpoint;
* **not** perform a tree-wide constant sweep, which is its own batch with its own regression surface;
* add a **pinned test** that counts the hardcoded constants so the number cannot grow silently and cannot be
  forgotten.

---

## 4. Critical and High endpoint list

### 4.1 Wave assignment — reconciles to 183 exactly

| Wave | Endpoints | Risk |
|---|---|---|
| **W1 — Critical financial / data-integrity** | **40** | Critical |
| **W2 — HR, identity and privilege** | **52** | High |
| **W3 — Projects (non-financial)** | **7** | High |
| **W4 — Tasks** | **10** | Medium |
| **W5 — Communication** | **28** | Medium |
| **W6 — POS / Admin / setup** | **44** | Medium–Low |
| **Anonymous by design** | **2** | Justified |
| **Total** | **183** | |

Two endpoints were moved out of their controller's default bucket on inspection, because the controller is not the
risk:

* `PosController.CustomerQuickAdd` → **W1**. It creates a `Customer` — financial master data — not POS setup.
* `PosController.AssignPosRole`, `RemovePosRole` → **W2**. These **grant and revoke privilege**. A privilege-granting
  endpoint is an identity operation regardless of which controller hosts it; leaving them in a "lower-risk setup"
  bucket would be the classification error that matters most.

### 4.2 Critical — Wave 1 (40)

| Controller | Actions | Effect |
|---|---|---|
| `ProjectController` | `PostBilling` `ApproveBilling` `DeleteBilling` `SaveBilling` `PostSubBilling` `ApproveSubBilling` `DeleteSubBilling` `SaveSubBilling` `ReceiveAdvance` `ReleaseRetention` `ReleaseSubRetention` `PostEquipmentDepreciation` `DeleteEquipmentDepreciation` `SaveEquipmentDepreciation` `PostLabor` `PostMaterialIssue` `SaveMaterialIssue` `DeleteMaterialIssue` `ApproveVariationOrder` `SaveVariationOrder` `DeleteVariationOrder` `SaveBoq` `SaveSubcontract` (23) | GL posting, **stock** (material issue), money in/out, contract value, budget |
| `AdminController` | `PostFinalSettlement` `PostLeaveProvision` `Encash` `RunLeaveCarryOver` `SaveSalaryPolicy` (5) | **Payroll** GL, provisions, cash payout, salary basis |
| `PosController` | `OpenShift` `CloseShift` `PrepareFinished` `PrepareSemi` `CustomerQuickAdd` (5) | Cash/shift reconciliation, **stock** (production), financial master data |
| `AccountingController` | `StampInvoiceCustomer` `CustomerQuickAdd` `VendorQuickAdd` (3) | **Invoice legal beneficiary**, customer/vendor financial master data |
| `TasksController` | `GenerateInvoice` `PostLaborToWO` (2) | Invoice creation, work-order labour cost |
| `HyperPosController` | `StampInvoiceCustomer` (1) | Invoice legal beneficiary |
| `InventoryController` | `WarehouseQuickAdd` (1) | Stock structure |

**Inspected in detail — the two `StampInvoiceCustomer` endpoints are not equivalent, and the brief's named one is the
safer of the two:**

* **`HyperPosController.StampInvoiceCustomer`** (the endpoint the brief singles out) — `PosLaneActivityGuard`, plus a
  capability gate (`OfficialInvoice`), plus `BranchOwnsInvoiceAsync(c.BranchId, invoiceId)` and
  `i.CompanyID == PosCompanyId`. So it **does** validate that the invoice belongs to the caller's branch and company.
  What it lacks is a **role** check: any cashier on that branch can stamp the beneficiary on any invoice of that
  branch. Real, bounded. **Risk: High** (downgraded from Critical after inspection — bounded by branch).
* **`AccountingController.StampInvoiceCustomer`** — `SessionValidation` + `ValidateAntiForgeryToken` only, company from
  the constant `DefaultCompanyId`. **Any signed-in employee can rewrite the legal beneficiary name and tax number on
  any posted sales invoice in company 1.** That is tax-document alteration on a printed statutory document.
  **Risk: Critical** — and it was *not* on the brief's watch list, because attention had gone to the POS twin.

This is exactly why the brief forbids bulk attribute-adding: the two endpoints share a name and need different
answers.

### 4.3 High — Wave 2 (52) and Wave 3 (7)

**W2 (52):** `AdminController` 37 remaining (employee records, employment status, contracts, employee documents and
attachments, appraisals incl. scores, attendance and attendance policy, leave types/policies, holidays, training,
recruitment pipeline, org structure) · `AccountController.ChangePassword` **(identity — credential change)** ·
`PosController.AssignPosRole` `RemovePosRole` **(privilege grant/revoke)** · `HrApiController.CreateJobTitle`
`UpdateJobTitle` · `LeaveApiController.Create` `Decision` · `PeopleController.AcknowledgeAppraisal` `CreateLeave`
`CreateRequest` `DecideRequest` · `AiController.AnomalyJournalScan` `ForecastCashflow` `InventoryAnalyze` `Diag`
(JWT-authenticated APIs that **return journal, cashflow and inventory analysis** — authentication-only today).

**W3 (7):** `SaveProject` `DeleteProject` `SaveProgress` `ConfirmProgress` `DeleteProgress` `SaveActivityType`
`DeleteActivityType`.

### 4.4 Anonymous by design (2) — justification required and given

`AccountController.Login`, `AuthApiController.Login`. Both must be anonymous to function. Neither mutates business
data; both are the authentication entry points. Retained as documented exceptions, and the pinned test already
asserts `AuthApiController.Login` is the only anonymous backlog API.

### 4.5 Per-endpoint field capture

The brief's 27-field record (controller, action, method, route, module, auth mechanism, existing attribute, in-body
check, company source, request-supplied identifiers, direct `CrossDbContext` usage, service called, transaction
behaviour, business/financial/stock/payroll/confidentiality/cross-company impact, anti-forgery posture, required
access service, permission, `PermissionTarget`, record rule, risk, wave) is captured per endpoint in
`docs/architecture/evidence/D1-Endpoint-Remediation.csv`, generated **per wave immediately before that wave's
remediation** rather than all at once — because the tree shifts under us and a field captured three waves early is a
field that has already gone stale.

**Status: W1's 40 rows captured. W2–W6 not yet captured** — see §9.

---

## 5. Wave plan

| Wave | Access service | Permission vocabulary | New attribute needed? |
|---|---|---|---|
| W1 | `AccountingAccessService`, `InventoryAccessService`, `ProjectsAccessService`, `HrAccessService`, `PosAccessService` | existing `post`/`manage`/`doc` + project budget/billing | No — `AccPerm`, `InvPerm`, `ProjectPerm` exist |
| W2 | `HrAccessService`, platform-admin policy | existing `HrActions` + a **platform-admin** gate for identity/privilege | `HrPerm` exists; identity ops need `PlatformOps`, not HR |
| W3 | `ProjectsAccessService` | existing | `ProjectPerm` exists |
| W4 | `TasksAccessService` + `TaskScopeQuery` | existing `TasksActions` | **`TaskPerm` must be created** |
| W5 | `CommunicationAccessService` | existing `CommunicationActions` | **`CommunicationPerm` must be created** |
| W6 | `PosAccessService`, `PlatformOps` | existing | classification first, then enforcement |

**Ordering rationale:** W1 first because it is the only wave where a missing check has an irreversible financial
consequence. W2 second because privilege-granting and credential endpoints are the escalation path into every other
wave. W4/W5 need two new API-safe attributes built first, so they follow the waves that need none.

**API-safe guards.** `AiController`, `HrApiController`, `LeaveApiController`, `NotificationsApiController` are JSON
APIs. An MVC redirecting attribute would return a **302 to an HTML login page** to a JSON client, which the brief
forbids. These need the API-safe guard returning 401/403 in the project's response shape — the same shape
`IAccountingApiAuthorization` established in Hotfix A.1, which is the precedent to reuse rather than reinvent.

---

## 6. Files to modify

**Controllers (W1):** `ProjectController.cs` · `AdminController.cs` · `PosController.cs` · `AccountingController.cs` ·
`TasksController.cs` · `HyperPosController.cs` · `InventoryController.cs`
**Later waves:** `AccountController.cs` · `HrApiController.cs` · `LeaveApiController.cs` · `PeopleController.cs` ·
`AiController.cs` · `ChatController.cs` · `CommController.cs` · `CommentsController.cs` · `CalendarController.cs` ·
`AnnouncementsController.cs` · `NotificationsController.cs` · `NotificationsApiController.cs` ·
`FileManagerController.cs` · `PosAppController.cs` · `BrandController.cs` · `ServiceController.cs`
**New:** `Models/TaskPermAttribute.cs` · `Models/CommunicationPermAttribute.cs` · an API-safe guard
**Access services:** additive actions only where a required permission has no vocabulary — **no new access service.**
**Not touched:** `JournalEntryService`, `StockService` (architectural invariants), any calculation, any posting path,
any SQL script, `CrossDbContext`, `Program.cs` beyond attribute registration if required.

---

## 7. Expected new tests

Per remediated endpoint, the brief's 10 general cases; plus the wave-specific sets (financial 11–15, HR 16–20,
projects 21–25, tasks 26–30, communication 31–36). Estimated **≈120–160 new tests**, of which **SQL Server
integration tests** for: refused financial action creates no journal; refused stock action creates no stock movement;
refused payroll action creates no payroll posting; `BusinessEvent.CompanyID` matches the affected entity; transaction
rollback on refusal. Existing **645 must stay green**, and the suite is run **multiple full passes** to expose flaky
authorization tests.

---

## 8. Compatibility risks and rollback

**Risks**

1. **Bootstrap-open asymmetry.** `AccountingAccessService` and `CrmAccessService` return *true for every action* when
   their role table is empty company-wide. Adding `AccPerm` to an endpoint on an unconfigured install changes
   nothing — so a green test on an unconfigured database **proves nothing**. Every W1 test must seed a role row so
   the closed path is genuinely exercised. This is the single most likely way to ship a false "protected".
2. **`PlatformRoleAssignments` and `ProjectMembers` are absent from `CrossBuyDB2`.** W3's project-membership rules
   cannot be exercised there. Deployment stays the operator's call; the tests run on their own databases.
3. **JSON clients receiving 302.** Mitigated by the API-safe guard (§5).
4. **POS lane behaviour.** `BranchUserRoles` is the documented permanent POS exception; enforcement must compose with
   it, not replace it. `PosLaneActivityGuard` stays — as a lane guard, not as authorization.
5. **`StockBalance` locking.** Excluded from EF global filtering and its `UPDLOCK/HOLDLOCK` semantics are untouched.
6. **Parallel tree drift.** Uncommitted work in nine controllers we must edit. Mitigation: selective commits, our
   lines only, via git plumbing.
7. **Hidden buttons ≠ authorization.** Menu/button visibility corrections only where a now-denied action would
   otherwise present a dead control.

**Rollback.** Every change is an added attribute or an added in-body check plus tests — no schema, no data, no
calculation. Rollback is `git revert` of our commits per wave; each wave is committed separately so a single wave can
be reverted without the others. No database change to undo, in any wave.

---

## 9. Requirement status

| Item | Status |
|---|---|
| Phase 1 — inventory reconciliation (5 sources) | **Completed** |
| Phase 1 — corrected counts, concurrent changes, Critical/High list, wave plan, files, tests, risks, rollback | **Completed** |
| Per-endpoint 27-field capture | **Partial** — W1's 40 rows captured; W2–W6 deferred **by design**, captured per wave immediately before remediation. *Missing work:* 143 rows. *Reason:* a field captured three waves early is stale before use, and the tree is shifting under uncommitted parallel work. *Security impact:* none — no endpoint is claimed protected. *Business impact:* none. *Next step:* capture W2's 52 rows at the start of W2. |
| CORRECTION-005 (company-source measurement) | **Not Started** — *Missing work:* the correction record and the pinned constant-count test. *Reason:* Phase 1 is analysis; the test is production-adjacent and belongs with W1. *Security impact:* the 151 figure continues to overstate protection until recorded. *Business impact:* none. *Next step:* write `CORRECTION-005` and the pinned test as W1's first commit. |
| **W1 – W6 remediation** | **Not Started** |
| Batch D2 / Security Administration Console | **Not Started** — correctly, out of scope |

**No production code was modified. No SQL was executed against any database. No endpoint is claimed protected.**