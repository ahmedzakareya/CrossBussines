# Stage-Communication — Legacy `DocComments` Migration Plan

**Platform:** CrossBusiness Communication & Collaboration Platform
**Owner tab:** THIRD TAB (Communication & Collaboration)
**Status:** **PLAN ONLY — NOT EXECUTED.** No migration script exists, and none is authorised by this document.
**Related:** ADR-031 (collaboration surface), ADR-035 (history/soft-delete/audit), CPS-001, Entity Registry Contract

---

## 1. Statement of intent, and one measured fact that changes the risk

`DocComments` is the pre-platform comment table (Comm Hub P5). The Communication Platform supersedes it. This
document designs the migration; it does **not** perform it.

**Measured on CrossBuyDB2, 2026-08-06:**

```
DocComments rows            = 0
distinct EntityType values  = 0
```

**The table is empty in the reference database.** That is a material fact and it cuts both ways:

* it means the migration is currently a **no-op** on this database, and the risk of executing it here is near zero;
* it also means **the mapping rules in §4 are unvalidated against real data**. Every "unknown `EntityType`" branch
  below is reasoning about data that does not exist here. A production database may hold rows with types this plan
  has never seen.

**Consequence for the owner:** do not read "0 rows" as "safe to skip the dry run". The dry run (§9) exists
precisely to discover what a *populated* database contains, and it must be run against every environment
separately.

---

## 2. Source inventory

### 2.1 The legacy table, as it actually exists

Live schema, `dbo.DocComments`:

| Column | Type | Null | Migration note |
| --- | --- | --- | --- |
| `Id` | int | NOT NULL | identity; becomes `LegacyDocCommentId` on the target |
| `CompanyID` | int | NOT NULL | maps directly; the tenant key |
| `EntityType` | nvarchar | NOT NULL | **free text** — the core mapping problem (§4) |
| `EntityId` | int | NOT NULL | maps directly |
| `Body` | nvarchar | NOT NULL | maps to `CommComments.Body`, `BodyFormat = PlainText` |
| `DeletedAt` | datetime2 | NULL | soft delete — maps directly |
| `CreatedBy` | int | NULL | **the only author signal** (§5) |
| `CreatedAt` | datetime2 | NULL | preserved verbatim (§6) |
| `updatedBy` | int | NULL | last editor; no per-edit history exists |
| `UpdatedAt` | datetime2 | NULL | last edit time; no per-edit history exists |

### 2.2 What the legacy table does NOT have

Naming these is more important than naming what it has, because each absence is a field the target requires and
the migration must therefore **invent or default** — and every invented value is a lie unless it is recorded as one.

| Absent | Target requires | Resolution |
| --- | --- | --- |
| **Thread** | `CommComments.ThreadId` | synthesise one thread per `(CompanyID, EntityType, EntityId)` — §4.3 |
| **Visibility** | `CommComments.Visibility` | default `Internal`; **never** `Public` — §7 |
| **Attachments** | — | **none exist.** No `DocCommentAttachments` table. §8 |
| **Mentions** | — | none exist; `Body` is plain text with no mention markup |
| **Reactions / read state / participants** | — | none exist; nothing to migrate |
| **Edit history** | `CommCommentRevisions` | only *last* edit is known; §6.2 |
| **Author (explicit)** | `CommComments.AuthorEmployeeId` | only `CreatedBy` exists; §5 |
| **Parent / replies** | `CommComments.ParentCommentId` | flat; all migrated rows are roots |

### 2.3 Sibling tables explicitly NOT in scope

`Announcements` and `AnnouncementReads` are a different feature (broadcast, not record-anchored). They are **not**
migrated by this plan and have no target in this platform.

---

## 3. Target mapping — table level

| Legacy | Target | Cardinality |
| --- | --- | --- |
| `DocComments` (row) | `CommComments` (row) | 1 : 1 |
| `DocComments` (distinct entity) | `CommThreads` (row) | N : 1 |
| — | `CommParticipants` | derived, §5.3 |
| — | `CommAuditEntries` | 1 : 1, migration-marked, §10 |

---

## 4. `EntityType` → `CommEntityRef` — the core problem

### 4.1 Why this is the hard part

`DocComment.EntityType` is **unconstrained free text**. `CommEntityRef.EntityCode` must be a **registered
`IEntityRegistry` code**, and the surface refuses anything else — fail-closed by design
(`CommSecurityBoundaryTests.An_unregistered_entity_code_is_refused_by_every_capability`).

So the migration cannot simply copy the column. Every distinct legacy value must be **classified** before a single
row moves.

### 4.2 The classification, and the rule for each outcome

| Outcome | Rule | Action |
| --- | --- | --- |
| **A — exact match** to a registered code (`SalesInvoice`) | ordinal compare | migrate |
| **B — case/spacing variant** (`salesinvoice`, `Sales Invoice`) | case-insensitive + whitespace-stripped match | migrate, normalising to the canonical code; **record the normalisation** |
| **C — known alias** (`Invoice` → `SalesInvoice`) | an explicit, reviewed alias map | migrate **only if the alias map is signed off by the module owner** |
| **D — registered code, capability OFF** | code exists, `SupportsComments` false / not enabled | **do not migrate.** Onboard the entity first (Registry Contract §8), then re-run |
| **E — unknown** | no match, no alias | **do not migrate. Do not guess.** Report and stop |

**Outcome C is the one that will be got wrong.** An alias map is where a migration quietly re-points a comment
from one record type to another. It must be produced by the *module* owner, not inferred by whoever runs the
migration, and it must be reviewed as data — not code.

**Outcome E must never be resolved by inventing a code.** A comment whose entity type is unknown is a comment
whose record cannot be permission-checked; migrating it would place unreachable content in a platform whose entire
guarantee is that every artifact is gated by its record.

### 4.3 Thread synthesis

One `CommThread` per distinct `(CompanyID, EntityCode, EntityId)`:

```
Kind        = CommThreadKind.Discussion
ThreadKey   = ""                        -- the entity's default thread
CreatedAt   = MIN(DocComments.CreatedAt) for that entity   -- not migration time (§6.1)
CreatedBy   = CreatedBy of that earliest comment
```

`ThreadKey = ""` matters: it is the *default* thread, so a legacy comment and a comment written after go-live land
in the **same** conversation rather than two parallel ones.

---

## 5. Author mapping

### 5.1 The only signal is `CreatedBy`

`DocComments` has no author column; `CreatedBy` is an employee id and is **nullable**.

| Case | Rule |
| --- | --- |
| `CreatedBy` is a live employee **in the same `CompanyID`** | map to `AuthorEmployeeId` |
| `CreatedBy` names an employee in **another** company | **do not migrate the row.** Report it — it indicates pre-existing data corruption, and migrating it would create a cross-tenant artifact |
| `CreatedBy` names an employee who no longer exists | migrate, preserving the id. Comm tables carry **no FK to Employees** by design, exactly so a personnel cleanup cannot block historical audit |
| `CreatedBy` is NULL | **do not invent an author.** §5.2 |

### 5.2 NULL authors

Two defensible options; the plan does not choose for the owner, but it does rank them:

* **(a) Skip and report.** Safest, and consistent with "never invent". Recommended when the count is small.
* **(b) Migrate with a designated system author id** recorded in the migration audit. Acceptable only if the
  deployment can produce a real, inactive "system" employee — **never** an arbitrary live person's id, which would
  attribute someone else's words to them.

**Never:** default to the migration operator's own employee id. It is the easiest thing to write and it silently
forges authorship.

### 5.3 Participants

Derive one `CommParticipant` per distinct author per thread, `Role = CommParticipantRole.Author`, `CreatedAt` =
their earliest comment on that thread. **Do not** derive followers or watchers — nobody chose to follow anything,
and manufacturing a follow would generate notifications for conversations people never opted into.

---

## 6. Timestamp preservation

### 6.1 The rule

`CommComments.CreatedAt` = `DocComments.CreatedAt`, **verbatim**. Never migration time.

This is not cosmetic. The timeline is ordered by `OccurredAt`; stamping migration time would collapse years of
history into one instant and destroy the ordering the timeline exists to present. Thread `CreatedAt` likewise
takes the **earliest** child comment's timestamp, not the migration's.

### 6.2 The edit-history problem, stated honestly

Legacy carries `UpdatedAt` / `updatedBy` — a *last* edit — but no revision history.

**The plan does NOT fabricate a revision chain.** Synthesising a `CommCommentRevisions` row from `UpdatedAt` would
assert "this is what the comment said before", which is unknown. Instead:

* migrate `UpdatedAt` / `updatedBy` onto the comment;
* write **no** revision rows;
* record in the migration audit that pre-migration edit history is **unavailable**, so a later reader knows the
  absence is a property of the source and not a migration defect.

### 6.3 NULL timestamps

`CreatedAt` is nullable in the source. A row with no `CreatedAt` cannot be ordered in a timeline.
**Rule:** skip and report. Do not substitute `UpdatedAt` (wrong) or migration time (§6.1).

---

## 7. Privacy mapping

Legacy has **no** visibility column. Every migrated comment therefore gets a default, and the default is a
security decision.

**Rule: `CommVisibility.Internal`. Never `Public`.**

Reasoning, in order:

1. `Public` is the most open tier this platform has. Assigning it to content whose intended audience is unknown
   converts an unknown into the widest possible answer.
2. `Internal` matches the *de facto* audience of the legacy feature: `DocComments` was only ever reachable from
   authenticated internal screens.
3. `Confidential` / `Restricted` would be *narrower* than the legacy behaviour and would hide content from people
   who could previously see it — a different kind of wrong, and a support burden.

**Note the interaction with the unbuilt privacy ceiling** (Registry Contract §6.1): if a per-entity `MaxVisibility`
is introduced later, migrated rows must be re-checked against it. Record the migration's chosen tier explicitly so
that re-check is possible.

---

## 8. Attachment mapping

**There is nothing to map.** No `DocCommentAttachments` table exists; the legacy feature stored text only. The
`CommCommentAttachments` table receives **zero** rows from this migration.

Stated explicitly because "attachments" appears on the required-contents list, and the honest answer is "the source
has none" rather than a designed mapping for data that does not exist.

*(`CommAttachments` in the database belongs to the separate Comm **email** module and is unrelated.)*

---

## 9. Duplicate detection

Two distinct risks:

### 9.1 Re-running the migration

`CommComments` gains a nullable `LegacyDocCommentId INT NULL` with a **filtered unique index**:

```sql
CREATE UNIQUE INDEX UX_CommComments_LegacyDocCommentId
    ON dbo.CommComments (CompanyID, LegacyDocCommentId)
    WHERE LegacyDocCommentId IS NOT NULL;
```

The migration inserts only where no row already carries that `LegacyDocCommentId`. **The database enforces
idempotency**, not the script's control flow — the same discipline `communication_platform_slice_001.sql` uses.
A second run inserts zero rows; a partially-failed run resumes cleanly.

### 9.2 Content already double-entered in the source

If the legacy feature ever wrote the same body twice for one entity (a double-submit), both rows migrate. That is
**correct**: the migration's job is fidelity, not editorial cleanup. Near-duplicates are reported in the dry run so
a human can decide; the migration itself never silently drops content.

---

## 10. Audit of the migration itself

Every migrated comment writes a `CommAuditEntries` row with a distinguishing action (`CommentMigrated`), the
`LegacyDocCommentId`, and a **single correlation id for the entire run**.

That correlation id is what makes §11 possible and what lets a reader six months later tell a migrated comment
from an authored one. `CommAuditEntries.DedupKey` carries `docmigrate:{runId}:{legacyId}`, so the audit write is
idempotent on the same terms as the content write.

---

## 11. Rollback

**Full reversal, and it is only cheap because of §9.1 and §10.**

```sql
-- Scope: exactly the rows this run created.
DELETE FROM dbo.CommParticipants WHERE MigrationRunId = @runId;
DELETE FROM dbo.CommComments     WHERE MigrationRunId = @runId;
DELETE FROM dbo.CommThreads      WHERE MigrationRunId = @runId
                                   AND NOT EXISTS (SELECT 1 FROM dbo.CommComments c
                                                   WHERE c.ThreadId = CommThreads.Id);
-- Audit rows are NOT deleted: the fact that a migration ran and was rolled back is itself history.
```

Three properties this depends on:

* **`MigrationRunId` on every inserted row.** Without it, rollback becomes "delete comments created between two
  timestamps", which would take genuine post-go-live comments with it.
* **A thread is deleted only if it is now empty** — a thread that has since received a real comment must survive.
* **`DELETE`, not soft-delete.** These rows should never have existed. Soft-deleting them would leave them in the
  audit trail as *deleted comments*, which is a different and false statement.

**The legacy `DocComments` table is NEVER modified — not even a "migrated" flag.** Leaving the source pristine is
what makes rollback safe and re-run possible. Retiring the legacy table is a separate, later decision.

---

## 12. Verification

Run after migration, before declaring success. Every check has a required result.

| # | Check | Required |
| --- | --- | --- |
| 1 | Row parity: migrated count = eligible source count | exact match |
| 2 | Every migrated `Body` is byte-identical to source | 0 differences |
| 3 | Every `CreatedAt` equals its source | 0 differences |
| 4 | No comment on an unregistered entity code | 0 rows |
| 5 | No comment whose company ≠ its thread's company | 0 rows |
| 6 | No comment with `Visibility = Public` | 0 rows |
| 7 | `LegacyDocCommentId` unique per company | enforced by index |
| 8 | Re-run inserts nothing | 0 rows on second run |
| 9 | Every migrated comment has an audit row with the run's correlation id | exact match |
| 10 | Skipped rows reported = source total − migrated total | reconciles to zero |
| 11 | A denied caller still cannot read a migrated comment | boundary suite passes against migrated data |

**Check 11 is the one that matters most and is easiest to omit.** Migrated content must obey the same access rules
as authored content; a migration that bypassed the surface could produce rows on entities the surface would refuse.

---

## 13. Dry run — mandatory, and it is the actual first deliverable

**Read-only. Writes nothing. Run per environment.**

Produces:

1. Total rows, and rows per `CompanyID`.
2. **Every distinct `EntityType` with its count and its §4.2 classification (A–E).**
3. Count of NULL `CreatedBy`, NULL `CreatedAt`, cross-company `CreatedBy`.
4. Rows that would be **skipped**, with the reason.
5. Threads that would be created.
6. Estimated audit rows.

**Nothing is executed until the dry run's outcome-E list is empty or every entry has an owner decision.** Given
§1's measurement, the dry run against CrossBuyDB2 will report zero of everything — which is a valid result and is
*not* evidence that other environments will.

---

## 14. Production ownership

| Responsibility | Owner | Not the owner |
| --- | --- | --- |
| Approving the §4.2 **alias map** (outcome C) | the **module owner** of each entity type | Communication tab |
| Deciding NULL-author handling (§5.2) | **data owner** / operations | Communication tab |
| Onboarding entities so outcome-D rows become eligible | **kernel owner** + module owner | Communication tab |
| Running the dry run and publishing its output | **deployment/operations** | — |
| Executing the migration | **deployment/operations**, after sign-off | — |
| Rollback decision | **deployment/operations** | — |
| The migration script itself | **Communication tab**, when commissioned | — |
| Retiring `DocComments` | a **separate** later decision | this plan |

**This tab owns the design and would own the script. It does not own the decision to run it, and it has not run
it.**

---

## 15. Preconditions before any execution

All must hold. None holds today.

1. `communication_platform_slice_001.sql` applied to the target — ✅ provable (SQL-From-Empty evidence).
2. **Production registration active** — ❌ **not active**; `AddCommunicationPlatform` is not called in `Program.cs`.
3. The dry run has been executed and its outcome-E list resolved — ❌ not run.
4. The alias map is signed off — ❌ does not exist.
5. `LegacyDocCommentId` + `MigrationRunId` columns added by an additive idempotent slice — ❌ not written.
6. NULL-author policy chosen — ❌ open.
7. A verified backup of the target database — ❌ operations.

**Item 2 alone is decisive: the platform is not registered in production, so there is nothing for migrated data to
be served by.** Migration necessarily follows activation, and activation is outside this gate.
