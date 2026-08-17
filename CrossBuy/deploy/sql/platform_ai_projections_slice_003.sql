-- =============================================================================================
-- AI Foundation — Increment 3: retention / expiry on AI projections.
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact.
-- Adds TWO columns to AiProjections. Drops nothing, deletes nothing, rewrites no existing value.
--
-- WHY
--
--   Increment 2 gave AI data a revocation boundary — an EVENT that makes data unreadable. It did not
--   give it a CLOCK. Without one, every projection is immortal by default, and the moment RAG adds a
--   second copy that copy is immortal too. Retention is the thing that stops "we can delete it if
--   someone asks" from being the only control.
--
--   ExpiresAtUtc is stored rather than computed at read time on purpose: the retention policy for a
--   shape may change, and a row must keep the lifetime it was written under. Recomputing would silently
--   extend data that was persisted under a shorter promise.
--
-- EXISTING ROWS — deterministic, never "keep forever"
--
--   Verified before writing: AiProjections held 0 rows in the development database, so the backfill is
--   a no-op there. It is still written correctly for an environment that has rows. The backfill assigns
--   the SHORTEST policy in force (400 days from OccurredAt, the CalendarScheduling figure) rather than
--   the longest, because an existing row's original lifetime is unknown and the conservative reading is
--   the shorter one. RetentionClass is left NULL for those rows, which the reader treats as
--   NOT ELIGIBLE — so a legacy row fails closed on retrieval rather than being served under a lifetime
--   nobody approved.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- The retention class this row was written under. NULL = unknown = not eligible for retrieval.
-- Nullable deliberately: a NOT NULL default would hand every legacy row a policy it never had.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('AiProjections','RetentionClass') IS NULL
    ALTER TABLE AiProjections ADD RetentionClass NVARCHAR(40) NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- When this row stops being retrievable. NULL is NOT "never": the reader refuses a row whose
-- retention metadata is missing, so NULL fails closed.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('AiProjections','ExpiresAtUtc') IS NULL
    ALTER TABLE AiProjections ADD ExpiresAtUtc DATETIME2 NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- Deterministic backfill for any pre-existing row. Idempotent: it only touches rows that still
-- have no expiry, so a second run affects nothing and cannot extend a lifetime already assigned.
-- ---------------------------------------------------------------------------------------------
UPDATE AiProjections
   SET ExpiresAtUtc = DATEADD(day, 400, OccurredAt)
 WHERE ExpiresAtUtc IS NULL;
GO

-- The retrieval read path, now including the expiry bound. Filtered to LIVE rows only: revoked and
-- expired rows are never retrieved, so keeping them out keeps the index proportional to the readable
-- corpus rather than to everything ever held.
--
-- ExpiresAtUtc cannot appear in the filter predicate (it is compared against a runtime value, which
-- SQL Server does not permit in a filtered index), so it is an INCLUDE column: the index still covers
-- the comparison without a key lookup.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiProjections_Live' AND object_id=OBJECT_ID('AiProjections'))
    CREATE INDEX IX_AiProjections_Live
        ON AiProjections (CompanyID, ProjectionType, OccurredAt)
        INCLUDE (EntityType, EntityId, ProjectionVersion, Visibility, ExpiresAtUtc, RetentionClass)
        WHERE RevokedAt IS NULL;
GO

-- Retention classes are a frozen vocabulary. A row carrying an unrecognised class would be refused by
-- the reader anyway; the constraint says so at the boundary rather than relying on the writer.
-- NULL is permitted precisely so legacy rows can be distinguished from classified ones.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_AiProjections_RetentionClass')
    ALTER TABLE AiProjections ADD CONSTRAINT CK_AiProjections_RetentionClass
        CHECK (RetentionClass IS NULL
               OR RetentionClass IN ('ShortLived','BusinessRecordBound','RevocationBound'));
GO
