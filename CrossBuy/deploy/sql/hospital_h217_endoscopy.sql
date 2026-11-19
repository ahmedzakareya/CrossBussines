-- ==========================================================================================
-- Hospital H217 - endoscopy: the scopes, their reprocessing cycles (a scope is used only after a passed cycle), and
-- the procedures with their findings, quality figures and biopsies (which open a pathology case).
--
--   HospitalScopes         (new)
--   HospitalScopeCycles    (new)
--   HospitalEndoscopies    (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52017, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalScopes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalScopes (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Serial nvarchar(40) NOT NULL,
        Kind nvarchar(15) NOT NULL,
        Model nvarchar(100) NULL,
        IsActive bit NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalScopes PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalScopes_Kind CHECK (Kind IN ('Gastroscopy', 'Colonoscopy', 'Bronchoscopy', 'ERCP'))
    );
    CREATE UNIQUE INDEX IX_HospitalScopes_CompanyId_Serial ON dbo.HospitalScopes (CompanyId, Serial);
END

IF OBJECT_ID('dbo.HospitalScopeCycles','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalScopeCycles (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ScopeId int NOT NULL,
        LeakTestPassed bit NOT NULL,
        ManualClean bit NOT NULL,
        Agent nvarchar(60) NOT NULL,
        Minutes int NOT NULL,
        Passed bit NOT NULL,
        Note nvarchar(300) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalScopeCycles PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalScopeCycles_Scope FOREIGN KEY (ScopeId) REFERENCES dbo.HospitalScopes (ID),
        -- a passed cycle had its leak test, its manual clean and a disinfection time
        CONSTRAINT CK_HospitalScopeCycles_Passed CHECK (Passed = 0 OR (LeakTestPassed = 1 AND ManualClean = 1 AND Minutes > 0))
    );
    CREATE INDEX IX_HospitalScopeCycles_CompanyId_ScopeId_At ON dbo.HospitalScopeCycles (CompanyId, ScopeId, At);
END

IF OBJECT_ID('dbo.HospitalEndoscopies','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEndoscopies (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ProcNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        Kind nvarchar(15) NOT NULL,
        ScopeId int NOT NULL,
        CycleId int NOT NULL,
        Indication nvarchar(500) NOT NULL,
        Sedation nvarchar(10) NOT NULL,
        Status nvarchar(10) NOT NULL,
        StartedAt datetime2 NOT NULL,
        EndoscopistEmployeeId int NULL,
        Findings nvarchar(4000) NULL,
        Interventions nvarchar(1000) NULL,
        Complications nvarchar(500) NULL,
        Recommendations nvarchar(1000) NULL,
        CecumReached bit NULL,
        WithdrawalMinutes int NULL,
        BowelPrepScore int NULL,
        Biopsies int NOT NULL,
        PathCaseId int NULL,
        CompletedAt datetime2 NULL,
        CONSTRAINT PK_HospitalEndoscopies PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalEndoscopies_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalEndoscopies_Scope FOREIGN KEY (ScopeId) REFERENCES dbo.HospitalScopes (ID),
        CONSTRAINT FK_HospitalEndoscopies_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalScopeCycles (ID),
        CONSTRAINT CK_HospitalEndoscopies_Kind CHECK (Kind IN ('Gastroscopy', 'Colonoscopy', 'Bronchoscopy', 'ERCP')),
        CONSTRAINT CK_HospitalEndoscopies_Sedation CHECK (Sedation IN ('None', 'Moderate', 'Deep')),
        CONSTRAINT CK_HospitalEndoscopies_Status CHECK (Status IN ('Started', 'Completed')),
        CONSTRAINT CK_HospitalEndoscopies_Completed CHECK (Status <> 'Completed' OR (Findings IS NOT NULL AND CompletedAt IS NOT NULL)),
        CONSTRAINT CK_HospitalEndoscopies_Prep CHECK (BowelPrepScore IS NULL OR BowelPrepScore BETWEEN 0 AND 9)
    );
    CREATE UNIQUE INDEX IX_HospitalEndoscopies_CompanyId_ProcNo ON dbo.HospitalEndoscopies (CompanyId, ProcNo);
    CREATE INDEX IX_HospitalEndoscopies_CompanyId_ScopeId_StartedAt ON dbo.HospitalEndoscopies (CompanyId, ScopeId, StartedAt);
    CREATE INDEX IX_HospitalEndoscopies_CompanyId_PatientId ON dbo.HospitalEndoscopies (CompanyId, PatientId);
END

COMMIT TRANSACTION;
