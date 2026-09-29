-- =============================================================================================
-- PROJECTS-2 — ProjectMilestones and ProjectDeliverables: what a project must deliver, and when.
--
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact, NO data modification,
-- NO change to Projects or any existing table, NO backfill, NO seed rows.
--
-- EXISTING PROJECTS ARE UNAFFECTED, and that is a requirement rather than a side effect. A project
-- with zero milestones is a complete, valid, closable project: nothing in this script or in the code
-- reads "no milestones" as "incomplete". Closure criteria are NOT changed by this wave —
-- ProjectCloseoutService is untouched, so a project that could be closed before can still be closed
-- after. Whether an unmet milestone should one day block closure is a product decision, recorded and
-- deliberately not taken here.
--
-- WHY TWO TABLES AND NOT ONE. A milestone is a dated COMMITMENT; a deliverable is a THING handed
-- over. Several deliverables can belong to one commitment, and a project may track deliverables with
-- no milestones at all — so ProjectDeliverables.MilestoneId is NULLABLE. Collapsing them into one
-- self-referencing table (BoqItem's shape) was considered and rejected: the two rows have different
-- required fields, TargetDate being the clearest — a milestone must have one and a deliverable
-- need not.
--
-- WHY THIS IS NOT A SECOND TASKS TABLE. There is no assignee, no hours, no priority, no escalation
-- level, no hold state and no approval column here. Work lives in dbo.TaskItems, which already links
-- to any record through EntityType/EntityId. A milestone says WHAT and WHEN; a task says WHO.
--
-- DEPLOYMENT: apply BEFORE the PROJECTS-2 code ("SQL before code"). Requires dbo.Projects to exist.
-- Filtered/necessary indexes require QUOTED_IDENTIFIER ON — deploy with sqlcmd -I.
-- =============================================================================================
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. ProjectMilestones
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ProjectMilestones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectMilestones
    (
        ID              int            IDENTITY(1,1) NOT NULL,

        -- Denormalised from Project deliberately, and for exactly ProjectMembers' reason: every access
        -- check filters by company FIRST, and a join to Projects on the hot path to learn the company
        -- would make the company predicate depend on the very table being authorized. The invariant is
        -- re-checked in ProjectMilestoneService, which reads the company from BusinessContext and the
        -- project row — never from the request.
        CompanyID       int            NOT NULL,

        ProjectId       int            NOT NULL,

        -- Arabic primary with an English twin, the convention Projects and BoqItems already follow.
        Name            nvarchar(200)  NOT NULL,
        NameEn          nvarchar(200)  NULL,
        Description     nvarchar(max)  NULL,
        DescriptionEn   nvarchar(max)  NULL,

        -- REQUIRED. A commitment with no date is not a milestone, so this is the definition rather
        -- than a constraint. Deliverables may be undated; milestones may not.
        TargetDate      date           NOT NULL,

        -- ProjectMilestoneStatuses. Four values, bounded by CK below.
        Status          nvarchar(20)   NOT NULL CONSTRAINT DF_ProjectMilestones_Status DEFAULT (N'Planned'),

        -- Author-chosen order. Milestones are a sequence a human reads, and date order is not always
        -- that sequence — two commitments can share a target date.
        SortOrder       int            NOT NULL CONSTRAINT DF_ProjectMilestones_SortOrder DEFAULT (0),

        -- Set on completion, cleared on reopen, so the row answers "when was this met" by itself.
        CompletedAt     datetime2(7)   NULL,
        CompletedBy     int            NULL,

        CreatedBy       int            NULL,
        CreatedAt       datetime2(7)   NOT NULL CONSTRAINT DF_ProjectMilestones_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       int            NULL,
        UpdatedAt       datetime2(7)   NULL,

        CONSTRAINT PK_ProjectMilestones PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMilestones_Keys')
    ALTER TABLE dbo.ProjectMilestones WITH CHECK
        ADD CONSTRAINT CK_ProjectMilestones_Keys
        CHECK (CompanyID > 0 AND ProjectId > 0);
GO

-- Only the four supported states. Mirrors ProjectMilestoneStatuses so a hand-inserted row cannot
-- create a state no service knows how to move out of.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMilestones_Status')
    ALTER TABLE dbo.ProjectMilestones WITH CHECK
        ADD CONSTRAINT CK_ProjectMilestones_Status
        CHECK (Status IN (N'Planned', N'InProgress', N'Completed', N'Cancelled'));
GO

-- CompletedAt and the Completed state travel together in BOTH directions. Without this a reopened
-- milestone could keep a completion timestamp, and the row would claim to have been met and not met
-- at the same time.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectMilestones_Completion')
    ALTER TABLE dbo.ProjectMilestones WITH CHECK
        ADD CONSTRAINT CK_ProjectMilestones_Completion
        CHECK ((Status = N'Completed' AND CompletedAt IS NOT NULL)
            OR (Status <> N'Completed' AND CompletedAt IS NULL));
GO

-- NO CASCADE: deleting a project must not silently erase what it promised. This module reverses and
-- cancels rather than deleting, by convention.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ProjectMilestones_Project')
    ALTER TABLE dbo.ProjectMilestones WITH CHECK
        ADD CONSTRAINT FK_ProjectMilestones_Project FOREIGN KEY (ProjectId)
        REFERENCES dbo.Projects (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

-- THE ONE QUERY THIS TABLE SERVES: the milestones of one project, in author order. Company leads
-- because every read is company-scoped first. A separate (ProjectId, Status) index is deliberately
-- NOT created — it would duplicate this index's prefix to filter a set already narrowed to a single
-- project, which is a write cost for no read.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectMilestones_ProjectOrder'
               AND object_id = OBJECT_ID(N'dbo.ProjectMilestones'))
    CREATE INDEX IX_ProjectMilestones_ProjectOrder
        ON dbo.ProjectMilestones (CompanyID, ProjectId, SortOrder);
GO

-- ---------------------------------------------------------------------------------------------
-- 2. ProjectDeliverables
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ProjectDeliverables', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectDeliverables
    (
        ID              int            IDENTITY(1,1) NOT NULL,

        CompanyID       int            NOT NULL,
        ProjectId       int            NOT NULL,

        -- NULL = a project-level deliverable, which is a real shape rather than an orphan: a project
        -- may track what it owes without grouping it under dated commitments. Forcing a placeholder
        -- milestone to express that would put fictitious promises in the list an owner reads.
        --
        -- When set, ProjectMilestoneService proves the milestone belongs to THIS project and THIS
        -- company before writing — a posted id cannot file a deliverable under another tenant's
        -- milestone. The FK below enforces existence; the service enforces belonging.
        MilestoneId     int            NULL,

        Name            nvarchar(200)  NOT NULL,
        NameEn          nvarchar(200)  NULL,
        Description     nvarchar(max)  NULL,
        DescriptionEn   nvarchar(max)  NULL,

        -- OPTIONAL, unlike a milestone's TargetDate: a deliverable is often dated BY the milestone it
        -- sits under, and NULL says "no date of its own" rather than inventing one that would drift.
        DueDate         date           NULL,

        -- ProjectDeliverableStatuses. NOTE WHAT IS ABSENT: there is no 'Accepted'. A search of the
        -- model and the business layer for a customer-acceptance authority returns nothing, and the
        -- nearest thing the repository has — ProgressBilling's approval — is a company approving its
        -- own certificate, not a customer accepting work. 'Delivered' is a fact this company can
        -- state; acceptance is a fact about the customer that nothing here is entitled to record.
        Status          nvarchar(20)   NOT NULL CONSTRAINT DF_ProjectDeliverables_Status DEFAULT (N'Pending'),

        SortOrder       int            NOT NULL CONSTRAINT DF_ProjectDeliverables_SortOrder DEFAULT (0),

        CompletedAt     datetime2(7)   NULL,
        CompletedBy     int            NULL,

        CreatedBy       int            NULL,
        CreatedAt       datetime2(7)   NOT NULL CONSTRAINT DF_ProjectDeliverables_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       int            NULL,
        UpdatedAt       datetime2(7)   NULL,

        CONSTRAINT PK_ProjectDeliverables PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectDeliverables_Keys')
    ALTER TABLE dbo.ProjectDeliverables WITH CHECK
        ADD CONSTRAINT CK_ProjectDeliverables_Keys
        CHECK (CompanyID > 0 AND ProjectId > 0 AND (MilestoneId IS NULL OR MilestoneId > 0));
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectDeliverables_Status')
    ALTER TABLE dbo.ProjectDeliverables WITH CHECK
        ADD CONSTRAINT CK_ProjectDeliverables_Status
        CHECK (Status IN (N'Pending', N'InProgress', N'Delivered', N'Cancelled'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProjectDeliverables_Completion')
    ALTER TABLE dbo.ProjectDeliverables WITH CHECK
        ADD CONSTRAINT CK_ProjectDeliverables_Completion
        CHECK ((Status = N'Delivered' AND CompletedAt IS NOT NULL)
            OR (Status <> N'Delivered' AND CompletedAt IS NULL));
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ProjectDeliverables_Project')
    ALTER TABLE dbo.ProjectDeliverables WITH CHECK
        ADD CONSTRAINT FK_ProjectDeliverables_Project FOREIGN KEY (ProjectId)
        REFERENCES dbo.Projects (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

-- NO CASCADE here either: the service refuses to delete a milestone that still has deliverables, so
-- a cascade would only ever fire on a path the code does not take — and if it ever did, it would
-- silently destroy the deliverables rather than surface the refusal.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ProjectDeliverables_Milestone')
    ALTER TABLE dbo.ProjectDeliverables WITH CHECK
        ADD CONSTRAINT FK_ProjectDeliverables_Milestone FOREIGN KEY (MilestoneId)
        REFERENCES dbo.ProjectMilestones (ID) ON DELETE NO ACTION ON UPDATE NO ACTION;
GO

-- The project-scoped list, as above.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectDeliverables_ProjectOrder'
               AND object_id = OBJECT_ID(N'dbo.ProjectDeliverables'))
    CREATE INDEX IX_ProjectDeliverables_ProjectOrder
        ON dbo.ProjectDeliverables (CompanyID, ProjectId, SortOrder);
GO

-- Grouping deliverables under a milestone, and answering whether one still has open deliverables.
-- Status is in the key because that second question is asked on every completion attempt.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectDeliverables_Milestone'
               AND object_id = OBJECT_ID(N'dbo.ProjectDeliverables'))
    CREATE INDEX IX_ProjectDeliverables_Milestone
        ON dbo.ProjectDeliverables (MilestoneId, Status);
GO
