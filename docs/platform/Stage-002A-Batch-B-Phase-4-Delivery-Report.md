# CrossBusiness Platform — Stage 2A Batch B — Phase-4 Delivery Report

## 1. Executive summary

Phase 4 is **complete**. The CRM visible-owner typed contract, the compatibility-read matrix and all five outstanding
evidence documents are on disk and verified. **935 / 935 application tests · 93 / 93 analyzer tests · 0 failed ·
0 skipped.**

**B6 was not started in this increment.** The five documents were the mandated prerequisite and consumed the available
capacity. Converting three financial authorization sites — with test-first behaviour preservation and four mutation
proofs — is the next increment, not a remainder to be squeezed in after documentation.

Three of five Mechanism A sites are technically ready. Two CRM sites are blocked on business decisions that are stated
here and not guessed.

## 2. Official product name

**CrossBusiness Platform** is used in every new document produced in this increment. No production identifier,
namespace, assembly, route, database name or API was renamed — forward naming policy only.

## 3. Starting state

Batch A · B0 · B1 · B2 · B3 · B4 · B5 · B7 all complete. B6 Not Started. Production authorization behaviour unchanged;
all five Mechanism A sites unchanged. Five Phase-4 documents missing.

## 4. Delivered work

| Item | Evidence |
|---|---|
CRM visible-owner typed contract | `CrmVisibleOwnerScope.cs` `663cd9aa49a81bc7` + **22 tests** |
Compatibility-read matrix | 13 rows, all required columns, 4 decisions flagged |
Seed-plan reconciliation (41/32/4) | source-derived correction |
B2 storage evidence | references `99c22d9eab59cd98`, `09471492be657f09` |
B3 seed evidence | references `ba307f516a139b98`, `3992dde719978485` |
B4 reader evidence | references `8900589605881eb5`, `9216b19729e7f04c` |
Phase-4 preservation report | archive `2b13f7b0d729…`, **12 files**, 0 mismatches |
Phase-4 disk-state manifest | 9 rows with hashes (source/documents; 3 later-added report files bring the archive to 12) |

## 5. CRM typed contract

`CrmVisibleOwnerScopeResult` with four states. The problem it solves: `VisibleOwnerIdsAsync` returns `HashSet<int>?`
where **`null` = unrestricted** and **empty = no access** — the two opposite extremes, one keystroke apart, both
mistakes compiling silently. `?? new HashSet<int>()` turns *see everything* into *see nothing*; the inverse turns *see
nothing* into *see everything* on a CRM database.

Two deliberate design choices worth review: an **invalid owner id fails the whole scope** rather than being filtered
(filtering hides the defect that produced it), and **`ToLegacy` maps `ConfigurationError` to an empty set, never
`null`** (mapping a fault to `null` would turn a misconfiguration into company-wide CRM visibility).

Production `CrmAccessService` is unchanged — asserted by reflection so a premature signature change fails the test.

## 6. Compatibility matrix

13 rows across Accounting (5), Inventory (5), CRM (3 + owner scope). Classifications: **8 NeverBootstrapOpen ·
4 LegacyCompatibilityRead · 1 RequiresBusinessDecision**. No row is labelled `LegacyRole` — unconditional authenticated
access is not role authorization, and calling it that is what let this exposure sit unexamined.

## 7. The 41 / 32 / 4 correction

**41** superseded historical planning figure (not reproducible from live vocabularies — 32/35/45/46 all attempted).
**32** live eligibility inventory. **4** implemented seed set. Owner decision after the discrepancy was reported; the
number was not forced in either direction.

## 8. Five Mechanism A sites

| # | Site | Status |
|---|---|---|
1 | `AccountingAccessService.cs:60` | unchanged — **ready** for B6 |
2 | `InventoryAccessService.cs:48` | unchanged — **ready** |
3 | `InventoryAccessService.cs:91` | unchanged — **ready** (narrowing) |
4 | `CrmAccessService.cs:59` | unchanged — **blocked** |
5 | `CrmAccessService.VisibleOwnerIdsAsync` | unchanged — **blocked** |

## 9. Fifteen Never actions

Accounting 4 (`post`, `pay`, `manage`, `currency-override`) · Inventory 4 (`doc`, `purchase`, `manage`,
`warehouse-access`) · CRM 1 (`manage`) · Platform 1 (`PlatformOps`) · Projects 1 (`billing`) · HR 2
(`payroll-manage`, `confidential-view`) · Tasks 1 (`manage`) · Communication 1 (`outbox-manage`).

One authoritative list — `NeverBootstrapOpen.All` — never forked, and pinned by a test.

## 10. Four business decisions

1. **`Accounting.read`** exposes ledger balances to any authenticated employee, no branch filter.
2. **`Inventory.read`** exposes stock costs, no branch or warehouse filter.
3. **`Crm.read` / `Crm.edit`** expose and mutate customer data for unconfigured companies.
4. **`VisibleOwnerIdsAsync`** returns unrestricted owner scope for unconfigured companies.

Each carries options and a recommendation in the matrix. **Only 3 and 4 block B6**; 1 and 2 may proceed with behaviour
preservation and explicit risk recording.

## 11. The `Crm.edit` finding

The `:59` bootstrap return precedes the action switch, so an unconfigured company genuinely permits `edit` today — even
though the switch comment says *"CrmViewer (or no role) → read-only"*, which describes the **configured** path only.

Omitting it from the seed would make B6 a **silent tightening**, not behaviour preservation. It is seeded and marked
`ReviewExpected` because it is the **only mutating action in the plan**.

## 12. The CRM visible-owner finding

`null` means unrestricted **including for unconfigured companies** — company-wide CRM visibility with no role
configured. The typed contract makes the three states explicit and fails closed; the policy choice is not made.

## 13. Tests

| Suite | Result |
|---|---|
Application | **935 passed · 0 failed · 0 skipped** |
Analyzer | **93 passed · 0 failed · 0 skipped** |
B2 + B4 (shared class) | 44 |
B3 seed | 20 |
CRM typed contract | 22 |

## 14. Analyzer

CBA001 / CBA004 / CBA006 = **0 / 0 / 0**. No baseline additions, no suppressions.

## 15. Manifest

**190 enforced tests.** Guards 5 and 6 clean. **Guard 6 caught a real gap in my own registration** — a 6-line lookahead
silently skipped a method carrying 14 `InlineData` rows. Widened to 40 lines.

## 16. Disk-state verification

9 files hashed in `Stage-002A-Phase-4-Disk-State-Manifest.csv`; the archive contains **12** — the three
report/manifest files were added after the manifest was generated. Both counts are stated rather than reconciled to a
single tidy number, because the manifest is a snapshot and the archive is the superset.

## 17. Preservation

Phase-4 archive `2b13f7b0d72958cd…`, **12 files, restore-verified, 0 mismatches**. Phases 1–3 intact
(`2e0cfd558679`, `aa1ce5f1258b`, `b66aff246366`), none overwritten. Zero mutation markers.

## 18. Guard 3 findings

**Fired twice this increment; correct both times.** It caught a leaked `CrossBuyPlatformTest_*` fixture database and a
`CrossBuyProbe_BatchP_*` probe, left behind when test runs were interrupted and `DisposeAsync` never ran. Dropped with
per-name ownership re-verification. Earlier in Batch B it caught **106** leaked probes from the lost-source episode.

Without this guard, every one of those runs would have inherited state its tests did not create.

## 19. Known limitations

* **`bootstrap_access_policies.sql` is proven idempotent against a probe whose table came from the EF model**, not
  against a completely empty database created only by the script. The first-deploy `CREATE TABLE` path is not covered
  by a test.
* **Nothing consumes the reader or the seed rows.** B6 is inactive, so their effect is unproven end-to-end.
* **No production company has been seeded.**
* **The CRM contract is not wired in** — the adapter is proven, not applied. Cross-company detection only runs when the
  caller supplies the company owner set.
* **Shared-file preservation remains a patch, not a commit** — the parallel team's kernel is still uncommitted and
  interleaved with this work.

## 20. B6 readiness

Three of five sites ready. The reader, the four seeded policies, the Never list and the decision contract all exist and
are tested. What is missing is the conversion itself, its behaviour-preservation tests, and mutations E–H.

## 21. Sites approved for B6

`AccountingAccessService.cs:60` · `InventoryAccessService.cs:48` · `InventoryAccessService.cs:91` (narrowing).

## 22. Sites blocked from B6

`CrmAccessService.cs:59` — blocked on decision 3 (`Crm.edit` is mutating).
`CrmAccessService.VisibleOwnerIdsAsync` — blocked on decision 4.

## 23. Production behaviour status

**Unchanged.** All five Mechanism A sites carry their original code. No access service was modified in Phase 4.

## 24. Recommended next increment

B6 for the three approved sites **only**, test-first, with mutations E–H, and the four B6 documents. It is a
self-contained increment on the highest-risk code in the programme and deserves the whole of one.

## 25. Stop statement

Stopping for review. **B6 Not Started.** No Bootstrap Policy Writer API · no Security Console UI · no role migration ·
no Wave 2 · no Master Data change · Batch C not started. `CrossBuyDB2` present and untouched; 0 probes remain.
