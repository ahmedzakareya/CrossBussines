# 11 — Role access and report exports

Two things the first pass could not answer: who can do what, and whether an export produces a
file.

---

## 1. Role provisioning — how, and within what authority

Four profiles were provisioned through the project's **own dev-only seeder**
(`GET /api/dev/identity-roles-seed`), which creates users through `RoleManager`/`UserManager` so
they sign in by the ordinary `Login → PasswordSignInAsync` path. The password is supplied per
invocation and **appears nowhere in this package or in the repository**.

| Profile | Employee | Company | Identity role | What it isolates |
|---|---|---|---|---|
| `dev.superadmin` | 2047 | 1 | SuperAdmin | the elevated path |
| `dev.auditor` | 2045 | 1 | Auditor | authorised, not an administrator |
| `dev.clerk` | 2046 | 1 | **none** | refusal on ROLE alone |
| `dev.otherco` | 2048 | **65** | Auditor | refusal on COMPANY alone |

Sign-ins were genuine: each session greeted a different name — *Dev SuperAdmin*, *Dev Auditor*,
*Dev Clerk*, *Dev Other Co*.

## 2. What the read probe found — and why it is not the whole answer

Twelve accounting routes, four profiles:

| Profile | Allowed | Denied |
|---|---:|---|
| `dev.superadmin` | 10 / 12 | `/Accounting/AccountingRoles` |
| `dev.auditor` | 10 / 12 | `/Accounting/AccountingRoles` |
| **`dev.clerk` (no roles)** | **10 / 12** | `/Accounting/AccountingRoles` |
| **`dev.otherco` (company 65)** | **10 / 12** | `/Accounting/AccountingRoles` |

Every profile — including one with no roles at all — can **open** the trial balance, the journal
register, the create-journal form, payments, receipts, fiscal periods and the year-end screen.
Only the role-management screen refuses.

**Reaching a screen is not the same as seeing data or being able to write**, and the write probes
show the difference.

## 3. The write and data probes — the real answer

| Question | Profile | Result |
|---|---|---|
| Does the other-company user see company 1's figures? | `dev.otherco` | **No.** Trial balance renders **0.00 / 0.00 with 0 account rows** |
| What does an authorised user see? | `dev.superadmin` | **113,396,869.64**, 38 account rows |
| Can a user with no roles post a journal? | `dev.clerk` | **No.** The form opens, the post is refused, and **0 journal entries were created** |

So the **company boundary holds** and the **write gate holds**. `AccountingController` carries
`private const int DefaultCompanyId = 1`, which might suggest otherwise — but the observed
behaviour is that a company-65 user sees an empty ledger, not company 1's. **Observation beats
inference, and it is recorded as observed.**

**What is genuinely open is read-screen reachability.** For a guide this matters: a warehouse
clerk can navigate to the year-end close screen. They cannot use it, but they can see it.

## 4. Segregation of duties — tested with a positive threshold

Baseline `ApprovalThreshold = 0.00`, under which the control **cannot fire**. It was raised to
**1,000.00** through the supported settings screen (`/Accounting/SaveAccSettings`), exercised,
then **restored to 0.0000**. No source code was changed.

| Test | Result |
|---|---|
| Admin creates a **2,500.00** entry and presses **Post** | **Forced to Draft**, no entry number — the `forcedDraft` rule (`if (post && threshold > 0 && total >= threshold) { post = false; }`) |
| The **creator** then tries to post their own above-threshold draft | **REFUSED** — still `Draft`, `EntryNo` null, `PostedBy` null |
| `dev.clerk` tries to save a 3,000.00 draft | refused, as in §3 |

**Both halves of the control are proven**: an above-threshold entry cannot be posted on creation,
and its creator cannot approve it afterwards.

> **The baseline hides this entirely.** With the threshold at 0, every entry posts on the first
> press and the creator restriction never runs. A guide that describes CrossBuy's segregation of
> duties without saying *"only when ApprovalThreshold is greater than zero"* would be describing
> a control the reader does not have.

## 5. The eight role profiles the brief asked for

| Profile asked for | Tested | Evidence |
|---|---|---|
| Auditor | ✅ **as an identity role** | `dev.auditor`, 12 probes |
| (elevated) chief accountant | ⚠ **partially** — `admin` holds the `ChiefAccountant` module role and was used throughout; a *separate* chief-accountant login was not created | module role table + the SoD test |
| Accountant | ❌ not provisioned | the seeder creates four fixed profiles; an `Accountant` identity role exists in the permission map but no user holds it |
| Cashier, Purchasing, Sales, Warehouse, Payroll | ❌ **not tested** | these are module/POS roles (`WarehouseKeeper`, `PurchasingOfficer`, `SalesRep`, `PayrollOfficer`, cashier roles) assigned per employee on each module's roles screen; no test user was given them |

**Four of eight profiles are covered.** The remaining four need a module-role grant per employee
and a sign-in each — roughly two hours, and not done.

---

## 6. Report exports — actual files, not buttons

### 6.1 In-module exports — 8 of 8 downloaded and verified

Every file was downloaded, opened as a ZIP/OOXML package, its rows counted and its content
searched for the records this discovery created.

| Export | Bytes | Data rows | Opens | Contains this pass's records |
|---|---:|---:|---|---|
| `journals` | 458,180 | **11,808** | ✅ | `PI-2026-05073`, `PI-2026-05074`, `SV-2026-17613`, `ZZ-DISCOVERY` |
| `sales-invoices` | 235,131 | 5,503 | ✅ | `SV-2026-17613` |
| `receipts` | 132,504 | 4,427 | ✅ | `RC-2026-15485`, `RC-2026-15486` |
| `purchase-invoices` | 10,314 | 119 | ✅ | `PI-2026-05073`, `PI-2026-05074` |
| `customer-analytics` | 10,936 | 43 | ✅ | `ZZ-DISCOVERY` |
| `customers` | 8,332 | 44 | ✅ | `ZZ-DISCOVERY` |
| `vendors` | 7,276 | 12 | ✅ | `ZZ-DISCOVERY` |
| `payments` | 6,948 | 7 | ✅ | `PY-2026-00008`, `PY-2026-00009` |
| | | **21,963** | **8/8** | **8/8** |

All are **.xlsx** (ClosedXML). Four carry Arabic text (party names) and render it in the shared
string table — so Arabic survives the export.

**Every document created in §3 and §4 of document 10 is findable in its corresponding export.**
That is a complete trace from executed transaction to delivered file.

### 6.2 Platform exports — PDF, XLSX and CSV all produced

The in-module exports are xlsx only. **PDF is offered solely by the reporting platform**, whose
renderer is Playwright/Chromium — so whether a PDF can be produced at all was a real question.

Against `Accounting.JournalActivity`:

| Format | Result | Verified |
|---|---|---|
| **Csv** | 32,681 B | UTF-8 with BOM, correct header row, **contains this pass's entries** |
| **Xlsx** | 21,929 B | valid OOXML package |
| **Pdf** | **64,980 B** | `%PDF-1.4`, **17 pages**, terminates with `%%EOF`, subset fonts embedded |

The CSV's first data rows are this discovery's own postings — the SoD draft and the FX
revaluation — so the export reflects the executed examples, not stale data.

### 6.3 Two findings from the exports

**The PDF embeds Dubai, not Cairo.** Fonts found: `AAAAAA+Dubai-Bold`, `BAAAAA+Dubai-Regular`.
The reporting layer's `ReportFontLibrary` embeds static Cairo faces precisely so that PDFs render
in Cairo — the produced file does not use them. For a guide that specifies Cairo throughout, this
is a real discrepancy and belongs in the brand section, not buried here.

**`Accounting.TrialBalance` is not a registered platform report.** The first PDF attempt returned
**404**. The Reports Center lists 14 viewer links and 47 cards; the registered accounting reports
are `SalesRevenue`, `Purchases`, `CustomerAging`, `CustomerProfitability`, `JournalActivity` and
`JournalVoucher`. **The trial balance exists only as an in-module screen** and cannot be exported
through the platform. An earlier list in this package included `Accounting.TrialBalance` among
the report codes; that came from a string match in source and is **corrected here**.

## 7. Report status matrix

Execution, reconciliation and export are separate measures with separate denominators.

| Report | Executed | Reconciled | PDF | Spreadsheet | CSV |
|---|---|---|---|---|---|
| Trial Balance (in-module) | ✅ en+ar | ✅ to the ledger | ❌ **unsupported** — not a platform report | ❌ | ❌ |
| Balance Sheet | ✅ en+ar | ⬜ not cross-footed | ❌ unsupported | ❌ | ❌ |
| Income Statement | ✅ en+ar | ⬜ | ❌ unsupported | ❌ | ❌ |
| Cash Flow | ✅ en+ar | ⬜ | ❌ unsupported | ❌ | ❌ |
| AR Aging | ✅ en+ar | ✅ **bridge closes exactly** | ❌ unsupported | ❌ | ❌ |
| AP Aging | ✅ en+ar | ✅ **bridge closes to 74.20** | ❌ unsupported | ❌ | ❌ |
| Journal register | ✅ en+ar | ✅ Dr = Cr | ❌ | ✅ **tested** (11,808 rows) | ❌ |
| Customer / Vendor statement | ✅ en+ar | ⬜ | ❌ | ❌ | ❌ |
| `Accounting.JournalActivity` (platform) | ✅ | ⬜ | ✅ **tested, 17 pp** | ✅ **tested** | ✅ **tested** |
| Sales invoices / Purchase invoices / Receipts / Payments / Customers / Vendors / Analytics | ⬜ list screens | ⬜ | ❌ | ✅ **tested** | ❌ |

**Denominators, stated plainly:**

- **Reports executed and captured in both languages: 9 of 9** in-module + the platform catalogue.
- **Reconciled: 5 of 10** checks pass, **0 fail**, **5 not tested**.
- **Exports produced and verified: 11 files** — 8 in-module xlsx + 3 platform (PDF, XLSX, CSV).
- **PDF tested: 1 report.** Every other report either has no platform registration or was not
  attempted.
