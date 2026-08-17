-- =============================================================================================
-- AI Foundation — Increment 2: read-side classification + revocation boundary.
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact.
-- Adds COLUMNS to AiProjections. Drops nothing, deletes nothing, rewrites no existing value.
--
-- WHY THESE COLUMNS
--
--   Visibility — Increment 1 enforced the event's classification on the WRITE side (a grant admits
--   only Internal facts). The READ side must enforce it too: a user who may View an Internal entity
--   must not receive a Confidential projection about it. That decision needs the classification on
--   the row, because re-joining BusinessEvents for every candidate row would make the authorization
--   query depend on a second table's filters — and BusinessEvent is itself company-filtered, which is
--   exactly the kind of implicit coupling that hides a leak.
--
--   RevokedAt / RevokedBy / RevocationReason — a TOMBSTONE, not a delete. See slice notes below.
--
-- EXISTING ROWS
--
--   Verified before writing this script: AiProjections held 0 rows in the development database, so
--   the backfill below is a no-op there. It is still written correctly for any environment that does
--   have rows: Increment 1's grant table admitted ONLY BusinessEventVisibility.Internal, so every row
--   that can exist was Internal by construction. The default is therefore a statement of fact about
--   what those rows already are, not a guess that widens anything.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- Classification, copied from the source BusinessEvent at projection time.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('AiProjections','Visibility') IS NULL
    ALTER TABLE AiProjections ADD Visibility NVARCHAR(40) NOT NULL
        CONSTRAINT DF_AiProjections_Visibility DEFAULT ('Internal');
GO

-- ---------------------------------------------------------------------------------------------
-- REVOCATION — tombstone, not row deletion.
--
-- THE DECISION AND WHY. Three requirements had to hold at once: revoked content must no longer be
-- retrievable; audit must still be able to say the data once existed and was revoked; and revocation
-- must be provably tenant-scoped. A hard DELETE satisfies the first and third but destroys the
-- second — after it, nobody can answer "did this company's data ever reach the AI subsystem, and when
-- was it removed?", which is precisely the question a privacy or audit review asks.
--
-- So the row survives as EVIDENCE and the PAYLOAD is destroyed: RevokeAsync clears PayloadJson to an
-- empty JSON object and stamps the three columns below. The reader refuses any row with RevokedAt set,
-- so retrievability ends immediately and does not depend on the payload having been cleared.
--
-- This matches the platform's existing habit — "reverse, never delete" for financial history, soft
-- delete (DeletedAt) across the feature modules — rather than inventing a fourth convention.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('AiProjections','RevokedAt') IS NULL
    ALTER TABLE AiProjections ADD RevokedAt DATETIME2 NULL;
GO

IF COL_LENGTH('AiProjections','RevokedBy') IS NULL
    ALTER TABLE AiProjections ADD RevokedBy NVARCHAR(100) NULL;
GO

IF COL_LENGTH('AiProjections','RevocationReason') IS NULL
    ALTER TABLE AiProjections ADD RevocationReason NVARCHAR(200) NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- The retrieval read path: live (non-revoked) rows for one company, one shape, in time order.
--
-- FILTERED on RevokedAt IS NULL deliberately. Revoked rows are retained for audit but are never
-- retrieved, so keeping them out of the index keeps it proportional to the LIVE corpus rather than to
-- everything the subsystem has ever held.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiProjections_Retrieval' AND object_id=OBJECT_ID('AiProjections'))
    CREATE INDEX IX_AiProjections_Retrieval
        ON AiProjections (CompanyID, ProjectionType, OccurredAt)
        INCLUDE (EntityType, EntityId, ProjectionVersion, Visibility)
        WHERE RevokedAt IS NULL;
GO

-- Classification must stay inside the platform's frozen vocabulary. A row carrying an unknown level
-- would be evaluated by the reader's mapping and denied, but a constraint says so at the boundary
-- rather than relying on the reader being the only writer.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_AiProjections_Visibility')
    ALTER TABLE AiProjections ADD CONSTRAINT CK_AiProjections_Visibility
        CHECK (Visibility IN ('Internal','Confidential','Restricted','System'));
GO
