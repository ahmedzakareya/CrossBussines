-- =============================================================================================
-- Platform Kernel slice 1 — Enterprise Event Platform (PKS-001 / ADR-001 / ADR-003 / ADR-004)
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact.
--
-- FOUR RULES THIS SCHEMA EXISTS TO ENFORCE
--
--   1. BusinessEvents rows are written INSIDE the business transaction that produced the fact.
--      The event and the fact commit or roll back together (ScopedTx). An event is never written
--      after CommitAsync and never inside a try/catch that swallows the failure — that is what
--      separates BusinessEvents (durable audit) from Notifications (best-effort, after-commit).
--
--   2. Dispatchers process PENDING DISPATCH ROWS, not events. Each consumer owns an independent
--      row per event in BusinessEventDispatch, so a consumer that fails can be retried alone while
--      the consumers that succeeded stay Done.
--
--   3. Dispatchers must NEVER use "EventId > lastSeen", MAX(EventId), or any global high-water
--      cursor. EventId values are assigned at INSERT but become visible at COMMIT: transaction A
--      can take EventId 100 while transaction B takes 101 and commits FIRST. A cursor that advances
--      to 101 would skip event 100 forever once A commits. Claiming is by Status only.
--
--   4. Each consumer has an independent dispatch state. A single DispatchedAt flag on the event
--      cannot express N consumers; BusinessEvents.CompletedAt is a convenience roll-up for
--      reporting and must not be used to decide what to process.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- BusinessEvents — the append-only log of business facts.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('BusinessEvents','U') IS NULL
CREATE TABLE BusinessEvents (
    EventId         BIGINT IDENTITY(1,1) PRIMARY KEY,
    EventUid        UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),   -- stable public identity
    CompanyID       INT NOT NULL,                                -- tenant isolation (every query scopes on it)
    BranchID        INT NULL,
    EntityType      NVARCHAR(60) NOT NULL,                       -- canonical IEntityRegistry code, never free text
    EntityId        INT NOT NULL,
    EventType       NVARCHAR(80) NOT NULL,                       -- '<EntityType>.<Action>'
    ActorEmployeeId INT NULL,                                    -- NULL = system
    Payload         NVARCHAR(MAX) NULL,                          -- JSON, <= 64 KB, no files/binary/secrets
    PayloadVersion  INT NOT NULL DEFAULT 1,                      -- payload SCHEMA version only
    CorrelationId   UNIQUEIDENTIFIER NULL,                       -- groups every event of one operation
    DedupKey        NVARCHAR(120) NULL,                          -- idempotency, unique per company
    Visibility      NVARCHAR(40) NOT NULL,                       -- frozen vocabulary, see constraint below
    CreatedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt     DATETIME2 NULL                               -- all consumers Done (reporting only, see rule 4)
);
GO

-- Timeline read path: every projection query is (company, entity type, entity id) ordered by time.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BusinessEvents_Entity' AND object_id=OBJECT_ID('BusinessEvents'))
    CREATE INDEX IX_BusinessEvents_Entity ON BusinessEvents (CompanyID, EntityType, EntityId, CreatedAt);
GO

-- Idempotent recording: a second RecordAsync with the same key inside a company must not create a row.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_BusinessEvents_DedupKey' AND object_id=OBJECT_ID('BusinessEvents'))
    CREATE UNIQUE INDEX UX_BusinessEvents_DedupKey ON BusinessEvents (CompanyID, DedupKey) WHERE DedupKey IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BusinessEvents_Correlation' AND object_id=OBJECT_ID('BusinessEvents'))
    CREATE INDEX IX_BusinessEvents_Correlation ON BusinessEvents (CorrelationId) WHERE CorrelationId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BusinessEvents_CreatedAt' AND object_id=OBJECT_ID('BusinessEvents'))
    CREATE INDEX IX_BusinessEvents_CreatedAt ON BusinessEvents (CreatedAt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_BusinessEvents_EventUid' AND object_id=OBJECT_ID('BusinessEvents'))
    CREATE UNIQUE INDEX UX_BusinessEvents_EventUid ON BusinessEvents (EventUid);
GO

-- Visibility is a FROZEN vocabulary (ADR-004). It must never become free text: the timeline and, later,
-- the AI context pipeline filter on it, so an unrecognised value would silently widen or hide history.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_BusinessEvents_Visibility')
    ALTER TABLE BusinessEvents ADD CONSTRAINT CK_BusinessEvents_Visibility
        CHECK (Visibility IN ('Internal','Confidential','Restricted','System'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_BusinessEvents_PayloadVersion')
    ALTER TABLE BusinessEvents ADD CONSTRAINT CK_BusinessEvents_PayloadVersion CHECK (PayloadVersion >= 1);
GO

-- JSON validation, only where the engine supports it. ISJSON() ships with SQL Server 2016 (major
-- version 13); on anything older the payload contract is enforced by BusinessEventService alone.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_BusinessEvents_PayloadJson')
   AND CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 13
    EXEC(N'ALTER TABLE BusinessEvents ADD CONSTRAINT CK_BusinessEvents_PayloadJson
               CHECK (Payload IS NULL OR ISJSON(Payload) = 1);');
GO

-- ---------------------------------------------------------------------------------------------
-- BusinessEventDispatch — per-consumer outbox state (rule 2 + rule 4).
--
-- Status lifecycle:  Pending --claim--> Claimed --success--> Done
--                                              \--failure--> Failed --retry--> Claimed
-- 'Claimed' is what makes claiming ATOMIC: one UPDATE ... WITH (ROWLOCK, READPAST, UPDLOCK) flips
-- Pending/Failed to Claimed and OUTPUTs the rows it took, so two workers can never hold the same
-- row. A worker that dies holding a Claimed row releases it after the stale-claim timeout.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('BusinessEventDispatch','U') IS NULL
CREATE TABLE BusinessEventDispatch (
    ID        BIGINT IDENTITY(1,1) PRIMARY KEY,
    EventId   BIGINT NOT NULL,
    Consumer  NVARCHAR(40) NOT NULL,
    Status    NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    Attempts  INT NOT NULL DEFAULT 0,
    Error     NVARCHAR(400) NULL,                                -- truncated by the store to fit
    UpdatedAt DATETIME2 NULL                                     -- also the stale-claim clock
);
GO

-- The dispatch row belongs to its event: no orphan work, and deleting archived events cannot leave
-- a queue entry pointing at nothing.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name='FK_BusinessEventDispatch_Event')
    ALTER TABLE BusinessEventDispatch ADD CONSTRAINT FK_BusinessEventDispatch_Event
        FOREIGN KEY (EventId) REFERENCES BusinessEvents (EventId);
GO

-- One row per (event, consumer). This is also the idempotency guard for creating dispatch rows.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_BusinessEventDispatch_Event_Consumer' AND object_id=OBJECT_ID('BusinessEventDispatch'))
    CREATE UNIQUE INDEX UX_BusinessEventDispatch_Event_Consumer ON BusinessEventDispatch (EventId, Consumer);
GO

-- The claiming index. Filtered on Status <> 'Done' so it stays small forever no matter how large the
-- event log grows: finished work leaves the index entirely.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BusinessEventDispatch_Pending' AND object_id=OBJECT_ID('BusinessEventDispatch'))
    CREATE INDEX IX_BusinessEventDispatch_Pending ON BusinessEventDispatch (Consumer, Status, UpdatedAt)
        INCLUDE (EventId, Attempts) WHERE Status <> 'Done';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_BusinessEventDispatch_Status')
    ALTER TABLE BusinessEventDispatch ADD CONSTRAINT CK_BusinessEventDispatch_Status
        CHECK (Status IN ('Pending','Claimed','Done','Failed'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_BusinessEventDispatch_Attempts')
    ALTER TABLE BusinessEventDispatch ADD CONSTRAINT CK_BusinessEventDispatch_Attempts CHECK (Attempts >= 0);
GO

-- ---------------------------------------------------------------------------------------------
-- Registered consumers in this slice: TimelineProjection ONLY.
--
-- A dispatch row is created per registered consumer inside the business transaction, so registering
-- a consumer that has no implementation would accumulate rows nothing ever drains. Notifications /
-- Search / AI / Dashboards / Integrations are added to BusinessEventConsumers.Registered (and get
-- their rows automatically) as each one is actually implemented.
-- ---------------------------------------------------------------------------------------------