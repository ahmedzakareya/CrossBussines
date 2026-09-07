# Communication Platform — Delivery Report

**Product:** CrossBusiness Platform
**Phase:** Communication Platform architecture (third parallel work stream)
**Date:** 2026-08-05
**Instruction:** *"Build the architecture only. No UI implementation. No production integration. Stop after
architecture completion."*

---

## 1. Verdict

Delivered: architecture, interfaces, DTOs, domain models, events, extension points, documentation and unit tests for
all 43 requested modules. **250 unit tests pass.** `AddCommunicationPlatform` exists and is **not called** from
`Program.cs`, so the running application is byte-for-byte unchanged.

Nine of the 43 modules are delivered as **contract-only or configuration-blocked**, each by an explicit decision or
an external dependency, and each is enumerated with the work that closes it in CPS-001 §9. Nothing is silently
partial.

---

## 2. What was built

| Area | Files | Contents |
|---|---|---|
| Contracts | `Models/Communication/` — 8 files | 16 frozen vocabularies, `CommEntityRef`, ~40 DTOs, event contracts, options, 4 exception types |
| Domain | `Models/Context/Communication/` — 6 files | 14 EF entities + the single mapping entry point |
| Services | `BL/Communication/` — 21 files | 24 registered services, 9 extension-point interfaces |
| Schema | `deploy/sql/communication_platform_slice_001.sql` — 1034 lines | 14 tables, 24 indexes, 17 `CHECK` constraints — additive, idempotent, dependency-free |
| Tests | `CrossBuy.Tests/Communication/` — 8 files | 250 tests |
| Docs | `docs/platform/` — 9 files | CPS-001, ADR-030…036, this report |

Totals: **35 production files / ~9,900 lines**, **8 test files / ~4,050 lines**.

### 2.1 The one idea

Every table and every service is keyed by `CommEntityRef` = (canonical registry code, id), and one gate
(`ICommEntitySurface`) decides whether an entity carries a capability. Onboarding Accounting, Inventory, CRM, HR,
Tasks, Projects, Manufacturing and Support comments (modules 36–43) is therefore **configuration**, not code.

### 2.2 What was deliberately consumed rather than rebuilt

| Existing thing | How this platform uses it |
|---|---|
| `IPlatformPermissionProvider` | **consumed** for entity-level authorization. This platform registers no provider, no access service, no role. |
| `IEntityRegistry` | **the** source of entity codes. An unregistered code is refused by every path. |
| `ITimelineProjectionService` | one timeline **source**. This platform never queries `BusinessEvents`. |
| `IOrgHierarchy` | `@team` expansion — the existing cycle-guarded, company-intersected walk. |
| `IBusinessEventService` | an **optional, off-by-default** forward target. |
| `NotificationTypes` | reused catalog keys, so an in-app render gets the product's existing icon and priority. |

---

## 3. Compliance with the constraints

| Constraint | Status | Evidence |
|---|---|---|
| Do not modify **Authorization** | **Met** | No permission provider, module access service, adapter or role registered. `CommAccessPolicy` only *calls* `IPlatformPermissionProvider.CanAsync`. No file under the authorization surface was edited. |
| Do not modify **Accounting / Inventory / CRM** | **Met** | No file in those modules touched. Their entities are referenced only as `EntityType` **strings** validated against the registry. |
| Do not modify **Bootstrap Policies / Stage 2A / Security** | **Met** | Untouched. |
| Do not modify **existing production logic** | **Met with one declared exception** — see §4. |
| **No UI** | **Met** | No view, partial, controller, endpoint, route or wwwroot asset. Bodies are stored unrendered by design (ADR-030 §9). |
| **No production integration** | **Met** | `Program.cs` unchanged. No hosted service. No `IStartupFilter`. No middleware. Nothing resolves until `AddCommunicationPlatform` is called. |

### 3.1 Verified independence

```
$ git status --porcelain | grep -i communication
?? CrossBuy/BL/Communication/
?? CrossBuy/Models/Communication/
?? CrossBuy/Models/Context/Communication/
?? CrossBuy/deploy/sql/communication_platform_slice_001.sql
```

Plus `CrossBuy.Tests/Communication/`, `docs/platform/CPS-001*`, `docs/platform/ADR-03[0-6]*`, and the one line in
`CrossDbContext.cs` declared below. (`CrossBuy/BL/CommunicationAccessService.cs` also shows as untracked — that is
the **parallel team's** Stage 1 Batch C file, untracked because their whole work stream is uncommitted. Not ours, not
modified.)

---

## 4. Declared deviation: one line in a shared file

`Models/Context/CrossDbContext.cs`, inside `OnModelCreating`:

```csharp
Communication.CommunicationModel.Configure(builder);
```

**Why it is unavoidable.** EF entity types must enter the model in `OnModelCreating`. Without this the 14 tables are
unmapped and the platform cannot run or be tested.

**Why it is one line and not fifteen.** Fourteen `DbSet<>` properties were deliberately not added; services use
`db.Set<T>()` via `BL/Communication/CommDb.cs`. Two other work streams are editing this file concurrently — it was in
fact modified on disk during this session — so the merge surface is one line instead of a fourteen-property block.
Every table name, key, length and index lives in `CommunicationModel.cs`, which this work stream owns outright.

**Commit handling.** Per CLAUDE.md's selective-commit rule, this shared file must be committed with **our lines only**
via git plumbing (`git show HEAD:… > base; insert our line; hash-object -w; update-index --cacheinfo`), never a
whole-file `git add`.

**Behavioural impact when the platform is not enabled:** the entity types are in the model, so EF maps them; nothing
reads or writes them, because no service is registered. A database without slice-001 applied is unaffected unless
code calls a Communication service.

---

## 5. Three real defects found by the tests

Recorded because each is a design mistake that looked correct while being written.

### D1 — Two opposing visibility bounds collapsed into one value
`CommThreadAccess.MaxAuthorVisibility` was computed as the *more restrictive* of (thread ceiling, caller tier), and
the service then refused anything more open. For an ordinary caller on an Internal thread that resolved to
`Restricted`, so **an Internal comment on an Internal thread was refused** — the ordinary case.

The bounds pull in opposite directions ("no more open than the thread" vs "no more restricted than the caller can
read") and no single value can express them. Replaced with an intersected `AuthorVisibilities` set plus `MayAuthorAt`.
Both bounds now have their own test. (ADR-033 §6.)

### D2 — A locked thread reported as an authorization failure
`RequireThreadAsync(Comment)` ran before the lock check, so posting to a locked thread returned **403 "denied"** — the
caller was told they lacked permission when the conversation was simply closed. The distinct `thread_locked` code
existed but was unreachable. The lock check now runs first. (ADR-033 §9.)

### D3 — The kernel timeline source gated on the wrong flag
`PlatformEventTimelineSource` asked `ICommEntitySurface` whether the entity supports a timeline. The surface honours
the **configuration allow-list**, which has no authority over the kernel — so a configured-but-unregistered entity
looked eligible, `ITimelineProjectionService` threw, and the aggregator reported `error:`. A working timeline reported
as broken. The guard now reads `IEntityRegistry` directly. (ADR-031, ADR-036 §5.)

Also fixed while writing the tests: a Restricted thread was unreadable **by the employee who created it**, because
opening a thread does not by itself create a participant row. Resolved by extending the kernel's own-actor exception
to the thread row (ADR-033 §5).

---

## 6. Test results

```
CrossBuy.Tests, filter FullyQualifiedName~Communication
Passed!  Failed: 0   Passed: 250   Skipped: 0   Total: 250
```

| File | Tests |
|---|---|
| `CommunicationDiWiringTests` | 33 |
| `CommBodyPolicyTests` | 34 |
| `CommCommentServiceTests` | 28 |
| `CommAccessPolicyTests` | 20 |
| `CommMentionAndNotificationTests` | 28 |
| `CommTimelineAndDispatchTests` | 35 |
| `CommunicationSchemaParityTests` | 72 |

`CommunicationDiWiringTests` builds the **real** container with `ValidateOnBuild` + `ValidateScopes` and resolves
every service, because CLAUDE.md records as a permanent rule that "a DI graph is not verified by unit tests that
construct services by hand — 112 green tests coexisted with an application that could not boot."

`CommunicationSchemaParityTests` asserts the EF model against the SQL script as text: every table, index, unique
index and filtered index; every vocabulary value; that every `CREATE` is guarded; that the script contains no
`UPDATE`/`DELETE`/`INSERT`/`DROP`/`TRUNCATE`, no `ALTER` of a foreign table and no FK leaving the platform. That is
what makes "safe to apply next to two other work streams" checkable rather than promised.

Storage is SQLite in a shared in-memory connection, mirroring `PlatformTestHost` — the only in-process provider with
real transactions, which every `CommTransaction` rollback assertion depends on.

---

## 7. Build state and shared-tree observations

Both are recorded per CLAUDE.md's build-freshness rule, and neither is caused by this work.

### 7.1 Baseline and final
- **Session start:** `dotnet build CrossBuy.sln` → **0 errors**, 286 warnings.
- **Final:** `CrossBuy.csproj` → **0 errors**; `CrossBuy.Tests.csproj` → **0 errors**. This platform's files add
  **0 warnings**.

### 7.2 Two transient breaks from a parallel work stream
While this work was in progress, a **Reporting** work stream added `CrossBuy/BL/Reporting/` (17 untracked files,
timestamps 13:53–14:21) and its tests:

1. `ReportPrintService.cs` referenced `IReportService`, which existed nowhere → **the web project would not compile**.
2. `ReportingDiWiringTests.cs` / `ReportingOutputTests.cs` did not implement `IReportRenderer.UnavailableReason` →
   **the test assembly would not compile**.

Both were resolved by that work stream during this session; the final build is green. While the test assembly was
broken, this platform's tests were verified in a **temporary isolated harness** in the session scratchpad that linked
the same test files (249 passing there). That harness is not part of the repository and references nothing in it. The
final 250-test run above is from the real `CrossBuy.Tests` project.

Their files were **not** touched.

### 7.3 Eight pre-existing failures elsewhere in the suite — NOT from this work

```
dotnet test CrossBuy.Tests  →  Failed: 8   Passed: 1207   Skipped: 183   Total: 1398
```

All eight are in other work streams' tests and all share one cause: `AccountingAccessService` gained an
`IBootstrapAccessPolicyReader` constructor dependency in the **working tree** without the affected hand-built test
containers being updated.

```
$ git show HEAD:CrossBuy/BL/AccountingAccessService.cs | grep -c IBootstrapAccessPolicyReader
0
$ grep -c IBootstrapAccessPolicyReader CrossBuy/BL/AccountingAccessService.cs
2
```

Failing: `BatchCAccessServiceTests` (1), `D1Wave1CompanySourceTests` (1), `D1Wave1GateTests` (3),
`Stage1DiWiringTests` (2), `Stage1PermissionTests` (1). Error in every case:
*"Unable to resolve service for type `IBootstrapAccessPolicyReader` while attempting to activate
`AccountingAccessService`."*

`AccountingAccessService` is Accounting **and** Authorization surface — this work stream is instructed not to modify
it, and did not. **0 of the 250 Communication tests fail.** Raised for the owner of that change.

---

## 8. Deployment checklist

1. Apply `deploy/sql/communication_platform_slice_001.sql` with **`sqlcmd -I`** (QUOTED_IDENTIFIER ON — several
   indexes are filtered, and SQL Server silently refuses to create a filtered index otherwise; the same footgun
   CLAUDE.md records for `platform_business_events_slice_002.sql`).
2. No other slice is required. This script references no kernel table and applies to a database with no platform
   slice at all.
3. Add the `CommunicationPlatform` configuration section (CPS-001 §7). Every default is the closed one.
4. Add **one** line to `Program.cs`: `builder.Services.AddCommunicationPlatform(builder.Configuration);`
5. **Do not** enable `BridgeToBusinessEvents` until `platform_business_events.sql` **and**
   `platform_business_events_slice_002.sql` are confirmed applied on every target database, and the kernel owner has
   agreed. `RecordAsync` has no swallowing catch: a missing table is SQL-208 inside the caller's transaction, i.e. a
   failed comment on every screen. Enabling also requires the separate `services.UseBusinessEventBridge()` call.

---

## 9. Open items for the owner

| # | Item | Owner |
|---|---|---|
| **O1** | `AccountingAccessService` gained a constructor dependency that breaks 8 existing tests (§7.3). | whoever made that change |
| **O2** | `@role` mentions and Role principals need a reverse "who holds this role" read over `PlatformRoleAssignments` (CPS-001 G1). | Authorization / RBAC owner |
| **O3** | Registry codes for **Task** and **Support/Ticket** do not exist, blocking modules 36 and 38 (CPS-001 G9). | `IEntityRegistry` owner |
| **O4** | Whether to bridge this platform's Email channel to the existing `CommMessages` outbox, or ship a separate adapter (CPS-001 G2, HM-D46). | Comm module owner |
| **O5** | Whether to migrate `DocComments` into `CommComments`, and retire the old widget. A sketch (not a script) is in CPS-001 §10; historical mention targets are **unrecoverable** because `DocCommentService` never stored them. | DocComments owner |
| **O6** | Registry capability flags: 9 codes are onboarded here by configuration while `SupportsComments` is still false. `ICommEntitySurface.GetOnboardingGap()` returns the list (CPS-001 G9). | `IEntityRegistry` owner |
| **O7** | Adding 28 `Comm.Notification.*` resx keys via the selective-commit plumbing, plus a resx-reading `ICommTemplateTextProvider`, to complete French (CPS-001 G8). | this work stream, next phase |
| **O8** | A hosted worker for delivery, plus a SQL-Server `ICommDeliveryClaimStore` and a gated concurrency test (CPS-001 G10). | this work stream, next phase |

---

## 10. Stopping point

Per instruction, this phase **stops at architecture completion**. Not started, deliberately:

- No UI of any kind.
- No controller or API surface.
- No `Program.cs` registration.
- No hosted worker.
- No integration with any production module.
- No migration of existing comment data.

The next phase can proceed on any of: production wiring (one line + a worker), a UI layer over the DTOs, channel
adapters, or onboarding more entities (configuration only).
