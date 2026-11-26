-- ==========================================================================================
-- CRM C3 — the deal workspace: stage requirements, the buying committee, discount limits and approvals.
--   CrmStageRequirements     what a deal must have before it may ENTER a pipeline stage
--   CrmOpportunityContacts   the people on a deal and the part each plays (decision maker, approver, ...)
--   CrmDiscountLimits        the largest line discount each CRM role may give without asking
--   CrmDiscountApprovals     a request to go above that limit, decided by someone else, bound to the exact lines
--
-- Additive only (four new tables, no change to an existing table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.Opportunities','U') IS NULL OR OBJECT_ID('dbo.CrmPipelineStages','U') IS NULL OR OBJECT_ID('dbo.CrmContacts','U') IS NULL
    THROW 51964, 'The CRM base tables (Opportunities, CrmPipelineStages, CrmContacts) must exist first.', 1;

IF OBJECT_ID('dbo.CrmStageRequirements','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmStageRequirements (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        StageId                int            NOT NULL,
        Requirement            nvarchar(24)   NOT NULL,
        FieldId                int            NULL,      -- the custom field, for Requirement = CustomField
        CONSTRAINT PK_CrmStageRequirements PRIMARY KEY (ID),
        CONSTRAINT CK_CrmStageRequirements_Kind CHECK (Requirement IN (N'Amount', N'CloseDate', N'Products', N'DecisionMaker', N'Approver', N'NextStep', N'Discount', N'Quotation', N'CustomField')),
        -- a field id exactly when the requirement is a custom field
        CONSTRAINT CK_CrmStageRequirements_Field CHECK ((Requirement = N'CustomField' AND FieldId IS NOT NULL) OR (Requirement <> N'CustomField' AND FieldId IS NULL))
    );
    CREATE UNIQUE INDEX UX_CrmStageRequirements ON dbo.CrmStageRequirements (CompanyId, StageId, Requirement, FieldId);
END;

IF OBJECT_ID('dbo.CrmOpportunityContacts','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmOpportunityContacts (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        OpportunityId          int            NOT NULL,
        ContactId              int            NOT NULL,
        Role                   nvarchar(16)   NOT NULL,   -- DecisionMaker | Approver | Influencer | Champion | User
        AddedAt                datetime2      NOT NULL,
        AddedByEmployeeId      int            NULL,
        CONSTRAINT PK_CrmOpportunityContacts PRIMARY KEY (ID),
        CONSTRAINT CK_CrmOpportunityContacts_Role CHECK (Role IN (N'DecisionMaker', N'Approver', N'Influencer', N'Champion', N'User'))
    );
    CREATE UNIQUE INDEX UX_CrmOpportunityContacts ON dbo.CrmOpportunityContacts (CompanyId, OpportunityId, ContactId);
END;

IF OBJECT_ID('dbo.CrmDiscountLimits','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmDiscountLimits (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        Role                   nvarchar(16)   NOT NULL,   -- SalesRep | SalesManager | Marketing | CrmViewer
        MaxPercent             decimal(5,2)   NOT NULL,
        CONSTRAINT PK_CrmDiscountLimits PRIMARY KEY (ID),
        CONSTRAINT CK_CrmDiscountLimits_Range CHECK (MaxPercent >= 0 AND MaxPercent <= 100)
    );
    CREATE UNIQUE INDEX UX_CrmDiscountLimits ON dbo.CrmDiscountLimits (CompanyId, Role);
END;

IF OBJECT_ID('dbo.CrmDiscountApprovals','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmDiscountApprovals (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        OpportunityId          int            NOT NULL,
        RequestedPercent       decimal(5,2)   NOT NULL,
        LinesFingerprint       nvarchar(64)   NOT NULL,   -- the approval covers THESE lines; any edit needs a new one
        Amount                 decimal(18,4)  NOT NULL,
        Reason                 nvarchar(500)  NOT NULL,
        RequestedByEmployeeId  int            NOT NULL,
        RequestedAt            datetime2      NOT NULL,
        Status                 nvarchar(10)   NOT NULL,   -- Pending | Approved | Rejected | Withdrawn
        DecidedByEmployeeId    int            NULL,
        DecidedAt              datetime2      NULL,
        DecisionNote           nvarchar(500)  NULL,
        CONSTRAINT PK_CrmDiscountApprovals PRIMARY KEY (ID),
        CONSTRAINT CK_CrmDiscountApprovals_Status CHECK (Status IN (N'Pending', N'Approved', N'Rejected', N'Withdrawn')),
        CONSTRAINT CK_CrmDiscountApprovals_Range CHECK (RequestedPercent > 0 AND RequestedPercent <= 100),
        -- nobody approves their own discount
        CONSTRAINT CK_CrmDiscountApprovals_NotSelf CHECK (DecidedByEmployeeId IS NULL OR DecidedByEmployeeId <> RequestedByEmployeeId),
        CONSTRAINT CK_CrmDiscountApprovals_Decided CHECK ((Status IN (N'Pending', N'Withdrawn')) OR (DecidedByEmployeeId IS NOT NULL AND DecidedAt IS NOT NULL)),
        CONSTRAINT CK_CrmDiscountApprovals_RejectNote CHECK (Status <> N'Rejected' OR DecisionNote IS NOT NULL)
    );
    CREATE INDEX IX_CrmDiscountApprovals_Opp ON dbo.CrmDiscountApprovals (CompanyId, OpportunityId, Status);
    -- one open request per deal at a time
    CREATE UNIQUE INDEX UX_CrmDiscountApprovals_OnePending ON dbo.CrmDiscountApprovals (CompanyId, OpportunityId) WHERE Status = N'Pending';
END;

IF (SELECT COUNT(*) FROM (VALUES (N'CrmStageRequirements'), (N'CrmOpportunityContacts'), (N'CrmDiscountLimits'), (N'CrmDiscountApprovals')) t(n) WHERE OBJECT_ID(N'dbo.' + t.n, 'U') IS NOT NULL) <> 4
    THROW 51965, 'crm_c3_deal: verification failed.', 1;

COMMIT TRANSACTION;
