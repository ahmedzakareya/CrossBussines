# Stage-Construction-C1 — Commercial Foundation Plan

**Product:** CrossBusiness Platform · **Layer:** CrossBusiness Construction & Contracting
**Tab:** FOURTH · **Increment:** C1 — fix the three critical commercial defects before adding capability
**Approved decisions in force:** D-01 (multi-contract per project), D-07 (hard block), D-08 (three-mode policy)
**Brand:** CrossBusiness **Blue** is the official identity; green/gold are domain/report content colours only.

---

## 1. Scope of this increment

Owned here — and nothing else:

1. **CR-01** BOQ stable identity (remediated)
2. **CR-02** subcontract certificate lines + cap (remediated)
3. **CR-03** commercial revision snapshots (remediated)
4. Concurrency and line-level audit foundation for the affected entities
5. Evidence and preservation

Explicitly **not** started: WBS UI, DSR, RFI, claims, cash flow, final screens, Primavera import, resource
leveling, mobile. Those are roadmap phases C4–C12.

## 2. The governing constraint, and the design it forced

This increment **may not execute SQL against any database**, and three other tabs are working in the same tree.
That rules out the obvious design — adding columns to `BoqItems` and `ProgressBillingLines` — because between
shipping the code and the owner applying the script, an EF mapping referencing a not-yet-created **column** would
break every read of the existing Projects screens, for everyone.

So C1 is built entirely from **new tables**:

| New table | Replaces the column that would otherwise be added |
|---|---|
| `BoqLineStates` | `BoqItems.Status` / `.ClientContractId` / `.CurrentRevisionId` / `.RowVersion` |
| `CertificateLineSnapshots` | `ProgressBillingLines.BoqRevisionId` / contracted quantity + rate |
| `ClientContracts` | the five implicit contract columns on `Project` (D-01) |
| `CommercialRevisions` + `…Lines` | — (new concept) |
| `SubcontractScopes` | — (new concept: the cap) |
| `SubcontractCertificateLines` | the lines `SubcontractBillings` never had |
| `ConstructionAuditEntries` | — (new concept) |

**Failure mode if the script is not applied:** only the new C1 code paths fail, loudly. Every existing screen keeps
working. That is the whole reason for the shape. Folding the satellites into their base tables is a later,
owner-scheduled consolidation (`Stage-Construction-Concurrency-and-Audit-Design.md` §6).

**No existing entity class was modified.** `Boq.cs`, `ProgressBilling.cs`, `Subcontract.cs`, `VariationOrder.cs`,
`Dimensions.cs` are byte-identical to before this increment.

## 3. What changed, file by file

| File | Kind | Change |
|---|---|---|
| `CrossBuy/Models/Context/Construction/ConstructionCommercial.cs` | **new** | 8 entities, status vocabularies, model configuration |
| `CrossBuy/BL/Construction/ConstructionAuditService.cs` | **new** | append-only audit + the concurrency token helper |
| `CrossBuy/BL/Construction/CommercialRevisionService.cs` | **new** | CR-03 revisions, snapshots, reproduction |
| `CrossBuy/BL/Construction/SubcontractScopeService.cs` | **new** | CR-02 scope, cap, certificate lines |
| `CrossBuy/BL/BoqService.cs` | **modified** | CR-01: `ReplaceAllAsync` no longer deletes; `SaveLinesAsync` added |
| `CrossBuy/Models/Context/CrossDbContext.cs` | **modified** | 8 DbSets + one `ConstructionCommercialModel.Configure` call |
| `CrossBuy/Program.cs` | **modified** | 3 `AddScoped` registrations |
| `deploy/sql/construction_c1_commercial_foundation.sql` | **new** | idempotent DDL — **not executed** |
| `deploy/sql/construction_c1_contract_mapping_measurement.sql` | **new** | read-only SELECTs — **not executed** |
| `CrossBuy.Tests/ConstructionTestFixture.cs` + 4 test files | **new** | 38 tests |

Shared files touched: `CrossDbContext.cs` (11 lines), `Program.cs` (7 lines). Both additive, both clearly marked,
neither altering an existing line. No file belonging to tabs 1–3 was modified.

## 4. The three remediations in one line each

- **CR-01** — a BOQ save is now a *difference*: identified lines keep their id, omitted lines are **retired** (never
  deleted), and a referenced line cannot be removed at all. Evidence: `…CR01-BOQ-Identity-Evidence.md`.
- **CR-02** — certification happens **line by line against a scope**, and a line past the cap is refused. The cap
  moves only on an approved variation. Evidence: `…CR02-Subcontract-Cap-Evidence.md`.
- **CR-03** — a contractual quantity or rate changes only through an **approved, immutable commercial revision**, and
  every certificate line keeps a snapshot of what it certified. Evidence: `…CR03-Commercial-Revision-Evidence.md`.

## 5. Test-first, and proved by mutation

38 tests were written **before** the services they exercise. Green tests alone prove nothing, so each remediation was
then broken on purpose:

| Mutation | Guarding test | Result |
|---|---|---|
| C-01 restore delete-and-reinsert | `Previously_billed_work_cannot_become_billable_again_after_a_boq_edit` | failed under mutation ✔ |
| C-02 remove the cap | `Certification_beyond_assigned_quantity_is_hard_blocked` | failed under mutation ✔ |
| C-03 reproduce from today's BOQ | `A_posted_certificate_reproduces_the_rate_it_was_certified_at` | failed under mutation ✔ |

All three restored byte-identically (SHA-256 compared), and a whole-tree sweep confirms no mutation marker survives.
Raw results: `mutation-proof-results.json`.

## 6. What is deliberately NOT done, and why

| Not done | Reason |
|---|---|
| Wiring the header/line reconciliation guard into `SubcontractBillingService.Approve/Post` | It would change an existing Accounting-posting path. The brief forbids that here; the guard exists and is tested, and C6 wires it. |
| Capturing the client-certificate snapshot inside `ProgressBillingService` | Same reason: that is the posting path. The capture service exists and is tested; C6 wires it. |
| A `ClientContracts` / `BoqLineStates` backfill | The brief requires the mapping to be MEASURED first and to stop if it is not deterministic. The measurement script ships; the backfill is commented out in the DDL awaiting the owner's M3/M9 output. |
| Any UI | No production UI in this increment, by instruction. |
| RowVersion on `BoqItems` / `ProgressBillings` themselves | Would require an ALTER + EF mapping change (see §2). The token lives on the satellite that owns the commercial decision. |

## 7. Verification summary

| Check | Result |
|---|---|
| Fresh build (`dotnet build CrossBuy.sln`) | **succeeded**, 0 errors |
| C1 construction tests | **38 passed**, 0 failed |
| Full application suite | **1319 passed, 0 failed**, 183 skipped |
| Mutation proofs C-01/C-02/C-03 | **all PROVEN**, all restored byte-identical |
| Mutation markers remaining | **none** |
| SQL executed against CrossBuyDB2 or any DB | **none** |
| Files of tabs 1–3 modified | **none** |
| Production UI built | **none** |

Detail in `Stage-Construction-C1-Delivery-Report.md`.
