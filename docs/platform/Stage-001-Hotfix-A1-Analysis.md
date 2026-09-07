# Stage 1 Hotfix A.1 — pre-implementation analysis

**Scope:** `AccountingApiController` only. **No other controller is touched.**
**Analysis date:** 2026-08-04 · **Evidence:** source inspection · controller-action scanner · raw grep ·
endpoint inventory. All four reconciled below.

---

## 1. Action list — reconciled across four sources

| Source | Total actions | Mutating |
|---|---|---|
| 1. Source inspection (read the file) | 22 | **10** |
| 2. Controller-action scanner (`Permission-Coverage.csv`) | 22 | **10** |
| 3. Raw grep for `[Http{Post,Put,Delete,Patch}]` | — | **10** |
| 4. Endpoint inventory (`Endpoint-Inventory.csv`) | 22 | **10** |

**All four agree: exactly 10 mutating actions.** The accepted figure of ten is confirmed, not corrected.
No `PUT`, `DELETE` or `PATCH` exists on this controller — every mutation is a `POST`.

`Journals`, `TrialBalance`, `Ledger`, `Customers` and `PayrollPreview` — named in the instruction as "known
areas" — are **GET** actions. They are read exposure, not mutations; §3 covers them.

## 2. The ten mutating actions, in full

Common to all ten: **authentication** is `[Authorize(AuthenticationSchemes = JwtBearerDefaults...)]` at class level
(bearer only — a cookie does not satisfy it); **existing permission** is `(none)`; **in-body authorization** is
`False`; **anti-forgery** is absent and **correctly so** (§5); **DbContext usage** is via the injected services,
except where noted; **rollback** is owned by the service's `ScopedTx`; **test coverage before this hotfix: none.**

| # | Action | Route | Company source | Service | Financial / journal effect | Existing perm | **Required perm** |
|---|---|---|---|---|---|---|---|
| 1 | `CreateJournal` | `POST journals` | **body** `dto.CompanyID = 1` | `IJournalEntryService.CreateDraftAsync` / `CreateAndPostAsync` | Creates a GL journal; posts it when `PostNow` | none | **`post`** |
| 2 | `Post` | `POST journals/{id}/post` | **none at all** — acts on `id` | `PostAsync` | Posts an existing draft to the GL | none | **`post`** |
| 3 | `Reverse` | `POST journals/{id}/reverse` | **none at all** | `ReverseAsync` | Creates a mirror reversing journal; also raises `JournalEntry.Reversed` | none | **`post`** |
| 4 | `PayrollPost` | `POST payroll/post` | **query** `companyId = 1` | `IAccountingPostingService.PostPayrollRunAsync` | Posts the monthly **payroll** journal (salaries/accruals) | none | **`manage`** |
| 5 | `CreateCustomer` | `POST ar/customers` | **query** `companyId = 1` | `IReceivableService.CreateCustomerAsync` | Creates a `Customer` + its AR control account link | none | **`post`** |
| 6 | `CreateSalesInvoice` | `POST ar/invoices` | **body** `dto.CompanyID = 1` | `CreateSalesInvoiceAsync` | Creates + posts a sales invoice; GL + stock-out + COGS | none | **`post`** |
| 7 | `CreateReceipt` | `POST ar/receipts` | **body** `dto.CompanyID = 1` | `CreateReceiptAsync` | Cash/bank receipt against a customer; GL | none | **`pay`** |
| 8 | `CreateVendor` | `POST ap/vendors` | **query** `companyId = 1` | `IPayableService.CreateVendorAsync` | Creates a `Vendor` + AP control link | none | **`post`** |
| 9 | `CreatePurchaseInvoice` | `POST ap/bills` | **body** `dto.CompanyID = 1` | `CreatePurchaseInvoiceAsync` | Creates + posts a purchase invoice; GL + GRNI | none | **`post`** |
| 10 | `CreatePayment` | `POST ap/payments` | **body** `dto.CompanyID = 1` | `CreatePaymentAsync` | Cash/bank payment to a vendor; GL | none | **`pay`** |

### 2.1 The permission mapping is DERIVED, not invented

The Accounting vocabulary is exactly five actions — `AccountingAccessService.Actions`:

```
read | post | pay | manage | currency-override
```

There is **no** `create`, `edit`, `reverse`, `payroll`, `confidential` or `restricted` action in this module. The
instruction's candidate list is therefore narrowed to what exists. Each mapping above is taken from the **MVC
controller that already performs the same operation**, so an authorized user's rights do not change between the
web UI and the API:

| Operation | MVC action (line) | Existing `AccPerm` | API action mapped to it |
|---|---|---|---|
| create journal (draft or post) | `AccountingController.CreateJournal` (242) | `post` | 1 |
| post an existing journal | `PostJournal` (290) | `post` | 2 |
| reverse a journal | `ReverseJournal` (314) | `post` | 3 |
| post the payroll run | `PostPayroll` (377) | **`manage`** | 4 |
| save a customer | `SaveCustomer` (592) | `post` | 5 |
| create a sales invoice | `CreateSalesInvoice` (649) | `post` | 6 |
| create a receipt | `CreateReceipt` (826) | `pay` | 7 |
| save a vendor | `SaveVendor` (879) | `post` | 8 |
| create a purchase invoice | `CreatePurchaseInvoice` (952) | `post` | 9 |
| create a payment | `CreatePayment` (1129) | `pay` | 10 |

**`PayrollPost` is `manage`, not `post`** — the MVC equivalent restricts payroll posting to `ChiefAccountant`, and
the API must not be the looser door. Effective role rights: `post` = Chief or Accountant; `pay` = Chief, Accountant
or Cashier; `manage` = **Chief only**.

## 3. Company-tampering map

| Action | Company arrives from | Tampering today | After the hotfix |
|---|---|---|---|
| 1, 6, 7, 9, 10 | **request body** (`dto.CompanyID`, default 1) | `{"CompanyID": 2, …}` writes into company 2 | body value validated against the resolved context; mismatch rejected |
| 4, 5, 8 | **query string** (`?companyId=2`) | `?companyId=2` writes into company 2 | query value validated; mismatch rejected |
| 2, 3 | **nothing** — the row is addressed by `{id}` | another company's journal can be posted or reversed by guessing its id | explicit ownership check against the validated company |
| GET 11–22 | query `companyId = 1`, or `{id}` / `{accountId}` | `?companyId=2` reads another company's ledger, trial balance, customers, vendors, aging, chart of accounts and dashboard | same validation, plus `read` permission |

**No route parameter carries a company id anywhere on this controller** — the only route parameters are `{id}`
(journal) and `{accountId}`. The instruction's "route companyId tampering" case therefore does not exist here; a
test asserts that it *stays* non-existent rather than pretending to cover it.

### 3.1 What Batch B already blocks, and why it is not enough

B2's filters and B4's write guard already stop a *pilot-entity* cross-company write: a JWT resolving company 1 that
posts `dto.CompanyID = 2` would be refused by `CompanyWriteGuardInterceptor` for `JournalEntry`, `SalesInvoice`,
`PurchaseInvoice` and `Customer`. That is real defence, and it is **not** the fix:

* it is an *incidental* effect of another batch — the endpoint is still unauthorized, and any caller with a token
  can attempt a GL post;
* `Vendor` is **not** a pilot entity, so `CreateVendor(companyId: 2)` is not guarded at all;
* the failure mode is an unhandled `CompanyWriteDeniedException` — a 500, not a safe 403;
* the reads (§3, GET rows) return an *empty* result rather than a rejection, which reads as "company 2 has no
  data" instead of "you may not ask";
* `Post`/`Reverse` rely entirely on the filter making the row invisible.

Per the instruction — *"do not declare an endpoint safe only because global query filters exist"* — every check
below is explicit and independent of the filters.

## 4. API authentication map

| Property | Finding |
|---|---|
| Scheme | `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]` — **bearer only**, class level |
| Cookie accepted? | **No.** A browser session cannot reach this controller; `SessionValidationMiddleware` also exempts `/api`, so the cookie gate never applies either way |
| Token issuance | `TokenService.CreateToken` — claims: `sub`, `ClaimTypes.NameIdentifier` (= AspNet user id), `employeeId`, `name`, `jti` |
| Validation | issuer ✔ audience ✔ lifetime ✔ signing key ✔, `ClockSkew = TimeSpan.Zero` — verified in `Program.cs:326-340` |
| Query-string token | Allowed **only** for paths starting `/hubs` (SignalR). `/api/acc` cannot pass a token in the URL |
| Role claims | **None issued.** `context.Roles` is therefore empty for every API request |
| BusinessContext resolution | Works: `BusinessContextFactory` falls through the absent session blob to `ClaimTypes.NameIdentifier` → `Employee.UserId` → the Employee row is authority for company and branch |

**Consequence, decided and documented:** because no role claim is issued, an API caller can never satisfy
`CompanyBypassPolicy`'s admin-role requirement, so **cross-company access through this API is not supported at
all**. A supplied foreign company id is rejected rather than escalated. That is the correct posture for a mobile
integration surface, and it is asserted by a test rather than left implicit.

## 5. Anti-forgery: why it is deliberately NOT added

The controller is **bearer-only**. CSRF requires an *ambient* credential the browser attaches automatically; a
bearer token is attached by client code, so a cross-site form post carries no token and is simply 401. Adding
`[ValidateAntiForgeryToken]` would require a cookie-backed antiforgery token that no mobile client possesses and
would break every existing authenticated integration — which the instruction explicitly forbids.

Per A4, this is documented rather than assumed, and a test asserts the controller is bearer-only so the reasoning
cannot silently expire if someone adds a cookie scheme later.

## 6. Financial write safety — verified, not modified

| Requirement | Finding |
|---|---|
| Transaction ownership / `ScopedTx` | Each service owns its own `ScopedTx.BeginOrJoinAsync`; the controller starts no transaction. Unchanged. |
| Rollback | Service-owned. A guard rejection happens **before** any service call, so nothing is begun, nothing to roll back. |
| Duplicate payroll posting | Already prevented: `PostPayrollRunAsync` refuses when `pre.AlreadyPosted` (a `Payroll`-sourced journal with `SourceId = year*100 + month` for that company). **No change.** |
| BusinessEvent company | Raised inside the services from the entity being written, so validating the company at the door makes the event's company correct by construction. |
| Actor identity | **Gap found.** All ten actions pass `null` as `userId`, so `JournalEntry.CreatedBy`/`PostedBy` are empty for every API write. MVC passes `empId` (lines 265/270/305). Fixed by passing `context.EmployeeId` — the same value MVC passes, so this is consistency, not a new invention. It populates an audit column and **changes no calculation**. |
| Accounting formulas / posting logic | **Not touched.** No service body is modified by this hotfix. |

## 7. Files

**Create**
* `CrossBuy/BL/AccountingApiAuthorization.cs` — the guard: resolves the context, validates the requested company,
  asks the existing context-aware access service, returns a safe JSON error or the validated company.
* `CrossBuy.Tests/HotfixA1AccountingApiTests.cs` — endpoint-specific tests for all ten, plus the reads.
* `docs/platform/ADR-025-Accounting-API-Security-Policy.md`
* `docs/platform/Stage-001-Hotfix-A1-Delivery-Report.md`
* `docs/architecture/maturity/Stage-005-Hotfix-A1-Result.md`
* the A6 UI-identity documents.

**Modify**
* `CrossBuy/Controllers/Api/AccountingApiController.cs` — the only production controller changed.
* `CrossBuy/Program.cs` — one DI registration.
* `CLAUDE.md` — A7 permanent rules.
* evidence CSVs (regenerated) + roadmap/gap documents.

**Explicitly NOT modified:** `JournalEntryService`, `AccountingPostingService`, `ReceivableService`,
`PayableService`, `AccountingAccessService`, `AccountingController` (MVC), and the 193-action backlog.

## 8. Compatibility risks

| Risk | Assessment |
|---|---|
| An integration relying on the **implicit company-1 default** | Real. A token whose employee belongs to company 1 keeps working unchanged. A token whose employee belongs to company 2 that was *silently writing into company 1* will now be corrected to company 2 — a **behaviour change that fixes a data-integrity bug**, and the only such change. Called out rather than buried. |
| An integration calling with **no accounting role** | It will now receive 403 where it previously succeeded. That is the vulnerability being fixed. Mitigated by the bootstrap-open rule: a company with **no** accounting roles configured stays fully open, so an install that never configured roles is unaffected. |
| Response contract | Unchanged on success. Failures keep the existing `{ success = false, message }` shape; only the status code differs (403 for a denial). |
| Status codes | New: **403** for denial. Previously these requests returned 200. No legitimate client depended on an unauthorized request succeeding. |
| `companyId` parameters | **Preserved in every signature** for backward compatibility, and documented as compatibility-only — validated, never the source of truth. |

## 9. Test plan

Twenty-one required cases, mapped in the delivery report. Endpoint-specific coverage for **all ten** mutating
actions plus the reads — not one representative test. SQL Server is used for the whole suite (transaction, journal
and filter behaviour), and the existing Batch A/B isolation tests must stay green.

## 10. Rollback plan

1. Revert `AccountingApiController.cs` and delete `AccountingApiAuthorization.cs`; remove the one DI line.
2. No schema change, no migration, no data change — **nothing to undo in any database**.
3. The guard is additive and self-contained: no service, no shared filter and no existing attribute is modified, so
   reverting cannot affect the MVC accounting screens or any other module.
