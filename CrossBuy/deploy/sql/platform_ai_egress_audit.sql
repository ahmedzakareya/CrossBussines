-- =============================================================================================
-- AI Foundation — Increment 4.6. AiEgressAudits: a DURABLE record of every external AI egress
-- attempt, allowed or refused.
--
-- Additive + idempotent. Creates one table, its indexes and its check constraints. Touches no
-- existing table, no GL, no stock, no business data.
--
-- WHY THIS EXISTS
--
-- Until now the AI egress audit was ILogger only. Correct in CONTENT — payload-free, secret-free —
-- and ephemeral in NATURE: log retention is an infrastructure setting nobody has tied to this, and
-- "which tenant sent what to which provider, and what did it cost" is a question that must survive
-- a log rotation. It becomes a compliance question the moment a paid provider is approved.
--
-- WHAT IS DELIBERATELY NOT HERE
--
-- No prompt. No completion. No request or response body. No serialized DTO. No API key, no
-- Authorization header, no token of any kind. No PersonalData, no JournalEntry.Description, no free
-- text of any origin. The whole point of the classification matrix is to control what leaves the
-- estate; storing the payload here would recreate, inside CrossBuy, the retention the owner
-- forbade at the provider. See the DELIBERATELY ABSENT COLUMNS note at the foot of this file.
--
-- APPEND-ONLY. Nothing updates a row after insert. An audit that can be edited is not an audit.
-- =============================================================================================

IF OBJECT_ID('dbo.AiEgressAudits', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiEgressAudits (
        ID                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiEgressAudits PRIMARY KEY,

        -- Tenancy. NOT NULL and > 0: an audit row that cannot say whose data left is not an audit
        -- row. The application refuses an unresolved company long before here; the constraint means
        -- a future caller cannot write one either.
        CompanyID          INT            NOT NULL,

        -- Which provider, which capability, which model. ProviderId is our identifier ("OpenAI"),
        -- not a URL.
        ProviderId         NVARCHAR(64)   NOT NULL,

        -- The AiEgressPurpose. Named Feature because that is the word the rest of the AI subsystem
        -- uses for the same idea (AiRateLimitKey.Feature, AiUsageKey.Feature); Purpose and Feature
        -- are ONE concept here and are deliberately not stored twice.
        Feature            NVARCHAR(64)   NOT NULL,

        Model              NVARCHAR(128)  NULL,          -- NULL when refused before a model was chosen

        Classification     NVARCHAR(64)   NOT NULL,      -- AiDataClassification
        DestinationClass   NVARCHAR(64)   NOT NULL,      -- AiEgressDestinationClass

        -- The governance verdict and the identity of the decision (AiEgressApproval.AppliedPolicy,
        -- or the deny reason). An identity and a machine code — never a payload.
        GovernanceDecision NVARCHAR(256)  NOT NULL,
        ApprovalReference  NVARCHAR(256)  NULL,

        CorrelationId      NVARCHAR(128)  NOT NULL,

        -- DATETIME2(3): milliseconds. Higher precision would imply an ordering guarantee that
        -- concurrent inserts do not provide; ID is the tie-breaker for ordering.
        OccurredAtUtc      DATETIME2(3)   NOT NULL,
        DurationMs         INT            NOT NULL CONSTRAINT DF_AiEgressAudits_DurationMs DEFAULT (0),

        -- Success is the cheap filter; Outcome is the detail. Both, because "how many failed today"
        -- must not require parsing a string, and "how did it fail" must not be lost to a bit.
        Success            BIT            NOT NULL,
        Outcome            NVARCHAR(32)   NOT NULL,      -- AiProviderOutcome
        FailureCategory    NVARCHAR(128)  NULL,          -- e.g. 'provider-error:429'. NEVER a provider message.

        InputTokens        INT            NOT NULL CONSTRAINT DF_AiEgressAudits_InputTokens  DEFAULT (0),
        OutputTokens       INT            NOT NULL CONSTRAINT DF_AiEgressAudits_OutputTokens DEFAULT (0),

        -- COMPUTED + PERSISTED rather than a third stored column: a total that can disagree with its
        -- own parts is a reporting bug waiting to happen. Persisted so it can be indexed and summed
        -- without recomputation.
        TotalTokens        AS (InputTokens + OutputTokens) PERSISTED,

        RequestBytes       INT            NOT NULL CONSTRAINT DF_AiEgressAudits_RequestBytes DEFAULT (0),

        -- NULL means UNPRICED, never free. A model with no configured price must not accumulate as
        -- 0.000000 beside real token counts — that reads as "this cost nothing", a different and
        -- wrong claim. DECIMAL(18,6): six places because per-request costs are frequently in the
        -- fourth or fifth decimal, and rounding them to currency precision at row level would make
        -- a monthly SUM visibly wrong.
        EstimatedCost      DECIMAL(18,6)  NULL,
        CostCurrency       NVARCHAR(8)    NULL,          -- ISO 4217; NULL exactly when EstimatedCost is NULL

        CreatedAtUtc       DATETIME2(3)   NOT NULL CONSTRAINT DF_AiEgressAudits_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
    PRINT 'ADDED dbo.AiEgressAudits';
END
ELSE PRINT 'dbo.AiEgressAudits EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- CONSTRAINTS. Each one encodes an invariant the application already enforces, so that a future
-- caller — a report, a script, a second service — cannot write a row the application would refuse.
-- ---------------------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AiEgressAudits_Company')
BEGIN
    -- Company 0 is the "unresolved" sentinel everywhere else in this codebase. An audit row is
    -- exactly the wrong place for it.
    ALTER TABLE dbo.AiEgressAudits ADD CONSTRAINT CK_AiEgressAudits_Company CHECK (CompanyID > 0);
    PRINT 'ADDED CK_AiEgressAudits_Company';
END
ELSE PRINT 'CK_AiEgressAudits_Company EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AiEgressAudits_Failure')
BEGIN
    -- A failure with no category recorded is not a usable record, and a success carrying a failure
    -- category is a contradiction. Both are rejected rather than stored. Mirrors
    -- CK_PlatformSchemaHistory_Error, which exists for the same reason.
    ALTER TABLE dbo.AiEgressAudits ADD CONSTRAINT CK_AiEgressAudits_Failure
        CHECK ((Success = 1 AND FailureCategory IS NULL) OR (Success = 0 AND FailureCategory IS NOT NULL));
    PRINT 'ADDED CK_AiEgressAudits_Failure';
END
ELSE PRINT 'CK_AiEgressAudits_Failure EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AiEgressAudits_Cost')
BEGIN
    -- Currency and amount travel together or not at all. A cost with no currency is unusable; a
    -- currency with no cost implies a zero that was never measured.
    ALTER TABLE dbo.AiEgressAudits ADD CONSTRAINT CK_AiEgressAudits_Cost
        CHECK ((EstimatedCost IS NULL AND CostCurrency IS NULL)
            OR (EstimatedCost IS NOT NULL AND CostCurrency IS NOT NULL));
    PRINT 'ADDED CK_AiEgressAudits_Cost';
END
ELSE PRINT 'CK_AiEgressAudits_Cost EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AiEgressAudits_Counters')
BEGIN
    -- Negative tokens, bytes or duration are impossible and would corrupt every SUM built on them.
    ALTER TABLE dbo.AiEgressAudits ADD CONSTRAINT CK_AiEgressAudits_Counters
        CHECK (InputTokens >= 0 AND OutputTokens >= 0 AND RequestBytes >= 0 AND DurationMs >= 0);
    PRINT 'ADDED CK_AiEgressAudits_Counters';
END
ELSE PRINT 'CK_AiEgressAudits_Counters EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- INDEXES. Three, one per question that will actually be asked.
-- ---------------------------------------------------------------------------------------------

-- "What did this tenant send, and when." The company-scoped read, which is every operator screen.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AiEgressAudits_Company_Time' AND object_id = OBJECT_ID('dbo.AiEgressAudits'))
BEGIN
    CREATE INDEX IX_AiEgressAudits_Company_Time
        ON dbo.AiEgressAudits (CompanyID, OccurredAtUtc DESC)
        INCLUDE (ProviderId, Feature, Success, Outcome, TotalTokens, EstimatedCost, CostCurrency);
    PRINT 'ADDED IX_AiEgressAudits_Company_Time';
END
ELSE PRINT 'IX_AiEgressAudits_Company_Time EXISTS';
GO

-- "What has this provider cost, across all tenants, this period." The billing reconciliation.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AiEgressAudits_Provider_Time' AND object_id = OBJECT_ID('dbo.AiEgressAudits'))
BEGIN
    CREATE INDEX IX_AiEgressAudits_Provider_Time
        ON dbo.AiEgressAudits (ProviderId, OccurredAtUtc DESC)
        INCLUDE (CompanyID, Feature, Model, TotalTokens, EstimatedCost, CostCurrency);
    PRINT 'ADDED IX_AiEgressAudits_Provider_Time';
END
ELSE PRINT 'IX_AiEgressAudits_Provider_Time EXISTS';
GO

-- "Show me every refusal." FILTERED, so a healthy database keeps this index nearly empty and the
-- security question stays cheap to ask. Same technique as IX_PlatformSchemaHistory_Failed.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AiEgressAudits_Failed' AND object_id = OBJECT_ID('dbo.AiEgressAudits'))
BEGIN
    CREATE INDEX IX_AiEgressAudits_Failed
        ON dbo.AiEgressAudits (OccurredAtUtc DESC)
        INCLUDE (CompanyID, ProviderId, Feature, Outcome, FailureCategory)
        WHERE Success = 0;
    PRINT 'ADDED IX_AiEgressAudits_Failed';
END
ELSE PRINT 'IX_AiEgressAudits_Failed EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- DELIBERATELY ABSENT COLUMNS
--
-- Recorded here so "why is there no prompt column?" is answered in the file, and nobody adds one as
-- a convenience:
--
--   ApiKey / Authorization  A credential in an audit table is a credential in every backup, every
--                           restore to a test box, and every DBA's query window.
--   Prompt / RequestBody    The payload is precisely what the classification matrix exists to
--                           control. Storing it here would recreate, inside CrossBuy, the retention
--                           the owner forbade at the provider.
--   Completion / Response   The same, and it can echo the input back.
--   ProviderMessage         A provider error body frequently quotes the offending request. Only the
--                           status CATEGORY is stored (FailureCategory).
--   UserId / EmployeeId     Omitted deliberately. "Who ran this analysis" is a personal-data
--                           question with its own retention answer; CorrelationId already links to
--                           the request log where that lives under existing rules.
--
-- RETENTION. This table has no ExpiresAtUtc, and that is a decision rather than an oversight: an
-- audit trail whose purpose is compliance must not silently expire on a policy set by the subsystem
-- it audits. Pruning is an operator decision, taken with Legal, and belongs in a separate reviewed
-- script — not in the code path that writes the rows.
-- ---------------------------------------------------------------------------------------------
