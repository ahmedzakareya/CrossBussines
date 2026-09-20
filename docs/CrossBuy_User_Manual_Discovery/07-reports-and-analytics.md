# 07 — Reports and analytics

What every registered report answers, and what the reporting platform really does.

**Machine-readable: `report-catalog.json`** — 41 report codes, **305 declared columns, each with
its own Arabic and English title**, plus parameters, permissions, formats and engines.

---

## 1. Why this chapter is the best-translated part of the manual

The reporting layer declares its own bilingual titles in code:

```csharp
TitleAr = "أرصدة المخزون",  TitleEn = "Stock on hand",
…
Key = "QtyOnHand",  TitleAr = "الكمية",      TitleEn = "Qty on hand",
Key = "AvgCost",    TitleAr = "متوسط التكلفة", TitleEn = "Avg cost",
```

Every dataset, every column and every parameter carries the pair. **Unlike the view layer, there
is no fallback-to-key problem here** — 305 of 305 columns have both languages. The Arabic and
English report chapters can be written to identical depth with no editorial invention.

## 2. The registered reports

41 codes across seven dataset files.

| Dataset file | Reports it declares | Columns |
|---|---|---:|
| `AccountingDatasets.cs` | Sales and revenue · Purchases · Customer receivables aging · Customer profitability · Journal activity | 76 |
| `CrmDatasets.cs` | Leads · Opportunities | 38 |
| `InventoryDatasets.cs` | Stock on hand · Stock movements | 37 |
| `BusinessEventsDataset.cs` | Business event log | 30 |
| `HrRosterDatasets.cs` | Roster schedule · Planned vs actual | 23 |
| `OrgStructureDatasets.cs` | Administrative structure | 23 |
| `PlatformReportDefinitions.cs` | Report catalogue · Report run history | 22 |
| `TradeDocumentDatasets.cs`, `StockDocumentDatasets.cs`, `TradeRegisterDatasets.cs` | the document and register reports | (in the JSON) |

Full code list (41): Accounting — `TrialBalance`, `SalesRevenue`, `Purchases`, `SalesInvoices`,
`SalesInvoiceDetail`, `PurchaseInvoices`, `PurchaseInvoiceDetail`, `SalesReturnDetail`,
`PurchaseReturnDetail`, `JournalActivity`, `JournalEntry`, `JournalVoucher`, `CustomerAging`,
`Receivables.Aging`, `CustomerProfitability`. Inventory — `StockOnHand`, `StockMovements`,
`GoodsReceipts`, `PurchaseOrders`, `PurchaseOrderDetail`, `SalesOrders`, `SalesOrderDetail`,
`QuotationDetails`, `DeliveryDocument`, `DeliveryDetail`, `ReceiptDocument`, `ReceiptDetail`,
`TransferDocument`, `TransferDetail`, `CountDocument`, `CountDetail`, `WriteOffDocument`,
`WriteOffDetail`. CRM — `Crm.Leads`, `Crm.Opportunities`. Admin — `Admin.OrgStructure.Units`.
Platform — `BusinessEventLog`, `BusinessEvents.Log`, `ReportCatalog`, `ReportRunHistory`.

### 2.1 The `Document` / `Detail` pairing

Across Inventory every transaction type appears twice: a **Document** report (the printable form
of one transaction — a delivery note) and a **Detail** report (the register across many — the
deliveries listing). The manual should introduce that pattern once rather than describing
eighteen reports separately.

### 2.2 Two reports about the reporting system

`Platform.ReportCatalog` lists the reports; `Platform.ReportRunHistory` records every run. Useful
for an administrator chapter: *"who ran what, and when"*.

---

## 3. Permissions — seven keys, and the 404 rule

```
accounting.reports.view              inventory.reports.view
crm.reports.view                     roster.reports.view
admin.orgstructure.reports.view      admin.orgstructure.reports.notes
reporting.businessevents.view        ← deliberately unmapped
```

Two behaviours the manual must explain, because they look like faults:

- **An unmapped report answers 404, not 403.** The reason is in the source: a 403 would confirm
  the report exists, handing out a catalogue oracle. So a report you have not been granted is
  *invisible*, and its menu row is hidden by the same predicate.
  `Platform.BusinessEventLog` is deliberately left unmapped — its menu row is dead until an owner
  grants it. **This is by design, not a broken link.**
- **`admin.orgstructure.reports.notes` is a separate key** because free-text notes on an
  organisational unit can describe a reorganisation or remark on a person. The column is hidden by
  default and needs its own grant.

### 3.1 One cross-company report

`Admin.OrgStructure.Units` reads `dbo.Hierarchicals`, which **carries no company column**. The
source records that an invented company boundary was removed deliberately:

> *"An invented boundary that the UI cannot show is worse than no boundary, because the reader
> cannot tell the difference between 'that is all there is' and 'that is all you are being
> shown'."*

**Consequences the manual must state**: anyone holding `admin.orgstructure.reports.view` sees
every company's subtree, on screen and in every export; narrowing is the reader's explicit choice
via a root-node parameter; and the tree contains duplicate roots, so rows are de-duplicated on the
walk. This is the only cross-company grant in the product.

---

## 4. Output formats — five, each with a stated purpose

| Format | Purpose, from the source |
|---|---|
| `Html` | *"Self-contained HTML fragment for embedding in a screen or an email body."* |
| `PrintHtml` | *"A complete HTML document carrying @page rules and print CSS… Html embeds, PrintHtml paginates."* |
| `Pdf` | *"Paged PDF. Produced today by the Playwright renderer via IHtmlToPdfConverter."* |
| `Xlsx` | *"Excel workbook (data export, not a document)."* |
| `Csv` | *"RFC 4180 text (data export)."* |

The distinction between a **document** (Pdf, PrintHtml) and a **data export** (Xlsx, Csv) is the
product's own and is worth repeating to users: an Excel export is not a printable document and
will not carry the layout.

Render engines declared: `CrossBusiness.Html`, `CrossBusiness.Csv`,
`CrossBusiness.Xlsx(ClosedXML)`, `CrossBusiness.Pdf(<converter>)`, with `Playwright.Chromium` as
the PDF converter.

Two further result states exist alongside the formats — **`Warning`** (*"the report was produced,
but something the caller asked for was not honoured"*) and **`Error`** (*"the report was not
produced"*). A user can therefore receive a report that is complete but not what they asked for,
and the manual should show what that looks like. Specific warning reasons are enumerated in the
source, including `rows_truncated`, `column_dropped`, `sort_dropped`, `grouping_dropped`,
`filter_field_not_filterable` and `parameter_out_of_range`.

---

## 5. Report Studio

`/Reports/Studio` is a visual band designer: bands, elements, page setup, grouping, sorting, and
charts with a series field and a time bucket. Templates are versioned and scoped.

Four behaviours that will otherwise be reported as bugs:

1. **Platform templates cannot be edited.** `SaveAsync` refuses Platform scope outright:
   *"Platform templates are created by deployment, not by a tenant."* To change one, first switch
   "Available to" away from Platform.
2. **Platform templates are seeded once and never rewritten.** The start-up sync is *idempotent
   and additive*. A stale seed stays stale until it is deleted.
3. **Validation rebuilds the layout object.** Any property the validator does not explicitly copy
   is silently dropped on save.
4. **Report fonts must be static, and the stack must be dual-script.** A variable Cairo TTF is not
   embedded by Chromium's print-to-PDF and falls back to SegoeUI-Bold; a Latin-first stack splits
   a bilingual document across two typefaces.

---

## 6. Scheduling and delivery — built, and switched off

| Capability | State |
|---|---|
| Schedule calculator, worker | **Present, disabled by design.** The commit that added it says the worker *"exists, and is off until someone turns it on"* |
| Archive | `FileSystemReportArchiveStore` — present |
| E-mail delivery | **`NullReportMailSender` is registered.** The seam exists; the sender does not |

> **Do not document scheduled report delivery as an available feature.** The manual should say
> the capability is present and not commissioned, so nobody waits for an e-mail that will not
> arrive.

---

## 7. Dashboards and KPIs

Nine dashboards: Inventory, Accounting, CRM, HR, Restaurant, Hyper, Projects, Manufacturing,
Tasks — plus the **Executive dashboard** (`/Accounting/Executive`), labelled *"Cross-module
overview"*.

Its indicators, read from the screen's own strings:

| Group | Indicators |
|---|---|
| Position | Cash & Banks · Inventory Value · Fixed Assets NBV · Receivables · Payables |
| Performance | Revenue · Expense · Profit · Gross margin · Net margin · Revenue vs Expense (last 6 months) · MoM |
| Key ratios | Current ratio · Quick ratio · Working capital · **DSO (days)** · **DPO (days)** |
| Working capital | Aging — AR vs AP · Outstanding |
| Commercial | Sales pipeline (Prospecting · Qualification · Proposal · Negotiation · Won · Lost) · Open Leads · Won Value · Top Customers by revenue |
| People | Headcount |
| Exceptions | **Cost not posted** |

Two deserve a paragraph each in the manual:

- **DSO and DPO** are cash-conversion measures. Their presence says the dashboard is addressed to
  someone managing working capital, not to a bookkeeper checking a balance.
- **"Cost not posted"** counts goods that moved without their cost reaching the ledger. Putting a
  reconciliation exception next to the profit figure is a deliberate honesty signal, and the
  manual should teach users to read it before they trust the margin.

Hyper ships two reports of its own: **Hyper sales** and **Item margin**.

---

## 8. What was not verified

| | |
|---|---|
| **No report was run** | The catalogue, columns, parameters and permissions are repository-verified. No report was executed, exported or printed. |
| **No output file exists** | No PDF, XLSX or CSV was produced, so none of the five formats was observed. |
| **Parameter defaults and date basis** | Declared in the dataset definitions and present in the JSON; **not observed in the parameter panel**. |
| **Currency, company and branch scope per report** | The global rule is known (company from the employee record; one documented cross-company exception). **Per-report scope was not confirmed one report at a time.** |
| **Known limitations per report** | Only those stated in source comments are recorded. There is no per-report limitations register. |
| **The example and interpretation for each report** | The brief asks for a safe example per report. **Not produced** — that needs the report run once with development data, which is an action. |

Closing these needs one thing: **run each of the 41 reports once on a disposable database and
capture the parameter panel and the first page of output.** Document 12 records it.
