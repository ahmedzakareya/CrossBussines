# Stage 2 — Hypermarket Gap Analysis and Improvement Proposal

**Analysis only. Nothing here is added to implementation scope by this document.** Fulfils H15. Companion to
`Stage-002-Hypermarket-Current-State-Assessment.md`.

**Priority bands** are the four mandated ones. Their meaning here:

- **Required Now** — a verified defect, a feature the operator is *told* they have and do not, or a control gap. Ten items.
- **Recommended Now** — high value, low risk, and cheap because the engine already exists. Eight items.
- **Planned Later** — real retail capability that needs a new model, a decision, or hardware. Eleven items.
- **Rejected** — considered and declined, with the reason. Six items.

Every item carries the sixteen mandated attributes. Where an attribute is genuinely "none", it says none.

---

## Part 1 — REQUIRED NOW

### RN-1 — Four declared hyper capabilities have no consumer, and two ship default-ON

| | |
|---|---|
| **Problem** | `SuspendResume`, `CashDrawer`, `ExpiryControl` and `ShelfLabels` appear in `HyperController.Capabilities`, are seeded by `hyper_presets.sql`, and are rendered as capability badges on both the lane and the diagnostic panel. `SuspendResume` and `CashDrawer` are seeded **ON**. Nothing in the application reads any of the four |
| **Evidence** | `grep '"SuspendResume"' --include=*.cs` → 1 hit (a seed array). Same for `CashDrawer`, `ExpiryControl`, `ShelfLabels`. `hyper_presets.sql` sets `SuspendResume=1`, `CashDrawer=1`. `PosLane.cshtml:183–190` renders `cap.On` as a green check badge |
| **Proposed solution** | Two separable decisions per key: **implement** (`SuspendResume` → expose `HoldOrderAsync`/`RecallHeldAsync`; `ShelfLabels` → gate the existing label screen), or **withdraw** (`CashDrawer` and `ExpiryControl` have no feature behind them at all — remove from the catalogue and the preset, or mark them explicitly as "reserved" in the UI). Do not leave a green badge over nothing |
| **Business value** | Removes the module's most direct source of operator distrust: a store manager who enables "Suspend/resume" and finds no button stops believing the rest of the panel |
| **Technical value** | Restores the capability catalogue as a truthful contract, which is what the whole `IsCapabilityEnabledAsync` gate depends on |
| **Affected files/flows** | `HyperController.Capabilities`, `deploy/sql/hyper_presets.sql`, `Views/Hyper/PosLane.cshtml`, `Views/Hyper/Dashboard.cshtml`; Flows 19–20 |
| **Dependencies** | none |
| **Security impact** | none — these are feature gates, not authorization |
| **Accounting impact** | none |
| **Inventory impact** | none |
| **UX/UI impact** | High and immediate |
| **Test approach** | A structural test asserting **every** key in `HyperController.Capabilities` has at least one `IsCapabilityEnabledAsync` call site, and every key in the preset SQL exists in the catalogue. This is the same shape as `Correction005StructuralTests` and would have caught this at introduction |
| **Migration risk** | None for implement. For withdraw: existing `BranchCapabilities` rows become orphans — harmless (the gate returns false for an unknown key) but they should be left, not deleted, since deletion is not reversible and reveals nothing |
| **Target stage** | Hypermarket pack, first slice |

### RN-2 — `OfficialInvoice` capability has no production path to enable

| | |
|---|---|
| **Problem** | The A4 invoice print and beneficiary stamp are gated by an `OfficialInvoice` capability that is written **only** by `DevSeedController.hm8-accept`. It is absent from `HyperController.Capabilities`, from `PosController.Capabilities` and from `hyper_presets.sql`. On a production database the feature cannot be turned on, and the "Reprint" link in receipt recovery therefore never works |
| **Evidence** | `grep -rn "OfficialInvoice" --include=*.cs --include=*.sql` → the only writes are `DevSeedController:1397–1399`. `HyperPosController:336` and `:374` read it |
| **Proposed solution** | Add the key to the hyper capability catalogue, to the Hyper preset (default OFF — issuing a tax document is a deliberate choice), and to the hyper branch-setup screen (RN-4) |
| **Business value** | A hypermarket that cannot issue an official invoice on request cannot trade with business customers |
| **Technical value** | Closes the catalogue/consumer asymmetry from the other direction to RN-1 |
| **Affected files/flows** | `HyperController.cs`, `hyper_presets.sql`, hyper setup screen; Flow 18 |
| **Dependencies** | RN-4 for the editable surface; deliverable without it via SQL |
| **Security impact** | Positive — the feature currently has no governed on/off switch, only a manual row |
| **Accounting impact** | None. The stamp is display-only, set-once, audited, and refused on a taxed invoice. **Do not relax any of those three rules** |
| **Inventory impact** | none |
| **UX/UI impact** | Medium |
| **Test approach** | the RN-1 structural test covers it; plus a case asserting the stamp is still refused when `TaxTotal != 0` |
| **Migration risk** | none |
| **Target stage** | Hypermarket pack, first slice |

### RN-3 — Weighted items, scale format, branch tax and loyalty rate are configurable only by SQL

| | |
|---|---|
| **Problem** | Ten of the fourteen `BranchPosSetting` columns have **no writer in the application**: `DefaultTaxCodeId`, the six `Scale*` columns, the three delivery columns, and `LoyaltyPointsPerCurrencyUnit`. On the item side, `Item.IsWeighted`, `Item.ScaleCode` and `Item.LoyaltyEligible` have no field in `ItemForm.cshtml`. So the entire weighted-retail feature — the deepest piece of hypermarket-specific engineering in the module — cannot be configured by a user |
| **Evidence** | `PosSetupService.SavePosSettingAsync:257–265` writes 4 columns. `grep -rn "ScaleBarcodePrefix\|LoyaltyPointsPerCurrencyUnit\|DefaultTaxCodeId" --include=*.cshtml` → no matches outside reports. `grep "Weighted\|ScaleCode\|LoyaltyEligible" Views/` → no item-form matches |
| **Proposed solution** | A hyper branch-setup screen (RN-4) for the branch columns, and a **Retail tab** on the item form for `IsWeighted`/`ScaleCode`/`LoyaltyEligible` beside the existing per-unit barcode grid |
| **Business value** | Weighted selling is not optional in a grocery hypermarket — meat, cheese, produce and deli are all weighed. Today none of it can be set up without a DBA |
| **Technical value** | `ItemService` already validates all three item fields (ScaleCode required when weighted, unique per company, scale-range guard on new fixed barcodes). The validation is complete and unreachable |
| **Affected files/flows** | `Views/Inventory/ItemForm.cshtml`, `ItemService`, `PosSetupService.SavePosSettingAsync`, new hyper setup screen; Flows 4, 6, 12 |
| **Dependencies** | Master Data (2C) will touch `ItemForm` anyway — **do the retail tab there, not twice** |
| **Security impact** | New mutating endpoints need real role checks; the branch-setup screen must use the existing `PosGate` pattern (validated company + role at that branch), not `[SessionValidation]` alone |
| **Accounting impact** | `DefaultTaxCodeId` changes the tax rate resolution for new sales at that branch. It must be an audited, deliberate change — not a silent dropdown |
| **Inventory impact** | none directly; a wrong `ScaleCode` mapping would sell the wrong item, so uniqueness enforcement must stay |
| **UX/UI impact** | High |
| **Test approach** | acceptance: create a weighted item entirely through the UI, print its shelf label, scan a built scale barcode, sell it, verify the base quantity posted to stock. Today that path can only be seeded |
| **Migration risk** | none — additive UI over existing columns |
| **Target stage** | item fields → **Master Data (2C)**; branch fields → Hypermarket pack |

### RN-4 — No hypermarket back-office setup screen exists

| | |
|---|---|
| **Problem** | The only screen that assigns `Branch.ActivityPresetCode` and edits capabilities is `Views/Pos/Setup.cshtml`, which renders the **Restaurant** sidebar and binds to the **7 restaurant capability keys**. A hypermarket manager must therefore enter the Restaurant system to configure their store, and cannot edit any of the 10 hyper keys from there |
| **Evidence** | `PosController.SaveCapabilities:207–213` builds its dictionary from `PosController.Capabilities` (7 restaurant keys). `PosController.OnActionExecuting` sets `MainMenu.Restaurant()`. `MainMenu.Hyper()` has no setup group. `ActivityPresetCode` is written nowhere else |
| **Not a data-loss defect** | `SaveCapabilitiesAsync` **upserts** per key rather than replacing the set, so saving from the restaurant screen leaves hyper rows intact. It is a *cannot-edit* defect |
| **Proposed solution** | A hyper setup screen on `MainMenu.Hyper()` covering: activity preset (with the existing risky-change guard preserved), the 10 hyper capabilities, sales warehouse, price list, currency, tax code, the scale-barcode format, and the loyalty rate. Reuse `PosSetupService` — extend `SavePosSettingAsync` rather than adding a second writer |
| **Business value** | A hypermarket becomes independently operable |
| **Technical value** | Removes incorrect coupling #1 of the three identified in the assessment §13.1 |
| **Affected files/flows** | new controller actions on `HyperController`, new view, `MainMenu.Hyper()`, `PosSetupService`; Flows 2, 4, 6, 12 |
| **Dependencies** | absorbs RN-2 and half of RN-3 |
| **Security impact** | must use `PosGate`-equivalent real role authorization. Capability editing is a privilege-adjacent action (it turns selling behaviours on) |
| **Accounting impact** | via `DefaultTaxCodeId` and the price list — both must be audited |
| **Inventory impact** | via the sales warehouse — changing it mid-operation moves where stock is deducted from. Should be blocked while an open shift exists, mirroring the terminal-delete guard |
| **UX/UI impact** | High |
| **Test approach** | per-endpoint authorization tests including a cross-branch attempt and a cross-company attempt; an acceptance run that configures a hyper branch end-to-end through HTTP only |
| **Migration risk** | none |
| **Target stage** | Hypermarket pack, first slice |

### RN-5 — Opening a shift has no role check

| | |
|---|---|
| **Problem** | `POST /hyper/pos/start` opens a shift and sets its opening float with only `HyperCtx` + the lane guard. No `CanSell`, no `IsManager`. The restaurant twin has the same shape |
| **Evidence** | `HyperPosController.Start:126–141` — no `_access` call. Compare `ShiftClose:179`, which does check `CanSell` |
| **Proposed solution** | Require at minimum `CanSell`; consider `IsManager` for the opening float specifically, since it is the figure the shift reconciles against. Apply to both lanes in one change |
| **Business value** | The opening float is a cash-control number; whoever sets it should be authorised to |
| **Technical value** | Closes an inconsistency where close is gated and open is not |
| **Affected files/flows** | `HyperPosController.Start`, `PosAppController.Start`; Flow 2 |
| **Dependencies** | none |
| **Security impact** | This is the item's whole purpose. Classified **High** in the assessment's Stage 2 delta as HS-1 |
| **Accounting impact** | Indirect: a wrong opening float produces a wrong variance JE at close |
| **Inventory impact** | none |
| **UX/UI impact** | none visible |
| **Test approach** | a per-endpoint test asserting a `pos-waiter`-only session is refused |
| **Migration risk** | A real deployment where a non-cashier role currently opens shifts would break. Audit `BranchUserRoles` before shipping |
| **Target stage** | Hypermarket pack, first slice (shared change — coordinate with the restaurant owner) |

### RN-6 — No return, refund or void from the hyper lane

| | |
|---|---|
| **Problem** | `ReturnOrderLinesAsync`, `VoidPaidOrderAsync` and `VoidOrderAsync` are implemented, accounting-correct, FX-aware and loyalty-aware — and have **no `/hyper/pos/*` route**. A hyper cashier who rings the wrong item cannot correct it at the till |
| **Evidence** | the three methods exist in `PosOrderService` (1424, 1492, 775). `HyperPosController` has 18 endpoints and none of them is a correction |
| **Proposed solution** | A correction panel on the redesigned lane, exposing partial return with refund, and paid-order void, both manager-gated (`IsManager`) |
| **Business value** | Highest operational value of any item in this document. A checkout without correction is not a checkout |
| **Technical value** | Pure exposure — the engine, its transaction boundary and its acceptance coverage already exist |
| **Affected files/flows** | `HyperPosController`, lane redesign; Flows 21, 22, 23 |
| **Dependencies** | **HARD: must ship in the same slice as RN-7.** See RN-7 |
| **Security impact** | New mutating endpoints; a return moves cash out of a drawer, so `IsManager` rather than `CanSell` is the defensible default. Must carry the same `BranchOwnsInvoiceAsync`-style ownership proof used by the invoice print |
| **Accounting impact** | None to the calculations — do not change them. The refund's realized-FX split (`4902`/`5902`) and the negative `Receipt` row that keeps the AR subledger balanced are both correct and subtle; preserve verbatim |
| **Inventory impact** | Returns stock at **original cost**, mirroring the sale's sourcing method so a `RecipeAtSale` item returns components, not a never-stocked finished good. Preserve verbatim |
| **Reversal dependency** | Void calls `JournalEntryService.ReverseAsync`, which the parallel kernel wired to `RecordAsync(JournalEntry.Reversed)` in-transaction with no swallow (HM-D53). **Any environment taking hyper corrections must have the platform slices applied first** |
| **Test approach** | acceptance: partial return of a weighted line; full return restoring the loyalty balance to zero; void of a split-tender order; a return on an invoice belonging to another branch must be refused |
| **Migration risk** | none |
| **Target stage** | Hypermarket pack — the checkout-completion slice |

### RN-7 — `ExpectedCash` ignores refunds and `ReturnsTotal` is hard-coded zero

| | |
|---|---|
| **Problem** | `ExpectedCashAsync` sums cash payments and cash tips of non-voided orders and **never subtracts refunds**; `ZReportDto.ReturnsTotal` is literally `0m`. Both carry a comment deferring the work ("refunds land in RC-6c") |
| **Evidence** | `PosSetupService.ExpectedCashAsync:936–946`; `GetShiftZReportAsync:924` |
| **Why it is Required Now despite being currently unreachable** | The hyper lane cannot refund today, so neither is wrong **yet**. The instant RN-6 ships, every refunded shift over-states expected cash by the refund amount and posts a false shortage variance to `520111`. That is a real GL error created by a UI addition |
| **Proposed solution** | Subtract cash refunds (the negative `Receipt` rows already written by `ReturnOrderLinesAsync`, joinable to the shift through the order) from `ExpectedCash`, and populate `ReturnsTotal` from the same source. **Ship in the same slice as RN-6, not after it** |
| **Business value** | Correct drawer reconciliation |
| **Technical value** | Removes the module's one known latent accounting defect |
| **Affected files/flows** | `PosSetupService.ExpectedCashAsync`, `GetShiftZReportAsync`; Flows 24, 25 |
| **Dependencies** | RN-6 (they are one slice) |
| **Security impact** | none |
| **Accounting impact** | **Direct.** It changes the variance amount posted at shift close. Requires a fail-first proof: a refunded shift must show the correct expected cash *before* the fix is trusted |
| **Inventory impact** | none |
| **UX/UI impact** | The Z tiles become correct |
| **Test approach** | Fail-first: construct a shift with a sale and a cash refund; assert the pre-fix `ExpectedCash` is wrong by exactly the refund; apply; assert correct; assert a zero-variance shift still posts **no** JE |
| **Migration risk** | Shifts already closed keep their stored `ExpectedCash`/`CashVariance` — historic values must **not** be recomputed (reverse-never-delete applies to the variance JEs too) |
| **Target stage** | Hypermarket pack — same slice as RN-6 |

### RN-8 — No item lookup other than a barcode scan

| | |
|---|---|
| **Problem** | `Scan` resolves `Items.Barcode` and `ItemBarcodes` only. There is no name search, no PLU/`QuickCode` entry, no category browse and no quick-button grid. An item with a damaged, missing or unregistered barcode cannot be sold |
| **Evidence** | `HyperPosController.Scan:201–269` — three resolution branches, all barcode-keyed. `Item.QuickCode` exists and is read only by the restaurant quick menu |
| **Proposed solution** | Add PLU entry over the existing `Item.QuickCode` (unique per company, already modelled) and a name/code search restricted to the branch's price list — so a searched item is guaranteed sellable rather than rejected at add-line |
| **Business value** | Removes a hard stop at the till. This is the second-most-cited cashier complaint in any grocery rollout after speed |
| **Technical value** | `QuickCode` is already modelled and indexed; the search is a read |
| **Affected files/flows** | `HyperPosController.Scan` (or a sibling `lookup` action), lane redesign; Flow 4 |
| **Dependencies** | lane redesign (RN-9) for the surface |
| **Security impact** | a read scoped to company + active items; no new authority |
| **Accounting impact** | none |
| **Inventory impact** | none |
| **UX/UI impact** | High |
| **Test approach** | acceptance: sell by PLU; search returns only price-listed items; an item absent from the branch list is not offered |
| **Migration risk** | none |
| **Target stage** | Hypermarket pack — checkout-completion slice |

### RN-9 — The lane's interaction model cannot meet counter speed

| | |
|---|---|
| **Problem** | Every action is a form POST + 302 + full document reload: each scan, each quantity change, each removal, the payment. There is no partial update, no in-flight indicator, no keyboard model, and no touch-sized controls |
| **Evidence** | `Views/Hyper/PosLane.cshtml` — five `<form method="post">` blocks, 20 lines of JS, no fetch/XHR anywhere |
| **Proposed solution** | Rebuild the lane against the existing services as JSON endpoints. The engine needs **nothing**: `AddLineAsync`, `SetLineQtyAsync`, `RemoveLineAsync` and `PayAsync` already return `(ok, error)` tuples, and `GetOrderAsync` already returns a complete cart DTO. Keep the server-computes/client-displays discipline exactly as it is |
| **Business value** | Throughput. At one full page load per item, a 40-item basket is 40 round-trips |
| **Technical value** | Also fixes the missing loading state, which is the state whose absence produced the opaque-failure scenario HM-10 had to engineer around |
| **Affected files/flows** | `Views/Hyper/PosLane.cshtml`, thin JSON wrappers on `HyperPosController`; Flows 3–13 |
| **Dependencies** | **Design System tokens (2B)** — this is the module's highest-value screen and must not be built twice |
| **Security impact** | Endpoint count grows; each wrapper must keep `CanSell` and the capability gates. Idempotency must survive the transport change — the token is per pay *intent*, not per page load |
| **Accounting impact** | none if the settlement service is called unchanged. **Do not move any computation to the client** |
| **Inventory impact** | none |
| **UX/UI impact** | Highest of any item here |
| **Test approach** | acceptance parity: the JSON lane must produce byte-identical invoices, JEs and movements to the form lane for the same basket; plus an idempotency re-run over the new transport |
| **Migration risk** | Medium — it is a rewrite of the one screen that takes money. Recommend running both surfaces during a pilot rather than a cutover |
| **Target stage** | Hypermarket pack — after 2B tokens |

### RN-10 — Two working screens have no navigation entry

| | |
|---|---|
| **Problem** | `Views/Inventory/BulkPriceChange.cshtml` and `Views/Inventory/ShelfLabels.cshtml` are complete, tested HM-4 deliverables with no entry in `MainMenu.Inventory()` and no inbound link from any view. They are operated by typing a URL |
| **Evidence** | `grep "BulkPriceChange\|ShelfLabels" Models/Menu/MainMenu.cs Views/` → no matches |
| **Proposed solution** | Add both to the Inventory menu (and to the hyper menu for shelf labels, which is a store task). Ideally as part of the consolidated Pricing workspace |
| **Business value** | Two shipped features become usable |
| **Technical value** | none — pure navigation |
| **Affected files/flows** | `Models/Menu/MainMenu.cs` |
| **Dependencies** | none |
| **Security impact** | The three bulk mutating actions already carry `[InvPerm("doc")]`. The GET shells are authentication-only, which is acceptable for a preview but should be `[InvPerm("read")]` for consistency. **Unlinked was never protection** |
| **Accounting impact** | none |
| **Inventory impact** | none — pricing does not move stock |
| **UX/UI impact** | Medium |
| **Test approach** | a navigation test asserting every controller action rendering a top-level view is reachable from a menu; this class of defect recurs otherwise |
| **Migration risk** | none |
| **Target stage** | Hypermarket pack, first slice |

---

## Part 2 — RECOMMENDED NOW

Each of these is cheap **because the engine already exists and is already covered by an acceptance endpoint**.

### RC-1 — Expose multi-tender and split payment in the hyper lane
`PayTendersAsync` and `PaySplitByItemAsync` are implemented, remainder-exact and tested; the hyper lane hard-codes
`"Cash"`. Value: card acceptance is table stakes for a hypermarket. Impact: no accounting change (each tender settles to
its configured `BranchPaymentMethod.TargetAccountId`). Test: parity with the restaurant acceptance cases. Risk: none.
**Dependency:** the tender panel from RN-9's redesign.

### RC-2 — Tendered amount and change due
Not modelled anywhere: the lane sends no tendered amount and computes no change. Value: it is what a cash cashier
actually needs on screen. Impact: **display-only** — the receipt/GL amount is the invoice total either way, so no
posting changes. Test: change = tendered − total at the document currency's precision, with explicit
`AwayFromZero`. Risk: none. Note: if change is ever *stored*, it must go through `ICurrencyRounding`, never a `toFixed(2)`.

### RC-3 — Expose suspend/resume
`HoldOrderAsync`/`GetHeldOrdersAsync`/`RecallHeldAsync` exist and the capability already ships ON. This is RN-1's
"implement" branch for the most visible of the four dead keys. Note the engine refuses an empty order and refuses a
table order — both correct for hyper.

### RC-4 — Thermal receipt printing
`PosTerminal.ReceiptPrinterName`/`ReceiptPaperWidthMm`/`ReceiptCopies` are configured and unread. The restaurant lane
prints via `window.print()` against a receipt template; the hyper lane has only an A4 invoice. Value: a customer
receipt is a legal and practical expectation. Impact: none to posting. Risk: low. Note: ESC/POS is explicitly out of
scope (see PL-8).

### RC-5 — Make the Z report visible in full
`GetShiftZReportAsync` computes payment breakdown by method, tips, subtotal, service and tax; the lane renders **four
tiles**. Value: the manager's shift-close conversation happens over the payment breakdown. Impact: display only.
Sequencing: do this **with** RN-7 so the numbers shown are the corrected ones.

### RC-6 — A real hyper operations home
Replace the diagnostic panel as the landing page with today's sales, transaction count, average basket, live lane and
shift status, low stock and near-expiry counts. Every figure exists. Impact: read-only. Value: it is the module's first
impression and currently answers a setup-time question.

### RC-7 — Make the expiry report generate work
`ExpiryAlerts` lists near-expiry and expired on-hand and offers no action. Add row actions routing to the existing
write-off and transfer services, plus a branch/warehouse filter. Value: in grocery this is the report that must produce
work. Impact: no new posting logic — `StockWriteOff` already forces a named batch for tracked items, which is exactly
right for an expiry write-off. Note: **markdown** is deliberately *not* included here (see PL-3) because
`Promotion` has no batch dimension.

### RC-8 — Structural tests for the capability contract and for navigation reachability
Two small tests that would have caught RN-1, RN-2 and RN-10 at introduction: every catalogue key must have a consumer
and every preset key must be in a catalogue; every action returning a top-level view must be reachable from a menu.
Value: these three defects are the same defect three times. Cost: two test files, no production change.

---

## Part 3 — PLANNED LATER

| ID | Item | Why later, precisely |
|---|---|---|
| **PL-1** | **Basket-level promotions** (buy-X-get-Y, mix-and-match, second-item, basket threshold) | Needs a basket evaluator and an order-header discount concept. `PosOrderLine.DiscountAmount` is a single per-line scalar and `PosOrder` has no header discount field, so a basket rule cannot be represented today. Belongs to Pricing & Promotions, **not** the till |
| **PL-2** | **Coupons** | No entity, no redemption counter, no per-customer usage cap. Needs its own model plus an anti-reuse guarantee (the same INSERT-keyed pattern `HyperPayTokens` uses) |
| **PL-3** | **Markdown on near-expiry** | Requires `Promotion` to gain a batch dimension (HM-D54, already logged). Without it, a clearance price cannot be scoped to the lot that is expiring |
| **PL-4** | **Loyalty redemption** | HM-9 slice 3, deferred by owner decision after HM-10, with its policy already fixed: negative-balance rule decided, **liability-settlement** model (Dr points-liability / Cr cash, new `21xxxx` account) chosen over a discount model — because the single `DiscountAmount` scalar and the promotion comparator technically forbid the discount approach. Capability stays OFF until it ships |
| **PL-5** | **Gift cards / wallet** | No entity. Needs a liability account, an issue/redeem/expire ledger and the same derived-balance discipline as loyalty |
| **PL-6** | **Supplier-funded promotions** | `Promotion` has no vendor or funding dimension and there is no rebate accrual. Touches AP, not just pricing |
| **PL-7** | **Shelf vs backroom stock, and store replenishment** | `BinLocation`/`BinStock` exist, hold **quantity only, never value**, and are **entirely unused by the sale path** (`SalesLineInput` has no bin field). Retail is the natural first consumer — which is why this belongs to **WMS (Stage 4)**, not to the Hypermarket pack |
| **PL-8** | **Hardware: ESC/POS, cash drawer kick, customer display, ESL, label printer, EFT terminal, networked scale** | Five of the seven need a physical device we do not have. `PosTerminal.ReceiptPrinterName` and the `CashDrawer` capability are the placeholders. HM-D48 already defers label printers pending hardware and its command language |
| **PL-9** | **Click-and-collect** | Missing primitive: a server-side stock **reservation**. Nothing in the model reserves stock ahead of settlement — the same gap that blocked offline selling. Needs the reservation model first, then a pickup-slot and fulfilment state |
| **PL-10** | **Offline selling for the hyper lane** | Deferred for a stated architectural reason worth preserving: FEFO and the oversell guard both need a live SQL row lock (`UPDLOCK, HOLDLOCK`) that a disconnected terminal cannot hold. Two offline lanes selling the last units of a tracked batch produce a physical oversell and a COGS error a memo reversal cannot fix. Also: the restaurant offline engine trusts device-supplied 2-decimal prices (HM-D24) and **has never run on a real path** |
| **PL-11** | **Weighted-item extras: tare weight, min/max weight guard** | HM-D41. Needed for deli/butchery counters; not needed for the weighed-produce core |

---

## Part 4 — REJECTED

| ID | Proposal | Rejected because |
|---|---|---|
| **RJ-1** | Rebuild the Hypermarket as a separate platform with its own order and settlement engine | It would fork the one part of the module that is demonstrably correct — one transaction, two writers, currency-aware rounding, idempotent pay, FEFO — and double the surface exposed to the parallel team's kernel coupling (HM-D53). The engine is the asset; the surface is the problem |
| **RJ-2** | Merge the hyper and restaurant lanes into one lane driven by capabilities | The lanes have genuinely diverged: different session key, layout, capability catalogue, barcode semantics, currency precision, tax resolution level and reports. Merging would recreate the activity-mixing defect HM-10 slice B had to fix in reporting, this time in the till. **Share the core, keep the lanes** |
| **RJ-3** | Alias the unclaimed `Retail` activity preset to `Hyper` so it stops looking broken | `IsActivityAllowedForLane` is an explicit whitelist precisely so a new activity never leaks into a lane by default. Aliasing would defeat the design. Leave `Retail` unclaimed until a third lane exists |
| **RJ-4** | Generalise the restaurant offline engine to the hyper lane | Explicitly rejected at HM-10 with reasons that still hold: a different dataset (full barcode/scale catalogue vs a QuickMenu), a layer that trusts device 2-decimal prices, a re-requirement for the cancelled `(TerminalId, ReceiptNo)` unique index that caused a live collision (HM-D5-b), and no real-path evidence at all |
| **RJ-5** | Store the loyalty balance as a column for performance | The derived balance (`Σ signed Points`) is a deliberate repeat of the batch-on-hand decision, taken to avoid the stored-balance lost update recorded as DEV-2026-005. Reintroducing a stored balance would reintroduce a known defect for an unmeasured gain |
| **RJ-6** | Move any till computation to the client for speed | The whole module holds the line that the server computes and the view displays — which is why the hyper cart survives a disconnect and why the totals are currency-correct to three decimals. RN-9 makes the *transport* asynchronous; it must not make the *arithmetic* client-side |

---

## Part 5 — Reporting gap table (H11)

Classified per the brief's five values.

| Report | Status | Basis |
|---|---|---|
| Sales by hour | **Data Available but No Report** | `PosOrder.ClosedAt` is a timestamp; the report groups by day only |
| Sales by cashier | **Data Available but No Report** | `PosOrder.CashierUserId` + `PosShift.OpenedByEmployeeId` |
| Sales by branch | **Partial** | the hyper report has a branch filter; there is no side-by-side branch comparison |
| Basket size | **Data Available but No Report** | order count and line count are both present |
| Gross margin | **Existing** | `HyperItemMargin`, with the three-state cost guard |
| Discount analysis | **Data Not Available** | the winning promotion is folded into one effective `DiscountPercent` and **not persisted per line**. Analysing discounts requires storing the promotion id on the order line |
| Promotion performance | **Data Not Available** | same root cause: `PriceResult.PromotionId` is computed and never stored |
| Returns | **Data Available but No Report** | `SalesReturn` + `SalesReturnLine` |
| Refunds | **Data Available but No Report** | negative `Receipt` rows with an `RF-` prefix |
| Voids | **Data Available but No Report** | `PosOrder.Status == "Voided"` + reversal JEs |
| Shift variance | **Partial** | stored per shift and shown as one tile; no cross-shift or cross-branch variance report |
| Payment-method split | **Partial** | computed by `GetShiftZReportAsync` and **not displayed** |
| Item movement | **Existing** (Inventory) | `StockMovements` screen + item ledger |
| Dead stock | **Existing** (Inventory) | `StagnantReport` |
| Expiry | **Existing** (Inventory) | `ExpiryAlerts` + `Batches` |
| Waste | **Data Available but No Report** | `StockWriteOff` with reason codes |
| Stock-outs | **Data Not Available** | a refused sale is not recorded. The negative-stock block and the price-list rejection both fail the transaction silently from a reporting standpoint. **Capturing refused-sale reasons is the single highest-value new data point in this table** |
| Replenishment | **Partial** | `Planning` uses reorder point; it is not store-shelf replenishment |
| Queue time | **Data Not Available** | no per-item scan timestamp, no lane queue state |
| Cashier productivity | **Data Available but No Report** | orders and lines per cashier per shift are derivable |
| Customer loyalty | **Data Available but No Report** | `PointsMovements` gives balance, earn rate and per-customer history |
| Category performance | **Existing** | the by-category block of `HyperSales` |
| Brand performance | **Data Not Available for retail** | `BrandId` is stamped on `PosOrder` (the store's trade name) but not on the item, so item-brand analysis is impossible |

**Conclusion:** 5 Existing · 4 Partial · 9 Data Available but No Report · 5 Data Not Available. Two-thirds of the gap is
presentation, not capture — which makes hyper reporting a **Reporting-stage** deliverable rather than a Hypermarket one,
with three exceptions that must be captured at the source: refused-sale reasons, the applied promotion id per line, and
item brand.
