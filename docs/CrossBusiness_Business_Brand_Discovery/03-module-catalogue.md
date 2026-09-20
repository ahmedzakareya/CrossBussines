# 03 — Module catalogue

The product's own map of itself. `Models/Menu/MainMenu.cs` is a single file that declares ten
module menus; a layout passes one of them to `_MainMenu.cshtml`, which prepends the cross-module
**Platform** category to whichever it is given. So the menu is not decoration — it is the
authoritative inventory of destinations, maintained in one place.

Full tree with Arabic and English labels for all 186 entries: `business-catalog.json` →
`modules`.

---

## 1. The ten modules at a glance

| Module | Categories | Destinations | Sidebar reached from |
|---|---:|---:|---|
| **Accounting** | 12 | 64 | `_LayoutAccounting` |
| **Inventory** | 7 | 34 | `_LayoutInventory` |
| **Admin** (HR & system) | 4 | 28 | `_LayoutBackend` |
| **Restaurant** | 4 | 19 | `PosController` / `PosAppController` |
| **Platform** (always present) | 2 | 10 | prepended to every menu |
| **Manufacturing** | 4 | 9 | `_LayoutManufacturing` |
| **Tasks** | 3 | 8 | `TasksController` |
| **Projects** | 1 | 7 | `ProjectController` |
| **Hyper** (supermarket) | 3 | 4 | `HyperController` |
| **Calendar** | 1 | 3 | `CalendarController` |
| | **41** | **186** | |

All ten are live: every `MainMenu.X()` has at least one call site, in a layout or a controller.

Razor views tell the same story about where the weight is — Inventory 79, Accounting 68, CRM 34,
Admin 31 of 372.

---

## 2. Platform — the cross-module spine

Prepended to every module's sidebar so it exists once and appears everywhere.

**Platform:** Workspace · Agenda · Notifications · Mentions · My reports · Business event monitor
**Reporting:** Reports center · Business event report · Favourite reports · Report Studio

Two things here are worth a business reader's attention:

- **Workspace** (`/Workspace`) is a cross-module read model — one surface that aggregates what is
  waiting for you regardless of which module produced it. Agenda, Notifications, Mentions and
  My reports are its four faces.
- **Business event monitor** is an operations tool over the platform event stream (document 04).
  Its menu row is gated by the *same* predicate the screen's filter uses, so the link is hidden
  exactly when the screen would refuse — a small discipline that is applied consistently and is
  worth noting as a quality signal.

## 3. Accounting — the largest module

Twelve categories, 64 destinations. This is the product's centre of gravity.

| Category | Destinations |
|---|---|
| **General** | Executive dashboard · Dashboard · Chart of accounts · Journals · Cost centers · Fiscal periods · AI insights |
| **Currencies & FX** | Currencies · Exchange rates · Functional currency setup · FX revaluation |
| **Receivables** | Customers · Sales invoices · Receipts · Sales returns (credit note) · Customer statement · AR aging · Customer analytics |
| **CRM** | 19 destinations — see §4 |
| **Payables** | Vendors · Purchase invoices · Payments · Purchase returns (debit note) · Vendor statement · AP aging |
| **Banks & cash** | Bank accounts · Cash boxes · Cash transfer · Bank reconcile |
| **Payroll** | Payroll run · Payslips · Disbursement & remittance · Payroll tax & insurance |
| **Tax** | Tax codes · VAT returns · **ETA status** (Egyptian e-invoicing) |
| **Fixed assets** | Fixed assets · Asset categories · Depreciation runs · Maintenance due |
| **Financial reports** | Trial balance · Balance sheet · Income statement · Cash flow |
| **Closing** | Year-end close |
| **Setup & roles** | Accounting roles |

This is a complete double-entry system, not a ledger bolted to an inventory app: fiscal periods,
cost centres, functional-currency setup with revaluation, a year-end close, and the four statutory
statements. **Trial balance** is the product's stated visual authority for accounting screens
(document 09).

Multi-currency is a first-class concern with its own controller (`CurrencyController`) and four
screens including FX revaluation — that is an unusual depth for a mid-market product and is a
genuine differentiator.

## 4. CRM — a full module living inside Accounting's menu

Nineteen destinations, its own controller (`CrmController`, 34 views), its own roles screen — and
no menu of its own. It is a category inside `MainMenu.Accounting()`.

Accounts · Leads · Opportunities · Pipeline (Kanban) · Opportunity Insights · Account Insights ·
Pipeline setup · Activities · Campaigns · Marketing lists · Tickets · SLA policies · Forecast ·
360° reports · Scoring & automation · Custom fields · Automation rules · CRM roles

The consequence is visible at runtime: `/Crm/Index` renders `_LayoutAccounting`, so the browser
tab reads *"Accounting System - CrossBuy"* and the sidebar shows all 161 Accounting links
(document 02, document 11). A salesperson using CRM navigates an accountant's product.

This is the single clearest structural mismatch between how the product is *built* and how it is
*sold* — the sign-in screen lists CRM as one of eight peers.

## 5. Inventory — the operational spine

Thirty-four destinations across seven categories, and the module every other one borrows from.

| Category | Destinations |
|---|---|
| **Master data** | Items · Categories · Units · Warehouses · Sections/racks · Item locations |
| **Movements & balances** | Stock movements · Stock balances · Rack stock · Batches & expiry · Serials |
| **Operations** | Assembly · Transfers · Stock counts · Write-offs · Landed costs · Capitalize asset |
| **Purchasing & sales** | Purchase orders · Goods receipts · Purchase invoices → · Replenishment · Price lists · Promotions · Quotations · Sales orders · Deliveries · Sales invoices → |
| **Governance & setup** | Opening balances · Integrity check · Approvals · Inventory roles · Inventory settings |
| **Reports** | Inventory reports |

Depth signals: batch/expiry **and** serial tracking, rack-level stock, landed costs, and
`CapitalizeAsset` — moving stock into the fixed-asset register. `IntegrityReconciliation` is a
governance screen that checks stock against the ledger; a product that ships a reconciliation
screen has been used on real data.

Note the two arrows: *Purchase invoices* and *Sales invoices* in the Inventory menu point at
`AccountingController`. The menu is honest about the seam — the document belongs to Accounting,
and Inventory links across rather than duplicating it.

## 6. Manufacturing — declared work in progress

Nine destinations: Manufacturing dashboard · Work orders · New work order · Production planning
(MRP) · Quick assembly · Items & BOMs · Work centers · Units · Manufacturing reports.

Every one lives on `InventoryController` — manufacturing has no controller of its own. The portal
chooser labels it *"Work orders & production tracking **(WIP)**"*, which is the product telling
the truth about itself, and its dashboard is the one screen whose title still reads *"Warehouse
System - CrossBuy"* (document 02).

Nonetheless the pieces of a real MRP are present: bills of material, work centres, production
planning, and work-order lifecycle events (`ManufWorkOrder.Created/Released/Produced/Completed/Cancelled`).

## 7. Restaurant — a separate product inside the product

Nineteen destinations and the only module with purpose-built operator screens rather than
back-office lists.

| Category | Destinations |
|---|---|
| **Operations** | **Cashier terminal** · **Kitchen display** · **Delivery board** |
| **Floor & menu** | Operations setup · Areas & stations · Floor plan · Reservations · Quick items · Modifiers · Cashier preview |
| **Setup** | Cashier terminals · Delivery drivers · Payment methods · Cashier roles · Delivery zones & fees · Item sourcing · Sourcing overview · **Sync conflict review** |

Two entries reveal the ambition. **Sync conflict review** means the cashier is designed to keep
selling when the network is gone and reconcile afterwards — an offline-first POS, with a screen
dedicated to the conflicts that produces. **Item sourcing** decides, per item, whether a sale
draws from stock, is made to order in a kitchen, or is bought in.

It has its own layouts (`_LayoutPos`, `_LayoutPosApp`) and its own product name in the tab
(`· CrossBuy POS`). Of everything in CrossBuy, this is the piece most capable of being sold on its
own.

## 8. Hyper — supermarket retail

Four destinations: Dashboard · Cashier terminal (`HyperPos/Login`) · Hyper sales · Item margin.
A third cashier surface with a third layout (`_LayoutHyperPos`, `· CrossBuy Hyper`).

Small in the menu, distinct in intent: supermarket lanes, not restaurant tables. The two reports
it ships — sales and item margin — say what it is for.

## 9. Projects & Contracting — construction

Seven destinations: Projects dashboard · Projects · **Receive advance** · **Release retention** ·
**Release subcontractor retention** · Project activity types · Project profitability.

Advances, retention and subcontractor retention are construction-contract mechanics, not generic
project management. `BL/Construction/` and `Models/Context/Construction/ConstructionCommercial.cs`
confirm a dedicated domain. The design documents in `docs/` carry it further than the menu does —
`CrossBuy_Projects_P6d_VariationOrders_Design.md` among six project design papers.

## 10. Tasks and Calendar — the work layer

**Tasks** (8): My tasks · All tasks · Board · Reports dashboard · Hours report · Auto-task rules ·
Match suggestions · Task templates.
**Calendar** (3): Calendar · Timeline · Resource view.

*Auto-task rules* and *Match suggestions* are the interesting pair: tasks generated from business
events rather than typed by a person, with a review queue for proposed matches. An hours report
implies timesheets. This is the connective tissue the tagline claims — the layer that turns a
posted invoice into somebody's work.

## 11. Admin — HR and system setup

Twenty-eight destinations.

| Category | Destinations |
|---|---|
| **General** | HR Dashboard · Chat · Notifications · My approvals · Email · Calendar · File Manager · Announcements |
| **Employees & structure** | Employees · Org structure · Job titles · Administrative bodies · Job applications · Contracts & documents · Expiry alerts · Required documents · **Termination & final settlement** |
| **Leave & attendance** | Leave types · Policies · Official holidays · Attendance · **Encashment & provision** · **Leave carry-over** · Performance appraisal · Training |
| **System setup** | Companies · Branches · Brands |

Leave encashment, leave provisioning, carry-over and end-of-service settlement are accrual
mechanics — they touch the ledger, not just a calendar. Together with Payroll's tax and social
insurance screens this is a regionally-specific HR system, not a staff directory.

The **General** category is the giveaway that Admin is also the fallback home: Chat, Email, File
Manager, Announcements and Approvals are platform-wide facilities that live here because there is
nowhere else for them (document 12).

---

## 12. What the menu does not include

Present in the codebase, reachable, and absent from every module menu:

| Surface | Controller | Views |
|---|---|---|
| Client Portal | `ClientPortalController` | 6 |
| Public store | `StoreController` | 5 |
| People portal | `PeopleController` | 12 |
| Restaurant intelligence | `RestaurantIntelligenceController` | — |
| Insight actions | `InsightActionsController` | — |
| Employee onboarding | `EmployeeOnboardingController` | — |
| Documents · Roster · Announcements · Brand | four controllers | — |

The People portal is reachable from `Portal/Choose`; the others are reached by direct URL or from
within another screen. That is not necessarily wrong — a client portal is for clients, not for
staff sidebars — but it means **the sidebar is not a complete map of the product**, and any
inventory built from the menu alone will miss eleven surfaces. The controller list in
`business-catalog.json` is the complete one.
