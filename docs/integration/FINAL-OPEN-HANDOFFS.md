# CrossBusiness — Open Handoffs After Central Convergence

Only genuinely remaining work. Anything integrated and proven is in `FINAL-CONVERGENCE-STATE.md`
and is deliberately absent here.

---

## A. Engineering handoffs

### A1. Document lifecycle reporting datasets — **ownership-blocked**
`documents.expiring`, `documents.expired`, `documents.renewal-status`, and unresolved document
follow-up.

`CrossBuy/BL/Reporting/**` is **TAB-2's**. TAB-3 could not write them and neither can the
Integration Owner without taking a surface the registry assigns elsewhere.

**The constraint that must travel with them:** they consume `PlatformDocumentService.EvaluateAt`.
A dataset with its own `WHERE ExpiryDate < GETDATE()` disagrees with the platform on both boundaries
this work exists to get right — expiring *today* is still valid, and the warning window belongs to
the document's **type**, not to a constant.

### A2. Accounting period reporting datasets — **ownership-blocked, same surface**
`accounting.period-status`, `accounting.period-readiness`, `accounting.period-history`. Carried by
the Accounting foundation, never built. Verified absent on the converged tree.

### A3. `AccountingPeriod.Closed` / `AccountingPeriod.Reopened` events — **not implemented**
Verified absent. `FiscalPeriod` is **not** an `EntityRegistry` code, and one must not be invented to
manufacture events — the addressing scheme is the platform's decision, not an event's convenience.

### A4. Period Close workspace / dashboard — **P1, carried**
`AccountingController.Periods` + `Periods.cshtml` remain the governed invocation path and are
sufficient. Not a landing blocker.

### A5. Communication F-1 — **status unprovable from the repository**
No `F-1` marker exists in code or docs; the only textual match is a PDF version string. This handoff
**cannot be closed on the available evidence**, and it is not being claimed closed.

`IWorkspaceNotificationAuthority` **does not exist** under that name. What exists is
`IWorkspaceNotificationSource` plus `ICommNotificationDispatcher` / `ICommNotificationService`.
There is therefore no *second* notification authority — the constraint holds by nothing having been
added. `CommMessages.ClaimedAt` is present on the model and documented as the stale-claim clock.

**What the next batch needs from the owner:** what "F-1" names. Without that, closure is a guess.

### A6. Live company-1 constants — **pre-existing, none introduced here**
13 files still carry a live constant. Verified: **none** was touched by this convergence.

| File | Constant | References |
|---|---|---|
| `BL/InventoryApprovalService.cs` | `CompanyId` | 14 |
| `BL/PosAccessService.cs` | `CatalogCompanyId` | 3 |
| `BL/PosSetupService.cs` | `PosCompanyId` | 5 |
| `Controllers/AccountingController.cs` | `DefaultCompanyId` | 190 |
| `Controllers/AdminController.cs` | `HrCompanyId` | 25 |
| `Controllers/Api/InventoryApiController.cs` | `CompanyId` | 9 |
| `Controllers/BrandController.cs` | `DefaultCompanyId` | 6 |
| `Controllers/CurrencyController.cs` | `DefaultCompanyId` | 8 |
| `Controllers/HomeController.cs` | `StoreCompanyId` | 2 |
| `Controllers/HyperController.cs` | `PosCompanyId` | 9 |
| `Controllers/HyperPosController.cs` | `PosCompanyId` | 36 |
| `Controllers/PosAppController.cs` | `PosCompanyId` | 74 |
| `Controllers/StoreController.cs` | `StoreCompanyId` | 2 |

Some are **by design** and say so (`CatalogCompanyId`, `PosCompanyId` — the POS catalogue and cash
accounts live under company 1 while branches sit under 65–79). The rest are genuine multi-tenancy
debt. §10 explicitly forbids turning convergence into that rewrite, so they are recorded with their
exact reference counts rather than half-fixed.

### A7. Legacy HR expiry notifier coexists with the platform
`HrDocumentService.NotifyExpiringAsync` and `AdminController.DocExpiryAlerts` / `NotifyExpiring`
operate on the **legacy** HR document tables — a different store from `PlatformDocument`. They are
hand-triggered, carry no lead-time policy and **no idempotency**: every click re-notifies everyone.

A deployment using both stores has two expiry mechanisms with different behaviour until the legacy
documents are migrated. This is a product fact, not a defect introduced here. The platform side is
the one with a policy, an idempotency key and an audit trail.

### A8. 106 test files are not in the repository
`CrossBuy.Tests` holds **226** `.cs` files on the integration machine and **120** in HEAD. A clean
clone runs 2540 tests; this machine's tree has almost twice the files.

This is the concrete form of the "work existed on another machine and was absent in a later clone"
symptom. Every figure in `FINAL-CONVERGENCE-STATE.md` is deliberately from the clean-clone suite,
because that is the only honest control — but the gap itself is real debt and someone has to decide,
file by file, which of the 106 are worth committing and which are scratch.

Not caused by this convergence; not repairable inside it without committing 106 files nobody
reviewed.

---

## B. Product decisions

1. **Legacy HR document migration** onto `PlatformDocument` — the only thing that ends A7.
2. **Report Studio next phase** — still product/ownership gated. Not started; integration success
   does not require inventing it.
3. **Brand / Facebook Blue rollout** — a live branch with three open ownership handoffs
   (`_blue-B-TAB2-reporting`, `_blue-C-TAB6-workspace`, `_blue-D-SHARED-integration-owner`). Needs
   its owner to declare it complete before any integration decision is meaningful.

---

## C. Ownership blocks

| Item | Blocked surface | Owner |
|---|---|---|
| A1, A2 | `CrossBuy/BL/Reporting/**` | TAB-2 |
| A7 migration | legacy HR document tables + `Views/Admin/HrDocuments.cshtml` | TAB-2 |
| Brand rollout | `Views/Reports/**`, `Views/Project/Billing.cshtml`, shared layouts | TAB-2 / TAB-6 / Integration Owner |

---

## D. External / business / legal blockers

These are **not** engineering failures and must never be reported as code state. None is satisfied
and none is faked.

* ZDR grant
* DPA
* required signatures
* AccountOwner
* LegalEntity

AI **engineering** readiness is separate and is green: DI registrations present exactly once each
(the two `IAiProjectionBuilder` registrations are a deliberate multi-implementation collection), the
EF model registers `AiProjection` and `AiEgressAudit` with both entity configuration and DbSet, and
no shadow registration exists.

**Known externally gated tests, by exact name** — these are the 6 skips in every run, and they are
skips for an external dependency, not disguised failures:

```
CrossBuy.Tests.AiRealPythonServiceE2ETests.Inventory_analysis_reaches_the_real_python_ml
CrossBuy.Tests.AiRealPythonServiceE2ETests.Cashflow_forecast_reaches_the_real_python_ml_and_returns_a_sane_projection
CrossBuy.Tests.AiRealPythonServiceE2ETests.Journal_anomaly_reaches_the_real_python_ml_and_flags_an_outlier
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_real_service_rejects_a_malformed_payload
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_real_service_refuses_a_missing_or_wrong_shared_secret
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_same_free_text_payload_is_refused_to_an_external_processor
```

There is no `manifest.json` in this repository under that name; the governed equivalent is
`governance/registry/manifest-facts.json`, which is generator output and is now tracked.

---

## E. Future phases

* Documents: migrate the legacy HR store, then retire the legacy notifier (A7).
* Accounting: AR Receipt Reversal and Project Billing Financial Reversal. Both were correctly kept
  out of the convergence — neither has an approved candidate awaiting landing. Both can now consume
  `ResolveCompensatingPostingDateAsync`, which is landed and project-agnostic.
* Reporting: A1 and A2 once TAB-2's surface is available.
* Multi-tenancy: the A6 list, as its own bounded batch.
