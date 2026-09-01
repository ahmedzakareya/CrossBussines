# Clean-Clone Test Authority — Proof

The point of this batch: the number in the report and the number a clean clone produces must be the
same number. Everything below comes from a tree archived out of the stabilization HEAD, with no
file from any other worktree and no directory outside the repository on the discovery path.

## Discovery

| | value |
|---|---|
| `git ls-files CrossBuy.Tests` (`.cs`) | **186** |
| `.cs` present in the pristine tree | **186** |
| **untracked test files visible to the runner** | **0** ✅ |
| duplicate discovery | 0 (no machine-only file declared a class name already present in tracked tests — measured) |
| absolute-path dependencies | 0 |
| external-resource dependencies | the SQL probe seam only, via `CROSSBUY_TEST_SQL`; each suite creates and drops its **own** probe database |

`186 = 120 tracked before + 66 recovered`.

## Execution

```
Debug build      Build succeeded · 0 errors · 393 warnings
Release build    Build succeeded · 0 errors · 388 warnings
TestRun build    Build succeeded · 0 errors · 393 warnings

CrossBuy.Analyzers.Tests    111 passed · 0 failed · 0 skipped
CrossBuy.Tests            3 740 passed · 0 failed · 6 skipped   (3 746 discovered)

CBA001 0 · CBA002 0 emitted (configured `suggestion`) · CBA003 0
CBA004 0 · CBA005 0 · CBA006 0
```

Run **serially**, in one test process. No parallel run shares a mutable database.

## Before and after

| | canonical `dea7bf3` | stabilization HEAD |
|---|---|---|
| tracked test source files | 120 | **186** |
| tests executed from a clean clone | 2 540 | **3 740** |
| tests that existed only on one machine | ~1 400 | **0 that are Class A** |

## The six skips, named

All six are the same externally gated suite — a Python ML service that is not running. They are
skips for a missing external dependency, not failures anybody stopped looking at:

```
CrossBuy.Tests.AiRealPythonServiceE2ETests.Inventory_analysis_reaches_the_real_python_ml
CrossBuy.Tests.AiRealPythonServiceE2ETests.Cashflow_forecast_reaches_the_real_python_ml_and_returns_a_sane_projection
CrossBuy.Tests.AiRealPythonServiceE2ETests.Journal_anomaly_reaches_the_real_python_ml_and_flags_an_outlier
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_real_service_rejects_a_malformed_payload
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_real_service_refuses_a_missing_or_wrong_shared_secret
CrossBuy.Tests.AiRealPythonServiceE2ETests.The_same_free_text_payload_is_refused_to_an_external_processor
```

## What is still only on one machine

**40 files** remain unrecovered and are classified in `MISSING-TEST-INVENTORY.md` — 2 obsolete and
38 blocked. None is silently ignored; each has a reason and, where one exists, the decision that
would unblock it. The count that matters is that **zero of them are needed for the 3 740 above**:
the clean clone is now the authority, not a subset of it.
