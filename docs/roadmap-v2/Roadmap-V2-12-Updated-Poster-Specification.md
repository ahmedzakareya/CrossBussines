# CrossBusiness Platform — Roadmap v2 — 12 Updated Poster Specification

## OWNER-APPROVED EDITION

**A specification for a later design step. No image is generated or edited in this increment.**

The owner-supplied *CrossBuy Comprehensive Roadmap* poster is the **structural reference**. This specification keeps its structure — banner, metrics strip, layered bands, phase timeline, bottom summary — and rebuilds the content from the verified baseline and the approved decisions. It is not the old poster with numbers swapped.

---

## 1. Identity — resolved

| Element | Value |
|---|---|
| Product name | **CrossBusiness Platform** |
| Arabic name | **منصة كروس بزنس** |
| Visual identity | **CrossBusiness Blue** — D-36 **APPROVED** |
| Green and gold | Permitted **inside** financial charts, accounting reports, profit-and-loss indicators and domain data visualizations. **Not** the shell identity. |
| Typography | Latin: Metronic 8 default sans · Arabic: a matching naskh with equal x-height |
| Direction | Full RTL/LTR parity — mirroring must be a layout flip, not a redraw |
| Subtitle EN | Owner-Approved Baseline and Roadmap v2 |
| Subtitle AR | خط الأساس المُعتمد وخارطة الطريق الإصدار الثاني |

**The earlier conflict is closed.** `crossbuy-brand.css` still applies green and gold globally; D-36 makes blue canonical and **D-37 governs the migration** — blue on new screens first, legacy pages migrated through an approved visual rollout. The poster shows the approved future identity. No CSS is changed in this increment.

## 2. Status legend — eight states

| # | State | EN | AR |
|---|---|---|---|
| 1 | Verified and Closed | Verified & Closed | مُتحقق ومُغلق |
| 2 | Foundation Complete | Foundation Complete | أساس مُكتمل |
| 3 | Implemented Not Activated | Implemented · Not Activated | مُنفذ وغير مُفعّل |
| 4 | Existing / Needs Integration | Existing · Needs Integration | قائم ويحتاج تكامل |
| 5 | Planned | Planned | مُخطط |
| 6 | Blocked by Decision | Blocked by Decision | مُعلّق بقرار |
| 7 | Blocked by Data | Blocked by Data | مُعلّق ببيانات |
| 8 | Deferred | Deferred | مُؤجل |

The legend appears **once, near the top**. Every card carries exactly one chip. A card without a chip is a specification error.

State 4 exists specifically so Tasks, Calendar and legacy Comm are not drawn as greenfield.

## 3. Sections, top to bottom

### S1 — Banner
Product name (EN over AR), subtitle, baseline date **2026-08-06**, legend strip, and the marker **Owner-Approved Edition**.

### S2 — Verified metrics strip

| Tile | Value | EN | AR |
|---|---|---|---|
| 1 | **391** | Actions Detected | إجراءات مُكتشفة |
| 2 | **157** | Attribute Secured | مؤمَّنة بالسمات |
| 3 | **91** | In-Body Secured | مؤمَّنة داخل الكود |
| 4 | **143** | Authorization Backlog | متأخرات الصلاحيات |
| 5 | **1502** | Tests Passed | اختبارات ناجحة |
| 6 | **0 / 0** | Failed / Skipped | فاشلة / متخطاة |
| 7 | **0 / 0 / 0** | CBA001 / CBA004 / CBA006 | بوابات التحليل |

Tile 4 must be visually distinct — a **backlog**, not an achievement. Render it in the attention treatment, never the tone of tiles 1–3 and 5–7. Print `157 + 91 + 143 = 391` in small type beneath the strip.

### S3 — Verified and Closed *(state 1)*
Foundation and Governance · **Stage 2A / B6** · Reporting Foundation · Dataset Layer · Communication Foundation · Construction C1 Commercial Foundation · Business Event Monitor · Hypermarket lane.

### S4 — Implemented but Not Activated *(state 3)*
Each card carries its test count **and an explicit "No UI" marker**:

* **Reporting Platform** — 221/221 · 12 tables · **No UI · no production dataset**
* **Communication Platform** — 274/274 · 14 tables · **Not activated · No UI**
* **Construction C1 schema and services** — 38/38 · 8 tables · **DDL applied nowhere · No UI**

This band is the poster's central honesty requirement. These three must never share a treatment with S3.

### S5 — Existing but Needs Integration *(state 4)*
**Tasks** (controller, 5 views, access service, 7 SQL slices, 2 hosted services) · **Calendar** (service, controller, slice) · **Legacy Comm** (active, dispatcher, 3 views — distinct from the Communication Platform) · **Projects/Contracting spine**.

Not greenfield. Not delivered. The state exists for exactly this.

### S6 — Not Started *(state 5)*
Report Studio UI · Communication UI · Security Console · Master Data · CRM B6 conversion · Batch C · Wave 2.

### S7 — Blocked *(states 6 and 7)*
**By decision:** Accounting.read breadth (D-01) · Inventory.read cost breadth (D-02) · CRM data breadth (D-03) · CRM owner scope (D-04) · legacy CSS migration (D-37) · AI governance (D-27).
**By data:** Construction M3/M9 measurement (D-20) · DocComments migration.

### S8 — First future product
A single prominent card: **CrossBusiness Workspace** — My Work, Tasks, Calendar, Approvals, Notifications, Mentions, Recent Activity, Timeline, Saved/Recent/Favourite Reports, Quick Actions, Personal and Team Dashboards.

With the constraint printed on the card: **owns no business data · adds no permissions**.

### S9 — First execution phase
A single prominent card: **R1 — Repository and Deployment Governance.** Sub-line: *one canonical SQL root · authored registry (manifest.json) · applied registry (PlatformSchemaHistory) · worktrees · protected integration branch.*

It must read as the gate everything else waits behind — not as one phase among sixteen.

### S10 — Platform layers
Four bands mirroring the target architecture — Experience, Business Domains, Shared Platforms, Governance & Security — each tinted by dominant state. **Governance is the only band predominantly state 1**; the layout should let that read at a glance.

### S11 — Roadmap phases R0 → R15
Horizontal timeline. R0 state 1. R1 emphasised as the gate. R2–R4 as the activation and first-product arc. R5–R15 planned.

Group into four arcs: **Reconcile** (R0) · **Govern & Activate** (R1–R2) · **Deliver** (R3–R6) · **Extend** (R7–R15).

Each node: id, short name, blocking decision ids. **No dates.** Complexity bands only.

### S12 — Bottom summary
Three columns:
1. **Proved** — 1502/0/0, B6 closed, two-writer discipline, isolation enforced.
2. **Built but dark** — three platforms, 533 tests, 34 tables, zero screens.
3. **Decided** — Workspace first · Blue canonical · Tasks/Calendar/Timeline ownership · Business Events pilot dataset · R1 is governance.

Closing line, EN and AR: *"A capability is not delivered until a user can reach it."* — *"القدرة لا تُعتبر مُنجزة حتى يستطيع المستخدم الوصول إليها."*

## 4. Colour usage

| Purpose | Treatment |
|---|---|
| Primary identity | **CrossBusiness Blue** |
| Verified (1) | Deepest saturation of the primary |
| Foundation (2) | Mid tone |
| Implemented Not Activated (3) | Mid tone **plus a visible "No UI" badge** |
| Existing / Needs Integration (4) | Neutral fill with a dashed accent edge |
| Planned (5) | Outline only, no fill |
| Blocked (6, 7) | The single attention accent — used **only** here and on metric tile 4 |
| Deferred (8) | Muted grey, dashed border |
| Green / gold | **Only** inside financial chart or accounting-report illustrations |

One accent colour. If everything is highlighted, the backlog tile stops meaning anything.

## 5. Rules for the design step

1. Use the owner-supplied poster as the structural reference; do not invent an unrelated design.
2. Do not edit the old poster by changing numbers — this is a new layout from a new dataset.
3. Every metric traces to `Roadmap-V2-01-Verified-Baseline.md`. No rounding, combining or estimating.
4. No card shows a capability as delivered when its `UIStatus` in the catalog is `Not started`.
5. Tasks, Calendar and legacy Comm use state 4 — never "Planned", never "Delivered".
6. Arabic and English are peers. The poster must be legible mirrored.
7. If a number changes before artwork, regenerate from the catalog rather than editing poster text.
8. **The image is not produced in this increment.**
