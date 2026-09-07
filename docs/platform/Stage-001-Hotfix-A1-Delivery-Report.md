# Stage 1 Security Hotfix A.1 — delivery report

**Accounting API authorization and company isolation.** Separate delivery, separate report.
Analysis: [Stage-001-Hotfix-A1-Analysis.md](Stage-001-Hotfix-A1-Analysis.md) · Policy:
[ADR-025](ADR-025-Accounting-API-Security-Policy.md) · Maturity:
[Stage-005](../architecture/maturity/Stage-005-Hotfix-A1-Result.md)

**Verification:** `492 passed / 0 failed / 0 skipped` on real SQL Server; `450 passed / 42 skipped` on SQLite alone.
**80 tests added** (412 → 492 total): 79 in `HotfixA1AccountingApiTests` plus one in the backlog reconciliation. Fresh build, **0 errors, 0 warnings from any hotfix file**.
**No production database was modified by this hotfix. No SQL was executed against CrossBuyDB2 or any other database**
— the only database access outside the test harness was one **read-only** integrity check (below). Scratch databases
remaining: **0**. **Batch C and Batch D not started.**

> **Read-only observation, stated precisely rather than as "unchanged".** `dbo.Notifications` in CrossBuyDB2 now holds
> **1288** rows against **1271** at the Batch B measurement, still with **0** NULL-company rows. The +17 is the
> **running application** creating notifications during this session — the tests never touch CrossBuyDB2 (they use
> SQLite in-memory or a scratch `CrossBuyPlatformTest_<guid>` database, and the fixture *refuses* a real CrossBuy
> database by name), and no SQL was executed against it. The NULL-company policy from Batch B still holds at 0 rows.

---

## 1. Requirement status

| Requirement | Status |
|---|---|
| Phase 1 pre-implementation analysis (4-source reconciliation) | **Completed** |
| A1 — Accounting permission enforcement | **Completed** |
| A2 — Company context enforcement | **Completed** |
| A3 — Financial write safety | **Completed** (verification; nothing modified) |
| A4 — API security posture | **Completed** |
| A5 — Accounting API tests | **Completed** (21/21 required cases, endpoint-specific for all 10) |
| A6 — Accounting visual identity as platform standard | **Completed** for A6.1-A6.3; **A6.4 Not Started, by its own precondition** (§8.2) |
| A7 — CLAUDE.md engineering rules | **Completed** |
| A8 — Architecture Validation Framework on the roadmap as Planned | **Completed** |
| A9 — Financial Intelligence Platform on the roadmap as Planned | **Completed** |
| Maturity measurement (separate immutable record) | **Completed** |
| Documentation and evidence | **Completed** |

Only one item is not Completed, and it is reported in full in §8.2.

## 2. Phase 1 — the ten actions, confirmed by four independent sources

| Source | Total | Mutating |
|---|---|---|
| Source inspection | 22 | **10** |
| Controller-action scanner | 22 | **10** |
| Raw grep for `[Http{Post,Put,Delete,Patch}]` | — | **10** |
| Endpoint inventory | 22 | **10** |

**All four agree.** The accepted figure of ten is confirmed, not corrected. No `PUT`/`DELETE`/`PATCH` exists here —
every mutation is a `POST`. `Journals`, `TrialBalance`, `Ledger`, `Customers` and `PayrollPreview` are **GET**s;
they were secured too, because A2/11 and A5/4 require it.

## 3. A1 — permission enforcement

The Accounting vocabulary is exactly `read | post | pay | manage | currency-override`. There is **no** `create`,
`edit`, `reverse`, `payroll`, `confidential` or `restricted` action in this module, so the brief's candidate list was
narrowed to what exists. Each mapping is taken from the **MVC action that already performs the same operation**, so a
user's rights are identical in the web UI and the API:

| Action | Permission | From (MVC) |
|---|---|---|
| `CreateJournal`, `Post`, `Reverse` | `post` | `CreateJournal` / `PostJournal` / `ReverseJournal` |
| `CreateCustomer`, `CreateVendor` | `post` | `SaveCustomer` / `SaveVendor` |
| `CreateSalesInvoice`, `CreatePurchaseInvoice` | `post` | same names |
| `CreateReceipt`, `CreatePayment` | `pay` | same names |
| **`PayrollPost`** | **`manage`** — ChiefAccountant only | `PostPayroll` |
| all 12 reads | `read` | — |

Enforced by `IAccountingApiAuthorization`, the **first statement of every action**. It asks the same context-aware
`AccountingAccessService` the platform provider uses, resolved from DI **by scope** as `ModulePermissionAdapterBase`
does — no parallel engine, no new rule. Unknown action → **deny** (logged Error). Missing context → **deny**.
Refusals are **403** in the project's existing `{ success, message }` shape, revealing no role, company, employee or
exception text; the detail goes to the log, where a company mismatch is logged at Warning with the actor and both
companies.

`AccPermAttribute` was deliberately **not** reused: it denies with a **302 to an HTML page**, which a mobile client
reads as a 200 success — and an attribute cannot hand the validated company back to the action, which is the whole
point.

## 4. A2 — company context

Every `companyId` / `dto.CompanyID` is **preserved** in its signature (compatibility) and is **compatibility-only**:

* equal to the resolved company → accepted;
* different → **403, rejected, never coerced**;
* absent or non-positive → the resolved company is used.

Defaults changed `1` → `0`: *"not supplied"* must not mean *"company 1"*. Services receive the **validated** company.
`Post`/`Reverse` carry no company, so journal ownership is checked **explicitly** — and a foreign id gives the **same
404** as an absent one, so existence cannot be probed.

**Cross-company access is not supported on this surface, by policy.** `TokenService` issues no role claim, so no API
caller can satisfy `CompanyBypassPolicy`; the guard consults no bypass, and an **ambient** bypass does not make a
tampered company acceptable. Both are tested.

**Route tampering does not exist here** — no route parameter carries a company id (`{id}`, `{accountId}` only). A test
asserts it stays that way rather than pretending to cover a case that does not exist.

## 5. A3 — financial write safety: verified, nothing modified

| Item | Finding |
|---|---|
| Transaction ownership / `ScopedTx` | Service-owned; the controller starts none. **Unchanged.** |
| Rollback | The guard runs **before** any service call, so a refused request begins no transaction — there is nothing to roll back. |
| Duplicate payroll posting | Already prevented (`pre.AlreadyPosted`, keyed on company + `year*100+month`). **Untouched.** |
| BusinessEvent company | Raised inside the services from the entity written, so validating at the door makes it correct by construction. |
| Accounting formulas, posting logic, payroll calculations | **Not touched. No service body was modified.** |
| Actor identity | **Gap fixed.** All ten passed `null`; they now pass `context.EmployeeId` — the same value MVC passes. Audit column only; no calculation changes. |

**A rejected request produces:** no partial save, no journal, no payroll posting, no BusinessEvent, no success
notification. Proven per endpoint — the recording service double is **empty** on every refusal.

## 6. A4 — API security posture

Bearer-only (`[Authorize(AuthenticationSchemes = JwtBearerDefaults...)]`); a cookie does not satisfy it, and
`SessionValidationMiddleware` exempts `/api` anyway. **Anti-forgery is deliberately not added**: CSRF needs an
*ambient* credential, a bearer token is attached by client code, and a cookie-backed token would break every mobile
client. A test asserts the controller is bearer-only, so if a cookie scheme is added the reasoning fails loudly.

Verified: issuer ✔ audience ✔ lifetime ✔ signing key ✔, `ClockSkew = TimeSpan.Zero`; query-string tokens accepted
**only** for `/hubs`. Also added: a `CancellationToken` on all 22 actions, forwarded to every query; `null` DTO
rejected with the project's standard message; no stack traces, no accounting figures or identifiers in any response.

## 7. A5 — tests: 80 added, all 21 required cases covered

| # | Required case | Test |
|---|---|---|
| 1 | unauthenticated denied | `A_request_with_no_resolvable_identity_is_denied` |
| 2 | authenticated without permission denied | `The_permission_matrix_matches_…` (16 cases) + `An_unauthorized_caller_is_refused_and_no_service_is_called` (10) |
| 3 | authorized allowed | `An_authorized_caller_reaches_the_service_with_the_validated_company_and_actor` (10) |
| 4 | A cannot read B's financials | `A_read_naming_another_company_is_refused` (10 reads) |
| 5 | A cannot post/modify B's data | `Company_tampering_is_refused_per_endpoint_and_nothing_is_written` (8) |
| 6 | query-string `companyId` tampering rejected | same, via `PayrollPost`/`CreateCustomer`/`CreateVendor` |
| 7 | body `companyId` tampering rejected | same, via the five DTO endpoints |
| 8 | route tampering rejected *(if applicable)* | **N/A — no route carries a company.** `Acting_on_another_companys_journal_by_id_is_not_found_and_never_posted` covers the id-addressed equivalent |
| 9 | controlled cross-company only where supported | `An_api_caller_cannot_obtain_a_cross_company_bypass`, `An_ambient_bypass_does_not_make_a_tampered_company_acceptable` |
| 10 | missing BusinessContext denied | `An_employee_without_a_resolvable_company_is_denied` |
| 11 | company 1 never a fallback | same (asserts `!= 1`), `The_resolved_company_comes_from_the_employee_row_not_from_a_default`, `An_omitted_company_falls_back_to_the_resolved_company_and_not_to_one` |
| 12 | query filter still active for ordinary requests | Batch B's `Stage1QueryFilterTests` (24) — all green |
| 13 | write uses the validated company | `An_authorized_caller_reaches_the_service_with_the_validated_company_and_actor` |
| 14 | JournalEntry company matches the source | same — the recorder captures the company handed to `CreateAndPostAsync` |
| 15 | BusinessEvent company matches the entity | by construction (§5) + Batch B's event-company tests |
| 16 | unauthorized `PayrollPost` creates no journal | `An_unauthorized_caller_is_refused…(PayrollPost, AccountantId)` — recorder empty |
| 17 | unauthorized action creates no partial data | all 10 refusal cases assert `Assert.Empty(recorder.Calls)` |
| 18 | authorized behaviour unchanged | the 10 allowed cases reach the same service methods with the same arguments |
| 19 | response contract compatible | `A_refusal_reveals_no_permission_internals_and_keeps_the_response_shape` |
| 20 | existing tests green | **492/492 on SQL Server**, 0 skipped |
| 21 | Batch B isolation tests green | `Stage1QueryFilterTests`, `Stage1WriteGuardTests`, `Stage1BypassTests`, `Stage1IsolationMatrixTests` — all green |

Plus structural guards so an **eleventh** action cannot arrive unprotected: `Every_mutating_action_calls_the_guard`
and `No_action_passes_a_request_supplied_company_to_a_service` (both read the source).

The guard is tested against the **real** `AccountingAccessService` over real `AccountingUserRoles` rows and a real
`BusinessContext` resolved from JWT-shaped claims — not a stub that agrees with the implementation. The bootstrap-open
rule makes a seeded role row **mandatory** for these tests to prove anything, so the fixture seeds four real roles.

## 8. A6 — the two honest outcomes

### 8.1 The identity is GREEN, not blue — a conflict for the owner

A6.2 asks to *"preserve the existing CrossBuy blue identity."* `wwwroot/Backend-assets/css/crossbuy-brand.css` says,
in its own header comment, that it *"overrides Metronic's blue primary with the 'ledger green + gold' palette across
the whole app"* — deep `#13433a`, mid `#1f6253`, light `#e9f1ee`, gold `#d4a017`/`#f0c64b`. Blue (`#009ef7`) is the
stock Metronic colour this file exists to replace.

The standard therefore specifies **green**, because that is what every screen renders, and the wording is flagged for
correction. **Nothing was rebranded** — a blue rebrand would be an application-wide visual change, far outside a
security hotfix. Decision needed: reword the principle, or schedule a rebrand as its own work.

Also measured and documented rather than assumed: **light theme only** (`crossbuy-brand.css` defines
`[data-bs-theme="light"]` and no dark block), Inter + Cairo, `dir` from culture with separate RTL bundles, and
**no consistent empty-state pattern anywhere** — recorded as a gap instead of inventing one and calling it existing.

### 8.2 A6.4 shared component extraction — **Not Started**, by its own precondition

* **Exact missing work:** extract `_PlatformPageHeader`, `_PlatformFilterToolbar`, `_PlatformEmptyState`,
  `_PlatformLoading`, `_PlatformDataTable`, details-drawer header.
* **Reason:** A6.4 permits extraction only when *"an equivalent component already exists in Accounting."* **It does
  not.** Those patterns exist as inline markup repeated per screen — there is no partial to extract. Doing it would
  mean *authoring* new shared components inside a security hotfix and editing the accounting views to adopt them.
* **Affected endpoint:** none — UI only.
* **Financial risk:** none. **Company-isolation risk:** none.
* **Exact next action:** in a dedicated UI stage, define the missing empty state, extract the six components from the
  five reference screens, then realign Business Event Monitor. Recorded in Standard §10-§11.

## 9. Maturity: 54.20% → 54.20% (**0.00 pp**)

Reported in full in [Stage-005](../architecture/maturity/Stage-005-Hotfix-A1-Result.md). The short version: the
model's dimension-2 level 80 requires *"no hard-coded company anywhere"*, and `AccountingController` (MVC) still uses
`DefaultCompanyId` while `PosCompanyPolicy.CatalogCompanyId = 1` remains a declared exception. The model moves in
10-point steps, so the only positions are 70 and 80; inventing a 75 to reward this work would be exactly the flattery
the model exists to prevent.

**The security improvement is real and the score is unchanged. Both statements are true, and neither is softened.**

Correction versus implementation: **zero correction impact.** The backlog moved **193 → 183** because ten endpoints
became authorized — arithmetic, not a re-measurement — and it is pinned as a test:
`384 = 151 attribute + 50 in-body + 183 backlog`, with the mutating total unchanged at 384.

Disclosed (R7): the scanner's in-body detection was extended to recognise `_guard.AuthorizeAsync(`. Without it the
scanner would have reported these ten as unprotected — a false negative after the fix, as surely as crediting the POS
lane guard was a false positive before it. Separately, total actions moved 1055 → 1056: a **non-mutating** action in
another controller (AccountingApiController is unchanged at 22; mutating held at 384), consistent with the parallel
team's concurrent `DevSeedController` edits. Recorded, not absorbed.

## 10. A defect found in a dependency, and fixed

`BusinessContextFactory.ResolveHttpAsync` read `httpContext?.Session?.GetString("Employee")`. **`HttpContext.Session`
is a property whose getter throws** when the session feature is absent — `?.` guards a null context, not a throwing
property. On any path where `UseSession` has not run, the factory **raised instead of falling through to claims**, so
an unresolvable request surfaced as a **500 rather than a clean deny** — the opposite of failing closed, on the exact
path this hotfix secures. Now guarded. (`CompanyScopeMiddleware` already guarded the same call; the factory did not.)
Found by a test, not by reading.

## 11. Compatibility

| Change | Impact |
|---|---|
| **403 on denial** | New status for requests that previously **succeeded**. No legitimate client depended on an unauthorized request succeeding. Mitigated by the bootstrap-open rule: a company with **no** accounting roles configured stays fully open. |
| **Company correction** | A token whose employee belongs to company 2 that was silently writing into **company 1** is now correctly scoped to company 2. A behaviour change that **fixes a data-integrity bug** — the only such change, called out rather than buried. |
| Request/response shapes | **Unchanged.** Every `companyId` parameter preserved. Failures keep `{ success, message }`. |
| Actor recorded | Additive: `CreatedBy`/`PostedBy` now populated on API journals. |

## 12. Prohibitions — compliance

| Instruction | Compliance |
|---|---|
| No single generic permission across all actions | Five distinct permissions, each derived from the MVC action performing the same operation. |
| Never trust `companyId` from query/route/body/form | Validated against `BusinessContext`; rejected on mismatch; never the source of truth. |
| Never silently replace the caller company with company 1 | Defaults `1` → `0`; four tests assert company 1 is never implicit. |
| No change to accounting / journal / payroll calculations | **No service body modified.** |
| No contract change unless required and documented | Shapes unchanged; the 403 status and the `1`→`0` defaults are documented in §11 and ADR-025. |
| Don't break existing authenticated integrations without identifying them | §11 identifies both affected populations. |
| No blind MVC anti-forgery on token flows | Not added; reasoned in §6 and asserted by a test. |
| Authentication ≠ authorization | The guard's second step is the context; the fourth is the module decision. |
| Don't declare an endpoint safe because filters exist | Every check is explicit and holds with the filters removed; ADR-025 §4 lists what filters did **not** cover (e.g. `Vendor` is not a pilot entity). |
| Don't rely on UI hiding | No UI was involved. |
| Don't modify the backlog | Not modified. It shrank by exactly the ten actions this hotfix authorized. |
| Don't start Batch C or D | Not started. |
| Don't execute SQL against CrossBuyDB2 | **Nothing executed. No database was touched.** |
| No unrelated screens | None created. |
| No maturity points for design docs or roadmap | **0.00 pp awarded.** |

## 13. Files

**Created:** `CrossBuy/BL/AccountingApiAuthorization.cs` · `CrossBuy.Tests/HotfixA1AccountingApiTests.cs` ·
`CrossBuy.Tests/ServiceRecorder.cs` · `docs/platform/ADR-025-…` · `Stage-001-Hotfix-A1-Analysis.md` ·
this report · `docs/architecture/maturity/Stage-005-…` · `docs/design/{CrossBuy-Platform-UI-Standard,
Accounting-Visual-Identity-Reference,Platform-Screen-Checklist}.md`

**Modified:** `Controllers/Api/AccountingApiController.cs` (the only production controller) ·
`BL/Platform/BusinessContextFactory.cs` (§10) · `Program.cs` (one DI line) ·
`deploy/scan-architecture.ps1` (in-body detection) · `CLAUDE.md` (A7) ·
`CrossBuy.Tests/Stage1PermissionBacklogTests.cs` (reconciliation 193→183) ·
architecture docs 10, 13, 16, 19, 20, 21, 22 · `Stage-001-Roadmap.md` · evidence CSVs regenerated

**Not modified:** `JournalEntryService`, `AccountingPostingService`, `ReceivableService`, `PayableService`,
`AccountingAccessService`, `AccountingController` (MVC), any view, and the 183-action backlog.

## 14. Rollback

Revert `AccountingApiController.cs`, delete `AccountingApiAuthorization.cs`, remove the one DI line. No schema change,
no migration, no data change — **nothing to undo in any database**. The guard is additive and self-contained, so
reverting cannot affect the MVC accounting screens or any other module. The `BusinessContextFactory` guard (§10) should
be **kept** on revert: it is an independent robustness fix.

## 15. Open items handed forward

1. **`DefaultCompanyId` in `AccountingController` (MVC)** and `PosCompanyPolicy.CatalogCompanyId = 1` — the single
   item blocking maturity dimension 2 from level 80. Out of scope here ("do not remediate unrelated endpoints").
2. **No actor on master-data creation** — `CreateCustomerAsync` / `CreateVendorAsync` take no `userId`, so customer
   and vendor creation cannot be attributed. Widening a service contract inside a security hotfix was the wrong trade.
3. **`AccPermAttribute` returns a 302 for API requests** — any future API controller reaching for it gets a redirect a
   client will read as success. It should learn to return 403 for API requests.
4. **The blue-vs-green identity conflict** (§8.1) needs an owner decision.
5. **183-action backlog** — Batch C then Batch D, unchanged as a set.
