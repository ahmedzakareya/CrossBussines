# Stage 2 — Hypermarket Assessment — Requirement Status

**No production code written or modified. No SQL executed against `CrossBuyDB2`. No Stage 1 document altered.
No roadmap document edited.** Closes item **C10** of `Stage-002-Phase-0-Completion-Report-3.md`.

---

## 1. Requirement-by-requirement

| Req | Item | Status |
|---|---|---|
| **Discovery** | Sweep the tree for all 33 named concept families, by behaviour and data relationship, not keywords alone | **Completed** — assessment §2. Four features were found under generic names: replenishment/sourcing as `BranchItemSourcing`, offline as `PosSyncLog`, FEFO valuation as `StockCostLayer`, loyalty as `PointsMovement` |
| **H1** | Implementation inventory with 11 attributes per file | **Completed** — `Stage-002-Hypermarket-File-Inventory.csv`, 73 files. Summary in assessment §4 |
| **H2** | Data-model assessment, 40 capabilities classified | **Completed** — assessment §5. 19 Implemented · 8 Partial · 3 Model Only · 1 UI Only · 0 Legacy · 8 Not Found · 1 Unclear-resolved. "Not Found" used only after models, services, controllers, views **and** `deploy/sql` were all checked |
| **H3** | End-to-end traces for 28 flows | **Completed** — `Stage-002-Hypermarket-Business-Flow-Map.md`. 13 Implemented · 6 Partial · 8 engine-implemented-but-unreachable-from-hyper · 1 not present |
| **H4** | POS variant classification | **Completed** — assessment §13.1. Seven variants tabulated; what is shared, duplicated and incorrectly coupled named individually |
| **H5** | Item / master-data dependencies | **Completed** — assessment §7.1. 13 fields traced with breakage risk; **3 named non-negotiable** for the 2C migration gate |
| **H6** | Pricing and promotions traced from source | **Completed** — assessment §7.2. Candidate filter, the 6-key deterministic order, the promotion 4-key order, calculation order and both rounding systems. 22-row support matrix |
| **H7** | Inventory / WMS integration | **Completed** — assessment §8.1. All 10 mandated questions answered, including the finding that the bin layer is real, correct and **entirely unused by retail** |
| **H8** | Accounting and cash control | **Completed** — assessment §8.2. Account-by-account, plus posting time, duplicate-post protection, rollback, company/branch source |
| **H9** | Security and company isolation | **Completed** — assessment §9. Every mutating hyper endpoint classified into the brief's 8 categories; **Stage 2 delta of 4 findings** raised without touching Stage 1 evidence |
| **H10** | UX/UI assessment per screen | **Completed** — `Stage-002-Hypermarket-Screen-Inventory.md`. 21 screens × 18 attributes; 6-way classification; attribute coverage matrix |
| **H11** | Reporting and analytics inventory | **Completed** — Gap Analysis Part 5. 23 report types: 5 Existing · 4 Partial · 9 Data Available but No Report · 5 Data Not Available |
| **H12** | Integrations | **Completed** — assessment §10.2. 14 integrations; 1 implemented by convention, 1 implemented as a data contract, 1 adapter-only stub, 3 configured-but-unused, 8 not found |
| **H13** | Test and reliability assessment | **Completed** — assessment §10.3. **No skipped SQL test counted as coverage; no dev endpoint counted as an automated test.** Suite not re-run — see §3 |
| **H14** | Maturity, evidence-based, three separate scores | **Completed** — assessment §11. Backend **3.2 / 5** · UX/UI **1.6 / 5** · enterprise retail **1.9 / 5**, each with per-dimension evidence |
| **H15** | Gap and improvement proposal, 4 priority bands, 16 attributes each | **Completed** — `Stage-002-Hypermarket-Gap-Analysis.md`. 10 Required Now · 8 Recommended Now · 11 Planned Later · 6 Rejected |
| **H16** | Roadmap integration | **Completed** — `Stage-002-Hypermarket-Roadmap-Proposal.md`. Five candidate classifications each answered from evidence; 8 dependencies with the reason each is a dependency; 3-slice placement inside the existing sequence |
| **Outputs** | 8 documents | **Completed** — all 8 delivered (§2) |
| **Permanent delivery principle** | Unrequested findings classified and documented rather than silently acted on | **Completed** — 24 risks, 4 security-delta findings, 3 corrections to circulating assumptions, and 6 explicit rejections. **Nothing was fixed** |

---

## 2. Output documents

| # | Document | Lines | Contents |
|---|---|---|---|
| 1 | `Stage-002-Hypermarket-Current-State-Assessment.md` | ~430 | Executive summary · corrections to assumptions · inventory summary · data model · flows summary · master data · pricing · inventory/WMS · accounting · security · reports · integrations · tests · maturity · UX summary · variant classification · recommended shape · dependency impact · completion gate |
| 2 | `Stage-002-Hypermarket-File-Inventory.csv` | 85 rows | 73 files + 12 grouped script/asset rows, 11 attributes each |
| 3 | `Stage-002-Hypermarket-Business-Flow-Map.md` | ~330 | 28 flows traced; standing facts stated once; an offline-behaviour appendix that separates three commonly conflated mechanisms |
| 4 | `Stage-002-Hypermarket-Screen-Inventory.md` | ~230 | 21 screens × 18 attributes · 6-way classification · coverage matrix · 19 expected screens for a rebuild |
| 5 | `Stage-002-Hypermarket-Gap-Analysis.md` | ~290 | 10 Required Now · 8 Recommended Now · 11 Planned Later · 6 Rejected · reporting gap table |
| 6 | `Stage-002-Hypermarket-Roadmap-Proposal.md` | ~200 | Shape decision · 8 dependencies · 3-slice placement · ownership decision required · 14 invariants that must not change |
| 7 | `Stage-002-Hypermarket-Risk-Register.md` | ~230 | 24 risks (4 CRITICAL · 8 HIGH · 9 MEDIUM · 3 LOW) with 13 attributes each · 7 risks explicitly not raised, with reasons |
| 8 | `Stage-002-Hypermarket-Requirement-Status.md` | this file | Requirement status · limitations · completion gate |

---

## 3. Limitations, stated rather than hidden

1. **The test suite was not re-run.** The brief permits running it "only if this assessment requires verification". It
   did not: the two hyper-touching tests are structural (they read source text), and their content was read directly.
   No claim in this pack depends on a test result. The last recorded suite state is Phase 0's **763/763 passed, 0 failed,
   0 skipped** with Razor and SQL Server enabled.
2. **No behaviour was executed.** Every behavioural claim is a source trace. Where the record contains an execution
   result — the 12 `hm*-accept` runs — it is cited **as a record**, attributed, and never presented as this
   assessment's own verification. Two of those records carry their own caveat, and both are repeated here rather than
   quietly inherited: the HM-5 acceptance ran on a **prior binary** because parallel work broke the tree
   (HM-D52), and the restaurant offline engine **has never run on a real path** (HM-D24 note).
3. **No SQL was executed.** The 26 relevant `deploy/sql` scripts were read as text. Consequently, statements about
   *schema intent* are certain and statements about `CrossBuyDB2`'s *current applied state* are not made. The one
   applied-state fact carried is from the existing record: `platform_schema_history` is itself unapplied, so it cannot be
   relied on to report what is applied.
4. **The working tree contains uncommitted parallel work.** All 15 hyper paths are committed
   (`git ls-files | grep -i hyper`), but three shared files were modified in the tree at assessment time
   (`PosAccessService.cs`, `PosController.cs`, `PosAppController.cs`). Where those files are cited, the **working-tree**
   content was read. HM-D44 — our base can shift invisibly — applies to this assessment as it does to every phase.
5. **Restaurant-lane depth was assessed only where it shares the engine or contrasts with hyper.** This is a hypermarket
   assessment; `PosAppController`'s 60+ endpoints and `pos-terminal.js` were surveyed, not audited.
6. **The mobile app was checked and excluded.** `crossbuy_mobile` has HR, finance and inventory-view screens and **no
   POS screen**; there is no retail behaviour to assess there.
7. **Maturity scores are judgements over verified facts, not measurements.** The facts behind each score are named so a
   reader who disagrees with a number can see exactly what produced it. Per the brief, nothing was inflated: the module's
   strongest dimension scores 4/5 and its weakest 1/5.

---

## 4. Findings that change existing Stage 2 documents (recorded, not applied)

Per instruction, **no roadmap or Stage 1 document was edited.** Four consequences are recorded here for the reviewer to
apply or decline:

| Document | Consequence |
|---|---|
| `Stage-002-Phase-0-Completion-Report-3.md` | Item **C10** ("Hypermarket integration gate — pending by instruction") is now answerable, and the "five candidate classifications remain open" note resolves to **shared retail core + hypermarket pack** |
| `Stage-002-Dependency-Roadmap.md` | Three additions proposed: the Master Data → Retail dependency gains **three named non-negotiable fields**; a Design-System-tokens → cashier-lane dependency; and WMS gains retail as the **first real consumer of the bin layer**. Placement proposed as three slices inside 2C / post-2B / 2D — **not** a new stage |
| `Stage-002-Risk-Register.md` | 24 hypermarket risks proposed as an **additive** `HR-nn` block. Four are CRITICAL, of which three are **latent** — created by planned work rather than by behaviour running today, and therefore invisible unless recorded now |
| `Stage-002-Screen-Forecast.md` | Adds a Hypermarket group of **14–19** screens (9 redesigns, 5–10 new), dominated by a hyper back-office setup area that has **no screen at all** today |

---

## 5. Completion gate

| # | Gate | Status |
|---|---|---|
| 1 | All hypermarket-related files inventoried | **Yes** |
| 2 | Models, services, controllers, views and SQL inspected | **Yes** — all 9 hyper-phase SQL scripts read in full |
| 3 | End-to-end checkout and shift flows traced | **Yes** — 28 flows |
| 4 | Accounting and stock effects traced | **Yes** — account by account |
| 5 | Restaurant POS and Hypermarket responsibilities distinguished | **Yes** — plus 3 named incorrect couplings |
| 6 | Master Data dependencies documented | **Yes** — 13 fields, 3 non-negotiable |
| 7 | Pricing and promotions traced from source | **Yes** — ordering and rounding order both explicit |
| 8 | Security assessed | **Yes** — every mutating endpoint classified; 4-item Stage 2 delta |
| 9 | Screens assessed individually | **Yes** — 21 screens |
| 10 | Reports and integrations inventoried | **Yes** — 23 reports, 14 integrations |
| 11 | Tests assessed | **Yes** — with the two exclusions the brief requires |
| 12 | Evidence-based maturity calculated | **Yes** — three separate scores |
| 13 | Gaps and improvements prioritized | **Yes** — 4 bands, 35 items |
| 14 | Roadmap placement proposed | **Yes** — proposed, not applied |
| 15 | No production implementation performed | **Yes** — nothing under `CrossBuy/` created or modified |
| 16 | No SQL executed against `CrossBuyDB2` | **Yes** — scripts read as text; no client invoked |

**Also required by the brief and confirmed:**

- The module was **not** classified before the sweep finished. It was classified as neither absent, complete, weak nor
  requiring rebuild — the verdict is *strong engine, thin surface*, and the engine is explicitly recommended for
  preservation.
- **No backend replacement is recommended on the basis of a weak UI.** In all five Major Redesign cases the server-side
  computation is already correct and complete.
- **No improvement was silently added to implementation scope.** All 35 gap items sit in the four mandated bands, and the
  6 rejections are stated with reasons.
- **Nothing was inferred from naming alone.** Every "Not Found" names the search that produced it, and four features
  found under generic names are called out in §2 of the assessment.

---

**Stopping here as instructed.** The remaining Phase 0 documents — roadmap completion, screen forecast, Master Data, WMS,
Retail and Design System — are not advanced until this assessment is reviewed.
