# ADR-025 — Accounting API security policy

**Status:** Accepted (Stage 1 Hotfix A.1)
**Date:** 2026-08-04
**Depends on:** ADR-010 (session-free permission evaluation), ADR-022 (BusinessContext resolution),
ADR-023 (controlled bypass), ADR-024 (pilot isolation).
**Applies to:** `AccountingApiController` (`/api/acc`). Other API controllers are out of scope.

---

## 1. Context

Twenty-two actions, ten of them mutating, all authenticated by JWT bearer and **none authorized**. Every action took
its company from the request with `= 1` as the default. Any holder of a valid mobile token could post to the general
ledger, post a payroll run, create invoices, receipts, payments, customers and vendors — and could direct any of it
at **another company** with `?companyId=2` or `{"CompanyID": 2}`.

## 2. Decisions

### 2.1 Authorization is in-body, through a guard service — not an attribute

`IAccountingApiAuthorization` is called as the first statement of every action. `AccPermAttribute` was **not** reused,
for two independent reasons:

1. it returns `RedirectToActionResult("Index", "Accounting")` — a 302 to HTML. A mobile client following it gets a
   page with status 200 and no way to know it was refused;
2. an attribute cannot return anything to the action, and every action here needs the **validated company** to pass
   to its service. A yes/no filter would leave `dto.CompanyID` still flowing downstream — the actual vulnerability.

**It is not a parallel permission engine.** The decision is made by the same context-aware `IModuleAccessService`
(`AccountingAccessService.CanAsync(BusinessContext, action)`) the platform provider and the MVC screens use, resolved
from DI **by scope** exactly as `ModulePermissionAdapterBase` does. The guard contributes no rule: it resolves the
context, validates the company, asks, and shapes a safe answer. If the accounting module were ever unregistered it
throws at construction rather than allowing everything.

### 2.2 The permission per action is the MVC controller's, not a new judgement

Mapped from the action in `AccountingController` that performs the same operation, so a user's rights are identical
in the web UI and the API. The vocabulary is exactly `AccountingAccessService.Actions` —
**read | post | pay | manage | currency-override** — and nothing outside it exists to assign.

| Actions | Permission | Effective roles |
|---|---|---|
| `CreateJournal`, `Post`, `Reverse`, `CreateCustomer`, `CreateSalesInvoice`, `CreateVendor`, `CreatePurchaseInvoice` | `post` | ChiefAccountant, Accountant |
| `CreateReceipt`, `CreatePayment` | `pay` | ChiefAccountant, Accountant, Cashier |
| **`PayrollPost`** | **`manage`** | **ChiefAccountant only** |
| all 12 reads | `read` | anyone in the company |

`PayrollPost` is `manage` because `AccountingController.PostPayroll` is. The API must not be the looser door to the
payroll journal.

An action outside the vocabulary **denies** — it does not fall through to `read`, and it is not read as "no rule
configured, therefore allow". Logged at Error, because asking for an unknown action is a programming mistake.

### 2.3 The company comes from the context; the parameter is compatibility-only

Every `companyId` / `dto.CompanyID` is **preserved** in the signature — removing them would break existing clients —
and is **validated**, never authoritative:

* equal to the resolved company → accepted;
* different → **403, rejected**, not coerced. Silently substituting the caller's company would make a tampered
  request look like it succeeded as asked;
* absent or non-positive → treated as "not supplied"; the resolved company is used.

Defaults changed from `1` to `0`: "not supplied" must not mean "company 1".

`Post` and `Reverse` carry no company at all — they address a journal by id — so ownership is checked **explicitly**
against the validated company, and a foreign or absent id give the **same** 404, so existence cannot be probed.

### 2.4 Cross-company access is not available on this surface

`TokenService` issues no role claim, so an API caller can never satisfy `CompanyBypassPolicy`'s admin requirement.
Rather than leave that as an accident, it is the **policy**: this controller consults no bypass, and an ambient
bypass does not make a tampered company acceptable. Both are tested. A cross-company accounting API, if ever wanted,
needs its own ADR.

### 2.5 Anti-forgery is deliberately absent

The controller is `[Authorize(AuthenticationSchemes = JwtBearerDefaults...)]` — **bearer only**; a cookie does not
satisfy it, and `SessionValidationMiddleware` exempts `/api` anyway. CSRF requires an *ambient* credential the
browser attaches automatically; a bearer token is attached by client code, so a cross-site post carries none and is
simply 401. Adding `[ValidateAntiForgeryToken]` would demand a cookie-backed token no mobile client has and would
break every existing integration.

A test asserts the controller is bearer-only, so if a cookie scheme is ever added this reasoning fails loudly
instead of expiring quietly.

**Verified JWT posture** (`Program.cs:326-340`): issuer, audience, lifetime and signing key all validated;
`ClockSkew = TimeSpan.Zero`; the query-string token is accepted **only** for `/hubs`, never for `/api/acc`.

### 2.6 Errors say nothing

Two generic messages, reusing the MVC wording, in the project's existing `{ success, message }` shape — so the
response contract is unchanged and only the status code differs (**403**). No role name, no company id, no employee
id, no exception text, no stack trace. The detail goes to the **log**: a company mismatch is logged at Warning with
the actor and both companies, which is the tampering signal an auditor needs.

### 2.7 The actor is recorded

All ten actions passed `null` as `userId`, so every API-created journal was anonymous while MVC recorded `empId`.
They now pass `context.EmployeeId` — the same value MVC passes. Additive audit only; **no calculation changes**.

**Limitation, stated:** `CreateCustomerAsync` and `CreateVendorAsync` take no `userId` parameter, so no actor can be
recorded on master-data creation. Widening a service contract inside a security hotfix would be the wrong trade;
recorded as a follow-up.

## 3. What this ADR does NOT change

* **No accounting formula, posting rule or journal-building logic.** No service body was modified.
* **Payroll duplicate-post protection is untouched** — `PostPayrollRunAsync` already refuses when a `Payroll`-sourced
  journal exists for the company and period.
* **No transaction ownership moved.** The services own their `ScopedTx`; the guard runs *before* any service call, so
  a refused request begins no transaction, writes no row, posts no journal, raises no business event and sends no
  notification.
* **No other controller**, and not the 183-action backlog.

## 4. Relationship to Batch B — defence in depth, not the control

B2's filters and B4's write guard already blocked a *pilot-entity* cross-company write, and that is real defence. It
was never sufficient: `Vendor` is not a pilot entity so `CreateVendor(companyId: 2)` was unguarded; the failure mode
was an unhandled exception (500) rather than a safe 403; the reads returned **empty** rather than refusing, which
reads as "that company has no data"; and the endpoints were still unauthorized for any token holder. Every check in
this ADR is explicit and holds with the filters removed.

## 5. A defect found in a dependency, and fixed

`BusinessContextFactory.ResolveHttpAsync` read `httpContext?.Session?.GetString("Employee")`. `HttpContext.Session`
is a property whose **getter throws** `InvalidOperationException` when the session feature is absent — `?.` guards a
null context, not a throwing property. On any path where `UseSession` has not run, the factory **raised instead of
falling through to claims**, so an unresolvable request would surface as a 500 rather than a clean deny — the
opposite of failing closed, on the exact path this hotfix secures. The read is now guarded.
(`CompanyScopeMiddleware` already guarded the same call; the factory did not.)

## 6. Tests

`CrossBuy.Tests/HotfixA1AccountingApiTests.cs` — **79 tests**, all ten mutating actions and all twelve reads covered
individually, against the **real** `AccountingAccessService` over real `AccountingUserRoles` rows and a real
`BusinessContext` resolved from JWT-shaped claims.
Full suite: **492 passed / 0 failed / 0 skipped on SQL Server**.
