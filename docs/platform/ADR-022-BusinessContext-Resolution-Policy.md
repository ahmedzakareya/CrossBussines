# ADR-022 — BusinessContext resolution policy: one pipeline, four sources, and no default company

**Status:** Accepted, implemented in Stage 1 Batch A.

## Context

`BusinessContext` was introduced in slice 1 as *the* isolation and authorization context every platform service
receives. One class built it — `BusinessContextAccessor` — and it ended like this:

```csharp
CompanyId = companyId is > 0 ? companyId.Value : FallbackCompanyId,   // FallbackCompanyId = 1
```

The comment beside it was honest about why: eight controllers already hard-coded `DefaultCompanyId = 1`, so the
fallback was "the current tenancy reality, not a new decision". That was true when it was written. It was also the
single most dangerous line in the platform, because **it fired on a live path**:

`PosAppController`'s KDS and delivery screens seeded `Session["Employee"]` with

```csharp
new EmployeeViewModel { FullName = c.EmployeeName, Email = "", ProfileImage = "" }
```

— no employee id, no company, no branch. So a kitchen screen operated by a cashier on a branch belonging to
**company 71** resolved to **company 1**, and every business event, notification, timeline read and permission
decision downstream inherited it. Silently, with no error and no log line.

Three further problems were structural rather than a single defect:

- **One source, one shape.** The accessor could only build an HTTP context. A worker, a dispatcher, an integration and
  a test all had to construct `BusinessContext` by hand, so validation existed nowhere.
- **No validation of the employee↔company relationship.** The session blob was trusted for company and branch, with a
  database "top-up" only when the blob was incomplete. A stale or tampered blob claiming another company won.
- **`IsSystem` was set by hand** on a literal object, which is how a context that bypassed every module permission
  came to be constructible anywhere.

## Decision

**One factory creates every context. There is no default company.**

```csharp
public interface IBusinessContextFactory
{
    Task<BusinessContext>  ForHttpAsync(CancellationToken ct = default);        // throws when unresolved
    Task<BusinessContext?> TryForHttpAsync(CancellationToken ct = default);     // for pre-sign-in callers
    BusinessContext        ForWorker(int companyId, Guid? correlationId = null);
    BusinessContext        ForSystem(int companyId, Guid? correlationId = null);
    Task<BusinessContext?> ForEmployeeAsync(int employeeId, int? explicitCompanyForOrphanRow = null, CancellationToken ct = default);
}
```

### 1. The Employee row is the authority; the session is a hint

Resolution order is unchanged in its *sources* — session blob, then `ClaimTypes.NameIdentifier` → employee — but the
`Employee` row is now read **always**, not only as a top-up, because validating the employee↔company relationship is
the point. A session blob claiming a different company loses, and the disagreement is logged at Warning (it means the
user's company changed mid-session, or a path wrote a blob it guessed).

A session **branch** is honoured only if it is a real branch of the employee's company — the POS lanes legitimately
operate on a branch other than the employee's default, and that must not become a way to reach another tenant.

An **inactive** employee, an employee with **no company**, and a **missing** `Employee` row all produce *no context*.

### 2. `Source` is a first-class field, because three rules depend on it

```csharp
public enum BusinessContextSource { Http, Worker, System, Integration, Test }
```

- only a **System** context may name a company freely, and it is the only one subject to `SystemContextPolicy`
  (see [ADR-010](ADR-010-Session-Free-Permission-Evaluation.md));
- a **Worker** context must name the company it is processing — `ForWorker(0)` throws — so a scheduled job cannot run
  against "whatever was resolved";
- a Worker is **not** a System context. It has no employee identity, so the permission provider denies it. A worker
  needing a decision asks `ForEmployeeAsync` for a specific employee.

`IsSystem` is now a computed property (`Source == System`), so it cannot be set independently of the source.

### 3. `ForEmployeeAsync` is what makes background authorization possible

It resolves a named employee's context from the database with no HTTP involvement, and returns `null` — meaning
**exclude this employee** — when the row is missing, inactive, or has no company.

Its one substitution is explicit: a caller that already knows the company it is processing may supply it for an
**orphan** `Employee` row (`EmpCompanyID = 0`, a data defect). That is an argument, never an ambient default, and it
is logged.

`Source = Integration`, not `System`, deliberately: the context represents a real employee's rights and must be
authorized like any other caller. Marking it `System` would hand it `SystemContextPolicy`'s grant and defeat the
per-recipient check it exists to enable.

**It does not publish to `ICompanyScopeHolder`.** The context belongs to a recipient being *evaluated*, not to the
scope doing the evaluating. Publishing it would repoint Batch B's query filters at someone else's company midway
through a dispatch pass.

### 4. `ICompanyScopeHolder` — an `int?` and nothing else

Stage 1 Batch B adds EF global query filters. A filter expression must read the current company from somewhere the
`DbContext` can reach, and it **cannot** read `IBusinessContextAccessor`, because that accessor queries
`CrossDbContext.Employee` to resolve the company:

```
resolve the company  ->  query Employee  ->  apply the company filter  ->  resolve the company …
```

— a circular dependency, and a filtered read of the very table the resolution depends on. So the filter will read a
holder that carries an `int?`, has no `DbContext` dependency, no HTTP, no services and no async.

It is Scoped, and `CrossDbContext` is Scoped with **no pooling** (`AddDbContext(..., ServiceLifetime.Scoped)`), so one
holder belongs to exactly one `DbContext` instance. A **conflicting** company in one scope throws, because it would
mean two tenants shared a `DbContext`.

**Batch A only populates it. Nothing reads it until Batch B** — deliberately: the holder ships and is proven correct
before anything depends on it for isolation.

### 5. The accessor keeps its interface and becomes a cache

`BusinessContextAccessor` no longer resolves anything. It caches the factory's answer per DI scope — **including the
negative answer**, so an anonymous request does not re-run resolution on every call — and keeps `GetCurrentAsync` for
the ~20 existing callers. `TryGetCurrentAsync` is added for code that legitimately runs before sign-in.

## Consequences

**Callers that used to silently get company 1 now fail explicitly** with `BusinessContextUnresolvedException`. The two
POS session writers are fixed in the same batch — they now state the real employee id, branch and **branch** company
(not `PosLoginContext.CompanyId`, which is the POS *catalog* company and is deliberately 1) — but fixing only the
writers would have left the next incomplete blob doing the same thing quietly. Both were needed.

**A `PosCtx` blob written before this change deserialises `BranchCompanyId` as null**, and the factory then falls
through to claims → the `Employee` row, which resolves correctly. Safe degradation; no forced re-login.

**`IEmployeeService` is no longer a dependency.** The old accessor used it purely to map a user id to an employee;
`GetEmployeeByUserIdAsync` also `Include()`s policy assignments and maps a full view model, none of which a context
needs. Doing the lookup directly keeps the factory self-contained — no service graph to stand up in a test — and
avoids loading rows nobody reads.

**767 `DefaultCompanyId` / `HrCompanyId` call sites across 8 controllers remain**, and this ADR does not touch them.
They are presentation-layer constants, not shared security code; rewriting them is the mass change Stage 1's brief
forbids, and doing it before Batch B's filters exist would move risk rather than remove it. Each now has a one-line
migration path (`await CurrentCompanyIdAsync()`), and Batch B makes the constant redundant rather than load-bearing.

**Rollback is two lines.** Restoring `FallbackCompanyId` and the `is > 0 ? … : Fallback` expression reverts the only
genuinely breaking change in the batch. Recorded here precisely so that stays true.

## Alternatives rejected

**Keep the fallback behind a config flag.** Rejected. A configurable silent tenancy default is worse than a hard one:
it would be on by default in every existing deployment, and nobody would ever turn it off because nothing would fail.

**Fix only the two POS session writers.** Rejected — it treats the symptom. The next incomplete blob (a new screen, a
new integration, a deserialisation change) resolves to company 1 again, silently.

**Resolve the company from a user-selected value.** Rejected as unnecessary: there is **no company switching anywhere
in CrossBuy** — no `SwitchCompany`, `SelectCompany` or `ChangeCompany` action exists. Current company is a function of
`Employee.EmpCompanyID`. Building selection machinery would add an attack surface for a feature that does not exist.

**`AsyncLocal` instead of `ICompanyScopeHolder`.** Rejected: it survives across awaits into places nobody intends,
and a scoped DI service already has exactly the lifetime we want, with a `DbContext` that shares it.

**Make `ForEmployeeAsync` return a System context so background checks always pass.** Rejected — that is the bug
ADR-006 documented, restated as a design.

## Verification

`Stage1ContextTests` (22): HTTP resolution from a session blob and from claims alone; a blob claiming another company
loses; a branch from another company is rejected while a branch of the same company is honoured; **four separate tests
that a missing/incomplete/companyless/inactive identity does not become company 1**; a source-level guard that
`FallbackCompanyId` and `const int CompanyId = 1` are gone from all six shared platform/security files; the POS catalog
constant confined to one literal inside `PosCompanyPolicy`; worker and system contexts require an explicit company;
worker contexts work with **no `HttpContext` at all**; contexts do not leak between scopes and carry distinct
correlation ids; a scope cannot be repointed at a second company; `ForEmployeeAsync` resolves without HTTP, excludes
missing/inactive/orphan employees, accepts only an explicit company substitution, and **does not** repoint the scope
holder; and the accessor caches per scope with the strict/lenient split.
