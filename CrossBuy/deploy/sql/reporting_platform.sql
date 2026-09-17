/* ============================================================================================================
   Reporting Platform (ADR-037) — schema, slice 1.

   IDEMPOTENT and ADDITIVE. Re-runnable any number of times: every object is created only if absent, and no
   existing table, column, index or row is altered or dropped. There is NO EF migration for this — the
   Migrations/ folder is a dead snapshot in this repository and deploy/sql is the deployment mechanism.

   SQL BEFORE CODE. Apply this file before deploying the build that contains BL/Reporting. The reverse order does
   not corrupt anything — the reporting tables are only touched by reporting code — but a report run would fail
   with SQL-208 (invalid object name) until the tables exist. Report GENERATION also degrades gracefully: the
   history writer swallows and logs (see ReportHistoryService), so a report still renders if only this file is
   missing. Templates, schedules, favourites, sharing and the archive do require it.

   NOTHING HERE TOUCHES A FINANCIAL OR STOCK TABLE. The reporting platform writes only these twelve tables; the
   GL is still written exclusively by JournalEntryService and stock exclusively by StockService.

   Run with sqlcmd -I (QUOTED_IDENTIFIER ON) so the filtered indexes below build.

   CONVENTIONS FOLLOWED (matching the existing deploy/sql family):
     * plural table names, dbo schema;
     * CompanyID on every table, plus CreatedBy/CreatedAt/updatedBy/UpdatedAt;
     * soft delete via DeletedAt where a row is referenced by another row or is audit-relevant;
     * NVARCHAR for anything user-facing (Arabic), and an English twin column (NameEn) for free-text names.

   CompanyID = 0 IS SIGNIFICANT on ReportTemplates and ReportCategories: it marks a PLATFORM row that belongs to
   no tenant and is visible to every company. It is not a null-substitute; see ReportTemplate.CompanyID in code.
   ============================================================================================================ */

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------------------------------------------
   1. ReportTemplates — named, versioned layouts. Scope: 0=Platform 1=Company 2=Team 3=Personal
        (the numeric values ARE the resolution precedence — see ReportTemplateScope).
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportTemplates (
        Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportTemplates PRIMARY KEY,
        CompanyID         INT           NOT NULL,                  -- 0 = platform row (no tenant)
        ReportCode        NVARCHAR(120) NOT NULL,                  -- frozen catalog code, e.g. Accounting.TrialBalance
        Name              NVARCHAR(200) NOT NULL DEFAULT(N''),
        NameEn            NVARCHAR(200) NULL,
        Scope             INT           NOT NULL DEFAULT(3),       -- 3 = Personal
        OwnerEmpId        INT           NULL,
        TeamId            INT           NULL,                      -- administrative-structure node (Hierarchical)
        CategoryId        INT           NULL,
        CurrentVersionNo  INT           NOT NULL DEFAULT(0),
        IsDefault         BIT           NOT NULL DEFAULT(0),
        DeletedAt         DATETIME2     NULL,
        CreatedBy         INT           NULL,
        CreatedAt         DATETIME2     NULL,
        updatedBy         INT           NULL,
        UpdatedAt         DATETIME2     NULL
    );

    -- The resolution query's covering shape: company + report, then scope. This is the index the precedence walk
    -- (Personal > Team > Company > Platform) rides on, and it is hit on every single report generation.
    CREATE INDEX IX_ReportTemplates_Company_Code_Scope
        ON dbo.ReportTemplates (CompanyID, ReportCode, Scope) INCLUDE (OwnerEmpId, TeamId, IsDefault, CurrentVersionNo);

    CREATE INDEX IX_ReportTemplates_Owner ON dbo.ReportTemplates (OwnerEmpId) WHERE OwnerEmpId IS NOT NULL;
    CREATE INDEX IX_ReportTemplates_Team  ON dbo.ReportTemplates (TeamId)     WHERE TeamId IS NOT NULL;
END
GO

/* ------------------------------------------------------------------------------------------------------------
   2. ReportTemplateVersions — IMMUTABLE published revisions.

   The unique index on (TemplateId, VersionNo) is load-bearing, not decorative: the service computes the next
   version number as MAX+1, so two simultaneous saves of one template would compute the same number. The index
   makes the loser FAIL rather than overwrite. A lost version is recoverable; a silently overwritten one is not.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportTemplateVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportTemplateVersions (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportTemplateVersions PRIMARY KEY,
        CompanyID    INT            NOT NULL,                      -- denormalised from the template
        TemplateId   INT            NOT NULL,
        VersionNo    INT            NOT NULL,                      -- 1-based, monotonic per template
        LayoutJson   NVARCHAR(MAX)  NOT NULL DEFAULT(N'{}'),       -- serialized ReportLayout (presentation only)
        ContentHash  CHAR(64)       NOT NULL DEFAULT(''),          -- SHA-256 of LayoutJson, lowercase hex
        ChangeNote   NVARCHAR(500)  NULL,
        IsPublished  BIT            NOT NULL DEFAULT(1),
        PublishedAt  DATETIME2      NULL,
        PublishedBy  INT            NULL,
        CreatedBy    INT            NULL,
        CreatedAt    DATETIME2      NULL,
        updatedBy    INT            NULL,
        UpdatedAt    DATETIME2      NULL,
        CONSTRAINT FK_ReportTemplateVersions_Template
            FOREIGN KEY (TemplateId) REFERENCES dbo.ReportTemplates (Id)
    );

    CREATE UNIQUE INDEX UX_ReportTemplateVersions_Template_Version
        ON dbo.ReportTemplateVersions (TemplateId, VersionNo);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   3. ReportCategories — the folder tree. CompanyID 0 + IsSystem = a platform category derived from the
      code-first catalog's CategoryKey (materialised by SyncPlatformCategoriesAsync, never hand-seeded here).
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportCategories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportCategories (
        Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportCategories PRIMARY KEY,
        CompanyID  INT           NOT NULL,                         -- 0 = platform category
        [Key]      NVARCHAR(120) NOT NULL,                         -- stable machine key a definition points at
        Name       NVARCHAR(200) NOT NULL DEFAULT(N''),
        NameEn     NVARCHAR(200) NULL,
        ParentId   INT           NULL,                             -- NULL = root
        SortOrder  INT           NOT NULL DEFAULT(0),
        Icon       NVARCHAR(100) NULL,
        IsSystem   BIT           NOT NULL DEFAULT(0),
        DeletedAt  DATETIME2     NULL,
        CreatedBy  INT           NULL,
        CreatedAt  DATETIME2     NULL,
        updatedBy  INT           NULL,
        UpdatedAt  DATETIME2     NULL
    );

    -- Filtered so a soft-deleted category's key can be reused; without the filter, deleting and recreating a
    -- category with the same key would fail forever.
    CREATE UNIQUE INDEX UX_ReportCategories_Company_Key
        ON dbo.ReportCategories (CompanyID, [Key]) WHERE DeletedAt IS NULL;

    CREATE INDEX IX_ReportCategories_Parent ON dbo.ReportCategories (CompanyID, ParentId);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   4. ReportTags + ReportTagLinks — cross-cutting labels (a report has ONE category, ANY number of tags).
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportTags', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportTags (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportTags PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        Name        NVARCHAR(120) NOT NULL DEFAULT(N''),
        NameEn      NVARCHAR(120) NULL,
        ColorToken  NVARCHAR(40)  NOT NULL DEFAULT(N'primary'),    -- Metronic contextual token
        DeletedAt   DATETIME2     NULL,
        CreatedBy   INT           NULL,
        CreatedAt   DATETIME2     NULL,
        updatedBy   INT           NULL,
        UpdatedAt   DATETIME2     NULL
    );

    CREATE UNIQUE INDEX UX_ReportTags_Company_Name
        ON dbo.ReportTags (CompanyID, Name) WHERE DeletedAt IS NULL;
END
GO

IF OBJECT_ID(N'dbo.ReportTagLinks', N'U') IS NULL
BEGIN
    -- A link ROW rather than a delimited column on ReportTags, so "everything tagged month-end" is an index seek
    -- instead of a LIKE scan.
    CREATE TABLE dbo.ReportTagLinks (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportTagLinks PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        TagId       INT           NOT NULL,
        ReportCode  NVARCHAR(120) NOT NULL,
        TemplateId  INT           NULL,                            -- NULL = the tag applies to the report itself
        CreatedBy   INT           NULL,
        CreatedAt   DATETIME2     NULL,
        updatedBy   INT           NULL,
        UpdatedAt   DATETIME2     NULL,
        CONSTRAINT FK_ReportTagLinks_Tag FOREIGN KEY (TagId) REFERENCES dbo.ReportTags (Id)
    );

    CREATE UNIQUE INDEX UX_ReportTagLinks_Unique
        ON dbo.ReportTagLinks (CompanyID, TagId, ReportCode, TemplateId);

    CREATE INDEX IX_ReportTagLinks_Report ON dbo.ReportTagLinks (CompanyID, ReportCode);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   5. ReportFavorites — per EMPLOYEE (not per user account: everything else in the platform keys off EmployeeId).
      Hard-deleted on unpin, so the uniqueness index needs no filter.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportFavorites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportFavorites (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportFavorites PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        EmployeeId  INT           NOT NULL,
        ReportCode  NVARCHAR(120) NOT NULL,
        TemplateId  INT           NULL,                            -- NULL = the report with its resolved default
        SortOrder   INT           NOT NULL DEFAULT(0),
        CreatedBy   INT           NULL,
        CreatedAt   DATETIME2     NULL,
        updatedBy   INT           NULL,
        UpdatedAt   DATETIME2     NULL
    );

    CREATE UNIQUE INDEX UX_ReportFavorites_Unique
        ON dbo.ReportFavorites (CompanyID, EmployeeId, ReportCode, TemplateId);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   6. ReportShares — the reporting platform's OWN access grants.

   PrincipalType: 0=Employee 1=Role 2=Team 3=Company.  AccessLevel: 0=None 1=View 2=Run 3=Edit 4=Manage.

   A grant can only ever RAISE access that the module permission already allowed — ReportAuthorizationService
   checks the module permission FIRST and returns before shares are read. This table therefore cannot be a back
   door around module permissions.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportShares', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportShares (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportShares PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        ReportCode      NVARCHAR(120) NOT NULL,
        TemplateId      INT           NULL,                        -- NULL = every template of the report
        PrincipalType   INT           NOT NULL DEFAULT(0),
        PrincipalKey    NVARCHAR(200) NOT NULL DEFAULT(N''),       -- employee id / role name / team id / '' for Company
        AccessLevel     INT           NOT NULL DEFAULT(2),         -- 2 = Run
        ExpiresAt       DATETIME2     NULL,                        -- evaluated at READ time, never by a sweeper
        GrantedByEmpId  INT           NULL,
        DeletedAt       DATETIME2     NULL,
        CreatedBy       INT           NULL,
        CreatedAt       DATETIME2     NULL,
        updatedBy       INT           NULL,
        UpdatedAt       DATETIME2     NULL
    );

    -- One live grant per (report, template, principal): two live grants would make "what does this person have?"
    -- a question with two answers.
    CREATE UNIQUE INDEX UX_ReportShares_Unique
        ON dbo.ReportShares (CompanyID, ReportCode, TemplateId, PrincipalType, PrincipalKey)
        WHERE DeletedAt IS NULL;

    -- The shape HighestShareLevelAsync reads on every authorization.
    CREATE INDEX IX_ReportShares_Lookup
        ON dbo.ReportShares (CompanyID, ReportCode, DeletedAt) INCLUDE (TemplateId, PrincipalType, PrincipalKey, AccessLevel, ExpiresAt);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   7. ReportRuns — APPEND-ONLY report history.

   No DeletedAt: the record that a report was produced is never deleted. The BYTES expire (ReportArchiveEntries
   .RetainUntil); the fact does not. A DENIED run is recorded too — a history containing only successes would be
   a success log wearing an audit log's name.

   BIGINT identity: this is the highest-volume table in the platform (one row per generation, previews included).
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportRuns (
        Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportRuns PRIMARY KEY,
        CompanyID          INT            NOT NULL,
        ReportCode         NVARCHAR(120)  NOT NULL,
        TemplateId         INT            NULL,
        TemplateVersionNo  INT            NULL,                    -- the EXACT version rendered (reproducibility)
        EmployeeId         INT            NULL,
        Kind               INT            NOT NULL DEFAULT(0),    -- 0=Full 1=Preview 2=Scheduled
        Status             INT            NOT NULL DEFAULT(0),    -- 0=Succeeded 1=Failed 2=Denied 3=Cancelled
        Format             NVARCHAR(20)   NOT NULL DEFAULT(N''),  -- format NAME, so history is readable unaided
        ParametersJson     NVARCHAR(MAX)  NULL,                   -- caller-supplied text only, never system keys
        ParametersHash     CHAR(64)       NULL,                   -- canonical SHA-256: "the same question"
        [RowCount]         INT            NOT NULL DEFAULT(0),   -- bracketed: ROWCOUNT is a reserved T-SQL keyword
        DurationMs         INT            NOT NULL DEFAULT(0),
        ArchiveEntryId     BIGINT         NULL,
        ErrorCode          NVARCHAR(80)   NULL,
        ErrorMessage       NVARCHAR(1000) NULL,                   -- truncated by the writer; never fails an insert
        StartedAt          DATETIME2      NOT NULL,
        CompletedAt        DATETIME2      NULL,
        CorrelationId      UNIQUEIDENTIFIER NULL,                 -- ties a scheduled run to its deliveries
        CreatedBy          INT            NULL,
        CreatedAt          DATETIME2      NULL,
        updatedBy          INT            NULL,
        UpdatedAt          DATETIME2      NULL
    );

    -- The history screen's default query: this company, newest first, optionally one employee.
    CREATE INDEX IX_ReportRuns_Company_Started
        ON dbo.ReportRuns (CompanyID, StartedAt DESC) INCLUDE (ReportCode, EmployeeId, Status, Kind, Format, [RowCount], DurationMs);

    CREATE INDEX IX_ReportRuns_Company_Employee ON dbo.ReportRuns (CompanyID, EmployeeId, StartedAt DESC);
    CREATE INDEX IX_ReportRuns_Company_Report   ON dbo.ReportRuns (CompanyID, ReportCode, StartedAt DESC);

    -- "The same heavy report run forty times in an hour."
    CREATE INDEX IX_ReportRuns_ParametersHash
        ON dbo.ReportRuns (CompanyID, ReportCode, ParametersHash) WHERE ParametersHash IS NOT NULL;
END
GO

/* ------------------------------------------------------------------------------------------------------------
   8. ReportArchiveEntries — metadata for stored artifacts. The BYTES live in IReportArchiveStore (filesystem by
      default), content-addressed by ContentHash, so re-archiving identical content reuses one file.

      IMMUTABLE except DeletedAt (retention). StoredPath is opaque and is path-contained by the store before use —
      a database value is not a trusted path.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportArchiveEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportArchiveEntries (
        Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportArchiveEntries PRIMARY KEY,
        CompanyID          INT           NOT NULL,
        ReportCode         NVARCHAR(120) NOT NULL,
        TemplateId         INT           NULL,
        TemplateVersionNo  INT           NULL,
        RunId              BIGINT        NULL,
        FileName           NVARCHAR(260) NOT NULL DEFAULT(N''),
        ContentType        NVARCHAR(200) NOT NULL DEFAULT(N''),
        Length             BIGINT        NOT NULL DEFAULT(0),
        ContentHash        CHAR(64)      NOT NULL DEFAULT(''),
        StoredPath         NVARCHAR(600) NOT NULL DEFAULT(N''),
        RetainUntil        DATETIME2     NULL,                     -- NULL = keep indefinitely (the default)
        DeletedAt          DATETIME2     NULL,
        CreatedBy          INT           NULL,
        CreatedAt          DATETIME2     NULL,
        updatedBy          INT           NULL,
        UpdatedAt          DATETIME2     NULL
    );

    CREATE INDEX IX_ReportArchiveEntries_Company_Report
        ON dbo.ReportArchiveEntries (CompanyID, ReportCode, Id DESC) INCLUDE (FileName, Length, CreatedBy, CreatedAt);

    -- Retention sweep, and the "is this hash still referenced by a live row" check that stops the sweep from
    -- deleting bytes another live entry shares.
    CREATE INDEX IX_ReportArchiveEntries_Retention
        ON dbo.ReportArchiveEntries (CompanyID, RetainUntil) WHERE DeletedAt IS NULL AND RetainUntil IS NOT NULL;

    CREATE INDEX IX_ReportArchiveEntries_Hash ON dbo.ReportArchiveEntries (CompanyID, ContentHash);
    CREATE INDEX IX_ReportArchiveEntries_Run  ON dbo.ReportArchiveEntries (RunId) WHERE RunId IS NOT NULL;
END
GO

/* ------------------------------------------------------------------------------------------------------------
   9. ReportSchedules — standing instructions. Frequency: 0=Interval 1=Hourly 2=Daily 3=Weekly 4=Monthly.

   NO HOSTED SERVICE consumes NextRunAt in this slice (see ReportScheduleService.cs for the three reasons). The
   table, the calculator and the runner exist; nothing fires on its own yet. Creating the schema now is safe and
   idempotent, and it means turning scheduling on later is a code change with no deployment step.

   OwnerEmpId is NOT NULL by intent: an unattended run with no principal would have to either skip authorization
   or invent a company-wide identity, and both turn a scheduler into a data-exfiltration path.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportSchedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportSchedules (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportSchedules PRIMARY KEY,
        CompanyID       INT            NOT NULL,
        ReportCode      NVARCHAR(120)  NOT NULL,
        TemplateId      INT            NULL,
        Name            NVARCHAR(200)  NOT NULL DEFAULT(N''),
        NameEn          NVARCHAR(200)  NULL,
        Frequency       INT            NOT NULL DEFAULT(2),        -- 2 = Daily
        IntervalMinutes INT            NULL,                       -- Interval only; >= 15 enforced in code
        DayOfWeek       INT            NULL,                       -- Weekly only, 0 = Sunday
        DayOfMonth      INT            NULL,                       -- Monthly only, 1..31 (clamped per month)
        AtHour          INT            NOT NULL DEFAULT(6),
        AtMinute        INT            NOT NULL DEFAULT(0),
        TimeZoneId      NVARCHAR(100)  NOT NULL DEFAULT(N''),      -- per schedule: month-end close is local
        Format          NVARCHAR(20)   NOT NULL DEFAULT(N'Pdf'),
        ParametersJson  NVARCHAR(MAX)  NULL,                       -- system keys are stripped before storing
        IsActive        BIT            NOT NULL DEFAULT(1),
        LastRunAt       DATETIME2      NULL,
        LastRunStatus   NVARCHAR(40)   NULL,
        NextRunAt       DATETIME2      NULL,
        OwnerEmpId      INT            NOT NULL,
        DeletedAt       DATETIME2      NULL,
        CreatedBy       INT            NULL,
        CreatedAt       DATETIME2      NULL,
        updatedBy       INT            NULL,
        UpdatedAt       DATETIME2      NULL
    );

    -- The due-sweep query. Filtered to live+active rows so the index stays small however many schedules are
    -- retired: a sweep runs often and must never scan history.
    CREATE INDEX IX_ReportSchedules_Due
        ON dbo.ReportSchedules (CompanyID, NextRunAt)
        WHERE DeletedAt IS NULL AND IsActive = 1 AND NextRunAt IS NOT NULL;

    CREATE INDEX IX_ReportSchedules_Owner ON dbo.ReportSchedules (CompanyID, OwnerEmpId);
END
GO

IF OBJECT_ID(N'dbo.ReportScheduleRecipients', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportScheduleRecipients (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportScheduleRecipients PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        ScheduleId  INT           NOT NULL,
        ChannelKey  NVARCHAR(40)  NOT NULL DEFAULT(N'Email'),
        Address     NVARCHAR(320) NOT NULL DEFAULT(N''),           -- 320 = max practical email length
        EmployeeId  INT           NULL,
        IsCc        BIT           NOT NULL DEFAULT(0),
        CreatedBy   INT           NULL,
        CreatedAt   DATETIME2     NULL,
        updatedBy   INT           NULL,
        UpdatedAt   DATETIME2     NULL,
        CONSTRAINT FK_ReportScheduleRecipients_Schedule
            FOREIGN KEY (ScheduleId) REFERENCES dbo.ReportSchedules (Id)
    );

    CREATE INDEX IX_ReportScheduleRecipients_Schedule ON dbo.ReportScheduleRecipients (ScheduleId);
END
GO

/* ------------------------------------------------------------------------------------------------------------
   10. ReportDeliveryAttempts — APPEND-ONLY. Status: 0=Pending 1=Sent 2=Failed 3=Skipped.

   Skipped is NOT Sent. With no mail transport bound (NullReportMailSender), every attempt records Skipped with
   the reason — so "we generated 40 reports and delivered 0" is visible instead of invisible. A null-object mailer
   that reported success would produce an audit trail that lies.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.ReportDeliveryAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportDeliveryAttempts (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportDeliveryAttempts PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        ScheduleId      INT           NULL,
        RunId           BIGINT        NULL,
        ArchiveEntryId  BIGINT        NULL,
        ChannelKey      NVARCHAR(40)  NOT NULL DEFAULT(N''),
        Address         NVARCHAR(320) NOT NULL DEFAULT(N''),
        Status          INT           NOT NULL DEFAULT(0),
        Detail          NVARCHAR(1000) NULL,
        AttemptedAt     DATETIME2     NOT NULL,
        CorrelationId   UNIQUEIDENTIFIER NULL,
        CreatedBy       INT           NULL,
        CreatedAt       DATETIME2     NULL,
        updatedBy       INT           NULL,
        UpdatedAt       DATETIME2     NULL
    );

    CREATE INDEX IX_ReportDeliveryAttempts_Schedule
        ON dbo.ReportDeliveryAttempts (CompanyID, ScheduleId, AttemptedAt DESC);

    CREATE INDEX IX_ReportDeliveryAttempts_Run ON dbo.ReportDeliveryAttempts (RunId) WHERE RunId IS NOT NULL;
END
GO

/* ------------------------------------------------------------------------------------------------------------
   11. VOCABULARY CHECK CONSTRAINTS (A0).

   OUTSIDE the CREATE TABLE guards, deliberately. Everything above is wrapped in `IF OBJECT_ID(...) IS NULL`,
   so a constraint written inside those blocks would never reach a database where the tables already exist —
   which is every database that applied an earlier copy of this file. Each constraint therefore guards on its
   own existence, exactly as platform_schema_history.sql does.

   WHAT IS CONSTRAINED, and what deliberately is NOT.

   CONSTRAINED — closed C# enums, stored as INT, whose members are a fixed part of the model. Values verified
   against the enum declarations, not against the comments above them:

       ReportTemplates.Scope         ReportTemplateScope       0..3   Platform Company Team Personal
       ReportShares.PrincipalType    ReportPrincipalType       0..3   Employee Role Team Company
       ReportShares.AccessLevel      ReportAccessLevel         0..4   None View Run Edit Manage
       ReportRuns.Kind               ReportRunKind             0..2   Full Preview Scheduled
       ReportRuns.Status             ReportRunStatus           0..3   Succeeded Failed Denied Cancelled
       ReportSchedules.Frequency     ReportScheduleFrequency   0..4   Interval Hourly Daily Weekly Monthly
       ReportDeliveryAttempts.Status ReportDeliveryStatus      0..3   Pending Sent Failed Skipped

   NOT CONSTRAINED, each for a stated reason — the rule is that a CHECK must not encode EXTENSIBLE vocabulary,
   because the database would then have to be redeployed before code could register a new member:

     * ChannelKey (recipients, delivery attempts) — PLUGIN vocabulary. Delivery channels are discovered from
       registered IReportDeliveryChannel implementations; a new channel is a code registration, and a closed
       CHECK here would turn that into a schema change. This is precisely the case the brief warns about.
     * Format (runs, schedules) — stored as the format NAME so history is readable unaided. ReportOutputFormat
       is closed today, but the renderer registry is explicitly designed for a future engine to claim a format,
       and pinning the column would couple that to a deployment. A wrong value here mis-labels one history row;
       it cannot corrupt a decision.
     * LastRunStatus — free text written for an operator to read, not a decision input.
     * ColorToken — a Metronic contextual token; the palette is presentation and may grow.
     * AtHour / AtMinute / DayOfWeek / DayOfMonth — genuine closed ranges, and worth constraining, but they are
       RANGE validation rather than vocabulary and ReportScheduleService already refuses out-of-range values.
       Considered and deferred to keep this increment's constraint surface to the vocabulary the brief names;
       recorded here so the decision is visible rather than forgotten.

   NO `WITH NOCHECK`. Each constraint validates existing rows on creation. If a database somewhere already holds
   an out-of-range value the ALTER FAILS LOUDLY — which is the correct outcome: it means the data disagrees with
   the model, and silently trusting it is how a bad value becomes permanent.
   ------------------------------------------------------------------------------------------------------------ */

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportTemplates_Scope')
BEGIN
    ALTER TABLE dbo.ReportTemplates ADD CONSTRAINT CK_ReportTemplates_Scope
        CHECK (Scope BETWEEN 0 AND 3);
    PRINT 'ADDED CK_ReportTemplates_Scope';
END
ELSE PRINT 'CK_ReportTemplates_Scope EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportShares_PrincipalType')
BEGIN
    ALTER TABLE dbo.ReportShares ADD CONSTRAINT CK_ReportShares_PrincipalType
        CHECK (PrincipalType BETWEEN 0 AND 3);
    PRINT 'ADDED CK_ReportShares_PrincipalType';
END
ELSE PRINT 'CK_ReportShares_PrincipalType EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportShares_AccessLevel')
BEGIN
    -- 0..4. A grant may only ever RAISE what the module permission already allowed, so an out-of-range level
    -- would not open a back door — but it would make HighestShareLevelAsync's MAX() return a level no code
    -- can interpret, and the caller would get an access decision nobody can explain.
    ALTER TABLE dbo.ReportShares ADD CONSTRAINT CK_ReportShares_AccessLevel
        CHECK (AccessLevel BETWEEN 0 AND 4);
    PRINT 'ADDED CK_ReportShares_AccessLevel';
END
ELSE PRINT 'CK_ReportShares_AccessLevel EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportRuns_Kind')
BEGIN
    ALTER TABLE dbo.ReportRuns ADD CONSTRAINT CK_ReportRuns_Kind
        CHECK (Kind BETWEEN 0 AND 2);
    PRINT 'ADDED CK_ReportRuns_Kind';
END
ELSE PRINT 'CK_ReportRuns_Kind EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportRuns_Status')
BEGIN
    ALTER TABLE dbo.ReportRuns ADD CONSTRAINT CK_ReportRuns_Status
        CHECK (Status BETWEEN 0 AND 3);
    PRINT 'ADDED CK_ReportRuns_Status';
END
ELSE PRINT 'CK_ReportRuns_Status EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportSchedules_Frequency')
BEGIN
    ALTER TABLE dbo.ReportSchedules ADD CONSTRAINT CK_ReportSchedules_Frequency
        CHECK (Frequency BETWEEN 0 AND 4);
    PRINT 'ADDED CK_ReportSchedules_Frequency';
END
ELSE PRINT 'CK_ReportSchedules_Frequency EXISTS';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReportDeliveryAttempts_Status')
BEGIN
    -- Skipped (3) is NOT Sent (1). With no mail transport bound every attempt records Skipped with a reason;
    -- a value outside the set would let "delivered" and "not delivered" become indistinguishable in the audit.
    ALTER TABLE dbo.ReportDeliveryAttempts ADD CONSTRAINT CK_ReportDeliveryAttempts_Status
        CHECK (Status BETWEEN 0 AND 3);
    PRINT 'ADDED CK_ReportDeliveryAttempts_Status';
END
ELSE PRINT 'CK_ReportDeliveryAttempts_Status EXISTS';
GO

/* ------------------------------------------------------------------------------------------------------------
   12. Verification. Prints one row per expected table with its presence, so an operator sees at a glance whether
       the file applied completely. No data is seeded: platform CATEGORIES are materialised from the code-first
       catalog by IReportLibraryService.SyncPlatformCategoriesAsync, so there is no hand-maintained seed list here
       that could disagree with the catalog.
   ------------------------------------------------------------------------------------------------------------ */
-- =================================================================================================
-- REPORT STUDIO V2 — dbo.ReportAssets
--
-- Stored logos, signatures and stamps for the visual designer. The BYTES are on disk under the
-- configured asset root; this row carries only the tenancy, the identity and the metadata. A saved
-- visual layout references an asset by Id and never by path or URL, which is what makes "no
-- arbitrary path, no arbitrary server fetch" a property of the contract rather than a runtime check.
--
-- Idempotent and additive, like every other table in this slice.
-- =================================================================================================
IF OBJECT_ID(N'dbo.ReportAssets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportAssets (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportAssets PRIMARY KEY,
        CompanyID    INT           NOT NULL,
        Role         INT           NOT NULL DEFAULT(0),        -- 0 Custom 1 Logo 2 Signature 3 Stamp
        FileName     NVARCHAR(260) NOT NULL DEFAULT(N''),
        ContentType  NVARCHAR(120) NOT NULL DEFAULT(N''),
        Length       BIGINT        NOT NULL DEFAULT(0),
        StoredPath   NVARCHAR(600) NOT NULL DEFAULT(N''),
        ContentHash  CHAR(64)      NULL,
        Title        NVARCHAR(200) NULL,
        DeletedAt    DATETIME2     NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL
    );

    -- The picker's query: this company's live assets, newest first, optionally by role.
    CREATE INDEX IX_ReportAssets_Company_Role
        ON dbo.ReportAssets (CompanyID, Role, Id DESC) INCLUDE (FileName, ContentType, Length, Title);

    -- De-duplicates a re-upload of the same bytes within one company. FILTERED so soft-deleted rows and
    -- rows written before hashing are not forced unique.
    CREATE UNIQUE INDEX UX_ReportAssets_Company_Hash
        ON dbo.ReportAssets (CompanyID, ContentHash)
        WHERE ContentHash IS NOT NULL AND DeletedAt IS NULL;
END;
GO

-- =================================================================================================
-- DERIVED DATASETS - dbo.ReportDatasetSpecs
--
-- A company's own narrowings of the code-authored datasets: parent code, a new name, and the kept
-- subset as JSON. The row carries NO data-source key and NO permission key, because the contract it
-- stores has no property for either - both are inherited from the parent when the definition is built.
-- That absence is the security argument, and it is deliberately visible in the schema.
--
-- Idempotent and additive, like every other table in this slice.
-- =================================================================================================
IF OBJECT_ID(N'dbo.ReportDatasetSpecs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReportDatasetSpecs (
        Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportDatasetSpecs PRIMARY KEY,
        CompanyID         INT            NOT NULL,
        DatasetCode       NVARCHAR(128)  NOT NULL,
        ParentDatasetCode NVARCHAR(128)  NOT NULL,
        TitleAr           NVARCHAR(200)  NOT NULL DEFAULT(N''),
        TitleEn           NVARCHAR(200)  NOT NULL DEFAULT(N''),
        DescriptionAr     NVARCHAR(600)  NULL,
        DescriptionEn     NVARCHAR(600)  NULL,
        SpecJson          NVARCHAR(MAX)  NOT NULL DEFAULT(N'{}'),
        IsActive          BIT            NOT NULL DEFAULT(1),
        DeletedAt         DATETIME2      NULL,
        CreatedBy         INT            NULL,
        CreatedAt         DATETIME2      NULL,
        UpdatedBy         INT            NULL,
        UpdatedAt         DATETIME2      NULL
    );

    -- The registry build: this company's live derivations, every time a caller lists datasets.
    CREATE INDEX IX_ReportDatasetSpecs_Company
        ON dbo.ReportDatasetSpecs (CompanyID, IsActive)
        INCLUDE (DatasetCode, ParentDatasetCode, TitleAr, TitleEn);

    -- ONE CLAIM PER CODE, PER COMPANY. Two live rows claiming one code would make "which dataset is
    -- this" a question the query plan answers - the same ambiguity the in-memory registry refuses for
    -- code-authored datasets, enforced here for the stored ones. FILTERED so a retired derivation does
    -- not block re-using its name.
    CREATE UNIQUE INDEX UX_ReportDatasetSpecs_Company_Code
        ON dbo.ReportDatasetSpecs (CompanyID, DatasetCode)
        WHERE DeletedAt IS NULL;
END;
GO

SELECT t.TableName,
       CASE WHEN OBJECT_ID(N'dbo.' + t.TableName, N'U') IS NULL THEN 'MISSING' ELSE 'ok' END AS Status
FROM (VALUES
        (N'ReportTemplates'), (N'ReportTemplateVersions'), (N'ReportCategories'),
        (N'ReportTags'), (N'ReportTagLinks'), (N'ReportFavorites'), (N'ReportShares'),
        (N'ReportRuns'), (N'ReportArchiveEntries'), (N'ReportDatasetSpecs'),
        (N'ReportSchedules'), (N'ReportScheduleRecipients'), (N'ReportDeliveryAttempts'),
        (N'ReportAssets')
     ) AS t(TableName)
ORDER BY t.TableName;
GO
