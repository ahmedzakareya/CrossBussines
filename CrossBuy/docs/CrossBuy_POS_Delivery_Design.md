# CrossBuy — POS-C (Delivery) Design & Build

**Status:** design approved 2026-07-08. Builds on OrderType=Delivery + customers (POS-4e) + RC-3 states/real-time. Fixed rule: open order = ZERO accounting; invoice (incl. delivery fee) + stock + GL at PAYMENT ONLY via the existing ReceivableService/StockService/JournalEntryService — NO new GL writer. Delivery status is operational. Invariants green (inv-test-integrity=2). Offline-ready.

## Approved decisions
1. **Address = customer master + order snapshot (option C).** `CustomerAddress` (multi per customer: area/address/phone/isDefault) is reusable master data; the chosen address/area/phone + fee are FROZEN onto the order at order time (address may change on the customer later).
2. **Delivery fee = per-zone (with branch default fallback), billed like the service charge.** Frozen on the order (`PosOrder.DeliveryFee`). At pay it becomes a "رسوم توصيل" invoice line mapped to `BranchPosSetting.DeliveryRevenueAccountId` (fallback = sales revenue), taxed unless `DeliveryTaxExempt`. Flows through the existing sales-invoice path — NO new GL writer.
3. **Driver = per-branch** simple entity, assigned to the order (POS-C2).
4. **Delivery status = extension of RC-3** on the order: Ready → OutForDelivery → Delivered, forward-only, role-gated, broadcast via the existing PosHub. Independent of payment (COD: deliver operationally; pay creates the invoice) (POS-C3).
5. **Dedicated /pos/delivery board** (separate from hall/takeaway) (POS-C4).

## Phases (small → verify → STOP)
- **POS-C1 — delivery data + fee** ✅ DONE (2026-07-08)
- **POS-C2 — driver** ✅ DONE (2026-07-08)
- **POS-C3 — delivery status + real-time** ✅ DONE (2026-07-08)
- **POS-C4 — delivery board** ✅ DONE (2026-07-08) → **POS-C COMPLETE**

## POS-C1 DONE (2026-07-08) — data + fee, operational, inv=2
- Models: `CustomerAddress {CompanyId,CustomerId,DeliveryZoneId?,Area,Address,Phone,IsDefault,IsActive}`; `DeliveryZone {BranchId,Name,NameEn?,Fee,IsActive}`; `PosOrder` +`DeliveryZoneId/DeliveryAddress/DeliveryArea/DeliveryPhone/DeliveryFee`; `BranchPosSetting` +`DefaultDeliveryFee/DeliveryRevenueAccountId/DeliveryTaxExempt`. DbSets added. SQL `deploy/sql/pos_delivery_c1.sql` (idempotent tables+columns + seed zones/branch-15 default), applied.
- Service (`PosOrderService`): `SetOrderDeliveryAsync` (freeze address/area/phone + fee = zone.Fee ?? branch default; only for OrderType=Delivery; recompute; no GL); `GetDeliveryZonesAsync`; `GetCustomerAddressesAsync`; `AddCustomerAddressAsync` (unsets other defaults). `RecomputeAsync` now adds `DeliveryFee` (+VAT unless exempt) to the order total. `PayAsync` + `PaySplitByItemAsync` add a "رسوم توصيل" line (delivery account, taxed/exempt) — split bills it once on the first invoice and excludes it from the residual.
- Endpoints (`PosAppController`, CanOrder-gated): `POST /pos/order/delivery`, `GET /pos/delivery/zones`, `GET /pos/customer/addresses`, `POST /pos/customer/address`. Middleware allowlist widened (`/pos/customer`, `/pos/delivery`). `PosOrderDto` exposes the delivery fields.
- Verified: `pos-c1-test` 12/12 allPass (address CRUD; fee frozen=zone fee; area frozen; order total grew by fee+VAT; ZERO GL while open; TB unchanged pre-pay; pay ok; invoice total == order total incl. fee; AR nets zero; TB balanced). inv-test-integrity=2.
- NOT in C1: the cashier delivery-entry UI (endpoints ready; entry wired later / with the board).

## POS-C2 DONE (2026-07-08) — driver, operational, inv=2
- Model: `Driver {BranchId, Name, Phone, IsActive}` (per-branch) + `PosOrder.DriverId` (nullable). DbSet added. SQL `deploy/sql/pos_delivery_c2.sql` (table + column + seed 2 drivers for branch 15), applied.
- Setup (`PosSetupService`): `GetDriversAsync`, `SaveDriverAsync`, `DeleteDriverAsync` (delete keeps history → deactivates if referenced by any order). Screen `Views/Pos/Drivers.cshtml` (PosController `Drivers`/`SaveDriver`/`DeleteDriver`, `_LayoutBackend`, branch picker) + menu link in `MainMenu.Restaurant()` Setup.
- Assign (`PosOrderService.AssignDriverAsync`): sets/clears `PosOrder.DriverId` on an OPEN Delivery order only; rejects a driver from another branch / inactive; operational (no GL). `PosOrderDto` exposes `DriverId`+`DriverName`. Endpoints (`PosAppController`, CanOrder): `POST /pos/order/driver`, `GET /pos/drivers` (active only).
- Verified: `pos-c2-test` 7/7 (assign, carries id, shows name, reject other-branch driver, clear/unassign, reject on non-delivery order, ZERO GL); C1 regression allPass; inv-test-integrity=2; `/Pos/Drivers` renders (302 to admin login, no view error).
- NOT in C2: the assign-driver UI on an order (comes with the delivery board C4).

## POS-C3 DONE (2026-07-08) — delivery status + real-time, operational, inv=2
- Model: `PosOrder.DeliveryStatus` (nullable): null → OutForDelivery → Delivered. SQL `deploy/sql/pos_delivery_c3.sql` (column), applied. `PosOrderDto.DeliveryStatus` exposed.
- Service `SetDeliveryStatusAsync(company,order,status)`: open Delivery order only; forward-only via `DeliveryFlow` (no back/repeat); **OutForDelivery requires the kitchen to be Ready** (`DeriveOrderKds == "Ready"`); operational (no GL). Independent of payment — Delivered ≠ Paid (order stays Open until pay).
- Endpoint `POST /pos/order/delivery-status` (CanOrder-gated) → broadcasts on the SAME `PosHub`: `OrderOutForDelivery` / `OrderDelivered` (new events, no new hub) via the RC-3c `PosBroadcast` helper.
- Verified: `pos-c3-test` 11/11 (reject-before-Ready, OutForDelivery after Ready, forward-only reject repeat/back, Delivered, reject invalid, **Delivered-but-still-Open COD separation**, role matrix cashier/manager yes + kitchen-only no, ZERO GL). Regressions c1/c2/rc3a allPass; inv-test-integrity=2. (Broadcast uses the RC-3c-verified PosHub path; the socket push itself is the same proven infrastructure.)
- NOT in C3: the delivery board UI that shows/advances these statuses (that's C4).

## POS-C4 DONE (2026-07-08) — delivery board, operational, inv=2 → POS-C COMPLETE
- Feed: `PosOrderService.GetDeliveryOrdersAsync(company,branch)` → open Delivery orders with customer, frozen area/address/phone, driver, `DeliveryStatus`, derived `KitchenStatus`, elapsed, item count, total, fee (`DeliveryOrderDto`).
- Screen `Views/PosApp/Delivery.cshtml` + `wwwroot/Backend-assets/js/pos-delivery.js` — PURE Metronic under `_LayoutAccounting` (Restaurant sidebar), RTL + Cairo, projects-style cards. Controller `GET /pos/delivery` (view, CanOrder, seeds Session["Employee"], injects active drivers) + `GET /pos/delivery/orders` (feed). Menu link in `MainMenu.Restaurant()` Operations.
- Actions on the board (reuse C2/C3 endpoints): assign driver = Metronic dropdown of active drivers → `POST /pos/order/driver`; advance status = "خرج للتوصيل" (enabled only when kitchen Ready, else disabled "لم يجهز بعد") / "تُسلّم" → `POST /pos/order/delivery-status`; each CONFIRMED (CB.confirm) then toast. Delivered card shows «تم التسليم» + «بانتظار الدفع». Real-time: subscribes to OrderSentToKitchen/LineKdsStatusChanged/OrderReady/OrderOutForDelivery/OrderDelivered/OrderPaid on the shared PosHub → re-fetch; adaptive poll fallback.
- **Bug fixed:** `/pos/drivers` was missing from the SessionValidationMiddleware cashier allowlist (302'd) — added.
- Verified: `pos-c4-test` 8/8 (delivery order on board, frozen area, driver name, DeliveryStatus, KitchenStatus, item count, takeaway excluded, ZERO GL); regressions c1/c2/c3 allPass; inv-test-integrity=2; CDP: board renders RTL/Cairo with 2 orders (OutForDelivery + driver → "تُسلّم"; New → disabled "لم يجهز بعد"), driver dropdown lists active drivers.

## POS-A — Restaurant admin organization (design approved 2026-07-08; Option 1 = standalone "Restaurant" system, 7th)
Unify restaurant setup under ONE `MainMenu.Restaurant()` (single source), make Restaurant a first-class system (7th in the switcher), and close the delivery-zone management gap. Organizational + the DeliveryZones screen only — NO sale/accounting logic change; inv=2.
- **✅ POS-A1 DONE (2026-07-08) — DeliveryZones management (the functional gap).** `PosSetupService`: `GetAllDeliveryZonesAsync` (incl. inactive), `SaveDeliveryZoneAsync(branch,id,name,nameEn,fee,isActive)`, `DeleteDeliveryZoneAsync` (deactivates if a `PosOrder.DeliveryZoneId` references it, else hard delete). Screen `Views/Pos/DeliveryZones.cshtml` (PosController `DeliveryZones`/`SaveDeliveryZone`/`DeleteDeliveryZone`, `_LayoutBackend`, branch picker + fee field). Cashier keeps the read-only `GetDeliveryZonesAsync` (active only). Menu links added to Admin System-setup AND `MainMenu.Restaurant()` Setup (Drivers too). Dev `pos-a1-test` 8/8 (CRUD + delete-ref→deactivate + active-only read + validation + ZERO GL); `/Pos/DeliveryZones` renders (302); inv=2.
- **✅ POS-A2 DONE (2026-07-08) — menu unified, Restaurant is the 7th system.** `_LayoutBackend` now renders `(ViewBag.SidebarMenu as List<MenuCategory>) ?? MainMenu.Admin()`; `PosController.OnActionExecuting` sets `ViewBag.SidebarMenu = MainMenu.Restaurant()` for ALL its setup screens → they show the unified Restaurant sidebar (not the generic Admin menu). `MainMenu.Restaurant()` regrouped per spec: التشغيل (Start/Kitchen/Delivery) · القاعة والقائمة (Setup/Areas/FloorPlan/QuickMenu/Modifiers/Preview) · الإعداد (Terminals/Drivers/PaymentMethods/CashierRoles/DeliveryZones). Added 7th system "المطعم/Restaurant" (→ Pos/Setup, colour #e8532f) to the `_MainMenu` Systems switcher (SysActive: path starts /pos). Removed the whole POS block from `MainMenu.Admin()` System-setup (kept Companies/Branches/Brands). Verified (CDP + HTML): setup screen shows Restaurant menu (Delivery board present, Employees absent = no Admin menu), switcher has 7 systems with "المطعم" active, DeliveryZones screen renders; regressions a1/c1/c2/c3/c4 allPass; inv-test-integrity=2. **POS-A COMPLETE — restaurant is now a first-class module; the Admin-vs-Restaurant menu divergence is resolved.**

## POS-C COMPLETE (2026-07-08)
Delivery end-to-end: data+fee (C1) → driver (C2) → status+real-time (C3) → board (C4). All operational; invoice incl. delivery fee + stock + GL at payment ONLY via existing services (no new GL writer); inv-test-integrity=2 throughout. NEXT per roadmap = RC-4 (Modifiers-at-sale).
