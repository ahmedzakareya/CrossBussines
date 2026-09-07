# Stage — Reporting: Integration Gate Report

**Platform:** CrossBusiness Reporting Platform
**Date:** 2026-08-06
**Scope:** close the architecture integration gate without building UI or connecting production modules.

---

## 1. Verdict

| Gate criterion | Status |
|---|---|
| 1. Reporting ADR renumbered 030 → 037 | ✅ |
| 2. Every Reporting-owned reference updated | ✅ 41 files, 61 occurrences, 0 stale |
| 3. Communication ADR-030…036 untouched | ✅ 36 = 36 occurrences; their suite green throughout (250/250, then 274/274 as that tab grew it) |
| 4. Shared solution builds without exclusions | ✅ **0 errors**, Debug and TestRun, at 05:36Z |
| 5. Reporting tests ≥ 179/179 | ✅ **221 / 221** (179 existing + 33 dataset + 9 security) |
| 6. Global failures classified by owner | ✅ §3 |
| 7. `reporting_platform.sql` applies from empty | ✅ |
| 8. Applies twice, idempotently | ✅ |
| 9. All 12 tables + indexes verified | ✅ 12 tables, 25 indexes, 10 filtered |
| 10. No scratch database remains | ✅ |
| 11. Dataset Layer design exists | ✅ contracts + registry + 33 tests + design document |
| 12. Dataset versioning defined | ✅ major/minor with stated blast radius |
| 13. Calculated-field / expression safety defined | ✅ REX design document |
| 14. Report Studio contract updated | ✅ |
| 15. CrossBusiness Blue is canonical | ✅ recorded in §7 and in the Studio contract §0 |
| 16. No production data source connected | ✅ mechanically asserted — §6 |
| 17. No PDF runtime activated | ✅ |
| 18. No email adapter activated | ✅ |
| 19. No hosted scheduler activated | ✅ |
| 20. No other tab's files modified | ✅ §5 |
| 21–23 | see the Preservation Report |

**All criteria are met.** The integrated tree builds and tests clean with no exclusions, verified at 05:36Z after
every change in this increment: **full suite 0 failed / 1319 passed / 183 skipped**.

§3.2 records that the shared tree was intermittently broken by other tabs *during* the increment. That history is
kept deliberately — it is why the verification was repeated to a pinned capture rather than claimed from a single
early run — but it is not an outstanding caveat.

---

## 2. What this increment added

| Item | Detail |
|---|---|
| `CrossBuy/BL/Reporting/ReportDataset.cs` | Dataset contracts: `IReportDatasetDefinition`, fields, sensitivity, operators, drill-down/through, versioning, validator |
| `CrossBuy/BL/Reporting/ReportDatasetRegistry.cs` | Registry with construction-time validation and Studio listing |
| `CrossBuy/BL/Reporting/ReportingRegistration.cs` | +1 scoped registration (dataset registry). No dataset is registered. |
| `CrossBuy.Tests/ReportingDatasetTests.cs` | 33 tests |
| `CrossBuy.Tests/ReportingSecurityInvariantTests.cs` | 9 tests — the 4 invariants not previously covered |
| `CrossBuy.Tests/ReportingTestHost.cs` | +`EngineWith(dataSource)` helper |
| 8 documents | this file and its siblings |

Source files: **34 → 36**. Reporting tests: **179 → 221**. Full suite: **1436 → 1502**, 0 failing.

---

## 3. Integrated verification — the honest record

Run with **no directory exclusions and no command-line source filters**, as required.

### 3.1 THE DEFINITIVE CAPTURE — 2026-08-06 05:36Z

Taken **after** every change in this increment, with no exclusions and no source filters:

| Check | Result |
|---|---|
| `dotnet build CrossBuy.sln -c Debug` | **0 errors** |
| `dotnet build CrossBuy.sln -c TestRun` | **0 errors** |
| **Reporting tests** | **221 / 221 passed** |
| Communication tests (third tab) | **274 / 274 passed** |
| DI-validation tests (all tabs) | **48 / 48 passed** |
| **Full application suite** | **Failed: 0**, Passed: **1319**, Skipped: 183, **Total: 1502** |

**The integrated tree builds and tests clean with no exclusions.** Gate criterion #4 is met without caveat, and
criterion #5 is exceeded: 221 Reporting tests against a required floor of 179.

An earlier capture at 04:55Z (git HEAD `ef4b869`) was also fully green at 179/179 and a 1436-test suite, before
this increment's 42 new tests were added.

### 3.2 The tree is being edited concurrently, and it moved repeatedly

This increment ran alongside three other active tabs. The shared tree changed **under** the verification, more
than once:

| Time (UTC) | State | Owner |
|---|---|---|
| ~04:30 | `CrossBuy.Tests/ConstructionTestFixture.cs` — 4 × CS0246 (`ICommercialRevisionService`, `ISubcontractScopeService` defined nowhere) | **fourth tab** |
| ~04:45 | error set changed mid-verification to `ConstructionC1CommercialRevisionTests.cs` + `ConstructionC1ConcurrencyAndAuditTests.cs`, 18 × CS0246, then cleared | **fourth tab** |
| **04:55** | **fully green — the capture in §3.1** | — |
| ~05:05 | `CrossBuy.Tests/Communication/CommSecurityBoundaryTests.cs` — CS1061/CS0117/CS0103/CS9035 against a changing Communication API | **third tab** |
| ~05:20 | `CommMentionAndNotificationTests.cs` — 2 × CS7036 after `ICommParticipationService.ResolveNotifiableAsync` gained a `BusinessContext` parameter | **third tab** |
| ~05:30 | `CrossBuy/BL/Construction/CommercialRevisionService.cs(425,8)` — CS0128, `mutantItem` already defined | **fourth tab** |
| **05:36** | **fully green — the definitive capture in §3.1** | — |

Full-suite totals also varied between consecutive runs (1436 → 661 → 511) because the test assembly was being
rebuilt underneath the runner.

**Classification: none of these are Reporting.** Every one is in a file this tab is instructed not to modify, and
none was modified.

### 3.3 Consequence, stated plainly

> Every one of those breaks was in a file this tab is instructed not to modify, and none was modified. The other
> tabs reached a stable point, and the pinned capture in §3.1 was then taken — so this report claims a clean
> integrated verification on evidence, not on a single lucky run.
>
> The instability is recorded because it is a real property of a four-tab shared tree: a green run is a
> *timestamped* fact here, not a permanent one.

### 3.4 How the new tests were verified while the shared assembly was broken

A temporary harness in the session scratchpad links **the same** Reporting test files
(`CrossBuy.Tests/Reporting*.cs`) against `CrossBuy.csproj`. It is not part of the repository, is referenced by
nothing, and is not in the preservation archive.

Per-class results (each class run individually, all green):

| Class | Tests |
|---|---|
| `ReportingCatalogTests` | 18 |
| `ReportingEngineTests` | 14 |
| `ReportingOutputTests` | 5 |
| `ReportingParameterEngineTests` | 16 |
| `ReportingScheduleTests` | 6 |
| `ReportingShaperTests` | 19 |
| `ReportingTemplateScopeTests` | 28 |
| `ReportingDiWiringTests` | 9 |
| **`ReportingDatasetTests` (new)** | **33** |
| **`ReportingSecurityInvariantTests` (new)** | **9** |

A combined run in that harness reports `Finished: RepTests` with every test line `Passed`, then the host process
exits non-zero during teardown. That teardown crash is an artefact of the temporary harness, not a test failure —
in the shared project the same files ran clean (179/179 at 04:55Z, before the new files were added).

### 3.5 Release configuration

`Release` could not be measured cleanly: `bin/Release/net8.0/CrossBuy.dll` is locked by **Visual Studio (PID
21604)** and **IIS Express Worker Process (PID 31580)** — the application is running on this machine. The failure
is `MSB3021`/`MSB3027` (file copy), not a compile error. Redirecting output to a scratch path was attempted and
breaks NuGet restore for the `netstandard2.0` analyzer project, so it is not a valid workaround.

**Recommendation:** measure Release on a build agent, or with the app stopped. Not treated as a Reporting defect.

---

## 4. Security invariant re-proof

Thirteen invariants re-reviewed. **Nine were already mechanically covered** and were not duplicated:

| Invariant | Existing test |
|---|---|
| denied runs are recorded | `A_caller_without_the_module_permission_is_denied_and_the_denial_is_recorded` |
| shares cannot widen module permission | `A_share_cannot_grant_access_the_module_permission_denies` |
| callers cannot pass CompanyId | `A_caller_supplied_CompanyId_is_dropped_with_a_warning_and_the_context_wins` |
| hidden/internal fields cannot be filtered | `A_filter_on_an_internal_column_fails_rather_than_being_quietly_ignored` |
| dropping a filter is never allowed | `A_filter_on_an_undeclared_column_FAILS_the_run_because_dropping_it_would_widen_the_result` |
| row limits only become stricter | `A_caller_can_lower_the_row_cap_but_not_raise_it` |
| truncation is always declared | `A_row_cap_truncates_and_says_so` |
| unmapped permission keys fail closed | `An_unmapped_permission_key_is_DENIED_because_omission_must_not_mean_unrestricted` |
| archive cannot bypass report permission | `An_archive_entry_is_not_readable_once_the_report_permission_is_revoked` |

**Four were not**, and 9 tests were added for exactly those:

| Invariant | New test | Why the old coverage was insufficient |
|---|---|---|
| **authorization happens BEFORE data fetch** | `A_denied_run_never_reaches_the_data_source`, `An_unresolved_company_never_reaches_the_data_source` | The existing denial test proves the *result* is denied — which a pipeline that fetched and then discarded would also satisfy. A report that reads Accounting and then refuses has still read Accounting. |
| **callers cannot select a renderer** | `The_public_request_surface_exposes_no_renderer_or_data_source_handle`, `The_format_lever_is_a_closed_enum_and_the_registry_owns_the_mapping` | Structural. A behavioural test cannot fail when somebody *adds* a renderer handle to `ReportRequest` next year; a reflective sweep can. |
| **callers cannot reach a data source** | `Only_the_report_service_and_print_service_are_public_doors_onto_report_generation`, `A_data_source_is_reachable_only_with_a_query_that_carries_a_resolved_context` | Same reason: the guarantee is about the public surface, so a new public type is the failure mode to defend against. |
| **template visibility cannot grant data access** | `A_platform_template_is_visible_to_everyone_and_grants_no_data_access`, `A_stored_template_carries_no_permission_and_no_data_source_handle` | The template tests prove who may *resolve* a template. None proved that resolving one does not let you *run* the report — and a platform template is visible to every tenant by design. |

Plus `A_dataset_declares_its_own_permission_so_it_can_only_add_a_gate`, extending the invariant set to the new
Dataset Layer.

---

## 5. Tab boundaries — nothing outside Reporting was modified

| Forbidden area | Touched? |
|---|---|
| Stage 2A / B6 / Authorization / Bootstrap Policies | **No** |
| `AccountingAccessService` | **No** |
| `InventoryAccessService` | **No** |
| CRM access services | **No** |
| Communication Platform files | **No** — verified by occurrence count *and* by re-running their 250 tests |
| Construction production files | **No** — their compile errors were reported, not fixed |
| Task Management / Calendar | **No** |
| Existing module production queries | **No** |

The only shared files edited are `Program.cs` and `CrossDbContext.cs`, **Reporting comment lines only**, line by
line, with a Communication guard (see the ADR Renumbering Evidence §2.3).

---

## 6. Owner decisions — applied and mechanically checkable

| # | Decision | How it is enforced |
|---|---|---|
| 1 | **CrossBusiness Blue** is the official identity | Recorded in the Studio contract §0 and §7 below |
| 2 | Green/gold only inside report content | Studio contract §0, §4 (chart colours) |
| 3 | Playwright PDF architecture stays, runtime deferred | `UnconfiguredHtmlToPdfConverter` still registered; no Playwright package added |
| 4 | Email waits for the Communication outbox contract | `NullReportMailSender` still registered; deliveries record as Skipped |
| 5 | Hosted scheduler deferred | **0 `IHostedService` registrations** — asserted by `ReportingDiWiringTests` |
| 6 | Permission provider stays fail-closed until B6 | `RoleMapReportPermissionEvaluator` unchanged; unmapped key = denied, asserted by test |
| 7 | No Accounting / Inventory / CRM / HR / Projects / Construction data source | **`ReportDatasetRegistry` ships empty** — asserted by `An_empty_registry_offers_nothing_to_the_studio` |

Decision 7 is worth emphasising: "no production data source is connected" is now a **test**, not a claim. The
dataset registry is registered and contains nothing, so `ListForStudioAsync` returns an empty list and the Studio
would offer nothing.

---

## 7. Canonical visual identity

> **CrossBusiness Blue** is the official identity of the CrossBusiness Reporting Platform and the CrossBusiness
> Report Studio.
>
> Ledger **green** and **gold** are permitted **only inside report content** — chart series, financial emphasis
> within a rendered statement — and never as platform or Studio chrome.

Recorded here and in `Stage-Reporting-Report-Studio-Contract.md` §0. No UI exists to apply it to yet, which is
why it is recorded as a decision rather than demonstrated.

**Note for the owner:** CLAUDE.md currently records an unresolved conflict — "*the brand is ledger green `#13433a`
+ gold, not blue*", with A6.2's "preserve the CrossBuy blue identity" listed as contradicting the code, needing an
owner decision. This increment applies **CrossBusiness Blue** for Reporting as instructed. Whether that also
settles the platform-wide question, and whether `crossbuy-brand.css` should change, remains outside this tab.

---

## 8. Remaining open items (unchanged by this increment, by decision)

| Item | Blocked on |
|---|---|
| PDF runtime binding | shared-project ownership approval (owner decision 3) |
| Email delivery adapter | the third tab's approved Communication outbox contract (decision 4) |
| Hosted scheduler | single-worker ownership model (decision 5) |
| Real permission provider | first tab completing B6 (decision 6) |
| Module data sources | a later increment (decision 7) |
| Excel direction ownership | owner — RTL/LTR sheet direction for Arabic exports |
| `CHECK` constraints on Reporting vocabulary columns | a `reporting_platform_slice_002.sql`; see SQL evidence §7 |
| Release-configuration verification | a build agent, or the app stopped; see §3.5 |
| A pinned simultaneous integrated verification | the other three tabs reaching a stable point |
