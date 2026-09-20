# 04 — Business domain model

The 265 things CrossBuy stores, and the four platform concepts that hold them together.

Complete list: `business-catalog.json` → `persisted_entities`.

---

## 1. One context, 265 entity sets

Every table the application knows is declared in a **single** `CrossDbContext`. There is no
per-module context, no separate schema per product, no service-per-domain split.

| Namespace | Entity sets | What it covers |
|---|---:|---|
| `Accounting.*` | 61 | Chart of accounts, journals, fiscal periods, cost centres, currencies & FX, tax & VAT & ETA, banks & cash, payroll, fixed assets, AR/AP, **BoQ items** |
| `Inventory.*` | 52 | Items, units, categories, warehouses, sections/racks, batches, serials, movements, price lists, promotions, and the trade documents |
| `Pos.*` | 28 | Dining areas, kitchen stations, floor plan, modifiers, drivers, delivery zones, terminals, branch capabilities, item sourcing, sync |
| *(no namespace)* | 28 | The original HR/company core — `Companies`, `Branch`, `Employee`, leave, attendance, payslips, policies, `Hierarchical` |
| `Crm.*` | 20 | Accounts, contacts, leads, opportunities, pipelines & stages, campaigns, marketing lists, tickets, SLA, scoring & automation, custom fields |
| `Admin.*` | 15 | Appraisals, training, job applications, HR documents, brands, employee requests |
| `Reporting.*` | 14 | Report catalogue, templates, assets, schedules, archive, run history |
| `Tasks.*` | 9 | Tasks, templates, auto-rules, match suggestions, timesheets |
| `Construction.*` | 8 | Contracts, advances, retention, subcontractors, variation orders |
| `Hr.*` | 8 | Onboarding templates & items, roster periods, assignments, work shifts |
| `Platform.*` | 6 | Business events, dispatch, role assignments, bootstrap policy, **AI egress audit**, AI projection |
| `Calendar.*` | 5 | Events, attendees, resources, schedules |
| `Comm.*` | 5 | Announcements, messages, attachments, document comments |
| `Chat.*` | 4 | Conversations, members, messages, reactions |
| `Library.*`, `Loyalty.*` | 1 each | |

### What the 28 un-namespaced sets tell you

`Companies`, `Branch`, `Employee`, `Hierarchical`, `LeaveRequest`, `Payslip`, `Policies`,
`AttendanceRecord`, `SystemForms`, `CountriesLookup` — these carry no namespace because they are
**the oldest layer**. CrossBuy began as an HR and company-administration system; Accounting,
Inventory, POS and the rest were built around it, each taking a namespace as it arrived.

That history is still load-bearing. `Employee` is not an HR record that Accounting happens to
reference — it is the identity the whole platform resolves a user's **company** from, which is
why there is no company switcher in the UI: your company is a property of your employee row.
`Hierarchical` is the organisation tree, and it is one tree shared by every company (see §5).

---

## 2. The documents — the business objects that matter

`BL/Platform/EntityRegistry.cs` is the platform's own list of things that can be referenced,
linked, commented on, approved and put on a timeline. It is a better definition of "business
object" than the table list, because it is what the product treats as *a thing a person works
on*:

**Trade:** `SalesInvoice` · `PurchaseInvoice` · `Quotation` · `SalesReturn` · `PurchaseReturn` ·
`PurchaseOrder` · `SalesOrder` · `GoodsReceipt` · `DeliveryNote`
**Stock:** `StockTransfer` · `StockCount` · `StockWriteOff` · `LandedCost` · `Item`
**Money:** `Receipt` · `Payment` · `JournalEntry`
**Parties:** `Customer` · `Supplier` · `Employee`
**Work:** `ManufWorkOrder` · `PosOrder` · `Project` · `ProjectBilling` · `Task` · `CalendarEvent`
**Platform:** `PlatformRoleAssignment` · `PlatformEvent`

Twenty-eight codes. Registration is not cosmetic: the registry's resolver is also the **tenancy
check**, so a code that is listed but has no resolver arm is denied for every action. Being on
this list is what makes an object first-class.

## 3. The event stream

`BL/Platform/BusinessEventTypes.cs` declares **47 business event types** across seven families:

| Family | Events |
|---|---|
| `SalesInvoice.*` | Created, Updated, Cancelled |
| `PurchaseInvoice.*` | Created, Updated, Cancelled |
| `SalesReturn.*` / `PurchaseReturn.*` | Created, Updated, Reversed |
| `Receipt.*` / `Payment.*` | Created, Updated, Allocated, Reversed |
| `JournalEntry.*` | Reversed |
| `ManufWorkOrder.*` | Created, Updated, Released, Produced, Completed, Cancelled |
| `Task.*` | Created, Assigned, Reassigned, StatusChanged, DueDateChanged, BecameOverdue, Completed, Reopened, Cancelled |
| `CalendarEvent.*` | Created, Updated, Rescheduled, Started, Completed, Cancelled, AttendeeAdded, AttendeeRemoved, ReminderTriggered |
| `Customer.*` | Created, Updated |

Events are written to `Platform.BusinessEvent`, claimed and delivered by a background worker
(`Platform:EventDispatch` — batch 50, poll 15 s, 5 attempts, 30 s backoff, 10 min stale-claim
reclaim), and surfaced three ways: notifications, the Workspace read model, and the
**Business event monitor** screen. They are also a registered report (`Platform.BusinessEventLog`).

**This is the mechanism behind the tagline.** «اربط فريقك وإجراءاتك وبياناتك» is implemented as:
a document is posted → an event is written → a notification, a task or a timeline entry appears
for a person who was not in that module. `Task.BecameOverdue` and the Tasks module's *auto-task
rules* close the loop in the other direction.

### Where the stream is thin

Nine of the 47 events are `CalendarEvent.*` and nine are `Task.*` — the collaboration layer is the
most instrumented. **Inventory has no events at all**: no `StockMovement.*`, no `GoodsReceipt.*`,
no `StockCount.*`. The module with 52 entity sets and 34 screens emits nothing into the stream.
Accounting and Manufacturing event families exist but their producers are incomplete (recorded in
the project's own engineering notes). A reader should treat the event stream as **real but
partial** — strong for money documents, tasks and the calendar; absent for stock.

## 4. Multi-tenancy: company is a filter, not a suggestion

The product is multi-company and enforces it in the data layer rather than the UI:

- Most entities carry `CompanyID` and are filtered globally by EF Core query filters.
- The company is resolved from the authenticated context (`IRequestCompanyResolver`) — **never
  taken from a query string**. `Controllers/Api/AiController.cs` carries a long comment about a
  defect where it *was* taken from the query string (`?companyId=7` returned another company's
  chart of accounts) and how it was closed. That comment is worth reading in full; it is the
  clearest statement in the codebase of how the boundary is meant to work.
- `Store:StoreCompanyId = 1` is the one deliberate exception — the anonymous public catalogue is
  pinned to a single company by configuration.

**Two documented gaps in the filter**, both written down by the engineers rather than discovered
here: `ItemComponent` (the bill-of-material line) sits outside the global filters, and four
accounting tables — `Account`, `CostCenter`, `BankAccount`, `CashBox` — carry no Stage-1 company
filter. Any new read of those must filter explicitly.

## 5. `Hierarchical` — the organisation tree, and the honest exception

`dbo.Hierarchicals` is the org chart behind `/Admin/AdministrativeStructure`. **It carries no
`CompanyID`.** One tree, shared by every company.

The reporting dataset built over it (`Admin.OrgStructure.Units`) is documented at length because
the first version *invented* a company boundary — entering only at the node matching the caller's
company — and it was removed deliberately:

> *"An invented boundary that the UI cannot show is worse than no boundary, because the reader
> cannot tell the difference between 'that is all there is' and 'that is all you are being
> shown'."*

Consequences stated plainly in the source: the permission `admin.orgstructure.reports.view` is a
**cross-company grant**; narrowing the data is the reader's explicit choice via a root-node
parameter; the tree contains duplicate roots, so rows are de-duplicated on the walk.

This is the best single example of the codebase's documentation culture — a limitation named,
reasoned about, and left visible rather than papered over. Document 12 says more about that
culture.

---

## 6. What the model says about the business

1. **One ledger, many industries.** Restaurant orders, hypermarket lanes, work orders and
   construction billing all land in the same `Accounting.*` namespace. That is the product's
   central bet and its hardest thing to copy.
2. **The document, not the screen, is the unit of work.** 28 registered entity codes, each with
   approvals, comments, timeline and reporting attached generically. Adding a document type means
   registering it, not rebuilding those four facilities.
3. **People are infrastructure.** `Employee` resolves the company, drives permissions, owns tasks
   and receives notifications. The HR module is not a side module; it is the identity layer.
4. **The event stream is the differentiator and the weakest link.** It is what makes CrossBuy a
   platform rather than five applications — and stock, the busiest part of the product, is not on
   it yet.
