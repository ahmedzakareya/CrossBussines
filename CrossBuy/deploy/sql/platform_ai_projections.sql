-- =============================================================================================
-- AI Foundation — Increment 1: the AI projection boundary (AiProjections).
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact.
-- Creates ONE new table. Modifies NO existing table. Deletes nothing. Back-fills nothing.
--
-- WHAT THIS TABLE IS
--
--   The only table the future AI/RAG layer reads. AI never queries a business table: it reads minimized,
--   explicitly-approved projections written by AiProjectionConsumer after that consumer has passed a
--   default-deny grant gate, a tenancy check and a permission check.
--
-- WHY EXISTING BusinessEvents ROWS ARE SAFE
--
--   Dispatch rows are created per registered consumer at RecordAsync time. Adding 'AiProjection' to
--   BusinessEventConsumers.Registered therefore affects only events recorded AFTER the deployment —
--   historical events acquire no AI dispatch row and are never projected. That is deliberate: a backfill
--   would push years of facts into the AI subsystem without anyone having reviewed a grant for them.
--   If a backfill is ever wanted it must be an explicit, separately-approved operation.
--
-- ORDERING: SQL before code, as with every platform slice.
-- =============================================================================================

IF OBJECT_ID('AiProjections','U') IS NULL
CREATE TABLE AiProjections (
    Id                BIGINT IDENTITY(1,1) PRIMARY KEY,

    -- Provenance: every projection names the business fact it came from. Without these two columns the
    -- AI corpus would be a copy with no answer to "why does this data exist?".
    BusinessEventId   BIGINT NOT NULL,
    EventUid          UNIQUEIDENTIFIER NOT NULL,

    -- Which subsystem produced it. Lets a revocation sweep target AI alone.
    Consumer          NVARCHAR(60) NOT NULL,

    -- Tenancy, copied from the EVENT's context. Never defaulted; the consumer refuses a non-positive value.
    CompanyID         INT NOT NULL,
    BranchID          INT NULL,

    EntityType        NVARCHAR(60) NOT NULL,
    EntityId          INT NOT NULL,
    EventType         NVARCHAR(80) NOT NULL,
    ActorEmployeeId   INT NULL,                       -- NULL = system/worker origin

    -- The projection SHAPE and its version, so a shape can be re-versioned and rebuilt without renaming
    -- events or losing the ability to tell old rows from new ones.
    ProjectionType    NVARCHAR(60) NOT NULL,
    ProjectionVersion INT NOT NULL DEFAULT 1,

    -- Minimized JSON, built field-by-field by a registered builder. Never a serialized EF entity, never
    -- free text, never secrets. See AiProjectionBuilders for the exact field list and the exclusions.
    PayloadJson       NVARCHAR(MAX) NOT NULL,

    OccurredAt        DATETIME2 NOT NULL,             -- when the business fact happened
    ProjectedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()   -- when we projected it (changes on rebuild)
);
GO

-- ---------------------------------------------------------------------------------------------
-- IDEMPOTENCY — the guarantee, not a convenience.
--
-- A dispatch row can be redelivered after a stale claim, a retry, or two workers racing. The application
-- pre-checks, but the pre-check cannot close the race between two workers that both read "not present".
-- This index is what actually makes a retry produce one row.
--
-- The key is (BusinessEventId, ProjectionType) rather than BusinessEventId alone: one event may
-- legitimately produce several SHAPES later, but never two rows of the same shape.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_AiProjections_Event_Shape' AND object_id=OBJECT_ID('AiProjections'))
    CREATE UNIQUE INDEX UX_AiProjections_Event_Shape ON AiProjections (BusinessEventId, ProjectionType);
GO

-- The AI read path, and the path a revocation/rebuild sweep needs: everything for one entity in one
-- company, in time order. Company first because every AI query is tenant-scoped before anything else.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiProjections_Company_Entity' AND object_id=OBJECT_ID('AiProjections'))
    CREATE INDEX IX_AiProjections_Company_Entity ON AiProjections (CompanyID, EntityType, EntityId, OccurredAt);
GO

-- Referential integrity back to the durable log. NO CASCADE: deleting a BusinessEvent must not silently
-- delete AI data as a side effect — revocation is an explicit, audited operation, not a cascade.
-- (BusinessEvents is append-only, so this FK should never actually block anything.)
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name='FK_AiProjections_BusinessEvents')
    ALTER TABLE AiProjections ADD CONSTRAINT FK_AiProjections_BusinessEvents
        FOREIGN KEY (BusinessEventId) REFERENCES BusinessEvents (EventId);
GO

-- A projection must belong to a real company. The application fails closed before it gets here; this is
-- the database saying the same thing, so a future writer cannot weaken it by accident.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_AiProjections_Company')
    ALTER TABLE AiProjections ADD CONSTRAINT CK_AiProjections_Company CHECK (CompanyID > 0);
GO
