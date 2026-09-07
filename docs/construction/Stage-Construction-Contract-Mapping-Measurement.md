# Stage-Construction — Contract Mapping Measurement (D-01)

**Decision in force:** D-01 — a project **may** contain multiple client contracts; every BOQ, certificate, variation
and commercial transaction must reference `ContractId`; overlapping commercial scope is prohibited; one contract may
be marked Primary; do not assume only one historical contract.

**Status of this document:** the measurement is **specified and shipped, not executed.** This increment ran **zero**
SQL statements against CrossBuyDB2 or any other database. §4 is the form the owner fills in.

---

## 1. Why a measurement gates the schema change

The brief is explicit: measure existing rows, identify projects with ambiguous contract mapping, produce a
classification, and **stop if mapping cannot be deterministic**. A backfill that guesses which contract an existing
certificate belonged to would put a wrong `ContractId` on posted financial history — worse than having no
`ContractId` at all, because it would look authoritative.

## 2. What the code says today (static evidence, no query needed)

| Fact | Source |
|---|---|
| There is **no** contract entity. Contract terms are five nullable columns on the accounting project dimension: `CustomerId`, `Location`, `ContractValue`, `AdvancePercent`, `RetentionPercent` | `CrossBuy/Models/Context/Accounting/Dimensions.cs:36-44` |
| A project therefore has **at most one implicit contract**, and exactly one customer | same |
| A certificate takes its customer **from the project**, not from a contract | `BL/ProgressBillingService.cs:209-210` — `project.CustomerId` |
| Retention/advance percentages are read **from the project** | `BL/ProgressBillingService.cs:100-101` |
| A BOQ line belongs to a **project**, with no contract reference | `Models/Context/Accounting/Boq.cs:12` |
| `BoqItems` has **no foreign key** to `Projects`, so orphans are structurally possible | `deploy/sql/boq.sql` |

**Provisional classification from the code alone:** the mapping is *deterministic by construction* — each
contract-bearing project maps to exactly one `ClientContract`, marked `IsPrimary = 1`. There is no stored data from
which a second contract could be inferred.

**That is a hypothesis about the schema, not a fact about the rows.** The rows can still contradict it: a project
whose posted certificates name a customer other than the project's own would mean the single-contract assumption was
already being violated in practice. M3 is what decides it.

## 3. The measurement script

`deploy/sql/construction_c1_contract_mapping_measurement.sql` — **SELECT statements only**. No INSERT, UPDATE,
DELETE, ALTER, CREATE, DROP or MERGE appears in the file; that is greppable and worth checking before running it.

| ID | What it measures | Why |
|---|---|---|
| **M1** | population sizes (projects, BOQ lines, measurements, certificates, subcontracts, variations) | the scale of any backfill |
| **M2** | contract-bearing projects per company | how many `ClientContracts` rows would be created |
| **M3** | **the determinism test** — projects whose posted certificates reference more than one customer, or a customer other than the project's own | **must return ZERO rows** |
| **M4** | orphans and cross-company references on the rows a backfill would touch | `BoqItems` has no FK, so this is real |
| **M5** | CR-01 exposure: BOQ lines already referenced by a certificate, measurement or issue | the rows whose identity must never move |
| **M6** | CR-01 footprint: certificate lines whose `BoqItemId` no longer resolves, or resolves to a line created **after** the certificate | evidence that delete-and-reinsert has already run on live data |
| **M7** | CR-02 exposure: subcontracts already certified beyond their contract value | the defect, quantified |
| **M8** | CR-03 exposure: approved variations that adjusted a line in place, and how many posted certificates preceded each | posted certificates whose basis changed underneath them |
| **M9** | duplicate BOQ codes within a project | the C1 uniqueness rule cannot be enforced until these are resolved |
| **M10** | whether the C1 objects exist yet | tells the owner whether the DDL has been applied |

## 4. Results — to be completed by the owner

Run against a **restored copy** of production, not production itself, if that is available.

```
sqlcmd -S <server> -d <database> -I -i deploy/sql/construction_c1_contract_mapping_measurement.sql -o measurement.txt
```

| ID | Expected | Observed | Date | By |
|---|---|---|---|---|
| M1 | — (informational) | | | |
| M2 | — (informational) | | | |
| **M3** | **0 rows** | | | |
| M4 | 0 rows in all five branches | | | |
| M5 | — (informational) | | | |
| M6 | 0 rows preferred; any row is historical damage to record | | | |
| M7 | 0 rows preferred; any row is live over-certification | | | |
| M8 | — (informational; each row is a certificate whose basis moved) | | | |
| **M9** | **0 rows**, or an agreed clean-up list | | | |
| M10 | all NULL before the DDL is applied | | | |

## 5. The decision rule — agreed in advance, so the answer is not negotiated after seeing the data

| Outcome | Action |
|---|---|
| **M3 = 0 and M9 = 0** | Mapping is deterministic. Run backfill (a) then (b) from §8 of the DDL script as a separate reviewed step. |
| **M3 = 0, M9 > 0** | Mapping is deterministic but codes clash. Resolve the duplicate codes first; the backfill does not depend on them, but the C1 uniqueness rule does. |
| **M3 > 0** | **STOP.** The single-contract assumption is already violated in the data. Do **not** backfill. Bring the M3 rows to the owner and decide per project which contract each certificate belonged to. A guess here would put a wrong `ContractId` on posted financial history. |
| **M4 > 0** | Fix the orphans before adding foreign keys, or the DDL will fail on those rows. |

## 6. What C1 shipped so the wrong answer is survivable

- `ClientContractId` is **nullable** on `BoqLineStates`, `CommercialRevisions` and `CertificateLineSnapshots`. The
  C1 code works correctly with it null, so nothing is blocked while the measurement is pending.
- The backfill statements are **commented out** inside `construction_c1_commercial_foundation.sql` §8, with the
  measurement named as their precondition. They cannot run by accident.
- `UX_ClientContracts_OnePrimaryPerProject` makes "two primary contracts on one project" impossible at the schema
  level, whatever a future backfill attempts.
- The service layer **creates a missing `BoqLineStates` row on the next save** rather than assuming one exists, so
  backfill (a) is a completeness and performance step, not a correctness prerequisite.

## 7. Scope note on D-01's other rules

Two D-01 rules are **modelled but not yet enforced end-to-end**, because they need the contract to be referenced by
documents that C1 does not touch:

| D-01 rule | State after C1 |
|---|---|
| every BOQ, certificate, variation and commercial transaction references `ContractId` | BOQ lines and commercial revisions carry it (nullable, pending backfill). Client certificates carry it on the **snapshot**. `VariationOrders` and `SubcontractBillings` do **not** yet — C3/C6/C7. |
| overlapping commercial scope is prohibited | Structurally supported: a BOQ line belongs to one contract via `BoqLineStates.ClientContractId`, so scope cannot overlap by construction. The **check** that two contracts do not claim the same line is C3 work. |
| one contract may be marked Primary | Enforced now, by filtered unique index. |
| simultaneous active contracts allowed only when scopes are explicitly separate | Depends on the line-ownership check above — C3. |
