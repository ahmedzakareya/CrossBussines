# Stage 2 — Hypermarket Risk Register

**Assessment output. No mitigation implemented.** Companion to `Stage-002-Hypermarket-Current-State-Assessment.md`.
Additive to `Stage-002-Risk-Register.md` (32 risks) — **no existing risk is edited or renumbered**. Hypermarket risks
are numbered `HR-nn` to keep the two registers distinguishable.

**24 risks.** Severity: **4 CRITICAL** · **8 HIGH** · **9 MEDIUM** · **3 LOW**.

Each risk carries the same thirteen attributes used by the Stage 2 register: evidence · probability · impact · domains ·
current control · mitigation · phase · owner · trigger · acceptance.

Three of the CRITICAL risks are **latent** rather than active — they are created by work the roadmap plans, not by
behaviour running today. They are ranked CRITICAL anyway because a latent risk that is invisible at planning time is the
one that ships.

---

## 1. CRITICAL

### HR-01 — A hyper refund endpoint will post false cash-shortage variances
*Evidence:* `PosSetupService.ExpectedCashAsync:936–946` sums cash payments and cash tips and never subtracts refunds
(comment: "refunds land in RC-6c"); `GetShiftZReportAsync:924` hard-codes `ReturnsTotal = 0m`. `ReturnOrderLinesAsync`
already writes the negative `Receipt` rows that would need subtracting.
*Probability* **Certain, if refunds ship before the fix** · *Impact* High · *Domains* Accounting, POS/Retail, Finance.
*Current control:* accidental only — the hyper lane has no refund route, so the defect is currently unreachable.
*Mitigation:* ship the `ExpectedCash`/`ReturnsTotal` completion in the **same slice** as any hyper refund endpoint
(gap items RN-6 + RN-7 are deliberately one slice). Prove fail-first: construct a shift with a cash refund and assert
the pre-fix expected cash is wrong by exactly the refund amount.
*Phase* Hypermarket H-B · *Owner* Retail Core owner (see HR-04) · *Trigger* any PR adding a return/refund route under
`/hyper/pos`.
*Acceptance:* a refunded shift closes with the correct variance; a zero-variance shift still posts **no** JE; shifts
already closed are **not** recomputed (reverse-never-delete applies to variance JEs).

### HR-02 — Master Data migration can post wrong quantities to stock and COGS
*Evidence:* `ItemBarcode.UoMId` is what makes "1 carton = 144 base"; `UoMConversion.Factor` is **per item**;
`Item.ScaleCode` is unique per company only through a **filtered** index (`UX_Items_Company_ScaleCode`).
`AddLineAsync:545–549` rejects a unit with no conversion; `StockService.ToBaseAsync:229–240` is where the factor is
applied.
*Probability* Medium · *Impact* **CRITICAL** · *Domains* Inventory, Accounting, Retail, Manufacturing.
*Current control:* none specific to these three fields. The existing 2C gate covers stock **valuation** (register R-22),
not unit semantics.
*Mitigation:* add a per-row non-regression assertion for all three to the 2C migration gate — counts and checksums
before and after, not a spot check. Note the failure modes differ: a lost conversion produces a **refused sale**
(safe but a hard outage at every till); a lost `ItemBarcode.UoMId` produces a **silently wrong quantity**; a duplicated
`ScaleCode` sells **the wrong product** with no error anywhere.
*Phase* 2C · *Owner* Master Data owner · *Trigger* any migration touching `Items`, `ItemBarcodes` or `UoMConversions`.
*Acceptance:* per-row equality on all three fields; the filtered index exists and is unique post-migration; a weighted
item sells the correct base quantity in an end-to-end acceptance run.

### HR-03 — Every hyper accounting correction depends on the parallel team's uncommitted kernel
*Evidence:* HM-D53 — `JournalEntryService.ReverseAsync` calls `RecordAsync(JournalEntry.Reversed)` in-transaction,
before commit, with no swallowing catch. `PosOrderService.VoidPaidOrderAsync:1454/1468/1477` reverses invoice, receipt
and tip JEs. So paid-order void, invoice edit, return edit, fiscal-year reopen, FX reverse and manual reversal all fail
completely if `BusinessEvents` is missing or schema-changed. Normal posting is **not** coupled.
*Probability* Medium (and it has already materialised twice as build breaks — HM-D52, HM-D61) · *Impact* **CRITICAL** ·
*Domains* Accounting, Retail, Platform.
*Current control:* `dbset_tables_exist` integrity check now includes `BusinessEvents`/`BusinessEventDispatch` **by
name**, turning an opaque mid-reversal SQL-208 into a red check beforehand. `writer_coupling` counts the injected
dependency by name. The deploy runbook makes the platform slices mandatory and ordered.
*Mitigation:* keep both checks green as a deploy gate; get the parallel work committed so our base stops shifting
(HM-D44 escalation); do not add further coupling to our writers without the owner's pre-coordination.
*Phase* standing · *Owner* platform owner + retail owner jointly · *Trigger* any parallel edit to
`JournalEntryService` or `StockService`; any new platform slice.
*Acceptance:* `dbset_tables_exist` GREEN and `writer_coupling` exactly 1 (the known kernel wiring) before any
environment is allowed to take a correction.

### HR-04 — The Retail Core is consumed by two products and owned by neither
*Evidence:* `PosOrderService` (1957 lines), `PosSetupService` (1128), `PosAccessService` are shared by
`PosAppController` and `HyperPosController`. The consequence is already recorded: HM-10 slice B added an **opt-in**
`activity` filter to `PosController.Dashboard` rather than changing its default, because changing the default would have
altered the restaurant product from the hypermarket track — leaving the activity-mixing defect half-fixed.
*Probability* **Certain** (it has already happened) · *Impact* High · *Domains* Retail, Restaurant POS, Platform.
*Current control:* the two *writers* have a named owner and a pre-coordination rule. The shared **POS services** do not.
*Mitigation:* assign Retail Core ownership (Roadmap Proposal §3.2 recommends the platform owner, with both packs as
consumers). Until then, treat any change to the three shared services as requiring two-owner sign-off.
*Phase* decision needed before H-A · *Owner* **owner decision required** · *Trigger* any shared-service change —
RN-5 (shift-open role check) is one such change and is blocked on this.
*Acceptance:* a named owner recorded; the dashboard activity filter default resolved rather than left opt-in.

---

## 2. HIGH

### HR-05 — Opening a shift requires no role
*Evidence:* `HyperPosController.Start:126–141` — `HyperCtx` + lane guard + terminal-belongs-to-branch, and **no**
`_access` call. `ShiftClose:179` does check `CanSell`. The restaurant twin has the same shape.
*Probability* Certain · *Impact* Medium-High (the opening float is the figure the whole shift reconciles against) ·
*Domains* Retail, Accounting, Security.
*Current control:* none beyond the lane session.
*Mitigation:* require `CanSell` at minimum; consider `IsManager` for the float. Apply to both lanes in one change.
*Phase* H-A · *Owner* Retail Core owner · *Trigger* any lane role-model change.
*Acceptance:* a `pos-waiter`-only session is refused; audit `BranchUserRoles` in any live deployment first, since a
non-cashier role may currently be opening shifts.

### HR-06 — Weighted retail cannot be configured by any user
*Evidence:* `Item.IsWeighted`, `Item.ScaleCode` and `Item.LoyaltyEligible` have no field in `ItemForm.cshtml` (verified
by grep across `Views/`); `PosSetupService.SavePosSettingAsync:257–265` writes 4 of 14 `BranchPosSetting` columns,
omitting `DefaultTaxCodeId`, all six `Scale*` columns, the three delivery columns and `LoyaltyPointsPerCurrencyUnit`.
Only SQL and `DevSeedController` write them.
*Probability* Certain · *Impact* High · *Domains* Retail, Master Data, Operations.
*Current control:* `ItemService` validates all three item fields correctly — and the validation is unreachable.
*Mitigation:* retail tab on the item form (in 2C, where the form is being touched anyway) + hyper branch-setup screen.
*Phase* item fields 2C · branch fields H-A · *Owner* Master Data owner / retail owner · *Trigger* any weighted-item
rollout request.
*Acceptance:* create a weighted item entirely through the UI, print its shelf label, scan a built scale barcode, sell it,
and verify the correct **base** quantity reached stock — a path that today can only be seeded.

### HR-07 — A hyper cashier cannot correct a mistake at the till
*Evidence:* `ReturnOrderLinesAsync`, `VoidPaidOrderAsync`, `VoidOrderAsync` all exist and are accounting-correct;
`HyperPosController` has 18 endpoints and none is a correction.
*Probability* Certain · *Impact* High · *Domains* Retail, Accounting, Operations.
*Current control:* the accounting back-office can reverse — which means a mis-scan becomes a finance ticket.
*Mitigation:* correction panel (RN-6), manager-gated, with the same branch-ownership proof used by the invoice print.
**Bundled with HR-01's fix.**
*Phase* H-B · *Owner* retail owner · *Trigger* first real store rollout.
*Acceptance:* partial return of a weighted line; full return restoring the loyalty balance to zero; void of a
split-tender order; a return on another branch's invoice refused.

### HR-08 — An item without a scannable barcode cannot be sold
*Evidence:* `Scan:201–269` resolves only `Items.Barcode` and `ItemBarcodes` (plus the scale range). No name search, no
PLU. `Item.QuickCode` exists and is read only by the restaurant quick menu.
*Probability* High · *Impact* High (a hard stop at the till) · *Domains* Retail, Operations.
*Current control:* none.
*Mitigation:* PLU entry over the existing `QuickCode` plus a search restricted to the branch's price list, so a searched
item is guaranteed sellable rather than rejected at add-line.
*Phase* H-B · *Owner* retail owner · *Trigger* first store with unlabelled or damaged-label stock.
*Acceptance:* sell by PLU; search offers only price-listed items.

### HR-09 — The lane's page-per-action model cannot meet counter throughput
*Evidence:* `PosLane.cshtml` — five `<form method="post">` blocks, zero fetch/XHR, 20 lines of inline JS. Every scan,
quantity change, removal and payment is a POST + 302 + full document reload.
*Probability* Certain · *Impact* High · *Domains* Retail, UX.
*Current control:* none. `autofocus` makes wedge scanning work on first load and, by luck rather than design, after each
reload.
*Mitigation:* rebuild the lane against the existing services as JSON endpoints; keep every computation server-side.
*Phase* H-B, gated on 2B tokens · *Owner* retail owner + design system owner · *Trigger* any throughput requirement.
*Acceptance:* byte-identical invoices, JEs and movements for the same basket across the old and new surfaces; idempotency
re-verified over the new transport.

### HR-10 — Four capabilities are displayed as enabled with no implementation behind them
*Evidence:* `grep '"SuspendResume"' --include=*.cs` → one hit (a seed array); same for `CashDrawer`, `ExpiryControl`,
`ShelfLabels`. `hyper_presets.sql` seeds `SuspendResume=1` and `CashDrawer=1`; `PosLane.cshtml:183–190` renders them as
green check badges.
*Probability* Certain · *Impact* Medium-High (trust, not data) · *Domains* Retail, UX, Governance.
*Current control:* none. The capability catalogue has no contract test.
*Mitigation:* implement or withdraw each key, and add the structural test that every catalogue key has a consumer and
every preset key is in a catalogue.
*Phase* H-A · *Owner* retail owner · *Trigger* any new capability key.
*Acceptance:* the structural test passes with the catalogue, the preset SQL and the consumers in agreement.

### HR-11 — No automated test protects any hypermarket business behaviour
*Evidence:* 466 xUnit facts; exactly two touch hyper and both are **structural** (they read source text). All behaviour
is verified by 12 `[DevOnly]` HTTP acceptance endpoints that need a populated SQL Server, a cookie jar (HM-D59),
Development environment and a seeded `Employee` session blob (HM-D58) — so they cannot run in CI and nothing fails a
build when a hyper behaviour breaks.
*Probability* Certain · *Impact* High · *Domains* Retail, Testing, Governance.
*Current control:* the acceptance endpoints are genuinely rigorous — every number re-read from a new DB query,
idempotency asserted over three runs, `failedCount 0` required. They are just not automated.
*Mitigation:* port the highest-value invariants to xUnit against the in-memory SQLite host already used by the platform
tests (real transactions): pricing resolution order, promotion ranking, unit-conversion rejection, scale-barcode parse
and reject cases, `PriceRound` order, `ExpectedCash`, and the idempotency race. **Do not count a dev endpoint as
coverage.**
*Phase* 2A guardrails, extended in H-A · *Owner* engineering platform owner · *Trigger* the first hyper regression that
reaches a store.
*Acceptance:* a named minimum set green in CI with SQL tests enabled and **zero skipped**.

### HR-12 — Dev-tuned integrity baselines will misclassify on production
*Evidence:* `IntegrityCheckService` carries compiled constants — `JvBaselineMaxNo=1162`, `PosOrderBaselineMaxId=3352`,
`unbatched_inbound_tracked=19`, `cogs_impact newNoCogs=2`, `PurchaseModelCutoffUtc`. The deploy runbook states plainly
that production has its own legacy boundary and these will misclassify legacy-vs-new for the gap, duplicate and
no-COGS checks.
*Probability* Certain on first production run · *Impact* Medium-High (a false green is worse than a red) ·
*Domains* Governance, Accounting, Retail.
*Current control:* documented in the runbook; the structural checks (`stock_gl`, `ar_sub`, `ap_sub`, `tb_balanced`,
`writer_coupling`, `dbset_tables_exist`) are baseline-free and valid immediately.
*Mitigation:* capture production's baselines at the first integrity run and re-tune per environment; better, move the
constants to configuration so they stop being a compile-time property of the binary.
*Phase* before any production go-live · *Owner* deployment owner · *Trigger* first production integrity run.
*Acceptance:* environment-specific baselines recorded; `failedCount` trusted only after re-tuning.

---

## 3. MEDIUM

### HR-13 — `OfficialInvoice` has no production enable path
*Evidence:* only `DevSeedController:1397–1399` writes the capability; absent from both catalogues and from
`hyper_presets.sql`; read by `HyperPosController:336,374`.
*Probability* Certain · *Impact* Medium (availability, not exposure) · *Domains* Retail, Accounting.
*Current control:* none. *Mitigation:* add to the catalogue, the preset (default OFF) and the setup screen.
*Phase* H-A · *Owner* retail owner · *Trigger* first business-customer invoice request.
*Acceptance:* enableable and disableable through the UI; the tax-zero-only stamp rule still enforced.

### HR-14 — Hyper configuration is only reachable through the Restaurant system
*Evidence:* `Views/Pos/Setup.cshtml` is the sole writer of `Branch.ActivityPresetCode`, renders
`MainMenu.Restaurant()`, and `SaveCapabilities:207–213` builds its dictionary from the **7 restaurant keys**. Sourcing
and production screens are likewise on `PosController`.
*Probability* Certain · *Impact* Medium · *Domains* Retail, UX, Operations.
*Current control:* `SaveCapabilitiesAsync` **upserts** per key, so hyper rows survive a restaurant-screen save —
this is a *cannot-edit*, not a *data-loss*, defect.
*Mitigation:* hyper setup screen (H-A); move sourcing/production out of the restaurant sidebar (H-C).
*Phase* H-A then H-C · *Owner* retail owner · *Trigger* first non-technical hypermarket administrator.
*Acceptance:* a hyper branch fully configurable from `MainMenu.Hyper()` alone.

### HR-15 — Two working screens are reachable only by URL
*Evidence:* `grep "BulkPriceChange\|ShelfLabels" Models/Menu/MainMenu.cs Views/` → no matches.
*Probability* Certain · *Impact* Medium · *Domains* Retail, Pricing, UX, Governance.
*Current control:* the three bulk mutating actions carry `[InvPerm("doc")]`; unlinked was never protection.
*Mitigation:* menu entries; ideally the consolidated Pricing workspace; `[InvPerm("read")]` on the GET shells for
consistency; a navigation reachability test so the class of defect stops recurring.
*Phase* H-A · *Owner* retail owner · *Trigger* any new top-level view.
*Acceptance:* every action rendering a top-level view is menu-reachable.

### HR-16 — An amount promotion is not unit-aware
*Evidence:* HM-D51 — `Promotion` has no `UoMId`, so a fixed 0.100 discount is 20% off a 0.500 piece and 0.17% off a
60.000 carton.
*Probability* High once multi-unit promotions are used · *Impact* Medium (margin) · *Domains* Pricing, Retail.
*Current control:* percent promotions are unaffected; the sell-time net-≤-0 guard prevents the pathological case only.
*Mitigation:* short term, warn in the promotion editor when `Amount` is chosen for a multi-unit item (the recorded UI
mitigation); long term, add the dimension in Pricing & Promotions.
*Phase* Pricing & Promotions · *Owner* pricing owner · *Trigger* first amount promotion on a multi-unit item.
*Acceptance:* the editor warns; the resolved shelf price is previewable per unit.

### HR-17 — Discount and promotion performance are unmeasurable
*Evidence:* the winning promotion is folded into a single effective `DiscountPercent`; `PriceResult.PromotionId` is
computed and **never persisted** on the order line.
*Probability* Certain · *Impact* Medium · *Domains* Reporting, Pricing.
*Current control:* none. *Mitigation:* persist the applied promotion id per line — a capture-at-source item that must
sit in the Hypermarket pack even though the reports belong to Stage 9.
*Phase* H-A (capture) / Stage 9 (report) · *Owner* retail owner then reporting owner · *Trigger* first promotion-ROI
question.
*Acceptance:* a promotion's applied lines are queryable.

### HR-18 — Refused sales leave no trace, so stock-outs are unmeasurable
*Evidence:* a sale refused by the negative-stock guard (`PostMovementAsync:502`) or by price-list absence
(`AddLineAsync:596`) rolls back with a message and no record.
*Probability* Certain · *Impact* Medium · *Domains* Reporting, Inventory, Retail.
*Current control:* none. *Mitigation:* record refused-sale reasons. Highest-value new data point in the reporting table.
*Phase* H-A (capture) · *Owner* retail owner · *Trigger* first availability question from a store.
*Acceptance:* refusals queryable by reason, item and branch.

### HR-19 — One employee, one branch
*Evidence:* `PosAccessService.ResolveByUserIdAsync:112` resolves the branch from `Employee.BranchID`; there is no branch
picker at login and no multi-branch role resolution.
*Probability* High in a chain · *Impact* Medium · *Domains* Retail, HR, Security.
*Current control:* the model is honest about it — `BranchUserRole` is per (branch, employee), so the data could support
multiple; only the resolver assumes one.
*Mitigation:* branch selection at login among the employee's authorised branches. Note this interacts with the
cross-company login guard, which must continue to refuse a branch of another company.
*Phase* H-A or H-B · *Owner* retail owner + HR owner · *Trigger* first cashier working two stores.
*Acceptance:* a two-branch cashier can select a branch and cannot select an unauthorised one.

### HR-20 — Branch is not a stock dimension
*Evidence:* `StockBalance` is keyed `(CompanyID, ItemId, WarehouseId)`; a branch maps to exactly one warehouse via
`BranchPosSetting.DefaultSalesWarehouseId`. `BinLocation`/`BinStock` hold quantity only and are **unused by the sale
path** (`SalesLineInput` has no bin field).
*Probability* Medium · *Impact* Medium · *Domains* Inventory, WMS, Retail.
*Current control:* the 1:1 mapping is adequate for single-warehouse stores.
*Mitigation:* do **not** change it inside the Hypermarket pack. Shelf-vs-backroom and store replenishment belong to WMS
(Stage 4), which is where the bin dimension gets its first real consumer.
*Phase* Stage 4 · *Owner* WMS owner · *Trigger* first store needing a backroom balance.
*Acceptance:* a WMS design that consumes the existing bin layer rather than adding a parallel one.

### HR-21 — Changing a branch's sales warehouse mid-operation silently moves where stock is deducted
*Evidence:* `SavePosSettingAsync` writes `DefaultSalesWarehouseId` with no guard; `PayAsync:1117–1118` reads it at
settlement and **fails the sale if it is null**.
*Probability* Low-Medium · *Impact* Medium · *Domains* Inventory, Retail.
*Current control:* the null case fails loudly; a *changed* case does not.
*Mitigation:* block the change while an open shift or open order exists on the branch, mirroring the existing
terminal-delete guard ("cannot delete a device with an open shift").
*Phase* H-A (part of the setup screen) · *Owner* retail owner · *Trigger* any warehouse reorganisation.
*Acceptance:* the change is refused with a clear message while a shift is open.

---

## 4. LOW

### HR-22 — Barcode resolution is duplicated inside one controller, in two different forms
*Evidence:* `HyperPosController.Scan:245–268` **rejects** a barcode registered on more than one item;
`PriceCheck:520–534` takes the **first** match. Same input, two behaviours.
*Probability* Low · *Impact* Low (price check is read-only) · *Domains* Retail.
*Current control:* the divergence is only visible on already-corrupt data, which the
`barcode_cross_table_dup` integrity classification surfaces.
*Mitigation:* extract one resolver used by both. Natural part of the Retail Core extraction.
*Phase* H-C · *Owner* Retail Core owner · *Trigger* any third consumer of barcode resolution.
*Acceptance:* one resolver, one behaviour, one test.

### HR-23 — Fixed product barcodes exist inside the GS1 reserved in-store range
*Evidence:* HM-D42 — four items carry `2001000000017/24/31/48`, in both `Items.Barcode` and `ItemBarcodes`. GS1 reserves
`2…` for in-store variable-measure.
*Probability* Low (branch 17 does not carry these items) · *Impact* Low-Medium if a branch later adds a range covering
them · *Domains* Retail, Master Data.
*Current control:* the save guard blocks **new** ones company-wide across any branch's configured prefix; the
`fixed_barcode_in_scale_range` classification counts the four; and a scan of an affected code inside a configured range
is **explicitly rejected** as a data error rather than mis-sold.
*Mitigation:* renumber the four as data cleanup.
*Phase* 2C data cleanup · *Owner* Master Data owner · *Trigger* adding a scale range at any branch.
*Acceptance:* the classification reaches zero.

### HR-24 — Stale header comments describe the module as a non-selling scaffold
*Evidence:* `HyperController.cs:12` and `HyperPosController.cs:19` still read "HM-0 ships ONE screen… No selling." /
"HM-0 has NO selling: no cart, no scan, no order line." Ten phases out of date.
*Probability* Certain · *Impact* Low, but it is real — this assessment had to correct three circulating assumptions,
and two of them trace to these comments.
*Current control:* none. *Mitigation:* update the file headers when the files are next touched.
*Phase* H-A · *Owner* retail owner · *Trigger* any onboarding of a new reader.
*Acceptance:* the header describes the current capability set.

---

## 5. Risks explicitly NOT raised, and why

Named so a later reader does not assume they were missed.

| Considered | Not raised because |
|---|---|
| "The hyper sale might post incorrect GL" | Traced account by account; the posting is correct, and the acceptance endpoints re-read every figure from the database. The engine is the module's strongest part |
| "Pay might double-charge" | Threefold protection verified: in-transaction unique INSERT on `HyperPayTokens`, the `Status != "Open"` guard, and Post-Redirect-Get. The `catch` is index-specific and rethrows everything else |
| "KWD fils might be lost to rounding" | Resolved across HM-2 (HM-D19, HM-D27): `ICurrencyRounding` with explicit `AwayFromZero`, EF decimal precision pinned, and a commercial fils step kept deliberately separate with currency precision applied first |
| "FEFO might pick an expired batch" | `FefoAllocateAsync` excludes expired lots, splits across nearest-expiry, and returns a clear message when short. A named expired batch is blocked, and an expired write-off must name its batch so FEFO cannot auto-pick it |
| "The storefront might leak other companies' data" | Reads run inside `BeginPublicCatalogRead`, pinned to configured `Store:StoreCompanyId`, with `AllowsCrossCompany == false` by construction — deliberately **not** the administrative bypass. Ids are encrypted tokens |
| "Offline sync might double-post" | `PosSyncLog(LocalGuid)` unique, checked **inside** the transaction, with the whole replay in one ambient transaction. It is a **restaurant** mechanism and, separately, has never run on a real path — recorded in the assessment as a claim not to make, rather than as a hyper risk |
| "Dev seed endpoints might run in production" | `[DevOnly]` returns 404 outside Development, and the runbook forbids Development on production. This is the single control, and it is adequate — but it is the reason HR-12 matters: the *baselines* those endpoints tuned are compiled into the shipping binary |
