# Stage Construction — R7 SQL Dependency Dataset

Status: AUTHORED, NOT COMMITTED. Companion to
`Stage-Construction-R7-SQL-Dependency-Dataset.csv`, which is the machine-readable form and the
integration reference. Slice names are the stable identifiers; no SQL registry numbers are assigned
here, because free IDs can only be allocated against the registry state at integration time.

## What this is, and why it exists as a file

The owner's ruling of 2026-11-04 placed the
`construction_r7_actual_cost_allocations` → `journal_functional_currency_snapshot` dependency in
"the R7 dependency dataset", and declined to add a slice-level dependency field to the registry
schema. At that moment no such dataset existed anywhere in the repository — the graph was session
analysis only. This file is that dataset, so the record has a home rather than moving from a
registry with no field to a dataset with no file.

It is **not** a registry. `governance/registry/sql-slices.json` records ownership and deployment
posture; `CrossBuy/deploy/sql/manifest.json` is the authored registry of record and expresses order
as the integer rank that `scan-sql-manifest.ps1` assigns. This file records the *reasons* behind
those ranks, which neither of the other two can hold.

## How the graph was derived

A dependency here is expressed three ways, and **only one of them is a foreign key**:

| Idiom | Example | Edges found |
|---|---|---|
| `REFERENCES dbo.X` | an ordinary FK | 20 |
| `IF OBJECT_ID('dbo.X','U') IS NULL` | refusal guard at the top of the slice | — |
| `IF COL_LENGTH('dbo.X','col') IS NULL` | prerequisite is a COLUMN another slice adds | — |
| **all three together** | | **28** |

An FK-only graph finds 20 of the 28 and returns a confidently wrong answer: on FK evidence alone
`journal_functional_currency_snapshot` looks like an unrelated Accounting file that happens to sit
inside the Construction rank block. It is not — see below.

## The ordering is load-bearing, measured

`scan-sql-manifest.ps1` sorts by `rank, path`, so every slice left at the default rank 40 deploys
**alphabetically**.

- Alphabetical order violates **28** real edges.
- The assigned ranks 43–72 violate **0**.

The clearest failures are exact inversions: alphabetically `..._applications` sorts before
`..._decisions` before `..._requests`, while the dependency runs requests → decisions →
applications. The same inversion appears in `rebase_*`, `purchase_rebase_*` and
`warehouse_exception_*`.

## The cross-module dependency

`construction_r7_actual_cost_allocations.sql` opens with

```sql
IF COL_LENGTH('dbo.JournalEntries','FunctionalCurrencyId') IS NULL
   OR OBJECT_ID('dbo.ConstructionWbsNodes','U') IS NULL
 THROW 51700,'Apply journal valuation snapshots and construction dimensions first.',1;
```

`FunctionalCurrencyId` is added by `journal_functional_currency_snapshot.sql`, which is an
**Accounting** slice. Construction therefore has a hard apply-time dependency on Accounting. The
journal slice is registered under Accounting ownership and is the one row in this dataset that is
not Construction's to register.

## Enforcement already lives in the scripts

All **25** of the 30 slices that have a prerequisite open with a guard of the form
`IF <OBJECT_ID / COL_LENGTH checks> THROW 5xxxx,'Apply ... first.',1;` inside `BEGIN TRANSACTION`
with `SET XACT_ABORT ON`. **None fails open.** So a missing prerequisite blocks loudly and rolls
back; it never half-applies in silence. A rank is an optimisation that makes a correct deploy
ordering happen on the first attempt — it is not the safety net.

Two honest limits on that guarantee:

1. The guards assert **schema presence**, not that the prerequisite slice ran to completion. A
   hand-created table satisfies them. This is the right layer regardless: deploy order is a
   database fact, not a registry or commit-order fact.
2. Auditing these guards with a line-oriented regex gives the wrong answer. They span four to six
   lines (`IF ... OR ... OR ... \n THROW`), so matching only the line after the `IF` reports 13 of
   25 as failing open. The text must be flattened before matching.

## Ranks with no inbound edge

Five slices have no prerequisite inside this set. Four of them are still rank-constrained because
other slices depend on *them* — notably `construction_r7_company_policies` (64), which five later
slices require. Exactly one rank is genuinely free:

- `construction_r7_native_rowversion` (43) — no edge in either direction. It is kept first
  deliberately, because it `THROW`s 51022 if a differently-named rowversion already exists on any of
  its five legacy tables; going first guarantees nothing has added one yet. The scanner comment that
  claims "native tokens precede state consolidation" is **false** and is corrected in the scanner
  unit; `construction_r7_consolidate_commercial_state` depends on
  `construction_c1_commercial_foundation` and adds its own `varbinary(16)` token, not a rowversion.

## Dependencies on already-registered slices

`construction_r7_consolidate_commercial_state` (44) and `construction_r7_structure` (46) both
require `construction_c1_commercial_foundation.sql`, which is already registered as SQL-10
("Authored - NOT applied"). SQL-10 and SQL-11 are referenced as existing rows and must not be
duplicated by this dataset.

## Outstanding

This file is untracked and is **not** listed in `MANIFEST-SHA256.txt`. That manifest is written by
`_generator/verify_deliverables.py` from its own expected-file list, and the verifier checks only
for missing paths, so an unlisted extra file breaks nothing today. Adding these two files to that
list is a one-line generator change plus a regeneration, and has not been authorized.
