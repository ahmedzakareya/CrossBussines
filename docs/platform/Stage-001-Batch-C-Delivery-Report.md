# Stage 1 Batch C — delivery report

**Missing module access services: HR · Projects · Tasks · Communication.**
Analysis: [Stage-001-Batch-C-Analysis.md](Stage-001-Batch-C-Analysis.md) · Approved architecture:
[Stage-001-Batch-C-RBAC-Architecture-Proposal.md](Stage-001-Batch-C-RBAC-Architecture-Proposal.md) ·
ADRs: [026](ADR-026-Shared-Platform-RBAC.md) [027](ADR-027-HR-Permission-Model.md)
[028](ADR-028-Projects-Permission-Model.md) [029](ADR-029-Tasks-Communication-Permission-Models.md) ·
Maturity: [Stage-006](../architecture/maturity/Stage-006-Stage-1-Batch-C-Result.md)

**Verification:** `606 passed / 0 failed / 0 skipped` on real SQL Server; `564 passed / 42 skipped` on SQLite alone.
**114 tests added** (492 → 606): 70 + 41 in the two Batch C files, plus one proof-endpoint pin in the backlog test. Fresh build, 0 errors.
**NO SQL was executed against CrossBuyDB2 or any other real database.** Both new scripts were verified **twice** on a
disposable database plus 13 constraint tests; it was dropped (0 scratch databases remain).
**Batch D not started.**

---

## 1. Requirement status

| Requirement | Status |
|---|---|
| Phase 1 pre-implementation analysis (5-source reconciliation) | **Completed** |
| RBAC architecture review before any schema | **Completed** |
| C1 — canonical, session-free access services | **Completed** |
| C2 — real permission vocabularies per module | **Completed** |
| C3 — HR record-level access | **Completed** |
| C4 — Project record-level access | **Completed** |
| C5 — Task record-level access + `ResolveScopeAsync` | **Completed** |
| C6 — Communication access | **Partial** — SignalR hubs not adopted (§6.2) |
| C7 — four platform permission adapters | **Completed** |
| C8 — compatibility entry points (4 attributes) | **Completed** |
| C9 — first real consumers (4 proof endpoints) | **Completed** |
| C10 — security test matrix | **Partial** — one demonstration test absent (§6.1) |
| C11 — backlog reclassification | **Completed** |
| C12 — visual identity | **Completed** (no UI added; nothing to restyle) |
| C13 — living roadmap | **Completed** |
| Maturity record | **Completed** |
| Documentation and evidence | **Completed** |

## 2. What was built

| | |
|---|---|
| **One shared role table** | `PlatformRoleAssignments` — no `HrUserRoles` / `ProjectUserRoles` / `TaskUserRoles` / `CommUserRoles`. Three of the four existing role tables were already the same shape; four more copies would have been the 4th-7th repetition (ADR-026 §1). |
| **One module-specific business table** | `ProjectMembers` — *who is on a project* is a relationship with its own lifecycle, not a permission. It is also the data that made C4 possible at all: `Project` had no manager, owner or member column, and no employee↔project relationship existed anywhere. |
| **One read path** | `IPlatformRoleDirectory` — company intersection, `IsActive`, `ValidFrom`/`ValidTo` against one UTC clock, and scope validation, written once. The four services never touch a `DbSet`. |
| **One shared gate set** | `ModuleAccessServiceBase` — C1's eleven requirements implemented once: missing context denies, unknown action denies, company mismatch denies **before** any role or record rule, System follows `SystemContextPolicy`, Worker is never employee access, no employee ⇒ deny. |
| **The hierarchy, fixed for new code** | `IOrgHierarchy` reuses the verified cycle-guarded walk and adds the **company intersection** `Hierarchical` never had. |
| **Four services** | `HrAccessService` (11 actions), `ProjectsAccessService` (8), `TasksAccessService` (8), `CommunicationAccessService` (6). |
| **Four adapters** + additive forwarding | `ModulePermissionAdapterBase.BuildTarget` is `virtual` returning null, so all five existing adapters are byte-identical in behaviour. |
| **`AccessScope` / `AccessBreadth`** | The set-shaped companion, implemented for Tasks. |
| **Startup validation** | Unknown or duplicate module scope ⇒ **boot failure**. |

## 3. SQL — verified, not deployed

| | |
|---|---|
| Scripts | `deploy/sql/platform_role_assignments.sql`, `deploy/sql/project_members.sql` |
| Idempotency | Run **twice** on a disposable database — identical object counts both passes (5 checks + 6 indexes; 4 checks + 2 FKs + 5 indexes) |
| Constraints | **13 tests**, all correct: duplicate active grant blocked, revoked duplicate allowed, `ValidTo < ValidFrom` blocked, unknown `PrincipalType` blocked, `CompanyID` 0 blocked, empty `Scope` blocked, duplicate active membership blocked, unknown `RoleOnProject` blocked, `LeftAt < JoinedAt` blocked, `AllocationPct > 100` blocked, FK to a missing project blocked |
| Manifest | Registered with SHA-256 hashes (115 scripts total) |
| Production | **Nothing executed.** Disposable database dropped; 0 remain |
| Deployment order | `platform_role_assignments.sql` then `project_members.sql`, **before** the Batch C code ("SQL before code"), with `sqlcmd -I` for the filtered indexes |
| Startup behaviour before deployment | The app starts; nothing reads either table until a module scope is asked for. A missing table then surfaces as a hard failure from `IPlatformRoleDirectory` — **not** as silent bootstrap-open, because "no roles configured" and "no table" must never look the same |

## 4. Bootstrap-open, and where it deliberately does not apply

Per company **and** per scope: a grant in company 1 does not close company 2, and configuring HR does not close
Projects. Tested three ways.

**Three deliberate exceptions**, because these were effectively unreachable before Batch C and opening them by
default would be a new exposure created by the batch meant to close one:

| Never bootstrap-open | Why |
|---|---|
| HR `confidential-view`, `payroll-manage` | salary / disciplinary / payroll runs |
| Communication `outbox-manage` | `CommMessage` has no owner column at all, so every queued email body would be readable by every employee |
| Communication conversation reads | a private message is not "open by default" for compatibility — membership is required even under bootstrap-open |

## 5. Proof endpoints — exactly four, recorded

| Module | Endpoint | Before → After |
|---|---|---|
| HR | `PeopleController.DecideLeave` | any signed-in employee could decide **any** leave request → the approver relationship (company-intersected hierarchy) is required |
| Projects | `ProjectController.Boq(int id)` | any signed-in employee could open **any** project's BOQ → active `ProjectMembers` membership, with the **project row's** company verified first |
| Tasks | `TasksController.ConfirmMatch(int taskId, int entityId)` | any signed-in employee could link **any** task → assignee/creator/manager, plus the linked entity checked through `IPlatformPermissionProvider` |
| Communication | `ChatController.Messages(int c, int? before)` | `ChatService` already checked membership internally → the rule now lives in the access service, and a non-participant gets **403** rather than an empty thread |

**No other endpoint was touched.** 114 of the 118 in-scope mutating actions remain for Batch D.

## 6. Partial items — full disclosure

### 6.1 C10/15 — the `AccessScope` "no per-record call" demonstration test

* **Exact missing work:** a test that resolves a scope once, translates it into a single LINQ predicate over a
  seeded task set, and asserts the permission provider was called **zero** times.
* **What DOES exist:** the `AccessScope`/`AccessBreadth` contract, `AccessScope.Includes`, the real Tasks
  `ResolveScopeAsync`, and the **agreement test** `CanAsync_and_ResolveScopeAsync_agree_for_read` across
  bootstrap-open / administrator / viewer for three tasks — which is the requirement that the two shapes cannot
  drift (C10/14).
* **Reason:** the test file was drafted and its creation was interrupted; it was not re-attempted in this batch.
* **Affected files:** none in production — test-only.
* **Security impact:** none. No production path changes; the seam is implemented and agreement-tested.
* **Business impact:** none.
* **Exact next action:** add `BatchCAccessScopeTests` with a counting `IPlatformPermissionProvider`, asserting
  `Calls == 0` after one scope resolution and one filtered query.

### 6.2 C6/12 + C10/19 — SignalR hub authorization

* **Exact missing work:** `ChatHub` (and `PosHub`/`NotificationsHub` where they touch conversations) should call
  `ICommunicationAccessService.IsConversationParticipantAsync` on group join and before delivery.
* **What DOES exist:** the method is implemented, tested (including the cross-company case) and exposed for exactly
  this purpose; the **controller** path is protected.
* **Reason:** a hub join is a different pipeline (no `HttpContext` action filter, a different lifetime) and changing
  message delivery is a behavioural change to a live feature. Doing it inside a foundations batch would have widened
  the batch beyond "build the access services", and the brief limits Batch C to a narrow proof set.
* **Affected files:** `Hubs/ChatHub.cs` (unmodified).
* **Security impact:** **real and stated** — hub authorization is unchanged from before Batch C. `ChatService` still
  performs its own internal membership check on the paths the hub uses, so this is not a new hole; it is an
  un-canonicalised one.
* **Business impact:** none today.
* **Exact next action:** in Batch D, inject `ICommunicationAccessService` into `ChatHub`, gate `JoinConversation` and
  the send path, and add a hub-level test.

## 7. Backlog — and a coincidence disclosed

```
183  before Batch C
 -2  proof endpoints authorized (PeopleController.DecideLeave, TasksController.ConfirmMatch)
 +1  HyperPosController.StampInvoiceCustomer — a NEW mutating lane action with no in-body check,
     added CONCURRENTLY by the parallel team (Batch C touched neither POS controller)
 +1  a further mutating action in DevSeedController, likewise concurrent
183  after
```

Mutating total **384 → 388**; in-body **50 → 54**; lane-guarded population **44 → 47** with in-body **40 → 42**.
Reconciled against source, scanner, grep and the endpoint inventory; the pinned test now asserts the new figures and
names `StampInvoiceCustomer` explicitly.

**Batch C's own contribution is −2.** Reporting "183 unchanged" without this breakdown would imply the batch achieved
nothing; reporting "−2" alone would take credit for someone else's tree.

**The backlog was NOT reduced because access services exist.** Only the two genuinely enforced and tested mutating
proof endpoints left it.

## 8. Compatibility

| Area | Impact |
|---|---|
| Accounting, Inventory, CRM, POS, Manufacturing access services | **None.** Not modified, tables untouched, not migrated. |
| The five existing permission adapters | **None.** `BuildTarget` defaults to null; one regression test per adapter plus action-mapping tests. |
| 168 existing permission attributes, views, `MainMenu` | **None.** No signature changes. |
| HR / Projects / Tasks / Communication | New restrictions **only on the four proof endpoints**. Everything else is bootstrap-open, i.e. behaves as before. |
| `PermissionTarget` | Extended additively (`CompanyId`, `SubjectEmployeeId`, `ProjectId`, `TaskId`, `ConversationId`) — all nullable, no existing caller changes. |
| `EntityRegistry` | Four scope constants added; `ScopeNone`'s comment corrected (it claimed HR/Projects "authorize on authenticated + same company only", which this batch superseded). |
| Deployment | Two additive scripts, reviewed, **not executed**. |

## 9. Prohibitions — compliance

| Instruction | Compliance |
|---|---|
| No generic permissions without inspecting behaviour | Every action traced to real operations; **7 example actions dropped** because nothing backs them (`member-manage`, `approve`, `delete`, `confidential-view` ×2, `moderate`, `participant-manage`) |
| One vocabulary must not be forced on all three | Four distinct vocabularies: 11 / 8 / 8 / 6 actions |
| No attributes on all 183 | Four proof endpoints only |
| Authentication ≠ authorization | Gate order: context → action → company → System/Worker → identity → roles → record |
| No HTTP Session in canonical checks | All four take `BusinessContext`; `ProjectsAccessService` reaches accounting through the **context-aware** `IModuleAccessService`, never the session-based legacy method |
| No silent `CompanyID = 1` | No fallback anywhere; company always from the context or the row |
| Don't trust posted CompanyID/EmployeeID/ProjectID/TaskID | All are lookup keys; the authoritative company comes from the `Employee`/`Project`/`TaskItem`/`Conversation` **row** |
| No parallel permission architecture | One `IModuleAccessService` contract, one `IPlatformRoleDirectory`, one adapter base |
| Don't break the four existing modules | Untouched; regression-tested |
| No screen redesign | No view changed |
| Don't implement Workflow / Inbox / AI / Escalation / Business Workspace | None built; roadmap only |
| Don't change payroll/attendance/billing/task-generation/communication rules | No business service body modified |
| No SQL against CrossBuyDB2 | **Nothing executed** |
| Don't claim a module is secured because a class exists | Maturity **0.00 pp**; §7 states 4 of 118 enforced |
| Don't bulk-add roles without compatibility analysis | Bootstrap-open per company+scope; no role row is created by this batch |
| Batch C not marked complete with gaps hidden | §6 discloses both Partial items in full |
| Don't start Batch D | Not started |

## 10. Files

**Created (production):** `deploy/sql/platform_role_assignments.sql` · `deploy/sql/project_members.sql` ·
`Models/Context/Platform/PlatformRoleAssignment.cs` · `Models/Context/Accounting/ProjectMember.cs` ·
`Models/Platform/AccessScope.cs` · `BL/Platform/PlatformRoleDirectory.cs` · `BL/Platform/OrgHierarchy.cs` ·
`BL/Platform/ModuleAccessServiceBase.cs` · `BL/HrAccessService.cs` · `BL/ProjectsAccessService.cs` ·
`BL/TasksAccessService.cs` · `BL/CommunicationAccessService.cs` · `Models/ModulePermAttributes.cs`

**Created (tests):** `BatchCAccessServiceTests.cs` (70) · `BatchCAdapterForwardingTests.cs` (41)

**Modified:** `Models/Context/CrossDbContext.cs` (2 DbSets + mapping) · `Models/Platform/PermissionTarget.cs`
(additive) · `BL/Platform/EntityRegistry.cs` (4 scopes + validation) ·
`BL/Platform/PlatformPermissionProvider.cs` (additive forwarding + 4 adapters) · `Program.cs` (DI) ·
the four proof-endpoint controllers · `CrossBuy.Tests/Stage1PermissionBacklogTests.cs` (reconciliation) ·
`deploy/sql/manifest.json`

**Not modified:** the four existing access services and their tables, `ChatService`, `LeaveWorkflowService`,
`CrmAccessService`, any view, any hub, and the 183-action backlog.

## 11. Rollback

Revert the additive code, remove the DI lines, revert the four proof endpoints and the `EntityRegistry`/
`PermissionTarget`/adapter-base changes. The two tables are **additive and unused until populated** — dropping them is
safe, and no data changes either way. Nothing to undo in any database.

## 12. Open items handed forward

1. **`IOrgHierarchy` is not yet used by `CrmAccessService.TeamOwnerIdsAsync` or `LeaveWorkflowService`** — both still
   walk `Hierarchical` without the company intersection. Not a new hole (it predates Batch C) but a real one, on the
   existing CRM and Leave paths. Batch D.
2. **SignalR hub adoption** (§6.2).
3. **The `AccessScope` demonstration test** (§6.1).
4. **No membership administration UI** — `ProjectMembers` is populated by data until a screen exists.
5. **114 of 118 in-scope mutating actions** remain unremediated — Batch D.
6. **`DefaultCompanyId` in `ProjectController` / `TasksController` / `AccountingController`** — blocks maturity
   dimension 2 from level 80.
7. **Planned, not implemented:** Role Templates, Role Groups, Delegation Framework, field-level permissions,
   Workflow, Unified Inbox, Search, Reports, AI Context, External Collaboration, Customer Portal, Tenant elevation.
---

## 13. Two startup defects found AFTER the tests were green — and how

> **Superseded by `Stage-001-Batch-C1-Delivery-Report.md` §5.** This section undercounts: there was a **third**
> startup defect — the one that actually left the application serving a blank page — and the test figure cited
> below is wrong. Both are corrected in the C.1 report rather than rewritten here, so this file stays the record
> of what was reported at the time.

The suite reported 604 passing while **the application could not start**. Both defects were in code Batch C added,
and both were invisible to 112 unit tests because every one of them constructs its service by hand. Only asking the
DI **container** to build finds them.

### 13.1 A singleton consuming scoped services

```
AggregateException: Some services are not able to be constructed
  ServiceType: Microsoft.Extensions.Hosting.IHostedService  Lifetime: Singleton
  ImplementationType: CrossBuy.BL.Platform.PermissionScopeStartupValidator
  Cannot consume scoped service 'System.Collections.Generic.IEnumerable`1[IModuleAccessService]'
```

`AddHostedService` registers a **singleton**; the four module access services are **scoped** (they depend on
`CrossDbContext`). The validator injected them directly.

The irony is worth recording: this project already carries *"never capture a scoped service instance in a
singleton"* as a permanent engineering rule, learned from a real defect — and the class that broke it was the
validator written to enforce discipline. Fixed by taking `IServiceScopeFactory` and creating a scope per run, exactly
as `BusinessEventDispatchWorker` does.

### 13.2 A circular dependency the first fix exposed

`ProjectsAccessService` took `IEnumerable<IModuleAccessService>` to reach the accounting module's decision — **and is
itself registered as `IModuleAccessService`**. Resolving the collection therefore required constructing the object
being constructed:

```
A circular dependency was detected for the service of type 'IModuleAccessService'
```

This would have failed startup too, immediately after 13.1 was fixed. It was found by the new container test, not at
runtime. Fixed by injecting the **concrete** `AccountingAccessService` (registered, implements `IModuleAccessService`,
so the same context-aware decision with no cycle — and still not the session-based legacy method).

### 13.3 What was added so this class of bug fails a test, not a boot

`Stage1DiWiringTests` gains two tests:

* **`The_batch_c_container_builds_with_scope_validation`** — builds the whole Batch C graph, including the hosted
  service, with `ValidateOnBuild = true, ValidateScopes = true`. Both defects above fail this test.
* **`The_scope_validator_takes_a_scope_factory_not_the_scoped_services`** — asserts the constructor shape by
  reflection, so a change back to constructor injection fails here rather than at startup.

**Honest note on the earlier claim.** The Batch C verification line said "fresh build, 0 errors" and 604 tests green,
and both were true — but a green build and a green suite did **not** mean the application started, and the report
should not have implied completeness without a container test. That gap is now closed, and the lesson is the
generalisable one: *a DI graph is not verified by unit tests that bypass DI.*
