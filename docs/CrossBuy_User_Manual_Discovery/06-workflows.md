# 06 — End-to-end workflows

Eighteen workflows, 93 steps, every step tied to a screen that exists.

**Machine-readable: `workflow-catalog.json`.** Every step carries `screen_id`, `route`, the
bilingual screen title, the action, the expected result, the status after, and its own evidence
level. The build script **validates every route against `screen-catalog.json` and fails if one
does not exist** — four invented routes were caught and corrected that way.

---

## ⚠ The one caveat that governs this whole document

> **No workflow below was executed.** Screens were opened and read. No document was created,
> posted, approved, cancelled or deleted, because doing so would change data — which the brief
> forbids.

Each step is therefore labelled:

| Label | Meaning |
|---|---|
| `behaviour tested safely` | Actually exercised — sign-in, language switch, navigation, a refusal |
| `runtime-viewed (screen renders); action NOT performed` | The screen was loaded and measured. Its buttons were not pressed. |
| `not verified` | The expected result is what the code and the screen's own text say will happen |

Of 93 steps, counted from the catalogue: **2 behaviour-tested**, **57 runtime-viewed** (including the trial balance, captured with real balanced figures), **34 not verified**.

---

## 1. The eighteen workflows

| ID | Workflow | Module | Steps |
|---|---|---|---:|
| `WF-SETUP-01` | Create the company and its branches | Service / Admin | 3 |
| `WF-SETUP-02` | Grant module roles so people can work | Admin | 4 |
| `WF-ACCESS-01` | Sign in, choose a portal, switch language | Account | 4 |
| `WF-P2P-01` | Procure to pay | Inventory + Accounting | 7 |
| `WF-O2C-01` | Order to cash | Inventory + Accounting | 5 |
| `WF-ACC-01` | Set up the ledger | Accounting | 7 |
| `WF-ACC-02` | Close a period and read the statements | Accounting | 8 |
| `WF-INV-01` | Set up stock and count it | Inventory | 8 |
| `WF-INV-02` | Transfer stock between warehouses | Inventory | 3 |
| `WF-HR-01` | Hire, record attendance, run payroll | Admin | 8 |
| `WF-APPR-01` | Act on an approval | Approvals | 3 |
| `WF-POS-01` | Serve a restaurant order | Restaurant | 5 |
| `WF-PRJ-01` | Run a construction project to billing | Projects | 7 |
| `WF-CRM-01` | Lead → opportunity → ticket | CRM | 5 |
| `WF-REP-01` | Find, run, print and design a report | Reporting | 4 |
| `WF-COLLAB-01` | Work reaches you: workspace, notifications, tasks, mentions | Platform | 5 |
| `WF-AI-01` | Read an AI insight | Accounting / Platform | 4 |
| `WF-EXT-01` | What people outside the company see | Portals | 3 |

---

## 2. The structural facts a manual writer must know first

### 2.1 The product is command-driven, not form-driven

```
form-then-save pairs (GET form → POST of the same name)      15
POST commands with no matching form                         390
```

Only fifteen screens follow *show form → submit → page reloads*. **Three hundred and ninety
actions are commands** fired by JavaScript from a button or a modal, with the affected row
updating in place.

**Every workflow step in this package is phrased accordingly.** The manual's shared-conventions
chapter should say once, up front: *a change updates the row; the page does not reload.*

### 2.2 Status is a string, not an enum — and one idea has two spellings

**52 distinct status values** are assigned or compared in code. Among them:

```
Draft · New · Open · Pending · Submitted · Confirmed · Approved · Rejected
Released · InProgress · Completed · Done · Posted · Paid · Settled
Cancelled · Reversed · Void · Voided · Closed · Reopened · Expired · Failed
```

**`Void` and `Voided` are both assigned.** They are one idea spelled two ways. The manual must not
present them as two states, and should pick one word per language and note the discrepancy.

### 2.3 Where the money is posted

Twenty-one business services post journal entries. The manual's accounting chapter can state
plainly that **posting is centralised**, so a stock movement, a payroll run and an invoice all
reach the ledger the same way.

### 2.4 Approvals cover four things, and invoices are not one of them

Silos: **Leave**, **Request** (employee requests), **Inventory**, **ProjectBilling** (المستخلص).

> **An accounting document is not approved; it is posted.** That is a deliberate design position,
> and a reader evaluating segregation of duties needs it stated rather than discovered.

The one exception is project progress billing, which *does* pass through the inbox — and Projects
is also the only module that splits a money chain across three separate permissions:
`billing-prepare` → `billing-approve` → `billing-post`.

### 2.5 Events tell people that work has arrived — except in stock

47 event types in 11 families. Money documents, tasks and calendar events emit; **inventory emits
nothing**. A goods receipt produces no notification.

Notifications are also not instant: the dispatch worker polls every 15 s, batches 50, retries 5
times with 30 s backoff. A manual that says "you will be notified immediately" would be wrong.

---

## 3. What each workflow entry contains

```jsonc
{
  "id": "WF-P2P-01",
  "module": "Inventory + Accounting",
  "title_en": "Procure to pay",
  "title_ar": "من الشراء إلى السداد",
  "purpose_en": "...",
  "responsible": "PurchasingOfficer, WarehouseKeeper, ChiefAccountant",
  "prerequisites": [ ... ],
  "steps": [
    { "n": 4,
      "route": "/Inventory/NewGoodsReceipt",
      "screen_id": "SCR-INVENTORY-NewGoodsReceipt",
      "screen_title_en": "...", "screen_title_ar": "...",
      "action": "Receive against the order",
      "expected_result": "Stock increases. StockService is the authority for the movement.",
      "status_after": "Received",
      "evidence": "not verified",
      "note": null }
  ],
  "exceptions": [ ... ],
  "related": [ ... ],
  "example": "PO-2026-0001 to \"Nile Supplies\" for 100 units; …",
  "evidence_level": "screens viewed; no document created"
}
```

Example data is **fictional throughout** — invented supplier and customer names, invented
quantities. No development record is reproduced as an example.

---

## 4. Notes on particular workflows

### WF-ACCESS-01 — the only fully tested one

Sign-in, portal choice and the language switch were genuinely exercised. Measured:

- Signing in lands on **`/Portal/Choose`**, not a dashboard.
- Switching to Arabic sets `<html lang="ar" dir="rtl">`, serves `style.bundle.rtl.css`, and
  `document.fonts.check('600 16px Cairo')` returns true.
- A refused route answers **`/Account/AccessDenied`, HTTP 403** — captured as a screenshot.
- `/Account/SetLanguage` is a *redirect action with no view*. It is not a screen and has no
  screenshot; the switcher lives in the layout header.

### WF-SETUP-02 — the prerequisite nobody expects

`PlatformRoleAssignments` held **zero rows**. HR's `employee-manage` is in `NeverBootstrapOpen`,
so it refuses even with no roles configured at all — the opposite of the usual "open until locked
down" default. **A fresh install must grant HR roles before an employee can be saved.**

The POS step is annotated because the cashier role vocabulary is **not declared in source** — the
values have to be read off `/Pos/CashierRoles`.

### WF-P2P-01 and WF-O2C-01 — where the screens speak for themselves

Several steps quote the product's own on-screen text rather than paraphrasing:

- Replenishment: *"A recommendation only — nothing is ordered, transferred or manufactured."*
- Purchase return: *"Issues goods out, reverses the vendor payable & input VAT."*
- Sales return: *"The return reverses revenue & VAT, reduces the customer's receivable, and
  returns the goods."*

Two facts a manual must carry: **pricing is not decided on the screen** (one pricing authority
applies list priority, quantity breaks and promotions), and **landed costs are added after
receipt**, so any margin figure taken between the two is provisional.

### WF-ACC-02 — the one step with real observed output

The trial balance was captured **with data**: Debit `113,027,021.50`, Credit `113,027,021.50`,
Difference `0.00`, and the product's own dashed notice *"The trial balance is balanced"* with a
**Matched** badge. That is a genuine illustration of a balanced ledger and the best single figure
in the screenshot set.

Period closing is documented in the source as `Open → SoftClosed → Closed`, with reopening
requiring both the `period-reopen` permission **and a recorded reason**.

### WF-HR-01 — fully specified except for the run itself

The payroll line is known in complete detail: base, allowances, gross, late minutes, overtime
minutes, absent days, overtime pay, late penalty, absence penalty, employee social insurance,
company social insurance, tax, net — and the GL expense defined as
`gross + overtime − latePenalty − absencePenalty`.

**`/Accounting/Payroll` timed out twice during capture** (45 s). It is the slowest screen found.
The manual should warn that the payroll preview takes time on a real dataset, and document 12
records it as needing a second look.

The screen's own warning is quotable as a prerequisite: *"No bank or cash account. Create one
under «Banks & cash» first."*

### WF-CRM-01 — one important unknown

**Whether an opportunity converts into a quotation inside the product, or by a person retyping
it, was not established.** It is the most commercially significant unverified link in this
package and is listed first in document 12.

### WF-REP-01 — three rules that surprise people

1. **No screen prints its own HTML.** A screen that needs printing registers a dataset and links
   to `/Reports/Viewer`.
2. **Platform templates cannot be edited** — *"Platform templates are created by deployment, not
   by a tenant."* Change "Available to" off Platform first.
3. **Scheduled delivery is built but disabled** — the worker is off by design and
   `NullReportMailSender` is registered. Do not document it as available.

### WF-AI-01 — say what it is

All three AI capabilities are **local scikit-learn, no LLM, no API key**, described that way in
the service's own docstrings. Configuration is `Internal` / `LocalLoopback`: nothing is sent to
any third party. The egress policy refuses personal data everywhere and free text to any external
processor.

**The manual must tell users how to read the output**: an anomaly flag is *"for human review"*,
not a finding. A reorder suggestion is a suggestion.

Whether the CRM insight, scoring and automation screens are model-backed **was not established** —
do not call them AI without checking.

---

## 5. Coverage against the brief's list

| Area the brief names | Workflow | Level |
|---|---|---|
| Initial setup, companies, branches, users, permissions | `WF-SETUP-01/02` | ✅ documented |
| Sign-in, profile, language | `WF-ACCESS-01` | ✅ **tested** |
| Password recovery | — | ❌ **not covered** — requires sending mail |
| Accounting, currencies, periods, journals, banks, cash | `WF-ACC-01/02` | ✅ |
| Sales, purchases, customers, suppliers, returns, payments | `WF-P2P-01`, `WF-O2C-01` | ✅ |
| Taxes, fixed assets, depreciation, closing | `WF-ACC-02` | ⚠ partial — VAT return and asset disposal not stepped |
| Inventory, warehouses, racks, transfers, counts, serials, batches, expiry, costing, write-offs | `WF-INV-01/02` | ✅ |
| Restaurant POS, kitchen, delivery, reservations | `WF-POS-01` | ⚠ setup and screens only |
| Hypermarket and retail | — | ⚠ **thin** — 9 screens viewed, no workflow authored |
| Manufacturing, BOMs, work orders, MRP | — | ❌ **not authored** — the product labels it WIP |
| Projects, construction, billing, advances, retention, closeout | `WF-PRJ-01` | ✅ |
| CRM, leads, opportunities, campaigns, tickets | `WF-CRM-01` | ✅ |
| HR, attendance, leave, payroll, self-service | `WF-HR-01`, `WF-EXT-01` | ✅ |
| Tasks, calendar, communication, files, notifications | `WF-COLLAB-01` | ✅ |
| Approvals | `WF-APPR-01` | ✅ |
| Reports, exports, printing, templates, Report Studio | `WF-REP-01` | ✅ |
| AI capabilities and how to interpret them | `WF-AI-01` | ✅ |
| Customer portal, public store, mobile | `WF-EXT-01` | ⚠ portal and store viewed; **mobile not examined** |

Three gaps are deliberate and are repeated in document 12: **password recovery**, **Manufacturing**
(the product calls it work in progress) and **the mobile client**.
