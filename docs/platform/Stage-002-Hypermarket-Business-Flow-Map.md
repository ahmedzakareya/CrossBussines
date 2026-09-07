# Stage 2 — Hypermarket Business-Flow Map

**Traced from source. No code executed, no SQL run.** Companion to
`Stage-002-Hypermarket-Current-State-Assessment.md` (§6).

Line references are to the files as read during this assessment. Every flow reports: entry screen · controller/API ·
service chain · transaction boundary · data written · accounting effect · inventory effect · BusinessEvents ·
notifications · authorization · company source · branch source · failure/rollback · tests.

**Standing facts that apply to every flow below** (stated once instead of repeated 28 times):

| Aspect | Hyper lane behaviour |
|---|---|
| **Company source** | `HyperPosController.PosCompanyId = 1` — a compile-time constant, pinned by name in `Correction005StructuralTests`. `HyperController` carries the same constant |
| **Branch source** | `HyperCtx.BranchId` from the **server-side session** only. No hyper endpoint accepts a branch id from the request |
| **Outer gate** | `SessionValidationMiddleware` bypasses `/hyper/pos` (not `/hyper`); the lane enforces its own `HyperCtx` gate |
| **Per-request gate** | `PosLaneActivityGuardAttribute("HyperCtx","hyper","/hyper/pos/login")` re-validates the branch activity on **every** action |
| **BusinessEvents** | The hyper track **raises none of its own** (standing decision at HM-5). One event fires on the sale path — `SalesInvoice.Created` inside the parallel team's `ReceivableService`. `JournalEntry.Reversed` fires inside their wiring of `ReverseAsync`, which every correction path uses |
| **Notifications** | No hyper endpoint calls `NotifyAsync`/`NotifyRoleAsync`. Any notification observed on the sale path is produced by the parallel team's projection consumer |
| **Tests** | No automated (xUnit) test covers any flow below. Coverage is by `[DevOnly]` HTTP acceptance endpoints, named per flow |

---

## Flow 1 — Cashier login

| | |
|---|---|
| **Entry screen** | `Views/Hyper/PosLogin.cshtml` (`Layout = null`, standalone) |
| **Controller** | `POST /hyper/pos/login` → `HyperPosController.Login(username, password)` (82–104) |
| **Service chain** | `UserManager.FindByNameAsync` → `SignInManager.PasswordSignInAsync` → `PosAccessService.ResolveByUserIdAsync` → `IsActivityAllowedForLane(preset,"hyper")` → direct company comparison |
| **Transaction** | none (no write) |
| **Data written** | `Session["HyperCtx"]` only |
| **Accounting / inventory** | none |
| **Authorization** | Four sequential gates: (1) user exists **and** `IsActive`; (2) password; (3) `ResolveByUserIdAsync` returns null unless the employee has ≥1 active `BranchUserRole` at a branch; (4) branch activity must be exactly `Hyper` (**whitelist**, so `Retail`/`NULL`/`Cafe`/`Restaurant` are all refused); (5) `Employee.EmpCompanyID` must equal `Branch.CompanyID` — **cross-company login is refused and the user is signed out** |
| **Failure** | Every failure path calls `SignOutAsync()` and sets a localised `TempData["PosErr"]`; no partial session is left behind |
| **Tests** | `hyper-hm0-seed` sets up the branch/role fixtures; login itself is exercised by every `hm*-accept` cookie-jar run |

**Note.** `ResolveByUserIdAsync` resolves the branch from `Employee.BranchID` — **one branch per employee**. A cashier
who works two stores cannot be represented, and there is no branch picker at login.

---

## Flow 2 — Opening a shift

| | |
|---|---|
| **Entry screen** | `Views/Hyper/PosStart.cshtml` — terminal cards with open-shift state |
| **Controller** | `POST /hyper/pos/start` → `HyperPosController.Start(terminalId, shiftType, openingFloat, resumeShiftId)` (126–141) |
| **Service chain** | terminal lookup scoped to `(ID, BranchId, IsActive)` → `PosSetupService.GetOpenShiftAsync` → `OpenShiftAsync` |
| **Transaction** | single `SaveChanges` (no explicit tx needed — one row) |
| **Data written** | `PosShift` (`ShiftType` normalised to Morning/Evening, `Status="Open"`, `OpenedByEmployeeId`, `OpeningFloat` clamped ≥ 0, `OpenedAt` UTC) + `HyperCtx.TerminalId/ShiftId` |
| **Accounting** | **none.** The opening float is a figure only — no GL movement (deliberate) |
| **Inventory** | none |
| **Authorization** | `HyperCtx` + lane guard + "terminal belongs to this branch and is active". **No role check** — see finding HS-1. Any lane session may open a shift and set its opening float |
| **Concurrency** | `OpenShiftAsync:890` refuses a second open shift on the same terminal |
| **Failure** | `(ok,err)` tuple → `TempData["PosErr"]`, redirect back to Start |
| **Tests** | `hm1-closeshift-test`, and implicitly every accept endpoint that pays |

---

## Flow 3 — Starting a sale

There is **no explicit "new sale" action.** The cart is created lazily by the first scan.

| | |
|---|---|
| **Controller** | `HyperPosController.EnsureOrderAsync` (188–199), called from `Scan` and `AttachCustomer` |
| **Service chain** | `PosOrderService.CreateOrderAsync(company, branch, "Takeaway", null, null, terminalId, shiftId)` |
| **Transaction** | single `SaveChanges` |
| **Data written** | `PosOrder` — `Status="Open"`, `OrderType="Takeaway"`, `BrandId` from the branch, `CurrencyId` from `BranchPosSetting.DefaultCurrencyId`, `CustomerId` = the company's single **Walk-in** customer (`EnsureWalkInAsync`, created on demand), `OpenedAt` UTC. `HyperCtx.OrderId` stamped |
| **Accounting / inventory** | **none — an open order writes nothing to GL or stock.** This is the module's core invariant |
| **Company guard** | `CreateOrderAsync:433–435` **hard-rejects** a branch whose `CompanyID ≠ companyId`. Not swallowed, not corrected |
| **Failure** | error returned to `Scan`, which surfaces it and does not add the line |

**Stale-cart handling** (`Lane:161–166`): if `HyperCtx.OrderId` points at an order that no longer exists or is no
longer `Open`, the reference is cleared from the session. So a paid or voided order cannot be re-edited through a stale
session pointer.

---

## Flow 4 — Finding or scanning an item

| | |
|---|---|
| **Entry screen** | the autofocused `barcode` input in `PosLane.cshtml:57` |
| **Controller** | `POST /hyper/pos/scan` → `HyperPosController.Scan(barcode)` (201–269) |
| **Authorization** | `HyperCtx` + lane guard + **`_access.CanSell`** (pos-cashier or pos-manager) |

**Resolution order — scale first, then fixed. Three branches, no silent fallback:**

1. **Scale range** (`ScaleBarcodeParser.MatchesPrefix` against the branch's `Scale*` config):
   - `Weight` capability must be ON, else explicit reject;
   - a **fixed** product barcode found inside the reserved scale range is an explicit **data-error reject**
     (HM-D42), never mis-sold;
   - `Parse` returns three distinguishable errors — `check` (bad mod-10 digit), `priceType` (price-embedded labels
     **not supported**), `length` — each with its own message;
   - the item is found by `Item.ScaleCode`; must be `IsActive` **and** `IsWeighted`, else specific reject;
   - quantity = parsed weight in **KG**, unit = the company's `KG` UoM.
2. **Fixed barcode**: `Items.Barcode` (base unit) ∪ `ItemBarcodes` (its own unit, gated by the `BarcodeMulti`
   capability). More than one distinct match → **explicit reject** ("registered on more than one item"). Zero matches
   with `BarcodeMulti` OFF but a secondary row existing → the distinct message "multiple barcodes are not enabled on
   this branch" rather than "not found".
3. **Nothing else.** There is **no** name search, **no** PLU/`QuickCode` entry, **no** category browse, **no** quick
   buttons. An item without a scannable barcode cannot be sold in the hyper lane.

| **Data written** | none by resolution itself; the resolved `(itemId, uomId)` is handed to `AddLineAsync` |
| **Tests** | `hm2-seed`, `hm2-edge`, `hm3-seed`, `hm3-guard-test` |

---

## Flow 5 — Adding an item

| | |
|---|---|
| **Service** | `PosOrderService.AddLineAsync(company, orderId, itemId, qty, optionIds, uomId)` (534–642) |
| **Transaction** | two/three `SaveChanges` calls, no explicit tx (an open order has no financial effect, so atomicity is not required here) |
| **Guards, in order** | order exists + belongs to company; order `Status == "Open"`; item exists in company; **unit guard** — a non-base `uomId` must have a `UoMConversion` to base **on this item**, else reject (never a silent factor of 1); modifier min/max enforced server-side; branch price-list membership; zero-or-below net rejected |
| **Pricing** | `PricingService.GetPriceAsync(company, item, null, null, order.CurrencyId, qty, today, branchListId, uomId, promoOn)` — see Flow 8/9/10 |
| **Data written** | `PosOrderLine` (`ItemName` snapshot, `Qty`, `UoMId`, `UnitPrice` **including folded modifier extras**, `DiscountAmount`, `TaxRate`, `LineTotal`, `Sort`) then `RecomputeAsync(order)` rewrites `SubTotal`/`ServiceAmount`/`TaxTotal`/`GrandTotal` |
| **Merge rule** | lines merge only when the item has **no** modifier groups **and** the same `(ItemId, UoMId)` — a carton line and a piece line stay distinct |
| **Rounding** | every amount rounded to the **order's document currency** decimals with explicit `AwayFromZero` |
| **Accounting / inventory** | none |

---

## Flow 6 — Weighted-item handling

Covered in Flow 4 step 1. Additional traced facts:

- The weight is embedded in the barcode by the shop scale and parsed, **never re-derived**: `WeightKg = raw / 10^ValueDecimals`.
- Quantity is the weight in KG and the price is per KG, so `LineTotal = weight × price/kg − discount`.
- `PriceListLine.MinQty` may legitimately be **0** (HM-D43): the earlier `<=0 ? 1` normalisation made every weighed
  item unsellable below one kilo. Now only negatives are clamped.
- **Not implemented (HM-D41):** tare weight, per-item min/max weight plausibility guard, price-embedded labels.

---

## Flow 7 — Unit conversion

Two enforcement points, both fail-closed:

1. **At add-line** — `AddLineAsync:545–549` rejects a unit with no `UoMConversion(ItemId, From→Base)`.
2. **At stock issue** — `StockService.ToBaseAsync` (229–240) converts `Qty` in `UoMId` to base units; the movement
   stores `QtyBase`, `UoMId` and `QtyInUoM` so both views of the quantity are auditable.

The unit is **stored on the cart line and re-read at pay**, deliberately not re-derived from the barcode (which may
change or vanish between add and pay). `SalesInvoiceLine.UoMId` carries it from the invoice to the movement.

---

## Flow 8 — Price resolution

`PricingService.GetPriceAsync` (136–223). Candidate filter and the six-key deterministic ordering are documented in
the main assessment §7.2. Behaviour specific to the hyper lane:

- The branch's `DefaultPriceListId` is passed, which **restricts candidates to that list**.
- If a list is pinned and the resolved source is neither `list` nor `costplus`, `AddLineAsync:596` **rejects the
  sale**: *"This item is not in the branch price list — it cannot be sold until it is priced."* It never silently
  converts `Item.SalesPrice` from EGP to KWD, and never prices at zero. This is the HM-D18 lesson.
- `CostPlus` resolves cost as: manufactured (has a BOM) → `ManufService.ComputeStandardUnitCostAsync`; else
  moving-average `ΣTotalValue / ΣQtyOnHand`; else `Item.OpeningCost`.

---

## Flow 9 — Discount resolution

| | |
|---|---|
| **What exists** | `PosOrderLine.DiscountAmount`, written **only by the system** from `PriceResult.DiscountPercent` (list discount folded with the winning promotion) |
| **Ceiling** | `PricingService.EvaluateLineDiscountsAsync` compares the max line discount % against `InventorySettings.MaxLineDiscountPct` in `Off`/`Warn`/`Block` mode, with a `canApprove` override — **no hyper caller** |
| **Margin floor** | `CheckMarginAsync`: `Off`/`Warn`/`Block`, per-item `MinMarginPct` override, comparison in functional currency, **skipped when cost is 0** so it never false-blocks — **no hyper caller** |
| **Safety net that IS wired** | `AddLineAsync:606` rejects a priced line driven to ≤ 0 by discounts |
| **What does not exist** | any cashier-entered discount in the hyper lane, any order-header/basket discount field, any discount-approval prompt |

---

## Flow 10 — Promotion resolution

`ApplyBestPromotionAsync` (227–291), applied on top of the resolved price, **sequentially**, **single-winner**.

- Gated per branch: `AddLineAsync:594` reads `BranchCapabilities` for `Promotions` directly and passes
  `applyPromotions` into the pricing call. Management is central; activation is per branch. A branch with the
  capability OFF prices with no promotion at all.
- Ranking: specificity (item > category > general) → `Priority` → largest amount → smallest `ID`. `Priority` ranks
  **above** the amount by design.
- `Amount` promotions are converted to the document currency and capped at the net; `Percent` promotions are
  currency-agnostic and clamped 0–100.
- Result folded into one effective `DiscountPercent`, so the till, invoice and GL all see a single **net-revenue**
  figure. There is deliberately **no "discount allowed" account**.
| **Tests** | `hm5-seed` + `hm5-accept` — 11 cases including determinism over 10 identical runs, item-beats-category, priority-beats-amount, qty break, weighted qty break, save guards, 100% rejection, capability off/on |

---

## Flow 11 — Customer selection

| | |
|---|---|
| **Entry screen** | `Views/Hyper/Customer.cshtml` |
| **Controller** | `GET /hyper/pos/customer`, `GET /hyper/pos/customer/search`, `POST /hyper/pos/customer/add`, `POST /hyper/pos/customer/attach` |
| **Service chain** | `ReceivableService.SearchCustomersAsync` / `CreateCustomerAsync` (reused — no new service) → `PosOrderService.SetOrderCustomerAsync` |
| **Capability** | `CustomerIdentity` — ON by default in the Hyper preset |
| **Fail-closed guard** | `CustomerControlAccountValidAsync` (405–411): the customer's `ControlAccountId` must exist, be > 0, and be an **active account in this company**. A missing/zero/inactive control account **refuses the link**. It deliberately does **not** hard-code `1102`, and deliberately does **not** require `IsPostable` (the AR control account is a non-postable control account by design) |
| **Timing** | pre-pay only — `SetOrderCustomerAsync` itself refuses a non-`Open` order |
| **Data written** | `PosOrder.CustomerId`; a new `Customer` when quick-added (phone persisted separately as the loyalty lookup key) |
| **Walk-in filtering** | the search results and the "current customer" display both exclude `NameEn == "POS Walk-in"` so the reusable cash customer is never presented as an identified customer |
| **Accounting** | none now. At pay, the receivable posts to **that customer's** control account |
| **Tests** | `hm9-s1-accept` — 11 cases |

---

## Flow 12 — Loyalty application

**Earn only. Redemption is deferred (HM-9 slice 3) and the capability ships OFF.**

| | |
|---|---|
| **Service** | `LoyaltyPointsHelper.AccrueForInvoiceAsync`, called from `PayAsync:1188` and `PayTendersAsync:1300` — **inside the settlement transaction, before commit** |
| **Guards** | `customerId > 0` · branch `Loyalty` capability ON · customer is **not** the walk-in · no `Earn` movement already exists for this invoice (earn-once) · resolved rate > 0 |
| **Rate** | branch `LoyaltyPointsPerCurrencyUnit` → else company default → else 0 (no earning). Data, not code |
| **Eligible net** | Σ of eligible invoice-line `LineTotal`; eligibility = `Item.LoyaltyEligible ?? ItemCategory.LoyaltyEligible`. A line with no item earns nothing |
| **Points** | `(long)Math.Floor(net × rate)` — an explicit floor on a **count**, deliberately outside `ICurrencyRounding` |
| **Data written** | one `PointsMovement` (`Kind="Earn"`, `SourceInvoiceId`) |
| **Accounting / inventory** | **none — points are a memo ledger.** No GL account, no liability (that arrives with redemption) |
| **Balance** | derived: `Σ signed Points`. There is **no stored balance column**, deliberately, to avoid the lost update recorded in DEV-2026-005 |
| **Reversal** | `ReverseForSaleUndoAsync` (proportional to returned eligible net, capped at un-reversed remainder) and `ReverseAllForInvoiceAsync` (paid-order cancel). Both fire **independently of the current capability state**, so turning the capability off never strands a balance |
| **Tests** | `hm9-s2-accept` — 14 cases |

---

## Flow 13 — Payment

| | |
|---|---|
| **Entry screen** | the "Pay cash" button in `PosLane.cshtml:112–116`, carrying a `payToken` hidden field |
| **Controller** | `POST /hyper/pos/pay` → `HyperPosController.Pay(payToken)` (294–307) |
| **Client token** | `crypto.randomUUID()` stored in `sessionStorage` keyed by order id, so it survives F5 — **but is lost if the tab closes**, which is the common opaque-failure symptom. The documented comprehensive protection is the order-status guard, not the token |
| **Pre-conditions** | `HyperCtx.TerminalId` present · **an open shift exists on this terminal** (re-read from the DB, not from the session) · `CanSell` · a cart exists |
| **Method** | **hard-coded `"Cash"`.** `PayAsync:1097` additionally rejects any non-Cash method. Card/KNet/Mada are unreachable from the hyper lane despite `PayTendersAsync` existing |
| **No tender/change** | the lane sends no tendered amount and computes no change due |

**Settlement, inside one `ScopedTx.BeginOrJoinAsync` (`PayAsync:1148`):**

| Step | Effect |
|---|---|
| Idempotency fast-path | pre-tx read of `HyperPayTokens` — a prior token returns the **first** invoice (labelled a fast path, not the guard) |
| `RecomputeAsync` + save | totals re-derived server-side before posting |
| Account resolution | revenue `4101` (else lowest postable `4*`); cash = **terminal drawer → branch Cash method → `110101`**; sales warehouse = `BranchPosSetting.DefaultSalesWarehouseId` (**mandatory** — pay fails without it) |
| Line build | `AppendSaleLines` routes by `BranchItemSourcing.Method`: `RecipeAtSale` → parent line is **revenue only** (`ItemId=null`, no stock) + recipe components as 0-price stock lines; every other method deducts the finished item itself. Modifier options append as 0-price stock lines |
| `CreateSalesInvoiceAsync` | `SalesInvoice` + lines; **JE**: Dr AR / Cr revenue (grouped by account) / Cr output VAT; then per stockable line `StockService.PostMovementAsync(Direction=-1, PostToGl=true)` → inventory credit + COGS debit; then the parallel kernel's `RecordAsync(SalesInvoice.Created)` **in-transaction, no swallow** |
| `CreateReceiptAsync` | Dr cash / Cr AR at the document currency, so AR nets to zero. `PosPayment` row links the receipt for clean reversal |
| `PostTipAsync` | no-op for hyper (tip is always 0) |
| `StampCurrentShiftAsync` | re-stamps the order with the terminal's **currently open** shift, so Z-report aggregation reflects when cash entered the drawer |
| `AllocateReceiptNoAsync` | **last step before commit** — one `UPDATE … OUTPUT deleted.NextReceiptNo` takes a row lock and returns the pre-increment value. Gaps on rollback accepted; duplicates impossible |
| Token insert | `HyperPayTokens` row **inside** the transaction — the authoritative race guard |
| `AccrueForInvoiceAsync` | loyalty earn |
| `CommitAsync` | |

**Race behaviour.** A concurrent pay with the same token loses the unique INSERT; the loser's whole transaction rolls
back (invoice + JE + stock discarded) and it returns the **winner's** invoice. The `catch` matches SQL 2601/2627
**and** the index name `UX_HyperPayTokens_Token`; anything else is rethrown. A *different* token on the same cart is
caught by the `Status != "Open"` guard instead.

| **Failure / rollback** | any inner failure returns before `CommitAsync`; the ambient transaction disposes and rolls back everything |
| **Tests** | `hm10-accept` — 11 cases including a genuine duplicate-index rejection, lost-token, raw re-post, pre-commit disconnect, and an over-long token proving a non-index failure is **thrown, not swallowed** |

---

## Flow 14 — Split payment

**Engine implemented; not reachable from the hyper lane.**

- `PayTendersAsync` (1214–1303): one invoice, N receipts, one per tender, each to that method's configured GL account.
  Tender sum validated at the document-currency unit; the **largest** tender absorbs the rounding remainder so Σ
  receipts equals the grand total exactly.
- `PaySplitByItemAsync` (1308+): N invoices, whole-unit allocation, with a strict guard that every unit of every line
  is covered exactly once.
- Equal split: `PayAsync(splitParts: N)` — one invoice, N cash receipts, remainder on the largest (first on tie).

All three are exposed only by `PosAppController` (`/pos/order/pay-tenders`, `/pos/order/pay-split-items`).
`HyperPosController` has no equivalent route.

---

## Flow 15 — Invoice creation · Flow 16 — Accounting posting · Flow 17 — Stock deduction

All three happen inside Flow 13 and are described there. Three properties worth isolating:

- **Ordering to avoid deadlocks.** The stock issue loop orders by `(ItemId, WarehouseId)` so concurrent baskets touch
  rows in the same order.
- **Item-type filter.** Only `ItemTypes.RequiresStock` items produce a movement; Service/NonStockable/Asset lines are
  skipped and surfaced by the `cogs_impact` integrity classification rather than silently ignored.
- **Every stock result is checked.** A failed issue fails the whole sale (`return (false, serr …)`), which is what makes
  the negative-stock guard a genuine oversell block rather than a warning.

---

## Flow 18 — Printing

| Output | Hyper lane |
|---|---|
| **Thermal receipt** | **Not present.** `PosTerminal.ReceiptPrinterName`, `ReceiptPaperWidthMm`, `ReceiptCopies` are configured and unread by any hyper code path |
| **A4 official invoice** | `GET /hyper/pos/invoice/print/{invoiceId}` → gated by the `OfficialInvoice` capability **and** `BranchOwnsInvoiceAsync` **and** a company predicate → renders the **shared** `Views/Accounting/SalesInvoicePrint.cshtml` via `OfficialInvoiceHelper.LoadPrintDataAsync`. Browser `window.print()` |
| **Beneficiary stamp** | `POST /hyper/pos/invoice/stamp` — `CanSell` + capability + branch ownership; `StampCustomer` is **set-once**, **audited** (`By`/`At`), and **allowed only when the invoice's tax is 0** (a taxed invoice's beneficiary must equal the ledger account holder). Financials untouched |
| **Reprint** | from `GET /hyper/pos/receipts`, which links the same print action |
| **Blocking issue** | the `OfficialInvoice` capability is written **only** by `DevSeedController` (`hm8-accept:1397`). There is no production path to enable it — finding HS-2 |
| **Tests** | `hm8-accept` — 15 cases |

---

## Flows 19 & 20 — Suspending and resuming a sale

**Engine implemented; capability declared and default-ON; no hyper endpoint.**

- `PosOrderService.HoldOrderAsync` (826–836): sets `IsHeld`/`HeldAt`; the order stays `Open`, so **no accounting**.
  Refuses an empty order and refuses a table order.
- `GetHeldOrdersAsync`, `RecallHeldAsync` exist and are used by `/pos/order/hold`, `/pos/orders/held`,
  `/pos/order/recall` — restaurant routes.
- `hyper_presets.sql` sets `SuspendResume = 1` by default, and `PosLane.cshtml` renders it as a **green "enabled"
  badge**, while `grep '"SuspendResume"' --include=*.cs` finds exactly one hit: a seed array. **Nothing reads it.**

This is the clearest single instance of the F5 pattern: a capability the operator is told is on, backed by no code.

---

## Flows 21 & 22 — Return and refund

**Engine implemented and accounting-correct; not reachable from the hyper lane.**

`PosOrderService.ReturnOrderLinesAsync` (1492–1579):

| Step | Effect |
|---|---|
| Guards | order must be `Paid`; per-line returned qty ≤ sold qty; branch sales warehouse present; revenue account present |
| Line build | **mirrors the sale** via `AppendSaleLines`, so a `RecipeAtSale` item returns its **components** (never the never-stocked finished good) and a deduct-itself item returns the finished good — the return exactly reverses whatever the sale issued, at cost |
| `CreateSalesReturnAsync` | returns stock `+1` at original cost, reverses revenue + VAT, opens an AR credit |
| Cash refund | Dr AR-control at the **invoice** rate (`GrandTotalBase`) / Cr drawer at the **return-day** rate, with the difference posted as **realized FX** to `5902` (loss) or `4902` (gain). A matching `Receipt` row with a **negative** amount is written so the AR subledger check stays balanced |
| Loyalty | `ReverseForSaleUndoAsync` — proportional negative movement |

Exposed only at `/pos/order/return-lines`. **A hyper cashier has no route to it.** The only path today is the
accounting back-office.

---

## Flow 23 — Void / cancellation

**Two distinct operations. One is reachable from hyper, one is not.**

| Operation | Hyper reachable? | Behaviour |
|---|---|---|
| Void an **unpaid** open order (`VoidOrderAsync`) | **No route** | sets `Status="Void"`, no GL. `ParkOrphanWalkInsAsync` does this automatically on restaurant terminal load; hyper has no equivalent housekeeping |
| Void a **paid** order (`VoidPaidOrderAsync`, 1424–1487) | **No route** | inside one `ScopedTx`: for each invoice of the order, re-post every `-1` movement as `+1` at the **original unit cost** (`SourceType="SalesInvoiceReversal"`), then `ReverseAsync` the invoice JE; reverse every linked receipt's JE via `PosPayment.ReceiptId`; reverse the tip JE; `ReverseAllForInvoiceAsync` for loyalty; set `Status="Voided"` |

**Important coupling:** `VoidPaidOrderAsync` calls `JournalEntryService.ReverseAsync`, which the parallel team wired to
`RecordAsync(JournalEntry.Reversed)` **in-transaction with no swallow** (HM-D53). So **every** hyper correction depends
on the `BusinessEvents` table existing with the current schema. Normal posting is *not* coupled — only reversal.

---

## Flow 24 — Closing a shift

| | |
|---|---|
| **Entry screen** | the counted-cash form in `PosLane.cshtml:149–157` |
| **Controller** | `POST /hyper/pos/shift/close` → `ShiftClose(closingFloat)` (175–184) |
| **Service** | `PosSetupService.CloseShiftAsync(company, terminal, shift, closingFloat, employee, date, user)` (951–1001) |
| **Authorization** | `CanSell` (unlike Flow 2, this one **does** check the role) |
| **Company guard** | hard-rejects a terminal whose branch belongs to another company |
| **Transaction** | one `ScopedTx` wrapping **both** the variance JE and the close-field write — so a "JE posted but shift still Open" state is impossible (which would otherwise allow a second variance JE) |
| **Computation** | `variance = closingFloat − ExpectedCash`, in **document** currency; the JE amount is `|variance| × rate` converted **once** to functional and used on **both** lines, so it balances by construction |
| **Accounting** | variance > 0 (overage): Dr drawer / Cr `520111`. variance < 0 (shortage): Dr `520111` / Cr drawer. **variance == 0 → no JE at all** |
| **Data written** | `PosShift.ClosingFloat`, `ExpectedCash`, `CashVariance`, `ClosedByEmployeeId`, `Status="Closed"`, `ClosedAt`, `VarianceJournalEntryId` |
| **Failure** | missing drawer or `520111` → explicit rollback with a message naming the account |
| **Tests** | `hm1-closeshift-test`, `hm1-close-batch-test` |

---

## Flow 25 — Cash reconciliation

`ExpectedCashAsync` (936–946): `OpeningFloat` + Σ cash `PosPayment` amounts of **non-voided** orders in this shift +
Σ cash tips, rounded to the document currency. Card/KNet are excluded because they settle to a bank, not the drawer.

**Known incompleteness, and the sequencing constraint it creates:**

- The method does **not** subtract refunds; the comment says "refunds land in RC-6c".
- `ZReportDto.ReturnsTotal` is hard-coded `0m` with the comment "RC-6c will populate refunds/returns for the shift".

Because the hyper lane cannot refund at all today, neither is currently wrong. **Both become real reconciliation
defects the moment a hyper refund endpoint ships** — so refund UI and `ExpectedCash`/`ReturnsTotal` completion must be
the **same** slice. This is recorded as a hard dependency in the gap analysis.

The Z report itself (`GetShiftZReportAsync`, 897–932) is read-only, built from paid orders and `PosPayment` grouped by
method, presented at the shift's document-currency precision. It is rendered as four tiles on the lane
(orders, grand total, expected cash, variance) — the full payment breakdown it computes is **not displayed**.

---

## Flow 26 — Production / preparation

**Engine implemented; driven from the restaurant sidebar; no hyper screen.**

`BranchItemSourcing.Method` selects one of four behaviours per (branch, item):

| Method | Behaviour | Where the stock/GL happens |
|---|---|---|
| `WorkOrder` | manufacture from raw at this branch | `ManufService` (manual, back-office) |
| `FinishedFromBranch` | `ReplenishFinishedFromBranchAsync` → `StockService.TransferAsync` from the source branch's warehouse (goods-in-transit `110302` nets to zero) | transfer; the sale then deducts the finished good |
| `SemiFromBranchComplete` | `PrepareSemiFinishedAsync` → transfer the exact BOM-derived semi quantity (`Quantity × qty × (1+ScrapPct)`), then `ManufService.CreateAsync` + `CompleteAsync` so cost rolls up (transferred semi + local components + labour/overhead) | transfer + work order |
| `RecipeAtSale` | at pay, the parent line posts **revenue only** and the recipe components are issued as 0-price lines | inside the sale transaction |

Only `RecipeAtSale` runs automatically. The other three are operator actions exposed at `/Pos/ItemSourcing` and
`/Pos/SourcingOverview` — i.e. **inside the Restaurant system**, which a hypermarket manager should not have to enter.

---

## Flow 27 — Branch transfer

`StockService.TransferAsync`: paired `-1`/`+1` movements with `PostToGl=false` (goods-in-transit nets to zero),
FEFO-split per line for expiry-tracked items, batch and bin carried through, and a `StockTransfer` document with
per-line out/in movement ids. Reachable from `/Inventory/StockTransfers` and from the two sourcing methods above.
**No hyper screen.**

---

## Flow 28 — Online order / delivery

**Not present for hypermarket.**

- The delivery model on `PosOrder` (zone, address, area, phone, frozen fee, driver, `DeliveryStatus`) plus
  `DeliveryZone` and `Driver` is complete **and exclusively restaurant-driven** — every setter
  (`SetOrderDeliveryAsync`, `AssignDriverAsync`, `SetDeliveryStatusAsync`) is exposed only on `/pos/*`.
- The delivery fee reaches the GL as its own invoice line mapped to `BranchPosSetting.DeliveryRevenueAccountId`
  (fallback: sales revenue), taxed unless the branch marks delivery exempt.
- The storefront (`/Store/*`) is **display only** — no cart, no order, no stock, no GL.
- **Click-and-collect does not exist**, and its missing primitive is the same one that blocked offline selling: a
  server-side stock **reservation**. Nothing in the model reserves stock ahead of settlement.

---

## Appendix — Offline behaviour, stated precisely

Three separate things are often conflated. From source:

| Mechanism | Lane | Status |
|---|---|---|
| Service worker + PWA manifest + local queue + `SyncPaidOrderAsync` replay + `PosSyncLog` idempotency + `PosSyncConflict` anomaly recording | **restaurant** | Implemented and internally rigorous (one transaction wraps rebuild + pay + the idempotency key). **Per `AUDIT-DEVIATIONS.md:1130` it has never run on a real path** — all evidence is synthetic `ZZ` fixtures. It also trusts device-supplied 2-decimal prices (HM-D24) |
| Pay idempotency + receipt recovery + a `navigator.onLine` message banner | **hyper** | Implemented (HM-10 slice A). This is *graceful degradation*, explicitly **not** offline selling |
| Offline **selling** for hyper | **hyper** | **Deferred for an architectural reason**, and the reason is worth preserving: FEFO and the oversell guard both need a live SQL row lock (`UPDLOCK, HOLDLOCK`) that a disconnected terminal cannot hold. Two offline lanes selling the last units of a tracked batch produce a physical oversell and a COGS error that a memo reversal cannot fix |
