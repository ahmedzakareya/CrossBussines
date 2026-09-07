# Stage R1 — Integration Baseline

**Run without exclusions. Honestly reported: the tree does NOT build in Release or TestRun, and another tab is mid-write.**

**No green integration baseline is claimed.**

---

## 1. Measured

| Gate | Result |
|---|---|
| **Debug build** | **0 Error(s)**, 296 Warning(s) |
| **Release build** | **2 Error(s)** — FAIL |
| **TestRun build** | **4 Error(s)** — FAIL |
| **Analyzer build** | **0 Error(s)** |
| **Analyzer tests** | 9 / 9 new Reporting-authority tests pass **in an isolation harness**; the full 93+9 suite could not run (§3) |
| **Full application suite** | **NOT RUN** — requires a TestRun build |
| **Ownership gate** | Operational; attributes every failing file correctly |
| **Integration gate** | **FAILS** at checks 2 and 3 (Release, TestRun) |
| **SQL governance gate** | **FAILS**, exit 2 — 4 violations, 2 not-deployable |
| **Documentation / reference validation** | **PASS** — 44 unique ADRs, all cross-references resolve, registries regenerate deterministically |
| **CBA001 / CBA004 / CBA006** | **Not measurable** — the analyzer cannot classify a compilation that does not complete |
| **Ownership violations** | 0 for TAB-1's own changes |
| **SQL governance violations** | 4 (divergent duplicates) + 2 (undeployable) |
| **Untracked production trees** | `BL/Workspace`, `BL/Reporting`, `BL/TasksCalendar`, `Models/Context/Reporting`, `Views/Workspace`, `Controllers/WorkspaceController.cs`, `Controllers/Api/ReportsCenterApiController.cs`, and the entire `BL/Platform` tree |
| **Shared-file conflicts** | None detected in TAB-1's changes |
| **Probe databases** | **0** |
| **CrossBuyDB2** | Present, **untouched** — no SQL executed against it |

## 2. Exact blocking files and their owners

| File | Error | Owner |
|---|---|---|
| `CrossBuy/BL/Reporting/ReportsCenterPresenter.cs:691` | `CS0029: cannot convert 'string' to 'decimal?'` | **TAB-2** |
| `CrossBuy/BL/Reporting/ReportsCenterPresenter.cs:692` | `CS0029: cannot convert 'string' to 'decimal?'` | **TAB-2** |
| `CrossBuy/BL/Reporting/ReportingRegistration.cs:156` | `CS0246: 'ReportingWorkspaceSource' not found` | **TAB-2** (consuming a TAB-6 Workspace type) |

Attribution was produced by the R1 ownership tool, not by inspection:

```
DENIED - these files belong to another tab
  CrossBuy/BL/Reporting/ReportingRegistration.cs  -> TAB-2 (Second Tab - Reporting Platform)
  CrossBuy/BL/Reporting/ReportsCenterPresenter.cs -> TAB-2 (Second Tab - Reporting Platform)
```

**Their implementation was not repaired.** R1 forbids business-feature work, and these files belong to another tab.

### A configuration-specific failure worth flagging

Debug reports **0 errors** while Release and TestRun report 2 and 4. The same source cannot legitimately compile in one configuration and not another unless conditional compilation symbols differ — and CLAUDE.md already records this exact class of defect: *"A custom build configuration must declare its compilation symbols. `TestRun` was undeclared, so `DEBUG` was undefined and every acceptance compiled a different program than Debug."*

`ReportingWorkspaceSource` resolving in Debug but not Release is the signature of that same problem recurring in TAB-2's new code. Recorded for TAB-2; not investigated further, as the file is theirs.

## 3. Why the analyzer suite could not run in the real project

`CrossBuy.Analyzers.Tests` holds a `ProjectReference` to `CrossBuy.csproj` — deliberately, so the reconciliation test can compile the application's own sources. While the application does not build, the analyzer test project cannot build either.

The nine new Reporting-authority tests were therefore executed in an **isolation harness** referencing only `CrossBuy.Analyzers`:

```
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9
```

**This proves the analyzer boundary and nothing else.** It does not exercise the application, and it does not substitute for the full 93-test suite plus reconciliation. Both must be re-run once the tree compiles. **No coverage is claimed from the tests that could not run.**

## 4. What a green baseline will require

1. TAB-2 fixes the three Reporting compile errors and confirms the Debug-vs-Release symbol discrepancy.
2. TAB-6 and TAB-3 settle the `CommActorDto` / `CommMentionHistoryItemDto` and `ReportingWorkspaceSource` contracts. A consumer never edits the producer's DTO.
3. Re-run: `build-gate.ps1` (expect 0/0/0), the full suite, the analyzer suite **in the real project**, and CBA001/004/006.
4. The SQL governance gate will still fail until the 4 divergent pairs and 2 undeployable scripts are resolved — those need their owners, not a build.

## 5. Honest summary

R1's own deliverables are complete and verified: the registries generate deterministically, the tools work and were proved to fire in both directions, the analyzer builds clean with the new authority, and the nine boundary tests pass.

**The integration baseline itself is red**, in files owned by other tabs, on the same day R1 was built. That is the third independent occurrence of RSK-05 during this programme, and it is the strongest available argument for the protected integration branch that R1 delivers but cannot enforce without an appointed owner and remote branch protection.
