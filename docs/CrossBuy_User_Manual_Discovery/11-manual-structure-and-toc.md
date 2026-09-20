# 11 — Manual structure and traceability

The proposed table of contents for each edition, and the rule that keeps engineering evidence out
of the published manuals.

---

## 1. The two editions

| | Arabic edition | English edition |
|---|---|---|
| Direction | RTL | LTR |
| Typeface | Cairo (Regular 400 / SemiBold 600 / Bold 700 / ExtraBold 800) | Cairo — same family, Latin letterforms |
| Cover attribution | **احمد زكريا** | **Ahmed Zakarya** *(spelling needs one word of confirmation — document 02)* |
| Portrait | `assets/owner/owner-portrait-original.png`, 1254 × 1254 | same |
| Logo | `assets/brand/crossbuy-logo-full.png` (outlined PNG, **not** the SVG — document 02 §4.4) | same |
| Tagline | المنصّة التي تربط أعمالك · منصّة واحدة. إمكانات بلا حدود. | THE CROSS-BUSINESS PLATFORM · One Platform. Endless Possibilities. |
| Footline | أعمال بلا حدود | BUSINESS WITHOUT BOUNDARIES |

**Neither cover can carry a version number or a publisher** — the product declares none
(document 01 §1.3). That needs a decision before printing.

---

## 2. Table of contents — English edition

```
Cover
   CrossBuy — User Manual
   One Platform. Endless Possibilities.
   Ahmed Zakarya  ·  portrait  ·  <version to be decided>

Front matter
   0.1  About this manual — scope, edition, what it covers and does not
   0.2  How to use it — conventions, evidence of screens, where to get help
   0.3  What is new  (only once a version exists)

Part 1 — Getting started
   1.1  Signing in, and the three sign-in routes (password, Microsoft, Google)   [WF-ACCESS-01]
   1.2  Choosing a portal — the six portals and six systems                       [/Portal/Choose]
   1.3  Choosing your language, and what changes when you do
   1.4  Your profile and settings
   1.5  Finding your way: the sidebar, the breadcrumb, the Workspace
        NOTE: the browser tab title names the LAYOUT, not the screen — navigate by heading

Part 2 — Role-based quick starts                                          ⚠ BLOCKED, document 12
   2.1  Administrator        2.5  Chief accountant
   2.2  Warehouse keeper     2.6  Sales representative
   2.3  Purchasing officer   2.7  HR manager / payroll officer
   2.4  Inventory manager    2.8  Cashier (restaurant / hypermarket)
   2.9  Employee (self-service portal)

Part 3 — Shared interface conventions
   3.1  The page: toolbar, breadcrumb, cards
   3.2  Lists: filters, searchable (select2) drop-downs, paging, export
   3.3  Icon-only buttons — the visual key  (603 of them; see document 05)
   3.4  Dialogs and confirmations — the five treatments
   3.5  Notices: information, warning, danger, success
   3.6  Changes update the row; the page does not reload
   3.7  Printing — always through the Reports viewer
   3.8  Arabic, English and French: numbers, dates, direction

Part 4 — Setting up
   4.1  Companies and branches                                            [WF-SETUP-01]
   4.2  Organisation structure, job titles, administrative bodies
   4.3  Employees
   4.4  Roles and permissions — the three layers                          [WF-SETUP-02]
        FIRST-RUN WARNING: grant HR roles before saving an employee
   4.5  Brands

Part 5 — Accounting
   5.1  Chart of accounts · 5.2 Cost centres · 5.3 Fiscal periods         [WF-ACC-01]
   5.4  Currencies, exchange rates, functional currency, FX revaluation
   5.5  Journals · 5.6 Banks and cash boxes · 5.7 Bank reconciliation
   5.8  Customers, sales invoices, receipts, sales returns                 [WF-O2C-01]
   5.9  Vendors, purchase invoices, payments, purchase returns             [WF-P2P-01]
   5.10 Statements and aging
   5.11 Tax codes, VAT returns, e-invoicing status
   5.12 Fixed assets, categories, depreciation, maintenance due
   5.13 Payroll, payslips, disbursement, payroll tax and insurance         [WF-HR-01]
   5.14 Trial balance, balance sheet, income statement, cash flow          [WF-ACC-02]
   5.15 Closing a period and the year end

Part 6 — Inventory and supply
   6.1  Categories, units, items and their tracking switches               [WF-INV-01]
   6.2  Warehouses, sections and racks, item locations
   6.3  Stock movements, balances, rack stock, batches and expiry, serials
   6.4  Purchase orders, goods receipts, landed costs
   6.5  Quotations, sales orders, deliveries
   6.6  Price lists and promotions
   6.7  Transfers, counts, write-offs, assembly, capitalising an asset     [WF-INV-02]
   6.8  Replenishment planning
   6.9  Opening balances, integrity reconciliation, approvals, settings

Part 7 — Manufacturing                                   ⚠ label as WORK IN PROGRESS
   7.1  What exists today: BOMs, work centres, work orders, production planning

Part 8 — Restaurant and retail
   8.1  Operations setup, areas, floor plan, reservations                  [WF-POS-01]
   8.2  Quick items, modifiers, payment methods, terminals
   8.3  Item sourcing — stock, kitchen, or bought in
   8.4  The cashier terminal · 8.5 Kitchen display · 8.6 Delivery board
   8.7  Working offline and reviewing sync conflicts
   8.8  Hypermarket lanes, sales and item margin

Part 9 — Projects and contracting
   9.1  Projects and activity types                                        [WF-PRJ-01]
   9.2  Advances · 9.3 Progress billing (المستخلص) · 9.4 Retention and release
   9.5  Profitability and closeout
        NOTE: prepare / approve / post are three separate permissions

Part 10 — Customers and relationships
   10.1 Accounts, contacts, leads                                          [WF-CRM-01]
   10.2 Opportunities and the pipeline · 10.3 Forecasting
   10.4 Campaigns and marketing lists · 10.5 Tickets and SLA
   10.6 Scoring, automation, custom fields

Part 11 — People
   11.1 Applications and hiring · 11.2 Contracts, documents, expiry alerts
   11.3 Leave types, policies, holidays, requests
   11.4 Attendance · 11.5 Accrual, carry-over, encashment
   11.6 Appraisal and training · 11.7 Termination and final settlement
   11.8 The employee self-service portal                                   [WF-EXT-01]

Part 12 — Working together
   12.1 The Workspace: agenda, notifications, mentions, my reports         [WF-COLLAB-01]
   12.2 Tasks, the board, templates, auto-rules
   12.3 Calendar, timeline, resources
   12.4 Chat · 12.5 E-mail · 12.6 Announcements · 12.7 File manager
   12.8 Approvals — the one inbox, four kinds                              [WF-APPR-01]
        NOTE: accounting documents are posted, not approved

Part 13 — Reports and analytics
   13.1 The Reports Center and how permissions decide what you see         [WF-REP-01]
   13.2 Running a report: parameters, filters, dates
   13.3 Reading each report — one section per registered report
   13.4 Exporting and printing: the five formats
   13.5 Designing a template in Report Studio
   13.6 Dashboards, and the Executive dashboard's indicators

Part 14 — Intelligent features
   14.1 What is and is not AI in CrossBuy
   14.2 Journal anomaly review · 14.3 Cash-flow projection · 14.4 Inventory analysis
   14.5 Reading the output responsibly — a flag is for review, not a verdict  [WF-AI-01]
   14.6 Where your data goes: nowhere outside this installation

Part 15 — Outside the company
   15.1 The client portal · 15.2 The public store
   15.3 The mobile app                                     ⚠ NOT EXAMINED — omit or mark

Part 16 — Troubleshooting                                                 [document 09]
   16.1 Messages you may see and what to do
   16.2 Access and permission problems
   16.3 Language and display problems
   16.4 When to contact an administrator

Back matter
   A  Glossary — Arabic / English / definition                            [document 10]
   B  Screen index — every screen, its route and its chapter              [screen-catalog.json]
   C  Report index                                                        [report-catalog.json]
   D  Keyboard and navigation reference                          ⚠ NOT COLLECTED
   E  Index
```

## 3. Table of contents — Arabic edition

**Structurally identical**, part for part and section for section. Only these differ:

- Direction throughout, including tables, figures and their captions.
- Screenshots come from the `*.ar.png` set.
- Where a screen has **no Arabic resource** (18 headings), the Arabic manual quotes the English
  text the screen really shows and marks it — see document 10 §4.
- Numbers use Latin digits, matching the product: the Arabic culture is deliberately mutated so
  `1,234.56` is what a user sees.

```
الغلاف
   CrossBuy — دليل المستخدم
   منصّة واحدة. إمكانات بلا حدود.
   احمد زكريا  ·  الصورة الشخصية

المقدّمة        ٠٫١ عن هذا الدليل   ٠٫٢ كيف تستخدمه   ٠٫٣ الجديد
الجزء ١   البداية
الجزء ٢   بدايات سريعة حسب الدور                     ⚠ متوقّف — راجع المستند ١٢
الجزء ٣   اصطلاحات الواجهة المشتركة
الجزء ٤   الإعداد
الجزء ٥   المحاسبة
الجزء ٦   المخزون والتوريد
الجزء ٧   التصنيع                                     ⚠ قيد التطوير
الجزء ٨   المطاعم والتجزئة
الجزء ٩   المشاريع والمقاولات
الجزء ١٠  العملاء والعلاقات
الجزء ١١  الموارد البشرية
الجزء ١٢  العمل المشترك
الجزء ١٣  التقارير والتحليلات
الجزء ١٤  المزايا الذكية
الجزء ١٥  خارج الشركة
الجزء ١٦  حل المشكلات
الملاحق    أ المصطلحات · ب فهرس الشاشات · ج فهرس التقارير · د لوحة المفاتيح · هـ الفهرس
```

> **Coverage parity is measured in functions documented, not in word count** (document 10 §10).

---

## 4. Traceability — how a manual step stays checkable

Every workflow step in `workflow-catalog.json` already carries the chain the brief asks for:

```
step  ──▶  screen_id        SCR-INVENTORY-NewGoodsReceipt
      ──▶  route            /Inventory/NewGoodsReceipt
      ──▶  screen title     ar + en, with the source of each
      ──▶  fields/actions   field-action-catalog.json, filtered by screen_id
      ──▶  screenshot       screenshot-manifest.csv, by screen_id + language
      ──▶  evidence         behaviour tested safely | runtime viewed | not verified
      ──▶  source           screen-catalog.json → source_view, controller_source
```

The join key throughout is **`screen_id`**. It appears in `screen-catalog.json`,
`field-action-catalog.json`, `workflow-catalog.json`, `screenshot-manifest.csv` and
`coverage-and-gaps.csv`, so any claim in the manual can be traced to a screen, a control, a
picture and an evidence level in one hop.

### 4.1 The two-layer rule

> **Keep engineering evidence in a separate reference layer so the published manuals stay written
> for users.**

| Layer | Contains | Who reads it |
|---|---|---|
| **Published manual** | Instructions, screenshots, glossary. No file paths, no controller names, no evidence labels, no commit hashes. | The user |
| **Reference layer** — this package | Screen ids, routes, source files, line numbers, evidence levels, gaps | The manual writer, the reviewer, the next discovery |

**Nothing from the second layer belongs in the first.** A user needs *"open Inventory → Items"*;
they never need `CrossBuy/Views/Inventory/Items.cshtml:234`. The catalogues carry both so the
writer can check a claim without the reader ever seeing the plumbing.

---

## 5. Machine-readable files, and their state

| File | Rows / size | State |
|---|---|---|
| `screen-catalog.json` | 309 screens | ✅ complete |
| `field-action-catalog.json` | 2,780 entries | ✅ complete |
| `workflow-catalog.json` | 18 workflows, 93 steps, **all routes validated** | ✅ complete |
| `role-permission-matrix.csv` | 73 rows | ✅ complete (declared only) |
| `bilingual-glossary.csv` | 4,333 keys | ✅ complete |
| `messages-catalog.csv` | 223 messages | ✅ complete |
| `report-catalog.json` | 41 codes, 305 columns | ✅ complete |
| `route-protection.csv` | 1,220 routes | ✅ complete |
| `partials-catalog.json` | 63 partials | ✅ complete |
| `screenshot-manifest.csv` | one row per capture attempt | ✅ generated from the capture logs |
| `coverage-and-gaps.csv` | one row per screen | ✅ complete |
| `evidence-register.csv` | every command and its output | ✅ complete |

## 6. Production notes for whoever typesets these

1. **Use the PNG lockup, not the SVG.** The SVG wordmark is live text in Segoe UI and will change
   shape on a machine without it (document 02 §4.4).
2. **Embed the four static Cairo faces.** A variable Cairo TTF is not embedded by Chromium's
   print-to-PDF; the product's own reporting layer records the same constraint.
3. **Keep the font stack dual-script.** A Latin-first stack splits a bilingual page across two
   typefaces.
4. **Do not use Inter.** It is not bundled, it is fetched from Google, and that fetch failed
   during this discovery. Cairo's Latin letterforms are complete and locally licensed (OFL).
5. **Amber is the brand accent *and* the warning colour** — both `#F59E0B`. Reserve it for the
   masthead; use `#92400E` for text that must actually warn.
6. **Body text colour** `#0E4A9E` for headings (8.43:1 on white) and near-black for body. Never
   small white text on `#1877F2` — 4.23:1 fails AA.
7. **Portrait and screenshots stay in separate asset folders** and must not be composited into one
   illustration.
