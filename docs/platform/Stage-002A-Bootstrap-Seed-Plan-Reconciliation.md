# CrossBusiness Platform — Stage 2A Batch B — Bootstrap Seed Plan Reconciliation

**STOPPED before implementing B3. The plan derives to 32 compatibility entries, not 41, and no reading of the live
source reaches 41.**

This is the same situation as the Never-Bootstrap-Open count (14 → 15), which was resolved by decision rather than by
forcing a total. The seed writes policy rows into **every company it is run against**, so seeding the wrong set is not
a reversible detail — it is data in production tenants.

Entry check passed on all ten points before this derivation was attempted (§4).

---

## 1. The derivation, from live source

Every action vocabulary read from the module's own `Actions` / `*Actions` declaration. Never entries removed using
`NeverBootstrapOpen.All` — the authoritative list, not a copy.

| Scope | Actions | Never | **Compatibility entries** | Entries |
|---|---|---|---|---|
Accounting | 5 | 4 | **1** | `read` |
Inventory | 4 | 3 | **1** | `read` |
CRM | 3 | 1 | **2** | `edit`, `read` |
HR | 11 | 2 | **9** | `attendance-manage`, `employee-manage`, `employee-view`, `leave-approve`, `leave-manage`, `organization-manage`, `payroll-view`, `performance-manage`, `read` |
Projects | 8 | 1 | **7** | `budget-manage`, `budget-view`, `close`, `create`, `edit`, `manage`, `read` |
Tasks | 8 | 1 | **7** | `assign`, `complete`, `create`, `edit`, `read`, `reassign`, `reopen` |
Communication | 6 | 1 | **5** | `announcement-send`, `create-group`, `manage-group`, `read`, `send` |
POS | — | — | **0** | refused by CHECK constraint and by the reader |
Platform | — | 1 (`PlatformOps`) | **0** | no action vocabulary |
| **Total** | **45** | **14 in-scope + PlatformOps = 15** | **32** | |

`Inventory.warehouse-access` is the fourth Inventory Never entry and belongs to the separate warehouse-scope site
(`InventoryAccessService:91`), not to the module action list — which is why Inventory shows 4 actions but 4 Never
entries across two sites.

## 2. Every reading attempted, and what each yields

| Reading | Total |
|---|---|
Compatibility entries only — what the seed would **write** | **32** |
…plus Manufacturing's adapter actions (`doc`, `manage`, `read`) | 35 |
…plus the 14 in-scope Never entries recorded as explicit `Disabled` rows | 46 |
Compatibility + Never, in-scope, without Manufacturing | 46 |
All 45 actions across the seven scopes | 45 |

**None is 41.** The two candidate contributors to a difference are examined below.

### 2.1 Manufacturing — 3 actions, and it has no access service of its own

`ManufacturingPermissionAdapter` exists with `read`, `doc`, `manage`, but there is **no `IModuleAccessService` whose
`Scope` is `Manufacturing`** — the adapter delegates to Inventory's roles. So Manufacturing has no independent
bootstrap behaviour to preserve, and seeding policies for it would create a policy surface for a module that does not
consult one. Adding it gives 35, not 41, so it does not explain the gap either way.

### 2.2 The four Mechanism B modules may not need seeding at all

HR, Projects, Tasks and Communication contribute **28 of the 32** entries. They already run through
`ModuleAccessServiceBase`, which is action-aware and already excludes their dangerous actions. B6 only converts
Accounting, Inventory and CRM.

That raises a real question I should not answer alone: **is the seed meant to cover only the B6 targets?**
Accounting + Inventory + CRM = **4 entries** (`Accounting.read`, `Inventory.read`, `Crm.read`, `Crm.edit`). Seeding the
other 28 would write policy rows that nothing will consult in this batch or in B6 — and per B4's design, a company
with no policy row is **denied**, so seeding them is harmless today but creates 28 rows per company whose only purpose
is a module that already governs itself.

Neither 4 nor 32 is 41.

## 3. The decision I need

1. **Which set is the accepted 41?** If the frozen matrix's 41 came from a scope list or action vocabulary that has
   since changed, the two numbers are not comparable and the live derivation (32) should supersede it — but that is
   your call, not mine.
2. **Should the seed cover only the B6 targets (4 entries), or every bootstrap-eligible action (32)?** The Mechanism B
   modules do not need compatibility policy for B6 to proceed safely.
3. **Manufacturing — in or out?** My reading: out, because it has no access service and no bootstrap behaviour of its
   own. 3 entries either way.

## 4. Entry check — all ten points passed

| # | Check | Result |
|---|---|---|
1–2 | Phase-1 and Phase-2 preservation artifacts | present; `stage2a-verified.tar.gz` `2e0cfd55…`, `stage2a-phase2.tar.gz` `aa1ce5f1…` |
3 | Four B2/B4 delivered files | all present |
4–6 | B3 source · CRM typed contract · compatibility matrix | **all absent**, as recorded |
7 | Mechanism A sites | **5 unchanged** — Accounting 1, Inventory 2, CRM 1, plus `CrmAccessService.VisibleOwnerIdsAsync` |
8 | Owned probe databases | **0** |
9 | `CrossBuyDB2` | present, untouched |
10 | Concurrent parallel-team changes | none affecting Batch B |

## 5. State — unchanged by this increment

Application tests **893 / 893 · 0 failed · 0 skipped** · manifest **151 enforced** · CBA001/004/006 **0 / 0 / 0** ·
debt **143** · endpoints **391 = 157 + 91 + 143** · production authorization behaviour **unchanged** · B2 and B4
untouched · B6 · Batch C · Wave 2 · Security Console · Master Data all **Not Started**.

Nothing was written to disk for B3. No policy row was created in any database.
