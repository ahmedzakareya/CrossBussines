# IMP-004 — Master Data Source Consolidation — Design

**Design, measurement planning and evidence only.** No production code or schema changed · no migration executed ·
no SQL against `CrossBuyDB2` · Stage 2A not started.

Generated evidence: `Stage-002-Master-Data-Source-Inventory.csv` (18 sources) ·
`Stage-002-Barcode-Source-Matrix.csv` (7 cases) · `Stage-002-Image-and-Media-Source-Matrix.csv` (4 cases) — all
deterministic, from `generate-imp004-artifacts.py`.

Retail behaviour is governed by the **accepted** `Stage-002-Hypermarket-Current-State-Assessment.md`, referenced not
restated.

---

## 1. Source inventory — 18 sources classified

| Classification | Count | Sources |
|---|---|---|
| **Authoritative** | 10 | `Item` · `ItemBarcode` · `ItemImage` · `ItemComponent` · `UnitOfMeasure` · `UoMConversion` · `ItemCategory` · `ItemWarehouseSetting` · `PriceList` · `BinLocation`/`BinStock` |
| **Legacy Compatibility** | 2 | `Item.Barcode` column · `Item.ImagePath` column |
| **Ambiguous** | 2 | `Item.CompositeType` · `Item.ProductionMethod` |
| **Channel-Specific** | 1 | the five `Store*` fields on `Item` |
| **New Capability Required** | 2 | `Item.IsWeighted` · `Item.ScaleCode` — see §2 |
| **Derived** | 1 | `StockBatch`/`StockSerial` — owned by the stock writer, out of scope |

## 2. The finding that most changes IMP-004's shape

**`IsWeighted` and `ScaleCode` have no admin screen anywhere** (Hypermarket finding F4). They are written only from
SQL or a `[DevOnly]` endpoint. So two fields that determine **fresh-food checkout quantity** are, in production,
unmanageable through the application.

That reclassifies them from "fields to migrate" to **New Capability Required** — the gap is an administration surface,
not a data model. `Views/Inventory/ItemForm.cshtml` has neither field. **RISK-049.**

## 3. Three rules that constrain every decision below

From the accepted Hypermarket assessment, and each one is a rule a naive redesign would break:

1. **`ItemBarcode.UoMId` selects the sold unit.** A barcode migration that changed the resolved UoM would change sale
   quantity. **RISK-046.**
2. **A unit with no conversion is REJECTED at add-line — never defaulted to factor 1.** Introducing a factor-1 fallback
   during a "cleanup" would silently change quantities. **RISK-048.**
3. **An ambiguous barcode is EXPLICITLY REJECTED** — the system never guesses. Any consolidation must preserve the
   rejection, not resolve it.

## 4. Item / Product domain model

Canonical, non-overlapping definitions (the four overlapping type signals — `ItemType`, `IsComposite`,
`CompositeType`, `ProductionMethod` — are *clarified*, not restructured):

| Concept | Stock | Work order | Explosion time | العربية |
|---|---|---|---|---|
| **Item** | base record | — | — | صنف |
| **Product** | yes | no | n/a | منتج |
| **Service** | no | no | n/a | خدمة |
| **Asset** | capitalised | no | n/a | أصل |
| **Kit** | components only | **no** | **at sale** | طقم |
| **Bundle** | parent only | no | at sale (price) | حزمة |
| **Sales BOM** | components | no | at order | قائمة مبيعات |
| **Manufacturing BOM** | both sides | **yes** | at work order | قائمة تصنيع |
| **Recipe** | both, with yield/loss | yes (or backflush) | production | تركيبة |
| **Configurable** | variant | maybe | at order | منتج قابل للتهيئة |
| **Variant** | own stock | — | n/a | متغير |
| **Packaging Unit** | handling only | — | n/a | وحدة تعبئة |

**The discriminating rule:** *creates or requires a work order ⇒ Manufacturing BOM; resolves at sale ⇒ Kit / Bundle /
Sales BOM; consumes by production logic ⇒ Recipe.* `RecipeAtSale` backflush (hyper) is a **Recipe** consumed at sale —
the one case that spans both, and it must keep its current behaviour.

**No historical record is reclassified automatically.** Stored `CompositeType`/`ProductionMethod` values keep their
meaning; only the *documentation* and *new* records use the canonical vocabulary.

## 5. Barcode authority

**`ItemBarcode` becomes authoritative; `Item.Barcode` is retained as Legacy Compatibility and is NOT deleted.**
Seven cases in the matrix. Primary designation moves from "the column" to an `IsPrimary` row flag, seeded from the
column. Coexistence: read primary row then fall back to the column; write both; nightly divergence compare.

**Preserved byte-for-byte:** `ItemBarcode.UoMId` semantics · ambiguous-barcode rejection · scale-prefix routing
*before* lookup · GS1 mod-10 check · `Item.ScaleCode` filtered uniqueness.
**New (additive only):** supplier barcode attribution · packaging barcode · general GS1 AI parsing alongside — never
replacing — the scale parser.

## 6. Image authority

**`ItemImage` becomes authoritative with an `IsPrimary` row; `Item.ImagePath` retained.** Files inherit authorization
from the parent `Item` through 2B unified attachments, served by an **authorizing endpoint — never a guessable static
path** (RISK-012). `StoreHoverImage` moves to Commerce Presentation (§7).

## 7. Commerce presentation separation

```
Item / Product  (operational)
      ↓
Commerce Presentation  (per channel)
      ↓
Store / Marketplace / B2B channel
```

The five `Store*` fields become channel-scoped presentation rows. **No data is moved in this delivery.** A
read-compatible view keeps `StoreController` working; the field remains until the channel model is proven. Owner:
Commerce/PIM domain, not Inventory.

## 8. Units, conversions, packaging

`UnitOfMeasure` and `UoMConversion(ItemId, From, To, Factor)` are **preserved unchanged**. **Unit Sets** are additive
— they declare *which units are legal for an item*, which is currently implicit and unenforceable. Conversion
**validity dates** are additive and must default to "always valid" so historical reads are unaffected.

**Acceptance equation, from source behaviour:** for every existing `(ItemId, From, To)` row, `Factor` must return the
identical decimal before and after, and a `(From, To)` pair with **no row must continue to be rejected** — never
factor 1.

**Packaging is a new capability, distinct from a unit:** `PackageType` · `ItemPackage` (contained quantity + UoM,
dimensions, weight, volume, barcode) · inner pack / retail pack / carton / pallet. It relates to Unit and Conversion
by *reference*, never by redefining them, and **introduces no calculation that changes historical stock quantities**.

## 9. Attributes and variants

`ProductTemplate` → `AttributeDefinition` → `AttributeValue` → `VariantCombination` → `VariantItem`, where a
`VariantItem` **is** an `Item` (own stock, cost, barcode, price) linked to a template. Additive: no existing item is
touched.

**No existing items are merged automatically, and variants are never inferred from naming patterns.** Migration is
possible only after approved measurement — see §10.

## 10. Variant population — still BLOCKED

Source code cannot reveal how many existing items are manual variants. Two safe options are specified:

**Option A — read-only measurement:** `SELECT` only · no temp tables · no writes · no schema change · anonymised
aggregates plus representative ids only · no customer/employee data · no financial balances · **explicit approval
required before any execution against `CrossBuyDB2`**. Candidate grouping signals: normalised name, category, brand,
shared price, barcode pattern, shared image, shared components, size/colour terms, SKU pattern. **Reports confidence,
not certainty.**

**Option B — anonymised extract:** required columns only, anonymisation rules, offline analysis script.

**No migration sizing claim is made until one option is approved and executed.** RISK-034 remains open.

## 11. Composite architecture — the cross-platform change

`ItemComponent` is read by **`StockService:319` and `:1189`** (kit explosion), **`PricingService:311`** (manufactured
classification), **`PosOrderService:302` and `:1746`** (sale-time explosion, semi-finished), `ManufService` and
`PosSetupService`. **20 write sites.** **RISK-033.**

Per future type: explosion time · stock ownership · pricing ownership · cost ownership · work-order requirement ·
return and cancellation behaviour · partial fulfilment · substitution · component availability · **revision support**.

**New finding — RISK-047: `ItemComponent` has no revision history**, so a historical explosion cannot be reproduced
from the data. Revisions are additive, and existing rows become revision 1 with their current effective date.
**No historical composite is reclassified.**

## 12. Weighted item and scale contract

Preserved exactly: `Item.ScaleCode` filtered uniqueness · scale-code resolution · quantity calculation · **KWD
3-decimal rounding** where applicable · FEFO allocation · stock and COGS output · scale-prefix routing before lookup.
**Added:** the missing administration screen and validation (design only) — closing RISK-049.

## 13. Governance

Nine roles (Master Data Administrator, Product Manager, Inventory/Purchasing/Sales Administrator, Manufacturing
Engineer, Finance Reviewer, Retail Administrator, Auditor). **Elevated four-eyes approval required for: financial
mapping changes · UoM factor changes · composite revisions** — the three changes that can alter money or quantity.
Every change carries reason, effective date, BusinessEvent, notification and audit.

## 14. Coexistence

Per duplicated source: `Legacy` → `Shadow` → `NewAuthoritative` → `LegacyReadOnly` → `Retired`.
**Never a union of two sources**, and **two writers are never simultaneously authoritative**. A successful write to a
non-authoritative source must never be silently ignored — the IMP-001 RISK-038 lesson applied to Master Data.

## 15. Non-regression gate — 24 invariants, non-skippable

All 24 from the brief, with **zero tolerance** on: moving-average valuation (byte-identical) · stock quantities ·
movement history · journal output · opening/serial/batch/**bin** balances · UoM conversion results and rounding ·
composite explosion quantities and historical cost · POS and **Hypermarket** calculations · pricing · tax ·
manufacturing · FEFO · receipt/invoice quantities · returns/cancellations · **scale barcode interpretation** ·
**`ItemBarcode.UoMId` behaviour** · **`Item.ScaleCode` uniqueness**.

Method: baseline capture → after-state comparison → **deterministic hashes** over canonical datasets on **isolated
disposable SQL Server** databases (RISK-036), carrying `[RequiredEvidence]` so they **cannot report skipped**
(RISK-026). Tolerance is permitted **only** where mathematically required (currency rounding at declared scale);
everything else is zero-tolerance. Any breach is a rollback trigger.

## 16. Migration

Thirteen phases: inventory → additive schema → compatibility readers → shadow projection → divergence measurement →
read-only validation → company pilot → module pilot → **non-regression proof** → controlled cutover → legacy writer
shutdown → rollback window → historical cleanup (later, separately approved).

Additive first · idempotent · resumable · auditable · reversible before cleanup · safe per company and per module ·
**safe for POS, Hypermarket, WMS and Manufacturing**. Phases 1–6 change no behaviour.

## 17. Hypermarket integration

Master Data explicitly supports and preserves: weighted items · scale codes and barcode format · branch tax
configuration dependency · loyalty configuration dependency · PLU readiness · item search · retail categories · store
presentation · UoM barcodes · FEFO · expiry · fresh-food data · retail packaging · receipt printing data.

**The Hypermarket backend is not rebuilt.** The shared Retail Core and settlement behaviour are preserved. The
assessment's own recommendation — **shared retail core + hypermarket pack** — is adopted as the retail classification,
which also closes the previously-pending item in the Phase 0 reports.

## 18. Delivery status — honest

| Required output | Status |
|---|---|
| `Stage-002-Master-Data-Source-Consolidation-Design.md` | **Completed** (this document) |
| `Stage-002-Master-Data-Source-Inventory.csv` | **Completed** — generated, 18 sources |
| `Stage-002-Barcode-Source-Matrix.csv` | **Completed** — generated, 7 cases |
| `Stage-002-Image-and-Media-Source-Matrix.csv` | **Completed** — generated, 4 cases |
| UoM dependency map · Composite dependency map · Weighted-item contract · Commerce separation · Variant measurement plan · Non-regression gates · Migration and rollback | **Completed in §8 / §11 / §12 / §7 / §10 / §15 / §16** — consolidated deliberately (permitted by the brief) rather than eight files that would need synchronising |
| `Stage-002-Product-Workspace-UX-Contract.md` | **Not Started** — the 16-area contract. §13 of `Stage-002-Master-Data-Reassessment.md` already carries the 15-area table with fields, actions, validation, roles and states; the outstanding work is adding the **Retail and Commerce** area and the per-area mobile/migration columns |
| Risk Register update (RISK-046…050) | **Not Started** — five risks identified here, not yet merged into the canonical generator |
| `Stage-002-IMP-004-Final-Delivery-Report.md` | **Not Started** |

**New risks identified: RISK-046** (barcode migration could change the resolved UoM and therefore sale quantity —
High) · **RISK-047** (`ItemComponent` has no revision history, so historical explosions cannot be reproduced — Medium)
· **RISK-048** (a conversion redesign introducing a factor-1 fallback would silently change quantities — High) ·
**RISK-049** (weighted and scale fields have no admin screen — High) · **RISK-050** (a Master Data change altering
price resolution would change invoice totals — High).

**Gate: 19 of 22 met.** Outstanding: the Product Workspace UX contract, the risk merge, and the final delivery report.

No production code changed · no migration executed · no SQL against `CrossBuyDB2` · Stage 2A not started.
