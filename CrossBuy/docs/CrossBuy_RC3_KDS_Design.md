# CrossBuy — RC‑3 / POS‑5 Design: KDS + Real‑time + Waiter/Kitchen roles on the shared order

**Status:** design approved 2026‑07‑07. Build phase‑by‑phase (small → verify → STOP each). Purely OPERATIONAL — no accounting effect; invoice/stock/GL happen ONLY at payment (StockService/JournalEntryService, built + verified in RC‑2). Invariants stay green (`inv-test-integrity` failedCount=2). Offline‑ready in structure (state read from the order; real‑time is an enhancement with poll fallback).

## Builds on existing pieces (confirmed)
- `PosOrderLine.SentQty` / `SentAt` (POS‑4b) — "sent to kitchen", per line.
- `KitchenStation {BranchId, Code, Name, StationType}` — exists; **no item→station map yet**.
- `BranchUserRole {BranchId, EmployeeId, PosRole}` roles: pos‑waiter / pos‑kitchen / pos‑cashier / pos‑manager. `PosAccessService.CanSell` (cashier/manager) + `IsManager`.
- `NotificationsHub` (SignalR) — per‑employee groups `emp-{id}`, Identity cookie or JWT; `/hubs/notifications`; `AddSignalR()` registered. No per‑branch group.
- Cashier `/pos` signs in via Identity cookie (PasswordSignInAsync) + `PosCtx` session → SignalR can authenticate it.

## Approved decisions
- **A — screens by role:** waiter uses the SAME cashier terminal minus payment (pay hidden/blocked when `!CanSell`; server already blocks it); kitchen gets a dedicated `/pos/kitchen` KDS. Add `IsWaiter`/`IsKitchen`; enforce every endpoint server‑side by role.
- **B1 — kitchen state on the LINE:** `PosOrderLine.KdsStatus` ∈ {New, Preparing, Ready} (NULL before send). Order KDS status is **derived** (all sent lines Ready ⇒ Ready; any Preparing / mixed ⇒ Preparing; all New ⇒ New; none sent ⇒ none).
- **B2 — single branch queue first:** KDS shows all sent lines for the branch; **station routing deferred to RC‑3e** (needs an item→station map).
- **C1 — dedicated `PosHub`** at `/hubs/pos`, per‑branch groups `posbranch-{branchId}` (Identity cookie auth). Separate from personal notifications.
- **C2 — events** (one branch group; each screen filters what it cares about):
  - `OrderSentToKitchen(orderId, tableCode, lines)` — waiter sends → KDS shows the ticket; cashier updates "sent".
  - `LineKdsStatusChanged(orderId, lineId, status)` — kitchen advances a line → waiter/cashier see progress; other KDS screens sync.
  - `OrderReady(orderId, tableCode)` — derived all‑Ready → alert waiter/cashier.
  - `OrderPaid(orderId)` — cashier pays → KDS clears the ticket.

## Phased breakdown (STOP after each)
- **RC‑3a — kitchen state model.** `PosOrderLine.KdsStatus`; send‑to‑kitchen sets `New`; kitchen endpoints New→Preparing→Ready (forward‑only, gated pos‑kitchen/manager, kitchen never edits items/prices); derived order status; dev test (transitions + role guard + zero GL + inv=2).
- **RC‑3b — KDS screen reads state (no real‑time).** `/pos/kitchen` (pos‑kitchen), ticket cards of sent lines with per‑line status buttons; periodic refresh.
- **RC‑3c — real‑time.** `PosHub` + per‑branch groups + the 4 events; KDS + terminal subscribe → live updates (poll fallback).
- **RC‑3d — waiter/kitchen role scoping.** `IsWaiter`/`IsKitchen`; login routing (kitchen → `/pos/kitchen`); hide payment for waiter; server‑enforce endpoints per role.
- **RC‑3e (optional) — station routing.** item→station map + station tabs/filter on KDS.

## RC-3e — station routing (BUILT 2026-07-07)
Key constraint: stations are per-BRANCH, items/categories are company-level → route by the per-branch **POS menu tab** (`PosMenuGroup`), not the item/category directly.
- `PosMenuGroup.KitchenStationId` (nullable, per-branch) — a tab's items route to this station. `PosOrderLine.StationId` (nullable) — FROZEN at send-to-kitchen. SQL `deploy/sql/pos_kds_stations.sql` (idempotent), applied.
- Resolution in `SendToKitchenAsync` (per newly-sent line, set once): item → its `PosQuickItem.GroupId` → `PosMenuGroup.KitchenStationId`; else DEFAULT = first active `KitchenStation` with StationType="Kitchen" (else first active). Frozen (`if StationId==null`) so later config changes never move a sent line.
- Config UI: a station `<select>` per tab in the existing QuickMenu admin screen (add-tab form + per-tab inline auto-submit); `SaveMenuGroupAsync(..., kitchenStationId?)` guards the station belongs to the branch.
- KDS: `KitchenLineDto` carries StationId+StationName; branch stations injected into the KDS page; **client-side station tabs** («الكل» + one per station) filter lines by StationId, persisted in localStorage; «الكل جاهز» only readies the visible station's lines. Legacy sent lines with null StationId show only under «الكل».
- Item-level override (`PosQuickItem.KitchenStationId`) deliberately deferred (add later if needed).
- Verified: `rc3e-test` 4/4 (tab→Bar, unmapped→default, different stations, ZERO journal entries); CDP KDS: tabs [الكل, الجريل, البار], البار shows only the Bar-tab item, الجريل only the default item; inv-test-integrity=2.

## KDS REBUILT to PURE Metronic (2026-07-07)
User rejected the earlier custom dark KDS (built with no reference). Rebuilt the whole screen with theme components only — **no control outside Metronic**.
- Layout: `_LayoutAccounting` (full admin shell — sidebar `MainMenu.Accounting()` + header), same as the accounting screens (user chose this explicitly).
- Structure mirrors `Views/Accounting/Journals.cshtml`: `#kt_app_toolbar` (page-heading + breadcrumb + toolbar buttons) → `#kt_app_content` container-fluid.
- Station tabs = `ul.nav.nav-pills` (was `.kds-tabs`). Ticket grid = `row g-5` of `col-12 col-md-6 col-xl-4` Metronic `card`s with `border-top border-3 border-{info|warning|success}` for order status. Lines = `table table-row-dashed`; status = `badge badge-light-{info|warning|success}` (New→info, Preparing→warning, Ready→success, same style as `_JournalRows` StatusBadge); actions = `btn btn-sm btn-light-{warning|success}`; footer «الكل جاهز» = `btn btn-light-success w-100`.
- ALL custom CSS removed from `pos-cashier.css` (former `.kds/.kt-*/.kl-*` block deleted); Kitchen.cshtml no longer links pos-cashier.css and has no `@section Styles`.
- JS: `pos-kds.js` render()/renderTabs() emit Metronic markup; behaviour UNCHANGED (station filter+localStorage, forward-only New→Preparing→Ready, all-ready for visible station, SignalR `/hubs/pos` + adaptive poll fallback, live clock + elapsed timers). Kept class hooks `.kl-adv`/`.kt-allready`/`.kt-time` so handlers are untouched.
- Kitchen() action seeds `Session["Employee"]` from PosCtx (the accounting shell reads it for the header user; POS users sign in via Identity, not that session — avoids a null-ref in the layout).
- Verified: `/pos/kitchen` renders with sidebar (kt_app_sidebar) + nav-pills + card grid, ZERO `.kt-card/.kds-*` classes, pos-cashier.css NOT linked, signalr loaded; CDP DOM cards:3 tabs:3 badges:22 hasCustom:false; tickets feed live; inv-test-integrity=2.

## KDS iteration 2 (2026-07-07): Restaurant sidebar + projects.html cards + confirm-on-every-change
- **Sidebar is the Restaurant menu, NOT accounting.** Added `MainMenu.Restaurant()` (Operations: Cashier terminal `PosApp/Start`, Kitchen display `PosApp/Kitchen` · Floor & menu: Areas/FloorPlan/QuickMenu/Modifiers/Preview `Pos/*` · Setup: Setup/Terminals/PaymentMethods/CashierRoles `Pos/*`). `_LayoutAccounting` now renders `(ViewBag.SidebarMenu as List<MenuCategory>) ?? MainMenu.Accounting()`; Kitchen() sets `ViewBag.SidebarMenu = MainMenu.Restaurant()`. Accounting screens unchanged (fallback). The only `/Accounting` link left on KDS is the global "Systems" quick-switch footer in `_MainMenu` (same on every screen).
- **Card design taken from Metronic demo39 `pages/user-profile/projects.html`**: `card border-hover-primary`; `card-header pt-7` with a `symbol symbol-45px bg-light-{color}` (geolocation=dine-in / handcart=takeaway) + title `fs-4 fw-bold` + `Order #` + status `badge badge-light-{color} fw-bold px-4 py-3`; `card-body` with two `border-dashed` stat boxes (Elapsed / ready-count) + a `progress` bar (readyCount/total) + the line list. Colors: New→info, Preparing→warning, Ready→success.
- **Confirm on EVERY status change** (user: "مفيش حاجه اسمها اضغط واتفاجئ بالحالة"). advance() and allReady() call `CB.confirm({...})` (crossbuy-confirm.js, loaded by the layout) BEFORE applying, then `CB.toast.success/error` after — the project-wide confirm→toast standard ([[confirm-toastr-standard]]). Start→"بدء تحضير «X»؟", Ready→"تعليم «X» كجاهز؟", all-ready→"تعليم كل أصناف الطلب #n كجاهزة؟".
- Verified (CDP): 4 projects-style cards + symbols + progress bars, `hasCB:true`, clicking Start opened the Metronic SweetAlert "Confirm status change / Mark \"Beef burger\" as ready?"; sidebar = Restaurant (no accounting category links); inv-test-integrity=2.

## KDS iteration 3 (2026-07-07): status-from-a-list + brand tabs + friendly elapsed
User feedback on iteration 2: (a) change status FROM A LIST (not buttons), (b) tabs weren't real Metronic, (c) colors off-brand (blue), (d) elapsed-as-a-clock is bad. Fixes:
- **Status change = Metronic dropdown LIST** (`data-kt-menu` menu, "CHANGE STATUS" header + forward-status items `.kl-set` with colored bullets) replacing the Start/Ready buttons. Forward-only options only (New→[Preparing,Ready], Preparing→[Ready], Ready=done badge). Picking an item → `CB.confirm` → apply → toast. Dynamically-rendered menus need `KTMenu.createInstances()` after each `grid.innerHTML` (Metronic only auto-inits menus present at load). Server allows any forward jump (`ti>=ci`), so New→Ready directly is valid.
- **Tabs = `nav-line-tabs nav-line-tabs-2x`** (was nav-pills). crossbuy-brand.css already themes `.nav-line-tabs .nav-link.active` → brand green `#13433a`. Verified active color = rgb(19,67,58).
- **Colors brand-aligned**: statusColor New→primary (brand green #13433a), Preparing→warning, Ready→success. No off-brand blue/purple (dropped `info`).
- **Elapsed = friendly duration** `elapsedText()` → "3h 24m" / "45m" / "5m" / "now" (bilingual "س/د"), NOT a HH:MM clock. Stat-box label "منذ الإرسال / Since sent".
- Verified (CDP): status dropdown opens (subShow=true, "CHANGE STATUS → Ready"), picking fires the confirm dialog, tab active = brand green, elapsed shows "3h 24m"; inv-test-integrity=2.

## KDS iteration 4 (2026-07-07): time-urgency + soft hover + inner padding
- **Elapsed conveys time passing**: `elapsedTier(iso)` → success(<8m)/warning(<18m)/danger(≥18m). The elapsed stat box escalates colour with age (green→amber→red) + a Metronic `pulse pulse-{tier}` ring (animation-pulse) beside a clock icon → visible "live/urgent" cue. Tick loop updates the number every 1s and only swaps colour classes when the tier actually changes (so the pulse animation never restarts). Verified: fresh=success, old 3h+=danger, all with animating pulse-ring.
- **Soft card hover**: replaced `border-hover-primary` (hard border) with Metronic `hover-elevate-up shadow-xs` (gentle lift). Pure theme utilities.
- **Inner padding**: line rows `py-3 px-2 gap-2` (were py-2, flush); card body `px-6`; stat-box labels `mt-1`.

## KDS iteration 5 (2026-07-07): tabs match the reference exactly
Station tabs now use the EXACT demo39 profile-nav classes: ul `nav nav-stretch nav-line-tabs nav-line-tabs-2x border-transparent fs-5 fw-bold`, links `nav-link text-active-primary ms-0 me-10 py-5` (+active). Bigger, well-spaced, prominent underline bar — active underline/text = brand green `#13433a` (crossbuy-brand.css), not the demo's blue. (Earlier fs-6/pb-3/cramped look was the "far off" complaint.)

## KDS iteration 6 (2026-07-07): reference typography + REAL dish images + Cairo
- **Real dish image per line** (not avatars): `KitchenLineDto.Image` added, populated from `Item.ImagePath` in GetKitchenTicketsAsync (same source the terminal/menu use). Client renders it as a `symbol symbol-50px` rounded food thumbnail beside each line; fallback = light food icon only if no image. Verified feed returns `"image":"/uploads/items/real-85.jpg"` and photos render.
- **Reference typography**: card title `fs-3 fw-bold text-gray-900`, order# `fs-6 fw-semibold text-gray-500`, item name `fs-6 fw-bold` with coloured qty prefix — matching demo39 projects card.
- **Cairo for Arabic**: no font-family override introduced (Arabic inherits Cairo from `_LayoutAccounting`'s `html[lang=ar] body`). Upgraded the Cairo Google-Fonts link to `family=Cairo:wght@400;500;600;700;800` so the bold/semibold reference weights render in real Cairo (was weight-400 only → faux-bold).

## Invariants / rules (every step)
Operational only (sent/preparing/ready) — no journal entries, no stock movement. Payment path unchanged (RC‑2). `inv-test-integrity` must stay failedCount=2. Any new `/pos/*` endpoint must be added to `SessionValidationMiddleware` cashier allowlist or it 302s to admin login.
