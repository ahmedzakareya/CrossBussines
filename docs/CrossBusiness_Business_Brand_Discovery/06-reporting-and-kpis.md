# 06 — Reporting and KPIs

What CrossBuy measures, and the platform that produces it.

---

## 1. Reporting is a platform, not a folder of reports

`BL/Reporting/` is **60 files and 25,354 lines** — larger than most modules in the product. It is
built as a registered, permissioned, multi-format pipeline rather than a set of screens that each
draw their own table.

```
 dataset  ──▶  parameters  ──▶  data source  ──▶  shaper  ──▶  renderer  ──▶  output
 (what can    (validated,        (the query,      (filter,     (layout)      (Html · Pdf ·
  be asked)    typed, bound)      company-        group,                      Xlsx · Csv ·
                                  filtered)        sort)                      PrintHtml)
                                                                    │
                                                              archive · schedule
                                                              · run history · favourites
```

Registering a new report is `services.AddScoped<IReportDataSource, XDataSource>()` plus a
definition. Everything else — export, schedule, archive, permissions, the Studio designer — is
already there.

**Five output formats**, each with a declared reason:

| Format | Purpose |
|---|---|
| `Html` | A self-contained fragment for embedding in a screen or an email body |
| `PrintHtml` | A complete document with `@page` rules — *paginates* rather than embeds |
| `Pdf` | Paged PDF, produced today by the Playwright renderer behind `IHtmlToPdfConverter` |
| `Xlsx` | Excel workbook — explicitly *"data export, not a document"* |
| `Csv` | RFC 4180 text |

Engine names are declared in the source: `CrossBusiness.Html`, `CrossBusiness.Csv`,
`CrossBusiness.Xlsx(ClosedXML)`, `CrossBusiness.Pdf(<converter>)`.

## 2. The report catalogue — 41 registered codes

| Area | Codes |
|---|---|
| **Accounting** (15) | `TrialBalance` · `SalesRevenue` · `Purchases` · `SalesInvoices` · `SalesInvoiceDetail` · `PurchaseInvoices` · `PurchaseInvoiceDetail` · `SalesReturnDetail` · `PurchaseReturnDetail` · `JournalActivity` · `JournalEntry` · `JournalVoucher` · `CustomerAging` · `Receivables.Aging` · `CustomerProfitability` |
| **Inventory** (17) | `StockOnHand` · `StockMovements` · `GoodsReceipts` · `PurchaseOrders` · `PurchaseOrderDetail` · `SalesOrders` · `SalesOrderDetail` · `QuotationDetails` · `DeliveryDocument` · `DeliveryDetail` · `ReceiptDocument` · `ReceiptDetail` · `TransferDocument` · `TransferDetail` · `CountDocument` · `CountDetail` · `WriteOffDocument` · `WriteOffDetail` |
| **CRM** (2) | `Crm.Leads` · `Crm.Opportunities` |
| **Admin** (1) | `Admin.OrgStructure.Units` |
| **Platform** (4) | `BusinessEventLog` · `BusinessEvents.Log` · `ReportCatalog` · `ReportRunHistory` |

Note the `Document` / `Detail` pairing across Inventory: a *document* report is the printable
form of one transaction; a *detail* report is the register across many. That pairing is what lets
the same platform produce both a delivery note and a deliveries listing.

`Platform.ReportCatalog` and `Platform.ReportRunHistory` are reports **about the reporting
system** — the catalogue reports on itself, and every run is recorded.

## 3. Permissions — unmapped means denied

Seven permission keys gate the catalogue:

```
accounting.reports.view
inventory.reports.view
crm.reports.view
roster.reports.view
admin.orgstructure.reports.view
admin.orgstructure.reports.notes      ← a separate, hidden-by-default column
reporting.businessevents.view         ← deliberately unmapped
```

Two details worth a business reader's attention:

- **A report whose key is unmapped answers 404, not 403.** The reason is stated in the source:
  a 403 would confirm the report exists, handing out a catalogue oracle. `Platform.BusinessEventLog`
  is deliberately left unmapped, so its menu row is hidden until an owner grants it.
- **`admin.orgstructure.reports.notes` is a separate key** because free-text notes on an
  organisational unit can describe a reorganisation in progress or a remark about a person.
  Readable with the structure itself was judged the wrong default.

## 4. Report Studio

`/Reports/Studio` is a visual layout designer: bands, elements, page setup, grouping, sorting,
charts with series and time buckets. Templates are versioned, scoped and validated.

Three behaviours a user needs to know, all read from the source:

- **Platform templates are seeded, not edited.** `SyncPlatformTemplatesAsync` seeds one platform
  template per report at start-up and is *idempotent and additive — it never rewrites*.
  `ReportTemplateService.SaveAsync` refuses Platform scope outright: *"Platform templates are
  created by deployment, not by a tenant."* To edit one you first change its "Available to" away
  from Platform.
- **Validation rebuilds the layout object.** `ReportVisualLayoutValidator` constructs a
  `new ReportVisualLayout` and copies properties across; anything not explicitly copied is
  silently dropped on save.
- **Fonts must be static, not variable.** `ReportFontLibrary` documents that Chromium's
  print-to-PDF does not embed a *variable* Cairo TTF and falls back to SegoeUI-Bold, so static
  faces are embedded instead (`Cairo-Regular.ttf`, `Cairo-Bold.ttf`, as csproj embedded
  resources). See document 08.

## 5. Scheduling, archive and delivery

Present and deliberately inert: the schedule worker *"exists, and is off until someone turns it
on"* (commit, 2026-09-17). Archive is a filesystem store (`FileSystemReportArchiveStore`); mail
delivery is registered as `NullReportMailSender` — the seam exists, the sender does not.

**For a reader evaluating the product: scheduled report delivery is built but not commissioned.**

## 6. The KPIs the product actually shows

### Executive dashboard — `/Accounting/Executive`

Labelled *"Cross-module overview"*. Measured from the view's own strings:

| Group | Indicators |
|---|---|
| **Position** | Cash & Banks · Inventory Value · Fixed Assets NBV · Receivables · Payables |
| **Performance** | Revenue · Expense · Profit · Gross margin · Net margin · Revenue vs Expense (last 6 months) · MoM |
| **Key ratios** | Current ratio · Quick ratio · Working capital · **DSO (days)** · **DPO (days)** |
| **Working capital** | Aging — AR vs AP · Outstanding |
| **Commercial** | Sales Pipeline (Prospecting · Qualification · Proposal · Negotiation · Won · Lost) · Open Leads · Won Value · Top Customers by revenue · Customer analytics |
| **People** | Headcount |
| **Exceptions** | **Cost not posted** |

Two of these deserve comment.

**DSO and DPO on the main dashboard** is not a mid-market default. Days sales outstanding and days
payable outstanding are cash-conversion measures — the dashboard is addressed to someone managing
working capital, not to a bookkeeper checking a balance.

**"Cost not posted"** is an exception counter: goods that have moved without their cost reaching
the ledger. Putting a reconciliation exception on the executive dashboard, next to the profit
figure, is a statement about how much the product trusts its own numbers — and a good one.

### Module dashboards

`Inventory/Index`, `Accounting/Index`, `Crm/Index`, `Admin/Index`, `Pos/Dashboard`,
`Hyper/Dashboard`, `Project/Dashboard`, `Inventory/ManufDashboard`, `Tasks/Reports` — nine
dashboards, each scoped to its module. `Hyper` ships two reports of its own: **Hyper sales** and
**Item margin**.

> **Runtime note.** Dashboard *values* were observed on the development database and are not
> reported here; they say nothing about the product. What was verified is that each dashboard
> renders with HTTP 200 and non-trivial content — see document 11.

## 7. The reporting rule that shapes the product

> **No screen prints its own HTML.** Printing goes through Report Studio: register a dataset,
> link to `/Reports/Viewer`.

This is why the reporting platform is 25,000 lines. It is also why the report layer is the part of
the product most likely to be sound: every printed artefact in CrossBuy goes through one
permissioned, company-filtered, versioned path, instead of 372 views each deciding for themselves.

Two consequences a reader should carry forward:

1. **Adding a printable document is a registration, not a screen.** That is a real cost advantage.
2. **A dataset defines the boundary of what can be reported.** `Admin.OrgStructure.Units` shows
   the sharp edge: it reports *less* than the screen does in one respect and *more* in another —
   the tree has no company column, so the report deliberately shows every company's subtree to
   anyone holding the permission, and says so rather than inventing a fence. Document 04 §5.
