# A0 — 8 · Reporting deployment runbook

> **PREPARED, NOT EXECUTED.** Nothing in this document has been run against `CrossBuyDB2`, prelive,
> production, or any real environment. It requires explicit owner approval.

---

## 1. Prerequisites

| # | Requirement |
|---|---|
| 1 | Owner approval naming the target database. |
| 2 | A backup, or a restore point, taken immediately before. The slice is additive and takes no destructive action — but "additive" is a property of this file, not a property of the session that runs it. |
| 3 | `CrossBuy/deploy/sql/platform_schema_history.sql` applied (creates `dbo.PlatformSchemaHistory`). Only needed to *record* the apply; the Reporting slice itself has no dependency on it. |
| 4 | **`sqlcmd -I`** (QUOTED_IDENTIFIER ON). The slice creates filtered indexes, which will not build without it. |
| 5 | Permission to CREATE TABLE / CREATE INDEX / ALTER TABLE in `dbo`. |
| 6 | No dependency on the platform kernel slices: Reporting writes only its own twelve tables. |

## 2. The artifact

| | |
|---|---|
| **File** | `CrossBuy/deploy/sql/reporting_platform.sql` |
| **SliceId** | `SQL-06` |
| **Bytes** | `32228` |
| **SHA-256** | `53db921a777c4612ee6a1a2943dd4ac8090df1df106e1e996af277f63ed75199` |
| **Rank** | 40 |
| **Idempotency** | `guarded` (`object-null`, `if-not-exists`, `sys-catalog`; `unguardedBatches: []`) |
| **Creates** | 12 tables, their indexes, 3 FKs, 7 CHECK constraints |
| **Drops / alters** | none |

## 3. Validate before applying

```bash
# 1. The manifest agrees with the file on disk (hash + byte count).
powershell -NoProfile -ExecutionPolicy Bypass -File CrossBuy/deploy/scan-sql-manifest.ps1

# 2. The governance and parity tests. These need no database.
dotnet test CrossBuy.Tests --filter "FullyQualifiedName~ReportingDeploymentGovernance"

# 3. The full deployment proof, against a disposable database on a SQL instance.
#    CROSSBUY_TEST_SQL must name an INSTANCE, never a database.
set CROSSBUY_TEST_SQL=Server=.;Trusted_Connection=True;TrustServerCertificate=True
dotnet test CrossBuy.Tests --filter "FullyQualifiedName~ReportingSchemaDeployment"
```

Do not proceed unless all three are green. Step 3 is the one that matters: it applies this exact file to an
empty SQL Server database.

## 4. Apply

```bash
sqlcmd -S <server> -d <database> -E -I -b -i CrossBuy/deploy/sql/reporting_platform.sql
```

`-I` is required (§1.4). `-b` makes sqlcmd exit non-zero on error, so a failure cannot be mistaken for
success by a wrapper script.

The slice prints one row per table at the end; every row must read `ok`.

## 5. Record the apply

```sql
INSERT INTO dbo.PlatformSchemaHistory (ScriptName, ScriptHash, AppliedBy, ApplicationVersion, Success)
VALUES ('CrossBuy/deploy/sql/reporting_platform.sql',
        '53db921a777c4612ee6a1a2943dd4ac8090df1df106e1e996af277f63ed75199',
        SUSER_SNAME(), '<build version>', 1);
```

The hash must be **lower-case** — `CK_PlatformSchemaHistory_Hash` rejects anything else, and a mis-cased
digest would make the drift report call this slice "changed" forever.

## 6. Post-apply checks

```sql
-- 6.1 All twelve tables present.
SELECT name FROM sys.tables WHERE name LIKE 'Report%' ORDER BY name;   -- expect 12 rows

-- 6.2 ReportShares specifically — the table whose absence caused the incident.
SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReportShares');   -- expect 14

-- 6.3 The two authorization indexes.
SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
 WHERE t.name = 'ReportShares' AND i.name IS NOT NULL;
-- expect UX_ReportShares_Unique, IX_ReportShares_Lookup

-- 6.4 The seven CHECK constraints.
SELECT cc.name FROM sys.check_constraints cc
  JOIN sys.tables t ON t.object_id = cc.parent_object_id
 WHERE t.name LIKE 'Report%' ORDER BY cc.name;   -- expect 7 rows

-- 6.5 Recorded, once.
SELECT ScriptName, ScriptHash, ApplyCount FROM dbo.vw_PlatformSchemaCurrent
 WHERE ScriptName LIKE '%reporting_platform.sql';
```

## 7. Smoke test the running product

1. `GET /Reports` → 200, catalog renders.
2. `GET /Reports/Viewer/Platform.BusinessEventLog` → the parameter form renders. **Before this slice it
   returned 500 `Invalid object name 'ReportShares'`; that is the specific regression this deployment fixes.**
3. Run the report → HTML preview.
4. Export CSV and Excel → files download.
5. Reports Center → Recent runs shows the run just made.

A caller needs `reporting.businessevents.view` (mapped to Admin / SuperAdmin / Auditor). Without it a **404
is correct**, not a failure of this deployment.

## 8. Rollback / forward-fix

**Policy: forward-fix. There is no rollback script, deliberately.**

The slice only creates objects. Rolling back means dropping twelve tables — which destroys report history,
saved layouts and archive metadata, and would be far more damaging than the additive change it undoes. If
the apply fails partway, the guards make **re-running it** the correct recovery: everything already created
is skipped and the rest is created.

If a table is found with an incompatible shape, **stop and report drift**. Do not ALTER destructively. That
means the database was built by something other than this slice, and the divergence must be understood
before anything is changed.

## 9. Explicitly out of scope

- `CrossBuyDB2` — **not applied.**
- Prelive / production — **not applied.**
- Nothing in this increment executed SQL against any environment other than disposable
  `CrossBuyProbe_*` databases, each dropped and confirmed removed.
