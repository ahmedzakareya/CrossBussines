# IMP-002 — Bootstrap-Open Governance — Final Delivery Report

**IMP-002 = Completed** (design). **IMP-003 = Completed. IMP-001 = Completed. IMP-004 = Not Started.
Stage 2A = Not Started.**

No production behaviour changed · no permission decision changed · no schema migration executed · no SQL executed
against `CrossBuyDB2`.

Authoritative design: `Stage-002-Bootstrap-Open-Governance-Design.md`.
Evidence: `docs/architecture/evidence/Stage-002-Bootstrap-Open-Inventory.csv` (49 actions, generated, deterministic).

---

## 1. Executive summary

IMP-002 set out to make bootstrap-open explicit and auditable. Tracing the production decision path found **two
structurally different mechanisms**, and the three modules with the highest financial impact use the one that
**cannot exclude any action**. Fourteen actions that must never be bootstrap-open — including journal posting,
payments, currency override and stock documents — are **open today** on an unconfigured company. Six risks were
raised, one Critical. Nothing was changed; hardening is sequenced behind IMP-001's Grant Writer.

## 2. Original objective

Design complete, explicit and auditable governance for bootstrap-open authorization so that a role-based allow, an
ownership/membership allow, a bootstrap-open allow, a system-policy allow and a denial become distinguishable — without
removing bootstrap-open or changing any production decision.

## 3. The finding that changed the scope

Bootstrap-open is not one mechanism with one policy. It is **two mechanisms with different capabilities**, and the
capability gap falls on exactly the wrong side: **the modules that can exclude sensitive actions are the lower-impact
ones, and the modules handling money and stock cannot exclude anything.**

## 4. Mechanism A — legacy early return

**Modules:** `AccountingAccessService` (`:60`) · `InventoryAccessService` (`:48` and `:91`) · `CrmAccessService`
(`:59`).

**Behaviour:** `if (!await AnyRoleConfiguredAsync(companyId)) return true;` placed **before** the action switch.

**Cannot exclude actions** — the switch is never reached, so every action is open. `Inventory` has a *second* early
return in `CanUseWarehouseAsync` (`:91`), so **every warehouse becomes usable** and the `WarehouseKeeper` branch
restriction is not applied. In CRM, `VisibleOwnerIdsAsync` returns `null` (unrestricted), so owner/team scoping and
hierarchy are not consulted.

**No logging.** Mechanism A emits nothing. So the highest-impact bootstrap decisions in the platform are also the
invisible ones (RISK-041).

## 5. Mechanism B — `ModuleAccessServiceBase`

Base computes `bootstrapOpen = grants.Count == 0 && !AnyConfiguredAsync(company, scope)` (`:106-107`), **logs it at
Information** (`:112`), then passes it to the module's `EvaluateAsync` — so the module *can* exclude.

**Modules:** HR · Projects · Tasks · Communication. Three of the four use the discretion: HR refuses `payroll-manage`
and `confidential-view` (`HrAccessService.cs:115`); Tasks refuses `manage` (`:125`); Communication refuses
`outbox-manage`. **This is the pattern Mechanism A must adopt.**

## 6. The fourteen Never-Bootstrap-Open actions — all open today

`Accounting.post` · `Accounting.pay` · `Accounting.manage` · `Accounting.currency-override` · `Inventory.doc` ·
`Inventory.purchase` · `Inventory.manage` · `Inventory.warehouse-access` · `CRM.manage` · `HR.leave-manage` ·
`Projects.budget-manage` · `Projects.manage` · `Projects.billing` · `Platform.platform-operations`.

Classification of all 49 inventoried actions: **14 Never · 16 Legacy-Requiring-Migration · 12 Explicitly-Allowed ·
7 Not-Bootstrap-Open** (the four existing exclusions plus POS, membership-based group management, and system policy).

## 7. Financial and inventory exposure

On an unconfigured company: journal entries post to the GL · payments and receipts record · **FX rate override**
applies to a document · stock receipts, issues and transfers execute · purchasing documents create · **every warehouse
is usable with branch scope unenforced** · costing method and item master are editable. Stock valuation and COGS follow
stock documents, so the exposure reaches the ledger indirectly as well as directly.

## 8. CRM role-administration exposure

`CRM.manage` is open, and `CRM.manage` is the right that **assigns CRM roles**. So an unconfigured company allows any
employee to grant CRM roles — bootstrap-open becomes a path to *ending* bootstrap-open on someone else's terms. The
same is true of `Accounting.manage` and `Inventory.manage`.

## 9. Project billing delegation exposure

`Projects.billing` requires accounting `post`, which is open under Mechanism A — so project progress billing posts to
the GL on an unconfigured company. **Closing `Accounting.post` closes this too**; one fix covers both.

## 10. PlatformOps exposure

`PlatformOpsAttribute` authorizes on an Identity admin role **or** accounting `manage`. Because accounting `manage` is
open under Mechanism A, **platform operational surfaces are reachable on an unconfigured company** — cross-module audit
payloads, event monitor, retry. The attribute documents the limitation in its own comment; IMP-002 measured it.
**RISK-042.**

## 11. POS finding

**POS has zero bootstrap-open behaviour.** `PosAccessService` contains no bootstrap reference;
`ResolveByUserIdAsync` returns null when no `BranchUserRole` exists, so **no POS role means no access**. POS is
**fail-closed** and is the strongest module in this respect.

**POS lane and session guards are explicitly NOT bootstrap-open** and are not classified as such — `PosLaneActivityGuard`
checks a branch's lane activity and no role, which makes it neither authorization nor a bootstrap mechanism.

## 12. Policy classification model

Six categories: **Never Bootstrap Open** · Installation-Only · Temporary · Explicitly Allowed · Legacy Behavior
Requiring Migration · Not Bootstrap Open. Every one of the 49 actions carries exactly one, in the inventory CSV.

## 13. Bootstrap state model

Per **(Company, Scope)**, with optional **ActionGroup** granularity — needed only where read must stay open while
writes close, which is precisely the granularity Mechanism A cannot express.

States: `Disabled` · `Installation` · `Temporary` · `ExplicitlyAllowed` · `LegacyCompatibility` · `ReviewRequired`.
Each carries reason, enabled-by/at, expiry, review date, acknowledgement, source and warning severity.
Detail: design §4.

**Missing configuration resolves to `LegacyCompatibility`, not `Disabled`** — failing closed would change production
behaviour the moment the table deploys, which this delivery forbids. Hardening is a migration step, not a default flip.

## 14. `BootstrapAccessPolicies` storage design

Separate policy table plus an **append-only `BootstrapAccessPolicyHistory`**, so the audit trail cannot be rewritten by
editing current state. Filtered unique index on `(CompanyID, Scope, ActionGroup) WHERE State <> 'Disabled'` (needs
`sqlcmd -I`). All SQL additive, idempotent, rollback-documented. **Nothing executed.** Detail: design §5.

## 15. Why policy must not live in `PlatformRoleAssignments`

Bootstrap-open is **policy, not a grant**. IMP-001 established that mixing concerns in one table is what produced the
split-source problem it had to solve. Worse, a policy row inside the grant table would be **grantable** — violating
Grant Writer rule 18, which forbids enabling bootstrap-open through a role assignment.

## 16. Administration authorization

Five roles; **enable/extend is Platform Security Administrator only**. All ten mandated rules hold, including: a module
admin cannot enable Platform scope · a company admin cannot affect another company · **no one** may enable a `Never`
action · **no one** may use bootstrap-open to obtain role-management rights (`Accounting.manage`, `CRM.manage`,
`Inventory.manage` are all in the 14) · reason mandatory · **bootstrap-policy management is itself never
bootstrap-open**, which requires `PlatformOps` to stop inheriting accounting `manage` (RISK-042). Detail: design §6.

## 17. Security Console contract

The Bootstrap-Open Exposure view shows company · scope · action group · state · effective behaviour · reason ·
enabled-by/at · expiry · review date · **affected endpoint count** · **affected High/Critical endpoints** · role
configuration status · IMP-001 migration state · divergence count · last audit event · warnings · recommended action.
It is view 4 of the seven in `Stage-002-Security-Console-Data-Contract.md` (consolidated into
`Stage-002-Role-Source-Consolidation-Design.md` §6). Detail: design §7.

## 18. Additive permission decision result

**The current limitation:** access services return `bool`. A boolean cannot say *why*, so bootstrap-open is
indistinguishable from a role grant at every call site and in every log.

**The future contract:** a source-aware result carrying `AllowedByDirectGrant` · `AllowedByMembership` ·
`AllowedByOwnership` · `AllowedByHierarchy` · **`AllowedByBootstrapOpen`** · `AllowedBySystemPolicy` · `Denied` (with
reason).

**Compatibility:** delivered as a **new method alongside `CanAsync`**, not a change to it — every existing caller is
untouched, and callers adopt the richer result only where they need the reason (console, audit, diagnostics). This is
the additive pattern `NotifyAsync`'s optional trailing parameters already established.

## 19. Logging, audit and BusinessEvents

Ten events: `BootstrapPolicyCreated` · `Enabled` · `Disabled` · `Extended` · `Expired` · `Acknowledged` ·
`ReviewRequired` · `BootstrapOpenDecisionUsed` · `BootstrapOpenMaskedDivergenceDetected` ·
`BootstrapPolicyViolationAttempted`.

**`BootstrapOpenDecisionUsed` is sampled and aggregated, not per-decision** — an unconfigured company would otherwise
emit one event per authorization check. It records that the state was used within a window, with a count.
**No restricted payload content is logged** — an `outbox-manage` denial records the attempt, never the email body.
Detail: design §8.

## 20. Warning and notification model

Persistent console banner · dashboard warning for Critical/High exposure · notify on enable, before expiry, on
extension, when roles become configured while bootstrap remains on, when a masked divergence appears, and when a
`Never` action is attempted. **Grouped by (company, scope, policy, time window)** — alert fatigue is itself a risk
(RISK-045), and a notification per `Accounting.read` decision would bury the real signal.

## 21. First-run and installation behaviour

Create the platform administrator → assign a company administrator → `Installation` state covering **only**
`Explicitly Allowed` actions → first-login wizard walks the module grants → **short mandatory expiry** →
acknowledgement → transition to `Disabled`. Emergency recovery is a documented, audited platform-admin path.
**No hard-coded `CompanyID = 1`** — the company comes from the created company (RISK-003 discipline). This avoids both
locking out the first administrator and opening a module indefinitely.

## 22. Environment hardening

**Development** — warnings loud; convenience permitted **except** for the 14 `Never` actions.
**Test** — bootstrap must be **explicitly selected**; every authorization test declares whether it exercises bootstrap
behaviour, so no test passes accidentally because no roles exist (the Wave 1 guard, generalised).
**Staging** — production-like; temporary exceptions only; mandatory expiry.
**Production** — `Disabled` by default for **new** companies after setup; no `Never` action ever open; all temporary
states expire; persistent audit; emergency use needs elevated approval.
**Existing companies are unchanged by this delivery.**

## 23. IMP-001 interlock

All eight mandated rules hold, and the dependency is **bidirectional**:

* **IMP-001 gates IMP-002's hardening** — HR and Projects cannot be set to `Disabled` before the Grant Writer exists,
  because hardening a module whose grant store cannot be populated would lock everyone out (RISK-043).
* **IMP-002 gates IMP-001's cutover** — `BootstrapOpenMaskedDifference` blocks cutover, because a company in
  bootstrap-open cannot prove its role migration is correct: both decisions allow for the same reason (RISK-044).

## 24. Migration sequence

```
Grant Writer (IMP-001)
    ↓
Explicit behaviour-preserving bootstrap policies   ← changes nothing
    ↓
Close the 14 Never Bootstrap Open actions          ← first behavioural change
    ↓
Company-by-company hardening
```

Eleven phases in design §12: inventory → schema → **seed policies matching today exactly** → audit-only → warnings →
shadow comparison → close `Never` actions → company review → scope hardening → production default → remove implicit
behaviour (later, separately approved). Additive first · idempotent · auditable · reversible before implicit-behaviour
removal · company-scoped · scope-scoped · safe with IMP-001's Legacy/Shadow/Platform states.

## 25. Test strategy

All 22 mandated cases (design §11), including: `Never` action denies with zero roles · installation action permits only
during `Installation` · expired temporary denies · module admin cannot enable Platform scope · policy change records
actor **and reason** · first real grant triggers review · **bootstrap-open allow is distinguishable in the decision
reason** · masked divergence blocks cutover · inactive employee still denies · confidential HR and payroll-manage stay
closed · no partial policy write on failure · concurrent updates do not silently overwrite · **a test cannot pass
because an empty role table defaulted open unless it explicitly declares it is testing bootstrap behaviour**.

SQL Server **disposable, isolated probe** databases (RISK-036) for the filtered unique index, transactions,
concurrency, expiry and audit persistence, carrying `[RequiredEvidence]` so they **cannot report skipped** (RISK-026).

## 26. Risks

| Risk | Severity | Summary |
|---|---|---|
| **RISK-040** | **Critical** | Financial modules cannot exclude dangerous bootstrap actions; 7 named actions exposed |
| **RISK-041** | High | High-impact bootstrap decisions are not audited — Mechanism A logs nothing |
| **RISK-042** | High | Platform operations inherit bootstrap-open accounting `manage` |
| **RISK-043** | High | Bootstrap policy cannot be safely hardened before Grant Writer availability |
| **RISK-044** | Medium | Bootstrap-open masks role-migration divergence |
| **RISK-045** | Medium | Implicit bootstrap compatibility may remain enabled indefinitely |

**Reconciliation note, stated rather than glossed:** the closure brief **re-scoped** RISK-041 and RISK-043 relative to
my IMP-002 design draft, and the brief is authoritative. The draft's "warehouse and CRM scope unenforced while open" is
**folded into RISK-040's evidence** (`Inventory.warehouse-access` and `CRM.manage` are named there); the draft's
"expired policy remaining effective" concern is **carried by RISK-045**. No finding was dropped — both are represented
under the brief's numbering.

**Canonical totals: 45 risks — 8 Critical · 20 High · 14 Medium · 3 Low.** Previously 39 (7/17/12/3).
**Delta: +1 Critical, +3 High, +2 Medium, +6 total — exactly the brief's expected arithmetic.** Totals were not forced.

## 27. Completed artifacts

| Artifact | Note |
|---|---|
| `Stage-002-Bootstrap-Open-Governance-Design.md` | authoritative consolidated design |
| `Stage-002-Bootstrap-Open-Inventory.csv` | **generated** — 49 actions, deterministic |
| `generate-imp002-inventory.py` | the generator |
| Policy matrix · state model · console contract · event/audit contract · test plan · migration/rollback | **consolidated** into design §2/§4/§7/§8/§11/§12 rather than separate files, deliberately, to avoid synchronisation risk (permitted by the brief) |
| `Stage-002-Risk-Register.md` + `.csv` | **both regenerated from one canonical source** |
| `generate-risk-register.py` | the canonical generator |

Referenced, not duplicated: `Stage-002-Role-Source-Consolidation-Design.md` ·
`Stage-002-Platform-Grant-Writer-Design.md` · `Stage-002-Security-Console-Data-Contract.md` (consolidation design §6) ·
`Stage-002-Role-Shadow-Divergence-Contract.md` (Grant Writer design §6).

## 28. Verification state

| Check | Result |
|---|---|
| Application build, **Razor enabled** | **0 errors** |
| Full suite, **SQL evidence enabled** | **766 total · 766 passed · 0 failed · 0 skipped** |
| Risk Register generated twice | **identical hashes** |
| Risk Register Markdown hash | `e1f30a1398bae635990e0357e126f0df` |
| Risk Register CSV hash | `0abecc88c55e8fc5a147c47b348e3a0d` |
| Risk totals | **45 — 8 Critical · 20 High · 14 Medium · 3 Low** |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |

No skipped evidence test is counted as coverage — there are none.

## 29. Known limitations

1. **The 14 exposed actions remain exposed.** IMP-002 measured and classified them; closing them is phase 7 of the
   migration and is not part of this delivery.
2. **RISK-043 means IMP-002 cannot act alone** — HR and Projects hardening waits on IMP-001's Grant Writer.
3. **No per-company measurement.** How many production companies are currently unconfigured is unknown; it needs
   approved read-only access, the same constraint as the variant measurement.
4. **The decision-source contract is designed, not built** — until it exists, bootstrap-open allows remain
   indistinguishable in logs, so RISK-041 is unmitigated in practice.
5. **`Explicitly Allowed` read actions still expose data** — 12 actions include reading ledgers, stock costs and
   employee records on an unconfigured company. Judged acceptable during installation; it is a deliberate acceptance,
   not an absence of exposure.

## 30. Items explicitly not implemented

The `BootstrapAccessPolicies` table and history · any schema change · any policy seed · Mechanism A → B refactor ·
closing any of the 14 actions · the decision-source result contract · logging changes · events · notifications ·
the Security Console · environment hardening · first-run wizard · IMP-004 · Stage 2A analyzers.

## 31. Final requirement status

| Gate | Status |
|---|---|
| 1 RISK-040 merged as Critical | **Completed** |
| 2–6 RISK-041…045 merged | **Completed** |
| 7 Markdown and CSV from one canonical source | **Completed** |
| 8 Deterministic generation | **Completed** — identical hashes twice |
| 9 Exact new severity totals reported | **Completed** — 45 (8/20/14/3), delta +1/+3/+2/+6 |
| 10 Final IMP-002 report created | **Completed** — this document |
| 11 Accepted artifacts referenced | **Completed** (§27) |
| 12 Final verification passes | **Completed** (§28) |
| 13 No permission decision changes | **Completed** |
| 14 No production behaviour changes | **Completed** |
| 15 No schema migration executed | **Completed** |
| 16 No SQL against `CrossBuyDB2` | **Completed** |
| 17 IMP-004 not started | **Confirmed** |
| 18 Stage 2A not started | **Confirmed** |

**IMP-002 = Completed. 20 of 20.**

## 32. Recommendation for IMP-004

**Proceed with IMP-004 (Master Data source consolidation) as the next design** — it is the last outstanding Required-Now
item and it gates Stage 2C.

Two things to carry into it, both from evidence already gathered:

1. **Composite items are load-bearing in the two-writer core.** `ItemComponent` is read by `StockService:319` and
   `:1189` (kit explosion), `PricingService:311`, and `PosOrderService:302`/`:1746`. RISK-033. Any composite redesign
   touches the stock writer, pricing, POS and manufacturing simultaneously.
2. **The nine-invariant stock-valuation gate (RISK-022) is mandatory**, and the variant population is still
   **unmeasured** (RISK-034) — so IMP-004 can design the consolidation but **cannot size the 2C migration** until a
   read-only measurement is approved. That approval request should accompany IMP-004 rather than follow it.

**One sequencing note:** nothing in IMP-002 blocks IMP-004. But **Stage 2A implementation should not begin** until the
Grant Writer is scheduled, because RISK-037 and RISK-040 together mean Wave 2's authorization would evaluate
bootstrap-open on both the HR path and the financial path.

**Stopping for review. IMP-004 not started. Stage 2A not started.**
