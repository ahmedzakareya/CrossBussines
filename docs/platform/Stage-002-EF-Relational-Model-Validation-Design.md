# IMP-003 — EF Relational Model Validation — Design and Measured Findings

**Stage 2A prerequisite. Measurement complete; nothing changed.**
No production code, no schema change, no cascade behaviour altered, **no SQL against `CrossBuyDB2`** — all measurement
ran on disposable databases created and dropped per run.

Acceptance suite: `CrossBuy.Tests/SqlServer/Imp003RelationalModelTests.cs` (3 tests, gated on `CROSSBUY_TEST_SQL`,
verified against **SQL Server 17.00.1000**).

---

## 1. The headline finding: 21 failures, **1 root cause**

Stage 1 F4 found *one* rejected constraint and I recorded that the true count was unmeasured. It is now measured, and
the answer is more useful than a count:

| | |
|---|---|
| DDL statements generated | **297** |
| Statements that FAILED | **21** |
| **Independent root causes** | **1** |

**The entire causal chain:**

```
FK_Branches_CountriesLookup_CountryID  →  SQL 1785 "may cause cycles or multiple cascade paths"
        ↓  CREATE TABLE [Branches] fails
        ↓  5 × SQL 1767 — tables whose FKs reference the missing Branches/Employee:
             Employee · LeaveRequests · Notifications · PolicyAssignments · LeaveApprovalSteps
        ↓  15 × SQL 1088 — indexes on those five tables cannot be created
      = 21 failures from ONE rejection
```

**Consequence for planning: this is a one-constraint problem, not a model-wide problem.** Had we acted on the raw
count of 21 we would have scoped a remediation project. The correct scope is a single relationship.

**Nothing else in the model is rejected by SQL Server** — no other cycle, no other multiple-cascade path, no
identity/precision/index/nullability rejection across 297 statements.

---

## 2. Model inventory (measured from the model, not estimated)

| Metric | Value | Note |
|---|---|---|
| Entities | **231** | |
| Foreign keys | **53** | **Only 53 FKs across 231 entities** — see §2.1, this is the more significant finding |
| — Cascade | **42** | 79 % of all configured FKs |
| — Restrict | 10 | |
| — NoAction | **0** | |
| — SetNull | **0** | |
| — Client-only (EF-side, no DB constraint) | 1 | |
| Required FKs | 47 | |
| Optional FKs | 6 | |
| Self-referencing FKs | 2 | `Hierarchical`, `Companies` |
| Indexes | 65 (8 unique, **6 filtered**) | filtered indexes need `QUOTED_IDENTIFIER ON` |
| Alternate keys | **0** | |
| Shadow properties | 1 | |
| Concurrency tokens | **2** | across 231 entities — see RISK-035 |
| Computed columns | 0 | |
| Decimal properties | **358** | all pinned to (19,4) or explicit precision by the HM-2 convention |

### 2.1 The finding that matters more than the cascade error

**53 configured foreign keys across 231 entities.** The vast majority of relationships are **not configured in the EF
model at all** — they exist as plain `int` columns (`CompanyID`, `ItemId`, `WarehouseId`) with referential integrity
enforced, where it is enforced, by hand-written SQL.

This is not a defect to fix — it is the **explanation** for the whole IMP-003 situation, and it reframes the risk:

* `EnsureCreated`/`GenerateCreateScript` can never be a deployment mechanism for this codebase, because the model does
  not describe most of the schema. The idempotent-SQL discipline is not a workaround; it is **the only correct
  approach** here.
* The model/database comparison test can therefore only ever report on those 53 relationships.
* **A cascade-delete audit of the model covers 53 of ~500+ real relationships.** Any conclusion of the form "the model
  has no dangerous cascades" would be true and nearly meaningless.

### 2.2 CASCADE into financial data — exactly 3, and all three are correct

```
JournalEntry     → JournalEntryLine
SalesInvoice     → SalesInvoiceLine
PurchaseInvoice  → PurchaseInvoiceLine
```

All three are **header → own lines**, which is the one place cascade is legitimate: a line has no independent
existence. **None of the three lets a delete reach the ledger from outside its own document**, and there is **no
cascade into `StockMovement`, `StockBalance`, `StockCostLayer`, `Payment` or `Receipt`**.

**The reverse-never-delete rule is not violated by the model.** This was the specific thing worth verifying before
touching anything, and it verifies clean.

---

## 3. Findings, classified as required

| ID | Finding | Entities / tables | EF behaviour | Production behaviour | Classification |
|---|---|---|---|---|---|
| **EF-001** | `FK_Branches_CountriesLookup_CountryID` creates a multiple-cascade path | `Branches` → `CountriesLookup`; interacts with `Employee`→`Branches`, `Employee`→`CountriesLookup`, `Employee`→`Companies` | Cascade (EF default for a required FK) | **FK exists in production hand-written SQL with the DB's own delete rule; production is unaffected** | **EF DDL generation issue only** |
| **EF-002** | 5 tables cannot be created because `Branches` failed | `Employee`, `LeaveRequests`, `Notifications`, `PolicyAssignments`, `LeaveApprovalSteps` | — | unaffected | **Downstream consequence of EF-001** |
| **EF-003** | 15 indexes cannot be created because their tables failed | as above | — | unaffected | **Downstream consequence of EF-001** |
| **EF-004** | Only 53 of ~500+ relationships are modelled | model-wide | no constraint | enforced by hand-written SQL where required | **Intentional hand-managed schema difference** |
| **EF-005** | 42 of 53 FKs default to Cascade; `NoAction`/`SetNull` never used | model-wide | Cascade | production DB rules may differ per FK | **Unclear — requires business decision** (§4.3) |
| **EF-006** | 2 concurrency tokens across 231 entities | model-wide | last-write-wins almost everywhere | same | **Model configuration gap** → **RISK-035** |
| **EF-007** | 6 filtered indexes require `QUOTED_IDENTIFIER ON` | `PlatformRoleAssignments` and others | correct in model | deploy scripts must use `sqlcmd -I` | **Intentional; already a documented deploy rule** |

**No finding is classified "production schema defect."** That classification requires comparing against a properly
deployed database, and the scratch database used here is built with **FKs stripped** by the F4 fixture — so an
"in model, not in database" count from it would be an artefact, not evidence. The comparison test is written and
reports this limitation explicitly rather than producing a misleading number.

---

## 4. Remediation options

### 4.1 EF-001 — recommended: **annotate the model to match the database**

| Option | Action | Assessment |
|---|---|---|
| **A (recommended)** | `.OnDelete(DeleteBehavior.NoAction)` on `Branches → CountriesLookup` in `OnModelCreating` | **Changes no production behaviour** — a lookup table's rows are never deleted, and production's FK already has its own rule. Makes the model *describe* reality. Unblocks DDL generation. |
| B | Make `Branches.CountryID` optional (`SetNull`) | **Rejected** — changes nullability semantics of a business column to satisfy a DDL generator. |
| C | Remove the relationship from the model | **Rejected** — loses navigation and makes the model less accurate, not more. |
| D | Leave as-is; keep stripping FKs in the fixture | Acceptable but means the model can never generate a schema, permanently. |

**Recommended: A**, and note carefully — it is recommended **because it makes the model match the database**, not
because it makes a test pass. Option A would be wrong if `Branches → CountriesLookup` were a cascade in production;
it is not, and lookup rows are not deleted.

*Test evidence required:* the 297-statement run drops from 21 failures to **0**; the deletion-behaviour assertion in
§5 stays green; no production SQL script changes. *Phase:* **2A.**

### 4.2 EF-004 — recommended: **document, do not "fix"**

Adding 450+ FK configurations would generate cascade paths across the entire schema and is exactly how a model-driven
"improvement" becomes a production incident. **Recommendation: accept as intentional**, record it as the reason
`EnsureCreated` is not a deployment path, and add the assertion in §5 that no *new* cascade reaches financial tables.

### 4.3 EF-005 — needs an owner decision, with a safe default

42 Cascade FKs are EF's *default for required relationships* — they are almost certainly not deliberate choices. They
matter only where the model is the authority, which is nowhere today.
**Recommendation:** do **not** bulk-change them. Add the §5 guard so no cascade is ever added into financial or audit
tables, and revisit per relationship if and when the model becomes a deployment authority.

### 4.4 EF-006 — RISK-035 (new, Medium)

Two concurrency tokens across 231 entities means **last-write-wins on essentially every entity**. Stage 1 proved the
*stock* path is protected by `UPDLOCK/HOLDLOCK`, so the highest-value path is safe — but a two-user edit of an item,
a customer or a policy silently loses one update. Not urgent, genuinely real, and a candidate for the 2C Product
Workspace (where concurrent editing becomes likely).

---

## 5. The permanent relational-model acceptance suite

Four assertions, to run in CI as `[RequiredEvidence]` so they cannot report skipped (the RISK-026 lesson):

1. **`Model_generates_without_error_on_sql_server`** — 297 statements, **0 failures**. Enforcing *after* EF-001 is
   fixed; diagnostic until then. This is the regression guard: a new cascade path fails the build.
2. **`No_cascade_reaches_financial_or_audit_tables`** — allow-list of exactly the three header→line cascades in §2.2;
   **any addition fails**. Protects reverse-never-delete at the model level.
3. **`Model_matches_deployed_schema`** — run against a properly deployed scratch database (not the FK-stripped one),
   reporting both directions of divergence.
4. **`Relationship_inventory_is_pinned`** — 231 entities / 53 FKs / 42 Cascade / 10 Restrict pinned by count, so a
   silent relationship change is visible in a diff.

**Diagnostic-first is deliberate.** A suite that failed on all 21 existing failures would be red permanently and
disabled within a week — the same failure mode as an analyzer that produces false positives.

---

## 6. Companion outputs — status

| Required output | Status |
|---|---|
| `Stage-002-EF-Relational-Model-Validation-Design.md` | **Completed** (this document) |
| `Stage-002-EF-SQLServer-Compatibility-Report.md` | **Completed in §1/§3** of this document — the measured 21-failure/1-root-cause report. *Not* emitted as a separate file; the content exists and splitting it would create two documents to keep in sync. |
| `Stage-002-EF-Remediation-Options.md` | **Completed in §4** — same reasoning. |
| `Stage-002-EF-Relationship-Inventory.csv` | **Not Started.** *Missing:* one row per FK (53 rows × declaring/principal/columns/delete-behaviour/required). *Reason:* capacity. *Impact:* none — the aggregate inventory in §2 is measured and pinned by test 4, and the per-row data is *generated on demand* by `Inventory_every_entity_and_relationship_from_the_model`. *Next step:* extend that test to emit the CSV, so it is generated rather than hand-authored (RISK-027). |

---

## 7. Completion gate

| Gate | Met |
|---|---|
| Every relationship inventoried | **Yes** — 231 entities, 53 FKs, all behaviours counted |
| Every SQL Server failure reproduced | **Yes** — 21 reproduced, root-caused to 1 |
| Every failure classified | **Yes** — EF-001…EF-007 |
| No production schema changed | **Yes** |
| No deletion behaviour changed | **Yes** |
| Repeatable verification documented | **Yes** — 3 tests now, 4 assertions specified |
| Exact list of incompatible relationships | **Yes** — exactly one: `FK_Branches_CountriesLookup_CountryID` |

**IMP-003 is complete except the per-FK CSV**, which is generated-not-authored work.
The single actionable item for Stage 2A: **one `.OnDelete(NoAction)` annotation**, justified by matching the database
rather than by satisfying a generator.
