# ADR-010 — Permission evaluation takes a BusinessContext and never reads the HTTP session

**Status:** Accepted, implemented in Stage 1 Batch A.
**Owed since:** the as-built discovery (doc 22, "decisions still owed", item 3, C-3 + B-7). Recorded as a
prerequisite for Stages 3 and 6 and for per-object AI context.

## Context

Four module access services own all authorization policy in CrossBuy. Three of them resolved the current employee
the same way:

```csharp
private const int CompanyId = 1;

public int? CurrentEmployeeId()
{
    var json = _http.HttpContext?.Session.GetString("Employee");
    ...
}
```

Two consequences, and the second is the one that matters:

1. **The company was a constant.** Roles were read from `AccountingUserRoles WHERE CompanyID = 1` regardless of which
   company the user belonged to. On a second company the module was either wrongly open (company 1 has no roles
   configured, so bootstrap-open granted everything) or wrongly closed (company 1 has roles the user does not hold).
2. **The question could only ever be "may the current browser user…".** There was no way to ask "may employee 42
   see this record", so **nothing outside an HTTP request could make an authorization decision.**

Slice 2 hit this directly. `IPlatformPermissionProvider.CanAsync(context, …)` was designed to take a
`BusinessContext`, and four of six adapters discarded it — they called `_access.CanAsync(action)`, which read the
session. `NotificationProjectionConsumer` built a correct per-recipient context, handed it in, and documented the
outcome honestly in a 14-line comment: *"their answer is not specific to `candidateId`"*. The loop ran. It decided
nothing.

Everything Stage 1 is a foundation for — Workflow steps evaluating an approver, a Unified Inbox filtering another
user's work, Enterprise Search trimming results per viewer, AI Context assembling a prompt under the asker's
permissions — needs the same question answered for an arbitrary employee, outside a request. None of it was possible.

## Decision

**Every module access service implements a canonical, session-free contract, and the legacy methods become thin
adapters over it.**

```csharp
public interface IModuleAccessService
{
    string Scope { get; }                            // matches EntityDefinition.PermissionScope
    IReadOnlyCollection<string> Actions { get; }      // the module's own published vocabulary
    Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken ct = default);
}
```

**The rule, stated as a rule:** no implementation of `CanAsync(BusinessContext, …)` may read `HttpContext`, `Session`,
claims, or any ambient current-user state. The context is the input.

### Compatibility is preserved by delegation, not by a second engine

The existing interfaces and signatures are **untouched**, so 168 permission attributes (55 `AccPerm`, 74 `InvPerm`,
37 `CrmPerm`, 2 class-level `PosLaneActivityGuard`), 153 `[SessionValidation]` usages and 34 `CanAsync` call sites
keep working unchanged. `CanAsync(string action)` now resolves a `BusinessContext` and delegates:

```csharp
public async Task<bool> CanAsync(string action)
{
    var context = await _context.TryGetCurrentAsync();
    if (context == null) return false;               // was: evaluated against a company-1 context
    return await CanAsync(context, action);
}
```

**There is one engine.** Two independent permission implementations would drift, and the drift would be silent
because both would look plausible.

### `PermissionTarget` carries only what a module already consults

| Field | Consulted by | Existed before? |
|---|---|---|
`WarehouseId` | `InventoryAccessService.CanUseWarehouseAsync` | yes — and it had **zero call sites**, so the gate was latent. It is now reachable through the single canonical question. |
`OwnerEmployeeId` | `CrmAccessService.VisibleOwnerIdsAsync` (SalesRep → own, SalesManager → org subtree) | yes |
`BranchId` | the POS path (`Employee.BranchID` / `BranchUserRoles`) | yes |
`EntityType` / `EntityId` | carried so an adapter can look the record up | yes |

**No `Visibility` field**, deliberately: visibility is a property of a business *event* and is already expressed by
choosing between `View` / `ViewConfidential` / `ViewRestricted`. Two ways to say one thing is how a contract rots.

### Vocabularies are published, not folk knowledge

| Module | Actions |
|---|---|
Accounting | `read · post · pay · manage · currency-override` |
Inventory | `read · doc · purchase · manage` |
CRM | `read · edit · manage` |
POS | `view · sell · order · kitchen · manage` |

An action outside its module's list is **denied**, not treated as unknown-therefore-read. POS is expressed as
predicates rather than forced into `read/post/pay/manage`, because it genuinely has different semantics — pretending
otherwise would make the abstraction lie.

### Adapters resolve modules by scope, never by casting

`ModulePermissionAdapterBase` takes `IEnumerable<IModuleAccessService>` and selects on `Scope`. Casting
`IAccountingAccessService` to `AccountingAccessService` would compile and then fail the first time anyone decorated or
mocked the service. **A missing module service DENIES** — an adapter whose module is unregistered is as broken as a
missing adapter and must fail the same way.

### `IsSystem ⇒ Allow` is replaced by a governed policy

```csharp
public static class SystemContextPolicy
{
    public static readonly IReadOnlyList<string> AllowedActions = new[] { PlatformActions.View };
}
```

One boolean used to bypass every module permission for every action, including `ViewRestricted`. That was tolerable
while only the outbox dispatcher constructed system contexts; it becomes the platform's highest-value bypass the
moment Workflow, Search, AI Context or the Workspace reuse the provider — which is the entire point of this stage.
Widening the list is now a visible code change with a reason attached. A system context is **still** confined to its
own company.

### Company isolation is checked BEFORE the module

`PlatformPermissionProvider` resolves the record through `IEntityRegistry.ResolveAsync` — the same company-scoped
query that powers the record picker, so there is no second definition of "which company owns this record" — and
denies a cross-company target before any adapter runs.

Ordering is required, not cosmetic: a ChiefAccountant in company 2 holding `manage` would otherwise be **granted** a
company-1 invoice, because the module's role check knows nothing about which record is being asked about.

## Consequences

**An unresolved request is now denied where it used to be evaluated as company 1.** `CanAsync(action)` returns
`false`, and `VisibleOwnerIdsAsync()` returns an empty set rather than `null` (which meant *unrestricted*). Intended —
an anonymous or broken request must not borrow another company's authorization — but code relying on the old
permissiveness will now be refused.

**Bootstrap-open is evaluated per company.** `AnyRoleConfiguredAsync` asked about company 1; it now asks about the
caller's own company. More correct on a multi-company install, and a documented behaviour change: a company-2 user is
now subject to company 2's configuration.

**`CurrentEmployeeId()` still reads the session, and that is deliberate.** It is synchronous and has 26 call sites,
mostly views that cannot `await`. It is no longer consulted by any permission decision, and a test asserts each access
service contains **at most one** `Session.GetString` occurrence — so the session cannot creep back into the policy
path.

**The four services still have three vocabularies.** This ADR makes them *addressable through one contract*; it does
not unify them. Unifying them would change permission semantics for 194 already-guarded actions, which is a different
decision with a different risk.

**HR, Projects and Tasks still have no access service at all** — which is where 132 of the corrected 189-action
backlog sits (CORRECTION-003). `DefaultPermissionAdapter` grants `View` to a company-scoped authenticated user and
refuses every elevated action, because there is no policy to delegate to. Stage 1 Batch C builds the three missing
services.

## Alternatives rejected

**Pass the employee id instead of a context.** Rejected: the company, branch, roles and correlation id are all needed
by at least one module, and an id-only signature would have every caller re-resolving them differently.

**A new unified permission engine.** Rejected — it would be a second authorization source next to four existing ones,
and the migration would silently change the answer for 194 guarded actions.

**Keep the session read and inject a fake HttpContext in background code.** Rejected as the worst option available:
it preserves the coupling, makes the dispatcher build a fake request, and leaves the design unable to answer a
question about anyone but "the current user".

**Delete `CurrentEmployeeId()`.** Rejected for Batch A: 26 synchronous call sites, mostly in views. It is contained
and tested instead.

## Verification

- `Stage1PermissionTests` (22) — every service exercised with **no `HttpContext` at all**; two employees in the same
  module get different answers; roles read for the context's company (a company-1 chief is denied in company 2);
  bootstrap-open per company; warehouse scope respects branch **and** company; CRM ownership; POS predicates
  preserved and branch-confined; unknown action denied by every module even for its strongest role; unregistered
  entity denied; a scope with no registered module denied; the three visibility tiers enforced per employee; a system
  context limited to `View` and still company-confined; a worker context **not** treated as system; cross-company
  denied before the module; the legacy surface intact; the published vocabularies pinned; and a source-level guard
  that each service reads `Session.GetString` at most once.
- `Stage1NotificationAuthorizationTests` (9) — see [ADR-006](ADR-006-Notification-Projection.md)'s limitation, now
  closed: a recording provider proves the question is asked once per recipient with that recipient's own resolved
  context.
- All **230** pre-existing tests pass unchanged.
