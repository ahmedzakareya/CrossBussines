# Stage 2A — Batch 00-A — Roslyn Authorization Analyzer — Design

Analyzer infrastructure only. No production code changed · no production authorization behaviour changed · no SQL
executed against `CrossBuyDB2` · SQL companion guards not started · Batch A not started.

---

## 1. What it answers

One question per mutating endpoint: **does anything decide whether this caller may change state?**

The accepted Stage 1 measurement answers it with a 644-line PowerShell scanner that declares its own limits in its
own source:

> "this is a SYNTACTIC call graph over ONE file, not Roslyn semantic binding. It cannot follow an authorization call
> through an interface, a base class or another file, and it cannot distinguish two same-named methods."

Every one of the seven corrected scanner defects (CORRECTION-001 → 003) was a **text** problem: attributes on one
line, attributes inline before `public`, a comment after the closing bracket, a namespace qualifier, a multi-line
attribute. A symbol has none of those shapes — `[CrossBuy.Models.AccPerm("post")]` and `[AccPerm("post")]` are the
same `AttributeData`, and a commented-out attribute is not an `AttributeData` at all.

That is the whole reason to move to Roslyn. It is also why the analyzer is not merely a faster scanner: it runs
**in the build**, where a new gap can be refused rather than counted.

## 2. Project shape

`CrossBuy.Analyzers/` — **netstandard2.0**, which is not a preference but the analyzer contract: the C# compiler
loads analyzers into its own netstandard2.0 host. Targeting net8.0 produces an assembly the compiler silently
refuses to load — the analyzer would look installed and report nothing, the worst possible failure mode for a
security guardrail.

| Reference | Version | Why |
|---|---|---|
| `Microsoft.CodeAnalysis.CSharp` | **4.8.0** | already in the local cache (4.0.0 / 4.8.0 / 5.0.0 present); 4.8.0 loads under both the .NET 8 and .NET 10 compiler on this machine, 5.0.0 binds to the newer Roslyn only |
| `Microsoft.CodeAnalysis.Analyzers` | 3.3.4 | release tracking + analyzer correctness rules |

Both `PrivateAssets="all"` — an analyzer must never flow Roslyn into its consumers. **No package downloaded, no
dependency upgraded.**

| File | Role |
|---|---|
| `AuthorizationSurface.cs` | **the policy** — one readable list of what is credited and what is not |
| `AttributeFacts.cs` | attribute + HTTP-verb reading through symbols |
| `AuthorizationResolver.cs` | the bounded-fixpoint semantic call graph |
| `AuthorizationInventory.cs` | discovery + classification; the single derivation |
| `BaselineDocument.cs` | the baseline model and a purpose-built JSON reader |
| `AuthorizationDiagnostics.cs` | CBA001–CBA006 descriptors |
| `AuthorizationAnalyzer.cs` | the `DiagnosticAnalyzer` |
| `AnalyzerReleases.Unshipped.md` | release tracking, so re-severing a rule is a visible diff |

`CrossBuy.Analyzers.Tests/` — 86 tests. A **separate** project, not an addition to `CrossBuy.Tests`: that suite is
the frozen 781-test non-regression baseline, and moving the number every prior report cites is exactly what
shrink-only discipline exists to prevent. The two suites are run and reported separately.

## 3. The policy, in one place

`AuthorizationSurface` is a single file because **CORRECTION-004 was caused by a credit nobody could see** —
`PosLaneActivityGuard` sat on a permission list, checked no role, and silently credited 44 mutating POS actions as
protected. A policy spread through a resolver gets that wrong again.

### Credited — permission attributes

`AccPerm` · `InvPerm` · `CrmPerm` · `ApiPerm` · `PlatformOps` · `HrPerm` · `ProjectPerm` · `TaskPerm` ·
`CommunicationPerm` · `PosPerm`

`PosPerm` does not exist yet; it is forward-declared so adding it is picked up without editing the analyzer — the
same convention the accepted scanner uses. `TaskPerm` and `CommunicationPerm` **do** exist and are applied to zero
actions, so recognising them moves no number (§4 of the reconciliation).

### Credited — in-body authorities

The eight module access services (interface **and** implementation), `IModuleAccessService`,
`IPlatformPermissionProvider` + its adapters, and Hotfix A.1's `IAccountingApiAuthorization`. Matched on the
**containing type's simple name** of the resolved method, so an interface reference, a concrete field, a property, a
factory call (`_permissions().CanAsync(...)`) and a fully qualified call all reach the same verdict — and renaming a
field cannot silently remove protection, which the scanner's `_access.`-style field-name regexes could.

**One member is excluded by name:** `IPosAccessService.IsActivityAllowedForLane`. It lives on an authority type and
is still the lane routing predicate CORRECTION-004 refused to credit. "Any call on an access service" would
re-credit the lane guard through the back door.

### Never credited

| Thing | Why not |
|---|---|
| `[Authorize]`, `[SessionValidation]` | authentication — proves *who*, not *what they may change* |
| `[ValidateAntiForgeryToken]` | CSRF defence — proves the request came from the site |
| `PosLaneActivityGuard` | compares an activity preset to a lane; checks **no role**, and calls `next()` when the session is absent, so it does not even prove authentication |
| `DevOnly` | an **environment** gate (404 outside Development) |
| session existence, entity existence, logging, comments | not decisions about permission |

**On `DevOnly`** — the brief lists it under "recognize every approved production authorization path". It is
*recognised* as its own declared category and reported as such in CBA002's message, but it is **not credited**.
Crediting a filter that checks no role would be a false credit (§4 of the brief forbids it) and would reduce
measured debt below 143. Recognition and credit are different things; this is the honest reading of both
requirements. It changes no count either way — `DevOnly` matches zero mutating actions.

## 4. Discovery

Semantic throughout. A **controller** is a class whose base chain reaches `ControllerBase`/`Controller`, or which
carries `[ApiController]`, with a name-suffix fallback so a type whose base failed to bind is not silently dropped
from the measured surface. An **action** is a public, non-static, ordinary method declared on the controller
itself, excluding `[NonAction]` and the MVC lifecycle hooks. **Mutating** = `HttpPost`/`Put`/`Delete`/`Patch`, or
`AcceptVerbs` naming one of them.

`AcceptVerbs` is supported in both the string and the enum form although it is applied nowhere today, because the
failure mode of not supporting it is silent: an `AcceptVerbs("POST")` action would classify as GET and vanish from
the measured surface entirely.

### Declared scope limitation

Only types declared under a `Controllers/` directory are measured, because the accepted Stage 1 figure of 388 means
"mutating endpoints under `CrossBuy/Controllers/**`". Widening the scope would change the number without changing
any code — the one thing the brief forbids. A controller placed outside that folder would be invisible. This is a
**gap, tested so it stays declared** rather than becoming folklore.

Razor views are not compiled. No controller lives in a `.cshtml`, and Stage 1 did not scan them either, so no
number moves — but it is a limitation, not an absence of one.

## 5. The call graph

Bounded fixpoint. From the action, the walk follows: helpers on the containing type, helpers on its **base types**,
every **partial declaration** of them, local functions and lambdas in that scope, and interface members the
controller itself implements. It terminates at a declared authority. Depth is capped at 8 (the deepest real chain is
2: action → gate → access service), every visited method is memoised, and a cycle returns "not authorized" rather
than hanging the compiler — proven by a mutual-recursion test.

### The boundary, stated rather than implied

The walk does **not** descend into arbitrary application services to discover that one of them happens to check
something. That would credit an endpoint for a domain rule written for correctness — a rule any other caller can
bypass, that nobody declared as a control — and would move measured debt without a decision being taken. That is
the exact shape of CORRECTION-004.

This is a real design decision with a consequence: **some of the 143 may not be true gaps**, because a service-layer
check may exist that the analyzer refuses to credit. The honest resolution is to promote such a service into the
declared surface deliberately, so the count moves for a stated reason and is reviewable. Recorded as an open item,
not resolved by widening a heuristic.

## 6. Diagnostics

| Id | Meaning | Default |
|---|---|---|
| **CBA001** | new unprotected mutating endpoint, absent from the frozen baseline | Warning |
| **CBA002** | authentication / anti-forgery / lane guard present, authorization absent | Warning |
| **CBA003** | an authorization-shaped call resolving to no declared authority | Warning |
| **CBA004** | baseline entry no longer matching an unprotected mutating endpoint | Warning |
| **CBA005** | anonymous mutating endpoint not declared `AnonymousByDesign` | Warning |
| **CBA006** | an authorization diagnostic is suppressed | Warning |

All five compilation-end descriptors carry `WellKnownDiagnosticTags.CompilationEnd`. Without it (RS1037) Roslyn can
filter them out in IDE and partial-analysis modes — the analyzer would appear to work and report four of its six
rules nowhere.

### Warning vs Error — the two requirements are staged, not contradictory

The brief says both "the analyzer starts as Warning only" and "new debt → Build Error". Descriptors ship at
**Warning**; escalation is a **configuration** decision made by the consuming project. That separation is what lets
one build of the analyzer warn in the IDE and fail CI.

Escalation is **proven, not promised** — a test overrides CBA001 to `Error` and asserts the reported diagnostic has
`Severity == Error` while `DefaultSeverity == Warning`.

The `.editorconfig` that turns it on, **designed and not applied**:

```ini
[*.cs]
dotnet_diagnostic.CBA001.severity = error   # new debt breaks the build
dotnet_diagnostic.CBA004.severity = error   # a stale allowance breaks the build
dotnet_diagnostic.CBA006.severity = error   # suppression is not available
dotnet_diagnostic.CBA002.severity = warning
dotnet_diagnostic.CBA003.severity = warning
dotnet_diagnostic.CBA005.severity = warning
```

## 7. Baseline enforcement

The baseline is read from **AdditionalFiles**, not from disk: the compiler sandboxes analyzers so a build stays
reproducible from its declared inputs, and a declared input is visible in the project file rather than discovered by
path guessing.

| Situation | Result |
|---|---|
| unprotected endpoint listed in the baseline | accepted debt — no CBA001 |
| unprotected endpoint **not** listed | **CBA001** |
| baselined endpoint became protected | **CBA004** ("now protected by AccPerm") |
| baselined endpoint deleted | **CBA004** ("the action no longer exists on X") |
| baselined controller deleted | **CBA004** ("the controller no longer exists") |
| endpoint **renamed** | **CBA004** on the old id **and CBA001** on the new one |

A rename therefore costs two diagnostics and cannot pass as pre-existing debt — the baseline's own stated rule,
mechanically enforced.

### Declared degradation

**Baseline absent or unparseable → CBA001 and CBA004 stay silent** (CBA002/003/005/006 continue). Reporting 143
accepted entries as new debt on a misconfiguration is noise, not signal. A malformed baseline must also never throw
inside a build. Both behaviours are pinned by tests so they cannot change silently.

The baseline reader is purpose-built rather than `System.Text.Json`, because a serializer dependency inside the
compiler process is a real hazard: a version already loaded by the host causes a load failure, and the analyzer then
reports **nothing** while appearing installed. The reader has its own unit tests, including seven malformed inputs
and `\u`-escape decoding (the baseline may carry Arabic reasons).

## 8. Residual risks

1. **CBA006 can itself be suppressed.** Roslyn applies a `#pragma` to every diagnostic including this one. No
   analyzer can close it; it is closed by review — the pragma is visible in the diff and CI can grep for the ids.
2. **The service boundary (§5)** may leave real authorization uncredited. Deliberate; promotion into the declared
   surface is the remedy.
3. **The Controllers-folder scope (§4).**
4. **Not wired into the build.** Wiring requires `CrossBuy.csproj` (an analyzer `ProjectReference` + the baseline as
   an `AdditionalFile`) and `CrossBuy.sln` — both **shared files** under the selective-commit rule, and the brief
   forbids modifying production code. Designed here, applied in the next increment.
