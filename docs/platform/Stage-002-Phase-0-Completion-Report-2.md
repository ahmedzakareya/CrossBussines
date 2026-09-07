# Stage 2 — Phase 0 — Completion Report (continuation 2)

**Phase 0 remains PARTIAL — 10 of 16 gates met.** Not claimed complete.
**No production code. No schema change. No SQL against `CrossBuyDB2`. No accepted document rewritten.**

Supersedes `Stage-002-Phase-0-Completion-Report.md` (kept as the record of the previous continuation).

---

## 1. Delivered this continuation — 3 of 6, in the mandated order

| Order | Document | Status |
|---|---|---|
| 1 | **`Stage-002-Enterprise-Design-System-Proposal.md`** | **Completed** |
| 2 | **`Stage-002-Screen-Forecast.md`** | **Completed** — written *after* the Design System, as required |
| 3 | **`Stage-002-Risk-Register.md`** | **Completed** — 32 risks, 13 attributes each (CSV twin outstanding, §4) |
| 4 | `Stage-002-Domain-Catalog.md` | **Not Started** |
| 5 | `Stage-002-Business-Capability-Map.md` | **Not Started** |
| 6 | `Stage-002-API-and-Integration-Catalog.md` | **Not Started** |
| — | P0-C10 Required-Now designs | **Partial** |
| — | Cross-document reconciliation | **Partial** (§5) |
| — | This report | **Completed** |

The execution order was followed exactly, so the three delivered are the three that gate later work: the Design System
gates 2B and the screen forecast; the screen forecast gates 2C scope; the risk register gates review.

---

## 2. Source findings from this continuation

Both from complete inspection, not assumption.

**The design system finding — there is no token layer.** `crossbuy-brand.css` is **211 lines** and its 52 custom
properties are **Bootstrap/Metronic variable overrides** (`--bs-btn-bg`, `--bs-component-active-bg`). Brand colour is
applied by overriding a framework, not by owning a vocabulary. Alongside that: **11 layouts** (an 11-file change for a
header), **0 ViewComponents**, and **two timeline partials that already duplicate each other**
(`_DocTimeline`, `_DocEventTimeline`). That duplication is the strongest single evidence for the component layer, and it
is why the proposal **inverts the dependency**: CrossBuy tokens feed Metronic instead of overriding it.

**The screen-forecast finding — four of six guardrail screens should not be built.** A new authorization violation
**fails the build**; a developer sees it in build output in seconds, and a screen would be read later, if ever. CI
already *rejects* baseline additions, so there is nothing to review interactively. **Recommend 2, not 6** — the
Compliance Dashboard (because the baseline's *trend* is the one thing CI text cannot show, and the trend is what stops
the baseline becoming permanent) and Baseline Debt as a tab of it. This mirrors the accepted decision to build 2
analyzers rather than 20.

---

## 3. Accepted corrections — recorded, and now consistent everywhere

The six authoritative corrections are recorded through the accepted R18/R19 mechanism and propagated:

| Correction | Where recorded |
|---|---|
| WMS = **Partial Platform Build** over `BinLocation`/`BinStock` | backlog R18 · Master Data §0 · roadmap · **Risk R-19 (reframed: foundation *underused*, mitigation is *extend* not *build*)** |
| Manufacturing = **major extension** over `ManufRoutingOp`/`ManufWorkCenter`/`ManufPlan`/`ManufPlanDemand` | backlog R19 · Master Data §0 · **Risk R-20** |
| Multi-barcode exists (`ItemBarcode`) | Master Data §0/§2 · **Screen Forecast marks Barcode Center as R, not N** · Risk R-09 |
| Units/conversions exist (`UnitOfMeasure`, `UoMConversion`) | Master Data §2 · **Forecast marks Unit Conversion Designer R, not N** · Risk R-21 |
| Composite foundations exist (`ItemComponent`, `CompositeType`, `ProductionMethod`) | Master Data §3 · **Forecast marks Composite Designer R** · Risk R-18 |
| Master Data still precedes WMS/Manufacturing | roadmap §2 · unchanged |

**Genuinely new in 2C, after correction: Unit Sets, Packaging, Attributes, Variants** — four concepts, not a platform.

---

## 4. Partial and Not Started — exact detail

**P0-C6 Domain Catalog — Not Started.**
*Missing:* 28 domains × 16 attributes, source-backed.
*Reason:* capacity. *Impact:* documentation only; roadmap places it in **2D**, so nothing is blocked.
*Next step:* **do not hand-author it.** Extend `scan-architecture.ps1` (already modernised in Stage 1 F1) to emit
`Domain-Inventory.csv` mapping DbSet → controller → service → domain from source. Hand-enrich purpose and maturity
only. This is R-27's own stated mitigation — a hand-maintained catalog of 224 DbSets goes stale and is then trusted.

**P0-C7 Business Capability Map — Not Started.**
*Missing:* 22 end-to-end flows × 14 attributes.
*Reason:* capacity. *Impact:* none immediate; **but it is the artefact most likely to reveal a missing integration
point**, so it has analytical value beyond documentation.
*Next step:* trace each flow to real endpoints and events rather than describing an ideal; a flow that cannot be traced
to source is a gap, and that is the finding.

**P0-C8 API and Integration Catalog — Not Started.**
*Missing:* 289 API actions, 3 hubs, 8 workers, 56 SQL scripts, external connectors × 13 attributes.
*Reason:* capacity. *Impact:* none immediate (2D).
*Next step:* generate from source — the scanner already produces `Endpoint-Inventory.csv` with route, auth attributes
and API flag, so most columns exist and need projecting, not authoring. **Never document secrets.**

**P0-C10 Required-Now — Partial.**
Treatments are defined across the roadmap, framework contracts, Master Data document and risk register (R1 → R-04,
R2 → R-05/R-06, R3 → R-07/R-08, **R4 → R-09/R-10/R-11**). *Missing:* the four standalone designs — R1 coexistence /
read-precedence / dual-write / verification / rollback; R2 acknowledgement + hardening options + console
representation; R3 per-constraint remediation alternatives; R4 formal source-of-truth proposal.
*Impact:* **R3 is a 2A deliverable and R1 a 2B gate**, so both need their designs before those phases start. R4 gates
2C.
*Next step:* one document per improvement, **R3 first** (2A), then R1, R4, R2.

**Master Data source-of-truth (barcodes / images / commerce presentation) — Partial.**
*Delivered:* the problem, evidence, authoritative-source decision and migration posture — `ItemBarcode` authoritative
with read precedence and the column retained; `ItemImage` authoritative with a primary designation; commerce
presentation separated per channel with a read-compatible view. All in Master Data §0/§2/§5 and Risks R-09/10/11, with
**no data movement in Phase 0**.
*Missing:* the field-by-field mapping, GS1 semantics, supplier-barcode attribution, channel model, and the structural
tests that prevent divergence.
*Next step:* fold into the R4 document.

**Risk Register CSV twin — Not Started.** The Markdown register is authoritative; the CSV write failed on shell
quoting. *Next step:* generate the CSV **from** the Markdown so the two cannot diverge.

---

## 5. Cross-document reconciliation

| Check | Result |
|---|---|
| Terminology | **Consistent** — Item/Product/Service/Asset/Kit/Bundle/Sales BOM/Manufacturing BOM/Recipe/Configurable defined once in Master Data §3 and used unchanged in the forecast |
| Stage numbers | **Consistent** — 2A/2B/2C/2D, Stages 3–10 |
| Dependencies | **Consistent** — Design System → 2B → 2C is now asserted in the roadmap, the Design System proposal and the forecast |
| Classifications | **Consistent after correction** — WMS partial, Manufacturing extension, nothing classified Replace |
| Screen counts | **Consistent** — 31–45 for Stage 2 (2–4 / 11–15 / 18–26); ranges, not invented exact counts |
| Risk IDs | **Consistent** — R-01…R-32, distinct from improvement IDs R1…R21 |
| Required Now | **Consistent** — R1–R4 appear in roadmap, backlog and register with the same treatment |
| Correction pointers | **Present** — accepted documents unaltered; Master Data §0 and backlog R18/R19 carry the corrections |
| Corrected WMS/Manufacturing classification used everywhere | **Yes** (§3) |

**One naming collision to fix:** improvement IDs (`R1`, `R18`) and risk IDs (`R-01`, `R-19`) are visually similar.
*Recommend* renaming improvements to `IMP-nn` in a future pass. Flagged rather than changed, because renaming IDs in
accepted documents would be rewriting them.

---

## 6. Dependency refinement requiring approval — Financial vs Manufacturing

The brief's refinement is **accepted and supersedes my earlier recommendation** to move the whole Financial Operations
Platform ahead of Manufacturing. My version was too coarse: Manufacturing needs *costing contracts*, not treasury.

**Refined sequence:** Master Data → **Financial Posting & Manufacturing Costing Contracts** → WMS + Manufacturing
planning/execution → full Financial Operations Platform → advanced costing integration.

**Minimum financial foundation before Manufacturing execution:** account mapping · posting rules · inventory valuation
contracts · WIP accounting contracts · standard cost · actual cost · variance posting · overhead allocation ·
transaction boundaries (`ScopedTx`, in-transaction `RecordAsync` before commit, no swallow) · BusinessEvents ·
rollback behaviour proven by SQL Server tests.

**Explicitly NOT required first:** treasury, collections, credit management, bank connectivity.

*Status:* recorded here as a refinement requiring approval. The accepted roadmap is **not** rewritten; it carries this
by reference.

---

## 7. Recommended Stage 2A and 2B scope

**Stage 2A (developer-facing, no user-visible value — stated honestly):** Roslyn Authorization Analyzer (CBA001–006)
with the dogfood gate reproducing **157/88/143** before enforcing · shrink-only baseline of 143 · SQL Evidence CI
guardrail (required tests fail if skipped, Razor enabled in acceptance, leftover scratch DBs fail, four-state
reporting, 3 identical runs) · **R3** EF-model measurement · Engineering Compliance Dashboard (**2 screens, not 6**).

**Stage 2B:** design tokens + Tier 1 components + 13 page templates · Business Object Registry + capability metadata ·
**unified attachments** (collapses three file stores — highest-value single item) · comments · timeline · audit ·
approval binding inheriting the `HierarchyDefect` contract · notification binding · **R1** role consolidation *(gate)* ·
**R2** bootstrap-open governance · **8 screens**.
**Deferred out of 2B:** Search, Related Objects, Workflow Designer, Escalation Designer, File Workspace, Followers,
tags, retention, portal exposure — the two designers alone are High complexity and would double the phase.

---

## 8. Remaining unknowns

1. **How much existing data violates the assumptions of the corrected model** — how many "variants as separate items"
   exist, how many items have divergent `Item.Barcode` vs `ItemBarcode`. **Measurable, not yet measured**, and it sizes
   2C.
2. **The true count of SQL-Server-incompatible relationships** — one is known; R3 exists to measure the rest before any
   decision.
3. **Whether `BinLocation`/`BinStock` are actually used in production data** or are dormant scaffolding — this changes
   Stage 4 from *extend* to *adopt*.
4. **Whether the parallel team will commit**, which determines whether our baseline is stable (R-29).

---

## 9. Phase 0 final gate

| # | Gate | Met |
|---|---|---|
| 1 | All six outstanding documents complete | **No** — 3 of 6 |
| 2 | Design System approved as a 2B dependency | **Yes** |
| 3 | Screen forecast uses the Design System | **Yes** |
| 4 | Risk Register includes corrected source findings | **Yes** — R-09/10/11/19/20/21 |
| 5 | Domain Catalog source-backed | **No** — approach defined, not built |
| 6 | API Catalog has a regeneration approach | **Partial** — approach defined, catalog not built |
| 7 | Capability Map complete | **No** |
| 8 | P0-C10 complete | **Partial** |
| 9 | Master Data source-of-truth proposals complete | **Partial** |
| 10 | Stock-valuation invariance is a mandatory gate | **Yes** — Risk Register §4, nine invariants, `[RequiredEvidence]` |
| 11 | WMS classified as partial platform build | **Yes** |
| 12 | Manufacturing classified as major extension | **Yes** |
| 13 | Cross-document terminology consistent | **Yes** (§5) |
| 14 | No production implementation started | **Yes** |
| 15 | No SQL against `CrossBuyDB2` | **Yes** |
| 16 | Complete Phase 0 report delivered | **Yes** |

**10 of 16 met. Phase 0 remains PARTIAL.**

**Recommended next:** R3 design (2A blocker) → R1 design (2B gate) → R4 source-of-truth → Domain + API catalogs
**generated from source** → Capability Map → R2 → Risk CSV.

**Stage 2A implementation has not started. Awaiting review.**
