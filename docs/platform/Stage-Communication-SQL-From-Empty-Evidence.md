# Stage-Communication — SQL-From-Empty Evidence

**Script under test:** `CrossBuy/deploy/sql/communication_platform_slice_001.sql`
**Executed:** 2026-08-06
**Server:** Microsoft SQL Server 2025 (RTM) 17.0.1000.7, local default instance
**Scratch database:** `CommScratch_20260806` — created empty, dropped at the end
**Result:** ✅ **PASS on all seven required conditions**

---

## 1. What was proven

| # | Required | Result |
| --- | --- | --- |
| 1 | 14 tables created | ✅ 14 |
| 2 | All keys, indexes and constraints present | ✅ 14 PK · 14 FK · 22 CHECK · 13 DEFAULT · 43 indexes (9 filtered) |
| 3 | Second application succeeds | ✅ exit 0, 0 objects created |
| 4 | No duplicate objects | ✅ object inventory byte-identical between runs |
| 5 | No dependency on Reporting or Stage 2A schema | ✅ applied to a database containing nothing else |
| 6 | No scratch database remains | ✅ dropped and verified absent |
| 7 | CrossBuyDB2 untouched | ✅ identical before and after |

---

## 2. Method

The script was applied to a **genuinely empty** database, not to a copy of a working one. That distinction is the
whole point of the exercise: a script applied to a populated database can silently depend on objects that happen
to be there.

```bash
# 1. create empty, confirm empty
CREATE DATABASE [CommScratch_20260806];
SELECT COUNT(*) FROM sys.tables;          -- user_tables=0

# 2. apply, with -I (QUOTED_IDENTIFIER ON — required for the filtered indexes)
sqlcmd -S . -E -I -d CommScratch_20260806 -i communication_platform_slice_001.sql

# 3. inventory
# 4. apply a SECOND time
# 5. inventory again and diff
# 6. drop, verify absent
# 7. re-measure CrossBuyDB2 against its pre-run baseline
```

**Note on `sqlcmd` invocation:** `-C` and `-I` are mutually incompatible in this client build (it misparses the
combination and reports *"-E and -U/-P are mutually exclusive"*), and `-i` requires a **backslash** path. The
working form is `-S . -E -I -d <db> -i <windows\path>`. Recorded because the obvious invocation fails in a way
that does not name the real cause.

---

## 3. Condition 1 — the fourteen tables

```
CommAuditEntries              CommNotificationDeliveries
CommCommentAttachments        CommNotificationPreferences
CommCommentRevisions          CommNotifications
CommComments                  CommParticipants
CommMentionRecipients         CommReactions
CommMentions                  CommReadReceipts
                              CommThreadPermissions
                              CommThreads
```

`TABLES = 14`, matching the declared slice exactly. Verified independently by
`CommunicationSchemaParityTests`, which asserts the EF model and this script agree — that suite is what caught a
**real defect during this gate**: its entity selector keyed off the `"Comm"` **name prefix** and began matching the
Construction module's `CommercialRevisions` / `CommercialRevisionLines`. Fixed to key off the CLR namespace
(`CrossBuy.Models.Context.Communication`), which is the actual statement of ownership. See the Integration Gate
Report §6.

---

## 4. Condition 2 — keys, indexes and constraints

| Object class | Count |
| --- | --- |
| Primary keys | 14 (one per table) |
| Foreign keys | 14 |
| CHECK constraints | 22 |
| DEFAULT constraints | 13 |
| Indexes (non-clustered + unique) | 43 |
| — of which **filtered** | **9** |

**The 9 filtered indexes are the meaningful number.** SQL Server refuses to create a filtered index when
`QUOTED_IDENTIFIER` is OFF. Their presence proves `-I` took effect and that a deployment run without it would
fail loudly rather than silently produce an unindexed schema — the footgun CLAUDE.md records for
`platform_business_events_slice_002.sql`.

**22 CHECK constraints** mirror the frozen C# vocabulary in `Models/Communication/CommVocabulary.cs`. That
mirroring is deliberate: an unconstrained string column forks into disagreeing vocabularies, which is what ADR-002
exists to prevent.

---

## 5. Conditions 3 and 4 — idempotency

| Measure | Run 1 | Run 2 |
| --- | --- | --- |
| Exit code | 0 | 0 |
| `CREATED` lines emitted | **14** | **0** |
| Trailing output | `COMPLETE` | `COMPLETE` |

Run 2 emitted `… EXISTS` for every object and created nothing.

Object inventory, both runs:

```
TABLES=14  PK=14  UQ=0  FK=14  CHECK=22  DEFAULT=13  INDEXES=43  FILTERED=9
```

`diff run1 run2` → **identical**. No duplicate object was created, and a partially-applied earlier run would
complete cleanly on a re-run because every statement is individually guarded.

---

## 6. Condition 5 — no external schema dependency

Two independent checks.

**6.1 Static.** Every `dbo.<object>` reference in the script was extracted and filtered for anything not
`dbo.Comm*`:

```
(no results — the script references no object outside its own fourteen tables)
```

**6.2 Empirical, and stronger.** The script applied successfully to a database containing **no** `BusinessEvents`,
no `Notifications`, no `PlatformRoleAssignments`, no `BootstrapAccessPolicies`, no `Report*` tables and no
application tables of any kind. A dependency on Reporting or Stage 2A schema would have failed here with SQL-208.

All 14 foreign keys resolve **within** the slice:

```
CommCommentAttachments  -> CommComments,  CommThreads
CommCommentRevisions    -> CommComments
CommComments            -> CommComments (self, replies), CommThreads
CommMentionRecipients   -> CommComments,  CommMentions
CommMentions            -> CommComments,  CommThreads
CommNotificationDeliveries -> CommNotifications
CommParticipants        -> CommThreads
CommReactions           -> CommComments
CommReadReceipts        -> CommThreads
CommThreadPermissions   -> CommThreads
```

**No FK points at `Employees` or `Companies`** — by design. An employee id here references a person; a hard FK
would let a historical audit row block a personnel-record cleanup. Same stance the kernel takes for
`BusinessEvents.ActorEmployeeId`.

---

## 7. Conditions 6 and 7 — cleanup and non-interference

```
DROP DATABASE [CommScratch_20260806];
SELECT name FROM sys.databases WHERE name LIKE 'CommScratch%';   -- (no rows)
```

**CrossBuyDB2**, measured before any SQL work and again after the scratch database was dropped:

| Measure | Before | After |
| --- | --- | --- |
| `sys.objects` count | 1157 | 1157 |
| Tables matching `Comm%` | 2 | 2 |

`diff before after` → **identical**. The two `Comm%` tables are `CommMessages` and `CommAttachments`, belonging to
the pre-existing Comm **email** module — this slice's fourteen tables were never applied to CrossBuyDB2, which is
correct: production registration is not active.

---

## 8. What this evidence does NOT prove

Stated so the evidence is not over-read:

* **It does not prove the schema is deployed anywhere.** It is deployed to nothing. CrossBuyDB2 does not carry
  these tables, and `AddCommunicationPlatform` is not called in `Program.cs`.
* **It does not prove behaviour under concurrency.** Filtered-unique-index collisions under parallel insert are
  covered by the SQL-Server-gated test suite, not by this exercise.
* **It does not prove the EF model matches** — that is `CommunicationSchemaParityTests`, run separately and
  reported in the gate report.
* **It was run against SQL Server 2025.** Older target versions are untested here; nothing in the script uses
  post-2016 syntax, but that is an inspection claim, not a measurement.

---

## 9. Reproduction

```bash
SQLCMD="/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd"
DB="CommScratch_$(date +%Y%m%d%H%M)"
SQL=$(cygpath -w CrossBuy/deploy/sql/communication_platform_slice_001.sql)

"$SQLCMD" -S . -E -I -Q "CREATE DATABASE [$DB];"
"$SQLCMD" -S . -E -I -d "$DB" -i "$SQL"        # run 1
"$SQLCMD" -S . -E -I -d "$DB" -i "$SQL"        # run 2 — must create nothing
"$SQLCMD" -S . -E -I -d "$DB" -Q "SELECT COUNT(*) FROM sys.tables;"   # 14
"$SQLCMD" -S . -E -I -Q "ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB];"
```

The script prints a per-object `CREATED` / `EXISTS` line and a terminal `COMPLETE`, so a run is auditable from its
output alone.
