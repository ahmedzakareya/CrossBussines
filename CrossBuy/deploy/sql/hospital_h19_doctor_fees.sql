-- ==========================================================================================
-- Hospital H19 — doctors' fees. A practitioner's fee rules (a percent of what was collected, or an amount per
-- unit, for a service or a category); where an approved statement posts; and the monthly statement with its
-- lines — each part of a bill line (the patient's, the payer's) counted once, ever.
--
-- FOUR NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
   OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalPayers','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h4_outpatient.sql, hospital_h6_billing.sql and hospital_h11_insurance.sql first.', 1;

IF OBJECT_ID('dbo.HospitalFeeRules','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFeeRules (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalFeeRules PRIMARY KEY,
        CompanyId       int            NOT NULL,
        PractitionerId  int            NOT NULL,
        Category        nvarchar(20)   NOT NULL,
        ServiceId       int            NULL,
        Basis           nvarchar(8)    NOT NULL,
        Value           decimal(18,3)  NOT NULL,
        CONSTRAINT CK_HospitalFeeRules_Basis CHECK (Basis IN ('Percent','PerUnit')),
        CONSTRAINT CK_HospitalFeeRules_Value CHECK (Value >= 0 AND (Basis <> 'Percent' OR Value <= 100)),
        CONSTRAINT FK_HospitalFeeRules_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID),
        CONSTRAINT FK_HospitalFeeRules_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID)
    );
    CREATE INDEX IX_HospitalFeeRules_CompanyId_PractitionerId ON dbo.HospitalFeeRules (CompanyId, PractitionerId);
END

IF OBJECT_ID('dbo.HospitalFeeSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFeeSettings (
        ID                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalFeeSettings PRIMARY KEY,
        CompanyId         int  NOT NULL,
        ExpenseAccountId  int  NULL,
        PayableAccountId  int  NULL
    );
    CREATE UNIQUE INDEX IX_HospitalFeeSettings_CompanyId ON dbo.HospitalFeeSettings (CompanyId);
END

IF OBJECT_ID('dbo.HospitalFeeStatements','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFeeStatements (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalFeeStatements PRIMARY KEY,
        CompanyId              int            NOT NULL,
        StatementNo            nvarchar(20)   NOT NULL,
        PractitionerId         int            NOT NULL,
        Period                 nvarchar(7)    NOT NULL,
        Status                 nvarchar(16)   NOT NULL CONSTRAINT DF_HospitalFeeStatements_Status DEFAULT('Generated'),
        Billed                 decimal(18,3)  NOT NULL,
        Collected              decimal(18,3)  NOT NULL,
        Share                  decimal(18,3)  NOT NULL,
        GeneratedAt            datetime2      NOT NULL,
        GeneratedByEmployeeId  int            NULL,
        DirectorApprovedAt     datetime2      NULL,
        DirectorEmployeeId     int            NULL,
        FinanceApprovedAt      datetime2      NULL,
        FinanceEmployeeId      int            NULL,
        JournalEntryId         int            NULL,
        CONSTRAINT CK_HospitalFeeStatements_Status CHECK (Status IN ('Generated','DirectorApproved','FinanceApproved')),
        -- each approval is dated, in order
        CONSTRAINT CK_HospitalFeeStatements_Path CHECK ((Status = 'Generated') OR (Status = 'DirectorApproved' AND DirectorApprovedAt IS NOT NULL) OR (Status = 'FinanceApproved' AND DirectorApprovedAt IS NOT NULL AND FinanceApprovedAt IS NOT NULL)),
        CONSTRAINT FK_HospitalFeeStatements_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalFeeStatements_CompanyId_StatementNo ON dbo.HospitalFeeStatements (CompanyId, StatementNo);
    -- one statement per practitioner and month
    CREATE UNIQUE INDEX IX_HospitalFeeStatements_CompanyId_PractitionerId_Period ON dbo.HospitalFeeStatements (CompanyId, PractitionerId, Period);
END

IF OBJECT_ID('dbo.HospitalFeeStatementLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFeeStatementLines (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalFeeStatementLines PRIMARY KEY,
        CompanyId      int            NOT NULL,
        StatementId    int            NOT NULL,
        BillLineId     int            NOT NULL,
        Part           nvarchar(8)    NOT NULL,
        PatientId      int            NOT NULL,
        PayerId        int            NULL,
        CollectedOn    date           NOT NULL,
        Description    nvarchar(300)  NOT NULL,
        DescriptionEn  nvarchar(300)  NOT NULL,
        Billed         decimal(18,3)  NOT NULL,
        Collected      decimal(18,3)  NOT NULL,
        Basis          nvarchar(8)    NOT NULL,
        Rate           decimal(18,3)  NOT NULL,
        Share          decimal(18,3)  NOT NULL,
        CONSTRAINT CK_HospitalFeeStatementLines_Part CHECK (Part IN ('Patient','Payer') AND (Part = 'Patient' OR PayerId IS NOT NULL)),
        CONSTRAINT FK_HospitalFeeStatementLines_Statement FOREIGN KEY (StatementId) REFERENCES dbo.HospitalFeeStatements(ID),
        CONSTRAINT FK_HospitalFeeStatementLines_Line FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID),
        CONSTRAINT FK_HospitalFeeStatementLines_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalFeeStatementLines_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID)
    );
    -- each part of a bill line is counted once, ever
    CREATE UNIQUE INDEX IX_HospitalFeeStatementLines_BillLineId_Part ON dbo.HospitalFeeStatementLines (BillLineId, Part);
    CREATE INDEX IX_HospitalFeeStatementLines_StatementId ON dbo.HospitalFeeStatementLines (StatementId);
END

COMMIT TRANSACTION;
