-- CRM C6 — follow-up sequences and meeting booking. Additive only: six new tables, nothing existing is altered.
--   CrmSequences / CrmSequenceSteps   a named run of steps (e-mail, WhatsApp template, call task, task) on day offsets
--   CrmSequenceEnrollments            one person (lead or contact) walking one sequence; stops itself on a reply, a
--                                     booking, a conversion or a withdrawn consent
--   CrmSequenceEvents                 what each step actually did (queued, task created, skipped and why, failed)
--   CrmBookingLinks / CrmBookings     a rep's public booking page and the meetings booked through it
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmSequences') IS NULL
CREATE TABLE dbo.CrmSequences (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSequences PRIMARY KEY,
    CompanyId int NOT NULL,
    Name nvarchar(150) NOT NULL,
    NameEn nvarchar(150) NULL,
    Target varchar(10) NOT NULL CONSTRAINT CK_CrmSequences_Target CHECK (Target IN ('Lead', 'Contact')),
    IsActive bit NOT NULL CONSTRAINT DF_CrmSequences_Active DEFAULT 1,
    SkipNonWorkDays bit NOT NULL CONSTRAINT DF_CrmSequences_Skip DEFAULT 1,
    CreatedByEmployeeId int NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_CrmSequences_At DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.CrmSequenceSteps') IS NULL
CREATE TABLE dbo.CrmSequenceSteps (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSequenceSteps PRIMARY KEY,
    CompanyId int NOT NULL,
    SequenceId int NOT NULL CONSTRAINT FK_CrmSequenceSteps_Seq REFERENCES dbo.CrmSequences(ID),
    StepNo int NOT NULL,
    DayOffset int NOT NULL CONSTRAINT CK_CrmSequenceSteps_Day CHECK (DayOffset BETWEEN 0 AND 365),
    Kind varchar(10) NOT NULL CONSTRAINT CK_CrmSequenceSteps_Kind CHECK (Kind IN ('Email', 'WhatsApp', 'Call', 'Task')),
    Subject nvarchar(200) NULL,
    Body nvarchar(2000) NULL,
    Template varchar(100) NULL,
    CONSTRAINT UQ_CrmSequenceSteps UNIQUE (SequenceId, StepNo),
    -- a WhatsApp step outside the 24-hour window can only be an approved template
    CONSTRAINT CK_CrmSequenceSteps_Template CHECK (Kind <> 'WhatsApp' OR Template IS NOT NULL)
);

IF OBJECT_ID('dbo.CrmSequenceEnrollments') IS NULL
CREATE TABLE dbo.CrmSequenceEnrollments (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSequenceEnrollments PRIMARY KEY,
    CompanyId int NOT NULL,
    SequenceId int NOT NULL CONSTRAINT FK_CrmSequenceEnrollments_Seq REFERENCES dbo.CrmSequences(ID),
    SubjectType varchar(10) NOT NULL CONSTRAINT CK_CrmSequenceEnrollments_Type CHECK (SubjectType IN ('Lead', 'Contact')),
    SubjectId int NOT NULL,
    OwnerEmployeeId int NULL,
    EnrolledAt datetime2 NOT NULL,
    EnrolledByEmployeeId int NULL,
    Status varchar(10) NOT NULL CONSTRAINT CK_CrmSequenceEnrollments_Status CHECK (Status IN ('Active', 'Completed', 'Stopped')),
    StopReason varchar(12) NULL CONSTRAINT CK_CrmSequenceEnrollments_Reason CHECK (StopReason IS NULL OR StopReason IN ('Replied', 'Booked', 'Converted', 'OptOut', 'Manual', 'Inactive')),
    StoppedAt datetime2 NULL,
    NextStepNo int NULL,
    NextDueAt datetime2 NULL,          -- UTC
    -- a stopped enrollment says why; a running one has a next step
    CONSTRAINT CK_CrmSequenceEnrollments_State CHECK ((Status = 'Stopped' AND StopReason IS NOT NULL) OR (Status <> 'Stopped' AND StopReason IS NULL))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmSequenceEnrollments_Due')
    CREATE INDEX IX_CrmSequenceEnrollments_Due ON dbo.CrmSequenceEnrollments (CompanyId, Status, NextDueAt);
-- one person walks one sequence once at a time
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmSequenceEnrollments_Active')
    CREATE UNIQUE INDEX UX_CrmSequenceEnrollments_Active ON dbo.CrmSequenceEnrollments (SequenceId, SubjectType, SubjectId) WHERE Status = 'Active';

IF OBJECT_ID('dbo.CrmSequenceEvents') IS NULL
CREATE TABLE dbo.CrmSequenceEvents (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmSequenceEvents PRIMARY KEY,
    CompanyId int NOT NULL,
    EnrollmentId int NOT NULL CONSTRAINT FK_CrmSequenceEvents_Enr REFERENCES dbo.CrmSequenceEnrollments(ID),
    StepNo int NULL,
    At datetime2 NOT NULL CONSTRAINT DF_CrmSequenceEvents_At DEFAULT SYSUTCDATETIME(),
    Outcome varchar(10) NOT NULL CONSTRAINT CK_CrmSequenceEvents_Outcome CHECK (Outcome IN ('Queued', 'Sent', 'Task', 'Skipped', 'Failed', 'Stopped', 'Enrolled', 'Completed')),
    Detail nvarchar(300) NULL,
    RefId int NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmSequenceEvents_Enr')
    CREATE INDEX IX_CrmSequenceEvents_Enr ON dbo.CrmSequenceEvents (EnrollmentId, ID);
-- a step runs once per enrollment even if two workers race
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmSequenceEvents_Step')
    CREATE UNIQUE INDEX UX_CrmSequenceEvents_Step ON dbo.CrmSequenceEvents (EnrollmentId, StepNo) WHERE StepNo IS NOT NULL;

IF OBJECT_ID('dbo.CrmBookingLinks') IS NULL
CREATE TABLE dbo.CrmBookingLinks (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmBookingLinks PRIMARY KEY,
    CompanyId int NOT NULL,
    EmployeeId int NOT NULL,
    PublicKey varchar(32) NOT NULL CONSTRAINT UQ_CrmBookingLinks_Key UNIQUE,
    CalendarToken varchar(48) NOT NULL CONSTRAINT UQ_CrmBookingLinks_Cal UNIQUE,
    PublicOrigin varchar(200) NULL,    -- scheme://host the link was saved from; messages written by the worker use it
    Title nvarchar(150) NULL,
    DurationMinutes int NOT NULL CONSTRAINT CK_CrmBookingLinks_Dur CHECK (DurationMinutes BETWEEN 15 AND 240),
    BufferMinutes int NOT NULL CONSTRAINT CK_CrmBookingLinks_Buf CHECK (BufferMinutes BETWEEN 0 AND 120),
    DaysAhead int NOT NULL CONSTRAINT CK_CrmBookingLinks_Days CHECK (DaysAhead BETWEEN 1 AND 60),
    MinNoticeHours int NOT NULL CONSTRAINT CK_CrmBookingLinks_Notice CHECK (MinNoticeHours BETWEEN 0 AND 168),
    IsActive bit NOT NULL,
    CONSTRAINT UQ_CrmBookingLinks_Emp UNIQUE (CompanyId, EmployeeId)
);

IF OBJECT_ID('dbo.CrmBookings') IS NULL
CREATE TABLE dbo.CrmBookings (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmBookings PRIMARY KEY,
    CompanyId int NOT NULL,
    LinkId int NOT NULL CONSTRAINT FK_CrmBookings_Link REFERENCES dbo.CrmBookingLinks(ID),
    EmployeeId int NOT NULL,
    StartsAt datetime2 NOT NULL,       -- UTC
    EndsAt datetime2 NOT NULL,
    Name nvarchar(150) NOT NULL,
    Email nvarchar(150) NULL,
    Phone nvarchar(50) NULL,
    Note nvarchar(1000) NULL,
    Status varchar(10) NOT NULL CONSTRAINT CK_CrmBookings_Status CHECK (Status IN ('Booked', 'Cancelled')),
    ActivityId int NULL,
    LeadId int NULL,
    ContactId int NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_CrmBookings_At DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_CrmBookings_Span CHECK (EndsAt > StartsAt),
    CONSTRAINT CK_CrmBookings_Reach CHECK (Email IS NOT NULL OR Phone IS NOT NULL)
);
-- two visitors cannot take the same minute of the same person
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmBookings_Slot')
    CREATE UNIQUE INDEX UX_CrmBookings_Slot ON dbo.CrmBookings (EmployeeId, StartsAt) WHERE Status = 'Booked';

COMMIT TRANSACTION;
