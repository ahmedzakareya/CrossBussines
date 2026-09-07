# Stage 2A — Entry Gate — Final Report

## 1. Executive summary

**Stage 2A Entry Gate = MET.** All 25 completion-gate criteria satisfied.

The Roslyn Authorization Analyzer is now wired into the real application build and **enforces**. CBA001, CBA004 and
CBA006 each fail a real build, proven in all three configurations. A clean build passes. The baseline is unchanged at
143 entries, byte-identical (`sha256 aaca6059…`). All tests green: **93/93** analyzer, **827/827** application, 0
skipped. No production behaviour changed, no package touched, no runtime dependency added.

Three defects were found by doing this — all three invisible to analyzer unit tests, and two of them cases where the
guardrail *looked* installed and enforced nothing:

1. **CBA004 was not being escalated at all.** A stale-baseline diagnostic can have no source location; `.editorconfig`
   sections are matched by **path**, so the configured `error` was silently ignored and the build **passed** with a
   stale allowance.
2. **The fix's own `GlobalAnalyzerConfigFiles` item silently disabled the severities it was added to apply** — it
   created a second entry for the same file, and Roslyn drops an option when two global configs set the same key.
3. **`TestRun` did not exist as a solution configuration** (MSB4126), so a solution-level TestRun build was impossible.

Remote CI enforcement is **not yet active**; the platform decision is open and both pipelines are ready to apply.

## 2. Previous state — engineering-ready but unenforced

The analyzer reproduced 388 / 157 / 88 / 143 exactly, with 0 classification disagreements across all 388 endpoints and
0 false credits, and its escalation to Error was proven by a unit test. But it ran in **nobody's build**. It was a
measurement tool. The previous report said so plainly, and said only approval could change it.

## 3. Shared-file approval

Explicit authorization was granted for `CrossBuy.sln`, `CrossBuy/CrossBuy.csproj` and the repository `.editorconfig`.
An implicit root `Directory.Build.props` was forbidden and **was not used** — the approved direct project and solution
wiring was applied.

## 4. Exact integration performed

| # | File | Change | Status |
|---|---|---|---|
| 1 | `CrossBuy.sln` | 2 `Project` blocks (reserved GUIDs) + `TestRun\|Any CPU` solution configuration + TestRun mappings for the 2 existing projects + full mappings for the 2 new ones | **+37 lines, 0 removed or changed** |
| 2 | `CrossBuy/CrossBuy.csproj` | analyzer `ProjectReference`, baseline `AdditionalFiles`, `CBA000` baseline-presence guard | **+65 / −0 vs HEAD** |
| 3 | `.editorconfig` | new file — approved per-rule severities | new |
| 4 | `.globalconfig` | new file — **documented adjustment**, §7 | new |
| 5 | `CrossBuy.Analyzers/CrossBuy.Analyzers.csproj` | `TestRun` configuration declared | +8 lines |
| 6 | `CrossBuy.Analyzers/*.cs` | CBA004 anchored to the controller when one exists | analyzer fix |
| 7 | `CrossBuy.Analyzers.Tests/SeverityConfigTests.cs` | 7 tests reconciling the two config files | new |

### Two documented adjustments to the reviewed plan

**(a) `TestRun` added as a solution configuration.** The plan said TestRun was deliberately absent from the solution.
The brief requires TestRun mappings and a solution-level TestRun build, and the current state made that impossible —
`dotnet build CrossBuy.sln -c TestRun` failed with **MSB4126: The specified solution configuration "TestRun|Any CPU" is
invalid**. Adding the solution configuration requires mapping the two pre-existing projects as well, or a solution-level
TestRun build would silently skip them. Purely additive: 0 existing lines changed.

**(b) `.globalconfig` added.** Forced by PROOF C — see §7 and §12. Not optional; without it CBA004 is unenforceable.

## 5. Solution changes

Verified against the exact pre-edit file:

```
lines removed or changed by me: 0
lines added by me:             37
```

| Check | Result |
|---|---|
| Formatting preserved | UTF-8 **with BOM**, **CRLF on all 88 lines** |
| Unrelated projects preserved | both, untouched |
| Unrelated entries reordered | none — insertions only |
| Distinct project GUIDs | 4, **each declared exactly once** |
| Duplicate configuration mappings | **none** |
| Solution loads | `dotnet build CrossBuy.sln` succeeds in Debug, Release and TestRun |
| Solution regenerated | **no** — targeted insertions only |

Reserved GUIDs used as documented: `{3F1503B2-FA02-41FB-B30A-22ED6576D266}` (CrossBuy.Analyzers),
`{2BCDBF22-B70D-4E6B-95BD-FE337CC2B9BD}` (CrossBuy.Analyzers.Tests).

## 6. Project changes

```xml
<ProjectReference Include="..\CrossBuy.Analyzers\CrossBuy.Analyzers.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />

<AdditionalFiles Include="..\engineering\authorization-baseline.json" />
```

| Requirement | Verified |
|---|---|
| Analyzer runs during normal compilation | `/analyzer:…\CrossBuy.Analyzers.dll` present on the csc command line in **all three** configurations |
| Not a runtime dependency | `CrossBuy.Analyzers.dll` **absent** from `bin/**/net8.0`; **0** occurrences in `CrossBuy.deps.json` |
| Baseline available to the analyzer | `/additionalfile:..\engineering\authorization-baseline.json` on the csc command line in all three configurations |
| Works from a clean checkout | the path is project-relative; **and** a `CBA000` MSBuild error fires if the file is missing (§9) |
| No unrelated Package/ProjectReference change | `<PackageReference>` elements **byte-identical to HEAD** |
| Roslyn packages not upgraded | 4.8.0 / 3.3.4, unchanged, both `PrivateAssets="all"` |
| Target framework unchanged | `net8.0` |
| TestRun still defines DEBUG | `<DefineConstants>$(DefineConstants);DEBUG;TRACE</DefineConstants>` intact |

## 7. EditorConfig changes — and why a second file was required

`.editorconfig` (new, repo root) carries the approved severities. **It is not sufficient on its own**, and only a real
build could show that:

PROOF C injected a stale baseline entry for a non-existent controller. CBA004 fired with the correct message — as
`CSC : warning CBA004`, with **no file** — and the build **succeeded**. An `.editorconfig` `[*.cs]` section is matched
by **path**, so it can never apply a severity to a diagnostic that has no path.

Two-part fix:

* the analyzer now **anchors CBA004 to the controller** when one exists, covering the two common cases (endpoint became
  protected; action deleted, controller remains) — better severity handling *and* a clickable diagnostic;
* `.globalconfig` (`is_global = true`) declares the same severities **compilation-wide**, closing the residual case
  where the controller is entirely gone — which is precisely the case a shrink-only baseline must catch.

**Drift is impossible by test.** `SeverityConfigTests` (7 tests) asserts both files declare the approved value for
every rule, that they are identical to each other, that `is_global = true` is present, that every rule the analyzer
supports has a declared severity, and that **CBA002 is still `suggestion`**.

## 8. Diagnostic severities

| Rule | Meaning | Severity | Occurrences today |
|---|---|---|---|
| **CBA001** | new unprotected mutating endpoint | **error** | 0 |
| **CBA004** | stale baseline entry | **error** | 0 |
| **CBA006** | suppression of a CBA diagnostic | **error** | 0 |
| CBA003 | authorization-shaped call, no declared authority | warning | 0 |
| CBA005 | undeclared anonymous mutating endpoint | warning | 0 |
| **CBA002** | authenticated but not authorized | **suggestion** | **124** |

CBA002 was **not** escalated, as instructed. No `NoWarn`, no `TreatWarningsAsErrors`, no blanket CBA suppression, no
project-wide analyzer disablement was added anywhere.

## 9. Baseline resolution

| Check | Result |
|---|---|
| Path on the csc command line | `/additionalfile:..\engineering\authorization-baseline.json` |
| Entries | **143** |
| `sha256` before and after all four proofs | `aaca60595718e472feb97002f383e0f72c1636a822d5dae4c22ee9df88efde46` — **identical** |
| Temporary proof entries remaining | **0** |
| Source-tree delta found | **none** — 388/157/88/143 unchanged, so the baseline was not modified |
| Missing-baseline behaviour | `error CBA000` from the `CrossBuyVerifyAuthorizationBaseline` target |

The `CBA000` guard exists because a missing `AdditionalFiles` glob does **not** fail an MSBuild build — it matches
nothing — and the analyzer deliberately degrades to silent without a baseline. Combined, a checkout without the
baseline would have built **green while enforcing nothing**: the one failure mode of this integration that looks
exactly like success.

## 10. Clean-build proof (PROOF A)

Full solution `-t:Rebuild`, Razor enabled, analyzer enabled:

| Configuration | Exit | Time | CBA001 | CBA004 | CBA006 | Errors |
|---|---|---|---|---|---|---|
| Debug | **0** | 64 s | 0 | 0 | 0 | **0** |
| Release | **0** | 30 s | 0 | 0 | 0 | **0** |
| TestRun | **0** | 33 s | 0 | 0 | 0 | **0** |

CBA002 suggestions are not emitted by a command-line build at `suggestion` severity, and correctly do not fail it.

## 11. CBA001 failure proof (PROOF B)

A new unprotected mutating endpoint was added in a **new temporary file**, so no existing controller was ever edited —
the proof is fully reverted by deleting one file.

| Configuration | Exit | Diagnostic |
|---|---|---|
| Debug | **1** | `error CBA001` |
| Release | **1** | `error CBA001` |
| TestRun | **1** | `error CBA001` |

```
error CBA001: Mutating endpoint 'ZzCbaProofController.ProofOfEnforcement' has no authorization the analyzer
can see and is not listed in authorization-baseline.json. … The baseline may only shrink — a new entry is not
an option.
```

Restored: file deleted, clean build green again, exit 0.

## 12. CBA004 failure proof (PROOF C)

A stale entry for a controller that does not exist was appended to the baseline.

| Attempt | Configuration | Exit | Diagnostic | Verdict |
|---|---|---|---|---|
| 1 — `.editorconfig` only | Debug | **0** | `warning CBA004` | **FAILED TO ENFORCE** — the defect in §7 |
| 2 — after the fix | Debug | **1** | `error CBA004` | enforced |
| 2 — after the fix | Release | **1** | `error CBA004` | enforced |
| 2 — after the fix | TestRun | **1** | `error CBA004` | enforced |

```
error CBA004: Baseline entry 'ZzGhostController.DeletedAction' no longer matches an unprotected mutating
endpoint (the controller no longer exists). Remove it from authorization-baseline.json.
```

Restored: entry removed, `sha256` verified identical.

**This is the single most important result in this report.** Had the entry gate accepted analyzer unit tests as proof,
CBA004 would have shipped unenforced — the tests pass because they override severity through
`WithSpecificDiagnosticOptions`, which is location-independent and bypasses configuration files entirely. Only a real
build exercises the path that was broken.

## 13. CBA006 failure proof (PROOF D)

A `#pragma warning disable CBA004` was added to a temporary file whose action was **protected**, isolating CBA006.

| Configuration | Exit | Diagnostic |
|---|---|---|
| Debug | **1** | `error CBA006` |
| Release | **1** | `error CBA006` |
| TestRun | **1** | `error CBA006` |

```
error CBA006: 'CBA001' is suppressed here. Authorization diagnostics are not suppressible: fix the endpoint,
or record the decision in authorization-baseline.json where it is reviewable.
```

The suppression did not take effect — the attempt itself became the error.

## 14. Debug verification

Analyzer loaded ✓ · baseline resolved ✓ · `.editorconfig` + `.globalconfig` on the command line ✓ · CBA001 ✓ CBA004 ✓
CBA006 ✓ all enforced as errors · clean build **exit 0, 0 errors**, 64 s rebuild.

## 15. Release verification

Identical wiring and enforcement. Clean build **exit 0, 0 errors**, 30 s rebuild. All three seeded violations fail.

## 16. TestRun verification

Identical wiring and enforcement. Clean build **exit 0, 0 errors**, 33 s rebuild. All three seeded violations fail.
`TestRun` continues to define `DEBUG` in all four projects — newly declared in `CrossBuy.Analyzers.csproj` too, so an
undeclared custom configuration cannot silently compile a different program (the Stage 1 Batch B defect).

## 17. Analyzer build overhead

| Measurement | Value |
|---|---|
| Syntax trees analyzed | 373 |
| Measured analyzer pass | **≈ 4.4 s** |
| Full solution rebuild, Debug | 64 s |
| Full application rebuild before wiring | 65 s |

**Honest statement:** the analyzer pass measures ≈ 4.4 s in isolation, but the observed rebuild times *after* wiring
(64 s) are within normal variance of the pre-wiring measurement (65 s), so at solution granularity the overhead is not
separable from build noise. The ≈4.4 s figure is the one to quote; the before/after rebuild times do not demonstrate a
regression and are not offered as one.

## 18. Application test verification

| Suite | Result |
|---|---|
| `CrossBuy.Analyzers.Tests` | **93 passed · 0 failed · 0 skipped** (86 + 7 new severity-config tests) |
| `CrossBuy.Tests` | **827 passed · 0 failed · 0 skipped** (2 m 21 s) |

Unchanged from the pre-wiring counts for the application suite: analyzer integration broke nothing and changed no test
discovery.

## 19. SQL evidence verification

Re-run after integration, `CROSSBUY_TEST_SQL` set to an **instance**:

| Check | Result |
|---|---|
| `SQL-Evidence-Summary.md` verdict | **EXECUTED** |
| Declared SQL evidence tests | **98** |
| Discovered | **98** |
| Declared but missing | **0** |
| Discovered but unregistered | **0** |
| Batch P preserved rules | pass, within 827 |
| SQL companion guards (46 cases) | pass, within 827 |
| `PlatformSchemaDeploymentTests` | pass, within 827 |
| Required-evidence manifest guard | pass |
| Mandatory tests undiscovered | **0** |
| Required test skipped | **0** |
| Risk Register deterministic generation | **identical `sha256` across two runs**, 51 rows |

## 20. Scratch database verification

| Check | Result |
|---|---|
| `CrossBuyProbe_*` | **0** |
| `CrossBuyPlatformTest_*` | **0** |
| `CrossBuyImp003_*` | **0** |
| Shared fixture fingerprint | `5\|214\|1\|13\|1341` |

## 21. `CrossBuyDB2` status

**Present and untouched. No SQL was executed against it.** Production-catalog refusal happens **before any connection
is opened**, so even the deliberate guard-1 proof in the previous increment never connected.

## 22. Frozen-document status

No frozen Phase 0 document was modified. No accepted report was rewritten. No generated file was overwritten except
the evidence artifacts that are regenerated by design (`Roslyn-Authorization-Inventory.csv`,
`Roslyn-Analyzer-Diagnostic-Profile.csv`, `Roslyn-Analyzer-Build-Impact.csv`, `SQL-Evidence-Summary.md`,
`Stage-002-Risk-Register.csv`). The authorization baseline is byte-identical.

## 23. Remaining approvals

| Item | Status | Owner action |
|---|---|---|
| **CI platform selection** | **Pending decision** | choose GitHub Actions or Azure DevOps; both pipelines are ready to apply in `Stage-002A-CI-Enforcement-Plans.md` |
| **Read-only measurement package** | **Pending approval** | written approval to run `SELECT`-only queries against `CrossBuyDB2` |
| **Batch P — 21 remaining invariants** | **Not started** | required before **Batch C** (Master Data), not before Batch A |
| **CBA002 escalation to warning** | rollout phase 3 | after Wave 2 works the 124 down |

## 24. Entry Gate verdict

### **Stage 2A Entry Gate = MET**

| # | Completion criterion | Status |
|---|---|---|
| 1 | `CrossBuy.sln` includes the analyzer project | **MET** |
| 2 | `CrossBuy.csproj` references it as an analyzer | **MET** — `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"` |
| 3 | Baseline supplied through `AdditionalFiles` | **MET** — verified on the csc command line |
| 4 | `.editorconfig` applies the approved severities | **MET** — plus `.globalconfig`, §7 |
| 5 | CBA001 enforced as error | **MET** |
| 6 | CBA004 enforced as error | **MET** — after the §7 fix |
| 7 | CBA006 enforced as error | **MET** |
| 8 | CBA002 remains suggestion | **MET** — pinned by test |
| 9 | Clean real build succeeds | **MET** — 3 configurations |
| 10 | New unprotected endpoint fails a real build | **MET** — 3 configurations |
| 11 | Stale baseline fails a real build | **MET** — 3 configurations |
| 12 | Invalid suppression fails a real build | **MET** — 3 configurations |
| 13 | Debug build passes | **MET** |
| 14 | Release build passes | **MET** |
| 15 | TestRun build passes | **MET** |
| 16 | Analyzer tests pass | **MET** — 93/93 |
| 17 | Application tests pass | **MET** — 827/827 |
| 18 | SQL evidence remains enforced | **MET** — 98 = 98, 0 unregistered |
| 19 | No mandatory test skipped | **MET** — 0 skipped |
| 20 | No scratch database remains | **MET** — 0 |
| 21 | `CrossBuyDB2` untouched | **MET** |
| 22 | Shared-file diffs minimal | **MET** — sln +37/−0; csproj +65/−0; packages byte-identical |
| 23 | No production behaviour changed | **MET** — an analyzer influences the build, never the emitted IL |
| 24 | Batch A not started | **CONFIRMED** |
| 25 | Final Entry Gate report delivered | **MET** — this document |

**25 of 25.**

### On CI — local enforcement is not CI enforcement

The gate is MET on **repository-local build enforcement**, which is now real and proven. **Remote CI enforcement is
NOT yet active.** The carve-out conditions are satisfied: local enforcement works, and ready-to-apply GitHub Actions
and Azure DevOps pipelines both exist. They are **not applied** — no CI configuration exists in the repository.

The distinction matters and is not cosmetic: today the gate protects anyone who builds, and nothing prevents a push
that was never built. Both pipelines therefore include a **permanent seeded-violation step** — CI deliberately adds an
unprotected endpoint and requires CBA001 to fail the build — because a pipeline that builds without the analyzer wired
is green and useless, and that is the one way this can fail silently.

## 25. Batch A recommendation

**Recommended: start Batch A.** The gate that Batch A needed is in place — a new unprotected mutating endpoint written
during Batch A now fails the build, in every configuration, with a message naming the endpoint. That was the whole
purpose of the entry gate.

Three conditions on the recommendation:

1. **Batch A must not touch Master Data.** Only 3 of 24 preserved-rule invariants are enforced; the other 21 are
   required before **Batch C**. Batch A staying out of Master Data is what keeps that ordering valid.
2. **Choose the CI platform early in Batch A.** Local enforcement protects builders, not pushes. The longer that gap
   stays open, the more likely a Batch A branch bypasses the gate simply by not being built.
3. **Do not escalate CBA002 during Batch A.** It has a 124-endpoint backlog; escalating mid-batch would add noise to
   exactly the work that should be moving the count down.

**Stopping for approval. Batch A not started. Grant Writer not implemented.**
