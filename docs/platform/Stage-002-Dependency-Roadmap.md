# Stage 2 — Dependency Roadmap

**Analysis only. No implementation. No SQL against `CrossBuyDB2`.**
Companion to `Stage-002-Architecture-Review-and-Improvement-Proposal.md` (accepted, not rewritten).

---

## 1. Verdict on the candidate sequence

The candidate sequence is **accepted with three changes**, each dependency-driven rather than preference-driven.

| Change | From | To | Why |
|---|---|---|---|
| **2D moves earlier, and splits** | Design System after Master Data | **Design tokens + core components in 2B; catalogs stay 2D** | Master Data (2C) is the largest screen delivery in Stage 2 — a Product Workspace of ~15 areas. Building it *before* tokens exist means building it twice. The *catalogs* (domain, capability, API, event) are documentation and can stay late. |
| **R1 role consolidation is a 2B gate, not a 2A nice-to-have** | unscheduled | **2B, blocking Batch D2** | Accepted conclusion #4. A console over two role systems shows half the grants. |
| **Stage 6 (Financial Ops) moves ahead of Stage 5 (Manufacturing)** | 5 then 6 | **consider 6 then 5** | See §4. Manufacturing's costing consumes financial posting rules; the reverse dependency does not exist. Flagged as a **recommendation for owner decision**, not applied unilaterally. |

**Final recommended sequence:** 2A → 2B → 2C → 2D → 3 (HR) → 4 (WMS) → **6 (FinOps)** → **5 (Manufacturing)** →
7 (CX) → 8 (CRM) → 9 (Reporting/AI) → 10 (Ecosystem).

---

## 2. The six mandated dependency statements

| Dependency | Direction | Evidence | Consequence if violated |
|---|---|---|---|
| **Master Data → WMS** | 2C before Stage 4 | WMS models bins, putaway and picking **per packaging unit and per item behaviour**. `Item` today has no packaging concept distinct from unit, and no variant model. | Bin/packaging/picking logic built against a model that then changes. Rework is proportional to WMS size. |
| **Master Data → Manufacturing** | 2C before Stage 5 | Manufacturing BOM exists; **sales BOM / kit / bundle do not**, and are currently conflated. Routing and capacity attach to item behaviour. | Routing and scheduling built on a conflated BOM concept; separating it later touches production data. |
| **Platform Framework → HR / WMS / Manufacturing** | 2B before Stages 3–5 | All three need **attachments, approvals and audit on day one**. Three file stores already exist (`FileManager`, HR docs, project files) — that duplication is exactly what happens without the framework. | A fourth, fifth and sixth attachment implementation. |
| **PlatformRoleAssignments consolidation → Security Console** | R1 before Batch D2 | Wave 1 fixtures had to seed **both** `AccountingUserRoles` and `PlatformRoleAssignments` for one decision to be meaningful. | A console that displays half the grants — worse than none, because it is trusted. |
| **Design System → large-scale screen delivery** | tokens in 2B before 2C | 327 existing views; Master Data alone forecasts ~26 screens. | Every 2C screen becomes a retrofit candidate. |
| **Reporting/AI → Search + Events + Registry + permission trimming** | Stage 9 last | AI endpoints are authentication-only today; no search exists; 13 registry codes, ~2 entities onboarded to events. | An AI that answers from data the asker may not see. This is the single highest-severity sequencing risk in the plan. |

---

## 3. Stage detail

### Stage 2A — Engineering Guardrails

| | |
|---|---|
| **Prerequisites** | None. Stage 1 closed. |
| **Position** | First, because Stage 1's two most expensive failures were *measurement* failures (scanner misread 38/40; 46 tests falsely "skipped"). Everything after 2A is measured by these tools. |
| **Deliverables** | Roslyn Authorization Analyzer (CBA001–006) · shrink-only baseline of 143 · SQL Evidence CI guardrail · EF-model measurement tests · Engineering Compliance Dashboard |
| **User-visible value** | **None — stated honestly.** Developer-facing phase. |
| **Technical value** | Waves 2–6 become self-verifying instead of self-reported. |
| **Database impact** | None. |
| **Migration** | None. |
| **Screens** | 1–6 (dashboard + diagnostics; see forecast) |
| **APIs** | None. |
| **BusinessEvents** | None. |
| **Testing** | Analyzer unit tests per supported pattern (10 positive / 10 negative minimum); **dogfood gate: must reproduce 157/88/143 before enforcing**. |
| **Risks** | Analyzer false positives block the build → Warning-first, generous ambiguity, dogfood gate. Baseline becomes permanent → CI rejects additions, dashboard shows trend. |
| **Complexity** | Medium |
| **Gates** | Reproduces 157/88/143 · baseline cannot grow · required SQL tests fail if skipped · Razor enabled in acceptance · 3 identical suite runs |
| **Maturity dimensions** | Testing and Reliability; Deployment and Operations |
| **Consumed by** | Every later stage |

### Stage 2B — Enterprise Platform Framework (+ design tokens, + R1, + R2)

| | |
|---|---|
| **Prerequisites** | 2A (so new framework code is analyzer-checked from day one) |
| **Position** | Before any business platform, because HR/WMS/Manufacturing all consume the same capabilities. |
| **Deliverables** | Business Object Registry + capability metadata · `BusinessObjectRef` · relationships · timeline projection · **universal attachments** · universal comments · followers/tags · task-link contract · workflow + approval binding · escalation · notification binding · **R1 role consolidation** · **R2 bootstrap-open governance** · design tokens + core components |
| **User-visible value** | Approval Center · Notification Center · unified attachments and comments on onboarded objects · object timeline |
| **Technical value** | Eliminates the three-file-store and two-role-system duplications; one authorization path for capabilities. |
| **Database impact** | New platform tables (registry metadata, relationships, comments, attachments, followers, tags, workflow, approvals, escalation). **Additive; no business table altered.** |
| **Migration** | R1 read-both/write-new for role rows; attachment/comment back-fill is **opt-in per module**, not a big-bang move. |
| **Screens** | 10–16 |
| **APIs** | Capability APIs per object (timeline, comments, attachments) |
| **BusinessEvents** | Onboard 5–10 entities; capability changes raise events |
| **Testing** | Per-capability contract tests · company/branch semantics per capability · **permission trimming proven before any capability is exposed** · R1 parity tests (identical decisions before/after) |
| **Risks** | Scope sprawl (22 capabilities) → ship registry + attachments + approvals first, defer tags/followers/retention. R1 touches live authorization data → parity tests + coexistence window. |
| **Complexity** | **High** — the largest single phase in Stage 2 |
| **Gates** | R1 parity green · bootstrap-open explicit + audited · attachments single-store with authorization tests · Approval Center usable end-to-end on ≥1 real object |
| **Maturity dimensions** | Security; Platform Kernel; Workflow and Approvals |
| **Consumed by** | 2C, 3, 4, 5, 6, 7, 8, 9 |

### Stage 2C — Enterprise Master Data Platform

| | |
|---|---|
| **Prerequisites** | 2B (capabilities + tokens) |
| **Position** | Before WMS and Manufacturing (accepted conclusion #3; evidence §2). |
| **Deliverables** | Item/Product/Service/Asset type separation · packaging distinct from unit · attributes + variants · kit/bundle **separated from manufacturing BOM** · barcode centre (multi-barcode, GS1, supplier) · serial/batch/expiry rules · lifecycle + governance + duplicate detection · Product Workspace (~15 areas) · import + validation |
| **User-visible value** | **Highest in Stage 2.** A coherent product workspace replacing scattered item screens. |
| **Technical value** | Removes the model limitation that would otherwise be inherited by WMS, Manufacturing and PIM. |
| **Database impact** | New: variants, attributes, packaging, barcodes, kit/bundle, lifecycle. Existing `Item` **extended, not replaced**. |
| **Migration** | **The principal risk of Stage 2.** `InventoryController` holds **267** hardcoded company references — the largest concentration in the codebase — and existing "variants as separate items" data must be interpreted, not blindly converted. Recommend a **read-compatible** model with a reversible, opt-in conversion. |
| **Screens** | ~26 (see forecast) |
| **APIs** | Product read/write, import, validation, duplicate check |
| **BusinessEvents** | `Item.Created/Updated/Approved/Discontinued`, `Variant.*`, `Barcode.*` |
| **Testing** | Model round-trip · conversion reversibility · every rule (serial/batch/expiry) · **stock valuation unchanged** (moving average must not move) |
| **Risks** | Data interpretation; stock valuation regression; screen volume. |
| **Complexity** | **Very High** |
| **Gates** | Existing stock valuation byte-identical · no `Item` row loses data · Product Workspace covers every field the old screens exposed · duplicate detection measured |
| **Maturity dimensions** | Business Object Coverage; ERP Business Coverage |
| **Consumed by** | 4 (WMS), 5 (Manufacturing), 8 (CRM/PIM), 9 (analytics) |

### Stage 2D — Architecture Catalogs + Design System completion

Prerequisites 2B/2C. Documentation and component-library completion: domain catalog, capability map, API/event
catalogs, component gallery, pattern library. **No user-visible business value**; value is that Stages 3+ start from a
catalogue instead of a search. Low complexity, low risk. Moves **Architecture** maturity only.

### Stage 3 — Enterprise HR / HCM

Prerequisites: 2B (attachments/approvals/audit — HR needs all three immediately). **Position: first business platform**
because HR is self-contained (it does not feed WMS or Manufacturing), so it is the safest first consumer of the new
framework — a deliberate risk-reduction choice. Deliverables: org structure **with company ownership** (today
`Hierarchical` has no `CompanyID`), payroll basis, self-service, documents on the unified store, approvals on the
engine. Database: additive + a company column on the org tree (**migration-sensitive**). Screens 20–30. Risks:
payroll correctness — **no formula may change**; the org-tree company migration. Gates: payroll output identical
pre/post; hierarchy company-scoped with the C.1 defect handling preserved. Moves ERP Coverage, Security.

### Stage 4 — Enterprise WMS

Prerequisites: **2C mandatory**, 2B. First genuinely new capability (none exists today). Bins, zones, putaway,
picking, waves, counts, mobile scanning. Database: substantial new tables; `StockBalance` semantics **must not
change** — its `UPDLOCK/HOLDLOCK` path is Stage-1-proven and out of scope. Screens 25–40 incl. mobile/operational.
Risks: touching the stock writer (**forbidden**); mobile UX. Gates: stock invariants unchanged under F4-style
concurrency proofs; no new writer. Moves ERP Coverage, Business Object Coverage.

### Stage 6 — Financial Operations *(recommended before Manufacturing)*

Prerequisites: 2B. Treasury, cash forecasting, bank integration, payment runs, allocations. **Why earlier:**
Manufacturing costing consumes posting rules and cost centres; the reverse dependency does not exist. Also lower risk
than Manufacturing and moves the heaviest maturity dimension. Database additive. Screens 15–25. Gates: no change to
GL posting or reversal semantics. Moves ERP Coverage, Finance.

### Stage 5 — Smart Manufacturing *(recommended after FinOps)*

Prerequisites: **2C mandatory**, 2B, and ideally Stage 6. Routing, work centres, capacity, scheduling, shop-floor UI,
MES/APS. Highest complexity of any stage. Risks: costing correctness; scheduling is an optimisation problem that can
absorb unlimited effort — **scope it to finite scheduling first**. Gates: existing work-order costing unchanged.

### Stages 7–10

**7 CX** (prereq 2B + workflow): case management, SLA, call-centre console. **8 CRM** (prereq 7, 2C): pipeline,
campaigns, PIM-adjacent product data. **9 Reporting/Analytics/AI** (prereq 2B search + events + trimming):
**must be last of the capability stages** — an AI that answers from data the asker may not see is the highest-severity
sequencing risk. **10 Ecosystem**: integration marketplace, industry packs, developer platform — only after a stable
API contract exists.

---

## 4. Recommendation requiring an owner decision

**Should Financial Operations (6) precede Smart Manufacturing (5)?**

*For:* Manufacturing costing consumes posting rules; FinOps has no manufacturing dependency; FinOps is lower risk and
moves the heaviest maturity dimension sooner.
*Against:* if manufacturing throughput is the commercial driver, delaying it defers the revenue story.

This is a business-priority question, not an architectural one. **I recommend 6 before 5** and have flagged it rather
than silently reordering.

---

## 5. Cross-document agreement

| Claim | Also stated in |
|---|---|
| 2A = two guardrails only | Architecture Review §2.1; Engineering Platform §1 |
| Master Data before WMS/Manufacturing | Review §5; here §2 |
| R1 before Security Console | Review §3.1/§5; Risk Register R-04 |
| Registry + composition, not inheritance | Review §3; Framework Contracts §2 |
| No module warrants full rewrite | Review §4 |
| 143 / 908 / 57.60 baseline | verified in Review §1 |
