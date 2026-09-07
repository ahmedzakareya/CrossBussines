# Stage R1 — Reporting Authorization Surface Evidence

**One authority added to the declared surface, through the documented process, after verifying it is real.**

`IReportAuthorizationService` / `ReportAuthorizationService` were added to `AuthorizationSurface.AuthorityTypes`. **No suppression, no baseline addition, no controller exception, no endpoint whitelist.**

---

## 1. What was verified before anything was declared

The rule this follows is CORRECTION-004: *never credit an authority that checks no role.* A surface that says "authorized" where no decision is taken is worse than no surface, because it converts an unknown into a false assurance. So the seam was read before it was declared.

| Property required | Evidence in `CrossBuy/BL/Reporting/` |
|---|---|
| **A single authorization seam exists** | `IReportAuthorizationService` — `AuthorizeReportAsync`, `AuthorizeTemplateAsync`, `FilterVisibleAsync`, `IsAdministratorAsync`. The file states the design intent: *"there is ONE seam, IReportPermissionEvaluator"*. |
| **Fails closed on unresolved company** | `RoleMapReportPermissionEvaluator.HasPermissionAsync` → `if (context.CompanyId <= 0) return false`. `ReportTemplateService.SaveAsync` refuses with `CodeCompanyUnresolved` before touching a row. |
| **Enforces BusinessContext** | Every write takes `BusinessContext context` — `SaveAsync`, `ForkAsync`, `SetDefaultAsync`, `DeleteAsync`, `AddFavoriteAsync`, `RemoveFavoriteAsync`, `ReorderFavoritesAsync`. |
| **Does not accept a caller-supplied CompanyId** | Grep for `companyId` across every action on `ReportsCenterApiController` returns **nothing**. Company comes only from the resolved context. |
| **Enforces ownership and scope** | `SetDefaultAsync` / `DeleteAsync`: `if (existing == null || !decision.Allowed) return false;` |
| **Company-scoped reads** | One template query, commented as such: `t.CompanyID == context.CompanyId || (t.CompanyID == 0 && t.Scope == Platform)`. |
| **Wired to real roles** | `Program.cs`: `.MapPermission(ReportPermissions.Administer, "Admin", "SuperAdmin")` and the BusinessEvents permissions. Not a stub returning true. |
| **No module-permission bypass** | Writes route through the seam; the evaluator consults roles and administrator status. |

### One correction I had to make mid-analysis

My first pass reported *"SaveAsync contains no authorization call"*, based on grepping its body for `_authorization.`. That was wrong, and wrong in the dangerous direction — it would have under-credited a path that does authorize.

`SaveAsync`, `ForkAsync`, `SetDefaultAsync` and `DeleteAsync` reach the seam **transitively**, through the private helper `LoadForAccessAsync`, which calls `_authorization.AuthorizeTemplateAsync`. A direct-call grep cannot see that; the analyzer's call-graph resolver can, which is precisely why the analyzer is semantic rather than textual.

The opposite trap was also present: `ReportAuthorizationService.CodeCompanyUnresolved` is a **constant reference**, not a call. Counting substring matches for "Authorize" would have credited it. Both were resolved by distinguishing an awaited invocation from a type reference.

## 2. What the addition credits — and what it deliberately does not

| Endpoint | Reaches the seam? | Via |
|---|---|---|
| `Preview` | **Yes** | `ReportService` → `AuthorizeReportAsync(View)` |
| `Export` | **Yes** | `ReportService` → `AuthorizeReportAsync(View)` |
| `SaveReport` | **Yes** | `ReportTemplateService.SaveAsync` → `LoadForAccessAsync` → `AuthorizeTemplateAsync` |
| `ForkSavedReport` | **Yes** | `ForkAsync` → `LoadForAccessAsync` |
| `SetDefaultSavedReport` | **Yes** | `SetDefaultAsync` → `LoadForAccessAsync` |
| `DeleteSavedReport` | **Yes** | `DeleteAsync` → `LoadForAccessAsync` |
| `AddFavorite` | **Yes** | `ReportLibraryService` → `AuthorizeReportAsync(View)` |
| `RemoveFavorite` | **NO** | row ownership only — `CompanyID == context.CompanyId && EmployeeId == context.EmployeeId`, returns false when either is unresolved |
| `ReorderFavorites` | **NO** | same row-ownership pattern |

**The last two are not credited, and that is the correct outcome.** They authorize by row predicate rather than by a permission decision. That is defensible for a user reordering their own favourites — the predicate is the authorization and it fails closed — but it is *not* what the declared surface records, and forcing them green would be the exact false credit this process exists to prevent.

They remain visible to CBA001 and are TAB-2's to resolve: either route them through the seam, or have the owner accept row-ownership as sufficient and record that decision.

**No count is claimed here.** The endpoint totals cannot be measured while the application does not compile (§4 of the integration baseline), so this document states which paths reach the authority, not how many endpoints moved.

## 3. Analyzer tests — the boundary, proved

`CrossBuy.Analyzers.Tests/ReportingAuthoritySurfaceTests.cs` — **9 tests, all passing.**

**Recognised (3):**
* a write that calls the seam directly is credited, with `IReportAuthorizationService` in the evidence;
* a write that reaches it **through a private helper** is credited, with the helper in the chain — this pins the real `LoadForAccessAsync` shape, and without it all four template writes would be wrongly reported unauthorized;
* the **concrete** `ReportAuthorizationService` is credited too, since a controller may hold either.

**Refused (4):**
* `IFakeReportAuthorizationService` — identical method shape, undeclared name — is **not** credited. Authority is granted by declaration, not by resemblance.
* `IReportTemplateService` — a service that *consumes* the seam — is **not** credited. Declaring it would credit every endpoint touching a template, including the two that authorize only by row ownership.
* a Reporting write with no authority at all remains rejected.
* an unrelated controller is unaffected — proving the addition did not widen anything outside its own type.

**Surface discipline (2):**
* both names are declared explicitly;
* a test asserts that `IReportService`, `IReportTemplateService`, `IReportLibraryService`, `IReportHistoryService`, `IReportArchiveService`, `IReportDatasetRegistry` and `IReportPermissionEvaluator` are **not** authorities. If anyone later adds one, this fails and they must justify it in the file header the way every prior addition was justified.

### How the tests were executed

`CrossBuy.Analyzers.Tests` references `CrossBuy.csproj`, and the application does not currently compile (TAB-3's Workspace work, mid-write). The tests were therefore run in an **isolation harness** referencing only `CrossBuy.Analyzers`, with the same `Surface.cs` and `AnalyzerHarness.cs`:

```
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9
```

**Stated plainly:** this proves the analyzer boundary. It proves nothing about the application, and the tests must be re-run in the real project once the tree compiles. `CrossBuy.Analyzers` itself builds with **0 Error(s)**.

## 4. What was not done

* No `#pragma warning disable CBA001`, no `SuppressMessage`.
* No entry added to `authorization-baseline.json` — the baseline may only shrink.
* No controller name added as an exception.
* No GET/POST reclassification.
* No unrelated Reporting type declared.
* **No file owned by TAB-2 was modified.** The controller and services were read only.

## 5. Observation for TAB-2 (not a change)

`SaveAsync` reaches the seam through `LoadForAccessAsync` only on the **edit** path (`input.Id > 0`). Creating a *new* Company- or Team-scoped template appears to be gated by scope-capability and context checks rather than by a report-permission decision. That may be intended. It is recorded here for the owning tab to confirm, and no change was made.
