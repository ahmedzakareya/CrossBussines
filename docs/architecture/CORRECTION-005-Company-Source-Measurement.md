# CORRECTION-005 — The authorization metric measured WHO, never WHICH TENANT

**Status:** accepted and implemented as the first Wave 1 change (Stage 1 Batch D1).
**Supersedes:** nothing. **Corrects:** the interpretation of the "151 attribute-protected" figure reported from
Hotfix A.1 onward, including in the Batch B, C and C.1 delivery reports.

---

## 1. The defect in the measurement

The permission-coverage metric classifies a mutating action as protected when it carries a module-permission
attribute or a verified in-body access-service check. That question is *"is a role checked?"*.

It never asked *"where did the company come from?"*

Twelve controllers hold the company as a **compile-time constant**:

| Constant | Controllers |
|---|---|
| `DefaultCompanyId = 1` | `Accounting`, `Brand`, `Crm`, `Currency`, `Inventory`, `Pos`, `Project`, `Tasks` |
| `HrCompanyId = 1` | `Admin` |
| `PosCompanyId = 1` | `Hyper`, `HyperPos`, `PosApp` |
| `CompanyId = 1` | `Api/InventoryApi` |

**13 declarations, 931 references.** And the blast radius is not confined to the backlog:

```
242 of 388 mutating actions (62%) live in the 8 DefaultCompanyId controllers
  150  attribute-protected   ← ALL 150 attribute-protected mutating actions in the platform
    1  in-body protected
   91  backlog
```

## 2. Why this is not a lesser version of a known problem

The project already forbids trusting a request-supplied `companyId`. This is the same class of defect **one step
earlier**, and it is worse in one specific way: a request-supplied company can be *validated against the caller*.
A compile-time company cannot be wrong-for-this-caller, because it was never about the caller at all.

On a single-company install the constant happens to be correct, so nothing is observably broken. On a multi-company
install, an employee of company 2 either operates on **company 1's** data or is denied everything — and the role
check above it passes identically in both cases. A role check over a literal company is a control over *who*, with
no control over *which tenant*.

It also contradicts two rules already on the books: *"never use CompanyID = 1 as an implicit fallback"* and *"an
unresolved company scope reads no company-scoped data and writes none. Fail closed; never default to a company."*
The rules were right; the metric could not see the violation.

## 3. What the figure actually means

**"151 attribute-protected" is honest about role checks and silent about company source. It therefore overstates
real protection.** No claim may be made that those 151 endpoints are company-safe until each one's company source
is remediated. That is stated in the Wave 1 report and is a standing constraint on every later report.

## 4. What was implemented

`CrossBuy/BL/Platform/RequestCompanyResolver.cs` — `IRequestCompanyResolver.ResolveAsync(requestSuppliedCompanyId?)`:

1. resolves the company from the **`BusinessContext`** (via `IBusinessContextAccessor.TryGetCurrentAsync`);
2. **fails closed** when nothing resolves — returns `Unresolved`, with `CompanyId == 0`, never 1;
3. fails closed when a context resolves but carries **no company**;
4. treats any request-supplied company as **compatibility only**: validated against the resolved company, and a
   mismatch is returned as `CompanyMismatch` — **refused, never coerced**, because coercing it to the caller's own
   company would silently carry out a different operation than the one requested;
5. returns a **result type, not an `int`** — a method returning `int` invites `?? 1`, and the failure modes are
   distinct outcomes a caller must handle differently, not shades of "no company".

It resolves the company. It does **not** decide permissions — those stay in the access services.

## 5. Scope boundary — deliberately not a sweep

Repointing 931 references would touch calculation-bearing code in the two largest financial controllers and is its
own batch with its own regression surface. The brief forbids a blind sweep in Wave 1. So:

* every endpoint **Wave 1 remediates** gets a validated company source;
* the constant count is made **visible and monotonic** by `Correction005StructuralTests`, which pins the 13
  declaring files by name and fails both when a **14th appears** and when one is **removed without the report being
  updated** — so code and documentation move together;
* per-endpoint claims stay narrow. Wave 1 asserts *"`AccountingController.StampInvoiceCustomer` no longer reads the
  constant"* — **not** *"`AccountingController` is company-safe"*, which would be false: that controller still holds
  the constant for its other 198 references.

## 6. Remaining inventory (the exact future remediation list)

| Controller | Constant | References | Wave |
|---|---|---|---|
| `InventoryController` | `DefaultCompanyId` | 267 | future (Inventory) |
| `AccountingController` | `DefaultCompanyId` | 198 remaining | Wave 1 continuation |
| `CrmController` | `DefaultCompanyId` | 97 | future (CRM) |
| `ProjectController` | `DefaultCompanyId` | 83 | Wave 1 continuation / W3 |
| `PosController` | `DefaultCompanyId` | 40 | Wave 1 continuation / W6 |
| `TasksController` | `DefaultCompanyId` | 33 | W4 |
| `CurrencyController` | `DefaultCompanyId` | 9 | future |
| `BrandController` | `DefaultCompanyId` | 7 | W6 |
| `AdminController` | `HrCompanyId` | — | Wave 1 continuation / W2 |
| `HyperPosController` | `PosCompanyId` | — | sanctioned POS scope, reviewed per endpoint |
| `HyperController`, `PosAppController` | `PosCompanyId` | — | sanctioned POS scope |
| `Api/InventoryApiController` | `CompanyId` | — | future (API wave) |

**Count after Wave 1's first two endpoints: still 13 declarations.** Neither remediated endpoint's controller could
drop its constant, because both controllers use it elsewhere. The count falls only when a controller's **last**
reference is repointed — which is the honest unit of progress and is why the count, not the endpoint tally, is what
this correction pins.

## 7. The POS exception is not a precedent

`HyperPosController`/`PosAppController`/`HyperController` resolve lane identity from a session blob whose roles come
from `BranchUserRoles` — the **documented permanent POS exception**. It is used inside the POS lane and is
deliberately **not** copied into accounting or HR. `HyperPosController.StampInvoiceCustomer` uses it;
`AccountingController.StampInvoiceCustomer` resolves a real `BusinessContext` instead. Same action name, two
different identity models, on purpose.

## 8. Evidence

`Correction005StructuralTests` (5 tests) · `D1Wave1CompanySourceTests` (18 tests) · suite **668 total, 622 passed,
0 failed**, three consecutive full passes with identical results.
