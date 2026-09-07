# Stage 2 — Screen Forecast

**Forecast only. No screen implemented.** Fulfils P0-C2. Uses the patterns and tiering in
`Stage-002-Enterprise-Design-System-Proposal.md` (written first, as mandated).

**Counts are ranges.** Exact counts are not invented; where a range is wide the reason is stated.

| Group | Range | Phase |
|---|---|---|
| Engineering guardrails | **2–4** (not 6 — see §1) | 2A |
| Platform framework | **11–15** | 2B |
| Master Data | **18–26** | 2C |
| **Stage 2 total** | **31–45** | |

Legend — **Cls**: N new / R redesign · **Type**: D desktop / M mobile / O operational · **UX**: L/M/H complexity.

---

## 1. Engineering guardrail screens — recommend 2, not 6

The brief asks which are required immediately and warns against building dashboards where CI output suffices. **Four
of the six proposed screens are better as CI output**, and building them would be the UI equivalent of the 18
speculative analyzers already rejected.

| Screen | العربية | Purpose | Verdict |
|---|---|---|---|
| **Engineering Compliance Dashboard** | لوحة الالتزام الهندسي | Baseline size + trend, diagnostics by module, CI evidence status | **BUILD (2A).** The baseline's *shrinkage over time* is the one thing CI text cannot show, and it is what stops the baseline becoming permanent. |
| **Baseline Security Debt** | الدين الأمني الأساسي | The 143 known-unprotected actions, owner, target phase | **BUILD (2A)** — as a tab of the dashboard, not a separate screen. It is the work queue for Waves 2–6. |
| Authorization Violations | مخالفات التصريح | New violations | **CI output only.** A new violation *fails the build* — a developer sees it in build output within seconds. A screen would be read later, if ever. |
| Analyzer Diagnostic Details | تفاصيل التشخيص | File/method/route/reason | **CI output only.** The diagnostic already carries file+line; an IDE shows it inline. |
| CI Evidence Status | حالة أدلة الاختبار | Passed/Failed/Skipped/NotDiscovered, leftover scratch DBs | **Dashboard tile, not a screen.** |
| Suppression Review | مراجعة الاستثناءات | Review baseline additions | **CI output only** — CI *rejects* additions, so there is nothing to review interactively. |

**Engineering Compliance Dashboard** — roles: platform admin, tech lead · actions: filter by module, export, open source
location, mark target phase · deps: analyzer + baseline · **N** · **D** · auth: `PlatformOps` (admin-only; it is an
attack map) · objects: none (build artefacts) · 2A · **priority 1** · UX **M** · audit: baseline changes logged ·
reporting: debt trend over time · AI: cluster diagnostics by root cause — *later, not 2A*.

---

## 2. Platform framework screens (2B) — 11–15

| Screen | العربية | Purpose | Roles | Key actions | Cls | Type | UX | Auth |
|---|---|---|---|---|---|---|---|---|
| **Business Object Registry** | سجل كائنات الأعمال | Browse registered objects, codes, ownership mode | platform admin | inspect, export | N | D | M | `PlatformOps` |
| **Capability Configuration** | إعداد القدرات | Enable/disable capabilities per object per company | platform admin | toggle, acknowledge | N | D | **H** | `PlatformOps` + **event on change** |
| **Approval Center** | مركز الموافقات | Act on pending approvals across all objects | every employee | approve, reject, delegate, comment | N | D+M | **H** | own queue only |
| **Notification Center** | مركز التنبيهات | All notifications, read state, preferences | every employee | read, mute, configure | R | D+M | M | own only |
| **Object Timeline** | السجل الزمني | Chronological activity for one object | varies by object | filter, deep-link | **R** | D | M | **inherits parent object** |
| **Universal Comments** | التعليقات | Discussion on any object | varies | comment, mention, resolve | **R** | D+M | M | inherits parent |
| **Universal Attachments** | المرفقات | Files on any object | varies | upload, preview, version, delete | **R** | D+M | M | **inherits parent — no static path** |
| **Object Audit & History** | التدقيق والتاريخ | Field-level change history | auditor, admin | filter, compare, export | N | D | M | inherits + audit right |
| **Related Objects Explorer** | الكائنات المرتبطة | Graph of linked objects | power users | navigate, link, unlink | N | D | **H** | **trimmed per target** |
| **Enterprise Search** | البحث الموحد | One search across objects | every employee | search, filter, jump | N | D+M | **H** | **trimmed before ranking** |
| **File Workspace** | مساحة الملفات | Browse the unified file store | every employee | browse, move, share | R | D | M | per-object inheritance |
| **Workflow Designer** | مصمم سير العمل | Define transitions and approval steps | process owner | design, version, publish | N | D | **H** | `PlatformOps` |
| **Escalation Policy Designer** | مصمم سياسات التصعيد | Time-based escalation rules | process owner | design, test, publish | N | D | **H** | `PlatformOps` |
| Followers & Subscriptions | المتابعون والاشتراكات | Follow objects | every employee | follow, configure | N | D | L | own only |
| Task Linking | ربط المهام | Link tasks to objects | every employee | link, create task | R | D | L | inherits both |

**Recommended 2B content — 8 of these:** Registry, Capability Configuration, Approval Center, Notification Center,
Timeline, Comments, Attachments, Audit. **Deferred to 2B-late/2D:** Search, Related Objects, Workflow Designer,
Escalation Designer, File Workspace, Followers, Task Linking — the two designers alone are High complexity and would
double the phase.

**Marked R (redesign) deliberately:** Timeline replaces the **two existing partials** (`_DocTimeline`,
`_DocEventTimeline`); Comments/Attachments consolidate existing per-module implementations; Notification Center
extends `_NotificationBell`. These are consolidations of what exists, not new surface.

**Audit requirement for all:** capability enable/disable and every approval decision are audited.
**AI opportunities:** summarise a timeline; suggest approvers from history; cluster related objects — **all gated on
permission trimming existing first** (Risk R-24/R-25), so none in 2B.

---

## 3. Master Data screens (2C) — 18–26

The range is wide for one evidenced reason: **the Product Workspace's 15 areas may ship as one screen with 15 tabs
(counted as 1) or as a shell plus separately-routable areas (counted as up to 15).** I recommend **one workspace with
tabbed areas** — the record-workspace pattern — which puts the realistic count near the low end.

### 3.1 Core

| Screen | العربية | Purpose | Cls | Type | UX | Priority |
|---|---|---|---|---|---|---|
| **Product Workspace** (15 tabbed areas) | مساحة عمل المنتج | Single record: Overview · Identity · Classification · Units & Packaging · Attributes & Variants · Composition · Inventory · Purchasing · Sales · Financial · Manufacturing · Logistics · Media & Documents · Analytics · Timeline | **R** | D (+M read) | **H** | **1** |
| **Product List** | قائمة المنتجات | Find, filter, bulk-act | **R** | D | M | **1** |
| **Category Management** | إدارة التصنيفات | Tree of `ItemCategory` | R | D | M | 2 |
| **Barcode Center** | مركز الباركود | Multi-barcode, primary designation, GS1, supplier codes, duplicate detection | **R** | D | **H** | **1** |
| **Unit Sets** | مجموعات الوحدات | **New concept** — which units are legal per item | **N** | D | M | **1** |
| **Unit Conversion Designer** | مصمم تحويل الوحدات | Conversion graph, cycle prevention | **R** | D | **H** | **1** |
| **Packaging Management** | إدارة التعبئة | **New** — pack hierarchy distinct from unit | **N** | D | **H** | **1** |
| **Attribute Designer** | مصمم الخصائص | **New** — attribute definitions | **N** | D | M | 2 |
| **Variant Designer** | مصمم المتغيرات | **New** — variant matrix generation | **N** | D | **H** | 2 |
| **Composite Product Designer** | مصمم المنتج المركب | Kit / Bundle / Sales BOM over `ItemComponent` + `CompositeType` | **R** | D | **H** | 2 |
| Recipe Designer | مصمم التركيبات | Manufacturing BOM with yield/loss | R | D | H | 3 |

**Barcode Center and Unit Conversion Designer are R, not N** — `ItemBarcode` and `UoMConversion` already exist
(accepted corrections 3 and 4). Only Unit Sets, Packaging, Attributes and Variants are genuinely new.

### 3.2 Rules, governance, data quality

| Screen | العربية | Cls | UX | Priority |
|---|---|---|---|---|
| Serial & Batch Rules | قواعد التسلسل والدفعات | R | M | 2 |
| Product Financial Mapping | الربط المالي | R | M | 2 |
| Inventory Rules · Procurement Rules · Sales Rules · Manufacturing Rules · Logistics Rules | القواعد (٥ شاشات) | R×4, N×1 | M | 3 — **recommend these live as Workspace tabs, not separate screens** |
| **Product Lifecycle** | دورة حياة المنتج | **N** | M | **1** — no lifecycle exists beyond `IsActive` |
| **Master Data Approval Queue** | طابور موافقات البيانات | **N** | M | 2 — consumes 2B Approval Center |
| **Duplicate Resolution** | معالجة التكرار | **N** | **H** | **1** — no duplicate detection exists |
| **Master Data Quality Dashboard** | لوحة جودة البيانات | **N** | M | 2 |
| **Product Import** | استيراد المنتجات | **N** | **H** | **1** |
| **Import Validation** | مراجعة التحقق | **N** | **H** | **1** |
| Product Media & Documents | الوسائط والمستندات | R | M | 2 — Workspace tab using 2B attachments |

**Authorization across Master Data:** read `inv:read` · edit `inv:manage` · **pricing `acc:post`** (a margin is a
financial decision) · **financial mapping `acc:manage`** · approval per 2B binding. Every screen resolves company via
`IRequestCompanyResolver` — and `InventoryController`'s **267 hardcoded company references** must be swept **before**
these screens land (roadmap 2C, own commit).

**Audit:** every field change on `Item`, every barcode add/retire, every lifecycle transition, every approval.
**Reporting:** completeness %, duplicate rate, items missing barcode/unit/price/mapping, lifecycle ageing.
**AI opportunities:** duplicate detection by name/attribute similarity (**highest-value, evidence-backed** — no
detection exists today); category suggestion; attribute extraction from descriptions; import column mapping. All
require permission trimming first.

---

## 4. Screens explicitly NOT forecast for Stage 2

Security Administration Console (**blocked on R1** — a console over two role systems shows half the grants), WMS
screens (Stage 4), Manufacturing scheduling (Stage 5), CX/case screens (Stage 7), report builder (Stage 9), portal
screens (Stage 7+). Listed so their absence is a decision, not an omission.

---

## 5. Design System dependency — the concrete link

Every screen above uses Tier 1 components and one of the 13 page patterns:

| Pattern | Screens using it |
|---|---|
| Record workspace | Product Workspace |
| List page | Product List, Baseline Debt, File Workspace |
| Dashboard | Compliance, Quality |
| Setup screen | Unit Sets, all Rules screens, Capability Configuration |
| Approval queue | Approval Center, Master Data Approval Queue |
| Designer | Unit Conversion, Packaging, Attribute, Variant, Composite, Recipe, Workflow, Escalation (**8 screens**) |
| Import wizard | Product Import |
| Validation review | Import Validation, Duplicate Resolution |
| Diagnostic page | (CI output instead — §1) |
| Split view | Enterprise Search, Related Objects |
| Details drawer | Product List, Approval Center |

**The designer pattern serves 8 of ~40 screens** — the single highest-leverage pattern in the system and the strongest
argument for the Design System preceding 2C. Building eight designer screens without a shared canvas pattern would
produce eight different canvases.
