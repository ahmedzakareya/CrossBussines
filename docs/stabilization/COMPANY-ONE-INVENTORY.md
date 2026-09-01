# Company-1 Inventory

Re-measured on the stabilization candidate, comments stripped so the documentation of past fixes is
not counted as debt. The convergence report said 13 files; the honest current figure is **17 sites
across 16 files** — 14 declared constants and 3 files carrying company parameter defaults. The
earlier count missed the parameter-default form, which `Correction005StructuralTests` explicitly
notes is invisible to a declaration scan.

**One was a DEFECT and is fixed in this batch** (`InventoryApprovalService`, no longer in the list).

| # | File | Member | Uses | Class | Evidence |
|---|---|---|---|---|---|
| 1 | `BL/Platform/CertificationDataContract.cs` | `CertificationCompanyId` | 0 | **DESIGN-INTENTIONAL** | Named for what it is: the certification fixture's company. Referenced nowhere in production flow. |
| 2 | `BL/PosAccessService.cs` | `CatalogCompanyId` | 2 | **DESIGN-INTENTIONAL** | The POS *catalogue* tenant. Documented in the file: branches sit under 65–79 while the catalogue and cash accounts live under company 1. |
| 3 | `BL/PosSetupService.cs` | `PosCompanyId` | 5 | **DESIGN-INTENTIONAL** | Same product decision, same documentation. |
| 4 | `BL/Uat/UatDatasetSeeder.cs` | `PrimaryCompanyId` | 1 | **TEST-ONLY** | UAT dataset seeder. Guarded by the same suite that refuses production-looking database names. |
| 5 | `BL/BrandService.cs` | parameter default | 1 | **DESIGN-INTENTIONAL** | Branding is a single-tenant concept in this product. |
| 6 | `Controllers/AccountingController.cs` | `DefaultCompanyId` | **185** | **UNKNOWN** | The largest surface by far. Some paths already resolve through `IRequestCompanyResolver`; 185 references cannot be classified without reading each. Not touched here. |
| 7 | `Controllers/AdminController.cs` | `HrCompanyId` | 24 | **UNKNOWN** | Mixed HR / org-administration surface (SHF-27). |
| 8 | `Controllers/Api/DevSeedController.cs` | parameter defaults | **208** | **DEV-ONLY** | `[DevOnly]` — 404 outside Development. The remediation that named the intent (`DevSeedFixture.DefaultCompanyId`) exists but is **untracked**; see MISSING-TEST-INVENTORY E3. |
| 9 | `Controllers/Api/InventoryApiController.cs` | `CompanyId` + param | 10 | **UNKNOWN** | An API surface; needs the same per-endpoint reading as #6. |
| 10 | `Controllers/BrandController.cs` | `DefaultCompanyId` | 6 | **DESIGN-INTENTIONAL** | Consistent with #5. |
| 11 | `Controllers/CurrencyController.cs` | `DefaultCompanyId` | 8 | **UNKNOWN** | Currency setup may or may not be per-tenant here. |
| 12 | `Controllers/HomeController.cs` | `StoreCompanyId` | 2 | **DESIGN-INTENTIONAL** | The public storefront's tenant. |
| 13 | `Controllers/StoreController.cs` | `StoreCompanyId` | 2 | **DESIGN-INTENTIONAL** | Same. |
| 14 | `Controllers/HyperController.cs` | `PosCompanyId` | 9 | **DESIGN-INTENTIONAL** | POS catalogue tenant, as #2/#3. |
| 15 | `Controllers/HyperPosController.cs` | `PosCompanyId` | 36 | **DESIGN-INTENTIONAL** | Same. |
| 16 | `Controllers/PosAppController.cs` | `PosCompanyId` | 74 | **DESIGN-INTENTIONAL** | Same. |

**Totals:** DESIGN-INTENTIONAL 10 · TEST-ONLY 1 · DEV-ONLY 1 · DEFECT 0 remaining (1 fixed) ·
UNKNOWN 4.

## The defect — real, and fixed by its owner, not by this batch

`BL/InventoryApprovalService.cs` — a hardcoded company across **twelve** call sites, five of them
writes: the approval row, and on approval a purchase order, a stock transfer, a stock count and a
write-off. Registered in `Program.cs`, injected into `InventoryController`: a production HTTP path.
An inventory manager in company 41 who approved a write-off created it in **company 1's** stock and
ledger.

It hid well because half the class had already been migrated — `ApprovalInboxAsync` takes a
`BusinessContext` — so the file *read* as company-aware.

This batch wrote a fix, then **withdrew it**. Fast-forwarding surfaced that the file is dirty in the
shared tree and that another tab had already fixed the same defect, independently and better.

Where mine resolved every path through `IRequestCompanyResolver`, theirs splits the two directions:
reads and new submissions resolve from the `BusinessContext` and **throw** when it is unresolved,
while **approve/reject use the approval row's own `CompanyID`**. That second half is the part I had
not thought through — the payload was captured under that company's warehouses and vendors, so
replaying it against a resolved-now company would be a cross-company command execution. Using the
row also makes the change provably behaviour-preserving on existing data.

The fix is therefore **complete and awaiting landing as foreign WIP**, not open. What this batch
contributes is the ownership: `SHF-32` gives the file an owner it never had.

## The four UNKNOWNs

Deliberately not reclassified on a guess. Between them they carry **228 references**, and §10 of the
convergence brief and §8 of this one both forbid turning verification into a multi-tenancy rewrite.
Each needs a per-endpoint reading of whether a resolved company is already in scope — a bounded
batch of its own, listed in the final report as remaining debt.
