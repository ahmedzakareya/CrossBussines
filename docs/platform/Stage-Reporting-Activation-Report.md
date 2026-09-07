# Stage — Reporting Platform Activation (R1 + R2)

**Platform:** CrossBusiness Reporting Platform
**Date:** 2026-08-06
**Scope:** activate the existing engine. No redesign, no rebuild, no Report Studio designer, no UI.
**Status:** R1 and R2 complete. **Reporting tests 179 → 253, all passing.** Stopping for review.

---

## 1. What activation means here

The engine already existed and was not touched. Activation is the three things it was missing:

| # | Missing | Now |
|---|---|---|
| 1 | A dataset over real data | `Platform.BusinessEvents.Log` — the kernel's own event log |
| 2 | That dataset registered and permissioned | 3 registrations + 3 permission keys mapped in `Program.cs` |
| 3 | An HTTP surface | `api/reports` — 11 endpoints over the existing services |

No engine file was modified. `ReportEngine`, `ReportDataShaper`, the renderers, the exporters, the archive,
history, templates and schedules are unchanged.

---

## 2. R1 — the Business Events dataset

`CrossBuy/BL/Reporting/BusinessEventsDataset.cs` — dataset, report definition, definition provider and data
source.

**Why `BusinessEvents` and not Accounting/Inventory/CRM:** it is the highest-value read in the product for an
auditor, it touches no module's tables (so activation couples reporting to no module schema), and it exercises the
whole dataset layer against a table with genuinely confidential rows. Owner decision 7 — no Accounting, Inventory,
CRM, HR, Projects or Construction data source — is untouched.

### 2.1 The security problem, and what was done about it

`BusinessEvents` rows carry a visibility (ADR-004). The kernel's own reader applies **four** filters. A report over
the same table is a plausible way around all four, so the data source reproduces three exactly and replaces the
fourth with something stricter:

| Kernel filter | Here |
|---|---|
| company | identical — `CompanyID == context.CompanyId`, on the row |
| branch | identical, including "only when BOTH sides carry a branch" |
| visibility + own-actor | identical two-step |
| **per-entity View** | **cannot be reproduced** — 25 000 rows × 11 entity types is 25 000 permission evaluations. Replaced by a single report permission mapped to audit roles only. |

That last row is a real difference and is documented in the source, not glossed: holding
`reporting.businessevents.view` reveals *which* records changed and *when* across the company without the
per-record check. It is mapped to `Admin`, `SuperAdmin`, `Auditor` — never an ordinary module role.

Three keys, granted independently, all **unmapped-is-denied**:

| Key | Reveals | Mapped to |
|---|---|---|
| `reporting.businessevents.view` | the report, Internal rows | Admin, SuperAdmin, Auditor |
| `…confidential` | Confidential rows **and the Payload column** | Admin, SuperAdmin |
| `…restricted` | Restricted **and System** rows | SuperAdmin |

`Payload` is separately gated because a payload is a summary by contract, but a summary of a sales invoice still
carries its total — so the *shape* of activity and the *amounts* are separate grants. `DedupKey` is
`Never`-sensitivity: nobody, including an administrator.

### 2.2 A real bug the tests caught

`mayReadAnyRestricted` was first derived from the readable set — `allowed.Contains(Restricted)`. But the set
deliberately contains `Restricted` for an ordinary caller too, so their **own** rows can be fetched. Deriving the
manager flag from it made every viewer see every restricted event.

This is precisely the conflation `TimelineProjectionService` documents in its own source, and I wrote the warning
into the file header and then implemented the bug anyway. `A_plain_auditor_sees_internal_only` caught it. The set
and the flag are now two returned values.

Two test defects were also found and fixed, both mine rather than the code's:
- the cross-company seed was written through the company-1 context, where `CompanyWriteGuardInterceptor` **coerces**
  it into company 1 — so the report correctly returned both rows and the test failed against correct code;
- three failures were a stale assembly (the running app holds `bin/Debug/CrossBuy.dll`), not a logic fault.

### 2.3 Dataset shape

12 fields, 5 parameters, 2 drill-downs, 1 drill-through, 1 calculated field (`Action`, correctly non-filterable
and declaring `DependsOn`). `MaxRows` 25 000. `TruncateAndDeclare` — an investigative log is useful partially and
says so; a trial balance is not, which is why the policy is per dataset. `AvailableAsWidget = false`: a widget
refreshes unattended on a shared screen, and an audit log is the last thing that belongs on one.

---

## 3. R2 — the Reports Center backend

`CrossBuy/Controllers/Api/ReportsCenterApiController.cs`. **Backend only** — no view, no Razor page, no client
asset, per the standing rule that no Reporting UI is built until the approved Metronic reference is supplied.

| Endpoint | Purpose |
|---|---|
| `GET api/reports/catalog` | the reports this caller may see |
| `GET api/reports/categories` | the category tree |
| `GET api/reports/{code}/describe` | columns, parameters, capabilities, **available formats** |
| `GET api/reports/datasets` | datasets this caller may build over |
| `GET api/reports/{code}/preview` | capped HTML preview + diagnostics + truncation flag |
| `GET api/reports/{code}/export?format=` | **HTML / CSV / XLSX** as a file |
| `GET api/reports/history` | run history |
| `GET api/reports/history/{runId}/parameters` | re-run "the same report as last month" |
| `GET api/reports/archive` | archived artifacts |
| `GET api/reports/archive/{id}/download` | retrieve, re-checking the report permission |
| `GET api/reports/{code}/saved` · `GET api/reports/saved/{id}/versions` | saved layouts |
| `GET api/reports/favorites` | favourites |

The controller **decides nothing**. It resolves the context, calls one service, shapes the answer. Consequences:

- **No `companyId` parameter exists anywhere in the file.** The company comes from the resolved `BusinessContext`,
  so the hole Hotfix A.1 had to close for the Accounting API is avoided by construction rather than by validation.
- **The caller picks a format, never a renderer** — a closed enum handed to the engine.
- **`Internal` columns and `SystemSupplied` parameters are stripped from `describe`**, not just from rendering. A UI
  that received them could offer a filter the engine would then refuse.
- An unauthorized probe gets **404, not 403**, wherever the difference would disclose that a report or run exists.

`Preview` and `Export` are **GET**. They were written as POST to carry a body; running a report is a read, and GET
is the correct verb — it also makes a report link bookmarkable and shareable, which is what people actually want.
Parameters ride the query string (`?From=2026-01-01&To=2026-01-31`) and go through the same binder.

---

## 4. BLOCKED — 7 write endpoints, and it is not a design problem

These belong in R2, were written, compiled, and then **removed**:

```
POST   api/reports/saved                 POST   api/reports/favorites
POST   api/reports/saved/{id}/fork       DELETE api/reports/favorites
POST   api/reports/saved/{id}/default    POST   api/reports/favorites/reorder
DELETE api/reports/saved/{id}
```

**`CrossBuy.Analyzers` reports each as `CBA001` — an ERROR:** *"Mutating endpoint has no authorization the analyzer
can see."*

That diagnostic is **correct as written**. The analyzer credits authorization when an endpoint's call chain reaches
a type in `AuthorizationSurface.AuthorityTypes` — the eight module access services, `IPlatformPermissionProvider`,
`IAccountingApiAuthorization`, `IPlatformGrantWriter`. Reporting's services are not in that list, because reporting
did not exist when it was written.

The endpoints **are** authorized: `IReportTemplateService` and `IReportLibraryService` apply the report gate and
then the template's scope rules before touching a row, and 28 existing template-scope tests prove it
(`A_tenant_cannot_create_a_platform_template`, `Nobody_can_grant_more_than_they_hold`,
`Another_employees_personal_template_is_never_resolved_for_me`). The analyzer simply cannot see it.

**Three ways out, and why each was rejected here:**

| Option | Verdict |
|---|---|
| Add `IReportAuthorizationService` to `AuthorizationSurface.AuthorityTypes` | **The correct fix** and the documented process — that file's header records `IAccountingApiAuthorization` and `IPlatformGrantWriter` being added exactly this way when the analyzer blocked *their* endpoints. It is a **first-tab (Stage 2A) file**, which this tab is instructed not to modify. → **escalated** |
| Add to `engineering/authorization-baseline.json` | Refused by the analyzer itself: *"The baseline may only shrink — a new entry is not an option."* |
| Attach `[PlatformOps]` | Recognised, but it means platform administration — it would restrict *favouriting a report* to platform admins |

A fourth option — suppressing `CBA001`, or reshaping a write as a GET — was not considered. The verb change applied
to Preview/Export is legitimate because those genuinely **are** reads; doing it to a write would be defeating a
security guardrail rather than satisfying it.

**Owner action — one line, first tab**, in `CrossBuy.Analyzers/AuthorizationSurface.cs`:

```csharp
// Reporting Platform activation — the reporting module's access service. Every write path through
// IReportTemplateService / IReportLibraryService applies the report gate and then the template scope
// rules before touching a row; 28 template-scope tests prove it.
"IReportAuthorizationService", "ReportAuthorizationService",
```

The seven endpoints then compile unchanged. **The service layer is already complete and tested** — only the HTTP
surface is missing.

---

## 5. Verification

| Check | Result |
|---|---|
| `dotnet build CrossBuy.sln -c Debug`, no exclusions | **0 errors** (08:22Z) |
| **Reporting tests** | **253 / 253 passing** (179 → 253) |
| Full application suite | **18 failures, all fourth tab** — see below |

New tests: **+32** Business Events dataset, on top of the +33 dataset-layer and +9 security tests from the previous
increment.

Coverage of the new dataset: fail-closed gate · the three visibility tiers · own-actor exception · System
unreachable by own-actor · Payload gating · `DedupKey` invisible to everyone · company isolation · branch isolation
in **both** directions · inclusive date range · multi-value parameters · actor filter · System rendered as "System"
not blank · row cap truncation · exactly-at-cap not truncated · HTML/CSV/XLSX all produce · CSV never contains a
`Never` column · dataset validates clean · report columns projected from dataset fields.

### 5.1 The 18 suite failures are not Reporting

All are `TasksCalendarOverdueTests` / `TasksCalendarNotificationTests` / etc., failing on
`SQLite Error 19: NOT NULL constraint failed: Employee.Address` — a seed helper that does not fill Employee's
non-nullable columns. `CrossBuy.Tests/TasksCalendar*.cs` and `CrossBuy/BL/TasksCalendar/` are **untracked** (`??`),
i.e. the fourth tab's work in flight. Not modified.

(For reference, the same gap was hit and fixed in this session's Communication test host — the fix is to populate
`FirstName`, `LastName`, `Address`, `PhoneNumber`, `Email`, `ProfileImage`, `Gender`, `MaritalStatus`, `UserId`.)

---

## 6. Owner decisions — all still honoured

| # | Decision | State |
|---|---|---|
| 1 | CrossBusiness Blue canonical | unchanged (no UI added) |
| 2 | Green/gold only in report content | unchanged |
| 3 | Playwright PDF deferred | `UnconfiguredHtmlToPdfConverter` still registered; no package added |
| 4 | Email waits for the Communication outbox contract | `NullReportMailSender` unchanged |
| 5 | Hosted scheduler deferred | **0 `IHostedService`** registrations — still asserted by test |
| 6 | Permission provider fail-closed until B6 | `RoleMapReportPermissionEvaluator` unchanged; the three new keys are role-mapped, not provider-backed |
| 7 | No Accounting/Inventory/CRM/HR/Projects/Construction data source | **none added** — the one new source reads the kernel's `BusinessEvents` |

Not implemented, as instructed: Report Studio designer, Stimulsoft, Communication, Workspace, Construction,
Calendar, Tasks, Security Console.

---

## 7. Open items

| # | Item | Owner |
|---|---|---|
| **A1** | Add `IReportAuthorizationService` to the analyzer's authority surface → unblocks the 7 write endpoints (§4) | first tab |
| **A2** | 18 `TasksCalendar*` test failures (Employee seed) (§5.1) | fourth tab |
| A3 | `CHECK` constraints on Reporting vocabulary columns (`reporting_platform_slice_002.sql`) | this tab, next increment |
| A4 | Release-configuration build measurement (app holds `bin/Release`) | build agent |
| A5 | Module datasets (Accounting, Inventory, CRM) | deferred by decision 7 |
| A6 | Reports Center UI | blocked on the approved Metronic reference |
