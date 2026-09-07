# IMP-002 — Bootstrap-Open Governance — Design

**Design and evidence only. No production behaviour changed. No permission decision changed. No schema executed.
No SQL against `CrossBuyDB2`.** Bootstrap-open is **not removed** by this delivery.

Inventory: `docs/architecture/evidence/Stage-002-Bootstrap-Open-Inventory.csv` — **49 actions**, generated and
deterministic.

---

## 1. THE FINDING — two structurally different mechanisms, and the financial one has no discretion

| | Mechanism | Modules | Per-action discretion? |
|---|---|---|---|
| **A** | **Legacy early return** — `if (!await AnyRoleConfiguredAsync(companyId)) return true;` placed **before** the action switch | **Accounting, Inventory, CRM** | **NONE** |
| **B** | `ModuleAccessServiceBase` computes `bootstrapOpen` and passes it to `EvaluateAsync` | HR, Projects, Tasks, Communication | **Yes — and 3 modules use it** |
| **N** | Neither | POS, `PlatformOps`, `SystemContextPolicy` | n/a |

Call sites: `AccountingAccessService.cs:60` · `InventoryAccessService.cs:48` and `:91` · `CrmAccessService.cs:59` ·
`ModuleAccessServiceBase.cs:106-117`.

**The consequence is the headline of IMP-002.** Mechanism A returns `true` for **every** action, so on an unconfigured
company today:

* **`Accounting.post`** — journal entries to the GL — is **open**;
* **`Accounting.pay`** — payments and receipts — is **open**;
* **`Accounting.currency-override`** — FX rate override on a document — is **open**;
* **`Inventory.doc`** — stock receipts, issues, transfers — is **open**;
* **`Inventory.warehouse-access`** — every warehouse usable, **`WarehouseKeeper` branch restriction not applied**;
* **`CRM.manage`** — which assigns CRM roles — is **open**.

Meanwhile mechanism B's modules **do** exclude their most sensitive actions: `HrAccessService.cs:115` refuses
`payroll-manage` and `confidential-view`; `TasksAccessService.cs:125` refuses `manage`; Communication refuses
`outbox-manage`.

**So the three modules with the highest financial impact are the three with no exclusion capability at all, while the
modules that added exclusions are the ones handling comparatively lower-value actions.** That inversion is the defect
IMP-002 exists to correct — and mechanism B's exclusion pattern is the model to copy.

**Two further findings:**

* **`PlatformOps` inherits Accounting's bootstrap-open.** The attribute allows an Identity admin role **or** accounting
  `manage` — and accounting `manage` is bootstrap-open, so **platform operations are reachable on an unconfigured
  company**. The attribute's own comment declares this limitation; it is now measured. **RISK-042.**
* **Mechanism A is not logged.** Mechanism B logs every bootstrap-open decision at Information
  (`ModuleAccessServiceBase.cs:112`). The legacy early return has **no log line at all** — so the highest-impact
  bootstrap-open decisions are the invisible ones.

---

## 2. Policy classification — all 49 actions

| Classification | Count | Meaning |
|---|---|---|
| **NEVER BOOTSTRAP OPEN** | **14** | must deny with zero roles; **all 14 are open today** except where noted |
| Legacy Behavior Requiring Migration | 16 | open today, needs an explicit policy then hardening |
| Explicitly Allowed Bootstrap Open | 12 | read/visibility actions; safe to remain open during installation |
| Not Bootstrap Open | 7 | already correct — the 4 exclusions plus POS and system policy |

**The 14 Never-Bootstrap-Open actions, open today:**
`Accounting.post` · `Accounting.pay` · `Accounting.manage` · `Accounting.currency-override` · `Inventory.doc` ·
`Inventory.purchase` · `Inventory.manage` · `Inventory.warehouse-access` · `CRM.manage` · `HR.leave-manage` ·
`Projects.budget-manage` · `Projects.manage` · `Projects.billing` · `Platform.platform-operations`.

`Projects.billing` is open **only because** it delegates to accounting `post` — closing Accounting closes it too, so
one fix covers two.

**Correctly closed already (the model):** `HR.payroll-manage` · `HR.confidential-view` · `Tasks.manage` ·
`Communication.outbox-manage` · POS (all actions — no `BranchUserRole` means **no access**, never open) ·
`Communication.manage-group` (membership-based) · system-context policy.

**POS lane guards are explicitly NOT classified as bootstrap-open**, per the brief. `PosAccessService` has **zero**
bootstrap references; `ResolveByUserIdAsync` returns null without a role row. POS is the strongest module here.

---

## 3. Current module behaviour — the specifics that matter

**Accounting** (A): per company; **no action differs**; interacts with `AccountingUserRoles`; the financial APIs
(`AccountingApiController`, 10 mutating actions authorized in-body by Hotfix A.1) call the same service, so they are
open too on an unconfigured company.

**Inventory** (A): `read`/`doc`/`purchase`/`manage` all open; **`ScopeBranchId` is never consulted** because the switch
is not reached; `CanUseWarehouseAsync` has its **own** early return at `:91`, so every warehouse is usable.

**CRM** (A): `read`/`edit`/`manage` open; **`VisibleOwnerIdsAsync` returns `null` (unrestricted)** when unconfigured, so
owner/team scoping does not apply and hierarchy is not consulted.

**HR** (B): reads `PlatformRoleAssignments`, which **has no production writer (RISK-037)** — so HR is bootstrap-open in
production and **cannot leave that state today**. `payroll-manage` and `confidential-view` closed; **`leave-manage`
open**, which disburses cash and posts provisions (RISK-006). **Wave 2 exposure: all 52 HR/Identity endpoints would gate
on a service that currently answers bootstrap-open.**

**Projects** (B): same missing-writer exposure; `billing` delegates to accounting; `budget-manage` is the **only**
accounting check for GL-reaching project actions, and it is open.

**Tasks** (B): the **narrowest** bootstrap-open in the platform — module-level open, but `:177` still requires a
**record relationship** for a named task, so exposure is bounded. `manage` excluded.

**Communication** (B): module-level actions open, but **membership rules are independent of bootstrap-open** —
`Read => isMember` holds even when open, so a conversation is never exposed by bootstrap-open. SignalR inherits that
(Batch C.1). `outbox-manage` excluded.

**POS** (N): no bootstrap path.

---

## 4. State model

Per **(Company, Scope)**, with optional **ActionGroup** granularity — required only where a module needs to keep read
open while closing writes (Accounting, Inventory, CRM), which is exactly the granularity mechanism A cannot express.

| State | Authorization behaviour | Who sets | Expiry |
|---|---|---|---|
| **Disabled** | bootstrap-open **never** applies; zero roles ⇒ deny | Platform Security Admin | n/a |
| **Installation** | open for `Explicitly Allowed` actions only, from first company creation | set automatically at company creation | **mandatory**, short, non-extendable more than once |
| **Temporary** | open for the named action group | Platform Security Admin, **reason required** | **mandatory**; expiry cannot be silently extended |
| **ExplicitlyAllowed** | open, deliberately, with acknowledgement | Platform Security Admin + acknowledgement | review date required |
| **LegacyCompatibility** | today's behaviour, preserved | seeded by migration | **review date required**; the state migration exists to drain |
| **ReviewRequired** | behaves as its previous state **but warns**, entered automatically when the first real grant appears | system | must be resolved |

Every state carries: reason · `EnabledAt`/`EnabledBy` · `ExpiresAt` · `ReviewedAt`/`ReviewedBy` ·
`AcknowledgedAt`/`AcknowledgedBy` · source · company · scope · affected actions · warning severity.

**Missing configuration fails to `LegacyCompatibility`, not to `Disabled`** — deliberately. Failing to `Disabled`
would change production behaviour the moment the table is deployed, which this delivery forbids. Hardening is a
*migration step*, not a default flip.

**A `NEVER BOOTSTRAP OPEN` action is not settable to any open state** — enforced in the writer and by a test.

---

## 5. Storage

**A separate `BootstrapAccessPolicies` table, not metadata on `PlatformRoleAssignments`** — bootstrap-open is
**policy, not a grant**, and IMP-001 already proved that mixing concerns in one table is what produced the current
split-source problem. Putting a policy row in the grant table would also make it grantable, violating rule 18 of the
Grant Writer.

Columns: `Id` · `CompanyID` · `Scope` · `ActionGroup` (nullable = whole scope) · `State` · `Reason` · `EnabledAt` ·
`EnabledBy` · `ExpiresAt` · `ReviewedAt` · `ReviewedBy` · `AcknowledgedAt` · `AcknowledgedBy` · `CreatedAt` ·
`UpdatedAt` · `SourceSystem` · `MigrationBatchId`.

Plus **`BootstrapAccessPolicyHistory`** — append-only, never updated, one row per transition, so the audit trail cannot
be rewritten by editing the current state.

**Filtered unique index** on `(CompanyID, Scope, ActionGroup) WHERE State <> 'Disabled'` — one effective policy per
target. Needs `sqlcmd -I`. All SQL **additive, idempotent, rollback-documented**; nothing executed here.

---

## 6. Administration authorization

| Capability | Required right |
|---|---|
| View bootstrap exposure | `PlatformOps` or Auditor |
| Enable/extend a policy | **Platform Security Administrator only** |
| Acknowledge | Platform Security Admin or Company Admin (own company) |
| Set `Disabled` (harden) | Platform Security Admin or Company Admin (own company) |
| Emergency activation | Platform Security Admin **plus** a second approver, fully audited |

All ten mandated rules hold: a module admin **cannot** enable Platform scope · a company admin **cannot** affect
another company · **no one** may enable a `Never Bootstrap Open` action · **no one** may use bootstrap-open to obtain
role-management rights (`Accounting.manage`, `CRM.manage`, `Inventory.manage` are all in the 14) · reason mandatory ·
expired policies cannot continue · emergency needs elevated approval + audit · **bootstrap-policy management is itself
never bootstrap-open** (it requires `PlatformOps`, which must therefore stop inheriting accounting `manage` —
RISK-042) · cross-company needs platform authority · every change emits an event.

---

## 7. Security Console — Bootstrap-Open Exposure view

Shows: company · scope · action group · state · effective behaviour · reason · enabled by/at · expiry · review date ·
**affected endpoint count** · **affected High/Critical endpoints** · role configuration status · IMP-001 migration
state · divergence count · last audit event · warnings · recommended action.

**Decision-source vocabulary** — the console must distinguish all seven, and this is what makes bootstrap-open visible
rather than inferred:

`AllowedByDirectGrant` · `AllowedByMembership` · `AllowedByOwnership` · `AllowedByHierarchy` ·
**`AllowedByBootstrapOpen`** · `AllowedBySystemPolicy` · `Denied` (with reason: expired · revoked · company mismatch ·
branch mismatch · confidential policy · no grant).

**Requires production plumbing:** the access services return a boolean today. Surfacing the *reason* needs a decision
result carrying its source — an **additive** change (a new method alongside `CanAsync`, existing callers untouched).
Designed here, **not implemented**.

---

## 8. Warnings, events and audit

**Warnings:** persistent console banner · dashboard warning for Critical/High exposure · notify on enable, before
expiry, on extension, when roles become configured while bootstrap remains on, when a masked divergence appears, and
when a `Never` action is attempted. **Grouped by (company, scope, policy, time window)** — alert fatigue is itself a
risk (RISK-045), and a per-decision notification on `Accounting.read` would bury the real signal.

**Events:** `BootstrapPolicyCreated` · `Enabled` · `Disabled` · `Extended` · `Expired` · `Acknowledged` ·
`ReviewRequired` · `BootstrapOpenDecisionUsed` · `BootstrapOpenMaskedDivergenceDetected` ·
`BootstrapPolicyViolationAttempted`. Each carries company, scope, action, actor, policy state, reason, timestamp,
sensitivity, retention.

**`BootstrapOpenDecisionUsed` is sampled/aggregated, not per-decision** — an unconfigured company would otherwise emit
one event per authorization check. It records *that* the state was used within a window, with a count.

**No restricted payload content is logged** — an `outbox-manage` denial records the attempt, never the email body.

---

## 9. Interlock with IMP-001

All eight mandated rules, and the interlock runs **both** ways:

1. Shadow mode **cannot** prove migration correctness while bootstrap-open masks both decisions — both answers are
   "allow" for the same reason.
2. `BootstrapOpenMaskedDifference` is a **blocking** divergence.
3. Platform cutover is **blocked** for any (company, scope) with unresolved masked divergence.
4. Grant Writer rollout does **not** auto-disable bootstrap-open — that would change behaviour silently.
5. The **first real grant** flips the policy to `ReviewRequired`.
6. Migration must distinguish allowed-by-legacy-grant / allowed-by-platform-grant / **allowed-only-by-bootstrap-open**.
7. The console shows migration and bootstrap state **together**.
8. **Wave 2 HR rollout stays blocked** until: Grant Writer exists · the HR policy is explicit · `Never` actions are
   closed · evidence tests prove production configuration.

**The reverse dependency, stated plainly:** IMP-002 cannot harden HR or Projects to `Disabled` until IMP-001's Grant
Writer exists, because hardening a module whose grant store cannot be populated would **lock everyone out**. So
**IMP-001 gates IMP-002's hardening, and IMP-002 gates IMP-001's cutover.** Sequence: Grant Writer → explicit policies
(behaviour-preserving) → close `Never` actions → per-company hardening.

---

## 10. Environment hardening and first run

**Development:** warnings loud; convenience permitted **except** for the 14 `Never` actions.
**Test:** bootstrap must be **explicitly selected**; a test may not pass accidentally because no roles exist — every
authorization test declares whether it exercises bootstrap behaviour. This is the Wave 1 guard generalised.
**Staging:** production-like; temporary exceptions only; mandatory expiry.
**Production:** `Disabled` by default for **new** companies after setup; no `Never` action ever open; all temporary
states expire; persistent audit; emergency use needs elevated approval. **Existing companies are unchanged by this
delivery.**

**First run** must avoid both locking out the first administrator and opening a module indefinitely: create the
platform administrator → assign a company administrator → `Installation` state covering only
`Explicitly Allowed` actions → first-login wizard walks the module grants → **short mandatory expiry** →
acknowledgement → transition to `Disabled`. Emergency recovery is a documented, audited platform-admin path.
**No hard-coded `CompanyID = 1`** — the company comes from the created company (RISK-003 discipline).

---

## 11. Test strategy

All 22 mandated cases, including: `Never` action denies with zero roles · installation action permits **only** during
`Installation` · expired temporary denies · unauthorized admin cannot enable · module admin cannot enable Platform
scope · policy change records actor **and reason** · event emitted · first real grant triggers review ·
**bootstrap-open allow is distinguishable in the decision reason** · console effective source correct · masked
divergence blocks cutover · inactive employee **still** denies · confidential HR **stays** closed · payroll-manage
**stays** closed · role administration **stays** closed · no partial policy write on failure · concurrent updates do
not silently overwrite · **a test cannot pass because an empty role table defaulted open unless it explicitly declares
it is testing bootstrap behaviour**.

SQL Server **disposable, isolated probe** databases (RISK-036) for the filtered unique index, transactions,
concurrency, expiry and audit persistence — carrying `[RequiredEvidence]` so they **cannot report skipped** (RISK-026).

---

## 12. Migration and rollback

Eleven phases: inventory → policy schema → **seed explicit policies matching today exactly** → audit-only → warnings →
shadow comparison with grants → **close the 14 `Never` actions** → company-by-company review → scope-by-scope
hardening → production default hardening → remove implicit behaviour (later, approved separately).

**Additive first · idempotent · auditable · reversible before implicit-behaviour removal · company-scoped ·
scope-scoped · safe with IMP-001's Legacy/Shadow/Platform states.**

**Phase 3 changes nothing** — it records current behaviour. The first behavioural change is phase 7, and it is
deliberately the smallest high-value one: closing 14 named actions.

**Rollback:** set the policy back to `LegacyCompatibility`. Because seeding is additive and no service code changes
until phase 7, rollback before that point is a data change only; after phase 7 it is a policy-state change.

---

## 13. New risks

| Risk | Sev | Summary |
|---|---|---|
| **RISK-040** | **Critical** | Financial posting, payments, currency override, stock documents and warehouse access are **bootstrap-open today** because mechanism A has no per-action discretion |
| **RISK-041** | High | `WarehouseKeeper` branch scope and CRM owner scope are **unenforced** while bootstrap-open — `CanUseWarehouseAsync` returns true for every warehouse; `VisibleOwnerIdsAsync` returns unrestricted |
| **RISK-042** | High | `PlatformOps` inherits accounting `manage`, so **platform operations are reachable on an unconfigured company** |
| **RISK-043** | High | An expired bootstrap policy remaining effective, or being extended without authority |
| **RISK-044** | Medium | An `Installation` policy that never closes — first-run state becomes permanent |
| **RISK-045** | Medium | Alert fatigue hiding real exposure; and environment configuration drift between dev/test/staging/production |

**Mechanism A being unlogged** is folded into RISK-040: the highest-impact bootstrap decisions are also the invisible
ones.

---

## 14. Delivery status

| Output | Status |
|---|---|
| `Stage-002-Bootstrap-Open-Governance-Design.md` | **Completed** (this document — consolidated, per the brief's allowance) |
| `Stage-002-Bootstrap-Open-Inventory.csv` | **Completed** — generated, 49 actions, deterministic |
| Policy matrix · state model · console contract · event/audit contract · test plan · migration/rollback | **Completed in §2 / §4 / §7 / §8 / §11 / §12** — consolidated deliberately to avoid synchronisation risk |
| Risk Register merge (RISK-040…045) | **Not Started** — identified here, not yet in the canonical generator |
| `Stage-002-IMP-002-Delivery-Report.md` | **Not Started** — superseded by this section |

**Gate: 18 of 20 met.** Outstanding: the canonical risk merge and the standalone delivery report.

**No production behaviour changed · no permission decision changed · no schema executed · no SQL against
`CrossBuyDB2` · IMP-004 not started · Stage 2A not started.**
