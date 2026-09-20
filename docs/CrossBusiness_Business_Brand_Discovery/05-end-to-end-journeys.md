# 05 — End-to-end journeys

How work actually moves through CrossBuy. Each journey names the screens, the services and the
seams. Where a step is inferred from structure rather than read from a code path, it says so.

---

## 0. Entering the product

**Runtime.** Sign in at `/Account/Login` → land on **`/Portal/Choose`**, a destination chooser —
not a dashboard. Pick a portal; the chosen module's layout supplies the sidebar
(`MainMenu.<Module>()` prepended with the Platform category).

Two things a reader should know before following any journey:

- **There is no company switcher.** Your company comes from your `Employee` row
  (`EmpCompanyID`). Working in a second company means a second employee record. This is by
  design, and it is why the company can be enforced in the data layer.
- **`/Module` on its own is a 404.** The default route is
  `{controller=Account}/{action=Login}/{id?}` with **no `action=Index` default**, so `/Inventory`
  does not open Inventory — `/Inventory/Index` does. Only seven controllers were given an explicit
  bare-name route: `Comm`, `Calendar`, `Reports`, `Workspace`, `Chat`, `BusinessEventMonitor`,
  `Tasks`. *(Runtime: `/Inventory`, `/Accounting`, `/Crm`, `/Admin` each returned 404 during the
  capture run.)* Any link, QR code or printed URL written as `/Accounting` is dead.

---

## 1. Procure to pay

```
Replenishment ──▶ Purchase order ──▶ Goods receipt ──▶ Purchase invoice ──▶ Payment
  (Planning)        (Inventory)        (Inventory)       (Accounting)       (Accounting)
                                            │                  │                 │
                                     stock increases      AP + tax          AP cleared
                                     + landed costs       + ledger          + allocation
```

| Step | Screen | What it does |
|---|---|---|
| Plan | `Inventory/Planning` | Replenishment against reorder points |
| Order | `Inventory/PurchaseOrders` | Commitment; no stock, no ledger |
| Receive | `Inventory/GoodsReceipts` | Stock increases. `StockService` is the authority |
| Cost | `Inventory/LandedCosts` | Freight, duty and handling added to item cost after the fact |
| Bill | `Accounting/PurchaseInvoices` | AP liability, tax, ledger entry |
| Pay | `Accounting/Payments` | `Payment.Created` → `Payment.Allocated` against the invoice |
| Return | `Accounting/PurchaseReturns` | Debit note; `PurchaseReturn.Created` / `.Reversed` |

**The seam worth knowing:** goods receipt and purchase invoice are separate documents in separate
modules, which is correct — and it creates the *goods received not invoiced* position. The chart of
accounts carries an account for exactly that («بضاعة وردت ولم تُفوتَر»). Any reader assessing the
product's accounting seriousness should note that this account exists.

**Landed costs after receipt** means item cost is not final at receipt. Any margin report run
between receipt and landed-cost allocation is provisional.

## 2. Order to cash

```
Quotation ──▶ Sales order ──▶ Delivery ──▶ Sales invoice ──▶ Receipt
                                  │              │              │
                           stock decreases   AR + tax      AR cleared
                                             + ledger      + allocation
```

Screens: `Inventory/Quotations` → `Inventory/SalesOrders` → `Inventory/Deliveries` →
`Accounting/SalesInvoices` → `Accounting/Receipts`, with `Accounting/SalesReturns` (credit note)
as the reversal path.

Pricing is not decided on the screen. **`IPricingService.GetPriceAsync` is the single price
authority** — it applies price-list priority, quantity breaks and promotions, and issues four to
six queries per item. A screen that shows a price and a screen that bills one therefore agree by
construction. (Repository evidence; the query count was measured in earlier work on this codebase,
not re-measured here.)

Follow-through after invoicing: `Accounting/CustomerStatement`, `Accounting/ArAging`,
`Accounting/CustomerAnalytics`.

**Where CRM joins.** `Crm/Leads` → `Crm/Opportunities` → `Crm/Pipeline` is a parallel track that
ends where this one begins. Whether an opportunity converts into a quotation *in the product* or
by a person retyping it was **not verified** in this discovery. It is the most commercially
important unverified question in this document.

## 3. Post to statement — the ledger

Every module that moves money reaches the ledger through a small set of posting services. Twenty-one
`BL` services post journal entries: `AccountingPostingService`, `ReceivableService`,
`PayableService`, `StockService`, `PosOrderService`, `PayrollRun`, `FixedAssetService`,
`EquipmentDepreciationService`, `FxRevaluationService`, `ClosingService`, `TaxService`,
`LeaveAccrualService`, `FinalSettlementService`, `ProjectLaborService`, `ContractService`,
`BankService`, `OpeningBalanceService`, `MaintenanceService`, `IntegrityCheckService`,
`JournalEntryService`, and the POS preparation path.

```
Fiscal periods  ──▶  Journals  ──▶  Trial balance  ──▶  Balance sheet
      │                                    │              Income statement
      │                                    │              Cash flow
      └── close a period                   └── the identity: debits = credits
```

`Accounting/TrialBalance` is the product's stated visual authority for accounting screens
(document 09) — the trial balance is where the accounting identity is shown, and every other
accounting screen is expected to look like it.

Year-end runs through `Accounting/YearEndClose`. FX revaluation (`Currency/Revaluation`) and
depreciation runs (`Accounting/DepreciationRuns`) are the two periodic processes that post without
a user-entered document.

`JournalEntry.Reversed` is the **only** journal event in the platform stream — you can observe a
reversal, but not a posting.

## 4. Hire to retire — the people journey

```
Job application ──▶ Employee ──▶ Onboarding ──▶ Attendance ──▶ Payroll ──▶ Final settlement
   Admin/Applications              Hr.*          Admin/Attendance   Accounting/Payroll
```

Payroll is the step that proves this is an accrual system rather than a spreadsheet. The payroll
preview computes, per employee: base salary, allowances, gross, **late minutes, overtime minutes
and absent days** taken from attendance, overtime pay, late penalty, absence penalty, employee
social insurance, company social insurance, tax, net — and an `EarningExpense` figure defined as
`gross + overtime − latePenalty − absencePenalty`, with a comment explaining that this keeps the
GL entry balanced without inventing accounts.

Around it: leave accrual and provisioning (`Admin/LeaveAccrual`), carry-over
(`Admin/LeaveCarryOver`), encashment, and `Admin/FinalSettlement` for end of service. These post
to the ledger, which is why HR and Accounting cannot be separated in this product.

**Document expiry** is its own loop: `Admin/RequiredDocTypesList` defines what must be held,
`Admin/HrDocuments` holds it, `Admin/DocExpiryAlerts` warns before it lapses.

> **Verified gap (runtime, earlier work):** `("Hr","employee-manage")` is in the platform's
> `NeverBootstrapOpen` set. On an install where no HR role has been granted, saving an employee
> refuses — correctly, but the refusal is the first thing a new administrator meets.

## 5. Approvals — one inbox, four silos

`ApprovalInboxService` aggregates every pending approval into `/Approvals/Index` ("My approvals").
Four silos feed it:

| Silo | Source | Opens at |
|---|---|---|
| `Leave` | `LeaveRequest` | `People/Leaves` |
| `Request` | `Admin.EmployeeRequest` | `People/Requests` |
| `Inventory` | `InventoryApproval` | `Inventory/Approvals` |
| `ProjectBilling` | المستخلص — progress billing | the specific billing document |

Each silo supplies its own navigation target rather than the aggregator hardcoding module routes.
Project billing is the one that needs route values, because its screen is addressed by *project*
and *billing* — the inbox opens the real document, not a list.

**Accounting documents do not go through this inbox.** An invoice is not approved; it is posted.
That is a deliberate design position, and a reader evaluating the product for a
segregation-of-duties requirement should notice it.

## 6. A restaurant order

```
Cashier terminal ──▶ Kitchen display ──▶ Delivery board
  PosApp/Start        PosApp/Kitchen      PosApp/Delivery
       │                    │
   PosOrder            consumption
   + payment           of ingredients
```

Purpose-built operator screens, not back-office lists. Behind them: dining areas and kitchen
stations, a floor plan, reservations, quick items and modifiers, delivery drivers and zones with
fees, per-terminal configuration and cashier roles.

Two mechanisms carry the design's weight:

- **Item sourcing** (`Pos/ItemSourcing`, `Pos/SourcingOverview`) decides *per item per branch*
  whether a sale draws from stock, is prepared in a kitchen from a recipe, or is bought in. This
  is what lets one product run a restaurant and a shop from one item master.
- **Sync conflict review** (`Pos/SyncConflicts`) exists because the terminal is meant to keep
  selling when the network is gone. A screen dedicated to reconciliation conflicts is the mark of
  an offline-first design that has met reality.

Kitchen consumption is **capability-gated**: a branch opts in, and the shortage policy and
cancel-after-preparation waste treatment are configuration, not code branches.

## 7. A construction project

```
Project ──▶ Advance ──▶ Progress billing (مستخلص) ──▶ Retention ──▶ Release
                             │                           held back      Project/RetentionRelease
                        approval silo                                   Project/SubRetentionRelease
```

Screens: `Project/Projects`, `Project/Advance`, `Project/RetentionRelease`,
`Project/SubRetentionRelease`, `Project/ActivityTypes`, `Project/Profitability`, plus
`ProjectCloseoutController`.

Advances, retention and **subcontractor** retention are contract mechanics specific to
construction. `Accounting.BoqItem` (bill of quantities) in the entity list confirms the domain.
Progress billing is the one accounting-adjacent document that *does* go through the approval
inbox.

## 8. The cross-module journey — what the platform adds

This is the journey that distinguishes CrossBuy from five separate applications.

```
a document is posted
        │
        ▼
 Platform.BusinessEvent written        (47 declared event types)
        │
 BusinessEventDispatchWorker           batch 50 · poll 15s · 5 attempts · 30s backoff
        │
        ├──▶ Notification              the bell, and /Notifications
        ├──▶ Workspace read model      /Workspace — Agenda, Notifications, Mentions, My reports
        ├──▶ Task auto-rules           Tasks/AutoRules → a task appears for someone
        ├──▶ Entity timeline           the document's own history
        └──▶ Business event monitor    /BusinessEventMonitor, and a report
```

Also crossing modules: `@mentions` (parsed, audited, self-excluding, on the timeline), comments on
documents, the file manager, announcements, chat and email.

**What to trust here.** The mechanism is real and complete — worker, retry, monitor, report,
timeline. The *coverage* is partial: money documents, tasks and calendar events emit events;
**stock emits nothing**, and the Accounting and Manufacturing families have incomplete producers.
So a user will reliably hear about an invoice or a task, and will not hear about a goods receipt.

---

## 9. Printing — one route, no exceptions

Every journey above ends in a document someone wants on paper, and the product has one answer:

> **No screen prints its own HTML.** A screen that needs printing registers a *dataset* and links
> to `/Reports/Viewer`. Report Studio designs the layout; the PDF renderer produces the file.

Eight Inventory documents share one view (`DocumentDetails.cshtml`) and opt into printing by
naming themselves. Document 06 describes the pipeline.

This is a standing architectural rule in this codebase, and it is the reason the report platform
is as developed as it is.
