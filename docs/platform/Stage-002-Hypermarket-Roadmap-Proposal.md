# Stage 2 — Hypermarket Roadmap Proposal

**Proposal only. `Stage-002-Dependency-Roadmap.md` is NOT edited by this document.** Fulfils H16. Companion to
`Stage-002-Hypermarket-Current-State-Assessment.md` (§13) and `Stage-002-Hypermarket-Gap-Analysis.md`.

This document answers three questions and nothing else: **what shape** Hypermarket should take, **what it depends on**,
and **where it belongs** in the existing sequence.

---

## 1. Recommended shape

The brief offers five candidate classifications. Each is answered from evidence.

| Candidate | Verdict | Decisive evidence |
|---|---|---|
| **Part of POS** (a configuration) | **No** | Ten phases of divergence that no settings row can express: own route (`/hyper/pos`), own session key (`HyperCtx`), own layout, own capability catalogue (10 keys sharing exactly one with the restaurant's 7), own whitelist entry, own idempotency table (`HyperPayTokens`), own barcode-resolution semantics (scale-range routing before lookup), own document currency at 3 decimals inside an EGP-functional company, own tax-resolution level (branch), own reports, own deploy runbook |
| **A retail platform built on POS** | **Close, and this is the direction** | The engine is genuinely shared and genuinely correct: `PosOrderService` (1957 lines, one implementation), `PosSetupService`, `PosAccessService`, `PricingService`, and both writers. But "built on POS" understates it — POS today *is* the restaurant product plus a shared engine tangled together |
| **A separate enterprise retail platform** | **No** | It would fork the one demonstrably correct part of the module (one transaction, two writers, currency-aware rounding, idempotent pay, FEFO) and double the surface exposed to the parallel team's kernel coupling (HM-D53). Rejected as RJ-1 |
| **An industry pack** | **Partly** | Correct for the *hypermarket-specific* half (scale barcodes, weighted selling, shelf labels, price check, loyalty, hyper reports) — but a pack alone leaves the shared core unowned |
| **Shared retail core + Hypermarket pack** | **RECOMMENDED** | It matches what the code already half-is, and it names an owner for the shared half — which is the thing currently missing |

### 1.1 The recommendation

**Extract a Retail Core from the existing POS engine; deliver Hypermarket as a pack on top of it; leave Restaurant as a
second pack on the same core.**

| Layer | Contents | State today |
|---|---|---|
| **Retail Core** | order + settlement engine · terminal / shift / cash control · pricing + promotions · barcode + unit resolution · capability gating · the single lane-activity whitelist · one operational lane shell with slots | **~80% assembled.** The work is extraction and de-duplication, not construction. The one genuine construction item is the shell (`_LayoutHyperPos` is a documented verbatim clone of `_LayoutPosApp`) |
| **Hypermarket pack** | scale barcodes + weighted selling · multi-barcode / multi-unit checkout · shelf labels · price check · retail expiry/FEFO enforcement · loyalty · hyper reports · **hyper back-office setup (does not exist)** | Engine parts done; **surface and setup are the gap** |
| **Restaurant pack** | tables + floor plan · modifiers · KDS · delivery + drivers · reservations · tips · the offline queue | Complete, and the only place the offline engine should stay |
| **`Retail` activity** | declared in the preset catalogue, **claimed by no lane** | Leave unclaimed. `IsActivityAllowedForLane` is an explicit whitelist precisely so a new activity never leaks in by default (rejected as RJ-3) |

### 1.2 Why "separate controllers" is not evidence of legacy duplication

The two lane controllers were created as deliberate clones — the comments in `_LayoutHyperPos.cshtml:7` and
`Views/Hyper/PosStart.cshtml:13` say "verbatim clone" explicitly — and have since diverged **functionally**, not by
drift. `HyperPosController` gained scale routing, multi-barcode gating, an idempotency token, customer identity, price
check and A4 invoicing; `PosAppController` gained tables, modifiers, KDS, delivery, multi-tender, split and offline
sync. Neither is a stale copy of the other.

**Three couplings are wrong, and all three are surface-level, not engine-level:**

1. Hyper branch configuration lives on the Restaurant sidebar (`Views/Pos/Setup.cshtml`), bound to the 7 restaurant
   capability keys — so the 10 hyper keys cannot be edited at all.
2. Hyper production, sourcing and transfer are driven from `PosController` (`/Pos/ItemSourcing`,
   `/Pos/SourcingOverview`), so a hypermarket manager configures fresh-food sourcing inside the Restaurant system.
3. `PosController.Dashboard` mixed restaurant and hyper sales until HM-10 slice B added an **opt-in** `activity` filter —
   so the default view still mixes them.

Fixing these three is the Retail Core extraction's user-visible payoff.

---

## 2. Dependencies, with the reason each is a dependency rather than a preference

### 2.1 Master Data (Stage 2C) — **hard predecessor**

Three fields must survive a Master Data migration **byte-for-byte**, because their corruption does not produce a wrong
price (recoverable) but a **wrong quantity posted to stock and COGS** — the one error class the two-writers rule cannot
reverse cleanly:

| Field | Why it is non-negotiable |
|---|---|
| `ItemBarcode.UoMId` | This is what makes "1 carton = 144 base". Dropping or flattening it sells cartons at piece quantities and posts 1 unit of stock where 144 left the shelf |
| `UoMConversion.Factor` (per `ItemId`, `FromUoMId`→`BaseUoMId`) | The conversion is **per item**, not global. `AddLineAsync:545–549` *rejects* a unit with no conversion, so a lossy migration produces an outage rather than a silent error — the safe failure, but a hard stop at every till |
| `Item.ScaleCode` + its **filtered** unique index per company | The scale barcode carries this code and nothing else. Two items sharing a code sell the wrong product at the wrong price with no error anywhere |

**Recommended addition to the 2C migration gate** (which already has a stock-valuation non-regression gate for R-22):
a per-row assertion on these three, run before and after, with counts and checksums — not a spot check.

Master Data also owns two items this assessment surfaced: the **retail tab on the item form**
(`IsWeighted`/`ScaleCode`/`LoyaltyEligible`, none of which has a field today) and the **packaging-vs-unit** question —
packaging is currently modelled *as a unit* with a conversion and its own barcode and price, which works and has no
nesting or GTIN hierarchy.

### 2.2 Design System tokens (Stage 2B) — **hard predecessor for the lane rebuild**

The cashier lane is the module's highest-value screen and the one screen that must not be built twice. It also makes a
case the Design System proposal should absorb: an **operational** screen tier distinct from the desktop tier. The
existing 11-shell duplication count already includes `_LayoutHyperPos`; the Retail Core's single lane shell is the
concrete de-duplication.

The *other* Hypermarket priority-1 items — hyper branch setup, the correction panel, PLU/search — are back-office or
endpoint work and **do not** need to wait for tokens.

### 2.3 Pricing & Promotions — **peer, and the correct owner of basket logic**

The pricing engine as it stands should be treated as **accepted architecture** and preserved: deterministic six-key
resolution, `Fixed`/`CostPlus` modes, per-unit lines, the preview→confirm→audit→undo bulk-change machinery, the margin
floor, and the two deliberately separated rounding systems (currency precision first via `ICurrencyRounding` with
explicit `AwayFromZero`, then the commercial fils step, with a step finer than the currency **refused**).

What belongs to Pricing & Promotions and **not** to the till: basket-level rules (buy-X-get-Y, mix-and-match,
second-item, basket thresholds), coupons, promotion stacking, the three missing `Promotion` dimensions (`UoMId` —
HM-D51, batch — HM-D54, vendor funding), and persisting the applied promotion id per line so promotion performance
becomes measurable at all.

### 2.4 WMS (Stage 4) — **successor, and the first real consumer of the bin layer**

`BinLocation` (Section/Rack/Bin) and `BinStock` exist, hold **quantity only, never value**, and are **entirely unused by
the retail sale path** — `SalesLineInput` has no bin field and `PayAsync` passes only a warehouse. Shelf-vs-backroom
distinction and store-shelf replenishment therefore belong to WMS, which is where the bin dimension gets its first
genuine consumer. Retail is the use case that will prove it.

### 2.5 Finance — **no change requested, one hard constraint**

Posting is correct and should not be touched: two writers only, reverse-never-delete, realized FX on refund, threefold
duplicate-post protection, subledger reconciliation checks.

**The one live constraint** is a sequencing one: any hyper refund endpoint **must** ship in the same slice as the
`ExpectedCash`/`ReturnsTotal` completion (gap items RN-6 + RN-7). Shipping refunds first creates a real GL error — every
refunded shift would over-state expected cash and post a false shortage to `520111`.

A second, standing constraint carried from HM-D53: **every accounting-correction path reverses a journal entry**, and
`ReverseAsync` is kernel-wired in-transaction with no swallow. Any environment taking hyper corrections must have the
platform slices applied first.

### 2.6 CRM & Loyalty — **owner of redemption**

Loyalty earn is complete and dormant (capability OFF). Redemption (HM-9 slice 3) is deferred with its design already
fixed: negative-balance policy decided, and a **liability-settlement** model (Dr points-liability / Cr cash, new
`21xxxx` account, revenue and VAT untouched) chosen over a discount model — because the single `DiscountAmount` scalar
and the promotion comparator technically forbid the discount approach. That decision should be honoured, not revisited,
and the work belongs to CRM/Loyalty rather than the till.

### 2.7 Commerce — **click-and-collect needs a primitive that does not exist**

The missing primitive is a server-side stock **reservation**. Nothing in the model reserves stock ahead of settlement,
which is the same gap that blocked offline selling. Click-and-collect is therefore a Commerce deliverable **after** the
reservation model, not a Hypermarket one.

### 2.8 Reporting & AI — **owner of two-thirds of the reporting gap**

Of 23 report types: 5 Existing, 4 Partial, **9 Data Available but No Report**, 5 Data Not Available. Two-thirds of the
gap is presentation, so it belongs to the Reporting stage — **with three exceptions that must be captured at the source
and therefore fall inside the Hypermarket pack**:

1. **Refused-sale reasons** — a sale refused by the negative-stock guard or by price-list absence leaves no trace, so
   stock-outs are unmeasurable. Highest-value new data point in the whole reporting table.
2. **Applied promotion id per line** — computed as `PriceResult.PromotionId` and never persisted, which makes discount
   and promotion analysis impossible rather than merely unbuilt.
3. **Item brand** — `BrandId` is stamped on `PosOrder` (the store's trade name) but not on the item, so item-brand
   performance cannot be reported.

Two patterns from the hyper reports should be lifted into the reporting standard: the **three-state cost guard**
(Costed / ZeroCost / Unposted, with an unposted margin rendered "—" and never 0 or 100%) and the **blank-not-bait rule**
for a stale conversion rate.

---

## 3. Proposed position in the existing sequence

The accepted sequence is **2A → 2B → 2C → 2D → 3 (HR) → 4 (WMS) → 6 (FinOps) → 5 (Manufacturing) → 7 (CX) → 8 (CRM) →
9 (Reporting/AI) → 10 (Ecosystem)**.

**Hypermarket does not need a stage of its own, and should not get one.** It splits cleanly across three existing
positions:

| Slice | Contents | Position | Why there |
|---|---|---|---|
| **H-A — Truth and control** | RN-1 (dead capabilities) · RN-2 (`OfficialInvoice`) · RN-4 (hyper setup screen) · RN-5 (shift-open role check) · RN-10 (menu entries) · RC-8 (two structural tests) | **Inside 2C, alongside Master Data** | None of it needs tokens or new models. It is the smallest set that makes the module *honest* (nothing claimed that is not there) and *operable without a DBA*. It also removes incorrect coupling #1 |
| **H-B — Checkout completion** | RN-6 + RN-7 (corrections **and** the cash-reconciliation completion, one slice) · RN-8 (PLU + search) · RN-9 (lane rebuild) · RC-1 (tenders) · RC-2 (change due) · RC-3 (suspend/resume) · RC-4 (receipt print) · RC-5 (full Z) · RC-6 (operations home) | **Immediately after 2B tokens; may run in parallel with 2C** | The lane rebuild is gated on tokens and nothing else. Everything else in the slice is engine exposure. This is where the module becomes a real checkout |
| **H-C — Retail Core extraction** | one operational lane shell · shared capability catalogue mechanism · move sourcing/production/transfer screens out of the Restaurant sidebar · make the dashboard activity filter default rather than opt-in | **2D, with the Design System catalogues** | It is a consolidation, it touches the restaurant product, and it should follow rather than precede the two packs' own cleanup. Doing it first would refactor code whose second consumer is still changing |

**Deliberately left in their existing owners:** basket promotions and coupons → Pricing & Promotions · shelf/backroom
and store replenishment → WMS (Stage 4) · loyalty redemption → CRM (Stage 8) · click-and-collect → Commerce, after the
reservation model · hyper report redesign → Reporting (Stage 9), except the three capture-at-source items which sit in
H-A · all hardware → deferred pending devices.

### 3.1 What this costs, honestly

No estimate in days is offered — none would be defensible from a source read. What *is* defensible:

- **H-A is small and almost entirely additive.** Its largest item (the setup screen) reuses `PosSetupService` and needs
  one extended writer method, not a new service. Its riskiest item is RN-5, which could break a live deployment where a
  non-cashier role currently opens shifts — audit `BranchUserRoles` first.
- **H-B is one substantial rewrite (the lane) plus eight thin exposures.** The eight are cheap *because* the engine and
  its acceptance coverage already exist. The rewrite is the module's real cost and its real payoff.
- **H-C is a refactor with a second consumer (Restaurant) that must be coordinated**, which is why it is last.

### 3.2 A recommendation requiring an owner decision

**The Retail Core needs a named owner, and today it has none.**

`PosOrderService`, `PosSetupService` and `PosAccessService` are consumed by two products and owned by neither. This is
already visible in the record: HM-10 slice B had to add an *opt-in* activity filter to `PosController.Dashboard` rather
than change its default, because changing the default would have altered the restaurant product from the hypermarket
track. That was the correct call under the current ownership model, and it left the mixing defect half-fixed.

The same ambiguity applies with more force to RN-5, which is a **shared** change to both lanes' shift-open path.

Two options, and this document does not choose between them:

- **(a)** Assign Retail Core ownership to the platform owner, with both packs as consumers. Shared changes become
  platform changes with a coordination step, as already required for the two writers.
- **(b)** Keep pack-local ownership and require a two-owner sign-off for any shared-service change. Cheaper now, and it
  is exactly the arrangement that produced the half-fixed dashboard.

Recommended: **(a)**, for the same reason `JournalEntryService` and `StockService` are already treated as architectural
invariants with a named owner — the parallel team's uncoordinated kernel wiring of `ReverseAsync` (HM-D53) is the
standing demonstration of what unowned shared code costs.

---

## 4. What must not change

Carried forward as **accepted architecture**. Each of these was arrived at through a recorded defect, and each should be
preserved verbatim through any redesign.

| Invariant | Why it exists |
|---|---|
| An **open order writes nothing** to GL or stock; all accounting happens at settlement | The module's core correctness property |
| **One ambient transaction** wraps the whole settlement (`ScopedTx.BeginOrJoinAsync`); inner services **join**, never open their own | Makes a sale all-or-nothing across invoice + GL + stock + receipt + loyalty + token |
| **Two writers only** — `JournalEntryService` for GL, `StockService` for stock | Architectural invariant; the hyper track adds none |
| **Reverse, never delete** — corrections are reversing entries | Applies to the loyalty ledger too (negative movements) and to historic shift variances |
| A pay-idempotency token **must be an INSERT-keyed row**, never a field on the order | A unique index only arbitrates a race when the racers insert distinct rows; two updates of one row to the same value produce no violation. Pinned rule |
| The idempotency `catch` is **index-specific**; every other save failure is rethrown | The anti-swallow discipline |
| **Currency rounding** through `ICurrencyRounding` with explicit `AwayFromZero`, and **never** a 2-decimal assumption; **commercial** fils rounding kept separate, currency precision applied **first** | HM-D19/D27 — EF was truncating money to 2dp and KWD fils were being lost |
| Loyalty balance is **derived**, never stored | Avoids the stored-balance lost update recorded as DEV-2026-005 |
| An item **absent from the branch's pinned price list is rejected at the till** — never converted, never zero | HM-D18 |
| A non-base unit with **no conversion is rejected**, never a silent factor of 1 | Would post wrong quantities to stock and COGS |
| The lane **activity whitelist** is explicit and single-sourced (`IsActivityAllowedForLane`) | A new activity must never leak into a lane by default |
| The lane guard runs **per request**, not only at login | A session minted before the guard, or a direct hit on an inner action, is rejected |
| **Server computes, view displays** | Why the cart survives a disconnect and why totals are correct to three decimals |
| The **three-state cost guard** and the **blank-not-bait** stale-rate rule in reporting | Never show a margin of 0 or 100% for an unposted cost; never show a fabricated rate |
