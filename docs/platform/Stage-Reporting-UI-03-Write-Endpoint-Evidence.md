# Stage Reporting UI — 03 · Write-endpoint evidence

**Phase:** R3 Phase 1
**Status:** **NOT ACTIVATED.** The gate this phase depends on is still shut.
**Code:** `Controllers/Api/ReportsCenterWriteEndpoints.cs.pending` (written, complete, not compiled)
**Tests:** `CrossBuy.Tests/ReportingWriteAuthorizationTests.cs` (compiled, running, green)

---

## 1. The gate, checked rather than assumed

Phase 1 reads: *"After TAB 1 confirms the authorization-surface addition, restore…"*.

**TAB 1 has not made the addition.** Verified in this increment:

```
CrossBuy.Analyzers/AuthorizationSurface.cs → AuthorityTypes
  … "IAccountingApiAuthorization", "AccountingApiAuthorization",
    "IPlatformGrantWriter", "PlatformGrantWriter");      ← ends here
```

No `IReportAuthorizationService`. `.globalconfig` still has `dotnet_diagnostic.CBA001.severity = error`, so
including the endpoints fails the build rather than warning.

Nothing was assumed from the brief's phrasing. The list was read.

---

## 2. Four routes out, and why three are wrong

### 2.1 Declare the authority — the correct fix, and TAB 1's

One line in `CrossBuy.Analyzers/AuthorizationSurface.cs`:

```csharp
// Reporting Platform. Every write reaches IReportAuthorizationService, which applies the report
// permission gate and then the template scope rules before a row is touched.
"IReportAuthorizationService", "ReportAuthorizationService",
```

This is the **documented process**. That file's own header records `IAccountingApiAuthorization` and
`IPlatformGrantWriter` being added exactly this way when the analyzer blocked *their* endpoints, with a visible
justification each time. It is a TAB 1 file and this tab is instructed not to modify it. → **escalated**.

### 2.2 Append to the baseline — refused by the analyzer

`engineering/authorization-baseline.json` is shrink-only. The diagnostic's own message says so: *"The baseline
may only shrink — a new entry is not an option."*

### 2.3 Implement `IModuleAccessService` — investigated properly, and rejected

This looked like a legitimate route that stays inside TAB 2's files, and it deserved more than a dismissal.

`AuthorizationResolver.IsAuthority` credits a call when the containing type **or any interface it implements**
is named in `AuthorityTypes` (`AuthorizationResolver.cs:216-217`), and `IModuleAccessService` **is** named
there — "the canonical session-free contract every module also implements". So a
`ReportingAccessService : IReportingAccessService, IModuleAccessService` would be credited, with no analyzer
edit at all. And there is a real architectural argument for it: every other module has one.

**It does not work, and the reason is decisive.** `PermissionScopeStartupValidator.ValidateAsync` throws at
boot for any registered `IModuleAccessService` whose `Scope` is not in `EntityRegistry.PermissionScopes`:

> *"These IModuleAccessService scopes are not in EntityRegistry.PermissionScopes: Reporting. A module whose
> scope is unknown can never match a role assignment, so it would stay bootstrap-open silently."*

There is no `ScopeReporting`. `EntityRegistry.cs` is a platform-kernel file. So the route does not avoid a TAB 1
edit — it **moves it somewhere worse**: from a one-line declaration in a linter's policy file to a change in
the platform permission model, making Reporting a permission module as a side effect of satisfying a linter.

Rejected on the merits, not on ownership.

### 2.4 Suppress, or reshape a write as a GET — not considered

The GET verbs on Preview and Export are legitimate because those genuinely **are** reads. Doing the same to a
write would be defeating a security guardrail rather than satisfying it.

---

## 3. What was built anyway

All seven endpoints are written in full in `ReportsCenterWriteEndpoints.cs.pending`. The extension keeps them
out of the project's `**/*.cs` glob, so they do not compile and the build stays green.

```
POST   api/reports/saved                    save a report layout (create or edit)
POST   api/reports/saved/{id}/fork          copy a platform layout into an editable scope
POST   api/reports/saved/{id}/default       set the default layout
DELETE api/reports/saved/{id}               soft-delete a layout
POST   api/reports/favorites                favourite a report        (idempotent)
DELETE api/reports/favorites                unfavourite               (idempotent)
POST   api/reports/favorites/reorder        reorder the favourites bar
```

Design points carried in the file:

- **A separate controller** from the read API, sharing the route prefix — so the two can carry different
  filters. `[AutoValidateAntiforgeryToken]` belongs on the writes and must **not** go on the reads, where it
  would break the bookmarkable report links that are the point of Preview and Export being GETs.
- **Anti-forgery is needed even for a JSON API here** because these endpoints authenticate by *session cookie*,
  which a browser attaches to a cross-site post automatically. A bearer-token API would not need it.
- **One authority call**, `AuthorizeAsync`, on every write path. There is no route to a mutation that does not
  pass an authorization decision — which is what makes the analyzer's future credit honest rather than nominal.
- **Optimistic concurrency** on save via `ExpectedVersionNo` → `409 Conflict`.
- **Favouriting is a gated write.** Without the gate it would confirm that a report code exists, and leave a
  row referencing it, for a caller who may not see it.
- **403 vs 404**: a caller who already proved they can see the report gets the reason; an unauthorized probe
  gets 404.

---

## 4. The ten required scenarios

Eight were already proven when the template engine was built. Duplicating them would be two places to maintain
one rule, so they are **cited, not copied**:

| Scenario | Proven by |
|---|---|
| personal ownership | `Another_employees_personal_template_is_never_resolved_for_me` |
| team scope | `A_team_template_resolves_only_for_members_of_that_team` |
| platform scope | `A_tenant_cannot_create_a_platform_template` · `A_platform_template_is_runnable_by_all_and_editable_by_nobody` |
| unauthorized write | `A_share_cannot_grant_access_the_module_permission_denies` · `Nobody_can_grant_more_than_they_hold` |
| cross-company write | `A_template_from_another_company_is_refused` |
| deletion constraints | `Deleting_a_template_is_a_soft_delete_so_archive_rows_are_not_orphaned` |
| favourite idempotency | `Favouriting_is_idempotent_and_hides_reports_whose_permission_was_revoked` |

Two were **not** covered and are new in `ReportingWriteAuthorizationTests`:

| Scenario | New test | What it pins |
|---|---|---|
| **company scope** | `A_company_template_is_runnable_by_all_but_editable_only_by_its_owner` | Running and changing are different rights over the same row. Otherwise one person redefines the number the whole company reads, on a "may view sales" permission. |
| **stale version** | `A_second_save_appends_a_new_version_so_a_stale_writer_is_detectable` | Two people open v1, both save. Without a version check the second silently discards the first's work. Also asserts v1 still exists — a conflict is only recoverable if the loser's version was not rewritten. |
| **reorder isolation** | `Reordering_favourites_cannot_touch_another_employees_rows` | Reorder takes client-supplied row ids, which is the shape that invites the attack. A foreign id matches nothing. |

Plus two guards: `A_write_with_no_resolved_company_is_refused_by_the_service_itself` (defence in depth behind
the endpoint's own `ResolveAsync`) and `A_favourite_of_a_report_the_caller_cannot_see_is_never_returned`.

**Why the tests are at service level rather than over HTTP:** that is where the authorization actually lives,
and that layer is compiled, registered and reachable today. Activation becomes a rename against already-green
tests rather than a fresh test-writing exercise under time pressure.

---

## 5. The consistency guard

`The_write_surface_flag_and_the_endpoint_file_agree` asserts that `ReportingWriteSurface.IsActivated` matches
which of the two files exists on disk.

It deliberately does **not** assert the gate is shut — it will be opened, and a test that had to be deleted to
ship would be a bad test. It asserts the two halves are consistent, which is true before *and* after
activation. It also fails if both files exist (dead code that will drift) or neither (the writes were lost
rather than deferred).

---

## 6. Activation — three steps, no code change

1. **TAB 1** adds the two names to `AuthorizationSurface.AuthorityTypes` (§2.1).
2. Rename `ReportsCenterWriteEndpoints.cs.pending` → `.cs`.
3. Set `ReportingWriteSurface.IsActivated = true` in `ReportsCenterPresenter.cs`.

Step 3 turns the Viewer's Save / Fork / Favourite controls from disabled to live. Nothing else changes: the
service layer is complete and tested, and `CBA001` should report zero on the restored endpoints because the
call chain then reaches a declared authority.
