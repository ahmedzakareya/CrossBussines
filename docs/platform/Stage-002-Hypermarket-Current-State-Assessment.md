# Stage 2 — Hypermarket / Retail Current-State Assessment

**Assessment only. No production code written or modified. No SQL executed against `CrossBuyDB2`.**
Closes item **C10** of `Stage-002-Phase-0-Completion-Report-3.md` ("Hypermarket integration gate — pending by instruction").

Companions produced with this document:
`Stage-002-Hypermarket-File-Inventory.csv` ·
`Stage-002-Hypermarket-Business-Flow-Map.md` ·
`Stage-002-Hypermarket-Screen-Inventory.md` ·
`Stage-002-Hypermarket-Gap-Analysis.md` ·
`Stage-002-Hypermarket-Roadmap-Proposal.md` ·
`Stage-002-Hypermarket-Risk-Register.md` ·
`Stage-002-Hypermarket-Requirement-Status.md`

**Method.** Every statement below is traced to a file and, where behaviour is claimed, to the method that implements it.
Where the sweep found nothing, the negative is stated as "no consumer found in source" rather than "absent" — and the
search that produced it is named. Existing roadmap and design descriptions were **not** used as evidence; two of them
turned out to be wrong (§3).

---

## 1. Executive summary

**The Hypermarket is not absent, not a stub, and not a rebuild candidate. It is a real, financially-correct,
narrow checkout lane with a disproportionately strong engine underneath it and a disproportionately thin operator
surface on top of it.**

Five findings drive every recommendation in this pack.

| # | Finding | Evidence |
|---|---|---|
| **F1** | **The backend is materially more mature than the UI.** The hyper sale posts a real sales invoice, real AR, real VAT, real COGS and real stock issue inside **one** ambient transaction, with currency-aware 3-decimal KWD rounding, atomic per-terminal receipt numbering, INSERT-keyed pay idempotency, FEFO batch allocation and a reversing-entry correction model. | `PosOrderService.PayAsync` (1082–1202), `ScopedTx.BeginOrJoinAsync`, `ReceivableService.CreateSalesInvoiceAsync`, `StockService.FefoAllocateAsync` |
| **F2** | **The cashier lane is a form-postback page with a barcode box, a quantity box and one "Pay cash" button.** No item search, no PLU entry, no keypad, no tendered/change, no card, no split, no discount entry, no suspend/resume, no return, no void, no receipt print. Every action is a full page reload. | `Views/Hyper/PosLane.cshtml` (242 lines, 20 lines of JS); `HyperPosController` has 18 endpoints and none of them are void/return/hold/tender |
| **F3** | **Hypermarket is a *distinct lane over a shared engine*, and that separation is deliberate, enforced and clean.** Two lanes (`/pos` restaurant, `/hyper/pos` hyper), two session keys, one shared whitelist (`IPosAccessService.IsActivityAllowedForLane`), one shared order/settlement service. It is **not** legacy duplication. | `PosLaneActivityGuardAttribute`, `PosAccessService.IsActivityAllowedForLane` (148–156), `PosAppController` vs `HyperPosController` |
| **F4** | **A material amount of hyper capability is reachable only from SQL or a `[DevOnly]` endpoint.** Weighted items, scale codes, the scale-barcode format, branch tax code, loyalty rate and loyalty eligibility have **no admin screen anywhere**. The `OfficialInvoice` capability that gates the A4 invoice is set **only** by `hm8-accept`. | `Views/Inventory/ItemForm.cshtml` has no `IsWeighted`/`ScaleCode`; `PosSetupService.SavePosSettingAsync` (257–265) writes 4 of 14 `BranchPosSetting` columns; `grep "OfficialInvoice"` → only `DevSeedController` writes it |
| **F5** | **Four of the ten declared hyper capabilities have zero consumers in code, and two of those ship default-ON.** `SuspendResume`, `CashDrawer`, `ExpiryControl`, `ShelfLabels` are displayed as green "enabled" badges on the lane and the diagnostic panel while nothing reads them. | `grep '"SuspendResume"' --include=*.cs` → 1 hit, a seed array; `hyper_presets.sql` sets `SuspendResume=1`, `CashDrawer=1` |

**One-line verdict.** Preserve and extend the engine; rebuild the cashier surface; move the configuration out of SQL.
Recommended shape (§13, and `Stage-002-Hypermarket-Roadmap-Proposal.md`): **a shared Retail Core built on the existing
POS engine, plus a Hypermarket pack** — *not* a separate platform, *not* a mere POS configuration.

**Maturity, scored on evidence only (§11):** backend capability **3.2 / 5** · UX/UI **1.6 / 5** ·
enterprise-retail readiness **1.9 / 5**.

---

## 2. What was searched

The full source tree was swept for all 33 concept families named in the brief, by **name and by data relationship**.
Concepts were also chased through generic names, which is how `BranchItemSourcing` (replenishment), `PosSyncLog`
(offline), `StockCostLayer` (FEFO valuation) and `PointsMovement` (loyalty) were found.

| Concept family | Found under | Section |
|---|---|---|
| Hypermarket / Hyper / HyperPOS | `HyperController`, `HyperPosController`, `Views/Hyper/*`, `_LayoutHyperPos`, `ActivityPreset.Code='Hyper'` | §4, §5 |
| Retail | `ActivityPreset` catalogue lists `Retail` — **no lane claims it** (`IsActivityAllowedForLane` denies it) | §7 |
| Store / Grocery / Supermarket / Market | `StoreController` + `StoreCatalogService` = **display-only storefront**; `Item.Store*` presentation columns | §9 |
| Cashier / Checkout / POS / Lane / Till | `PosTerminal`, `PosShift`, `BranchUserRole`, two lane controllers | §5, §6 |
| Shift / opening & closing balance | `PosShift` (`OpeningFloat`, `ClosingFloat`, `ExpectedCash`, `CashVariance`, `VarianceJournalEntryId`) | §8 |
| Basket / Cart / Held basket | `PosOrder` + `PosOrderLine`; `IsHeld`/`HeldAt` exist and **are not wired into the hyper lane** | §5, §6 |
| Customer display | **No consumer found in source.** No second-screen view, hub message or route | §10 |
| Barcode / multiple barcodes | `Item.Barcode` (primary, unique) + `ItemBarcode` (secondary, per-unit) | §5, §7 |
| Scale / weighted / variable-weight barcode | `Item.IsWeighted`, `Item.ScaleCode`, `ScaleBarcodeParser`, 6 `BranchPosSetting.Scale*` columns | §5, §6, §7 |
| Promotion / discount / coupon | `Promotion` (+ `PricingService.ApplyBestPromotionAsync`); **no coupon entity found** | §7 |
| Loyalty / wallet / gift card | `PointsMovement` (earn only); **no wallet, no gift card found** | §5, §7 |
| Pricing / price list / branch price | `PriceList`, `PriceListLine`, `PriceChangeLog`, `BranchPosSetting.DefaultPriceListId` | §7 |
| Shelf / merchandising / ESL | `ShelfLabelService` (EAN-13 SVG, A4 browser print); **no planogram, no ESL** | §9, §10 |
| Replenishment | `ItemWarehouseSetting` (reorder point) + `InventoryController.Planning`; `BranchItemSourcing` 4 methods | §8 |
| Fresh food / production / preparation | `BranchItemSourcing.Method` = `RecipeAtSale` / `SemiFromBranchComplete` → `ManufService` | §5, §6, §8 |
| Expiry / waste / markdown | `StockBatch.ExpiryDate`, FEFO in `StockService`, `StockWriteOff`, `ExpiryAlerts`; **no markdown engine** | §8 |
| Branch stock / store transfer | `StockBalance` per warehouse + `BranchPosSetting.DefaultSalesWarehouseId`; `StockService.TransferAsync` | §8 |
| E-commerce / delivery / click-and-collect | `StoreController` (display only); `PosOrder` delivery fields + `Driver`/`DeliveryZone` — **restaurant lane only**; **no click-and-collect** | §9 |

---

## 3. Corrections to current assumptions

Three statements in circulation are contradicted by source. They are corrected here rather than carried forward.

| Assumption | Source reality |
|---|---|
| "Hypermarket is an HM-0 scaffold / diagnostic panel with no selling." — the header comment still in `HyperController.cs:12` and `HyperPosController.cs:19` | **Stale by ten phases.** The lane sells, pays, posts GL, issues stock, resolves promotions, parses scale barcodes, earns loyalty points, prints an A4 invoice and produces two margin reports. The comments were never updated after HM-1. |
| "The retail classification is pending the hypermarket assessment; five candidate classifications are open." (`Phase-0-Completion-Report-3.md:126,185`) | **Resolvable now.** §13 closes it with evidence: *shared retail core + hypermarket pack*. |
| "Hyper reporting exists." | It exists **only since HM-10 slice B**, is **two read-only screens**, and both carry a literal banner reading *"Functional placeholder layout — awaiting the final report design."* (`HyperSales.cshtml:18`, `HyperItemMargin.cshtml:18`). Before that slice the hyper Reports menu was three stubs pointing back at the diagnostic panel. |

A fourth correction concerns a **claim not to make**: the restaurant lane's offline engine (`pos-offline.js`,
`pos-sw.js`, `SyncPaidOrderAsync`) exists but, per `AUDIT-DEVIATIONS.md:1130`, **has never run on a real path** — all
of its evidence is synthetic `ZZ` fixtures. It must not be counted as hypermarket offline capability, and HM-10
explicitly declined to generalise it (§6.28).

---

## 4. H1 — Implementation inventory (summary; full table in the CSV)

**73 files** carry hypermarket-relevant behaviour. The machine-readable inventory with all eleven mandated attributes
per file is `Stage-002-Hypermarket-File-Inventory.csv`.

| Layer | Count | Hyper-exclusive | Shared with restaurant / inventory |
|---|---|---|---|
| Controllers | 8 | 2 (`HyperController`, `HyperPosController`) | 6 |
| Services (BL) | 21 | 3 (`ScaleBarcodeParser`, `ShelfLabelService`, `LoyaltyPointsHelper`) | 18 |
| Models / DbContext / guards | 12 | 2 (`HyperPayToken`, `PointsMovement`) | 10 |
| Views | 21 | 10 (9 × `Views/Hyper/*` + `_LayoutHyperPos`) | 11 |
| SQL scripts | 9 hyper-phase + 17 POS-foundation | 9 | 17 |
| Hubs / workers / JS | 4 | **0** | 4 (none reached by the hyper lane) |
| Tests | 2 structural + 12 dev-accept endpoints | 12 dev endpoints | 2 |

**Notable inventory facts:**

- **Zero SignalR, zero background worker, zero JavaScript file** serves the hyper lane. `PosHub` broadcasts
  `OrderPaid`/KDS events for the **restaurant** lane only; `HyperPosController` never injects `IHubContext`. The only
  client-side code in the lane is 20 inline lines in `PosLane.cshtml` (idempotency token + `navigator.onLine` banner).
- `IntegrityCheckHostedService` is the one background job whose output covers hyper data (16 classifications, five of
  them retail-specific: `receiptno_dup`, `receiptno_out_of_series`, `fixed_barcode_in_scale_range`,
  `unbatched_inbound_tracked`, `cogs_impact`).
- **Two views exist with no navigation entry and no inbound link from any view**: `Views/Inventory/BulkPriceChange.cshtml`
  and `Views/Inventory/ShelfLabels.cshtml`. `grep` over `Models/Menu/MainMenu.cs` and all of `Views/` returns nothing
  for either. They are reachable only by typing the URL. Both are the deliverables of HM-4 — working, tested, invisible.
- All hyper files are **committed** (`git ls-files | grep -i hyper` → 15 tracked paths); the working-tree modifications
  are in shared files (`PosController`, `PosAppController`, `PosAccessService`).

---

## 5. H2 — Data-model assessment

`Models/Context/Pos/OperationsPlatform.cs` (465 lines) holds 22 entity types; `Models/Context/Loyalty/Loyalty.cs`
holds the points ledger; retail pricing lives in `Models/Context/Inventory/PriceList.cs` and `PriceChangeLog.cs`.
All are registered as `DbSet`s in `CrossDbContext` (lines 444–482).

Classification uses the brief's seven values. **"Not Found" is used only where models, services, controllers, views
and `deploy/sql` were all checked.**

| Capability | Status | Evidence / limit |
|---|---|---|
| Branches & stores | **Implemented** | `Branch` + `Branch.ActivityPresetCode`; hyper scoping is `ActivityPresetCode == "Hyper"` |
| Tills / checkout lanes | **Implemented** | `PosTerminal`: own `CashAccountId`, own `ReceiptPrefix`, own `NextReceiptNo`, paper width, copies |
| Cashiers | **Implemented** | `BranchUserRole` (`pos-cashier`/`pos-manager`/`pos-waiter`/`pos-kitchen`), keyed by `EmployeeId` |
| Shifts | **Implemented** | `PosShift` open/close, one open shift per terminal enforced (`OpenShiftAsync:890`) |
| Opening / closing balances | **Implemented** | `OpeningFloat`, `ClosingFloat`, `ExpectedCash`, `CashVariance`, `VarianceJournalEntryId` |
| Sales orders | **Implemented** (two models) | `PosOrder` (till) and `Inventory.SalesOrder` (back-office); they are **not** linked |
| Invoices | **Implemented** | `SalesInvoice`/`SalesInvoiceLine`, incl. `UoMId` on the line (HM-2) |
| Returns | **Implemented in engine, UI Only for restaurant** | `PosOrderService.ReturnOrderLinesAsync` (1492) exists and posts correctly — **no hyper endpoint calls it** |
| Refunds | **Implemented in engine, not reachable from the hyper lane** | negative `Receipt` + realized-FX split (1539–1576) |
| Suspended transactions / held baskets | **Model Only for hyper** | `PosOrder.IsHeld`/`HeldAt` + `HoldOrderAsync`/`RecallHeldAsync` exist; `HyperPosController` has no hold/recall endpoint, and the `SuspendResume` capability has no consumer |
| Customers | **Implemented** | `Customer`; hyper links a real customer pre-pay via `SetOrderCustomerAsync` behind the `CustomerIdentity` capability |
| Loyalty | **Partial (earn only)** | `PointsMovement`, derived balance (`Σ Points`), earn + proportional reversal. **Redemption deferred** (HM-9 slice 3); capability ships OFF |
| Wallets | **Not Found** | no entity, no service, no column, no SQL |
| Coupons | **Not Found** | `Promotion` has no code-redemption path; no `Coupon` entity, no usage counter |
| Promotions | **Partial** | single best promotion, percent or amount, item/category/customer/segment targeted, date + qty bounded. No basket-level, no buy-X-get-Y, no stacking (see §7) |
| Discount rules | **Partial** | line-level `DiscountAmount` (system-computed only), a company-wide max-discount ceiling with Warn/Block, and a margin floor. **No cashier discount entry exists in the hyper lane** |
| Pricing | **Implemented** | `PricingService.GetPriceAsync`: list → cost-plus → base → converted, deterministic 6-key ordering |
| Price lists | **Implemented** | `PriceList` + `PriceListLine`, per-currency, per-customer, per-segment, priority, validity, qty break, per-UoM |
| Branch prices | **Implemented via list pinning** | `BranchPosSetting.DefaultPriceListId`; an item absent from a pinned list is **rejected at the till**, never silently converted (`AddLineAsync:596`) |
| Barcodes / multiple barcodes | **Implemented** | primary `Item.Barcode` + `ItemBarcode(Barcode, UoMId)`; multi gated by `BarcodeMulti`; ambiguous barcode → explicit reject |
| Weighted items | **Implemented (engine), Model Only (admin)** | `IsWeighted` + `ScaleCode` + `ScaleBarcodeParser` (GS1 mod-10, config-driven length). **No UI writes either field** |
| Unit conversions | **Implemented** | `UoMConversion(ItemId, From, To, Factor)`; a unit with no conversion is **rejected at add-line**, never factor-1 |
| Packaging | **Partial** | modelled *as a unit* (carton = a UoM with a conversion and its own barcode/price). No distinct packaging entity, no nesting, no GTIN hierarchy |
| Batches | **Implemented** | `StockBatch`; batch forced on tracked inbound; on-hand derived from movements, no stored balance |
| Serials | **Implemented** | `StockSerial` — not used by the hyper lane |
| Expiry | **Implemented** | `StockBatch.ExpiryDate`; FEFO on issue; expired batch blocked; `ExpiryAlerts` report |
| Fresh food | **Partial** | weighted selling + `RecipeAtSale` backflush exist; no tare, no min/max weight guard (HM-D41), no shelf-life-from-production |
| Production / preparation | **Implemented (engine), no hyper screen** | `PosOrderService.PrepareSemiFinishedAsync` / `ReplenishFinishedFromBranchAsync` → `ManufService` + `StockService.TransferAsync`; invoked from `PosController`, i.e. the **restaurant** sidebar |
| Stock movements | **Implemented** | `StockMovement` with `SourceType`/`SourceId`/`SourceLineId`; `StockService` is the sole writer |
| Branch stock | **Implemented (as warehouse)** | branch → warehouse is a 1:1 mapping in `BranchPosSetting.DefaultSalesWarehouseId`. **Branch is not itself a stock dimension** |
| Warehouse stock | **Implemented** | `StockBalance` + `StockCostLayer` per (item, warehouse) |
| Cash movements | **Implemented** | per-terminal drawer account; cash-in via `Receipt`, variance via a `PosShiftClose` JE |
| Payment methods | **Implemented (setup) / Partial (till)** | `BranchPaymentMethod` maps any method to a GL account; **the hyper till hard-codes `"Cash"`** (`HyperPosController.Pay:302`) |
| Split payments | **Implemented in engine, not reachable from hyper** | `PayTendersAsync` (multi-tender), `PaySplitByItemAsync` (N invoices), equal-split parts — all restaurant-only endpoints |
| Gift cards | **Not Found** | no entity, service, column or SQL |
| Supplier-funded promotions | **Not Found** | `Promotion` has no vendor/funding dimension; no rebate accrual |
| Shelf & merchandising data | **Partial** | shelf **label printing** exists (`ShelfLabelService`); `BinLocation` gives Section/Rack/Bin. No planogram, no facings, no shelf-vs-backroom split |
| E-commerce presentation | **Implemented (display only)** | `Item.Store*` columns + `ItemImage` gallery + `StoreController`; no cart, no order |
| Delivery | **Implemented for restaurant only** | `PosOrder` delivery fields, `DeliveryZone`, `Driver`, `DeliveryStatus`; **no hyper endpoint sets any of them** |
| Click and collect | **Not Found** | no reservation-of-stock, no pickup slot, no fulfilment state |

**Model-level observations worth carrying into Master Data (2C):**

1. **`PosOrder` carries eleven restaurant-only columns** (`TableId`, `GuestCount`, `Guests`, `DeliveryZoneId`,
   `DeliveryAddress`, `DeliveryArea`, `DeliveryPhone`, `DeliveryFee`, `DriverId`, `DeliveryStatus`, `TipAmount`/
   `TipMethod`/`TipJournalEntryId`). A hyper order writes `OrderType="Takeaway"` and leaves all of them null/zero. This is
   cheap and harmless today, and it is exactly the "one table, two products" shape Master Data will have to rule on.
2. **`RestaurantTable` is referenced by the shared floor builder** `GetFloorAsync`, which the hyper lane never calls —
   confirming the shared service is *read-partitioned by feature*, not by tenant.
3. **`Promotion` lacks three dimensions** the retail domain needs: `UoMId` (HM-D51 — an amount discount is 20% off a
   0.500 piece and 0.17% off a 60.000 carton), batch (HM-D54 — near-expiry clearance), and vendor funding.
4. **Loyalty balance is derived, never stored** — a deliberate repeat of the batch-on-hand decision, taken to avoid the
   stored-balance lost update recorded in DEV-2026-005. Worth preserving verbatim in any redemption slice.

---

## 6. H3 — End-to-end business flows

Full traces, with transaction boundaries, written data, GL and stock effects, are in
`Stage-002-Hypermarket-Business-Flow-Map.md`. Summary of the 28 mandated flows:

| # | Flow | Hyper status | Entry point |
|---|---|---|---|
| 1 | Cashier login | **Implemented** | `POST /hyper/pos/login` |
| 2 | Opening a shift | **Implemented** | `POST /hyper/pos/start` |
| 3 | Starting a sale | **Implemented (implicit)** | first `POST /hyper/pos/scan` → `EnsureOrderAsync` |
| 4 | Finding / scanning an item | **Partial — scan only** | `POST /hyper/pos/scan`; **no name search, no PLU, no browse** |
| 5 | Adding an item | **Implemented** | `AddLineAsync` |
| 6 | Weighted-item handling | **Implemented** | scale prefix routed *before* lookup; `ScaleBarcodeParser` |
| 7 | Unit conversion | **Implemented** | conversion required at add-line; base conversion at stock issue |
| 8 | Price resolution | **Implemented** | `GetPriceAsync` with the branch list pinned |
| 9 | Discount resolution | **Partial** | system-computed only; no manual discount in the lane |
| 10 | Promotion resolution | **Implemented** | gated by the `Promotions` capability |
| 11 | Customer selection | **Implemented** | `/hyper/pos/customer` behind `CustomerIdentity` |
| 12 | Loyalty application | **Partial — earn only** | `LoyaltyPointsHelper.AccrueForInvoiceAsync`, in-transaction |
| 13 | Payment | **Partial — cash only** | `POST /hyper/pos/pay` hard-codes `"Cash"`; no tendered/change |
| 14 | Split payment | **Not reachable from hyper** | engine exists (`PayTendersAsync`, `PaySplitByItemAsync`) |
| 15 | Invoice creation | **Implemented** | `ReceivableService.CreateSalesInvoiceAsync` |
| 16 | Accounting posting | **Implemented** | one JE: Dr AR / Cr revenue / Cr VAT, then Dr cash / Cr AR, then COGS |
| 17 | Stock deduction | **Implemented** | `StockService.PostMovementAsync`, FEFO where tracked |
| 18 | Printing | **Partial** | A4 invoice via a shared template (`window.print`); **no thermal receipt in the hyper lane** |
| 19 | Suspending a sale | **Not reachable from hyper** | `HoldOrderAsync` exists; no endpoint; capability has no consumer |
| 20 | Resuming a sale | **Not reachable from hyper** | `RecallHeldAsync` exists; no endpoint |
| 21 | Return | **Not reachable from hyper** | `ReturnOrderLinesAsync` exists; restaurant endpoint only |
| 22 | Refund | **Not reachable from hyper** | same |
| 23 | Void / cancellation | **Not reachable from hyper** | `VoidPaidOrderAsync` exists; restaurant endpoint only |
| 24 | Closing a shift | **Implemented** | `POST /hyper/pos/shift/close` |
| 25 | Cash reconciliation | **Implemented** | expected vs counted → variance JE to `520111` |
| 26 | Production / preparation | **Engine implemented, no hyper screen** | driven from the restaurant sidebar |
| 27 | Branch transfer | **Engine implemented, no hyper screen** | `TransferAsync` via `ReplenishFinishedFromBranchAsync` |
| 28 | Online order / delivery | **Not present for hyper** | restaurant-only delivery board |

**The transaction boundary is the strongest part of the implementation.** `PayAsync` opens **one**
`ScopedTx.BeginOrJoinAsync` and every inner service — invoice creation, GL posting, COGS, receipt, tip, receipt-number
allocation, loyalty accrual, idempotency-token insert — **joins** it rather than opening its own. A failure anywhere
discards the whole sale. Two consequences verified in source:

- The idempotency guard is the **in-transaction unique INSERT** of `HyperPayTokens(CompanyId, Token)`, not a pre-read.
  The pre-read at 1089–1094 is explicitly labelled a fast path. The `catch` is index-specific
  (`IsHyperPayTokenDuplicate`, SQL 2601/2627 **and** the index name) — any other failure is rethrown. This is the
  correct shape and it is documented as a pinned rule.
- Receipt numbering is a single `UPDATE … OUTPUT deleted.NextReceiptNo` (`AllocateReceiptNoAsync:225`) — an exclusive
  row lock returning the pre-increment value. Gaps on rollback are accepted; duplicates are impossible.

**The one honest weakness in the flow set** is that the *correction* half of the lifecycle is engine-complete and
UI-absent. A hyper cashier who rings the wrong item at 14:00 has no in-lane path to fix it: return, refund and void all
exist, all post correctly, and none has a `/hyper/pos/*` route. Today the only route is the accounting back-office.

---

## 7. H5–H6 — Master Data dependencies, pricing and promotions

### 7.1 What the hyper lane consumes from Item master

| Field | Hyper use | Breakage risk if Master Data changes it |
|---|---|---|
| `Item.Barcode` | primary scan key; also the shelf-label EAN-13 source | **Critical.** Unique per company and required. Making it optional or non-unique breaks scan resolution and the label barcode |
| `ItemBarcode(Barcode, UoMId)` | secondary/multi-unit scan; **each row carries the unit it sells in** | **Critical.** The unit on the barcode row is what decides "1 carton = 144 base". Dropping `UoMId` silently sells cartons at piece quantities |
| `Item.BaseUoMId` | fallback unit; conversion target | **Critical.** Every conversion is `From → BaseUoMId` on that item |
| `UoMConversion.Factor` | qty → base at stock issue | **Critical.** `AddLineAsync:545–549` *rejects* a unit with no conversion — a migration that drops conversions makes items unsellable rather than mis-priced, which is the safe failure but a hard outage |
| `Item.IsWeighted`, `Item.ScaleCode` | scale-barcode routing; `ScaleCode` unique per company via a **filtered** index | **High.** No UI writes them; any migration must carry them as data. The filtered index must survive |
| `Item.SalesPrice` | fallback only when no list matches | **Medium.** Recorded as a defect twice (HM-D20 currency-blind, HM-D37 remove from sale paths) — a hyper branch with a pinned list never reaches it |
| `Item.DefaultTaxCodeId` | first of three tax resolution steps | **Medium.** Order is item → branch → company (`ResolveTaxRateAsync:384`) |
| `Item.LoyaltyEligible` (nullable) | earn eligibility; `null` inherits `ItemCategory.LoyaltyEligible` | **Low.** Nullable-inherits is a deliberate two-level model; flattening it loses the category default |
| `Item.ItemCategoryId` | GL mapping (inventory/COGS/adjustment/GRNI) **and** promotion targeting **and** report grouping | **Critical.** A category with no `InventoryAccountId` cannot be opening-stocked or sold |
| `ItemComponent` (+`ScrapPct`) | `RecipeAtSale` backflush and cost-plus pricing | **High.** Also the "manufactured?" test in `ResolveItemCostAsync` |
| `Item.IsComposite` / `CompositeType` / `ProductionMethod` | **not read by the hyper sale path** | Low for hyper; high for the conflation Master Data must resolve |
| `Item.ImagePath` / `ItemImage` | cart line thumbnail (`GetOrderAsync`) and storefront gallery | Low |
| `Item.QuickCode` | POS PLU — **read by the restaurant quick menu only**; the hyper lane never reads it | Low today, and it is the field a hyper PLU entry would use |

**The one non-negotiable Master Data gate for Hypermarket** is that `ItemBarcode.UoMId`, `UoMConversion.Factor` and
`Item.ScaleCode`'s filtered uniqueness survive byte-for-byte. Everything else in the list degrades to a wrong price or a
refused sale; those three degrade to a **wrong quantity posted to stock and COGS**, which is the one class of error the
two-writers rule cannot reverse cleanly.

### 7.2 Pricing engine — traced order of operations

`PricingService.GetPriceAsync` (136–223). Candidate rows are `PriceListLine ⋈ PriceList` filtered by: active list ·
item match · `MinQty ≤ qty` · pinned list id when the branch pins one · unit match (`l.UoMId == uomId || l.UoMId == null`) ·
currency match (`(pl.CurrencyId ?? functional) == docCur`, or `PricingMode == "CostPlus"`) · customer/segment targeting ·
both list and line validity windows against `asOf`.

**Selection is fully deterministic — six keys, no `FirstOrDefault` on an unordered set** (170–177):

1. explicit `UoMId` match for the requested unit beats a null-unit line
2. customer-specific beats segment
3. segment-set beats general
4. higher `PriceList.Priority`
5. highest `MinQty ≤ qty` (most specific break)
6. smallest `LineId` — stable final tie-break

Then: `CostPlus` computes `cost × (1 + markup)` in functional currency and converts; otherwise the list price is used;
if no candidate and the document currency **is** functional, `Item.SalesPrice`; if the document currency is foreign, an
optional *temporary* converted suggestion governed by `InventorySettings.ConvertBasePriceForForeignDocs`, else
`Source="none"` with price 0.

**Promotion layer** (`ApplyBestPromotionAsync`, 227–291) — applied **after** list resolution, **sequentially**, and
**single-winner**: `net = unit × (1 − listDisc)` then minus the best promotion's per-unit amount. Amount promotions are
converted to the document currency and capped at the net. Ranking is again explicit (273–278):
**specificity (item > category > general) → `Priority` → largest amount → smallest `Promotion.ID`**. `Priority` is
deliberately ranked **above** the discount amount so the user's field means something. The result is folded back into a
single effective `DiscountPercent`, so the till, the invoice and the GL all see **one net-revenue number** — there is no
"discount allowed" account by design (recorded decision, `AUDIT-DEVIATIONS.md:570`).

**Support matrix, from source:**

| Feature | Status | Note |
|---|---|---|
| Item price | ✅ | `Item.SalesPrice` fallback |
| Branch price | ✅ | via pinned list; absent item **rejected** |
| Customer price | ✅ | `PriceList.CustomerId` |
| Quantity tiers | ✅ | `MinQty`, most-specific wins; `0` floor now legal (HM-D43) so sub-kilo weighed sales price correctly |
| Date ranges | ✅ | on both list and line, and on promotions |
| **Time ranges** | ❌ Not Found | date-only; no happy-hour / time-of-day window |
| Promotions | ✅ | single best |
| Coupons | ❌ Not Found | |
| Manual discounts | ❌ **no lane entry point** | `PosOrderLine.DiscountAmount` is only ever system-written |
| Line discounts | ✅ (system) | |
| **Basket discounts** | ❌ Not Found | no order-header discount field, no basket evaluator |
| **Buy X get Y** | ❌ Not Found | deferred by name |
| **Mix and match** | ❌ Not Found | |
| **Second-item discount** | ❌ Not Found | |
| Category discounts | ✅ | `Promotion.ItemCategoryId` |
| **Brand discounts** | ❌ Not Found | `Brand` exists on `Branch`/`PosOrder`, not on `Promotion` |
| **Supplier-funded** | ❌ Not Found | |
| **Loyalty discounts** | ❌ Not Found | earn only; redemption deferred |
| Stacking & priority | **Priority yes, stacking no** | exactly one promotion applies |
| Margin protection | ✅ | `CheckMarginAsync`: Off/Warn/Block, per-item override, **skipped when cost is 0** so it never false-blocks |
| Approval-required discounts | ✅ engine, ⚠ not in the lane | `EvaluateLineDiscountsAsync` Warn/Block against `MaxLineDiscountPct`; no hyper caller |

**Rounding behaviour — the most carefully engineered area in the whole module.** Two rounding systems are kept
deliberately separate:

- **Currency rounding** — every money value goes through `ICurrencyRounding` with explicit `MidpointRounding.AwayFromZero`,
  at the *document* currency's decimals for what was rung (KWD 3) and the *functional* currency's decimals for cost and
  margin (EGP 2). There is no `?? 2` fallback: an undefined currency **throws**.
- **Commercial retail rounding** — `PricingService.PriceRound` snaps to a 5- or 10-fils step, and is explicitly *not*
  routed through `ICurrencyRounding`. Order matters and is documented: **currency precision first, then the fils step**,
  because the reverse lets a coarse currency erase the step. A step finer than the currency's smallest unit is
  **refused**, not silently applied.

A third, deliberate non-money rounding: loyalty points use `Math.Floor` on a **count**, again explicitly outside
`ICurrencyRounding`.

**Sell-time safety net:** a promotion or discount that drives a *priced* line to zero or below is rejected with a clear
message (`AddLineAsync:606`) — never a silent zero or negative line. A genuinely zero-priced line (a free modifier
component) is unaffected.

### 7.3 Bulk price change and shelf labels

`BulkPreviewAsync` → `BulkExecuteAsync` → `BulkUndoAsync` implement preview / confirm / audit / undo with three
properties worth preserving: preview **writes nothing**; execute re-verifies the caller's shown baseline against live
prices and refuses on drift (optimistic concurrency); undo writes the **stored old price**, never a reverse percentage,
because +5% then −5% ≠ the original. `PriceChangeLog` keys on `(PriceListId, ItemId, UoMId)` and never on `LineId`,
because `SaveAsync` full-replaces a list's lines and renumbers ids (HM-D47 — a known, deferred defect).

Scope is limited to **one** optional `ItemCategoryId` (HM-D50). Shelf labels render a self-computed EAN-13 SVG with
correct module ratios and GS1-minimum quiet zones, plus an encode→decode round-trip self-test; an invalid EAN-13 prints
digits as text rather than a broken symbol. Output is an A4 browser grid — no label-printer driver (HM-D48).

---

## 8. H7–H8 — Inventory / WMS and accounting / cash control

### 8.1 Inventory integration

| Question from the brief | Answer, from source |
|---|---|
| Is store stock separate from warehouse stock? | **No.** A hyper branch maps to exactly one warehouse (`BranchPosSetting.DefaultSalesWarehouseId`); the till issues from it. There is no store-vs-DC distinction |
| Is stock branch-scoped? | **Only transitively.** `StockBalance` is keyed `(CompanyID, ItemId, WarehouseId)`. Branch is not a stock dimension |
| Does checkout deduct immediately? | **Yes, at pay — never before.** An open order writes nothing to stock or GL. `PayAsync` issues inside the settlement transaction |
| Is negative stock allowed? | **Per warehouse.** `Warehouse.AllowNegativeStock`; when false, `PostMovementAsync:502` blocks the issue and the whole sale rolls back |
| Overselling protection? | **Yes at the balance check, no as a reservation.** There is no soft allocation, so two concurrent lanes are arbitrated by the row lock, not by a reservation model. This is precisely why offline selling was deferred (§6.28) |
| FEFO / FIFO? | **FEFO on issue for expiry-tracked items** (`FefoAllocateAsync`), splitting across nearest-expiry batches, excluding expired, with a clear message when short. **FIFO cost layers** exist (`StockCostLayer`) alongside moving average |
| Replenishment? | **Two unrelated mechanisms.** `ItemWarehouseSetting` reorder point + `InventoryController.Planning` (back-office), and `BranchItemSourcing` four sourcing methods used by the *sale* path. Neither is a store-replenishment (shelf-fill) engine |
| Shelf vs backroom stock? | **Not distinguished.** `BinLocation` (Section/Rack/Bin) and `BinStock` exist and hold **quantity only, never value** — but the hyper sale path never passes a `BinLocationId` |
| Are the bin foundations used by Hypermarket? | **No.** `SalesLineInput` has no bin field; `PayAsync` passes only `WarehouseId`. The bin layer is real, correct and entirely unused by retail today |
| Reservations? | **Not Found** for stock. `Reservation` in the POS model is a *table* booking, not a stock reservation |

Waste and markdown: `StockWriteOff` handles waste and is batch-aware (an expired write-off **must** name its batch, so
FEFO cannot auto-pick it); `ExpiryAlerts` reports near-expiry and expired on-hand. **There is no markdown engine** — no
price-reduction-on-approaching-expiry, and `Promotion` has no batch dimension to build one on (HM-D54).

### 8.2 Accounting and cash control

Posting is done by exactly two writers, and the hyper lane adds none:

| Effect | Account resolution | Posted by |
|---|---|---|
| Revenue | `RevenueAccountId` per invoice line — `4101`, else the lowest postable `4*` | `ReceivableService` → `JournalEntryService.CreateAndPostAsync` |
| AR | `Customer.ControlAccountId` (validated non-zero and active before a customer may be linked) | same |
| Output VAT | resolved item → branch → company default | same |
| Cash | **terminal drawer → branch `Cash` payment method → `110101`** | `CreateReceiptAsync` |
| COGS / inventory credit | category `CogsAccountId` (`510101`) / `InventoryAccountId` (`1103`) | `StockService.PostMovementAsync(PostToGl: true)` |
| Discounts | **no account** — net-revenue method, by decision | n/a |
| Returns | reverses revenue + VAT, returns stock at original cost, opens an AR credit | `CreateSalesReturnAsync` |
| Refunds | Dr AR-control / Cr drawer, **plus realized FX to `4902`/`5902`** when the return-day rate differs from the invoice rate | `ReturnOrderLinesAsync` |
| Shift variance | Dr/Cr drawer ↔ `520111`; **no JE when variance is zero** | `CloseShiftAsync` |
| Tips | Dr cash/card, Cr `210207` — liability, not revenue, not taxed | `PostTipAsync` (restaurant) |
| Gift-card / wallet liability | **Not Found** | — |

**Posting time** is settlement only. **Duplicate-post protection** is threefold: `HyperPayTokens` unique INSERT
(concurrent same-intent), the `Status != "Open"` guard (different intent on the same cart), and Post-Redirect-Get
(ordinary F5). **Rollback** is all-or-nothing on one ambient transaction. **Company source** is the hard-coded
`PosCompanyId = 1` in both hyper controllers — pinned by name in `Correction005StructuralTests` as 2 of 13 known
hard-coded companies. **Branch source** is the server-side session (`HyperCtx.BranchId`), never a request parameter.

Two cash-control facts worth stating plainly:

- **`ExpectedCash` counts cash payments and cash tips of non-voided orders, and does not yet subtract refunds** —
  `ExpectedCashAsync:936` has the comment "refunds land in RC-6c", and `ZReportDto.ReturnsTotal` is hard-coded `0m`
  (`GetShiftZReportAsync:924`). Since the hyper lane cannot refund at all, this is currently unreachable rather than
  wrong — but it becomes a real reconciliation defect the moment a hyper refund endpoint ships. **This is the single
  most important sequencing constraint in the gap analysis.**
- The variance JE converts the variance **once** to functional currency and uses the same value on both lines, so it
  balances by construction even with no eligible P&L line.

---

## 9. H9 — Security and company isolation

### 9.1 The gate model

Three independent gates, deliberately not mixed:

| Surface | Gate | Enforced by |
|---|---|---|
| `/hyper` back-office | `Session["Employee"]` | `SessionValidationMiddleware` (bypass granted only to `/hyper/pos`) |
| `/hyper/pos` lane | `Session["HyperCtx"]` + ASP.NET Identity sign-in | `HyperPosController` itself |
| every `/hyper/pos` request | branch activity must still be `Hyper` | `PosLaneActivityGuardAttribute("HyperCtx","hyper","/hyper/pos/login")` |

The lane guard re-checks on **every** action, so a session minted before the guard existed, or a direct hit on
`/hyper/pos/lane`, is rejected — not just login. On mismatch it **drops the session** and returns 403 JSON for
AJAX/non-GET or redirects for GET.

Login enforces three things in sequence (`Login:83–104`): a POS role must exist at some branch
(`ResolveByUserIdAsync` returns null if `BranchUserRoles` is empty), the branch's activity must be whitelisted for
this lane (`IsActivityAllowedForLane` — an explicit **whitelist**, so a new activity never leaks in), and the
employee's company must equal the branch's company (**cross-company login refused with a clear message**).

### 9.2 Every mutating hyper endpoint, classified

| Endpoint | Classification | Controls present |
|---|---|---|
| `POST /hyper/pos/login` | **anonymous by design** | credential check + role + activity + company guard |
| `POST /hyper/pos/start` | **lane/session guard only** | `HyperCtx` + lane guard + terminal-belongs-to-branch check. **No role check** |
| `POST /hyper/pos/scan` | **real role authorization** | `CanSell` + capability gates + company-scoped item lookup |
| `POST /hyper/pos/line/qty` | **real role authorization** | `CanSell`; order id from session, not the request |
| `POST /hyper/pos/line/remove` | **real role authorization** | `CanSell` |
| `POST /hyper/pos/pay` | **real role authorization** | `CanSell` + open-shift requirement + idempotency |
| `POST /hyper/pos/shift/close` | **real role authorization** | `CanSell` + cross-company terminal guard in `CloseShiftAsync` |
| `POST /hyper/pos/customer/add` | **real role authorization** | `CanSell` + `CustomerIdentity` capability |
| `POST /hyper/pos/customer/attach` | **real role authorization** | `CanSell` + capability + **fail-closed AR-control-account guard** |
| `POST /hyper/pos/invoice/stamp` | **real role authorization + verified in-body authorization** | `CanSell` + `OfficialInvoice` capability + `BranchOwnsInvoiceAsync` + company predicate + set-once + tax-zero-only |
| `POST /hyper/pos/pricecheck` | **lane/session guard only** | capability gate; read-only, writes nothing |
| `GET /hyper/pos/receipts`, `/invoice/print/{id}` | **verified in-body authorization** | terminal + branch + company scoping; read-only |
| `GET /hyper`, `/hyper/reports/*` | **authentication only** | `Session["Employee"]`; read-only; scoped to `ActivityPresetCode=="Hyper"` |
| `GET/POST /api/dev/hm*` | **DevOnly** | `[DevOnly]` → 404 outside Development |

**Record-level access is genuinely good** where it exists: `BranchOwnsInvoiceAsync` proves the invoice is the pay result
of an order on *this* branch, not merely that the caller has a role. `Receipts` is scoped to
`(company, branch, terminal)` so a cashier cannot see another lane's receipts.

**Direct `DbContext` access from the lane controller is extensive** — `HyperPosController` injects `CrossDbContext` and
runs 20+ queries directly (barcode resolution, scale config, item lookup, currency, capability, customer). All are
company-predicated. It is not an isolation defect; it is a layering one, and it is the reason the barcode-resolution
logic is duplicated between `Scan` and `PriceCheck` in slightly different forms (`Scan` rejects a multi-item barcode;
`PriceCheck` takes the first match).

### 9.3 Stage 2 Hypermarket security delta

No Stage 1 evidence was altered. Four findings are **new** relative to the Stage 1 record and belong in a Stage 2 delta:

| ID | Finding | Severity |
|---|---|---|
| **HS-1** | `POST /hyper/pos/start` (open a shift, which sets the drawer's opening float) has **no role check** — any lane session may open a shift. Its restaurant twin has the same shape, so this is a shared, not hyper-specific, hole | **High** |
| **HS-2** | The `OfficialInvoice` capability that gates invoice printing and beneficiary stamping can only be enabled by `DevSeedController` (`hm8-accept`). There is no production path to turn it on, and no production path to turn it **off** either once set by hand | **Medium** (availability, not exposure) |
| **HS-3** | Two working, mutating-adjacent screens (`BulkPriceChange`, `ShelfLabels`) have no menu entry. `BulkPriceChange`'s three mutating actions **do** carry `[InvPerm("doc")]`; the GET shells carry authentication only. Unlinked ≠ unprotected, but it means the price-change audit trail is operated by URL | **Medium** |
| **HS-4** | `HyperController` and `HyperPosController` are 2 of the 13 controllers resolving company from a compile-time constant (`PosCompanyId = 1`) — already pinned by `Correction005StructuralTests`, restated here because retail is the highest-volume writer among the thirteen | **High** (carried, not new) |

---

## 10. H11–H13 — Reports, integrations, tests

### 10.1 Reports (detail in the Gap Analysis)

Two hyper reports exist, both read-only, both labelled placeholder layouts: **Hyper Sales** (total, order count, top 10
items, by category) and **Item Margin** (per item: qty, revenue in document currency, COGS and margin in functional
currency, margin %, an *indicative* document-currency margin, and a three-state cost status).

The cost-status guard is the most valuable thing in the reporting layer and deserves to survive any redesign:
**Costed** (cost movement with value), **ZeroCost** (movement present, value 0), **Unposted** (no cost-bearing movement
at all) — and an Unposted item's margin renders as "—", never 0 and never 100%. The same display guard was applied to
`CustomerAnalytics` and the Executive dashboard **without changing any stored or computed number**.

Of the 23 report types the brief asks about: **2 Existing**, **3 Partial**, **11 Data Available but No Report**,
**7 Data Not Available**. Nothing scores "Missing with data available" that would require new capture — which is the
useful conclusion: **the hyper reporting gap is a presentation gap, not a data gap**, for two-thirds of the list.

### 10.2 Integrations

| Integration | Status |
|---|---|
| Barcode scanners | **Implemented by convention** — keyboard-wedge into a focused text input. No device code, and none needed |
| Receipt printers | **Not found for the hyper lane.** The restaurant terminal prints via `window.print`; the hyper lane has only an A4 invoice. `PosTerminal.ReceiptPrinterName`/`ReceiptPaperWidthMm`/`ReceiptCopies` are **configured but unused** by hyper |
| Cash drawers | **Configured but unused.** The `CashDrawer` capability has no consumer; no kick command exists |
| Customer displays | **Not found** |
| Payment terminals | **Not found.** `BranchPaymentMethod` is a GL mapping, not an EFT integration; the hyper till is cash-only |
| Scales | **Implemented as a data contract, not a device.** `ScaleBarcodeParser` + 6 branch config columns consume a printed variable-measure label. No serial/IP scale driver. `ValueType='Price'` is explicitly **rejected as unsupported** |
| Label printers | **Not found** — A4 browser print only (HM-D48) |
| Electronic shelf labels | **Not found** |
| Mobile devices | **Not found for retail.** `crossbuy_mobile` is HR/finance/inventory-view only; no POS screen |
| Delivery platforms | **Not found** |
| E-commerce | **Display-only storefront**, correctly isolated through `BeginPublicCatalogRead` pinned to `Store:StoreCompanyId` — never the administrative bypass |
| Loyalty providers | **Not found** — internal ledger only |
| Government tax / e-invoice | **Adapter/interface only.** `IEtaInvoiceService` with `EtaInvoiceServiceStub` returning `NotConfigured`. `EtaSettings`, `SalesInvoice.EtaUuid`/`EtaStatus` exist |

Failure handling and monitoring across integrations: essentially **none**, because there are essentially no
integrations. The one place graceful degradation was engineered is connectivity, and it was engineered in the right
order — server idempotency and receipt recovery **first** (they work with no detector), the `navigator.onLine` banner
**last** and explicitly documented as *not* the protection.

### 10.3 Tests

**There is no automated test for any hypermarket business behaviour.** The 466 xUnit facts in `CrossBuy.Tests` cover
the platform kernel, permissions, isolation and DI. Exactly two touch hyper, and both are **structural** (they read
source text): `Correction005StructuralTests` pins the hard-coded company constants and asserts the stamp action keeps
its branch checks and gained a role check; `Stage1PermissionBacklogTests` lists three hyper lane actions in the
authorization backlog.

Hyper behaviour is instead verified by **12 `[DevOnly]` HTTP acceptance endpoints** (`hm4-accept`, `hm5-accept`,
`hm6-accept`, `hm8-accept`, `hm9-s1-accept`, `hm9-s2-accept`, `hm10-accept`, `hm-b-accept`, `hm7-count-accept`,
`hm7-landed-accept`, `hm16-accept`, plus guard tests). These are genuinely rigorous — every number is re-read from a new
DB query rather than a tracked entity, each run is asserted idempotent over three runs, and each closes with
`failedCount 0` on the integrity check. They are also, by construction:

- **not runnable in CI** — they need a populated SQL Server, a cookie jar (HM-D59), Development environment, and a
  seeded `Employee` session blob (HM-D58);
- **not regression tests** — nothing fails a build when a hyper behaviour breaks;
- **dependent on dev-only baselines** — `JvBaselineMaxNo`, `PosOrderBaselineMaxId`, `unbatched_inbound_tracked=19`,
  `cogs_impact newNoCogs=2` are compiled constants tuned to the dev database, and the runbook correctly warns they will
  misclassify legacy-vs-new on production.

Coverage by area: calculations **dev-endpoint only** · pricing **dev-endpoint only (11 cases)** · promotions
**dev-endpoint only (11 cases)** · payments **dev-endpoint only (11 cases)** · stock/FEFO **dev-endpoint only (9 cases)**
· accounting **dev-endpoint + integrity check** · shifts **dev-endpoint** · returns **dev-endpoint (restaurant path)** ·
branch isolation **dev-endpoint** · company isolation **xUnit (platform-level) + dev-endpoint** · concurrency
**dev-endpoint (`hm10-accept` T3, `hm1-d6-race-test`)** · peripherals **none** · offline **synthetic only, never proven
on a real path**.

Per the brief, **no skipped SQL test is counted as coverage**, and no dev endpoint is counted as an automated test.

---

## 11. H14 — Maturity assessment

Scored 0–5 on verified evidence only. Each score names what would raise it.

### 11.1 Backend capability maturity — **3.2 / 5**

| Dimension | Score | Evidence |
|---|---|---|
| Product & Master Data | **3** | multi-barcode + per-unit price + conversion + weighted + batch/expiry all real; no variants, no packaging hierarchy, no admin UI for weighted/scale |
| Checkout | **3** | one transaction, idempotent, atomic receipt numbers, currency-correct; cash-only, no tender/change, no in-lane correction |
| Pricing | **4** | deterministic 6-key resolution, cost-plus, per-unit, bulk change with preview/audit/undo, margin floor, two separated rounding systems |
| Promotions | **2** | single-winner line-level only; no basket, no BXGY, no coupon, no stacking, no vendor funding |
| Payments | **2** | engine supports multi-tender and split; the hyper lane exposes cash only |
| Shift & cash control | **3** | full open/expected/counted/variance with a real JE; refunds not yet in `ExpectedCash` |
| Inventory integration | **3** | immediate deduct at pay, FEFO, negative-stock guard, batch forcing; bins unused, no reservation, no shelf/backroom |
| Accounting integration | **4** | two writers only, reverse-never-delete, realized FX on refund, duplicate-post protection, subledger reconciliation checks |
| Branch operations | **2** | activity isolation is enforced and clean; sourcing/production/transfer engines exist but are driven from the restaurant sidebar; no hyper setup screen |
| Security | **3** | per-request lane guard, whitelist, cross-company refusal, record-level invoice ownership; hard-coded company, one shift action without a role check |
| Reporting | **2** | two placeholder reports with an excellent cost-status guard; 11 report types have the data and no report |
| Hardware integration | **1** | scanner by convention, scale by printed label; no printer, drawer, display, EFT, ESL |
| Omnichannel | **1** | display-only storefront; no cart, no click-and-collect, no hyper delivery |
| Testing & reliability | **2** | rigorous but manual, dev-only, non-CI, dev-tuned baselines; zero automated business tests |

### 11.2 UX/UI maturity — **1.6 / 5**

The cashier lane is the deciding evidence: full-page postbacks, one input at a time, no keyboard model beyond browser
defaults, no touch targets designed for a counter, no offline behaviour beyond a banner, and three of its own screens
carrying a literal "awaiting the final design" notice. RTL/LTR parity **is** consistently handled (every hyper view
branches on `isAr`, and the layout swaps RTL bundles), and Metronic 8 usage is consistent. Empty and error states exist
on the lane; loading, no-permission and offline states largely do not.

### 11.3 Enterprise retail maturity — **1.9 / 5**

Against what a real multi-store grocery operation needs: no basket-level promotions, no coupons, no gift cards or
wallet, no loyalty redemption, no markdown, no shelf/backroom distinction, no replenishment for stores, no ESL, no EFT,
no customer display, no click-and-collect, no queue or productivity measurement, and no automated regression suite. The
engine is sound enough to build all of it on — which is exactly why the recommendation is *extend*, not *rebuild*.

---

## 12. H10 — UX/UI assessment summary

Per-screen assessment against all eighteen mandated attributes is in
`Stage-002-Hypermarket-Screen-Inventory.md`. Classification totals across the **21 screens** in scope:

| Classification | Count | Screens |
|---|---|---|
| **Preserve** | 2 | `SalesInvoicePrint` (shared template, works), `PosLogin` |
| **Minor Improvement** | 4 | `PosStart`, `Receipts`, `PriceCheck`, `Batches` |
| **Major Redesign** | 5 | **`PosLane` (the cashier workspace — the single highest-value screen in the module)**, `Hyper/Dashboard`, `HyperSales`, `HyperItemMargin`, `Pos/Setup` |
| **Replace UI Only** | 5 | `BulkPriceChange`, `ShelfLabels`, `PromotionEditor`, `PriceListEditor`, `ExpiryAlerts` |
| **Consolidate** | 4 | `Promotions`+`PriceLists` list shells; `Hyper/Customer` into the redesigned lane; `Hyper/Dashboard` into a real hyper home |
| **Legacy / Remove Candidate** | 1 | none of the hyper screens; `Views/Home/Index.cshtml` (a 12-line ASP.NET scaffold stub, unrelated to retail but adjacent in the switcher path) |

**No screen's weakness justifies a backend replacement.** In five of the five Major Redesign cases the server-side
computation is already correct and complete — `PosLane` in particular does no arithmetic at all; the server computes,
the view formats. That is the ideal precondition for a UI rebuild.

---

## 13. H4 + H16 — POS variant classification and recommended shape

### 13.1 What exists, and how it is divided

| Product | Lane / entry | Session key | Dedicated code | Shares |
|---|---|---|---|---|
| Restaurant POS | `/pos` (`PosAppController`, 858 lines) | `PosCtx` | `Terminal.cshtml` (51 KB), `pos-terminal.js` (1642 lines), `pos-offline.js`, PWA manifest + service worker | `PosOrderService`, `PosSetupService`, `PosAccessService`, `PricingService`, both writers |
| Hypermarket POS | `/hyper/pos` (`HyperPosController`, 549 lines) | `HyperCtx` | `Views/Hyper/*` (9), `_LayoutHyperPos`, `ScaleBarcodeParser`, `LoyaltyPointsHelper`, `HyperPayTokens` | the same shared set |
| Retail POS | — | — | **none** | `ActivityPreset` lists `Retail`; `IsActivityAllowedForLane` denies it. A declared, unclaimed activity |
| Mobile POS | — | — | **none** | `crossbuy_mobile` has no POS screen |
| Storefront ordering | `/Store/*` | anonymous visitor | `StoreCatalogService`, 2 views | display only; no order |
| KDS | `/pos/kitchen` | `PosCtx` | `pos-kds.js`, `Kitchen.cshtml` | restaurant only |
| Delivery board | `/pos/delivery` | `PosCtx` | `pos-delivery.js`, `Delivery.cshtml` | restaurant only |

**What is shared, deliberately and correctly:** the order/settlement engine (`PosOrderService` — 1957 lines, one
implementation, one transaction discipline), terminal/shift/capability setup (`PosSetupService`), access resolution and
the **single** lane whitelist (`PosAccessService.IsActivityAllowedForLane` — the one place any future lane registers),
pricing, and both writers. `PayAsync` gained an *optional trailing* `idempotencyToken` parameter rather than being
duplicated into a hyper-specific financial path — the right call, explicitly recorded.

**What is duplicated:** the lane shell (`_LayoutHyperPos` is a verbatim clone of `_LayoutPosApp`), the login and
terminal-pick screens (documented clones), the capability catalogue (`PosController.Capabilities` 7 restaurant keys vs
`HyperController.Capabilities` 10 hyper keys, sharing only `Weight`), and barcode resolution (twice inside
`HyperPosController` itself, in two subtly different forms).

**What is incorrectly coupled — three items:**

1. **Hyper branch configuration lives on the restaurant sidebar.** `Views/Pos/Setup.cshtml` is the only screen that
   assigns `ActivityPresetCode` and edits capabilities, and `PosController.SaveCapabilities` builds its dictionary from
   the **restaurant** 7-key list. Saving capabilities for a hyper branch from that screen writes only restaurant keys.
   (The hyper keys survive, because `SaveCapabilitiesAsync` upserts rather than replaces — so this is a
   *cannot-edit*, not a *data-loss*, defect.)
2. **Hyper production, sourcing and transfer are driven from `PosController`** (`ItemSourcing`, `SourcingOverview`), so a
   hypermarket manager configures fresh-food sourcing inside the Restaurant system.
3. **`PosController.Dashboard` mixed restaurant and hyper sales** until HM-10 slice B added an optional `activity`
   filter — and the filter is **opt-in**, so the default view still mixes them. Financial reports remain
   company-unified by design, which is a stated decision, not a defect.

### 13.2 Is Hypermarket a configuration of POS, or a distinct platform?

**Neither.** The evidence points at a third answer, and it is the answer the code has already half-built.

It is **not a configuration**: it has its own route, session key, layout, capability catalogue, activity whitelist
entry, idempotency table, barcode-resolution semantics, currency (KWD 3dp against an EGP-functional company), tax
resolution level (branch), reports and deploy runbook. Ten phases of divergence are not a settings row.

It is **not a distinct platform**: it shares the order model, the settlement engine, the pricing engine, terminal/shift
setup, access resolution, and both writers. Forking those would duplicate the one part of the module that is
demonstrably correct, and would double the surface for the parallel team's kernel coupling (HM-D53) to bite.

**Separate controllers here represent separate products sharing one engine — not legacy duplication.** The clone
comments in `_LayoutHyperPos` and `PosStart.cshtml` say "verbatim clone" explicitly; the divergence since is real
functional divergence, not drift.

### 13.3 Recommended shape

**A shared Retail Core built on the existing POS engine, plus a Hypermarket pack.**

- **Retail Core** — what both lanes and any future lane consume: the order/settlement engine, terminal/shift/cash
  control, pricing + promotions, barcode/unit resolution, capability gating, the lane whitelist, and a *single* lane
  shell with slots. This is 80% assembled already; the work is extraction and de-duplication, not construction.
- **Hypermarket pack** — scale barcodes and weighted selling, multi-barcode/multi-unit checkout, shelf labels, price
  check, expiry/FEFO retail enforcement, loyalty, the hyper reports, and the hyper back-office setup that today does
  not exist.
- **Restaurant pack** — tables/floor, modifiers, KDS, delivery/drivers, reservations, tips.
- **`Retail` stays an unclaimed activity** until a third lane needs it. It should not be quietly aliased to `Hyper`;
  the whitelist is explicit for a reason.

Dependencies on the Stage 2+ roadmap, with the reason each is a real dependency rather than a preference, are in
`Stage-002-Hypermarket-Roadmap-Proposal.md`. In short: **Master Data (2C) is a hard predecessor** (barcode unit,
conversion factor, scale-code uniqueness — the three fields whose corruption posts wrong quantities to stock and COGS);
**Design System tokens (2B) are a hard predecessor for the cashier rebuild** (it is the module's highest-value screen
and must not be built twice); **WMS (Stage 4) is a successor** that will finally consume the unused bin layer; **Pricing
& Promotions is where basket-level evaluation belongs**, not in the till.

### 13.4 Expected screens

A rebuilt Hypermarket needs an estimated **14–19** screens, of which **9 are redesigns of existing screens** and
**5–10 are genuinely new** — dominated by the hyper back-office setup that has no screen at all today (branch
capabilities, scale-barcode format, branch tax and currency, loyalty rate, terminal and payment-method setup in a hyper
context). Detail and ranges are in the Screen Inventory and Roadmap Proposal.

---

## 14. Dependency impact on the rest of Stage 2

| Consumer | Impact of this assessment |
|---|---|
| **Master Data (2C)** | Adds three **non-negotiable** non-regression assertions to the migration gate: `ItemBarcode.UoMId` preserved per row, `UoMConversion.Factor` preserved per (item, from, to), `Item.ScaleCode` filtered-unique per company. Also supplies the packaging-vs-unit and sales-BOM-vs-manufacturing-BOM evidence the reassessment flagged as open |
| **Pricing & Promotions** | The engine's deterministic ordering, dual rounding systems and net-revenue decision should be treated as **accepted architecture** and preserved; basket-level evaluation, coupons and BXGY are the actual scope |
| **WMS (Stage 4)** | Retail is the first real consumer of `BinLocation`/`BinStock`, which today hold quantity only and are unused by the sale path. Shelf-vs-backroom and store replenishment belong here, not in the till |
| **Finance** | No change requested and none should be made: posting is correct. The one live constraint is that `ExpectedCash`/`ReturnsTotal` must be completed **in the same slice** as any hyper refund endpoint |
| **CRM & Loyalty** | Loyalty redemption (HM-9 slice 3) is deferred with its negative-balance policy and liability-settlement model already decided. It belongs to CRM/Loyalty, not to the till |
| **Commerce** | Click-and-collect is the natural first omnichannel step and needs the stock **reservation** model that does not exist — the same missing primitive that blocked offline selling |
| **Reporting & AI** | 11 report types have the data and no report. The cost-status three-state guard is a reusable pattern and should be lifted into the reporting standard |
| **Design System (2B)** | `_LayoutHyperPos` is one of the 11 duplicated shells already counted. The cashier lane is the strongest argument for an *operational* screen tier distinct from the desktop tier |

---

## 15. Completion gate

| # | Gate | Status |
|---|---|---|
| 1 | All hypermarket-related files inventoried | **Yes** — 73 files, CSV with 11 attributes each |
| 2 | Models, services, controllers, views, SQL inspected | **Yes** — including all 9 hyper-phase SQL scripts read in full |
| 3 | End-to-end checkout and shift flows traced | **Yes** — 28 flows, transaction boundaries named |
| 4 | Accounting and stock effects traced | **Yes** — §8.2, account-by-account |
| 5 | Restaurant POS and Hypermarket responsibilities distinguished | **Yes** — §13.1, incl. 3 incorrect couplings |
| 6 | Master Data dependencies documented | **Yes** — §7.1, with 3 non-negotiable assertions |
| 7 | Pricing and promotions traced from source | **Yes** — §7.2, ordering + rounding order |
| 8 | Security assessed | **Yes** — §9, every mutating endpoint classified; 4-item Stage 2 delta |
| 9 | Screens assessed individually | **Yes** — 21 screens, Screen Inventory |
| 10 | Reports and integrations inventoried | **Yes** — §10.1, §10.2 |
| 11 | Tests assessed | **Yes** — §10.3; no dev endpoint counted as automated coverage |
| 12 | Evidence-based maturity calculated | **Yes** — §11, three separate scores |
| 13 | Gaps and improvements prioritized | **Yes** — Gap Analysis, 4 priority bands |
| 14 | Roadmap placement proposed | **Yes** — §13.3 + Roadmap Proposal; roadmap files **not** edited |
| 15 | No production implementation performed | **Yes** — no file under `CrossBuy/` created or modified |
| 16 | No SQL executed against `CrossBuyDB2` | **Yes** — scripts were read as text; no client was invoked |

**Stopping here as instructed.** The remaining Phase 0 documents (roadmap completion, screen forecast, Master Data,
WMS, Retail, Design System) are not advanced until this assessment is reviewed.
