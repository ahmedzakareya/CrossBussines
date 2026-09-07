# Stage 2 — Phase 0 — Architecture Review and Improvement Proposal

**Status: analysis only. No production code was written. No SQL executed against `CrossBuyDB2`.**
Stage 1 documents were not modified.

---

## 1. P0-1 — Stage 1 baseline verified against live source

Every supplied figure re-derived from the working tree, not accepted on trust:

| Metric | Supplied | Measured | ✔ |
|---|---|---|---|
| Mutating actions | 388 | **388** | ✔ |
| Attribute-protected | 157 | **157** | ✔ |
| Verified in-body protected | 88 | **88** | ✔ |
| Backlog | 143 | **143** | ✔ |
| Hard-coded company controllers | 13 | **13** | ✔ |
| Hard-coded company references | 908 | **908** | ✔ |
| Final maturity | 57.60 | **57.60** (record 007) | ✔ |
| Suite | 763/763, 0 skipped | **763/763, 0 skipped** | ✔ |

**The baseline is confirmed. Nothing in it required correction.**

### 1.1 Stage 2 delta record — one concurrent change, and it is good news

**The parallel team's Razor break is fixed.** `Views/Accounting/CustomerAnalytics.cshtml` compiled with 14 errors
throughout Stage 1's final increments, which forced verification with `-p:RazorCompileOnBuild=false` — a declared
limitation meaning **no view was compiled during Stage 1 acceptance**.

That constraint is gone. Phase 0 re-ran acceptance with **Razor compilation ENABLED and SQL Server ENABLED
simultaneously**: application build **0 errors**, suite **763/763, 0 skipped**. This is a *stronger* acceptance than
Stage 1 could produce, and it retroactively closes limitation #6 of the closure report — without altering that
report, which stays frozen.

**Separated from the baseline:** 66 untracked + 65 modified files remain the parallel team's uncommitted work. The
standing risk (HM-D44 — our base can shift invisibly) is unchanged and carries into Stage 2.

**Impact classification:** positive, no action required, no Stage 1 figure changes.

---

## 2. P0-2 — What Stage 2 should build first

### 2.1 The single most important finding

**The proposed Stage 2 order is nearly right, but item 1 must be narrower and item 3 must move earlier than its
number implies.**

Stage 1's two most expensive failures were **both measurement failures, not code failures**:

* the scanner misread **38 of 40** protected endpoints, because its detector was three regexes;
* **46 SQL tests reported "skipped"** for two batches and were counted as coverage — they had never executed, and
  their first run failed with twenty `Invalid column name` errors.

Neither was caught by review, by tests, or by a delivery report. Both were caught only when something forced the
instrument to be exercised. **That is the strongest available evidence for what Stage 2 must build first**: not a
broad "Engineering Platform" of 20 analyzers, but the **two guardrails that would have caught those two failures** —
a Roslyn authorization analyzer and CI test-evidence enforcement. Everything else in the analyzer catalogue is
valuable and none of it is urgent by comparison.

### 2.2 Answers to the fifteen review questions

| # | Question | Answer |
|---|---|---|
| 1 | **Build first?** | **Stage 2A, narrowed:** Roslyn authorization analyzer + CI evidence enforcement. Not 20 analyzers. |
| 2 | **Reusable today?** | `BusinessContext` pipeline · `IPlatformRoleDirectory` (one RBAC table) · `IOrgHierarchy` · `AccessScope`/`TaskScopeQuery` · `IRequestCompanyResolver` · `ICompanyIsolationBypass` · company query filters · `BusinessEvents` + outbox dispatcher · `EntityRegistry` · `NotificationService` · the four gate patterns · `ApiPerm`. These are genuinely reusable and proven. |
| 3 | **Partial only?** | Business events (opt-in per entity; only Quotation/JournalEntry onboarded) · timeline (read path exists, few producers) · comments/attachments (exist per-module, not universal) · search (none) · AI context (none) · workflow (leave-only, hand-rolled) · escalation (none) · audit (per-table `CreatedBy/At`, no unified history). |
| 4 | **Duplicated across modules?** | **Company resolution** — 13 controller constants + 2 POS. **Role storage** — `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles`, `BranchUserRoles` coexist with `PlatformRoleAssignments`. **Comments** (`DocComments` vs project comments). **Attachments** (HR docs vs FileManager vs project files). **Notification producers** (outbox projection vs direct `NotifyAsync`). |
| 5 | **Legacy patterns forcing rewrites?** | (a) hardcoded company constants — 908 refs; (b) four module-specific role tables that `PlatformRoleAssignments` was built to replace but has not yet absorbed; (c) session-blob identity in POS lanes; (d) `[SessionValidation]` treated as a guard on 257 mutating actions; (e) bootstrap-open, which makes an unconfigured install permissive by design. |
| 6 | **Shared frameworks needed before HR/WMS/Manufacturing?** | In order: **Business Object Registry + capability metadata** → **unified attachments** → **unified workflow/approvals + escalation** → **unified audit/history** → **enterprise search**. HR and WMS both need attachments, approvals and audit on day one; building them per-module is how the duplication in row 4 happened. |
| 7 | **Need extension?** | Accounting · Inventory · Projects · Tasks · Communication · POS. |
| 8 | **Need partial redesign?** | **Master Data** (the item/product model) · HR (payroll basis + org structure) · CRM (owner-scoping only, no engine) · Reports. |
| 9 | **Need full replacement?** | **None.** No module warrants a rewrite on current evidence. Master Data is the closest and is still a *redesign of the model and UX over preserved data*, not a replacement. Recommending a rewrite anywhere would not be supportable from source. |
| 10 | **Not yet?** | Low-code studio · process mining · blockchain · digital twin · IoT · industry packs · marketplace · EPM. All lack a business case in current source. |
| 11 | **Sequencing to minimise rework?** | Guardrails → Registry/capabilities → Master Data → HR → WMS → Manufacturing. Master Data before HR is **essential**: WMS and Manufacturing both consume the item model, and changing it after they are built is the expensive path. |
| 12 | **User-visible value per phase?** | See `Stage-002-Dependency-Roadmap.md`. Stage 2A is deliberately **developer-visible only** — an honest statement, not a gap. |
| 13 | **Debt blocking features?** | 908 company constants (blocks true multi-tenancy) · no unified attachments (blocks HR documents, WMS proofs, quality records) · no workflow engine (blocks approvals anywhere but leave) · no search (blocks every "find it" story) · EF relational model unvalidated against SQL Server. |
| 14 | **New screens?** | See `Stage-002-Screen-Forecast.md`. |
| 15 | **Unrequested improvements?** | §3 below. |

---

## 3. Unrequested improvements — the permanent delivery principle applied

Every item here was found by inspection, not requested by the brief. Classified as instructed.

### 3.1 REQUIRED NOW (architecturally necessary; block later work if deferred)

**R1 — Absorb the four module-specific role tables into `PlatformRoleAssignments`.**
*Problem:* `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles` and `BranchUserRoles` still hold live
authorization data alongside the shared table Batch C built to replace them. Two role systems answer the same
question. *Evidence:* Wave 1 had to seed **both** `AccountingUserRoles` and `PlatformRoleAssignments` in one test
fixture to make one decision meaningful. *Value:* one place to grant, one place to audit; Batch D2's console cannot
be honest until this exists — it would show half the grants. *Risks:* live authorization data; needs read-both /
write-new migration. *Test:* per-module parity tests proving identical decisions before and after. *Phase:* **2A/2B.**
*Priority:* highest non-guardrail item.

**R2 — Make `bootstrap-open` an explicit, per-company, auditable setting.**
*Problem:* three modules return **true for every action** when their role table is empty for the company. It is a
compatibility device that makes an unconfigured tenant fully permissive, and it is invisible at runtime. *Evidence:*
Stage 1 needed a dedicated guard test asserting *"permission IS granted with no role rows"* to stop future tests
passing vacuously — the trap is real enough to require its own test. *Value:* a new tenant is safe by default; the
state becomes visible instead of inferred. *Risks:* flipping the default would lock out an unconfigured install —
so ship it as an explicit flag defaulting to today's behaviour, with a first-run warning. *Phase:* **2B.**

**R3 — Validate the EF relational model against SQL Server DDL.**
*Problem:* `GenerateCreateScript()` is **rejected by SQL Server** — `FK_Branches_CountriesLookup_CountryID`, multiple
cascade paths. Production schema is hand-written SQL, so the model's relational configuration has **never** been
checked against the real engine. *Evidence:* F4 hit it directly. *Value:* the model and the database are currently
allowed to disagree with no test that would notice. *Risks:* do **not** change cascade behaviour blindly — see
`Stage-002-Engineering-Platform-Proposal.md` §6 for the safe treatment. *Phase:* **2A.**

### 3.2 RECOMMENDED NOW (clearly beneficial, low risk)

**R4 — `[SessionValidation]` should not be silently mistakable for authorization.** It sits on 257 mutating actions
and proves only that a session exists. Rename or attribute-annotate it so its meaning is unambiguous in code as well
as in reports. *Phase:* 2A, mechanical.

**R5 — A per-request authorization decision log.** Stage 1 logs denials individually; there is no single place to ask
"why was this refused". Cheap now, invaluable when Batch D2's console exists. *Phase:* 2B.

**R6 — Retire the interim maturity records or mark them superseded.** `Platform-Maturity-Metrics.csv` holds eight
records; three interim permission figures (151/54/183, 152/55/181) are superseded and a reader cannot tell. *Phase:* 2A.

**R7 — Make `TaskScopeQuery`'s pattern reusable.** It is proven (1 evaluation for 500 rows) and Tasks-only. HR, WMS
and Projects all need set-shaped reads. Generalise when the **second** consumer appears — not before. *Phase:* 2B.

### 3.3 PLANNED LATER

**R8** Unified audit/history framework (needed by HR and Quality; premature before the Registry).
**R9** Enterprise search (high value, needs the Registry first).
**R10** AI context framework (depends on Registry + search + timeline).
**R11** Object-level permission trimming for portals (no portal exists yet).
**R12** Retention/archival (no volume pressure evidenced).

### 3.4 REJECTED, with reason

| Rejected | Reason |
|---|---|
| **Full rewrite of any module** | No source evidence supports it. Stage 1 showed targeted enforcement works; a rewrite would discard working accounting, stock and POS logic. |
| **20 analyzers in Stage 2A** | Only two are evidence-backed. Eighteen speculative analyzers would produce false positives that teach developers to suppress diagnostics — worse than having none. |
| **Blockchain / digital twin / IoT now** | No business case in source. Classified conditional in the platform proposals; not scheduled. |
| **Low-code studio** | Requires a stable Registry, workflow engine and permission trimming — none exist. Building it now guarantees rework. |
| **Forcing every entity into one base class** | Explicitly the wrong shape for this codebase (see framework proposal): entities are already persisted with divergent key/company conventions (`TaskItem.CompanyId` vs `CompanyID`), and inheritance would require touching every table. |
| **Fixing the 908 company constants inside a feature wave** | It is a large mechanical change to calculation-bearing controllers. It must be its own batch with its own regression surface — folding it into a wave is how a wave becomes unreviewable. |

---

## 4. P0-7 — Module reassessment (evidence-based, not inflated)

Maturity is my assessment from source; **Class** is the recommendation.

| Module | Maturity | Strongest | Weakest | Class |
|---|---|---|---|---|
| **Accounting** | High | Two-writer discipline, reversal-only corrections, currency rounding, FX | 201 company constants; 22 API actions | **Extend** |
| **Financial Ops** | Low-Med | Payments/allocations exist | No treasury, no cash forecasting beyond AI stub | **Extend** |
| **Sales** | Medium | Invoice/quotation flow, ETA integration | No pricing engine beyond `PricingService`; no CPQ | **Extend** |
| **Procurement** | Medium | PO→GRN→invoice chain, landed cost | No vendor scoring, no RFQ | **Extend** |
| **POS** | High | Two lanes, capabilities, shift/cash, offline sync | Session-blob identity; `BranchUserRoles` outside platform RBAC | **Refactor** (identity only) |
| **Inventory** | High | Stock writer, UPDLOCK/HOLDLOCK, batch/expiry, moving average | **267 company constants**; item model (see Master Data) | **Extend + Partial Rebuild of the item model** |
| **WMS** | **None** | — | No bins, no putaway, no picking, no waves, no mobile | **Full Platform Build** (new, not rebuild) |
| **Manufacturing** | Medium | Work orders, BOM, labour, partial production | No routing, no capacity, no scheduling, no shop-floor UI | **Partial Rebuild** |
| **Projects** | Medium-High | Billing, retention, advances, BOQ, variation orders, now authorized | 87 company constants; no scheduling/Gantt | **Extend** |
| **HR** | Medium | Payroll, leave, settlement, appraisals, org tree | Org tree has **no CompanyID**; `leave-manage` bootstrap-open; no self-service portal | **Partial Rebuild** |
| **CRM** | Low-Med | Owner-scoped records, leads/opportunities | 97 company constants; no pipeline automation, no campaigns | **Refactor** |
| **Customer Experience** | **None** | — | No case management, no call centre, no SLA | **Full Platform Build** |
| **Tasks** | Medium-High | `TaskScopeQuery` no-N+1, linked-entity gate, generators | No SLA, no escalation, no unified queue | **Extend** |
| **Communication** | Medium | Conversations, announcements, outbox, hub now authorized | No entity-scoped threads; comments duplicated | **Extend** |
| **Workflow** | **Very Low** | Leave chain only, hand-rolled | No engine, no designer, no escalation | **Full Platform Build** |
| **Reports/BI** | Low | Statements, dashboards | No report builder, no self-service | **Partial Rebuild** |
| **AI** | Very Low | 4 endpoints (anomaly, cashflow, inventory) | Authentication-only; no context framework | **Refactor then Extend** |
| **Documents** | Low | FileManager + HR docs + project files, **three stores** | No unified permission model | **Partial Rebuild** |
| **Assets** | Low | Fixed assets + depreciation | No maintenance, no lifecycle | **Extend** |
| **Maintenance / Quality / Logistics / Governance / Portals** | **None** | — | Absent | **Full Platform Build**, later stages |

**No module is rated "Preserve"** — every one has at least a company-source or capability gap. Equally, **none is
rated "Full Platform Rebuild"**: the five "Full Platform Build" entries are capabilities that do not exist yet, which
is a different thing from replacing working code.

---

## 5. Sequencing recommendation — two changes to the proposed order

The candidate sequence is sound with **two corrections**:

1. **Stage 2A must be narrowed** to the authorization analyzer + CI evidence enforcement (evidence in §2.1).
   The remaining 18 analyzers become a backlog fed by real defects, not a build-out.
2. **Master Data (2C) must not slip behind HR (Stage 3).** WMS and Manufacturing both consume the item model, and HR
   does not. If Master Data lands after HR, nothing breaks; if it lands after WMS, the item-model redesign invalidates
   bin, packaging and picking work. Keeping it at 2C is correct and the dependency should be stated explicitly rather
   than left implicit in the numbering.

One addition: **R1 (role-table consolidation)** should be scheduled in 2A/2B, before Batch D2's console, because a
security console that shows half the grants is worse than none.

Full detail, prerequisites, gates and maturity expectations: `Stage-002-Dependency-Roadmap.md`.

---

## 6. What Phase 0 did NOT do

No production code. No schema change. No SQL against any database. No Stage 1 document altered. No implementation of
any analyzer, framework or screen. Requirement-by-requirement status: `Stage-002-Requirement-Status.md`.
