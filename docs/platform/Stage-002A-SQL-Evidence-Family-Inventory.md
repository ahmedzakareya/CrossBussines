# Stage 2A — SQL Evidence Family Inventory

Every SQL test family, its schema behaviour, and its RISK-036 classification. Derived by inspecting each file for
`EnsureEfSchemaAsync`, `GenerateCreateScript`, `CREATE DATABASE`, `EnsureCreated` and `Database.Migrate`.

---

## 1. Classification

| Family | Schema mechanism | Database ownership | Cleanup | Classification |
|---|---|---|---|---|
| **`BatchPPreservedRuleTests`** | `CreateProbeDatabaseAsync("BatchP")` | **own probe** | `DropProbeDatabaseAsync`, removal confirmed | **Already Migrated** |
| **`Stage1F4DatabaseProofTests`** | `CreateProbeDatabaseAsync("F4")` | **own probe** | same | **Already Migrated** (this increment) |
| **`TaskScopeQuerySqlTests`** | `CreateProbeDatabaseAsync("TaskScope")` | **own probe** | same | **Already Migrated** (this increment) |
| **`Imp003RelationalModelTests`** | its own `CREATE DATABASE` probe for the DDL reproduction; **deliberately does not** call `EnsureEfSchemaAsync` | own probe, dropped in `finally` | explicit drop | **Safe** — see §2 |
| `PlatformSchemaDeploymentTests` | none — runs the **deploy scripts** and interrogates `sys.*` | shared fixture (correct: it tests the shared deployment) | n/a | **No Schema** |
| `DispatchConcurrencyTests` | none | shared fixture | n/a | **No Schema** |
| `CommOutboxConcurrencyTests` | none — uses `EnsureCommOutboxAsync` (a **deploy script**, not model DDL) | shared fixture | n/a | **No Schema** |
| `EventMonitorRetryConcurrencyTests` | none | shared fixture | n/a | **No Schema** |
| `WorkerLeaseTests` | none | shared fixture | n/a | **No Schema** |

**9 families. 3 migrated this increment or previously · 1 already safe · 5 create no schema.**
**No family remains in the "Needs Probe" state. No "Legacy" family exists.**

## 2. Why `Imp003RelationalModelTests` is Safe rather than migrated

It already creates and drops its own `CrossBuyImp003_<guid>` database for the DDL-reproduction test, and its
model-comparison test **deliberately does not** create schema — a decision recorded in the file itself after it broke
the schema-deployment tests once.

It could adopt `CreateProbeDatabaseAsync` for consistency, and that is a **Recommended Now** cleanup rather than a
RISK-036 exposure: its database is already isolated, owned and dropped. Left as-is deliberately, because changing
working isolated code to use a different isolated helper is churn without a defect behind it.

## 3. The distinction that decides the classification

**Model-derived DDL vs deploy scripts.** `EnsureEfSchemaAsync`/`GenerateCreateScript` create **the whole EF model** —
231 entities — which is what polluted the shared fixture. `EnsureCommOutboxAsync` and `ReapplySlice1Async` run the
**committed `deploy/sql` scripts**, which is exactly what the shared fixture is *for* and what
`PlatformSchemaDeploymentTests` asserts about.

Conflating the two would have migrated five families that never had a problem, and would have broken the tests whose
subject **is** the shared deployment.

## 4. Verification

| Check | Result |
|---|---|
| Residual `await _sql.EnsureEfSchemaAsync()` call sites | **0** |
| `Stage1F4DatabaseProofTests` | 15 passed |
| `TaskScopeQuerySqlTests` | 5 passed |
| `BatchPPreservedRuleTests` | 7 passed |
| `Imp003RelationalModelTests` | 3 passed |
| `PlatformSchemaDeploymentTests` | **9 passed** |
| Full suite ×2 | **781 passed · 0 failed · 0 skipped** (identical) |
| Probe databases remaining | **0** |
| `CrossBuyDB2` | present and untouched |

`EnsureEfSchemaAsync` remains on the fixture with no callers. **Recommended Now:** mark it `[Obsolete]` pointing at
`CreateProbeDatabaseAsync`, so the trap cannot be re-entered by a future family. Not done here — this increment was
scoped to eliminating the exposure, and adding an attribute is a separate, reviewable change.
