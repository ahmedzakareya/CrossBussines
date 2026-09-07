# A0 — 1 · Existing DDL discovery

**Verdict: (A) A COMPLETE REPORTING SCHEMA ALREADY EXISTED**, in the canonical root, correctly registered.
No new schema was authored. Nothing was duplicated.

---

## 1. The correction this document exists to make

My previous increment's delivery report stated:

> *"`deploy/sql/` has 58 files and not one creates a Reporting table. The Reporting platform has never had a
> deployment script."*

**That was wrong.** I searched the legacy repo-root tree `deploy/sql/` and never looked at the canonical tree
`CrossBuy/deploy/sql/`. The repository has two SQL trees; the Reporting slice has been in the canonical one
all along, registered in the manifest and in the slice registry.

Had A0 begun by writing DDL — which the runtime error invited — the result would have been a second,
divergent Reporting schema. The brief's "DO NOT immediately write a new schema" rule is what prevented that.

## 2. What was searched

| Location / pattern | Result |
|---|---|
| `deploy/sql/` (legacy root, 57 files) | no Reporting DDL |
| `CrossBuy/deploy/sql/` (canonical, 61 files) | **`reporting_platform.sql` — found** |
| Every `*.sql` in the repo containing `dbo.ReportShares` (excluding bin/obj) | **exactly one file** |
| Same, for `ReportTemplates` · `ReportRuns` · `ReportArchiveEntries` · `ReportFavorites` · `ReportSchedules` | same single file |
| `docs/platform/`, `engineering/`, preservation archives | prior evidence documents only; no competing DDL |
| `CrossBuy/deploy/sql/manifest.json` | **already registers the slice** |
| `governance/registry/sql-slices.json` | **already registers it as SliceId `SQL-06`** |

## 3. What was found

`CrossBuy/deploy/sql/reporting_platform.sql` — 456 lines, twelve tables, headed to ADR-037:

```
ReportTemplates  ReportTemplateVersions  ReportCategories  ReportTags  ReportTagLinks
ReportFavorites  ReportShares  ReportRuns  ReportArchiveEntries
ReportSchedules  ReportScheduleRecipients  ReportDeliveryAttempts
```

Twelve tables against twelve `DbSet<Reporting.*>` declarations — an exact name match. Idempotent by
construction (`IF OBJECT_ID(...) IS NULL`), additive, no DROP anywhere.

Registry entry, verbatim:

```json
{ "sliceId": "SQL-06", "slice": "reporting_platform.sql", "tree": "CANONICAL",
  "owner": "Second Tab", "appliedStatus": "Authored - NOT applied",
  "gatesCorePath": "No",
  "note": "12 tables; from-empty and second-apply idempotency proved. Applies in R2." }
```

## 4. So what actually caused the 500?

Not a missing script. **`appliedStatus: "Authored - NOT applied"`.**

The slice was authored, hashed into the manifest and registered — and never applied to the development
database. `Invalid object name 'ReportShares'` was a **deployment state**, not a repository gap.

## 5. What A0 therefore had to close

Four real gaps, none of them "write a schema":

| Gap | Closed by |
|---|---|
| No **re-runnable** proof that the slice applies. The prior from-empty proof was a one-off evidence document, and a document cannot fail when somebody changes an entity. | `ReportingSchemaDeploymentTests` (SQL Server) |
| No **mechanical** EF↔SQL parity — nothing failed when a Reporting entity was added without matching SQL. | parity tests, ownership derived from the CLR namespace |
| **No CHECK constraints.** The architecture had explicitly noted vocabulary columns were C#-only. | slice §11, seven constraints |
| No guard that `EnsureCreated()` is not mistaken for deployment evidence — the precise reason A0 stayed invisible behind 304 green tests. | `ReportingDeploymentGovernanceTests`, deliberately **ungated** |

## 6. Reconciliation

Nothing to reconcile: one slice, one tree, one manifest entry, one registry entry, no stale copies, no
divergent versions, no name collision (`duplicateNames` contains no Reporting entry).

`Exactly_one_reporting_slice_exists_anywhere_in_the_repository` now keeps it that way — if a second copy
appears in the legacy tree, that test names both paths and fails.
