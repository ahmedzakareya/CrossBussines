# HYPER-DEPLOY-RUNBOOK

Documentation only. The hypermarket track's acceptance is all on **CrossBuyDB2 (dev)**; the DEPLOY itself is unproven — ~a
dozen individually-idempotent SQL scripts were built across phases and **no one has verified the full ordered set lands clean
on a production DB**. This runbook is the ordered procedure. It executes nothing.

**Two hard rules first (from CLAUDE.md):**
- `Migrations/` is a **DEAD snapshot** — **NEVER run an EF migration against production.** Apply ONLY the idempotent
  `deploy/sql/*.sql` scripts below.
- Every script here is **idempotent** (guarded `IF NOT EXISTS` / `COL_LENGTH … IS NULL`), so re-running is safe. Re-running the
  whole set must be a no-op the second time.

---

## 1) Ordered script list (mandatory execution order)

Legend: **[schema]** = tables/columns · **[ref]** = reference/seed data · **[ours]** / **[platform]** = owner.

### Stage 0 — PRECONDITION (must already exist on the target DB; NOT in this list)
The core CrossBuy schema created once at first stand-up (chart of accounts incl. **1102** AR-control, **1103** inventory,
**110101** cash/drawer, **210201** VAT-output, **210203** GRNI, **4101** revenue, **510101** COGS; Companies, Branches,
Currencies, Items, ItemCategories, Warehouses, StockBalances, StockMovements, SalesInvoices, Receipts, JournalEntries,
AccountingSettings, TaxCodes, PriceLists, Employee, Users). A production DB already has this. Do not recreate it from a migration.

### Stage 1 — PLATFORM SLICES  [platform] — REQUIRED, they gate OUR GL path (HM-D53/D57)
Our GL writer `JournalEntryService.ReverseAsync` calls the kernel's `RecordAsync` in-transaction with no swallow (HM-D53), and
`RecordAsync` writes `BusinessEvents` + `Notifications.EntityType/EntityId`. If these are missing, **every sale/purchase/reversal
fails** (SQL-208 no `BusinessEvents`; SQL-207 no `Notifications.EntityId`).
1. `platform_business_events.sql`            [schema] — BusinessEvents + BusinessEventDispatch tables.
2. `platform_business_events_slice_002.sql`  [schema] — Notifications.EntityType/EntityId (+ a filtered index). **Apply with
   `sqlcmd -I`** (QUOTED_IDENTIFIER ON) so the filtered index builds.
   → **Dependency: slice-1 BEFORE slice-2, BOTH before ANY POS sale or reversal.** Re-review the `platform_business_events*`
   family before EVERY deploy — the kernel keeps adding slices (HM-D57); a slice can add a COLUMN to an existing table, not just
   a table. (Also present but NOT gating our path — apply per the full-platform deploy, safe to lag for a hyper-only branch:
   `comm_outbox_slice_003.sql`, `platform_schema_history.sql`, `platform_role_assignments.sql`.)

### Stage 2 — POS FOUNDATION  [ours/shared] — the hyper lane reuses PosOrderService and its tables
3. `pos_schema_master.sql`               [schema] — POS master schema.
4. `pos_setup.sql`                       [schema+ref] — ActivityPresets, capability catalog, **BranchPosSettings**, dining/kitchen.
5. `pos_terminals.sql`                   [schema] — PosTerminals + PosShifts + PosOrder linkage + ReceiptNo. (needs pos_setup)
6. `pos_order.sql`                       [schema] — cashier order → pay → invoice + stock + GL. (needs pos_terminals)
7. `pos_payment_roles.sql`               [schema+ref] — per-branch payment methods (→ GL account) + POS roles. (needs pos_setup)
8. `pos_branch_item_sourcing_bis1.sql`   [schema] — per-branch item sourcing (drives AppendSaleLines routing at pay).
9. `pos_shift_close_rc6a.sql`            [schema] — shift close + cash over/short (520111) + the Z-report columns.
10. `pos_void_rc6c.sql`                  [schema] — payment↔receipt link (paid-order void; the HM-9 loyalty reversal rides it).
    → Restaurant-only, **OPTIONAL for a hyper-only branch** (part of the full-platform deploy): `pos_modifiers*`,
    `pos_quickmenu`, `pos_kds*`, `pos_reservations_b1`, `pos_delivery_c1/c2/c3`, `pos_hold_recall`, `pos_merge_tables`,
    `pos_send_kitchen`, `pos_order_guests`, `pos_dining_area_nameen`, `pos_station_nameen`, `pos_sync_log_9d`,
    `pos_sync_conflict_9e` (the restaurant OFFLINE layer — the hyper lane is online-only; see HM-10 slice A / HM-D24).

### Stage 3 — HYPER PHASE SCRIPTS  [ours]
11. `hyper_presets.sql`          [ref] — the **Hyper** ActivityPreset + default capabilities (CustomerIdentity ON · Loyalty OFF ·
    advanced OFF). (needs pos_setup)
12. `hm2_multiunit.sql`          [schema] — multi-barcode + multi-unit columns.
13. `hm3_weighted.sql`           [schema] — weighted + scale-barcode columns/config.
14. `hm4_pricing.sql`            [schema] — pricing management + shelf labels + price-check.
15. `hm7_count_batch.sql`        [schema] — batch-aware physical-count columns.
16. `hm8_official_invoice.sql`   [schema] — SalesInvoices override columns (needs SalesInvoices). (Companies.CompanyImage reused as logo — no new column.)
17. `hm9_loyalty_earn.sql`       [schema] — PointsMovements + rate/eligibility columns (needs Companies, BranchPosSettings, ItemCategories, Items).
18. `hm10_pay_idempotency.sql`   [schema] — HyperPayTokens + the unique index (needs the base; a new table cannot have a pre-existing duplicate).
19. `inventory_reconcile_log.sql`[schema] — inv-reconcile audit trail (HM-D6).

### Stage 4 — ACCOUNT RENAMES / RELABELS  [ours] — need the accounts to exist first
20. `hm16_rename_grni.sql`                    [ref] — 210203 → "Goods Received Not Invoiced (GRNI)". (needs account 210203)
21. `hm_d39_rename_cogs.sql`                  [ref] — 510101 → COGS. (needs account 510101)
22. `hm_d34_relabel_shell_company_refs.sql`   [ref] — relabel entities mislabeled onto shell companies (data-quality; optional).

### Stage 5 — SETTINGS  [ours]
23. `hm2_d23_rate_staleness.sql`   [schema+ref] — AccountingSettings.RateMaxAgeDays/RateStaleBehavior (HM-D23). (needs AccountingSettings)
24. `hm_d38_branch_tax.sql`        [schema] — BranchPosSettings.DefaultTaxCodeId (branch-driven tax). (needs BranchPosSettings + TaxCodes)
25. `hm_d18_hyper_kwd_pricelist.sql`[ref] — a **MINIMAL DEMO** KWD price list. **NOT a production price source** — production
    uses the real price-list DATA (see §3); run this only on a demo/UAT DB.

---

## 2) Governing dependencies (explicit, not assumed)
- **Platform slice-1 → slice-2 → any sale/reversal.** Out of order or missing ⇒ SQL-208 / SQL-207. This is the single most
  important ordering constraint (HM-D53/D57).
- **pos_setup → pos_terminals → pos_order** (each references the prior's tables). **pos_setup → hyper_presets** (ActivityPresets).
  **pos_setup → hm_d38_branch_tax** (BranchPosSettings). **pos_payment_roles → pay** (a cash method → GL account is required or
  `PayAsync` returns "no cash account").
- **Accounts exist → hm16_rename_grni / hm_d39_rename_cogs** (they UPDATE 210203 / 510101 by code; a missing account is a silent
  no-op, so the rename must run after the account seed).
- **SalesInvoices exist → hm8** · **Companies/BranchPosSettings/ItemCategories/Items exist → hm9** · **AccountingSettings exist →
  hm2_d23**. All additive `COL_LENGTH … IS NULL` guards, so a missing base table is the only failure mode.
- **hm10_pay_idempotency** is independent (new table + index).
- The scripts are otherwise order-independent WITHIN a stage; the stage order above is the safe total order.

---

## 3) Required DATA (not scripts) — a hyper branch does not sell without ALL of these
Per hyper branch:
- **Branch currency** = KWD (BranchPosSettings.DefaultCurrencyId) — else the document currency is undefined.
- **Today's exchange rate** for KWD (HM-D23): a sale looks up the rate; if stale/missing the sale is rejected or warns per
  `RateStaleBehavior`, and the report's indicative KWD-margin column is left BLANK (never a bait rate).
- **A price list IN the branch currency (KWD)** with EVERY sold item priced (HM-D18): an item ABSENT from the list is
  **REJECTED at the till** (never priced at zero, never converted from `Item.SalesPrice`). BranchPosSettings.DefaultPriceListId
  must point at it.
- **ActivityPreset = "Hyper"** on the branch + its capabilities enabled (BranchCapabilities): CustomerIdentity (identity),
  Weight/BarcodeMulti/… as needed. **Loyalty stays OFF** until redemption (HM-9 slice 3) ships.
- **≥1 PosTerminal** with a ReceiptPrefix + NextReceiptNo, and **an open shift** to take payment.
- **≥1 cash BranchPaymentMethod → a GL cash account** (drawer 110101 or the terminal's CashAccountId).
- **Category GL accounts** on EVERY ItemCategory sold: Inventory (1103), COGS (510101), Revenue account per line (4101), GRNI
  (210203). A category with no InventoryAccountId cannot be opening-stocked or sold (COGS fails).
- **Opening stock** for every sold item at the branch's DefaultSalesWarehouse.
- **Loyalty points rate** (only if Loyalty is enabled): Companies.LoyaltyPointsPerCurrencyUnit (company default) and/or
  BranchPosSetting.LoyaltyPointsPerCurrencyUnit (branch override). Null everywhere ⇒ 0 points.

---

## 4) Post-deploy checklist
- **Run inv-test-integrity once** → **failedCount MUST be 0.** The 16 checks and what each proves:
  `stock_gl` (inventory value == 1103, structural/baseline — excluded from failedCount) · `ar_sub` · `ap_sub` (subledgers
  reconcile to GL) · `bal_qty_vs_moves` · `bal_value_diff` (balance == Σ movements) · `batch_no_negative` ·
  `unbatched_inbound_tracked` · `doc_je_status_mismatch` (document status == its JE) · `dbset_tables_exist` · `writer_coupling`
  (the two floor guards) · `open_grni_receipts` · `batches_created_in_count` · `barcode_cross_table_dup` · `receiptno_dup` ·
  `cogs_impact` · `tb_balanced` (trial balance).
- **Build config**: deploy a **Release** build (NOT `TestRun`/`Debug`). `TestRun` = "Debug in another folder" and DEFINES DEBUG
  (`CrossBuy.csproj`) — it must never be the production binary; the `#if DEBUG` test seams would compile in.
- **Environment**: `ASPNETCORE_ENVIRONMENT=Production` — this 404s the entire `[DevOnly]` `DevSeedController` (all seed/accept
  endpoints). Never set Development on production.
- **dbContextLifetime = Scoped** (Program.cs `AddDbContext(…, ServiceLifetime.Scoped)`) + the CompanyWriteGuardInterceptor
  Singleton. Do not change these lifetimes.
- **Frozen baselines are DEV numbers** — compiled into `IntegrityCheckService`: `JvBaselineMaxNo=1162` (254 legacy JV gaps),
  `PosOrderBaselineMaxId=3352` (legacy ReceiptNo dups), `unbatched_inbound_tracked=19`, `cogs_impact newNoCogs=2`,
  `PurchaseModelCutoffUtc`. **Production has its OWN legacy boundary**, so on production these dev constants will misclassify
  legacy-vs-new for the gap / dup / no-COGS checks. **Capture production's baselines at the FIRST integrity run** (record its
  legacy max JV number, max PosOrder id, and the counts of pre-existing no-COGS / dup rows) and re-tune the constants for the
  production line before trusting failedCount on those three checks. The structural checks (stock_gl, ar_sub, ap_sub, tb_balanced,
  writer_coupling, dbset) are baseline-free and valid immediately.

---

## 5) What NOT to deploy
- **All dev seed/accept endpoints**: `DevSeedController` is `[DevOnly]` → 404 outside Development. Never enable Development on
  prod. Do not port `hyper-hm0-seed`, `hm1-seed`, any `hm*-accept`, or the dev loyalty/points test data.
- **ZZ-* entities** (ZZ items/customers/categories/terminals/promos) — created only by dev endpoints; they never exist on a
  clean prod DB and must not be seeded.
- **The `#if DEBUG` seams** (`StockService._testBypassLandedLockRead`, etc.) — excluded from a non-DEBUG Release build. Do NOT
  deploy a Debug/TestRun binary that includes them.
- **`Numbering:JvAllocationMode`** config override — a Debug-only measurement seam. Leave it UNSET (default) in production.
- **`hm_d18_hyper_kwd_pricelist.sql`** as a real price source (§1 stage 5) — a demo seed only.

---

## 6) Known deploy risks
- **Dead migration snapshot** — do NOT `dotnet ef database update` against production; the `Migrations/` folder does not reflect
  reality. Idempotent `deploy/sql` scripts ONLY.
- **Unapplied platform slices** ⇒ the sale/purchase/reversal path fails (SQL-208/207). Verify `BusinessEvents` +
  `Notifications.EntityId` exist BEFORE the first sale. This gate is the parallel team's schema but OUR path depends on it (D53).
- **The parallel boot-break (HM-D61) is UNCOMMITTED WIP** — `Program.cs` in the working tree registers
  `PermissionScopeStartupValidator` (a singleton consuming a scoped service) that crashes startup; it is NOT in any of our
  commits. **Deploy from committed HEAD, not the working tree.** If the parallel team's `Program.cs` is what ships, the app will
  not boot until they fix that registration (singleton → IServiceScopeFactory).
- **Customer 1025 (ControlAccountId=0)** is a DEV-DB anomaly; a production DB may carry its own customers with a zero/invalid AR
  control account. The HM-9 slice-1 fail-closed guard correctly REFUSES linking such a customer to an order (it never posts a
  receivable to a non-existent account) — but audit for such rows before enabling CustomerIdentity broadly.
- **Group (د) — the shared kernel coupling**: our `ReverseAsync` hard-depends on the kernel (D53 → the slices are mandatory);
  `RecordAsync` needs a signed-in BusinessContext (D58 → interactive prod requests carry it; any NEW background path that emits
  an event would throw — monitor); the parallel `CompanyScopeMiddleware` + query filters return a silent EMPTY grid on an
  unresolved scope (D59/D60), so verify the company scope resolves for every production role before go-live.
- **Apply slice-2 with `-I`** (QUOTED_IDENTIFIER ON) or its filtered index silently fails to build.

---
*This runbook is documentation. It ran nothing and touched no database. Update it whenever a new `deploy/sql` script or platform
slice is added — the ordered list and the platform-slice gate must never fall out of date.*
