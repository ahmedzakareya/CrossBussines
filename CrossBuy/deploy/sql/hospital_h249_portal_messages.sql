-- ==========================================================================================
-- Hospital H249 - secure messaging on the patient portal: threads (a portal account writing about one of its people)
-- and their messages, with read times on both sides.
--
--   HospitalPortalThreads    (new)
--   HospitalPortalMessages   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalAccounts','U') IS NULL
    THROW 52049, 'Run the hospital portal slices first.', 1;

IF OBJECT_ID('dbo.HospitalPortalThreads','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalThreads (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AccountId int NOT NULL,
        PatientId int NOT NULL,
        Topic nvarchar(12) NOT NULL,
        Subject nvarchar(150) NOT NULL,
        Status nvarchar(8) NOT NULL,
        AssignedEmployeeId int NULL,
        AwaitingClinic bit NOT NULL,
        CreatedAt datetime2 NOT NULL,
        LastMessageAt datetime2 NOT NULL,
        ClosedAt datetime2 NULL,
        CONSTRAINT PK_HospitalPortalThreads PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPortalThreads_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts (ID),
        CONSTRAINT FK_HospitalPortalThreads_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalPortalThreads_Topic CHECK (Topic IN ('Clinical','Appointment','Billing','Other')),
        CONSTRAINT CK_HospitalPortalThreads_Status CHECK (Status IN ('Open','Closed'))
    );
    CREATE INDEX IX_HospitalPortalThreads_CompanyId_AccountId_LastMessageAt ON dbo.HospitalPortalThreads (CompanyId, AccountId, LastMessageAt);
    CREATE INDEX IX_HospitalPortalThreads_CompanyId_Status_AwaitingClinic ON dbo.HospitalPortalThreads (CompanyId, Status, AwaitingClinic);
    CREATE INDEX IX_HospitalPortalThreads_CompanyId_PatientId ON dbo.HospitalPortalThreads (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalPortalMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalMessages (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ThreadId int NOT NULL,
        FromPatient bit NOT NULL,
        EmployeeId int NULL,
        Body nvarchar(2000) NOT NULL,
        At datetime2 NOT NULL,
        ReadAt datetime2 NULL,
        CONSTRAINT PK_HospitalPortalMessages PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPortalMessages_Thread FOREIGN KEY (ThreadId) REFERENCES dbo.HospitalPortalThreads (ID)
    );
    CREATE INDEX IX_HospitalPortalMessages_CompanyId_ThreadId_At ON dbo.HospitalPortalMessages (CompanyId, ThreadId, At);
END

COMMIT TRANSACTION;
