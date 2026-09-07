# Stage 2 — Hypermarket Screen Inventory (UX/UI assessment)

**Assessed from source and rendered structure. No screen implemented or modified.** Companion to
`Stage-002-Hypermarket-Current-State-Assessment.md` (§12).

**21 screens** are in scope: the 9 hyper views, the hyper lane shell, and 11 shared screens a hypermarket operator must
use to run the store. Each is assessed against all eighteen mandated attributes and given one of the six mandated
classifications.

**Classification totals:** Preserve **2** · Minor Improvement **4** · Major Redesign **5** · Replace UI Only **5** ·
Consolidate **4** · Legacy/Remove **1**.

**A standing note on backend quality.** In all five Major Redesign cases the server-side computation is already correct
and complete. `PosLane.cshtml` in particular performs **no arithmetic at all** — the server computes every total and
the view formats it at the currency's decimal precision. Per the brief, no backend replacement is recommended on the
basis of a weak UI.

**A standing note on RTL.** Every hyper view branches on
`CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar"` and the layouts swap the RTL Metronic bundles plus the
Cairo font. RTL/LTR parity is one of the module's consistent strengths and is not re-listed per screen except where it
breaks.

---

## 1. The cashier workspace — the single highest-value screen in the module

### `Views/Hyper/PosLane.cshtml` — 242 lines · **MAJOR REDESIGN**

| Attribute | Assessment |
|---|---|
| **Purpose** | Ring a sale: scan → cart → cash pay. Also displays terminal/shift state, capability badges and a 4-tile Z summary |
| **User role** | `pos-cashier` / `pos-manager` (enforced: `CanSell` on every mutating post) |
| **Type** | **Operational** (counter-facing), rendered on the desktop Metronic shell |
| **Main workflow** | one barcode input (autofocused) → POST → full page reload → cart table → "Pay cash" → POST → redirect → reload |
| **Information hierarchy** | Inverted for the task. The scan box and cart occupy the top card; **terminal/shift, capabilities and the Z report — none of which a cashier reads mid-sale — occupy the majority of the page** below it. The single most important number (grand total) is `fs-2` inside a flex-stack, competing visually with the shift badge |
| **Speed of operation** | **The critical weakness.** Every action is a form POST + 302 + full document reload: scan, quantity change, line removal, payment. On a real counter this is one full round-trip per scanned item. There is no partial update anywhere |
| **Keyboard support** | Only what the browser gives. The barcode input is `autofocus`, which makes wedge scanning work on **first** load — but after each POST the page reloads and focus returns to it by luck of `autofocus`, not by design. **No numeric keypad, no function keys, no shortcut for quantity/void/pay, no Enter-to-pay** |
| **Barcode-first usability** | Half-present. Scan-to-add works. But there is **no fallback path at all**: no item search, no PLU/`QuickCode` entry, no category browse. An item whose barcode is damaged or missing **cannot be sold** |
| **Touch usability** | Poor for a counter. Quantity is an `<input type="number">` (`w-90px`) with a spinner plus a separate tick button to submit; removal is a small `btn-icon btn-sm`. There is no touch-sized numeric entry and no confirm-on-destructive |
| **RTL/LTR** | Correct |
| **Loading state** | **None.** The Metronic page loader covers navigation; there is no in-flight indicator on the pay button, so a slow pay looks like a dead page — which is exactly the opaque-failure scenario HM-10 was built for |
| **Empty state** | Present and good: *"Cart is empty — scan an item to start."* |
| **Error state** | Present: `TempData["PosErr"]` alert. But it is a **page-top alert after a reload**, so on a long cart the cashier may not see it without scrolling |
| **No-permission state** | Redirect to login or an error alert. A `pos-waiter` who somehow held a hyper session sees "This role is not allowed to operate orders" after a POST — never a disabled control |
| **Offline behaviour** | A `navigator.onLine` banner plus a pre-submit guard that blocks *starting* a scan/pay while offline. Correctly documented as a **message, not a protection** — the real protection is server idempotency + receipt recovery |
| **Responsiveness** | Bootstrap grid, so it reflows — but it was not designed for a fixed-size till display, and the useful area shrinks as the informational cards grow |
| **Visual consistency** | Consistent Metronic 8 `card card-flush` usage; the brand green/gold applies via `crossbuy-brand.css` |
| **Metronic usage** | Standard components, no custom operational component |
| **Technical debt** | 20 lines of inline JS (token + banner); the Z report and capability badges are embedded in the selling screen rather than a manager view; the terminal card duplicates information already in the header |

**Missing entirely from this screen** (each verified as having no endpoint, not merely no button): item search · PLU
entry · manual line discount · tendered amount and **change due** · non-cash payment · split payment · suspend/resume ·
return · refund · void · thermal receipt print · customer-facing display feed · price override with approval.

**Why Major Redesign and not Replace UI Only.** The information architecture itself is wrong for the task (selling
competes with diagnostics), and the interaction model — one full page load per scanned item — cannot be fixed by
restyling. The *server* side needs almost nothing: `AddLineAsync`, `SetLineQtyAsync`, `RemoveLineAsync` and `PayAsync`
already return `(ok, error)` tuples and would serve a JSON lane unchanged.

---

## 2. Remaining hyper lane screens

### `Views/Hyper/PosLogin.cshtml` — 101 lines · **PRESERVE**

Purpose: lane sign-in. Role: any employee with a POS role. Type: operational, standalone (`Layout = null`).
Workflow: username + password → POST. Hierarchy: single centred card, correct. Keyboard: native form, Enter submits.
Touch: adequate. RTL: correct, with its own font block. Empty/loading: n/a. Error: `TempData["PosErr"]` alert above the
form — appropriate. No-permission: the four login guards each produce a distinct, localised message, which is genuinely
good diagnostics for a store manager. Offline: none needed. Debt: a documented verbatim clone of
`Views/PosApp/Login.cshtml`, so a change must be made twice — the shared-shell work in 2B absorbs this.
**Preserve; fold the duplication into the Retail Core shell.**

### `Views/Hyper/PosStart.cshtml` — 96 lines · **MINOR IMPROVEMENT**

Purpose: pick a terminal, open or resume a shift. Type: operational. Workflow: terminal cards → opening-float input →
POST. Hierarchy: clear, one card per terminal with open-shift state. Empty state: present ("no terminals"). Error:
present.
Improvements needed: (1) the POST it targets has **no role check** (finding HS-1) — the UI should not offer an action the
server will not authorise, and the server should authorise it; (2) no confirmation on the opening float, which is the
number the whole shift reconciles against; (3) it is a documented verbatim clone of `Views/PosApp/Start.cshtml`.
**Backend unchanged; add the role check and a float confirmation.**

### `Views/Hyper/Customer.cshtml` — 106 lines · **CONSOLIDATE (into the redesigned lane)**

Purpose: search/quick-add a customer, show the linked customer and read-only points balance. Type: operational.
Workflow: leaves the lane, links, returns. Hierarchy: single 640px card, clean. Keyboard: search box, no debounce
mentioned in the view. Touch: adequate. Not-enabled state: present and explicit ("Customer identification is not
enabled on this branch"). Error/success: both present.
**Problem:** it is a **separate page in the middle of a sale.** Linking a customer mid-transaction should be a panel in
the lane, not a navigation away from the cart. The engine is already pre-pay-only and fail-closed on the AR control
account, so the move is purely presentational.

### `Views/Hyper/PriceCheck.cshtml` — 55 lines · **MINOR IMPROVEMENT**

Purpose: read-only price lookup by fixed or scale barcode. Type: operational. Workflow: scan → POST → result card
showing name, price at currency precision, unit, per-kg marker, scale code and price source. Hierarchy: excellent —
large input, large price. Keyboard: autofocus + Enter. Empty/error/not-enabled: all three present with distinct
messages. Offline: none.
Improvements: it requires a full cashier `HyperCtx` login, so it cannot serve a shop-floor kiosk (HM-D49 — deferred by
name); and it does **not** show stock on hand, which is the second question anyone asks at a price check.
**Best-designed screen in the module. Preserve the design, extend the content, add a kiosk-mode guard.**

### `Views/Hyper/Receipts.cshtml` — 57 lines · **MINOR IMPROVEMENT**

Purpose: receipt recovery — the last 10 paid orders of **this terminal** with a reprint link. Type: operational.
Workflow: read-only list. Hierarchy: clean, 760px card, explicit "Read-only" note. Empty state: present (`—`).
Security: correctly scoped to (company, branch, terminal) so a cashier cannot see another lane's receipts.
Improvements: fixed 10-row window with no date filter or receipt-number search — after a busy hour the sale you are
looking for may already be off the list; and the "Reprint" target is the **A4 invoice**, not a receipt, so it only works
where the `OfficialInvoice` capability is on (which today no production path can enable).

### `Views/Shared/_LayoutHyperPos.cshtml` — 114 lines · **CONSOLIDATE**

The lane chrome: header, hyper badge, branch badge, user menu. A **documented verbatim clone** of `_LayoutPosApp`, and
one of the 11 duplicated shells already counted in the Design System proposal. Includes a frame-breakout script and a
theme-mode bootstrap. **Consolidate into one operational shell with slots** as part of 2B.

---

## 3. Hyper back-office screens

### `Views/Hyper/Dashboard.cshtml` — 246 lines · **MAJOR REDESIGN**

| Attribute | Assessment |
|---|---|
| **Purpose** | A **system diagnostic panel**: branch picker, activity code, capability badges, POS setting, terminal list, open-shift count |
| **User role** | back-office employee. **Authentication only — no role check** |
| **Type** | Desktop, on `_LayoutBackend` with `MainMenu.Hyper()` |
| **Main workflow** | pick a branch → read state. **Nothing on this screen is actionable** |
| **Information hierarchy** | It answers *"is this branch configured?"* — a question asked once at setup — and never answers *"how is the store doing?"*, which is what a landing page must answer |
| **Missing** | today's sales, transactions, average basket, live lane status, low stock, near-expiry, cash position. All of that data exists |
| **Empty state** | Present and thoughtful: a "no hyper branch" state, plus a distinction between *"no capability config applied"* and *"configured but off"* |
| **Error/loading/no-permission** | none |
| **Debt** | It is the hyper system's **home page**, so its emptiness is the first impression of the module |

**Why Major Redesign.** The screen is not wrong, it is the wrong screen. A hypermarket home page should be an
operations dashboard with the diagnostic panel demoted to a "Setup health" tab.

### `Views/Hyper/HyperSales.cshtml` — 83 lines · **MAJOR REDESIGN**

Purpose: hyper sales overview (revenue in document currency, order count, margin in functional currency, top 10 items,
by category), free date range, optional branch filter. Type: desktop report.
It **carries its own verdict**: line 18 renders *"Functional placeholder layout — awaiting the final report design."*
Hierarchy: filter bar + tiles + two tables. Empty state: implicit (empty tables). Loading: none — a wide date range on
a real store re-renders the whole page synchronously. No chart, no export, no drill-through to the invoice, no
comparison period, no hour-of-day. The `AnyUnposted` flag is computed and surfaced, which is good discipline.
**Backend is correct and should not be touched; the presentation is explicitly provisional.**

### `Views/Hyper/HyperItemMargin.cshtml` — 81 lines · **MAJOR REDESIGN**

Same placeholder banner. Purpose: per-item qty, revenue (document currency), COGS and margin (functional), margin %, an
*indicative* document-currency margin, and a three-state cost status.
**The one thing to carry forward verbatim** is the cost-status guard: `Costed` / `ZeroCost` / `Unposted`, with an
Unposted item's margin rendered as "—" rather than 0 or 100%, and the indicative column left **blank** when today's rate
is missing or stale rather than showing a bait rate. That is a reusable reporting pattern, not a hyper detail.
Missing: sort, export, pagination (the table is unbounded), category roll-up toggle.

---

## 4. Shared screens a hypermarket operator must use

### `Views/Pos/Setup.cshtml` — 232 lines · **MAJOR REDESIGN**

Purpose: assign `Branch.ActivityPresetCode` and edit branch capabilities, sales warehouse, price list, service charge
and currency. Type: desktop admin. Role: `[SessionValidation]` + a real `PosGate` role check on the POSTs.

**This screen is the module's structural UX defect.** Three problems, all verified:

1. **It lives on the Restaurant sidebar** (`ViewBag.SidebarMenu = MainMenu.Restaurant()`), so configuring a hypermarket
   branch means entering the Restaurant system.
2. **It is bound to the wrong capability list.** `PosController.SaveCapabilities` builds its dictionary from
   `PosController.Capabilities` — the **7 restaurant keys** (`Tables`, `Kitchen`, `Manufacturing`, `Modifiers`,
   `QrOrder`, `Barcode`, `Weight`). The 10 hyper keys are not editable here. Because `SaveCapabilitiesAsync` **upserts**
   rather than replaces, existing hyper rows survive — so this is a *cannot-edit*, not a *data-loss*, defect.
3. **`SavePosSettingAsync` writes 4 of the 14 `BranchPosSetting` columns** (warehouse, price list, service charge,
   currency). The remaining ten — `DefaultTaxCodeId`, the six `Scale*` columns, the three delivery columns, and
   `LoyaltyPointsPerCurrencyUnit` — have **no writer anywhere in the application**. They are set by SQL or by a
   `[DevOnly]` endpoint.

**Consequence:** weighted selling, the scale-barcode format, branch tax, and the loyalty rate are not configurable by
any user. This is the largest single functional gap in the module and it is a *screen* gap, not an engine gap.

### `Views/Inventory/ItemForm.cshtml` — 579 lines · **MAJOR REDESIGN (retail fields)**

Purpose: item master editor. Present and good: item code, primary barcode with a generator, category, base unit, and a
**units grid** with per-unit conversion factor **and** per-unit barcode — which is exactly the multi-unit retail model
the till consumes.
**Absent:** `IsWeighted`, `ScaleCode`, `LoyaltyEligible`. All three are validated by `ItemService`
(`ScaleCode` is required when `IsWeighted`, must be unique per company, and a new fixed barcode inside any branch's
scale range is rejected) — and none can be entered. Verified by `grep "Weighted\|ScaleCode\|LoyaltyEligible" Views/`,
which returns only report/CRM matches.
**A weighted item cannot be created through the UI.** Master Data (2C) must add these fields regardless of what else it
changes.

### `Views/Inventory/BulkPriceChange.cshtml` — 96 lines · **REPLACE UI ONLY**

Purpose: preview → confirm → execute → undo a bulk price change with a mandatory reason. The engine behind it is the
best-engineered piece of the pricing layer (preview writes nothing; execute re-verifies the shown baseline and refuses
on drift; undo writes the stored old price, never a reverse percentage).
**Reachable by URL only** — no entry in `MainMenu.Inventory()` and no inbound link from any view (verified by grep over
`Models/Menu/MainMenu.cs` and all of `Views/`). Scope is limited to one optional category (HM-D50); real operations want
price bands, explicit selections, name/code search and exclusions.
**Keep the engine exactly as it is. Give it a menu entry, a scope builder and a batch history view.**

### `Views/Inventory/ShelfLabels.cshtml` — 69 lines · **REPLACE UI ONLY**

Purpose: A4 shelf-label sheet with a self-computed EAN-13 SVG (correct module ratios, GS1-minimum quiet zones,
round-trip self-tested) and a per-kg / scale-code variant for weighted items. Print via `window.print()`.
**Also reachable by URL only.** Scope is a whole price list with no filter, no label-size choice, no per-item selection,
and no label-printer output (HM-D48 — deferred pending hardware).
**Keep `ShelfLabelService` verbatim; rebuild the selection and layout surface.**

### `Views/Inventory/PriceListEditor.cshtml` — 173 lines · **REPLACE UI ONLY**

Purpose: edit a price list and its lines (per-unit, `MinQty`, `Fixed`/`CostPlus`, markup, validity). Functional.
Weaknesses: line-by-line editing with no bulk paste or import, no item search inside the grid, no visual indication of
which line the engine would actually pick for a given quantity/unit — which is the question a pricing manager actually
has. Also inherits HM-D47: every save full-replaces the lines, discarding `CreatedAt`/`CreatedBy` and renumbering ids.
**The renumbering is why `PriceChangeLog` keys on `(PriceListId, ItemId, UoMId)`; do not "fix" the log, fix the save.**

### `Views/Inventory/PromotionEditor.cshtml` — 101 lines · **REPLACE UI ONLY**

Purpose: create/edit a promotion. Functional, with a genuinely useful non-blocking typo warning above 90%.
Weaknesses: no preview of the resulting shelf price, no conflict view against other active promotions (the engine picks
exactly one winner and the user cannot see which), no branch activation control on the screen (activation is the
branch `Promotions` capability, edited elsewhere), and no warning when `Amount` is chosen for a multi-unit item —
which is HM-D51's recommended UI mitigation.

### `Views/Inventory/PriceLists.cshtml` + `Views/Inventory/Promotions.cshtml` — 45 lines each · **CONSOLIDATE**

Two near-identical list shells (filter + partial rows + paging). Both are correct and both are thin. They belong in one
**Pricing workspace** with tabs, alongside bulk change and shelf labels — the four screens are one job.

### `Views/Inventory/ExpiryAlerts.cshtml` — 49 lines · **REPLACE UI ONLY**

Purpose: near-expiry and expired on-hand by batch, `days` parameter. In the Inventory reports alias list, so it is
reachable. Weaknesses: no branch/warehouse filter (a chain manager sees everything at once), no action from the row —
no markdown, no write-off, no transfer — and **no push notification** (deferred by name in HM-6). For a grocery
operation this is the report that must generate work, and today it only generates reading.

### `Views/Inventory/Batches.cshtml` — 44 lines · **MINOR IMPROVEMENT**

All batches with on-hand and days-to-expiry, sorted by urgency. Correct and adequate. Needs a filter and an export.

### `Views/Accounting/SalesInvoicePrint.cshtml` — **PRESERVE**

One A4 template serving two thin actions (hyper lane and accounting), fed by `OfficialInvoiceHelper`. Correct
separation, correct localisation, browser print. The **only** issue is upstream: the `OfficialInvoice` capability that
gates the hyper caller has no production path to enable it.

### `Views/Home/Index.cshtml` — 12 lines · **LEGACY / REMOVE CANDIDATE**

A default ASP.NET scaffold page ("Learn about building Web apps with ASP.NET Core"). Not retail-specific, but it sits on
the switcher path and is the only genuine legacy artefact encountered during this sweep.

---

## 5. Attribute coverage matrix

`✓` present · `~` partial · `✗` absent · `n/a` not applicable.

| Screen | Keyboard | Barcode-first | Touch | Loading | Empty | Error | No-perm | Offline | Responsive | Class |
|---|---|---|---|---|---|---|---|---|---|---|
| `PosLane` | ~ | ~ | ✗ | ✗ | ✓ | ✓ | ~ | ~ | ~ | **Major Redesign** |
| `PosLogin` | ✓ | n/a | ✓ | ✗ | n/a | ✓ | ✓ | n/a | ✓ | Preserve |
| `PosStart` | ~ | n/a | ✓ | ✗ | ✓ | ✓ | ✗ | ✗ | ✓ | Minor |
| `Customer` | ~ | n/a | ✓ | ✗ | ✓ | ✓ | ✓ | ✗ | ✓ | Consolidate |
| `PriceCheck` | ✓ | ✓ | ✓ | ✗ | ✓ | ✓ | ✓ | ✗ | ✓ | Minor |
| `Receipts` | ✗ | n/a | ✓ | ✗ | ✓ | n/a | ✓ | ✗ | ✓ | Minor |
| `Hyper/Dashboard` | ✗ | n/a | ~ | ✗ | ✓ | ✗ | ✗ | n/a | ✓ | **Major Redesign** |
| `HyperSales` | ✗ | n/a | ~ | ✗ | ~ | ✗ | ✗ | n/a | ✓ | **Major Redesign** |
| `HyperItemMargin` | ✗ | n/a | ~ | ✗ | ~ | ✗ | ✗ | n/a | ~ | **Major Redesign** |
| `Pos/Setup` | ✗ | n/a | ~ | ✗ | ✓ | ✓ | ✓ | n/a | ✓ | **Major Redesign** |
| `ItemForm` | ~ | ~ | ~ | ✗ | n/a | ✓ | ✓ | n/a | ✓ | **Major Redesign** |
| `BulkPriceChange` | ✗ | n/a | ~ | ~ | ✓ | ✓ | ~ | n/a | ✓ | Replace UI |
| `ShelfLabels` | ✗ | n/a | ~ | ✗ | ✓ | ✗ | ✗ | n/a | ~ | Replace UI |
| `PriceListEditor` | ~ | n/a | ~ | ✗ | ✓ | ✓ | ✓ | n/a | ✓ | Replace UI |
| `PromotionEditor` | ~ | n/a | ~ | ✗ | n/a | ✓ | ✓ | n/a | ✓ | Replace UI |
| `PriceLists` | ✗ | n/a | ~ | ~ | ✓ | ✓ | ✓ | n/a | ✓ | Consolidate |
| `Promotions` | ✗ | n/a | ~ | ~ | ✓ | ✓ | ✓ | n/a | ✓ | Consolidate |
| `ExpiryAlerts` | ✗ | n/a | ~ | ✗ | ✓ | ✗ | ✗ | n/a | ✓ | Replace UI |
| `Batches` | ✗ | n/a | ~ | ✗ | ✓ | ✗ | ✗ | n/a | ✓ | Minor |
| `SalesInvoicePrint` | n/a | n/a | n/a | n/a | n/a | n/a | ✓ | n/a | print | Preserve |
| `_LayoutHyperPos` | n/a | n/a | ✓ | ✓ | n/a | n/a | n/a | n/a | ✓ | Consolidate |

**Two systemic patterns visible in the matrix:**

1. **Loading state is absent on 19 of 21 screens.** Every hyper interaction is a synchronous full-page POST, so the
   application has never needed one. It becomes mandatory the moment the lane becomes asynchronous — and it is the exact
   state whose absence produced the HM-10 opaque-failure scenario.
2. **No-permission state is absent on the reporting and label screens** because they are authentication-only. That is
   consistent with their read-only nature, but the two URL-only screens (`BulkPriceChange`, `ShelfLabels`) are the
   uncomfortable case: their mutating actions are properly gated, their shells are not, and neither is discoverable.

---

## 6. Expected screens for a rebuilt Hypermarket

**14–19 screens**, of which 9 are redesigns of screens listed above and 5–10 are new. Ranges are given where the count
depends on a design decision rather than a requirement.

| Screen | العربية | New / Redesign | Type | Priority | Why |
|---|---|---|---|---|---|
| **Cashier lane** | ممرّ الكاشير | Redesign | O | **1** | The module's core surface. Must carry search/PLU, keypad, tender + change, suspend/resume, return/void, receipt print |
| Tender panel | لوحة الدفع | New | O | 1 | Cash + card + split + change due. The engine exists; nothing exposes it |
| Correction panel (return / refund / void) | المرتجعات والإلغاء | New | O | 1 | Engine complete, no route. **Must ship with the `ExpectedCash`/`ReturnsTotal` completion** |
| Suspend / resume tray | الطلبات المعلَّقة | New | O | 2 | `HoldOrderAsync`/`RecallHeldAsync` exist; capability ships ON with no consumer |
| Customer panel (in-lane) | لوحة العميل | Redesign (from `Customer.cshtml`) | O | 2 | Stop navigating away mid-sale |
| Hyper operations home | لوحة تشغيل الهايبر | Redesign (from `Dashboard.cshtml`) | D | 2 | Today's sales, baskets, lanes, cash, low stock, near expiry |
| **Hyper branch setup** | إعداد فرع الهايبر | **New** | D | **1** | The largest gap: capabilities (10 hyper keys), scale-barcode format, branch tax, branch currency, loyalty rate, sales warehouse, price list — none editable today |
| Terminal & payment-method setup (hyper context) | الأجهزة وطرق الدفع | Redesign | D | 2 | Exists on the restaurant sidebar only |
| Shift & cash-control workspace | الورديات والنقدية | New | D | 2 | Z report is computed in full and only 4 tiles are shown; no multi-shift view, no variance history |
| Pricing workspace (lists · promos · bulk · labels) | مساحة الأسعار | Consolidate 4 → 1 | D | 2 | Four thin screens that are one job; two of them have no menu entry |
| Shelf-label builder | منشئ ملصقات الرفوف | Redesign | D | 3 | Keep `ShelfLabelService`; rebuild selection + layout |
| Price-check kiosk mode | كشك استعلام السعر | New | O | 3 | HM-D49; needs a lighter guard than a cashier session |
| Expiry & markdown worklist | قائمة الصلاحية والتخفيض | Redesign + New action | D | 2 | Make the report generate work: markdown, write-off, transfer |
| Item master retail tab | تبويب التجزئة للصنف | Redesign (in `ItemForm`) | D | **1** | `IsWeighted`, `ScaleCode`, `LoyaltyEligible`, per-unit barcodes in one place |
| Hyper sales report | تقرير مبيعات الهايبر | Redesign | D | 3 | Placeholder by its own admission |
| Item-margin report | تقرير هامش الصنف | Redesign | D | 3 | Preserve the three-state cost guard verbatim |
| Customer display (second screen) | شاشة العميل | New | O | 3 | Nothing exists; needs a push channel the lane does not have |
| Basket-promotion builder | منشئ عروض السلة | New | D | 4 | Belongs to Pricing & Promotions, not the till |
| Loyalty redemption panel | استبدال نقاط الولاء | New | O | 4 | HM-9 slice 3, deferred with its policy already decided |

**Sequencing note.** Priority-1 items are four: the lane, the tender panel, the correction panel, and hyper branch
setup — plus the item retail tab, which is a Master Data deliverable rather than a hypermarket one. Nothing on this list
requires a backend rebuild; the tender, correction and suspend panels are **exposure of engine capability that already
exists and is already tested by acceptance endpoints**.
