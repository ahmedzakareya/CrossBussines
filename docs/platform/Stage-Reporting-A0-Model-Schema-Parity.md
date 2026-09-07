# A0 — 2 · Model → schema parity

**The EF model is authoritative.** Ownership is decided by the CLR namespace
`CrossBuy.Models.Context.Reporting`, not by a name prefix and not by a hand-kept list — so a future
`ReportingSnapshot` entity is owned by this slice whether or not anyone remembers to register it somewhere.

---

## 1. The matrix

All twelve entities are mapped, deployed and verified. `Id` is `IDENTITY(1,1)` and the primary key on every
table. Every table carries `CompanyID` plus `CreatedBy / CreatedAt / updatedBy / UpdatedAt`.

| EF entity | Table | PK | FK | Unique | Notable indexes | Delete |
|---|---|---|---|---|---|---|
| `ReportTemplate` | ReportTemplates | INT | — | — | `IX_…_Company_Code_Scope` (covering; hit on every generation), Owner + Team (filtered) | soft |
| `ReportTemplateVersion` | ReportTemplateVersions | INT | → ReportTemplates | `(TemplateId, VersionNo)` | — | immutable |
| `ReportCategory` | ReportCategories | INT | — | `(CompanyID, [Key])` filtered `DeletedAt IS NULL` | Parent | soft |
| `ReportTag` | ReportTags | INT | — | `(CompanyID, Name)` filtered | — | soft |
| `ReportTagLink` | ReportTagLinks | INT | → ReportTags | `(CompanyID, TagId, ReportCode, TemplateId)` | Report | — |
| `ReportFavorite` | ReportFavorites | INT | — | `(CompanyID, EmployeeId, ReportCode, TemplateId)` | — | hard |
| `ReportShare` | ReportShares | INT | — | `(CompanyID, ReportCode, TemplateId, PrincipalType, PrincipalKey)` filtered | `IX_…_Lookup` (covering) | soft |
| `ReportRun` | ReportRuns | **BIGINT** | — | — | Company+Started DESC (covering), +Employee, +Report, ParametersHash (filtered) | **none — append-only** |
| `ReportArchiveEntry` | ReportArchiveEntries | **BIGINT** | — | — | Company+Report, Retention (filtered), Hash, Run (filtered) | soft |
| `ReportSchedule` | ReportSchedules | INT | — | — | Due (filtered to live + active), Owner | soft |
| `ReportScheduleRecipient` | ReportScheduleRecipients | INT | → ReportSchedules | — | Schedule | — |
| `ReportDeliveryAttempt` | ReportDeliveryAttempts | **BIGINT** | — | — | Company+Schedule+Attempted DESC, Run (filtered) | **none — append-only** |

Defaults: every vocabulary column has a `DEFAULT` matching the C# default (`Scope` 3 = Personal,
`AccessLevel` 2 = Run, `Kind`/`Status` 0, `Frequency` 2 = Daily). `NVARCHAR` throughout for user-facing text
(Arabic), with `NameEn` twins. Hashes are `CHAR(64)`. Timestamps are `DATETIME2`. `CorrelationId` is
`UNIQUEIDENTIFIER`.

Two BIGINT choices are deliberate and load-bearing: `ReportRuns` takes one row per generation *including
previews* and is the highest-volume table in the platform; `ReportDeliveryAttempts` is one row per recipient
per run.

## 2. How parity is proved, not asserted

Four mechanical tests, none of which reads the table above:

- **`Every_reporting_entity_in_the_ef_model_has_a_table_in_the_authored_slice`** — model → deployed schema,
  *and the reverse*: a table the model does not map is dead schema and fails too.
- **`Every_persisted_reporting_property_has_a_column`** — every mapped property resolves to a real column.
  This is the check that would have caught `TaskScopeQuerySqlTests`' twenty `Invalid column name` errors,
  which came from a hand-written table carrying only the columns its assertions touched.
- **`The_slice_creates_a_table_for_every_reporting_entity_in_the_ef_model`** — the same coverage read from
  the file text, **ungated**, so it still fails on a machine with no SQL Server. That matters: the gate is
  exactly how A0 hid.
- **`The_reporting_services_run_against_the_authored_schema`** — real EF queries over all twelve sets plus a
  write round-trip, against the database the `.sql` file built.

**This is the test that stops A0 recurring.** Adding a thirteenth Reporting entity without updating the slice
now fails two tests, one of which needs no database.

## 3. Isolation classification

| Class | Where | Rule |
|---|---|---|
| **Platform-global** | ReportTemplates, ReportCategories — *rows with `CompanyID = 0`* | `0` is significant, not a null-substitute: a platform row belongs to no tenant and is visible to every company. `AuthorizeTemplateAsync` treats Platform scope as readable everywhere and editable nowhere. |
| **Company-owned** | all twelve tables | `CompanyID NOT NULL` on every table; every service predicate begins with it, and every composite index leads with it |
| **User-owned** | ReportFavorites (`EmployeeId`); ReportTemplates at Personal scope (`OwnerEmpId`) | resolved from `BusinessContext`, never from the request |
| **Team-owned** | ReportTemplates at Team scope (`TeamId`) | membership via `IReportTeamResolver` |

**No cross-company FK is possible.** The three foreign keys are all inside one company's own rows
(version → template, tag link → tag, recipient → schedule), and each parent is itself company-scoped.

**There is deliberately no FK to Employees, Companies or Branches.** Reporting references those by id
without a constraint, matching the rest of the platform. A reporting history row must not be able to block a
tenant purge, and an append-only audit table with a hard FK to a mutable master table is how that happens.

**No caller-supplied `CompanyId` becomes trusted.** The schema cannot enforce that and does not claim to —
it is enforced above, by `BusinessContext` resolution and `CompanyWriteGuardInterceptor`. The schema's part
is to make the column non-nullable and index it first, which it does. This increment adds no path that
weakens it.
