-- ==========================================================================================
-- Tasks Ecosystem — checklist, dependencies, templates.
--
-- FOUR NEW TABLES. No ALTER of TaskItems, no ALTER of any existing table, no backfill.
-- That is deliberate: this tree is shared by four concurrent workstreams, and a new table that
-- has not been applied yet breaks only the new screens. An altered TaskItems would break the
-- Tasks list every other stream already depends on.
--
-- Idempotent: safe to run repeatedly. Column names follow the Tasks-module convention
-- (CompanyId, not the older CompanyID used by Inventory/Accounting tables).
--
-- NOT EXECUTED against any database by the authoring session.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- ---- checklist -------------------------------------------------------------------------
-- A checklist line is a note, not a status. It deliberately does NOT drive TaskItems.ProgressPct:
-- that column has always been a human's judgement, and recomputing it here would silently change
-- what an existing, already-reported number means.
IF OBJECT_ID('dbo.TaskChecklistItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.TaskChecklistItems (
        ID                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskChecklistItems PRIMARY KEY,
        CompanyId          int           NOT NULL,
        TaskId             int           NOT NULL,
        Title              nvarchar(400) NOT NULL,
        IsDone             bit           NOT NULL CONSTRAINT DF_TaskChecklistItems_Done DEFAULT(0),
        DoneAt             datetime2     NULL,
        DoneByEmployeeId   int           NULL,
        SortOrder          int           NOT NULL CONSTRAINT DF_TaskChecklistItems_Sort DEFAULT(0),
        CreatedAt          datetime2     NOT NULL CONSTRAINT DF_TaskChecklistItems_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int          NULL
    );
    CREATE INDEX IX_TaskChecklistItems_Task ON dbo.TaskChecklistItems (CompanyId, TaskId, SortOrder);
END

-- ---- dependencies ----------------------------------------------------------------------
-- PREDECESSOR happens first; SUCCESSOR is the one blocked.
IF OBJECT_ID('dbo.TaskDependencies','U') IS NULL
BEGIN
    CREATE TABLE dbo.TaskDependencies (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskDependencies PRIMARY KEY,
        CompanyId           int          NOT NULL,
        PredecessorTaskId   int          NOT NULL,
        SuccessorTaskId     int          NOT NULL,
        -- FinishToStart | StartToStart | FinishToFinish | StartToFinish.
        -- Only FinishToStart gates today; the others are recorded and shown as non-blocking.
        Kind                nvarchar(20) NOT NULL CONSTRAINT DF_TaskDependencies_Kind DEFAULT('FinishToStart'),
        LagDays             int          NOT NULL CONSTRAINT DF_TaskDependencies_Lag DEFAULT(0),
        CreatedAt           datetime2    NOT NULL CONSTRAINT DF_TaskDependencies_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int          NULL
    );

    -- One edge per (predecessor, successor). A second identical edge is not a second dependency,
    -- it is a duplicate, and it would double-count in every blocked calculation. The service
    -- refuses it too; this index is what makes the refusal true under concurrency.
    CREATE UNIQUE INDEX UX_TaskDependencies_Edge
        ON dbo.TaskDependencies (CompanyId, PredecessorTaskId, SuccessorTaskId);

    -- The board asks "what blocks this card" for every card at once.
    CREATE INDEX IX_TaskDependencies_Successor ON dbo.TaskDependencies (CompanyId, SuccessorTaskId);

    -- A task cannot wait for itself. Cycles of length > 1 cannot be expressed as a row constraint —
    -- they are refused by TaskDependencyService's reachability check before the insert.
    ALTER TABLE dbo.TaskDependencies WITH CHECK
        ADD CONSTRAINT CK_TaskDependencies_NotSelf CHECK (PredecessorTaskId <> SuccessorTaskId);
END

-- ---- templates -------------------------------------------------------------------------
IF OBJECT_ID('dbo.TaskTemplates','U') IS NULL
BEGIN
    CREATE TABLE dbo.TaskTemplates (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskTemplates PRIMARY KEY,
        CompanyId           int           NOT NULL,
        Name                nvarchar(200) NOT NULL,
        NameEn              nvarchar(200) NULL,
        [Description]       nvarchar(1000) NULL,
        DescriptionEn       nvarchar(1000) NULL,
        IsActive            bit           NOT NULL CONSTRAINT DF_TaskTemplates_Active DEFAULT(1),
        CreatedAt           datetime2     NOT NULL CONSTRAINT DF_TaskTemplates_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int           NULL,
        UpdatedAt           datetime2     NULL,
        UpdatedByEmployeeId int           NULL
    );
    CREATE UNIQUE INDEX UX_TaskTemplates_Name ON dbo.TaskTemplates (CompanyId, Name);
END

IF OBJECT_ID('dbo.TaskTemplateItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.TaskTemplateItems (
        ID                       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskTemplateItems PRIMARY KEY,
        TaskTemplateId           int            NOT NULL,
        CompanyId                int            NOT NULL,
        Title                    nvarchar(400)  NOT NULL,
        TitleEn                  nvarchar(400)  NULL,
        [Description]            nvarchar(2000) NULL,
        DescriptionEn            nvarchar(2000) NULL,
        Priority                 nvarchar(20)   NOT NULL CONSTRAINT DF_TaskTemplateItems_Prio DEFAULT('Normal'),
        EstimatedHours           decimal(9,2)   NULL,
        -- Days from the anchor date supplied at apply time, NOT an absolute date: a template that
        -- stored real dates would be stale the day after it was written.
        DueOffsetDays            int            NOT NULL CONSTRAINT DF_TaskTemplateItems_Due DEFAULT(0),
        DefaultAssigneeEmployeeId int           NULL,
        SortOrder                int            NOT NULL CONSTRAINT DF_TaskTemplateItems_Sort DEFAULT(0),
        -- Points at another item's SortOrder WITHIN THE SAME TEMPLATE. Template items have no task
        -- ids until they are applied, so the link has to be by position.
        PredecessorSortOrder     int            NULL,
        -- Newline-separated checklist lines, materialised into real rows at apply time.
        ChecklistLines           nvarchar(max)  NULL,
        CONSTRAINT FK_TaskTemplateItems_Template FOREIGN KEY (TaskTemplateId)
            REFERENCES dbo.TaskTemplates (ID) ON DELETE CASCADE
    );
    CREATE INDEX IX_TaskTemplateItems_Template ON dbo.TaskTemplateItems (TaskTemplateId, SortOrder);
END
GO

-- ---- ADDITIVE: DescriptionEn ------------------------------------------------------------
--
-- OUTSIDE the CREATE TABLE guards, deliberately. Everything above only runs when the table does
-- not exist yet, so a column added to a CREATE body reaches a fresh database and NO existing one.
-- Both tables already carry an English twin for their name/title; the description had none, so an
-- English UI printed an Arabic description under an English template name.
--
-- Idempotent and additive: nullable, no default, no backfill, no data touched.
IF COL_LENGTH('dbo.TaskTemplates', 'DescriptionEn') IS NULL
    ALTER TABLE dbo.TaskTemplates ADD DescriptionEn nvarchar(1000) NULL;
GO

IF COL_LENGTH('dbo.TaskTemplateItems', 'DescriptionEn') IS NULL
    ALTER TABLE dbo.TaskTemplateItems ADD DescriptionEn nvarchar(2000) NULL;
GO
