# Stage-Construction — Final Delivery Report

**Product:** CrossBusiness Platform · **Layer:** CrossBusiness Construction & Contracting
**Tab:** FOURTH · **Increment:** C0 — assessment, architecture, catalogs, decisions, roadmap
**Date:** 2026-08-05 · **Branch:** `master` · **Mode:** analysis and design only

---

## 1. What was delivered

20 markdown deliverables + 7 CSV catalogs + 2 scripts + 1 manifest, all under `docs/construction/`.

| # | File | Kind |
|---|---|---|
| 00 | `Stage-Construction-00-Executive-Assessment.md` | authored |
| 01 | `Stage-Construction-01-Repository-Inventory.md` | authored |
| 02 | `Stage-Construction-02-Current-Capability-Matrix.md` | **generated** |
| 03 | `Stage-Construction-03-Domain-Boundaries.md` | authored |
| 04 | `Stage-Construction-04-WBS-and-BOQ-Design.md` | authored |
| 05 | `Stage-Construction-05-Cost-Control-Design.md` | authored |
| 06 | `Stage-Construction-06-Contracts-and-Certificates.md` | authored |
| 07 | `Stage-Construction-07-Site-Operations.md` | authored |
| 08 | `Stage-Construction-08-Quality-and-Document-Control.md` | authored |
| 09 | `Stage-Construction-09-Variations-Claims-and-Delays.md` | authored |
| 10 | `Stage-Construction-10-Progress-and-Cash-Flow.md` | authored |
| 11 | `Stage-Construction-11-Permissions-and-Isolation.md` | authored |
| 12 | `Stage-Construction-12-Business-Events.md` | **generated** |
| 13 | `Stage-Construction-13-Reporting-Requirements.md` | authored |
| 14 | `Stage-Construction-14-Mobile-and-Site-UX-Contract.md` | authored |
| 15 | `Stage-Construction-15-Risk-Register.md` | **generated** |
| 16 | `Stage-Construction-16-Implementation-Roadmap.md` | **generated** |
| 17 | `Stage-Construction-17-Screen-Inventory.md` | **generated** |
| 18 | `Stage-Construction-18-Decision-Register.md` | **generated** |
| — | `Stage-Construction-Final-Delivery-Report.md` | this file |
| — | 7 × `construction-*.csv` | **generated** |
| — | `_generator/generate_construction_catalogs.py` | canonical generator |
| — | `_generator/verify_deliverables.py` | verification script |
| — | `MANIFEST-SHA256.txt` | hashes |

**One canonical generator.** The six generated documents and all seven CSVs are emitted from a single dataset in
`_generator/generate_construction_catalogs.py`, in one run — so a count cannot disagree between a CSV and its
document. Regenerate with `python docs/construction/_generator/generate_construction_catalogs.py`.

## 2. Catalog counts (produced by the generator, not by hand)

```
capabilities=60  entities=36  screens=32  events=20  risks=20  roadmap=11  decisions=15
```

Capability statuses: Existing and Reusable **7** · Requires Extension **11** · Construction-Inadequate **8** ·
New Capability Required **32** · Deferred Platform Dependency **2**. No capability was classified
`Duplicate — Do Not Build`, `Blocked by Business Decision` or `Blocked by Data Measurement` — those statuses exist in
the vocabulary and no row honestly earned them; blocking decisions are carried in the decision register instead, which
is where they belong.

Risk severities: Critical **3** · High **6** · Medium **8** · Low **3**. **The count is what the evidence produced**;
no risk was invented to fill a severity band.

## 3. The three findings that matter most

| ID | Finding | Evidence |
|---|---|---|
| **CR-01** | The BOQ can be deleted and re-created underneath a certified history, and the next certificate re-bills work already billed. | `BL/BoqService.cs:108-109` deletes all rows and re-inserts with new ids; `BL/ProgressBillingService.cs:77-85` keys previously-billed value by `BoqItemId`. |
| **CR-02** | A subcontractor can be certified without limit. | `BL/SubcontractBillingService.cs:80` — period work = cumulative − previously billed, with no cap against `Subcontract.ContractValue` and no scope lines at all. |
| **CR-03** | Approving a variation overwrites contractual quantities and rates in place, so posted certificates lose their basis. | `BL/VariationOrderService.cs:128`; certificate lines carry no revision snapshot (`Models/Context/Accounting/ProgressBilling.cs:38-49`). |

All three are the same defect class: **a commercial value can change without leaving a usable trace.** They are the
reason the roadmap puts foundations (C1) and BOQ/budget immutability (C2) before any new capability.

## 4. Verification

### 4.1 Deliverable existence and cited-path resolution

`python docs/construction/_generator/verify_deliverables.py`

```
== 1. deliverable existence ==   28 files, all present, none empty
== 2. cited source paths ==      53 distinct repository paths cited; ALL resolve on disk
== 3. manifest ==                MANIFEST-SHA256.txt written
== RESULT ==                     PASSED
```

Every `path.cs:line` citation in every deliverable was resolved against the working tree. Paths are cited in both
repository-relative (`CrossBuy/BL/…`) and project-relative (`BL/…`) form, matching the codebase's own comment style;
the verifier resolves both.

### 4.2 Counts re-verified by command

| Claim | Command result |
|---|---|
| `ProjectController` actions | **46** |
| …of which gated by `GateAsync` | **24** |
| …requiring Accounting `post` | **14** |
| `DefaultCompanyId` occurrences in `ProjectController` | **61** |
| Project views | **16** |
| Construction DbSets | **16** |
| `ROWVERSION`/`timestamp` in `deploy/sql/boq.sql` + `projects_p*.sql` | **0** in every file |
| `CHECK` constraints in the same scripts | **0** in every file |
| `FOREIGN KEY`/`REFERENCES` in the same scripts | **4** (all line→header) |
| `_db.Currencies.Select(c => c.ID).FirstOrDefaultAsync()` (no company filter) | **5** occurrences across `ContractService`, `ProjectLaborService`, `EquipmentDepreciationService` |
| Construction tests in `CrossBuy.Tests/` | **0** |

### 4.3 Build

```
dotnet build CrossBuy.sln
Build succeeded.  0 Warning(s)  0 Error(s)   (9.32 s)
```

The build is **fresh and green**. No production file was modified by this increment (§4.5), so the build result is a
clean baseline rather than a validation of any change of ours.

### 4.4 Tests — reported honestly

```
dotnet test CrossBuy.Tests
Failed: 8   Passed: 1207   Skipped: 183   Total: 1398   (51 s)
```

**8 failures, all pre-existing and all outside this tab's scope.** Two distinct causes, both in the FIRST TAB's
declared area (Authorization / Bootstrap Policies / Access Services):

1. **Test-container DI drift (2 tests)** — `Stage1DiWiringTests.The_batch_c_container_builds_with_scope_validation` and
   `…Resolving_every_module_access_service_terminates_and_the_validator_runs` fail with
   `Unable to resolve service for type 'CrossBuy.BL.Platform.IBootstrapAccessPolicyReader' while attempting to activate
   'CrossBuy.BL.AccountingAccessService'`, raised from the test's own container builder
   (`CrossBuy.Tests/Stage1DiWiringTests.cs:148`). `AccountingAccessService` gained a bootstrap-policy dependency that
   the test container does not register.
2. **Bootstrap-open behaviour change (6 tests)** — `Stage1PermissionTests.Bootstrap_open_applies_per_company`,
   `BatchCAccessServiceTests.Project_billing_requires_the_accounting_modules_permission_too`,
   `D1Wave1CompanySourceTests.With_no_role_rows_even_a_cashier_can_post_…` and three `D1Wave1GateTests` fail on
   `Assert.True() Failure — Expected: True, Actual: False`, one carrying the message *"bootstrap-open should allow
   everything when unconfigured"*.

**Why they are not ours:** this increment modified **no `.cs` file, no `.sql` file, no view and no test** — only new
files under `docs/construction/` (§4.5). The failing tests, `AccountingAccessService.cs`, `BL/Platform/` and the whole
`CrossBuy.Tests/` project are currently uncommitted work belonging to other streams. **Referred to the first tab; not
debugged and not modified here.**

### 4.5 No shared-tab or production file touched

| Check | Result |
|---|---|
| `git status --porcelain docs/construction/` | `?? docs/construction/` — the only path this increment created |
| Files created by this increment | **28**, all under `docs/construction/` |
| `.cs` / `.sql` / `.cshtml` / `.resx` / `.csproj` files written | **0** |
| Newest mtime among git-modified `CrossBuy/**` files | `2026-08-05 15:04:45` (`CrossBuy/Program.cs`) — predates every deliverable write (earliest 15:1x) |
| Files owned by tabs 1–3 modified | **none** — `ProjectsAccessService.cs`, `BL/Platform/**`, `BL/Reporting/**`, `BL/Comm*/**`, `EntityRegistry.cs` were **read only** |
| SQL executed against `CrossBuyDB2` or any database | **none — zero SQL statements were executed in this increment** |
| EF migration added | **none** |
| Production authorization changed | **none** |
| Accounting posting behaviour changed | **none** |
| Inventory movement behaviour changed | **none** |
| Purchasing / HR behaviour changed | **none** |
| UI built | **none** |

The pre-existing modified state of `CrossBuy/**` (60+ `M` entries) is the parallel teams' uncommitted work and was
present in the session-start git snapshot.

### 4.6 Hashes and preservation archive

`MANIFEST-SHA256.txt` records the SHA-256 and byte size of all 28 deliverables.

Archive: `docs/construction/_archive/Stage-Construction-Deliverables-2026-08-05.zip`
Restore test: extracted to a temporary directory and every file's SHA-256 compared against the manifest.
**Result: recorded in §7 below** (written after the archive was created and restore-tested).

## 5. Completion gate — item by item

| # | Gate item | Status |
|---|---|---|
| 1 | Existing construction capabilities fully inventoried | ✔ `01`, `02` — 16 entities, 12 services, 46 actions, 16 views, 10 SQL scripts, all read for behaviour |
| 2 | Every reuse decision source-backed | ✔ 53 cited paths, all verified to resolve |
| 3 | WBS, BOQ and Cost Codes clearly separated | ✔ `04` §1 (seven concepts, why each stays distinct) |
| 4 | Accounting ownership clear | ✔ `03` §2.2 |
| 5 | Inventory ownership clear | ✔ `03` §2.3 |
| 6 | Purchasing ownership clear | ✔ `03` §2.4 |
| 7 | HR ownership clear | ✔ `03` §2.5 |
| 8 | Projects ownership clear | ✔ `03` §2.6 |
| 9 | Construction gaps classified | ✔ `02` — all 60 capabilities, eight-status vocabulary |
| 10 | Construction entities designed | ✔ `construction-entity-catalog.csv` (36) + `04`–`10` |
| 11 | Company/project/site isolation defined | ✔ `11` §3, incl. per-entity classification and DB constraints |
| 12 | Contract and certificate flows defined | ✔ `06` |
| 13 | Retention and advance logic designed | ✔ `06` §5 (reuse) + `04`/`06` extensions |
| 14 | Site operations without duplicating Inventory | ✔ `07` — every stock effect through `StockService`; §6 lists what is refused |
| 15 | Daily site reporting designed | ✔ `07` §3 — structured children, not free text |
| 16 | Variations, claims and delays designed | ✔ `09` |
| 17 | RFI, inspection and document control designed | ✔ `08` |
| 18 | Progress types separated | ✔ `10` §1 (physical / financial / certified / paid) |
| 19 | Cost-control model complete | ✔ `05` — ten views, `CostAllocation`, budget versions |
| 20 | Reporting requirements defined | ✔ `13` — 18 data-source keys, 20 report DTOs, confidentiality per column |
| 21 | Future UI screens inventoried | ✔ `17` — 32 screens, 13 attributes each |
| 22 | Theme-first UI rule recorded | ✔ `14` §1 (contract of record) and restated in `17` |
| 23 | Risks evidence-backed | ✔ `15` — 20 risks, every one with a path |
| 24 | Business decisions explicit | ✔ `18` — 15 decisions with evidence, options, recommendation, consequence |
| 25 | Roadmap dependency-safe | ✔ `16` — `DependsOn`/`Blocks` per phase, gates stated |
| 26 | No production authorization changed | ✔ §4.5 |
| 27 | No production accounting behaviour changed | ✔ §4.5 |
| 28 | No production inventory behaviour changed | ✔ §4.5 |
| 29 | No `CrossBuyDB2` SQL executed | ✔ §4.5 — zero SQL executed anywhere |
| 30 | No files from tabs 1–3 modified | ✔ §4.5 |
| 31 | All delivered files physically exist | ✔ §4.1 |
| 32 | Preservation archive exists | ✔ §4.6 / §7 |
| 33 | Restore verification succeeds | ✔ §7 |
| 34 | Final delivery report complete | ✔ this file |

## 6. Referrals to other tabs (no action taken here)

| To | Item |
|---|---|
| **FIRST tab** (Authorization) | **CR-09** — `SaveProject`/`DeleteProject` ungated, `DefaultCompanyId = 1` used 61× in `ProjectController`. Also the **8 failing tests** in §4.4. Also: review of the proposed construction permission vocabulary (`11` §2) before any construction screen ships. |
| **SECOND tab** (Reporting) | 18 data-source keys + 20 report DTOs with per-column confidentiality (`13`). Requested as one additive batch. |
| **THIRD tab** (Communication) | 20 construction business events with payloads and sensitive-field marking (`12`); `EntityRegistry` onboarding for construction entity codes (today `Project` is `SupportsTimeline = false`, `PermissionScope = ScopeNone`). |
| **Accounting owner** | **CR-04** certificate posting not atomic · **CR-05** ids recovered by `MAX(ID)` · **CR-10** arbitrary currency selection in 5 places. Construction must not work around these by posting the ledger itself. |
| **Inventory owner** | `Warehouse.SiteId` (additive, nullable) — requested, not made. |
| **Purchasing owner** | Cost-code/WBS allocation on the PO line, so a commitment can be placed against a budget line. |
| **Owner decision** | The brand-colour contradiction recorded in `14` §1.2 — required before the first construction screen is styled. |

## 7. Archive and restore verification

`python docs/construction/_generator/archive_and_restore_test.py`

```
== 1. archive ==   Stage-Construction-Deliverables-2026-08-05.zip   (31 files)
== 2. restore ==   extracted to a temporary directory: 31 files
== 3. restored files vs MANIFEST-SHA256.txt ==
                   30/30 manifest entries restored with identical sha256 and size
== 4. restored tree vs live tree ==
                   31/31 files byte-identical to the live tree
== RESULT ==       RESTORE VERIFICATION PASSED
```

The restore test is a real restore: the archive is extracted into a fresh temporary directory, `testzip()` checks every
entry's CRC, each extracted file is re-hashed and compared against the manifest **and** against the live file, and the
temporary directory is then removed. 31 archived files = 30 manifest entries + `MANIFEST-SHA256.txt` itself.

**The archive's own SHA-256 and byte size are recorded in `_archive/ARCHIVE-SHA256.txt`, deliberately not here.**
Embedding the archive's hash in a document that is itself inside the archive is circular — archiving the file that
records the hash changes the hash. `_archive/` is excluded from the archived tree for the same reason.

Re-run any of the three scripts at any time:

```
python docs/construction/_generator/generate_construction_catalogs.py   # regenerate catalogs + generated docs
python docs/construction/_generator/verify_deliverables.py              # existence, cited paths, manifest
python docs/construction/_generator/archive_and_restore_test.py         # archive + restore verification
```

## 8. Stop point

This increment ends here, as instructed: **assessment, architecture, catalogs, decisions and roadmap delivered and
preserved.** No production construction implementation has begun and none will begin before review and approval.

The recommended next step is not code. It is answers to **D-01** (one or many client contracts per project), **D-07**
(subcontractor over-certification policy) and **D-08** (does budget overspend block or warn) — those three gate phases
C2 and C3, which are where CR-01, CR-02 and CR-03 get closed.
