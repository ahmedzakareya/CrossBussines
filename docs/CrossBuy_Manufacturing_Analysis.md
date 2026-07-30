# CrossBuy — Module 4: Manufacturing — Analysis & Design

> Status: **DESIGN — awaiting decisions before any code** (per the Master Build Spec working rule).
> Builds on the existing inventory/costing/GL engine and the current **instant Assembly** feature.

## 1. What already exists (don't duplicate)
- `Item.IsComposite` + `Item.CompositeType` (`"Assembly"` | `"Kit"`) and `ItemComponent` (BOM lines: ParentItemId, ComponentItemId, Quantity, UoM, SortOrder).
- `StockService.AssembleAsync(assemblyItemId, warehouse, qty, date, disassemble)` — **one-step, instant** build: consumes components at **actual cost** (FEFO for expiry items) via each category's `InventoryAccountId`, produces the assembly into stock, posts a balanced JE. UI: `NewAssembly`/`PostAssembly`.
- Invariants (must NOT break): JournalEntryService = sole GL writer; StockService = sole stock writer; inventory value == GL 1103; transit(110302)==0; trial balance balanced; daily IntegrityCheck.

**Gap = real manufacturing:** planned **Work Orders** with status lifecycle, **WIP** (issue raw → WIP → receive finished from WIP), **labor + overhead** added to product cost, optional **work centers/routing/operations**, production planning, and variances.

## 2. Proposed scope (phased) — 4-1 → 4-6
- **4-1 Bill of Materials (BOM) management** — promote `ItemComponent` to a first-class, versioned BOM: a dedicated BOM editor per manufactured item (components + quantities + scrap %, optional multiple/versioned BOMs, "current" flag). Reuses `ItemComponent` (+ a `BomVersion`/header if versioning chosen).
- **4-2 Work Centers & routing (optional, see Decision A)** — work centers (capacity, cost/hour), routing operations per BOM (sequence, work center, setup/run time). Drives labor/overhead absorption.
- **4-3 Work Orders (core)** — header (manufactured item, qty, BOM, planned dates, warehouse, status: Draft→Released→InProgress→Completed→Closed/Cancelled) + component lines (from BOM, editable) + output. **WIP flow:** issue components → WIP; report production → receive finished goods from WIP. Status-gated, idempotent, period-locked.
- **4-4 Production costing & GL** — see §3 + §4. Roll component + labor + overhead into the finished-good cost; post WIP movements; compute variances.
- **4-5 Production planning (MRP-lite)** — suggest work orders from demand (sales orders / reorder points) vs available stock + on-order; one-click create WOs.
- **4-6 Reports** — WO status board, WIP balance, production cost sheet per WO, material consumption, efficiency/variance.

## 3. Costing approach (Decision B)
- **Actual costing (recommended, matches current engine):** finished-good cost = Σ actual component cost (FIFO/FEFO via StockService) + actual labor + applied overhead, divided by produced qty. No standard/variance accounts needed initially. Consistent with how AssembleAsync + the costing engine already work.
- **Standard costing (alternative):** product carries a standard cost; WO posts at standard and books **material/labor/overhead variances** to variance accounts. More accounts + reconciliation; heavier.

## 4. GL design (new account needed)
- Add **`1104 — إنتاج تحت التشغيل (WIP)`** to the COA (asset, under 11). (1103 = finished/raw inventory.)
- **Issue components to WO:** Dr 1104 WIP / Cr 1103 inventory (component categories) — at actual consumed cost (via StockService, which already clears inventory correctly).
- **Apply labor/overhead (if Decision A includes them):** Dr 1104 WIP / Cr labor-applied (e.g., 510101) & overhead-applied accounts.
- **Receive finished goods:** Dr 1103 inventory (finished-item category) / Cr 1104 WIP — at the rolled-up cost.
- **WO close:** any residual WIP (over/under) → a production-variance account (or write-off) so WIP nets to 0 per closed WO. Invariant: **Σ WIP == GL 1104**; a fully-closed WO leaves 0 WIP.
- All postings go through JournalEntryService; all stock moves through StockService → invariants preserved. New integrity check: `wip_gl` (Σ open-WO WIP == 1104).

## 5. Data model (new tables, manual sqlcmd — migrations broken)
- `ManufBoms` (header) — Id, CompanyID, ItemId, Version, Name, IsCurrent, OutputQty, IsActive… (only if versioning chosen; else keep flat ItemComponent + scrap col).
- `ManufWorkCenters` — Id, CompanyID, Code, Name, CostPerHour, OverheadPerHour, IsActive. *(4-2, optional)*
- `ManufRoutingOps` — Id, BomId/ItemId, Seq, WorkCenterId, SetupMins, RunMinsPerUnit. *(4-2, optional)*
- `ManufWorkOrders` — Id, CompanyID, WoNo, ItemId, BomId, Qty, ProducedQty, WarehouseId, Status, PlannedStart/End, Notes, Owner, CreatedAt + cost roll-up fields (MaterialCost, LaborCost, OverheadCost, UnitCost).
- `ManufWorkOrderComponents` — Id, WorkOrderId, ItemId, PlannedQty, IssuedQty, UoMId, UnitCost.
- `ManufWorkOrderLabor` — Id, WorkOrderId, WorkCenterId, Hours, Rate. *(if labor)*
- (StockMovements/JournalEntries reused — WO references stored on the movement/JE for traceability.)

## 6. Standards followed
Tagify search + server-side lists + table-responsive + confirm-on-edit + select2 item pickers + unified menu (new "التصنيع/Manufacturing" category) + RBAC mirrors InventoryUserRole/InvPerm + dev-test endpoints + Arabic-safe seeding.

## 7. OPEN DECISIONS (need your answer before coding)
- **A. Depth:** material-only WO+WIP / + labor & overhead / + full work-centers & routing.
- **B. Costing:** actual (recommended) vs standard+variances.
- **C. Component consumption:** explicit "issue materials" step vs **backflush** (auto-consume on production report).
- **D. Existing instant Assembly:** keep as the quick path for simple kits AND add Work Orders for real production (recommended), or fold assembly into WOs.
- **E. BOM versioning:** simple single BOM per item (extend ItemComponent) vs versioned BOMs.
