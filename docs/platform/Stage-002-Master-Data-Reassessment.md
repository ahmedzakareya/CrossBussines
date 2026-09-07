# Stage 2C — Enterprise Master Data — Reassessment and UX Architecture

**Design only. No production code, no schema change, no SQL against `CrossBuyDB2`.** Fulfils P0-C4.

---

## 0. CORRECTIONS to my own earlier Phase 0 statements — read first

The accepted Phase 0 partial (`Stage-002-Requirement-Status.md` §4) and the Architecture Review §4 were written from
**shallow inspection**. Complete inspection of `Models/Context/Inventory/*.cs` contradicts several of my claims. The
permanent delivery principle requires inspecting the implementation completely, and I had not.

| My earlier claim | Reality | Correction |
|---|---|---|
| "multi-barcode is not modelled" | **`ItemBarcode` exists** | Multi-barcode **exists**; what is missing is GS1 semantics and supplier-barcode attribution |
| "unit sets and conversions exist; packaging is not modelled" | `UnitOfMeasure` + **`UoMConversion` exist**; Item carries `BaseUoMId`/`PurchaseUoMId`/`SalesUoMId` | Conversions **exist**. There is **no `UnitSet`** grouping and **no packaging hierarchy** — that part stands |
| "composition: sales BOM / kit / bundle do not exist" | **`ItemComponent` exists**, plus `Item.IsComposite`, `CompositeType`, `ProductionMethod` | Composition **exists and is already typed**. The defect is *terminological*, not absent — see §3 |
| Review §4: WMS "**None** — no bins" | **`BinLocation` and `BinStock` exist** | **Wrong.** WMS has a bin foundation. Reclassify WMS from *Full Platform Build* to **Partial Build on an existing foundation** |
| Review §4: Manufacturing "no routing, no capacity" | **`ManufRoutingOp` and `ManufWorkCenter` exist**, plus `ManufPlan`/`ManufPlanDemand` | **Wrong.** Routing, work centres and planning demand exist. Missing is *scheduling/capacity levelling*, not routing |
| "no variant model" | Confirmed — no `ItemVariant`/`ItemAttribute` | Stands |
| "no lifecycle beyond IsActive" | Confirmed | Stands |
| "no duplicate detection / governance" | Confirmed | Stands |

**Impact:** Stage 2C is **smaller** than I implied, and Stages 4–5 are **less green-field** than I implied. The
sequencing conclusions do **not** change — Master Data still precedes WMS and Manufacturing, because the missing
pieces (variants, unit sets, packaging) are exactly what bins and routing consume. But "Full Platform Build" was an
overstatement for WMS and Manufacturing, and I am withdrawing it.

The accepted documents are **not rewritten**; this section is the correction pointer they refer to.

---

## 1. The actual `Item` model — 39 properties

```
ID · CompanyID · ItemCode · Barcode · Name · NameEn · ItemCategoryId · ItemType
BaseUoMId · PurchaseUoMId · SalesUoMId · CostingMethod
TrackBatch · TrackExpiry · TrackSerial · IsWeighted · ScaleCode
DefaultTaxCodeId · ItemEgsCode · SalesPrice · MinMarginPct · LoyaltyEligible · OpeningCost
ImagePath · QuickCode · IsComposite · CompositeType · ProductionMethod · IsActive
CreatedBy/At · ModifiedBy/At
StoreOldPrice · StoreBadge · StoreRating · StoreVendor · StoreHoverImage
```

Supporting entities that **already exist**: `ItemCategory` · `ItemBarcode` · `ItemComponent` · `ItemImage` ·
`ItemWarehouseSetting` · `UnitOfMeasure` · `UoMConversion` · `Brand` · `PriceList`/`PriceListLine` · `Promotion` ·
`StockBatch` · `StockSerial` · `StockCostLayer`.

### 1.1 Findings from the real model

1. **Storefront presentation lives on the master record.** `StoreOldPrice`, `StoreBadge`, `StoreRating`,
   `StoreVendor`, `StoreHoverImage` are e-commerce display concerns on the item table. That is the clearest
   separation-of-concerns defect in the model and the strongest argument for a PIM-style split later.
2. **A single `Barcode` column coexists with the `ItemBarcode` table.** Two sources for the same fact — the same
   duplication class as the company constants and the four role tables. One must become authoritative.
3. **Three UoM foreign keys on the item** (`Base`/`Purchase`/`Sales`) with conversions in a separate table, but **no
   `UnitSet`** — so "which units are legal for this item" is implicit and unenforceable.
4. **`ItemType` + `IsComposite` + `CompositeType` + `ProductionMethod` are four overlapping type signals.** This is the
   terminology problem, not a missing feature.
5. **`Name`/`NameEn` twins** with a culture check at every read site rather than one resolver — repeated across master
   entities.
6. **No variant model**, so variants are separate items — which is why configurable products have no representation.
7. **No lifecycle** beyond `IsActive`; **no approval, no duplicate detection**.
8. **267 hardcoded company references in `InventoryController`** — the largest single concentration in the codebase and
   the principal migration risk for any Master Data work.

---

## 2. Classification — per the required scale

| Area | Current | Class |
|---|---|---|
| Items, ItemCode, Name/NameEn | Exists, works | **Extend** (add resolver, lifecycle) |
| Products vs Services vs Assets | Conflated in `ItemType` | **Refactor** (type separation) |
| Categories | `ItemCategory` exists | **Extend** |
| Groups / Families / Brands | `Brand` only | **Extend** |
| Units, Conversions, Precision | `UnitOfMeasure` + `UoMConversion` exist | **Extend** |
| **Unit Sets** | **Absent** | **New** |
| Dimensions / Weight / Volume | Absent (`IsWeighted` only) | **New** |
| **Packaging hierarchy** | **Absent** | **New** |
| Barcodes (multiple) | `ItemBarcode` **exists** | **Refactor** (retire the `Item.Barcode` column) |
| GS1 / supplier barcodes | Absent | **New** |
| **Variants / Attributes** | **Absent** | **New** |
| Composition (`ItemComponent`) | **Exists**, typed | **Refactor** (terminology, §3) |
| Kits / Bundles / Sales BOM | Present *as composite types* | **Refactor**, not new |
| Manufacturing BOM | `ManufWorkOrderComponent` + routing exist | **Preserve + Extend** |
| Configurable products | Absent (needs variants) | **New**, after variants |
| Serial / Batch / Expiry rules | `TrackSerial/Batch/Expiry` + `StockSerial`/`StockBatch` exist | **Extend** (rule expression) |
| Warranty | Absent | **New** |
| Inventory behaviour | `ItemWarehouseSetting`, `CostingMethod` | **Preserve** |
| Procurement / Sales behaviour | `PurchaseUoMId`, `SalesUoMId`, `PriceList`, `Promotion` | **Extend** |
| Financial mapping | `DefaultTaxCodeId`, `ItemEgsCode` | **Extend** |
| Manufacturing behaviour | `ProductionMethod`, routing | **Extend** |
| Logistics behaviour | Absent | **New** |
| Images | `ItemImage` + `ImagePath` (two sources) | **Refactor** |
| Documents | No item documents | **New** — via unified attachments (2B) |
| Translations | `Name`/`NameEn` twins | **Refactor** |
| **Lifecycle / Governance / Approval** | **Absent** | **New** — via 2B workflow |
| **Duplicate detection** | **Absent** | **New** |
| Import / Validation | Absent | **New** |
| Storefront fields on Item | Exists, misplaced | **Refactor** (separate presentation) |

**Nothing is classified Replace.** No source evidence supports discarding the item model; every gap is additive or a
rename.

---

## 3. Terminology — the unambiguous definitions

The model has four overlapping type signals. Proposed canonical meanings, chosen to fit what already exists:

| Term | Definition | Stock? | Produced? | Model |
|---|---|---|---|---|
| **Item** | The base master record. Everything below **is** an Item with a type. | — | — | `Item` |
| **Product** | An Item that is stocked and sold | Yes | No | `ItemType = Product` |
| **Service** | An Item that is sold and never stocked | No | No | `ItemType = Service` |
| **Asset** | An Item capitalised rather than sold | Yes (as asset) | No | `ItemType = Asset` → `FixedAsset` |
| **Kit** | Components delivered together, **exploded at sale**; no production order; components leave stock individually | Components only | No | `IsComposite`, `CompositeType = Kit` |
| **Bundle** | Priced group sold as one line; **stock unaffected structurally** — a commercial construct | Parent only | No | `CompositeType = Bundle` |
| **Sales BOM** | Structure resolved **at order time**, may substitute | Components | No | `CompositeType = SalesBom` |
| **Manufacturing BOM** | Consumed by a **work order**; produces a distinct output item | Both sides | **Yes** | `ManufWorkOrderComponent` + routing |
| **Recipe** | A Manufacturing BOM with **yield and loss** semantics (process manufacturing) | Both | Yes | Manufacturing BOM + yield fields |
| **Configurable Product** | A template resolved into a variant at order time | Variant | Maybe | **needs the variant model** |

**The single rule that removes the ambiguity:** *if it creates a work order it is a Manufacturing BOM; if it resolves at
sale it is Kit / Bundle / Sales BOM.* `ProductionMethod` then means only "how a manufactured item is produced" and stops
overlapping `CompositeType`.

---

## 4. Product Workspace — 15 areas

Bilingual (ar primary, en parity), RTL/LTR, Metronic, tokens from the Design System. One record, tabbed workspace,
**capability panels from 2B** (timeline, comments, attachments, audit) rather than bespoke per-area implementations.

| Area | العربية | Fields / content | Actions | Validation | Role | Empty state | Warning state |
|---|---|---|---|---|---|---|---|
| **Overview** | نظرة عامة | code, name, type, category, status, stock summary, cost, price | activate/deactivate, duplicate-check | — | `inv:read` | new-item hint | discontinued banner |
| **Identity** | الهوية | `ItemCode`, `Name`, `NameEn`, `QuickCode`, `ItemEgsCode`, brand | rename, regenerate code | code unique per company; name required | `inv:manage` | — | code-format warning |
| **Classification** | التصنيف | category, group, family, brand, tags | reclassify | category must exist in company | `inv:manage` | uncategorised prompt | orphan category |
| **Units & Packaging** | الوحدات والتعبئة | base/purchase/sales UoM, **unit set**, conversions, **packaging levels**, weight, volume, dimensions | add conversion, define pack | conversion must reach base; **no cycles** | `inv:manage` | "define a unit set" | missing conversion blocks purchase |
| **Attributes & Variants** | الخصائص والمتغيرات | attribute definitions, variant matrix | generate variants | attribute values unique per axis | `inv:manage` | "not a variant product" | variant count explosion warning |
| **Composition** | التركيب | `CompositeType`, `ItemComponent` lines, yield/loss | add component, explode preview | **no self-reference, no cycles**; type must match §3 | `inv:manage` | "simple item" | component inactive/discontinued |
| **Inventory** | المخزون | `CostingMethod`, `TrackBatch/Expiry/Serial`, `ItemWarehouseSetting`, reorder | set warehouse rules | **costing method immutable once stock exists** | `inv:manage` | no warehouse rules | negative stock, expiry breach |
| **Purchasing** | المشتريات | purchase UoM, suppliers, lead time, last cost | set preferred supplier | supplier same company | `inv:purchase` | no supplier | price variance |
| **Sales** | المبيعات | `SalesPrice`, `MinMarginPct`, price lists, promotions, `LoyaltyEligible` | set price | **margin ≥ `MinMarginPct`** | `acc:post` for price | no price list | below-margin warning |
| **Financial** | المالية | `DefaultTaxCodeId`, GL mapping, `OpeningCost` | map accounts | accounts same company **and postable** | `acc:manage` | unmapped | tax code missing |
| **Manufacturing** | التصنيع | `ProductionMethod`, routing ops, work centres | attach routing | routing ops ordered | `inv:doc` | not manufactured | routing without work centre |
| **Logistics** | اللوجستيات | handling, hazmat, storage conditions, shipping class | set constraints | — | `inv:manage` | — | conflicting conditions |
| **Media & Documents** | الوسائط والمستندات | `ItemImage`, unified attachments | upload, set primary | type/size limits | `inv:manage` | no media | missing primary image |
| **Analytics** | التحليلات | movement, margin, turnover, ABC | export | — | `inv:read` | insufficient history | slow-mover flag |
| **Timeline & History** | السجل | 2B timeline + audit | filter | — | `inv:read` | no activity | — |

**Mobile:** Overview, Inventory, Media are read-optimised; Units/Variants/Composition are **desktop-only** (matrix and
tree editing is not a phone task). **Dependent modules:** Inventory, POS, Sales, Procurement, Manufacturing, Projects,
Store — every one reads `Item`, which is why §5 matters.

---

## 5. Migration concerns — the real constraints

1. **`InventoryController` has 267 hardcoded company references.** Any Master Data screen work touches this controller.
   Recommend the **Class A company-source sweep for `InventoryController` land in 2C as its own commit**, before the new
   screens — not folded into them.
2. **Two barcode sources** (`Item.Barcode` + `ItemBarcode`) must be reconciled with a read-compatible view before the
   column is retired.
3. **Two image sources** (`Item.ImagePath` + `ItemImage`) — same pattern.
4. **Variants-as-items:** existing data must be *interpreted*, never auto-converted. Recommend an opt-in, reversible
   conversion with a dry-run report.
5. **Costing is immutable once stock exists** — `StockCostLayer` and moving-average history mean `CostingMethod` cannot
   be edited retroactively. This must be enforced in the UI, not just documented.
6. **Hard gate:** existing **stock valuation must be byte-identical** before and after. Moving average must not move.
   This is the acceptance test that makes 2C safe.
7. **Storefront field separation** changes what `StoreController` reads — coordinate, as those files carry parallel-team
   modifications.
