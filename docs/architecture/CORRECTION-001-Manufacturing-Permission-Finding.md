# CORRECTION-001 — Withdrawal of the Manufacturing work-order permission finding

**Status:** Accepted. Issued during Stage 0 (Slice-003), Batch A.
**Severity of the error:** High — the withdrawn finding was published as the single highest-severity security risk
in the system and drove the priority order of the modernization roadmap.

---

## What was claimed

The as-built architecture discovery reported, in `13-Security-Authorization-Isolation.md`,
`19-Current-Gaps.md` (**A1**) and `20-Architecture-Risks.md` (**R1**):

> "Work-order release/cancel/complete/create/save carry no permission attribute … any employee can post WIP/GL
> and consume stock … **privilege escalation** … the highest-severity authorization finding in the system."

It also reported headline coverage of **"16 of 270 write actions carry a module permission"** and
**"39 unguarded writes in `InventoryController`"**.

## What is actually true

**All nine work-order write actions already carried `[InvPerm("doc")]` and `[ValidateAntiForgeryToken]`**, and did
so before Stage 0 began. Verified directly against `Controllers/InventoryController.cs`:

| Line | Action | Attributes (pre-existing) |
|---|---|---|
1149 | `CreateWorkOrder` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1184 | `AddWorkOrderLabor` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1196 | `RemoveWorkOrderLabor` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1204 | `SaveWorkOrder` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1212 | `ReleaseWorkOrder` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1220 | `CancelWorkOrder` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1228 | `CompleteWorkOrder` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1237 | `ProducePartial` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |
1347 | `GeneratePlanWorkOrders` | `[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]` |

There was **no privilege-escalation exposure on the manufacturing write path**, and there never was.

## Root cause — two defects in the discovery scanner

**1. The attribute regex could not read concatenated attributes.**
The scanner walked upward from each method signature matching `^\s*\[([^\]]+)\]\s*$`. Against a line holding
several attributes:

```csharp
[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
```

`([^\]]+)` stops at the first `]`, so `\s*$` never matches, the line was classified as "not an attribute line",
the upward walk **aborted immediately**, and the action was recorded as `permission_attributes = "(none)"`. This
codebase writes attributes on one line as its dominant style, so the defect hit a large fraction of all actions.

**2. The `writes` heuristic had false positives and false negatives.**
It matched `.Add(` anywhere in a method body, so 36 GET report/export actions that build a `List<>` were counted
as writes — while the real work-order POSTs showed `writes = False`, because their database work happens inside
services rather than the controller body. The metric measured neither what it claimed nor anything useful.

Both defects are in the discovery tooling, not in the application.

## Corrected measurements

`Endpoint-Inventory.csv` and `Permission-Coverage.csv` were regenerated with a fixed scanner that extracts every
bracketed group from every attribute line **and** from the signature line.

| Metric | Originally published | Corrected |
|---|---|---|
Total controller actions | 1002 | **1012** |
"Write" actions with a module permission | 16 | **28** |
`InventoryController` unguarded "writes" | 39 | **1** |
**Mutating actions (POST/PUT/DELETE/PATCH)** | not measured | **362** |
— with a module permission | — | **95 (26%)** |
— without | — | **267 (74%)** |
— with anti-forgery | — | **317 (88%)** |

**Inventory/Manufacturing is the best-protected module in the system, not the worst.** The `writes` column is
retained in the CSVs for continuity but must not be used as a security metric; **`http_method`** is the defensible
basis, because an HTTP-mutating verb is a fact about the endpoint rather than a guess about its body.

## Where the real gap is

The 267 unprotected mutating actions, ranked:

| Controller | Count | Note |
|---|---|---|
`AccountingController` | 44 | many peers on the same controller *do* carry `[AccPerm("post")]` |
`PosController` | 42 | some paths gated by `PosCtx` session + `[PosLaneActivityGuard]` instead |
`PosAppController` | 36 | independent cashier app, `PosCtx` session gate |
`CrmController` | 32 | `[CrmPerm]` applied selectively |
`ProjectController` | 30 | no Projects RBAC exists |
`AdminController` | 24 | no HR RBAC exists |
`TasksController` | 13 | no Tasks RBAC exists |
`ChatController`, `HyperPosController` | 8 each | |
`PeopleController` | 5 | |
`CommController`, `FileManagerController` | 4 each | |
`AnnouncementsController`, `BrandController` | 3 each | |
`AccountController`, `ServiceController`, `CalendarController`, `CommentsController` | 2 each | |
`AuthApiController`, `NotificationsController`, `InventoryController` | 1 each | `AuthApiController` is intentionally anonymous (login) |

**This is recorded as the Stage 1 security backlog.** Stage 0 deliberately did **not** widen to remediate it: bulk
attribute application without per-endpoint analysis would change permission semantics for 267 endpoints at once,
and several are gated by a different mechanism (POS session, lane guard) that a naive `[InvPerm]`/`[AccPerm]`
would either duplicate or contradict.

## What Stage 0 did change

**Defense in depth on the read path only**, which was the genuine gap:

- `[InvPerm("read")]` added to `WorkOrders`, `WorkOrdersData`, `WorkOrderItemPickData`, `NewWorkOrder`,
  `WorkOrderDetails` — screens that expose work-order cost, WIP balance and component data and previously carried
  class-level `[SessionValidation]` only.
- **No behaviour change today**: `InventoryAccessService.CanAsync("read")` returns `true` for any authenticated
  user by the module's own policy. The gate makes the requirement explicit and gives manufacturing one place to
  tighten when real Manufacturing RBAC lands in Stage 1.
- UI gating aligned with the server: `ViewBag.CanDoc` mirrors `[InvPerm("doc")]` in `WorkOrderDetails.cshtml`,
  `WorkOrders.cshtml` and `NewWorkOrder.cshtml`. **Hiding a button is never the control** — the server remains the
  authority.
- Write semantics were **not** tightened. `doc` remains `doc`; reads were **not** raised to `doc`, which would
  have locked viewers out of screens they could previously open.

## Regression protection

`CrossBuy.Tests/Slice3ManufacturingSecurityTests.cs` (13 tests) asserts the attributes off **compiled metadata**,
which cannot be fooled by source formatting:

- every one of the nine write actions carries `[InvPerm("doc")]` + `[HttpPost]` + anti-forgery;
- every one of the five read actions carries `[InvPerm("read")]`;
- **any new work-order POST added later without a permission attribute fails the suite**;
- `InvPermAttribute` implements `IAsyncActionFilter`, proving the gate runs server-side;
- write and read permission levels are pinned, so a future change cannot silently tighten or loosen them.

## Documents corrected

| Document | Change |
|---|---|
`13-Security-Authorization-Isolation.md` | manufacturing case rewritten; coverage table replaced with the corrected mutating-action metric; scanner defect recorded |
`19-Current-Gaps.md` | **A1 withdrawn**; E1 restated on the corrected basis; Stage 1 security backlog added |
`20-Architecture-Risks.md` | **R1 withdrawn**; heat map and top-five list rebuilt |
`21-Modernization-Roadmap.md` | Stage 0's manufacturing item reclassified from emergency fix to defense-in-depth; Stage 1 backlog added |
`evidence/Endpoint-Inventory.csv`, `evidence/Permission-Coverage.csv` | regenerated with the fixed scanner |

## Lesson recorded

A security finding derived from static pattern-matching must be confirmed against the source (or compiled
metadata) before publication, and the confirming assertion should ship as a test. The corrected numbers here come
with 13 tests precisely so this class of claim is never made from a regex again.
