# Stage 2 — Phase 0 — CLOSURE REPORT

**Authoritative closure report. Phase 0 is CLOSED.**

**IMP-001 = Completed · IMP-002 = Completed · IMP-003 = Completed · IMP-004 = Completed · Stage 2A = Not Started.**

No production code changed · no schema changed · no migration executed · no SQL against `CrossBuyDB2` · no UI or
feature implemented.

Freeze record: `Stage-002-Phase-0-Freeze.md`. Execution plan: `Stage-002A-Execution-Roadmap.md`.

---

## 1. Executive summary

Phase 0 reassessed the platform after Stage 1's closure and produced implementation-ready designs for the four
Required-Now improvements. Its most valuable output was not the designs but **what measurement found**: a grant store
with no writer, financial posting open on unconfigured companies, three retail rules a routine cleanup would have
broken, and two of my own module classifications that were wrong. Fifty risks are now recorded from one generated
source. **Nothing in production was changed.**

## 2. Original objectives

Validate the Stage 1 baseline · reassess the architecture · design engineering guardrails · design the platform
framework · reassess Master Data · reassess every module · propose UX governance · forecast screens · define a
dependency roadmap · complete four Required-Now designs · record risks — **without** starting implementation.

## 3. Scope delivered

**30 documents · 10 generated CSV artifacts · 4 generators · 50 risks · 27 improvements · 4 of 4 Required-Now IMPs.**
Full inventory in the freeze record §1–§2.

## 4. IMP-001 — Role Source Consolidation

**The finding that reframed it:** `PlatformRoleAssignments` has **4 production readers and 0 production writers** —
writes exist only in 4 test files. So it is empty and unfillable in production, which means **HR and Projects resolve
through bootstrap-open**, and Wave 2's 52 HR/Identity gates would **allow everyone while their tests pass**.
**RISK-037, Critical.**

IMP-001 was therefore redefined as *Grant Management Capability → Legacy Role Migration → Authoritative Cutover →
Legacy Writer Shutdown*. Three sources migrate (Accounting, Inventory with `ScopeBranchId` preserved, CRM); memberships,
identity roles and POS do not. Coexistence never unions. **20/20.**
→ `Role-Source-Consolidation-Design.md` · `Platform-Grant-Writer-Design.md` · `IMP-001-Final-Delivery-Report.md`

## 5. IMP-002 — Bootstrap-Open Governance

**The finding:** two structurally different mechanisms. **Mechanism A** (Accounting, Inventory, CRM) returns `true`
*before* the action switch — **no per-action exclusion is possible**. Mechanism B (HR, Projects, Tasks, Communication)
lets modules exclude, and three do.

**So the three highest-financial-impact modules are the three that cannot exclude anything.** Fourteen
Never-Bootstrap-Open actions are open today, including journal posting, payments, currency override, stock documents and
all-warehouse access. Mechanism A is also **unlogged**. **RISK-040, Critical.** **20/20.**
→ `Bootstrap-Open-Governance-Design.md` · `Bootstrap-Open-Inventory.csv` (49 actions) ·
`IMP-002-Final-Delivery-Report.md`

## 6. IMP-003 — EF Relational Model Validation

**297 generated statements, 21 failures, 1 root cause** — `FK_Branches_CountriesLookup_CountryID` (SQL 1785) cascading
into 5 table failures and 15 index failures. Acting on the raw count of 21 would have scoped a project; the correct scope
is **one `.OnDelete(NoAction)` annotation**, justified by matching the database rather than satisfying a generator.

**More significant:** only **53 foreign keys across 231 entities** — most relationships are enforced by hand-written SQL,
so `EnsureCreated` can never be a deployment mechanism. And **cascade into financial data is exactly 3**, all
header→own-line: **reverse-never-delete is not violated by the model.** **7/7 gates.**
→ `EF-Relational-Model-Validation-Design.md` · `EF-Relationship-Inventory.csv` (generated, 53 rows)

## 7. IMP-004 — Master Data Source Consolidation

**18 sources classified.** `ItemBarcode` and `ItemImage` become authoritative; `Item.Barcode` and `Item.ImagePath` are
retained. **Three rules preserved byte-for-byte:** `ItemBarcode.UoMId` selects the sold unit · a missing conversion
**rejects** and is never factor 1 · an ambiguous barcode is **rejected**, never first-match. Composite revisions are
additive with existing rows as revision 1. **22/22.**
→ `Master-Data-Source-Consolidation-Design.md` · `Product-Workspace-UX-Contract.md` (16 areas) ·
3 generated matrices · `IMP-004-Final-Delivery-Report.md`

## 8. Cross-module architectural decisions

24 frozen decisions (freeze §3). The load-bearing ones: registry + composition **never** a universal base entity ·
memberships are not roles · POS exception upheld on mechanical grounds · coexistence never unions · bootstrap-open is
policy not a grant · design tokens feed Metronic not the reverse · two writers unchanged · **Master Data precedes WMS
and Manufacturing**.

## 9. Risk Register evolution

| Point | Total | C | H | M | L |
|---|---|---|---|---|---|
| First consolidation | 32 | 6 | 14 | 11 | 3* |
| After variant/composite findings | 34 | 6 | 14 | 11 | 3 |
| After IMP-001 | 39 | 7 | 17 | 12 | 3 |
| After IMP-002 | 45 | 8 | 20 | 14 | 3 |
| **After IMP-004 (frozen)** | **50** | **8** | **25** | **14** | **3** |

\* the first Markdown summary miscounted its own content (said 5 Critical while listing 6); the generated CSV corrected
it. **Both outputs now come from one generator**, so that class of error cannot recur.

## 10. Hypermarket conclusions

Assessment accepted as authoritative input. Retail classification resolved from its evidence: **Shared Retail Core +
Hypermarket Pack**. The backend is preserved. Its finding **F4** — weighted items and scale codes have **no admin screen
anywhere** — became RISK-049 and reclassified `IsWeighted`/`ScaleCode` as *New Capability Required*.

## 11. Master Data conclusions

Terminology resolved by one discriminating rule, **without restructuring stored values**. Four genuinely new concepts:
**Unit Sets · Packaging · Attributes · Variants**. Barcode Center, Unit Conversion Designer and Composite Designer are
**redesigns**, not new capabilities. Variant migration **cannot be sized** until measurement is approved (RISK-034).

## 12. Bootstrap Governance conclusions

Six-state model per (company, scope) with optional action-group granularity. **Missing configuration resolves to
`LegacyCompatibility`** so deployment changes nothing. Separate `BootstrapAccessPolicies` table plus append-only
history. **IMP-001 and IMP-002 interlock bidirectionally**: the Grant Writer must precede hardening (RISK-043), and
bootstrap governance must precede cutover (RISK-044).

## 13. EF model conclusions

Idempotent SQL remains the authoritative deployment mechanism. One incompatible relationship; three legitimate financial
cascades; 2 concurrency tokens across 231 entities (RISK-035). A four-assertion acceptance suite is specified,
**diagnostic-first** — a suite red on all 21 existing failures would be disabled within a week.

## 14. Security conclusions

**143 of 388 mutating actions still reach no authorization authority.** The two Critical blockers are RISK-037 (no grant
writer) and RISK-040 (financial modules cannot exclude). **Wave 2 must not ship before both are resolved**, or it
delivers gates that always allow while its tests pass.

## 15. Inventory conclusions

The stock engine is **strong and must not be touched** — writer, `UPDLOCK`/`HOLDLOCK`, batch/expiry, moving average, all
Stage-1-proven. **`BinLocation`/`BinStock` are operational**, participating in the stock writer's own movement
contracts — WMS is *Extend and Redesign* (CORR-001). `InventoryController` retains **267** hardcoded company references;
the Class A sweep must land in 2C as its own commit.

## 16. Manufacturing conclusions

Routing, work centres and planning **exist** (`ManufRoutingOp`, `ManufWorkCenter`, `ManufPlan`, `ManufPlanDemand`) —
Manufacturing is a **major extension**, not a rewrite (CORR-002). The missing piece is scheduling and capacity.
`ItemComponent` is **load-bearing across Stock, Pricing, POS and Manufacturing** with 20 write sites (RISK-033) and
**no revision history** (RISK-047).

## 17. Retail conclusions

Shared Retail Core + Hypermarket Pack. Engine mature, **administration absent**. Weighted-item and scale configuration
need a production surface before fresh food can be managed without SQL. Retail Core precedes Hypermarket UX.

## 18–19. Documents and CSV artifacts

**30 documents · 10 CSVs · 4 generators** — enumerated in freeze §1–§2. Every CSV and the Risk Register Markdown are
**generated**; hand-editing is prohibited.

## 20. Verification status

| Check | Result |
|---|---|
| Application build, **Razor enabled** | **0 errors** |
| Full suite, **SQL evidence enabled** | **766 total · 766 passed · 0 failed · 0 skipped** |
| Risk Register deterministic | **verified** — `e2992de2…` / `456d314b…` |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |
| Frozen documents modified | **none** |

## 21. Remaining limitations

1. **Variant population unmeasured** (RISK-034) — Stage 2C cannot be sized.
2. **Barcode/image divergence extent unmeasured** — duplication confirmed, scale unknown.
3. **The three preserved retail rules are prose, not tests** — nothing mechanically prevents a factor-1 fallback or
   first-match resolution until the non-regression suite exists.
4. **RISK-041 unmitigated in practice** — bootstrap-open allows stay indistinguishable in logs until the
   decision-source contract exists.
5. **908 company constants remain**; only the 40 Wave 1 endpoints have a verified company source.
6. **`PlatformRoleAssignments` and `ProjectMembers` absent from `CrossBuyDB2`.**
7. **Parallel work uncommitted** — 66 untracked, 65 modified files (RISK-029).

## 22. Deferred work

Domain Catalog · Business Capability Map · API and Integration Catalog — all three **source-generated**, placed in 2D,
blocking nothing. Also deferred: layout consolidation (11 → 3), the 18 trigger-based analyzers, tags, followers,
retention, portal exposure, external principals.

## 23. Explicitly rejected ideas

Full rewrite of any module (no source evidence) · 20 analyzers in 2A (false positives train suppression) · blockchain /
digital twin / IoT (no business case) · low-code studio (prerequisites absent) · one universal base entity (divergent
persistence conventions) · fixing 908 company constants inside a feature wave (must be its own batch) · forcing POS into
the shared role table (circular dependency at lane login). **Reasons retained in `Improvement-Backlog.csv`.**

## 24. Frozen decisions

24 decisions, terminology, identifier ranges, completion reports, metrics and the verification baseline — all in
`Stage-002-Phase-0-Freeze.md`. **No frozen document may be rewritten; corrections require new documents with pointers.**

## 25. Phase 0 completion statement

| Gate | Status |
|---|---|
| IMP-001 / 002 / 003 / 004 Completed | **Yes** |
| All accepted reports referenced | **Yes** |
| Freeze document created | **Yes** |
| Execution roadmap created | **Yes** — `Stage-002A-Execution-Roadmap.md`, no dependency cycles |
| Business roadmap created | **Yes** — `Stage-002-Business-Value-Roadmap.md` |
| Metrics recorded | **Yes** (§3, freeze §7) |
| Verification completed | **Yes** (§20) |
| No implementation started | **Yes** |
| Stage 2A | **Not Started** |

**STAGE 2 PHASE 0 IS CLOSED.**

**Two decisions are required before Stage 2A begins**, both stated in the execution roadmap and neither of which I can
make: **approve the read-only measurement** (three items depend on it), and **decide whether the Roslyn analyzer and CI
guardrail become Batch 0** — they were the original evidence-backed 2A scope and are what makes later batches
self-verifying rather than self-reported.

**Awaiting approval. No production implementation has begun.**
