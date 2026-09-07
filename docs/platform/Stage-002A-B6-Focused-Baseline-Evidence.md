# CrossBusiness Platform — Stage 2A B6 — Focused Baseline Evidence

**32 / 32 · 0 failed · 0 skipped**, established twice on fresh builds — before and after the mutation cycle.

Every run below used a **verified fresh build** (`0 Error(s)` checked explicitly). `--no-build` appears only after such
a build, never as the build itself. That rule exists because it was broken once and produced a false result — see §5.

---

## 1. The three groups

| Group | Site converted | Tests | Result |
|---|---|---|---|
**Accounting** | `AccountingAccessService.cs:60` | 14 | **14 / 14** |
**Inventory module** | `InventoryAccessService.cs:48` | 8 | **8 / 8** |
**Warehouse scope** | `InventoryAccessService.cs:91` | 10 | **10 / 10** |
**Combined** | — | **32** | **32 / 32 · 0 skipped** |

Assembly built 14:38:36; groups run individually then together, in the mandated order — a group had to be green
before the next was run.

## 2. What the Accounting group proves (14)

* `Accounting.read` is **preserved** through the seeded compatibility policy — the behaviour-preservation core.
* The decision names `BootstrapLegacyCompatibility` and carries the **policy id**, so a compatibility allow is never
  mistaken for role authorization.
* All four Never actions (`post`, `pay`, `manage`, `currency-override`) **deny** with no role configured, *even with a
  permitting read policy present* — proving the Never check is per-action, not per-module.
* A **hand-inserted** `ExplicitlyAllowed` policy for `post` still denies.
* `read` denies when the policy is **missing**, **expired** or **disabled** — three separate assertions.
* Configured roles behave **exactly** as before: `ChiefAccountant` full, `Cashier` keeps `pay` and is refused
  `post`/`manage`/`currency-override`, `Auditor` read-only. Each reports `DecisionSource = LegacyRole`.
* Company mismatch and unresolved context deny, with **no fallback to company 1**.
* An unknown action denies and never falls through to `read`.
* **PlatformOps** is independently denied — `Accounting.manage` is Never, so `PlatformOpsAttribute`'s accounting
  fallback cannot hand out platform operations (RISK-042).

## 3. What the Inventory module group proves (8)

* `Inventory.read` preserved through its seeded policy, with the source and policy id named.
* `doc`, `purchase` and `manage` deny with no role.
* **`purchase` remains ONE closed action** — the test asserts the Never entry's reason still contains
  `VOCABULARY DEFECT`, so the accepted decision (close it whole rather than split the permission code) cannot be
  quietly reversed.
* Configured roles unchanged: `InventoryManager` full; `PurchasingOfficer` keeps `purchase`, refused `doc`/`manage`.
* `read` denies on a missing or expired policy.

## 4. What the warehouse group proves (10)

This conversion **narrows** behaviour and is approved as such.

* No configured role ⇒ **warehouse access denied**. Previously an unconfigured company received *every* warehouse.
* **No policy state can open it** — a theory over `LegacyCompatibility`, `Installation`, `ExplicitlyAllowed` and
  `ReviewRequired`, each inserted with **raw SQL** so the row genuinely claims to permit a Never action.
* **Role path 1 preserved**: an `InventoryManager` reaches any warehouse.
* **Role path 2 preserved**: an *unscoped* `WarehouseKeeper` reaches any warehouse.
* **Branch exactness preserved**: a branch-scoped keeper reaches its own branch's warehouse and **not** another's.
* Cross-company: a keeper cannot reach a warehouse in another company, even unscoped.
* **CRM asserted unchanged** — `CrmAccessService`'s constructor must not contain `IBootstrapAccessPolicyReader`, and
  `VisibleOwnerIdsAsync` must still return `Task<HashSet<int>?>`. Both by reflection, so a premature CRM conversion
  fails here rather than being noticed in review.

## 5. The methodology rule this evidence depends on

An earlier Mutation H attempt reported a **pass** that was worthless: the build had failed, `--no-build` ran the
previous assembly, and the output was suppressed by `tail -2` so the error count was never seen.

Every run recorded here therefore captures the build's error count **explicitly** (`grep -oE "[0-9]+ Error\(s\)"`) and
proceeds only on `0 Error(s)`. A test result from an unverified build is not evidence — it is the previous result
wearing a new timestamp.

## 6. Stability across the mutation cycle

| Point | Combined result |
|---|---|
Baseline, before mutations | **32 / 32** |
After Mutation H applied | 1 failed (the intended failure) |
After restore | **32 / 32** |

The test file was restored to hash `e91e035e68b617b4` and `grep -rl "MUTATION [A-H]"` returns **0 files**.

## 7. Environment at the time of these runs

0 probe databases · 0 mutation markers · `CrossBuyDB2` present and untouched · no SQL executed against it · all
evidence from isolated `CrossBuyProbe_B6Conversion_*` probes, dropped with confirmed removal.
