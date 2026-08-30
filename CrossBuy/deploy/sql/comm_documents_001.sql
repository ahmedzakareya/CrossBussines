-- =============================================================================================
-- Central Document Platform — slice comm_documents_001.
--
-- AUTHORED, NOT EXECUTED. This batch does not run DDL anywhere: no production write, and no change
-- to CrossBuyDev. The slice is the deployable artefact; applying it is a separate, explicit act.
--
-- THE comm_ PREFIX IS A GOVERNANCE ARTEFACT, NOT A CLAIM. deploy/sql/comm_* is the only SQL glob
-- this work stream owns; platform_* belongs to another tab. The tables are PlatformDocument*, which
-- is what they are. Renaming the slice once a documents path is granted is a rename with no
-- behavioural change, and is recorded as debt rather than taken by claiming a path not granted.
--
-- WHAT THIS IS. Three tables that make a governed document possible:
--   PlatformDocuments         one governed document per business record
--   PlatformDocumentVersions  APPEND-ONLY history; a renewal adds a row, never overwrites one
--   PlatformDocumentTypes     the configurable catalogue, so HR never hardcodes "Passport" again
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONLY new objects:
--   * NO existing table is altered.
--   * NO existing row is read, written or migrated. There is no backfill anywhere in this script,
--     and legacy EmployeeDocuments / HrDocumentAttachments / ApplicationDocuments / Attachments /
--     LibraryItems are deliberately untouched — convergence is a later, separate decision.
--   * NO GL, stock, accounting, inventory, CRM, authorization or POS object is referenced.
--
-- NO FOREIGN KEY POINTS AT A BUSINESS TABLE. EntityType/EntityId is a REFERENCE, not a relation:
-- a hard FK would have to point at fourteen different tables, and the platform would then need a
-- schema change to onboard a fifteenth family. The company boundary is enforced in the service, by
-- the owner resolver, on every read — which is where it has to be anyway, because a FK cannot
-- express "and the caller's module says yes".
--
-- SQL BEFORE CODE. Migrations are disabled in this project (idempotent SQL in deploy/sql, not EF
-- migrations), so apply this BEFORE deploying code that writes documents.
--
-- RUN IT WITH sqlcmd -I  (QUOTED_IDENTIFIER ON) — the expiry index below is FILTERED, and SQL Server
-- refuses to create a filtered index when QUOTED_IDENTIFIER is OFF.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ---- 1. the catalogue --------------------------------------------------------------------------
IF OBJECT_ID('dbo.PlatformDocumentTypes') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformDocumentTypes
    (
        Id                     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlatformDocumentTypes PRIMARY KEY,
        -- NULL = a platform-wide type every company sees. A company row overrides by Code.
        CompanyID              int              NULL,
        Code                   nvarchar(64)     NOT NULL,
        NameAr                 nvarchar(200)    NOT NULL,
        NameEn                 nvarchar(200)    NOT NULL,
        -- Comma-separated EntityRegistry codes. A Passport applies to Employee and to nothing else.
        AppliesToEntityTypes   nvarchar(400)    NOT NULL,
        IsMandatory            bit              NOT NULL CONSTRAINT DF_PlatformDocumentTypes_Mandatory DEFAULT (0),
        RequiresIssueDate      bit              NOT NULL CONSTRAINT DF_PlatformDocumentTypes_IssueDate DEFAULT (0),
        RequiresExpiryDate     bit              NOT NULL CONSTRAINT DF_PlatformDocumentTypes_ExpiryDate DEFAULT (0),
        DefaultConfidentiality nvarchar(32)     NOT NULL CONSTRAINT DF_PlatformDocumentTypes_Conf DEFAULT ('Internal'),
        AllowedExtensions      nvarchar(400)        NULL,
        MaxSizeBytes           bigint               NULL,
        MetadataSchema         nvarchar(max)        NULL,
        IsActive               bit              NOT NULL CONSTRAINT DF_PlatformDocumentTypes_Active DEFAULT (1),
        SortOrder              int              NOT NULL CONSTRAINT DF_PlatformDocumentTypes_Sort DEFAULT (0),
        -- Mirrors the frozen C# vocabulary. An unconstrained string column forks into two vocabularies.
        CONSTRAINT CK_PlatformDocumentTypes_Conf CHECK
            (DefaultConfidentiality IN ('Internal','Confidential','Restricted','System'))
    );
    PRINT 'PlatformDocumentTypes created.';
END
ELSE PRINT 'PlatformDocumentTypes already present.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PlatformDocumentTypes_Scope_Code')
    AND OBJECT_ID('dbo.PlatformDocumentTypes') IS NOT NULL
BEGIN
    -- One platform-wide PASSPORT, and at most one company override of it per company.
    CREATE UNIQUE INDEX UX_PlatformDocumentTypes_Scope_Code
        ON dbo.PlatformDocumentTypes (CompanyID, Code);
    PRINT 'UX_PlatformDocumentTypes_Scope_Code created.';
END
GO

-- ---- 2. the documents --------------------------------------------------------------------------
IF OBJECT_ID('dbo.PlatformDocuments') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformDocuments
    (
        Id              bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlatformDocuments PRIMARY KEY,
        -- Written from the RESOLVED BusinessContext and re-checked against the owning record's own
        -- company before insert. Never a caller value, never a default.
        CompanyID       int            NOT NULL,
        EntityType      nvarchar(64)   NOT NULL,
        EntityId        int            NOT NULL,
        DocumentTypeId  bigint             NULL CONSTRAINT FK_PlatformDocuments_Type
                                              REFERENCES dbo.PlatformDocumentTypes (Id),
        -- Set after the first version commits, inside the same transaction.
        CurrentVersionId bigint            NULL,
        Confidentiality nvarchar(32)   NOT NULL CONSTRAINT DF_PlatformDocuments_Conf DEFAULT ('Internal'),

        -- The four fixed business fields. Columns rather than JSON because every governed document in
        -- this product already carries them (EmployeeDocument and ApplicationDocument both do), and
        -- because expiry must be an indexed seek rather than a scan that parses JSON per row.
        DocumentNumber  nvarchar(200)      NULL,
        IssueDate       datetime2          NULL,
        EffectiveFrom   datetime2          NULL,
        ExpiryDate      datetime2          NULL,

        Metadata        nvarchar(max)      NULL,
        Status          nvarchar(32)   NOT NULL CONSTRAINT DF_PlatformDocuments_Status DEFAULT ('Active'),
        CreatedBy       int            NOT NULL,
        CreatedAt       datetime2      NOT NULL CONSTRAINT DF_PlatformDocuments_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       int                NULL,
        UpdatedAt       datetime2          NULL,

        CONSTRAINT CK_PlatformDocuments_Conf CHECK
            (Confidentiality IN ('Internal','Confidential','Restricted','System')),
        CONSTRAINT CK_PlatformDocuments_Status CHECK
            (Status IN ('Draft','Active','Expired','Archived'))
    );
    PRINT 'PlatformDocuments created.';
END
ELSE PRINT 'PlatformDocuments already present.';
GO

IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformDocuments_Entity')
BEGIN
    -- The read every screen performs: this record's documents, in this company. Company leads
    -- because it is the boundary that must be cheap to enforce on every single query.
    CREATE INDEX IX_PlatformDocuments_Entity
        ON dbo.PlatformDocuments (CompanyID, EntityType, EntityId) INCLUDE (Status, Confidentiality);
    PRINT 'IX_PlatformDocuments_Entity created.';
END
GO

IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformDocuments_Expiry')
BEGIN
    -- Filtered: only documents that actually expire. An expiry sweep must not read the rest.
    CREATE INDEX IX_PlatformDocuments_Expiry
        ON dbo.PlatformDocuments (CompanyID, ExpiryDate)
        WHERE ExpiryDate IS NOT NULL AND Status = 'Active';
    PRINT 'IX_PlatformDocuments_Expiry created.';
END
GO

-- ---- 3. the append-only history ----------------------------------------------------------------
IF OBJECT_ID('dbo.PlatformDocumentVersions') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformDocumentVersions
    (
        Id                bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlatformDocumentVersions PRIMARY KEY,
        CompanyID         int           NOT NULL,
        DocumentId        bigint        NOT NULL CONSTRAINT FK_PlatformDocumentVersions_Document
                                            REFERENCES dbo.PlatformDocuments (Id),
        VersionNo         int           NOT NULL,
        -- Opaque handle. NOT a path, and never an authorization: every read re-runs the owner check,
        -- so knowing a StorageKey buys nothing at all.
        StorageKey        nvarchar(64)  NOT NULL,
        FileName          nvarchar(200) NOT NULL,
        ContentType       nvarchar(200) NOT NULL,
        SizeBytes         bigint        NOT NULL CONSTRAINT DF_PlatformDocumentVersions_Size DEFAULT (0),
        Reason            nvarchar(400)     NULL,
        ReplacesVersionId bigint            NULL CONSTRAINT FK_PlatformDocumentVersions_Replaces
                                            REFERENCES dbo.PlatformDocumentVersions (Id),
        UploadedBy        int           NOT NULL,
        UploadedAt        datetime2     NOT NULL CONSTRAINT DF_PlatformDocumentVersions_UploadedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_PlatformDocumentVersions_VersionNo CHECK (VersionNo > 0)
    );
    PRINT 'PlatformDocumentVersions created.';
END
ELSE PRINT 'PlatformDocumentVersions already present.';
GO

IF OBJECT_ID('dbo.PlatformDocumentVersions') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PlatformDocumentVersions_Document_No')
BEGIN
    -- THE constraint that makes append-only real. Two simultaneous renewals cannot both claim V2 and
    -- silently lose one of the two files: the database refuses the second, and the service re-reads.
    CREATE UNIQUE INDEX UX_PlatformDocumentVersions_Document_No
        ON dbo.PlatformDocumentVersions (DocumentId, VersionNo);
    PRINT 'UX_PlatformDocumentVersions_Document_No created.';
END
GO

-- The current-version pointer is added AFTER the versions table exists, so the two FKs can be
-- created in either order on a fresh database without a circular dependency.
IF OBJECT_ID('dbo.PlatformDocuments') IS NOT NULL
   AND OBJECT_ID('dbo.PlatformDocumentVersions') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PlatformDocuments_CurrentVersion')
BEGIN
    ALTER TABLE dbo.PlatformDocuments WITH NOCHECK
        ADD CONSTRAINT FK_PlatformDocuments_CurrentVersion
        FOREIGN KEY (CurrentVersionId) REFERENCES dbo.PlatformDocumentVersions (Id);
    PRINT 'FK_PlatformDocuments_CurrentVersion created.';
END
GO

PRINT 'comm_documents_001 complete. NOTE: this slice creates structure only - it seeds no document types and migrates no legacy attachment.';
GO
