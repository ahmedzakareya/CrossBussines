# Stage 2A — Roslyn Analyzer Build Integration Plan

**Not applied. Implementation-ready.** Shared-file policy still prevents modification, so `CrossBuy.sln` and
`CrossBuy/CrossBuy.csproj` were **not touched** this increment. Every change below is stated exactly as it should be
made when shared-file modification is approved.

---

## 1. Why it is not applied

| File | Status | Why it is blocked |
|---|---|---|
| `CrossBuy/CrossBuy.csproj` | `M` in git — **parallel-team WIP already in it** | Shared file. Our rule: shared files get *our lines only* via git plumbing (`git show HEAD:… > base; insert our lines; hash-object -w; update-index --cacheinfo`). A whole-file commit would take the parallel team's uncommitted kernel work with it. |
| `CrossBuy.sln` | `M` in git — parallel-team WIP | Same. |

There is also a behavioural reason, independent of policy: wiring the analyzer in today adds **124 CBA002 warnings**
to every developer's build of a tree that already emits **303**. That is a 41% increase in build noise, delivered to a
team mid-WIP, with no prior notice. §7 sequences it so that does not happen.

### The option that avoids both shared files — evaluated and rejected

A new `Directory.Build.props` at the repo root would inject the analyzer into **every** project without editing either
shared file, and it is genuinely the smallest diff. **Rejected**: it silently changes the build of the parallel team's
projects too, it is invisible in the project file a developer opens, and a repo-wide implicit build change is *more*
surprising than an explicit two-ItemGroup edit — the opposite of "safe and reviewable". Recorded because it is the
obvious idea and someone will ask.

## 2. Exact files requiring modification

| # | File | Change | Shared? |
|---|---|---|---|
| 1 | `CrossBuy/CrossBuy.csproj` | 2 new `ItemGroup`s (analyzer reference + baseline as AdditionalFiles) | **yes** — plumbing required |
| 2 | `CrossBuy.sln` | 2 `Project` blocks + 12 configuration lines | **yes** — plumbing required |
| 3 | `.editorconfig` (repo root) | **new file** — severity configuration | no |
| 4 | `CrossBuy.Analyzers/CrossBuy.Analyzers.csproj` | 1 line — `EnforceExtendedAnalyzerRules` already set; add `Analyzer` packaging item if it is ever shipped as a package | no |

Only items 1 and 2 are blocked. Item 3 is a new file and could be added at any time; it is deliberately held back so
severity and wiring land together — an `.editorconfig` naming CBA ids while no analyzer runs is a misleading artifact.

## 3. Exact `CrossBuy.csproj` changes

Insert both `ItemGroup`s immediately **after** the existing `PackageReference` `ItemGroup` and **before** the
`Compile Update` group. Nothing existing is modified; nothing is reordered.

```xml
  <!-- Stage 2A Batch 00-A: the authorization guardrail runs in the build.
       OutputItemType="Analyzer" is what makes the compiler LOAD it; ReferenceOutputAssembly="false" keeps
       CrossBuy.dll from taking a compile-time reference on it. Omitting either one is the classic mistake:
       with the first missing the analyzer is a silent no-op, with the second it ships in the output. -->
  <ItemGroup>
    <ProjectReference Include="..\CrossBuy.Analyzers\CrossBuy.Analyzers.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false"
                      PrivateAssets="all" />
  </ItemGroup>

  <!-- The frozen debt baseline is a DECLARED INPUT. An analyzer may not open files: the compiler sandboxes it so a
       build stays reproducible from its declared inputs. Without this line CBA001 and CBA004 degrade to silent by
       design, and the guardrail measures without enforcing. -->
  <ItemGroup>
    <AdditionalFiles Include="..\engineering\authorization-baseline.json" />
  </ItemGroup>
```

**No `PackageReference` change is required.** The analyzer's two Roslyn packages are `PrivateAssets="all"`, so nothing
flows into `CrossBuy`. No package is added, removed or upgraded anywhere.

### Applying it under the selective-commit rule

```bash
git show HEAD:CrossBuy/CrossBuy.csproj > /tmp/base.csproj
#   insert ONLY the two ItemGroups above into /tmp/base.csproj
git hash-object -w /tmp/base.csproj                      # -> <blob>
git update-index --cacheinfo 100644,<blob>,CrossBuy/CrossBuy.csproj
```

The working-tree file keeps the parallel team's WIP; the index carries HEAD + our two ItemGroups only.

## 4. Exact `CrossBuy.sln` changes

Two `Project` blocks, after the existing `CrossBuy.Tests` block:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CrossBuy.Analyzers", "CrossBuy.Analyzers\CrossBuy.Analyzers.csproj", "{3F1503B2-FA02-41FB-B30A-22ED6576D266}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CrossBuy.Analyzers.Tests", "CrossBuy.Analyzers.Tests\CrossBuy.Analyzers.Tests.csproj", "{2BCDBF22-B70D-4E6B-95BD-FE337CC2B9BD}"
EndProject
```

And in `GlobalSection(ProjectConfigurationPlatforms) = postSolution`, for each of the two GUIDs, the same six lines the
existing projects carry:

```
		{GUID}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{GUID}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{GUID}.Debug|x64.ActiveCfg = Debug|Any CPU
		{GUID}.Debug|x64.Build.0 = Debug|Any CPU
		{GUID}.Debug|x86.ActiveCfg = Debug|Any CPU
		{GUID}.Debug|x86.Build.0 = Debug|Any CPU
		{GUID}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{GUID}.Release|Any CPU.Build.0 = Release|Any CPU
		{GUID}.Release|x64.ActiveCfg = Release|Any CPU
		{GUID}.Release|x64.Build.0 = Release|Any CPU
		{GUID}.Release|x86.ActiveCfg = Release|Any CPU
		{GUID}.Release|x86.Build.0 = Release|Any CPU
```

**`TestRun` is absent from the solution's configuration list**, exactly as it is for the existing two projects — it is
a project-level configuration invoked as `dotnet build -c TestRun`, which both new projects already declare. Adding
`TestRun` to the solution would be a change to the parallel team's build surface and is not required.

The two GUIDs above are freshly generated and reserved by this document, so the eventual edit does not have to invent
them and cannot collide.

## 5. Exact `.editorconfig` (new file, repo root)

```ini
# Stage 2A Batch 00-A — authorization guardrail severities.
#
# The analyzer ships every rule at Warning (its dogfood default). Escalation is HERE, not in the analyzer, so one
# build of it can warn in the IDE and fail CI. Proven by test: CBA001 overridden to error is reported as an error.

[*.cs]

# Enforced. New debt, a stale allowance, and any suppression break the build.
dotnet_diagnostic.CBA001.severity = error
dotnet_diagnostic.CBA004.severity = error
dotnet_diagnostic.CBA006.severity = error

# Informational. CBA002 currently fires on 124 pre-existing endpoints; making it a warning today would add 41% to
# an already-303-warning build. It becomes a warning in phase 3, once the count is worked down (see the plan).
dotnet_diagnostic.CBA002.severity = suggestion
dotnet_diagnostic.CBA003.severity = warning
dotnet_diagnostic.CBA005.severity = warning
```

`CBA003` and `CBA005` are Warning from day one because both are **zero** on the current tree — they can only fire on
something new, so they add no noise and catch a real regression. That asymmetry is the whole reason severities are
per-rule rather than per-category.

## 6. Warning → error escalation strategy

| Rule | Current count | Day-1 severity | Rationale |
|---|---|---|---|
| CBA001 new debt | **0** | **error** | zero today, so error costs nothing and is the entire point of the guardrail |
| CBA004 stale baseline | **0** | **error** | zero today; a stale allowance must not survive a wave |
| CBA006 suppression | **0** | **error** | there is no legitimate suppression; the baseline is the review channel |
| CBA003 unrecognised guard | **0** | warning | zero today; a new access service should be noticed, not block |
| CBA005 undeclared anonymous | **0** | warning | zero today |
| CBA002 auth-without-authz | **124** | suggestion → warning at phase 3 | the only rule with a backlog |

The principle: **a rule at zero today is escalated immediately; a rule with a backlog is escalated after the backlog,
never before.** Escalating CBA002 first would train the team to ignore CBA ids, which would then hide CBA001.

## 7. Rollout plan

| Phase | Action | Exit condition |
|---|---|---|
| **0** | Owner approves shared-file modification. Notify the parallel team that the build will gain an analyzer. | acknowledgement |
| **1** | Apply §3 + §4 + §5 via plumbing. Build locally. | `dotnet build CrossBuy.sln -c Debug` succeeds; CBA001/004/006 = 0; CBA002 appears as suggestions only |
| **2** | Verify enforcement is live by a **deliberate temporary regression**: add a `[HttpPost]` action with no authorization, confirm the build FAILS with CBA001, then revert. | the build fails, then succeeds after revert |
| **3** | Work CBA002 down (Wave 2). When it reaches an agreed threshold, raise it to warning. | agreed threshold |
| **4** | Wire into CI when the CI decision unblocks; CI additionally sets `CROSSBUY_SQL_REQUIRED=1`. | CI red on a seeded violation |

Phase 2 is not optional. An analyzer that is wired but silently not loading looks exactly like an analyzer that is
wired and finds nothing — and a netstandard/Roslyn version mismatch produces precisely that. The only way to know it is
enforcing is to make it fail on purpose once.

## 8. Rollback plan

Rollback is **complete and immediate at three levels**, in increasing severity:

1. **Severity only** — set the three `error` lines in `.editorconfig` to `suggestion`. The analyzer still runs and
   still reports; nothing breaks the build. No rebuild of anything is needed. *This is the first response to any
   surprise.*
2. **Disable the analyzer, keep the projects** — delete the `.editorconfig` `[*.cs]` CBA lines, or add
   `<NoWarn>$(NoWarn);CBA001;CBA002;CBA003;CBA004;CBA005;CBA006</NoWarn>`. Build returns to its previous behaviour.
3. **Full revert** — remove the two `ItemGroup`s from `CrossBuy.csproj`. The analyzer projects remain in the tree and
   their tests keep running, so the measurement capability survives a rollback of the enforcement. That separation is
   deliberate: rolling back a gate should not cost the evidence.

No database, migration, generated file or runtime artifact is involved, so **no rollback step can fail partway**.
Nothing in this plan changes application behaviour: an analyzer influences the build, never the emitted IL.

## 9. Expected build impact — measured, not estimated

Recorded in `docs/architecture/evidence/Roslyn-Analyzer-Build-Impact.csv`, generated by a test.

| Measurement | Value |
|---|---|
| Syntax trees in the application compilation | **373** |
| Metadata references | 414 |
| Full application rebuild today (`-t:Rebuild -c TestRun`) | **65 s** |
| Analyzer pass over the whole compilation | **≈ 4.4 s** |
| Existing build warnings | **303** |
| New diagnostics added | **124** (all CBA002; suggestion severity in phase 1) |

**Expected impact: about +4.4 s on a full rebuild, roughly +7%.** Incremental builds pay it only when the C# compilation
actually re-runs; a Razor-only or unchanged build pays nothing.

**Honest caveat about one figure:** the CSV also records `compile_only_ms = 19`, which is **not** a valid baseline —
that read hits Roslyn's cached diagnostics from an earlier test on the same shared compilation. The number to use is
the 4.4 s analyzer pass against the 65 s rebuild, not a 19 ms → 4354 ms ratio. Stated because the CSV is checked in and
someone will otherwise quote it.

## 10. Expected CI impact

CI does not exist yet (**Blocked Pending Platform Decision** — there is no CI configuration in the repository), so this
is what the eventual pipeline must do rather than a change to something running.

| Item | Expectation |
|---|---|
| Wall clock | +4.4 s per solution build — negligible against test execution (2 m 37 s for the suite) |
| Failure modes CI gains | CBA001 (new debt), CBA004 (stale allowance), CBA006 (suppression) become **red builds** |
| Required environment | `CROSSBUY_SQL_REQUIRED=1` so an unconfigured SQL instance is a failure, not 72 skips (Batch 00-C guard 4) |
| Required inputs | `engineering/authorization-baseline.json` and `engineering/required-evidence-manifest.json` must be present in the checkout — both are checked in |
| Artifacts to publish | `Roslyn-Authorization-Inventory.csv`, `Roslyn-Analyzer-Diagnostic-Profile.csv`, `SQL-Evidence-Summary.md` |
| What CI must NOT do | run `dotnet build` **without** the `AdditionalFiles` line — CBA001/CBA004 degrade to silent, and CI would pass while enforcing nothing |

That last row is the one real trap in this plan. The degradation is deliberate and tested, but it means a
misconfigured pipeline is **green and useless**. Phase 2's deliberate-regression check is the countermeasure, and it
should be a permanent CI step (a seeded violation in a scratch branch), not a one-off.

## 11. Summary

Nothing here is speculative: the analyzer is built, 87 of its tests pass, it reproduces 388 / 157 / 88 / 143 exactly,
and the escalation to Error is proven by test. The only thing standing between this document and an enforcing build is
approval to edit two shared files.
