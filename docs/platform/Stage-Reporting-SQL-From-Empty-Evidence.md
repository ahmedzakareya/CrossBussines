# Stage — Reporting: SQL-From-Empty Evidence

**Script under proof:** `CrossBuy/deploy/sql/reporting_platform.sql`
**Date:** 2026-08-06
**Instance:** `localhost` — Microsoft SQL Server 2025 (RTM) 17.0.1000.7, Enterprise Developer Edition
**Client:** `sqlcmd` 15.0.1300.359 (`Client SDK/ODBC/170/Tools/Binn`)
**Disposable database:** `ReportingProof_20260806_a1` — created for this proof, dropped at the end.

> **`CrossBuyDB2` WAS NOT TOUCHED.** The script contains no `USE` statement, so its target is set entirely by
> `sqlcmd -d`. Every command below names the disposable database explicitly, and the runner carried a guard that
> refuses to proceed if the target name matches `CrossBuyDB*`, `master`, `model`, `msdb` or `tempdb`.
> Verified after the run: **0 Reporting tables exist in CrossBuyDB2.**

---

## 1. Required proofs — all 11 pass

| # | Required proof | Result |
|---|---|---|
| 1 | Database starts with **zero** Reporting tables | ✅ `0` |
| 2 | Script applies successfully | ✅ exit code `0`, no error lines |
| 3 | All **12** tables exist | ✅ 12/12, each reporting `ok` |
| 4 | Required constraints exist | ✅ PK 12, FK 3, DEFAULT 39 |
| 5 | Filtered indexes exist | ✅ 10, each with its predicate verified |
| 6 | Script applies a **second** time | ✅ exit code `0`, no error lines |
| 7 | Second application is idempotent | ✅ object inventory **identical** across all 10 measures |
| 8 | Presence output is correct | ✅ same 12-row `ok` table on both runs |
| 9 | No duplicate objects | ✅ `none` / `duplicate index names: 0` |
| 10 | Disposable database dropped | ✅ exit code `0` |
| 11 | Zero Reporting scratch databases remain | ✅ `0` |

---

## 2. Step 1 — the database starts empty

```
> SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'Report%';
0
```

The proof is meaningless without this: applied to a database that already had the tables, an idempotent script
would report success while proving nothing about creation.

---

## 3. Step 2 — first application

```
sqlcmd -S localhost -E -d ReportingProof_20260806_a1 -I -b -i CrossBuy/deploy/sql/reporting_platform.sql
exit code: 0
```

`-I` sets QUOTED_IDENTIFIER ON. Without it SQL Server refuses to create a filtered index — the same footgun
CLAUDE.md records for `platform_business_events_slice_002.sql`. The script's own header instructs the operator to
use it.

Error-line sweep over the output (`Msg`, `error`, `incorrect syntax`): **none**.

Presence output:

```
TableName                Status
------------------------ -------
ReportArchiveEntries     ok
ReportCategories         ok
ReportDeliveryAttempts   ok
ReportFavorites          ok
ReportRuns               ok
ReportScheduleRecipients ok
ReportSchedules          ok
ReportShares             ok
ReportTagLinks           ok
ReportTags               ok
ReportTemplates          ok
ReportTemplateVersions   ok
```

Twelve tables, matching RPS-001's declared count exactly.

---

## 4. Step 3 — object inventory after run 1

| Object class | Count |
|---|---|
| Tables | 12 |
| Primary keys | 12 |
| Unique constraints | 0 |
| **Check constraints** | **0** — see §7 |
| Default constraints | 39 |
| Foreign keys | 3 |
| Non-PK indexes | 25 |
| **Filtered indexes** | **10** |
| Unique indexes (non-PK) | 6 |
| Columns | 178 |

### 4.1 Script declarations vs objects created — exact match

| Declared in the script text | Created in the database |
|---|---|
| `CONSTRAINT PK_` × 12 | PK 12 ✅ |
| `FOREIGN KEY` / `REFERENCES` × 3 | FK 3 ✅ |
| `CREATE [UNIQUE] INDEX` × 25 | non-PK indexes 25 ✅ |
| `CREATE UNIQUE` × 6 | unique non-PK indexes 6 ✅ |
| `CHECK (` × 0 | CHECK 0 ✅ |

Nothing declared failed to create, and nothing was created that the script does not declare.

### 4.2 The 10 filtered indexes, with their predicates as stored by SQL Server

| Index | Table | Filter |
|---|---|---|
| `IX_ReportArchiveEntries_Retention` | ReportArchiveEntries | `([DeletedAt] IS NULL AND [RetainUntil] IS NOT NULL)` |
| `IX_ReportArchiveEntries_Run` | ReportArchiveEntries | `([RunId] IS NOT NULL)` |
| `UX_ReportCategories_Company_Key` | ReportCategories | `([DeletedAt] IS NULL)` |
| `IX_ReportDeliveryAttempts_Run` | ReportDeliveryAttempts | `([RunId] IS NOT NULL)` |
| `IX_ReportRuns_ParametersHash` | ReportRuns | `([ParametersHash] IS NOT NULL)` |
| `IX_ReportSchedules_Due` | ReportSchedules | `([DeletedAt] IS NULL AND [IsActive]=(1) AND [NextRunAt] IS NOT NULL)` |
| `UX_ReportShares_Unique` | ReportShares | `([DeletedAt] IS NULL)` |
| `UX_ReportTags_Company_Name` | ReportTags | `([DeletedAt] IS NULL)` |
| `IX_ReportTemplates_Owner` | ReportTemplates | `([OwnerEmpId] IS NOT NULL)` |
| `IX_ReportTemplates_Team` | ReportTemplates | `([TeamId] IS NOT NULL)` |

The three `UX_… WHERE DeletedAt IS NULL` indexes are load-bearing, not optimisations: they are what makes a
soft-deleted row free its unique slot, so a category or tag can be recreated after deletion.

---

## 5. Steps 4–5 — second application and idempotency

```
sqlcmd … -I -b -i reporting_platform.sql        (second time)
exit code: 0
```

Error-line sweep including `already exists`: **none**.

Inventory compared measure by measure:

| Measure | After run 1 | After run 2 | |
|---|---|---|---|
| TABLES | 12 | 12 | MATCH |
| PK | 12 | 12 | MATCH |
| UNIQUE_CONSTRAINTS | 0 | 0 | MATCH |
| CHECK | 0 | 0 | MATCH |
| DEFAULTS | 39 | 39 | MATCH |
| FOREIGN_KEYS | 3 | 3 | MATCH |
| INDEXES_NONPK | 25 | 25 | MATCH |
| FILTERED_INDEXES | 10 | 10 | MATCH |
| UNIQUE_INDEXES | 6 | 6 | MATCH |
| COLUMNS | 178 | 178 | MATCH |

```
IDEMPOTENT: object inventory identical after the second run   ✅
```

Duplicate detection:

```
duplicate object names : none
duplicate index names  : 0
```

Idempotency is asserted on the resulting **object inventory** rather than on the script's console output, because
output text is a claim while `sys.tables`/`sys.indexes` is the state. A script that dropped and recreated each
object would produce identical console output and a different — and briefly destructive — reality.

---

## 6. Steps 6–7 — teardown

```
ALTER DATABASE [ReportingProof_20260806_a1] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE  [ReportingProof_20260806_a1];
exit code: 0
```

Databases remaining on the instance:

```
AlprimeCapDb   AlprimeCapDb2   ClinicOSDb   CrossBuyDB2   dbAlprime
master   model   MOI_RightToInformation   msdb   tempdb
```

```
scratch databases remaining (ReportingProof% / %scratch% / %tmp%) : 0     ✅
Reporting tables in CrossBuyDB2                                   : 0     ✅
```

The instance is exactly as it was before the proof.

---

## 7. Finding: the script declares **no CHECK constraints**

Not a failure of this proof — the script creates everything it declares — but a **gap worth recording**, because
several columns carry a closed vocabulary that is currently enforced only in C#:

| Table | Column | Vocabulary |
|---|---|---|
| `ReportTemplates` | `Scope` | 0 Platform / 1 Company / 2 Team / 3 Personal |
| `ReportRuns` | `Status` | Success / Denied / Failed / … |
| `ReportRuns` | `Kind` | Full / Preview |
| `ReportSchedules` | `Frequency` | … |
| `ReportDeliveryAttempts` | `Status` | … |

For comparison, the Communication Platform's slice mirrors all 14 of its vocabularies in `CHECK` constraints and
asserts the mirroring in a test.

**Recommendation:** add `CHECK` constraints in a `reporting_platform_slice_002.sql`, guarded the way that platform
guards its own (create only if absent **and** if no existing row would violate), so a partially-populated database
reports rather than blocks. Deliberately **not** done in this increment: it is a schema change, and this increment
is an integration gate.

---

## 8. Reproducing this proof

```bash
SQLCMD="/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd"
DB="ReportingProof_$(date +%Y%m%d_%H%M%S)"

# guard — never a real database
case "$DB" in *CrossBuyDB*|master|model|msdb|tempdb) echo "REFUSED"; exit 1;; esac

"$SQLCMD" -S localhost -E -Q "CREATE DATABASE [$DB];"
"$SQLCMD" -S localhost -E -d "$DB" -h -1 -W -Q \
  "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'Report%';"   # expect 0

"$SQLCMD" -S localhost -E -d "$DB" -I -b -i CrossBuy/deploy/sql/reporting_platform.sql   # run 1
"$SQLCMD" -S localhost -E -d "$DB" -I -b -i CrossBuy/deploy/sql/reporting_platform.sql   # run 2

"$SQLCMD" -S localhost -E -Q \
  "ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB];"
```
