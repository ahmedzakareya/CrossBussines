# Stage 2 — Phase 0 — Requirement Status

**No production code written. No SQL executed against `CrossBuyDB2`. No Stage 1 document altered.**

Phase 0 acceptance (stronger than Stage 1's): application build **0 errors with Razor ENABLED**, suite
**763 / 763 passed, 0 failed, 0 skipped** with SQL Server integration tests **enabled**.

---

## 1. Requirement-by-requirement

| Req | Item | Status |
|---|---|---|
| **P0-1** | Load and verify the Stage 1 baseline | **Completed** — all 7 metrics re-derived from source and matched exactly; Stage 2 delta record produced (§2) |
| **P0-2** | Stage 2 architecture review, 15 questions | **Completed** — `Stage-002-Architecture-Review-and-Improvement-Proposal.md` |
| **P0-3** | Engineering Platform / analyzer design | **Completed** — `Stage-002-Engineering-Platform-Proposal.md`; 20 analyzers assessed, 2 recommended for 2A with reasons, 18 catalogued with trigger conditions |
| **P0-4** | CI and test-evidence guardrails | **Completed** — all 12 requirements addressed; EF cascade debt given a measure-first treatment |
| **P0-5** | Enterprise Platform Framework | **Partial** — approach chosen and justified (§3); the 22-capability design document is not written |
| **P0-6** | Master Data reassessment | **Partial** — model/terminology/duplication findings captured (§4); the full 15-area bilingual UX architecture is not written |
| **P0-7** | Module reassessment (24 modules) | **Completed** — all 24 classified with evidence in the review, §4 |
| **P0-8** | Future platform proposals (24 platforms) | **Partial** — validated as a sequence and conditional technologies classified; per-platform detail not written |
| **P0-9** | Tasks / workflow / communication / escalation contract | **Not Started** |
| **P0-10** | UX/UI governance standard | **Not Started** |
| **P0-11** | New screen forecast | **Not Started** |
| **P0-12** | Delivery sequencing / dependency roadmap | **Partial** — two corrections to the proposed order recommended with evidence (review §5); the per-phase gate table is not written |
| **P0-13** | Measurement model refinements | **Partial** — no-points rules restated and carried forward; per-stage completion gates not defined |
| **P0-14** | Ten output documents | **Partial** — 4 of 10 delivered (§5) |
| **Permanent delivery principle** | Unrequested improvements found, classified, documented | **Completed** — 12 improvements + 6 rejections, in the review §3 and `Stage-002-Improvement-Backlog.csv` |

## 2. P0-1 — the delta record

**One concurrent change, and it is favourable.** The parallel team fixed
`Views/Accounting/CustomerAnalytics.cshtml`, which had broken Razor compilation throughout Stage 1's final increments
and forced acceptance with `-p:RazorCompileOnBuild=false` — meaning **no view was compiled during Stage 1
acceptance**. Phase 0 re-ran acceptance with **Razor and SQL both enabled**: 0 errors, 763/763.

This retroactively closes limitation #6 of the closure report. The closure report was **not** edited; Stage 1 stays
frozen and this record supersedes that one limitation.

**Not part of the baseline:** 66 untracked + 65 modified files of parallel work remain uncommitted. HM-D44 (our base
can shift invisibly) carries into Stage 2 unchanged.

## 3. P0-5 — the framework decision, recorded now because later work depends on it

Four approaches were compared. **Recommendation: registry + capability metadata + interfaces, with event-driven
projections. Explicitly NOT inheritance.**

| Approach | Verdict |
|---|---|
| **Inheritance** (one base entity) | **Rejected.** Entities already persist with divergent conventions — `TaskItem.CompanyId` versus `CompanyID` everywhere else, differing key and audit shapes. A base class means touching every table, and the two-writer rule means migrating data through code that must not change. |
| **Interfaces alone** | Insufficient — an interface cannot express "this object supports timeline but not approvals" without a combinatorial explosion. |
| **Registry + capability metadata** | **Recommended.** `EntityRegistry` already exists and already carries frozen entity codes; extending it with opt-in capability flags matches the platform's own precedent (`SupportsTimeline`) and requires **no schema change to business tables**. |
| **Composition + event projections** | **Recommended alongside.** Timeline, search and AI context should be *projections* fed by `BusinessEvents`, not columns on business tables — which is what the existing outbox already does for notifications. |

*Missing work:* the per-capability contract for all 22 capabilities. *Reason:* capacity. *Impact:* none yet — no
module depends on it before 2B. *Next action:* write `Stage-002-Enterprise-Platform-Framework-Proposal.md` defining
opt-in surface, storage, permission trimming and event contract per capability.

## 4. P0-6 — Master Data findings captured so far

Enough was inspected to justify its 2C placement and to state the problem precisely:

* **Terminology collision:** Item / Product / Service / Asset are not consistently distinguished; `Item` carries all
  four behaviours by convention rather than by type or capability.
* **Duplicated fields:** name/NameEn twins on most master entities, with a culture check at every read site rather
  than one resolution helper.
* **Units:** unit sets and conversions exist; **packaging is not modelled** as a distinct concept from unit.
* **Variants/attributes:** no variant model — variants are currently distinct items, which is why kits, bundles and
  configurable products have no clean representation.
* **Composition:** manufacturing BOM exists; **sales BOM / kit / bundle do not**, and conflating them with the
  manufacturing BOM is the main data-model limitation.
* **Company source:** `InventoryController` holds **267** hardcoded company references — the largest single
  concentration in the codebase and a migration risk for any Master Data redesign.
* **Governance:** no duplicate detection, no approval workflow, no lifecycle status beyond `IsActive`.

*Missing work:* the 15-area bilingual workspace architecture and per-area field mapping. *Reason:* capacity.
*Impact:* 2C cannot start from this document alone. *Next action:* `Stage-002-Master-Data-Reassessment.md`.

## 5. P0-14 — outputs

| # | Document | Status |
|---|---|---|
| 1 | `Stage-002-Architecture-Review-and-Improvement-Proposal.md` | **Completed** |
| 2 | `Stage-002-Dependency-Roadmap.md` | **Not Started** — sequencing corrections are in review §5 |
| 3 | `Stage-002-Screen-Forecast.md` | **Not Started** |
| 4 | `Stage-002-Engineering-Platform-Proposal.md` | **Completed** |
| 5 | `Stage-002-Enterprise-Platform-Framework-Proposal.md` | **Not Started** — decision recorded in §3 |
| 6 | `Stage-002-Master-Data-Reassessment.md` | **Not Started** — findings in §4 |
| 7 | `Stage-002-UX-UI-Governance-Proposal.md` | **Not Started** |
| 8 | `Stage-002-Risk-Register.md` | **Not Started** — risks are embedded in the two delivered proposals |
| 9 | `Stage-002-Improvement-Backlog.csv` | **Completed** — 18 rows: 3 Required Now, 4 Recommended Now, 5 Planned Later, 6 Rejected |
| 10 | `Stage-002-Requirement-Status.md` | **Completed** — this document |

**4 of 10 delivered.** *Reason:* capacity within one delivery, not a technical obstacle. The four delivered are the
ones Stage 2A's first implementation actually needs — the review that sets direction, the analyzer design that is the
first deliverable, the classified backlog, and this status. The six outstanding are planning artifacts for 2B/2C and
none blocks 2A.

*Business impact of the gap:* none — no implementation is authorised yet.
*Technical impact:* 2B (framework) and 2C (Master Data) cannot begin from these documents alone.
*Proposed next action:* deliver 2, 3, 5, 6, 7, 8 as a Phase 0 continuation, in that order — roadmap and screens first,
because they are what a reviewer needs to approve scope.

## 6. Phase 0 completion gate

| # | Gate | Met |
|---|---|---|
| 1 | Stage 1 baseline verified | **Yes** |
| 2 | Concurrent changes separated | **Yes** |
| 3 | Stage 2 architecture proposed | **Yes** |
| 4 | Unrequested improvements documented | **Yes** |
| 5 | Engineering guardrails designed | **Yes** |
| 6 | CI SQL-test enforcement designed | **Yes** |
| 7 | Platform Framework designed | **Partial** — approach chosen, contracts not written |
| 8 | Master Data fully reassessed | **Partial** |
| 9 | Every major module reassessed | **Yes** — 24 modules |
| 10 | UX/UI governance proposed | **No** |
| 11 | New screens forecast | **No** |
| 12 | Dependency roadmap defined | **Partial** |
| 13 | Risks and migration impacts documented | **Partial** — embedded, not consolidated |
| 14 | No major production implementation started | **Yes** |
| 15 | No SQL against `CrossBuyDB2` | **Yes** |
| 16 | Review-ready report delivered | **Yes** |

**Phase 0 is therefore PARTIAL — 11 of 16 gates met, 5 outstanding.** Phase 0 is **not** claimed complete.

**Stage 2 implementation has not started. Awaiting review.**
