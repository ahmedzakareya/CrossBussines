# A0 — 3 · SQL design

**Canonical root:** `CrossBuy/deploy/sql/` · **Slice:** `reporting_platform.sql` · **SliceId:** `SQL-06`
**No second SQL root was created. The legacy tree was neither moved nor deleted.**

---

## 1. What changed in this increment

The slice already existed and was already correct (see Discovery). A0 added **one section**: seven CHECK
constraints on closed vocabulary columns, which the previous Reporting architecture had explicitly recorded
as C#-only.

Nothing else in the file was altered. No table was recreated, no column changed type, no DROP was added.

## 2. Why the constraints sit OUTSIDE the CREATE guards

Every table block is wrapped in `IF OBJECT_ID(N'dbo.X', N'U') IS NULL`. A constraint written inside that
block would only ever reach a database that did not already have the table — that is, never, on any database
that had applied an earlier copy of the file.

Each constraint therefore guards on **its own** existence:

```sql
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportShares_AccessLevel')
BEGIN
    ALTER TABLE dbo.ReportShares ADD CONSTRAINT CK_ReportShares_AccessLevel
        CHECK (AccessLevel BETWEEN 0 AND 4);
    PRINT 'ADDED CK_ReportShares_AccessLevel';
END
ELSE PRINT 'CK_ReportShares_AccessLevel EXISTS';
```

This is the pattern `platform_schema_history.sql` already uses for its two constraints — the same idiom, not
a new one.

## 3. What is constrained

Closed C# enums, stored as INT, values read from the **enum declarations** rather than from the comments
above the columns:

| Column | Enum | Range | Members |
|---|---|---|---|
| `ReportTemplates.Scope` | `ReportTemplateScope` | 0..3 | Platform Company Team Personal |
| `ReportShares.PrincipalType` | `ReportPrincipalType` | 0..3 | Employee Role Team Company |
| `ReportShares.AccessLevel` | `ReportAccessLevel` | 0..4 | None View Run Edit Manage |
| `ReportRuns.Kind` | `ReportRunKind` | 0..2 | Full Preview Scheduled |
| `ReportRuns.Status` | `ReportRunStatus` | 0..3 | Succeeded Failed Denied Cancelled |
| `ReportSchedules.Frequency` | `ReportScheduleFrequency` | 0..4 | Interval Hourly Daily Weekly Monthly |
| `ReportDeliveryAttempts.Status` | `ReportDeliveryStatus` | 0..3 | Pending Sent Failed Skipped |

`AccessLevel` earns particular care: an out-of-range value could not open a back door (a share only ever
raises what the module permission already allowed), but it would make `HighestShareLevelAsync`'s `MAX()`
return a level no code can interpret — an access decision nobody can explain.

## 4. What is deliberately NOT constrained

The rule the brief sets, and the one that matters: **a CHECK must not encode extensible vocabulary**, or the
database has to be redeployed before code can register a new member.

| Column | Why not |
|---|---|
| `ChannelKey` (recipients, delivery attempts) | **Plugin vocabulary.** Channels are discovered from registered `IReportDeliveryChannel` implementations. A closed CHECK would turn adding a channel into a schema change. This is exactly the case the brief warns about. |
| `Format` (runs, schedules) | Stored as the format NAME so history reads unaided. `ReportOutputFormat` is closed today, but the renderer registry is explicitly designed for a future engine to claim a format; pinning the column couples that to a deployment. A wrong value mis-labels one history row and cannot corrupt a decision. |
| `LastRunStatus` | Free text written for an operator to read, not a decision input. |
| `ColorToken` | A Metronic contextual token; presentation, and the palette may grow. |
| `AtHour` / `AtMinute` / `DayOfWeek` / `DayOfMonth` | Genuine closed ranges and worth constraining — but **range** validation rather than vocabulary, and `ReportScheduleService` already refuses out-of-range values. Considered and deferred to keep this increment's constraint surface to what the brief names. Recorded so the decision is visible rather than forgotten. |

## 5. No `WITH NOCHECK`

Each constraint validates existing rows as it is created. If some database already holds an out-of-range
value, the `ALTER` **fails loudly** — which is correct: it means the data disagrees with the model, and
`WITH NOCHECK` would make that disagreement permanent and invisible.

## 6. Drift policy

The brief's rule is followed: **if a table exists with an incompatible shape, STOP.** The slice contains no
`ALTER TABLE … ALTER COLUMN`, no DROP, and no data conversion. A shape mismatch surfaces as a failed CHECK
creation or a failed EF query, and is reported — not silently repaired.
