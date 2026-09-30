# Class-A SQL Execution Runbook

Seven scripts. The convergence branch already ships the code that reads the tables they create, so
until these run, Portal identity, Portal documents, Portal contact requests, the external-actor
communication columns and project milestones all reference objects the database does not have.

**Nothing in this document has been executed.** It was written from the scripts themselves, statically,
against convergence checkpoint `ddcf7a1` (SQL manifest Stage A) and its successors. No database was
contacted to produce it.

This runbook is meant to be usable on its own. You should not need the conversation that produced it.

---

## 0. The gate — execution is not authorized yet

The target server is **not** determined by this repository. The only tracked connection string is
`CrossBuy/appsettings.Production.json`, and its server is a deployment-time placeholder:

```
Server=__SQL_SERVER_INSTANCE__;Database=CrossBuyDB2;User Id=crossbuy_app;...
```

The database *name* is `CrossBuyDB2`; the *instance* is substituted at deploy time and is not
recoverable from the repository. Do not infer it, and do not connect to a local instance to guess.

**An operator must confirm all seven of these in writing before any script runs:**

| # | Item | Confirmed value |
|---|---|---|
| 1 | Server / instance | |
| 2 | Database | (expected `CrossBuyDB2` — confirm) |
| 3 | Environment (dev / test / staging / production) | |
| 4 | Authentication mode (integrated / SQL login) | |
| 5 | Application version / convergence checkpoint deployed | |
| 6 | Backup destination (path or URL) | |
| 7 | Maintenance window (start / end, timezone) | |

Until row 1 is filled in, the status of this package is **TARGET_DATABASE_UNCONFIRMED**. That is the
expected state of a readiness document, not a defect.

---

## 1. What will be executed

Verify each file before running it. The hash rule is **LF-normalised** — the repository checks out CRLF
while the manifest records the git-blob bytes, so hashing the working-tree file directly will not match.

| Order | Path | Bytes (LF) | SHA-256 | Rank | Idempotency |
|---|---|---|---|---|---|
| 1 | `CrossBuy/deploy/sql/comm_portal_001_identity.sql` | 5514 | `b6a8fbcbbfb41bddbd287e0000f6e899bf6b767a45fbcaae646f7e90999b6f76` | 40 | guarded |
| 2 | `CrossBuy/deploy/sql/comm_documents_001.sql` | 11257 | `e87f185dbcb06f3f63329e44f1e9132b4b2635fbf99b7c6281fa80ec57e4de1c` | 40 | guarded |
| 3 | `deploy/sql/portal_user_photo.sql` | 2556 | `1c7aa75d41ab178794d80b7d2fa7da2604082ae532bcc33f79a934b7b3bf030d` | 40 | guarded |
| 4 | `CrossBuy/deploy/sql/comm_portal_002_contact_request.sql` | 5962 | `d9484ebc9b97162774fcd4512f1686eaa2235716db10873bd366e5a3a4e25151` | 40 | guarded |
| 5 | `CrossBuy/deploy/sql/client_document_publication_doc1.sql` | 6269 | `71160ef8d9d86474fcb7e34e4dd056c57f4e6852bc80df00581f4d3e4fd3ef28` | 40 | guarded |
| 6 | `CrossBuy/deploy/sql/communication_external_actor_3bb1.sql` | 13059 | `7b0dda896a3bc71f5e155dfc3b4da91d1e68170ac9a746215de8ea4be0c57437` | 40 | guarded |
| 7 | `CrossBuy/deploy/sql/projects_milestones.sql` | 12138 | `c6684fc3807ea5a92ec50c46e7f62cb21acc565b0881870d10c090c85f3c9e0d` | 30 | guarded |

`SqlManifestCompletenessTests` asserts these seven hashes against the files on every test run, so a
tampered or re-saved script fails the build before it reaches a database.

**Two facts measured across all seven, which shape everything below:**

- **No data mutation.** Zero `INSERT` / `UPDATE` / `DELETE` / `MERGE` statements in any of the seven.
  Nothing existing is rewritten, backfilled or seeded.
- **No transaction wrapper.** Zero `BEGIN TRANSACTION` and zero `BEGIN TRY` in any of the seven. Each
  is a sequence of `GO`-separated batches. **A failure mid-script leaves the earlier batches applied.**
  The guards make re-running safe; they do **not** make the script atomic. Do not confuse the two.

---

## 2. Order, and why it is this order

Derived from the SQL — from `REFERENCES` clauses and from which script creates each object — not from
file names and not from manifest rank alone.

```
comm_portal_001_identity          creates PortalUsers
        │
        ├── portal_user_photo                 ALTERs PortalUsers        [HARD]
        ├── comm_portal_002_contact_request   PortalUserId column       [SOFT]
        └── communication_external_actor_3bb1 PortalUserId columns      [SOFT]

comm_documents_001                creates PlatformDocumentTypes / Documents / Versions
        │
        └── client_document_publication_doc1  FK → PlatformDocuments    [HARD]

communication_platform_slice_001  (ALREADY REGISTERED, rank 40 — must already be applied)
        │
        └── communication_external_actor_3bb1 ALTERs 5 Comm* tables     [HARD]

Projects schema (already deployed)
        │
        └── projects_milestones               FK → Projects             [HARD]
```

**HARD** = the script fails without it. **SOFT** = it will apply, but the column it adds is meaningless
until the other table exists; `comm_portal_002` and `external_actor` deliberately carry **no** FK to
`PortalUsers` (the scripts say so, and ownership is checked in the service layer instead). Order them
after `comm_portal_001_identity` anyway — a nullable orphan column is not worth the argument.

**Final execution order: 1 → 2 → 3 → 4 → 5 → 6 → 7**, exactly as numbered in §1. Script 7 is independent
of the Portal package and may run before it or after it; it is placed last only to keep the Portal
package contiguous.

---

## 3. Batching

One script per batch. Never all seven in one blind transaction — they are not transactional (§1), so a
single wrapper would buy nothing and would hold locks across the whole set.

```
for each script, in order:
    PRECHECK    (§5)
    EXECUTE ONE SCRIPT
    POSTCHECK   (§6)
    HEALTH      (§8, the subset relevant to that script)
    → continue, or ABORT and stop the whole package
```

**Script 6 gets its own maintenance-aware batch.** See §4.2.

---

## 4. The two risks worth stopping for

### 4.1 `portal_user_photo.sql` — a guard that cannot see a missing table

The script's only guard is:

```sql
IF COL_LENGTH('dbo.PortalUsers', 'PhotoPath') IS NULL
BEGIN
    ALTER TABLE dbo.PortalUsers ADD PhotoPath NVARCHAR(400) NULL;
END
```

`COL_LENGTH` returns `NULL` when the **column** is absent — and also when the **table** is absent. The
guard therefore cannot tell "PortalUsers exists, add the column" from "PortalUsers does not exist". In
the second case it enters the block and the `ALTER TABLE` fails.

This is a real weakness and it is **not** repaired here: the script is byte-unchanged, because changing
a script during a readiness wave means the thing you reviewed is not the thing you ran.

**Mandatory precondition — `PortalUsers` MUST exist before script 3 runs:**

```sql
IF OBJECT_ID('dbo.PortalUsers', 'U') IS NULL
    THROW 50001, 'ABORT: PortalUsers does not exist. Run comm_portal_001_identity.sql first.', 1;
```

If that throws, **abort the package**. Do not "just run script 1 quickly" out of order — restart the
batch sequence from step 1 with its own precheck.

### 4.2 `communication_external_actor_3bb1.sql` — non-additive, maintenance window required

This is the only script in the package that removes anything. It drops three indexes and creates six:

| Dropped (guarded by `IF EXISTS`) | On table |
|---|---|
| `UX_CommParticipants_Live` | `CommParticipants` |
| `IX_CommParticipants_Employee` | `CommParticipants` |
| `UX_CommReadReceipts_Unique` | `CommReadReceipts` |

| Created |
|---|
| `UX_CommParticipants_Live`, `UX_CommParticipants_Live_Portal` |
| `IX_CommParticipants_Employee`, `IX_CommParticipants_PortalUser` |
| `UX_CommReadReceipts_Unique`, `UX_CommReadReceipts_Portal` |

The three dropped indexes are **replaced** by same-named indexes with a different shape, plus three new
portal-side partners. It also `ALTER`s five `Comm*` tables and adds five `CK_*_ExactlyOne*` check
constraints (`ExactlyOneActor`, `ExactlyOneAuthor`, `ExactlyOneEditor`, `ExactlyOneUploader`,
`ExactlyOneReader`) — each enforcing that a row names an employee **or** a portal user, never both.

**Exposure, stated plainly: this is not a zero-downtime script.**

- Between the `DROP INDEX` and the matching `CREATE INDEX`, `CommParticipants` and `CommReadReceipts`
  have no unique constraint on the affected shape. Concurrent writes could insert duplicates that then
  make the `CREATE UNIQUE INDEX` fail — and leave you with neither the old index nor the new one.
- `ALTER TABLE ... ADD CONSTRAINT CHECK` takes a schema-modification lock and validates existing rows.
- 23 `GO` batches, no transaction: a mid-script failure leaves a partial state (§7).

**Requirements:**
1. Application traffic to Communication **stopped** (not merely quiesced) for the duration.
2. Background workers stopped — the outbox dispatcher writes `Comm*` rows.
3. Run inside the agreed maintenance window, never alongside user traffic.
4. **Abort conditions:** any duplicate found by the pre-check below; any `CREATE UNIQUE INDEX` failure;
   any check-constraint validation failure. In each case stop and go to §7 class C.

---

## 5. Precheck matrix

Run before the script named. All are read-only.

| # | Script | Precheck |
|---|---|---|
| 1 | comm_portal_001_identity | `OBJECT_ID('dbo.PortalUsers','U')` **IS NULL** (fresh) or table exists with `UX_PortalUsers_UserId` (re-run) |
| 2 | comm_documents_001 | `OBJECT_ID` for `PlatformDocumentTypes`, `PlatformDocuments`, `PlatformDocumentVersions` — all absent, or all present (never a partial set) |
| 3 | portal_user_photo | **`OBJECT_ID('dbo.PortalUsers','U') IS NOT NULL`** — see §4.1. Then `COL_LENGTH('dbo.PortalUsers','PhotoPath')` records whether this is a fresh add or a no-op |
| 4 | comm_portal_002_contact_request | `OBJECT_ID('dbo.PortalContactRequests','U')`; `PortalUsers` exists (soft) |
| 5 | client_document_publication_doc1 | **`OBJECT_ID('dbo.PlatformDocuments','U') IS NOT NULL`** (hard FK target); `ClientDocumentPublications` absent or already correct |
| 6 | communication_external_actor_3bb1 | All five `Comm*` tables exist (`CommParticipants`, `CommComments`, `CommCommentRevisions`, `CommCommentAttachments`, `CommReadReceipts`); record the current index inventory on `CommParticipants` and `CommReadReceipts`; **duplicate scan** proving the new unique shapes can be created; confirm traffic stopped |
| 7 | projects_milestones | **`OBJECT_ID('dbo.Projects','U') IS NOT NULL`** (hard FK target); `ProjectMilestones` / `ProjectDeliverables` absent or both present |

Generic index/constraint inventory to capture before script 6, so the postcheck has something to
compare against:

```sql
SELECT t.name AS table_name, i.name AS index_name, i.is_unique, i.has_filter
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
WHERE t.name IN ('CommParticipants','CommReadReceipts','CommComments',
                 'CommCommentRevisions','CommCommentAttachments')
  AND i.name IS NOT NULL
ORDER BY t.name, i.name;
```

---

## 6. Postcheck matrix

| # | Script | Postcheck — all must be true |
|---|---|---|
| 1 | comm_portal_001_identity | `PortalUsers` exists; indexes `UX_PortalUsers_UserId`, `IX_PortalUsers_Company_Customer` exist |
| 2 | comm_documents_001 | `PlatformDocumentTypes`, `PlatformDocuments`, `PlatformDocumentVersions` exist; indexes `UX_PlatformDocumentTypes_Scope_Code`, `IX_PlatformDocuments_Entity`, `IX_PlatformDocuments_Expiry`, `UX_PlatformDocumentVersions_Document_No` exist; its 5 FKs and 1 CHECK present |
| 3 | portal_user_photo | `COL_LENGTH('dbo.PortalUsers','PhotoPath')` **IS NOT NULL**; column is `NVARCHAR(400)` and **NULLable** |
| 4 | comm_portal_002_contact_request | `PortalContactRequests` exists; `IX_PortalContactRequests_Company_Status` and `IX_PortalContactRequests_Company_Customer_Quotation` exist; **no FK to Quotations** (by design) |
| 5 | client_document_publication_doc1 | `ClientDocumentPublications` exists; `UX_ClientDocumentPublications_Live` and `IX_ClientDocumentPublications_Customer` exist; FK to `PlatformDocuments` present with `ON DELETE NO ACTION` |
| 6 | communication_external_actor_3bb1 | **All six** indexes exist: `UX_CommParticipants_Live`, `UX_CommParticipants_Live_Portal`, `IX_CommParticipants_Employee`, `IX_CommParticipants_PortalUser`, `UX_CommReadReceipts_Unique`, `UX_CommReadReceipts_Portal`. All five `CK_*_ExactlyOne*` constraints exist **and are trusted** (`is_not_trusted = 0`). No dropped index left un-recreated |
| 7 | projects_milestones | `ProjectMilestones` and `ProjectDeliverables` exist; `IX_ProjectMilestones_ProjectOrder`, `IX_ProjectDeliverables_ProjectOrder`, `IX_ProjectDeliverables_Milestone` exist; FKs to `Projects` (both) and `ProjectMilestones` present, all `ON DELETE NO ACTION`; the 6 CHECK constraints present |

Untrusted constraints are worth calling out specifically — a check constraint added `WITH NOCHECK`
exists but proves nothing:

```sql
SELECT name, is_not_trusted FROM sys.check_constraints WHERE name LIKE 'CK_Comm%';
-- every row must show is_not_trusted = 0
```

And after the whole package, nothing unexpected should have appeared:

```sql
SELECT name FROM sys.tables
WHERE name IN ('PortalUsers','PortalContactRequests','ClientDocumentPublications',
               'PlatformDocumentTypes','PlatformDocuments','PlatformDocumentVersions',
               'ProjectMilestones','ProjectDeliverables')
ORDER BY name;   -- expect exactly these eight, no duplicates, no _old / _bak / _tmp variants
```

---

## 7. Failure and rollback

The guards make these scripts **re-runnable**. They do **not** make them **atomic** — there is no
transaction anywhere in the package (§1), so a failure part-way leaves earlier batches applied.

| Script | Class | What the operator does after a failure |
|---|---|---|
| 1 comm_portal_001_identity | **A** — safely re-runnable | Fix the cause, re-run. Guards skip what exists. |
| 2 comm_documents_001 | **B** — inspect first | 9 batches, 3 tables, 5 FKs. Check which tables/FKs landed, then re-run; guards skip the complete ones. |
| 3 portal_user_photo | **A** — safely re-runnable | Single batch. If it failed, `PortalUsers` was almost certainly absent (§4.1) — fix the order, re-run. |
| 4 comm_portal_002_contact_request | **A** — safely re-runnable | Table plus two indexes, all guarded. |
| 5 client_document_publication_doc1 | **B** — inspect first | Confirm the FK target survived, then re-run. |
| 6 communication_external_actor_3bb1 | **C** — restore/repair decision | See below. |
| 7 projects_milestones | **B** — inspect first | 15 batches across two tables, 6 FKs, 6 CHECKs. Verify which landed, then re-run. |

**Class C, script 6, spelled out because this is the one that can hurt:** if it fails after a
`DROP INDEX` but before the matching `CREATE`, a unique constraint is simply gone. Do **not** re-run
blindly and do **not** leave it. Decide between:

1. **Repair forward** — confirm from `sys.indexes` exactly which of the six exist, resolve any duplicate
   rows that blocked a unique index, and re-run. The `IF EXISTS` guards make the drops no-ops and the
   creates fill the gaps. This is correct *only if* you can show the data is clean.
2. **Restore** — from the backup taken in §9. Choose this if duplicates exist that you cannot resolve
   confidently, or if the failure is not understood.

The rollback boundary for the whole package is the **§9 backup**. There is no scripted down-migration:
none of the seven ships one, and this wave did not write one.

---

## 8. Application verification matrix

Run after the package completes, before returning traffic.

| Area | Check | Depends on |
|---|---|---|
| Portal authentication | `/PortalAccount/Login` returns 200; a portal cookie cannot satisfy an internal `[Authorize]` | 1 |
| Portal denied route | `/ClientPortal` unauthenticated redirects to the PortalAccount login, not the employee one | 1 |
| Portal documents | `/ClientPortal/Documents` lists only this customer's live publications | 2, 5 |
| Portal photo | a portal contact's photo renders; `/uploads/portal` still requires authentication | 3 |
| Portal contact requests | a contact request can be raised and appears for staff | 4 |
| ClientDocumentPublication schema | publication/revocation round-trips; a revoked link yields nothing | 5 |
| Documents workspace | the document spine lists types, documents and versions | 2 |
| Communication | threads, participants and read receipts work for **both** an employee and a portal actor; the `ExactlyOne*` constraints refuse a row naming both | 6 |
| Communication consumers | the outbox drains; `TimelineProjection`, `NotificationProjection`, `AiProjection` and `TaskOrchestration` all mark dispatch rows Done | 6 |
| ProjectMilestone reads | `/Project/Milestones/{id}` renders a project's milestones and deliverables | 7 |
| ProjectMilestone orchestration | a milestone status change produces a `ProjectMilestone.StatusChanged` event and the orchestration rule projects/completes the correlated task | 7 |
| BusinessEvent processing | no dispatch row stuck in Failed; no consumer logging "no implementation" | 6, 7 |

---

## 9. Backup and restore gate

Execution is forbidden until an operator confirms every row. Commands are deliberately **not**
pre-written: the instance, edition, and backup destination are unknown (§0), and a fabricated
`BACKUP DATABASE` line for an unknown environment is worse than none.

| # | Item | Confirmed |
|---|---|---|
| 1 | Full backup completed successfully | |
| 2 | Backup timestamp (with timezone) | |
| 3 | Backup destination (path/URL), reachable from the restore host | |
| 4 | Restore procedure written down and understood | |
| 5 | Operator has rights to perform the restore | |
| 6 | Free space at the destination ≥ database size | |
| 7 | Restore point corresponds to **this exact** target database (§0 rows 1–2) | |
| 8 | Backup taken immediately before the window, or an explicitly accepted age is recorded | |

---

## 10. Abort conditions

Stop before or during execution if any of these hold:

- §0 row 1 (server/instance) is not confirmed.
- Any §9 row is unconfirmed.
- A file's SHA-256 does not match §1.
- `PortalUsers` does not exist when script 3 is reached (§4.1).
- `PlatformDocuments` does not exist when script 5 is reached.
- `Projects` does not exist when script 7 is reached.
- Any of the five `Comm*` tables is missing when script 6 is reached — `communication_platform_slice_001`
  has not been applied.
- The script-6 duplicate scan finds rows that would break a new unique index.
- Communication traffic or background workers are still running at script 6.
- Any postcheck in §6 fails.

---

## 11. What is deliberately **not** in this package

- **Class-B debt (19 scripts).** Recorded in `CrossBuy/deploy/sql/manifest-known-debt.json`. Stage B.
- **Class-D collisions (3).** Two scripts create `AccountingPeriodAudits` with different bytes, and a
  second differing `hr_document_attachments.sql` exists. No winner has been chosen and this package does
  not choose one.
- **Five stale manifest hashes** — `crm_schema_updates_3x.sql`, `manuf_schema_4x.sql`,
  `tasks_ecosystem_checklist_dependencies_templates.sql`, `calendar.sql`,
  `warehouse_sections_item_groups.sql`. Pre-existing drift. **None is Class A and none is a prerequisite
  of any Class-A script**, so they do not block this package.
