# CrossBusiness Platform — Stage 2A B6 — Recovery Delivery Report

## Verdict

# STILL NOT VERIFIED

B6 remains **Not Verified** and **Not Delivered**. Phases 1 and 2 completed successfully; Phases 3–5 are **blocked by
another tab's in-progress code**, which prevents any build and therefore any valid test evidence.

No partial-success language is used below. Three of five sites are converted **on disk**; none of that is verified in
this increment.

---

## 1. What completed

| Phase | Status | Evidence |
|---|---|---|
**1 — Restore from verified artifact** | **COMPLETE** | 3/3 files match `c838003b1992c4b8…` byte-for-byte. `Stage-002A-B6-Recovery-Restore-Report.md` |
**2 — Re-derive the five Mechanism A sites** | **COMPLETE** | semantic reconciliation, every occurrence tied to its method. `Stage-002A-B6-Mechanism-Site-Reconciliation.md` |
**3 — Fresh build before tests** | **BLOCKED** | 2 cross-tab compile errors (§3) |
**4 — Re-establish 32/32 baseline** | **BLOCKED** | cannot build ⇒ no valid assembly ⇒ no accepted run |
**5 — Diagnose and re-prove Mutation H** | **BLOCKED** | same |

## 2. The correction that matters most

The previous increment reported that `InventoryAccessService.cs` had been damaged by a bad restore. **That diagnosis
was wrong.**

Hashes taken before restoring prove both production files were **already byte-identical** to the verified artifact:

| File | Before restore | Archive | Damaged? |
|---|---|---|---|
`AccountingAccessService.cs` | `2660fe293f7a0518` | `2660fe293f7a0518` | no |
`InventoryAccessService.cs` | `c4e83c0f1d1da748` | `c4e83c0f1d1da748` | no |
`B6BootstrapConversionTests.cs` | `841827a3a109e46b` | `e91e035e68b617b4` | **yes** |

The damaged file was the **test**, not the authorization code. The 6 failures came from a corrupted test file, which is
a materially different fault from corrupted financial authorization.

The wrong conclusion came from treating a **grep count as proof**: `grep -c 'AnyRoleConfiguredAsync(context.CompanyId,
cancellationToken)) return true'` returns 0 for both converted files, and 0 is the *correct* post-conversion value —
the conversion inverts the call's polarity, so that literal string is supposed to disappear. A hash against a verified
artifact is the fact; a grep count is a proxy that happened to point the wrong way.

## 3. The blocker — two other tabs, not this one

| Path | Missing type | Owning tab |
|---|---|---|
`CrossBuy/BL/Communication/CommThreadService.cs:59,69` | `ICommActorDirectory` | **THIRD TAB** — Communication Platform |
`CrossBuy/BL/Reporting/ReportPrintService.cs:155,160` | `IReportService` | **SECOND TAB** — Report Studio |

Both types are undefined anywhere in the repository — those tabs are mid-write. Per tab ownership I did **not** modify
their files, and I did not work around the break by excluding their code from compilation, because that would mean
editing a shared project file to hide another tab's state.

This is the **third** cross-tab break this session (the first cleared on its own when Report Studio landed
`ReportParameterSet`). It is now the dominant impediment to verifying security work.

## 4. Why no test result is reported

The brief is explicit: *"Never use `--no-build` for mutation evidence"* and *"Do not accept tests against stale
assemblies."* With the build failing, any test run would execute a stale assembly — which is exactly the trap that
produced the invalid Mutation H "pass" last increment, where a hidden build failure let `--no-build` run old code.

So no baseline and no mutation result is claimed. Reporting 32/32 from a stale DLL would be worse than reporting a block.

## 5. Mutation H — still unresolved, with the candidate causes narrowed

Not re-attempted (blocked). The prior evidence stands as **not accepted**:

* Attempt 1 — removing the policy call: **build failed**, `--no-build` ran a stale assembly ⇒ result void.
* Attempt 2 — policy present but `Disabled`: **compiled**, test still passed ⇒ a real anomaly.

Attempt 2 is the one to chase. `Disabled_denies_and_an_inactive_row_is_ignored` proves the reader denies a `Disabled`
policy independently, so `Accounting.read` returning allowed means one of:

1. the test's `PolicyAsync` helper wrote a row the reader does not match (scope/action/company mismatch);
2. `AnyRoleConfiguredAsync` returned true, routing to the role branch where `"read" => true` unconditionally;
3. fixture residue left a second active `LegacyCompatibility` row;
4. the assertion did not execute the path it appears to.

**Cause 2 is the most likely and the most serious**: if any accounting role row exists in the probe, `read` is granted
by the role switch and the policy is never consulted — which would mean the test never exercised the compatibility path
at all. That is a test-validity defect, not necessarily a conversion hole, but it must be settled before
`Accounting.read` preservation can be claimed.

## 6. Environment

| Check | Result |
|---|---|
Mutation markers in source | **0** |
Probe databases | **0** |
`CrossBuyDB2` | present and untouched; no SQL executed against it |
CRM sites 4 and 5 | **unchanged** — `:59 return true`, `:98 return null` verbatim |
Inventory role paths | **both preserved** (lines 206, 210) |
Parallel-tab files | **none modified** |
Batch C · Wave 2 · Security Console · Master Data | Not Started / unchanged |

## 7. Risk register

**Not updated.** No B6 risk moved. RISK-040/041/042/043/044/045 remain at unchanged severity, per the brief's
instruction for a recovery increment.

## 8. Exact blockers to clear, in order

1. **Cross-tab buildability.** The Communication and Report Studio tabs must land `ICommActorDirectory` and
   `IReportService`. Nothing in this tab can be verified until then. *Owner action: sequence the tabs, or give this tab
   a build that excludes in-progress work from other tabs.*
2. **Then** re-establish the focused baseline on a fresh build: 14 / 8 / 10 = 32.
3. **Then** diagnose Mutation H against the four candidates in §5 — instrument the test to assert that
   `AnyRoleConfiguredAsync` is false and the reader is invoked exactly once with the expected scope and action.
4. **Then** re-prove Mutation H, restore, and confirm 32/32.

## 9. Stop statement

Stopping for review. **B6 = Not Verified.** No final B6 delivery document written. No new B6 feature added. CRM not
converted. No risk status changed.
