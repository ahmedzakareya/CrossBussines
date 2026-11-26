-- CRM C7 — dynamic segments and marketing journeys. Additive only: five new tables, nothing existing is altered.
--   CrmSegments / CrmSegmentMembers   a rule over contacts (their account, sales, deals and consent), refreshed by the
--                                     CRM worker; membership keeps WHEN someone entered and left
--   CrmJourneys / CrmJourneyMembers   a drawn path (message, wait, condition with yes/no, task) run for everyone who
--   CrmJourneyEvents                  ENTERS a segment; every node a member passes is one event row
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmSegments') IS NULL
CREATE TABLE dbo.CrmSegments (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSegments PRIMARY KEY,
    CompanyId int NOT NULL,
    Name nvarchar(150) NOT NULL,
    NameEn nvarchar(150) NULL,
    MatchAll bit NOT NULL CONSTRAINT DF_CrmSegments_All DEFAULT 1,
    RulesJson nvarchar(4000) NOT NULL,
    MemberCount int NOT NULL CONSTRAINT DF_CrmSegments_N DEFAULT 0,
    RefreshedAt datetime2 NULL,
    CreatedByEmployeeId int NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_CrmSegments_At DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.CrmSegmentMembers') IS NULL
CREATE TABLE dbo.CrmSegmentMembers (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSegmentMembers PRIMARY KEY,
    CompanyId int NOT NULL,
    SegmentId int NOT NULL CONSTRAINT FK_CrmSegmentMembers_Seg REFERENCES dbo.CrmSegments(ID),
    ContactId int NOT NULL,
    EnteredAt datetime2 NOT NULL,
    LeftAt datetime2 NULL,
    CONSTRAINT CK_CrmSegmentMembers_Span CHECK (LeftAt IS NULL OR LeftAt >= EnteredAt)
);
-- a person is in a segment once at a time
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmSegmentMembers_In')
    CREATE UNIQUE INDEX UX_CrmSegmentMembers_In ON dbo.CrmSegmentMembers (SegmentId, ContactId) WHERE LeftAt IS NULL;

IF OBJECT_ID('dbo.CrmJourneys') IS NULL
CREATE TABLE dbo.CrmJourneys (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmJourneys PRIMARY KEY,
    CompanyId int NOT NULL,
    Name nvarchar(150) NOT NULL,
    NameEn nvarchar(150) NULL,
    SegmentId int NOT NULL CONSTRAINT FK_CrmJourneys_Seg REFERENCES dbo.CrmSegments(ID),
    Status varchar(10) NOT NULL CONSTRAINT CK_CrmJourneys_Status CHECK (Status IN ('Draft', 'Active', 'Paused')),
    IncludeExisting bit NOT NULL CONSTRAINT DF_CrmJourneys_Incl DEFAULT 0,
    ExitOnLeave bit NOT NULL CONSTRAINT DF_CrmJourneys_Leave DEFAULT 1,
    NodesJson nvarchar(max) NOT NULL,
    PublicOrigin varchar(200) NULL,    -- scheme://host for the unsubscribe link the worker writes into e-mails
    ActivatedAt datetime2 NULL,
    CreatedByEmployeeId int NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_CrmJourneys_At DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.CrmJourneyMembers') IS NULL
CREATE TABLE dbo.CrmJourneyMembers (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmJourneyMembers PRIMARY KEY,
    CompanyId int NOT NULL,
    JourneyId int NOT NULL CONSTRAINT FK_CrmJourneyMembers_J REFERENCES dbo.CrmJourneys(ID),
    ContactId int NOT NULL,
    AccountId int NULL,
    EnteredAt datetime2 NOT NULL,
    Status varchar(10) NOT NULL CONSTRAINT CK_CrmJourneyMembers_Status CHECK (Status IN ('Active', 'Finished', 'Exited')),
    ExitReason varchar(12) NULL CONSTRAINT CK_CrmJourneyMembers_Reason CHECK (ExitReason IS NULL OR ExitReason IN ('OptOut', 'LeftSegment', 'Removed', 'Manual')),
    NodeId varchar(12) NULL,           -- where they are now
    NextAt datetime2 NULL,             -- UTC; set while they wait
    FinishedAt datetime2 NULL,
    CONSTRAINT CK_CrmJourneyMembers_State CHECK ((Status = 'Exited' AND ExitReason IS NOT NULL) OR (Status <> 'Exited' AND ExitReason IS NULL)),
    -- a person walks a journey once
    CONSTRAINT UQ_CrmJourneyMembers UNIQUE (JourneyId, ContactId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmJourneyMembers_Due')
    CREATE INDEX IX_CrmJourneyMembers_Due ON dbo.CrmJourneyMembers (CompanyId, Status, NextAt);

IF OBJECT_ID('dbo.CrmJourneyEvents') IS NULL
CREATE TABLE dbo.CrmJourneyEvents (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmJourneyEvents PRIMARY KEY,
    CompanyId int NOT NULL,
    MemberId int NOT NULL CONSTRAINT FK_CrmJourneyEvents_M REFERENCES dbo.CrmJourneyMembers(ID),
    NodeId varchar(12) NULL,
    At datetime2 NOT NULL CONSTRAINT DF_CrmJourneyEvents_At DEFAULT SYSUTCDATETIME(),
    Outcome varchar(10) NOT NULL CONSTRAINT CK_CrmJourneyEvents_Outcome CHECK (Outcome IN ('Entered', 'Queued', 'Sent', 'Task', 'Skipped', 'Failed', 'Waiting', 'Yes', 'No', 'Finished', 'Exited')),
    Detail nvarchar(300) NULL,
    RefId int NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmJourneyEvents_M')
    CREATE INDEX IX_CrmJourneyEvents_M ON dbo.CrmJourneyEvents (MemberId, ID);
-- a node runs once per member even if two workers race
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmJourneyEvents_Node')
    CREATE UNIQUE INDEX UX_CrmJourneyEvents_Node ON dbo.CrmJourneyEvents (MemberId, NodeId) WHERE NodeId IS NOT NULL;

COMMIT TRANSACTION;
