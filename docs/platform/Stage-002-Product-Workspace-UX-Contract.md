# Stage 2C — Product Workspace — UX Contract

**Design only. No screen implemented.** Uses the accepted `Stage-002-Enterprise-Design-System-Proposal.md`
(tokens, Tier-1 components, 13 page patterns) and the accepted `Stage-002-Master-Data-Source-Consolidation-Design.md`.

**Pattern:** *record workspace* — summary header · sub-navigation rail (16 areas) · contextual command bar ·
structured sections · side drawer for inspect-without-leaving · capability rail (timeline, comments, attachments) from
2B. Designer areas use the *designer* pattern; area 16 uses the *timeline* pattern; imports use *import wizard* +
*validation review*.

**Migration Impact** per area, per the mandated vocabulary: None · Additive · Compatibility Required · Shadow
Required · High-Risk Migration · Blocked by Data Measurement.

---

## 1. Area matrix — all 16

| # | Area | العربية | Migration Impact | Primary users | Pattern |
|---|---|---|---|---|---|
| 1 | Overview | نظرة عامة | **None** | everyone | summary header + KPI row |
| 2 | Identity | الهوية | **Compatibility Required** | Master Data Admin | structured sections |
| 3 | Classification | التصنيف | **Additive** | Product Manager | tree picker |
| 4 | **Units and Packaging** | الوحدات والتعبئة | **HIGH-RISK MIGRATION** | Master Data Admin | **designer** |
| 5 | **Attributes and Variants** | الخصائص والمتغيرات | **BLOCKED BY DATA MEASUREMENT** | Product Manager | **designer** + matrix |
| 6 | **Composition** | التركيب | **HIGH-RISK MIGRATION** | Manufacturing Engineer | **designer** + revision compare |
| 7 | Inventory | المخزون | Compatibility Required | Inventory Admin | structured sections |
| 8 | Purchasing | المشتريات | Additive | Purchasing Admin | structured sections |
| 9 | Sales | المبيعات | Compatibility Required | Sales Admin | structured sections |
| 10 | Financial | المالية | Compatibility Required | Finance Reviewer | structured + approval |
| 11 | Manufacturing | التصنيع | Compatibility Required | Manufacturing Engineer | structured sections |
| 12 | Logistics | اللوجستيات | Additive | Inventory Admin | structured sections |
| 13 | **Retail and Commerce** | التجزئة والتجارة | **Compatibility Required** | Retail Admin | structured + **decoder preview** (§2) |
| 14 | Media and Documents | الوسائط والمستندات | **Compatibility Required** | Product Manager | gallery + 2B attachments |
| 15 | Analytics | التحليلات | None | everyone | dashboard |
| 16 | Timeline and History | السجل والتاريخ | **Additive** | Auditor | **timeline + audit** |

**Universal states, defined once and inherited by every area** (Design System §5): loading = skeleton, never a
spinner-only page · empty distinguishes *no data* from *no matching filter* · error carries what failed, what to do, and
a correlation id · **no-permission renders as a first-class layout, never a blank** · **partial-permission shows the
permitted areas and marks the rest** — a Finance Reviewer sees Financial and reads the others · conflict shows
side-by-side with no silent overwrite (RISK-035) · unsaved changes blocks navigation.

**Universal audit:** every field change on `Item` is audited; areas 4, 6, 10 and 13 additionally require a **change
reason**, and 4/6/10 require **elevated four-eyes approval** because they can alter money or quantity.

**Universal AI opportunities (all gated on 2B permission trimming existing first):** duplicate detection by
name/attribute similarity — the highest-value one, since no detection exists today · category suggestion · attribute
extraction from descriptions · import column mapping.

---

## 2. Area 13 — Retail and Commerce (the previously missing area)

**Purpose:** the retail and channel behaviour of an item, including the two fields that currently have **no
administration surface anywhere** (RISK-049).

### 2.1 Operational Item data — stays on `Item`

| Field | Control | Validation |
|---|---|---|
| **`IsWeighted`** | toggle | requires a compatible UoM; enabling requires a `ScaleCode` |
| **`ScaleCode`** | text + uniqueness check | **filtered uniqueness preserved exactly**; live duplicate check |
| Scale barcode mode | select (embedded **weight** / embedded **price**) | must resolve unambiguously |
| Decimal precision | numeric | must match the currency/UoM scale; **KWD 3-decimal preserved** |
| Default UoM · allowed selling UoMs | pickers | every allowed UoM needs a conversion — **a missing conversion is rejected, never factor 1** (RISK-048) |
| PLU readiness | derived indicator | shows whether `ScaleCode` + mode resolve |
| Expiry / fresh-food data | `TrackExpiry`, shelf life | FEFO unaffected |
| Receipt description · shelf-label readiness | text + preview | length limits per printer |
| UoM barcode behaviour | read-only summary → Barcode Center | `UoMId` shown explicitly (§3) |
| Retail category | picker | must exist in company |
| Branch availability · branch tax dependency · loyalty dependency | read-only indicators | **surfaced, not edited here** — they live in `BranchPosSetting` |

### 2.2 Commerce Presentation — designed, not moved

`StoreBadge` · `StoreRating` · `StoreHoverImage` · `StoreOldPrice` · `StoreVendor` · channel title · marketing
description · channel media · channel sort order · online visibility · channel status.

**Rendered here read-write for compatibility, labelled "channel presentation", and marked as future PIM ownership.**
No data is moved in Stage 2C; a read-compatible view keeps `StoreController` working (RISK-011).

**Warnings:** *weighted item without a scale code* · *scale code duplicate* · *allowed UoM without a conversion* ·
*channel visible but no price* · *no primary image for a channel-visible item*.
**Empty:** "this item is not sold in retail" with an enable action. **Mobile:** read-only summary + the barcode
decoder; **weighted configuration is desktop-only**. **Events:** `Item.RetailConfigured`,
`Item.ScaleCodeChanged` (elevated). **Reporting:** items missing scale code, weighted items without conversion,
channel-visible items missing media.

---

## 3. Barcode Center (redesign)

List of all barcodes with: value · **UoM (explicit, never hidden)** · kind (primary / secondary / supplier / internal /
weighted / packaging) · active state · effective dates · duplicate status · **ambiguity status** · source system ·
migration status.
Actions: add · set primary · deactivate · **scan test** · **print label** · **conflict resolution**.

**The sold-unit guard — the central UX requirement.** Changing a barcode's `UoMId` changes the sold quantity
(RISK-046), so it requires **all five**: a warning · permission · an audit reason · an **impact preview** ("this
barcode currently sells 1 CASE = 12 EACH; after the change it sells 1 EACH") · and approval. It can never be an
inline edit.

**Ambiguity is displayed and resolved, never guessed** (RISK-050). Where two barcodes collide the row is flagged and
the conflict workflow requires an explicit choice — the screen must not present a "use first match" action at all.

## 4. Unit Conversion Designer (redesign)

Canvas of base ⇆ source ⇆ destination with factor, inverse (derived, not stored twice), precision, rounding, effective
dates, transaction usage, impacted modules, and a **live sample calculation**.

**The screen states three rules explicitly, as text on the page:** *no conversion means the sale line is rejected* ·
*the system does not assume factor 1* · *existing conversion results must remain unchanged*. Making these visible is
the cheapest defence against a future "helpful" default.

Cycle detection on save. **Historical-usage warning:** editing a factor used by posted documents requires elevated
approval and shows how many documents used it. **Mobile:** read-only. **Migration warning state:** a conversion whose
factor differs from the shadow projection is flagged and blocks cutover.

## 5. Composite Product Designer (redesign)

Shows **canonical type** (Kit / Bundle / Recipe / Sales BOM / Manufacturing BOM / Configurable) **alongside the legacy
type signals** (`ItemType`, `IsComposite`, `CompositeType`, `ProductionMethod`) — **displayed, never silently
rewritten**. Plus revision · effective date · components with UoM and quantity · scrap/yield · **explosion time** ·
stock/pricing/POS/manufacturing impact · work-order requirement · return behaviour · draft/active/obsolete ·
**revision comparison**.

**Existing rows are revision 1** (RISK-047). No historical composite is reclassified, and the designer offers no
bulk-reclassify action. Self-reference and cycles rejected. Component inactive/discontinued warns.

## 6. Attributes and Variants (new capability)

Product Template → Attribute Definitions → Values → **Valid Combinations** → variant generation → variant matrix, with
inherited vs overridden fields marked per variant (SKU, barcode, image, price, cost, stock, lifecycle).

**Manual combination selection with preview before creation**, generated-item count shown, duplicate detection, and
**migration-candidate marking only**.

**Hard UX constraints:** no automatic merge · no inference from naming patterns · **variant migration controls are
disabled, with a visible "Blocked pending approved data measurement" state** citing RISK-034. The screen must make the
blocker legible rather than simply omitting the button.

## 7. Mobile and operational behaviour

**The full desktop workspace is not squeezed into a phone.** Instead, focused mobile workflows:

| Workflow | Capability |
|---|---|
| Barcode scan / quick lookup | camera scan → item summary; **shows the resolved UoM** |
| Image capture | capture → upload to 2B attachments; set primary |
| Stock lookup | on-hand by warehouse/bin, read-only |
| Approval | approve/reject a Master Data change with reason |
| **Weighted-item validation** | decode a sample scale barcode and confirm the quantity — field-verifiable |
| Field inspection | read-only area summaries |

**Tablet** renders the full workspace with `compact` density; **areas 4, 5 and 6 are desktop-only** (matrix and tree
editing is not a touch task). Mobile write is limited to image capture, approval and scan-assisted lookup. Offline is
**read-only** where cached, with a stale-data indicator. Large-touch controls in `operational` density for the scan and
weighted-validation flows.

---

## 8. Completion check against the brief

| Requirement | Status |
|---|---|
| 16 areas defined | **Yes** (§1) |
| Retail and Commerce complete | **Yes** (§2) |
| Weighted-item administration UX | **Yes** (§2.1) — all 9 mandated validations |
| Barcode Center UX | **Yes** (§3) — incl. the five-part sold-unit guard |
| Unit Conversion Designer UX | **Yes** (§4) — incl. the three on-screen rules |
| Composite Product Designer UX | **Yes** (§5) — revision 1, no silent translation |
| Attributes and Variants UX | **Yes** (§6) — migration controls disabled |
| Mobile behaviour | **Yes** (§7) |
| Migration impact per area | **Yes** (§1), matching the mandated minimums |

Per-area purpose, users, fields, actions, validations, dependencies, authorization, audit, warnings, the six states,
mobile/tablet, migration and source-of-truth impact, reporting and AI are given **once universally** (§1) and
**specifically** where an area diverges (§2–§6). This avoids sixteen near-identical blocks while keeping the divergent
detail explicit.
