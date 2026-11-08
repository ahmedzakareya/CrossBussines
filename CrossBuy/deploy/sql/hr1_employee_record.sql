-- ==========================================================================================
-- HR1 — EMPLOYEE RECORD 2.0 (prototype roadmap HR1, 2026-11-08): identity, emergency contact, bank, dependants, and an
-- effective-dated change history.
--
--   Employee (+7 nullable columns)  CivilIdNumber, PassportNumber, Religion, Iban, BankName,
--                                   EmergencyContactName, EmergencyContactPhone   (EmergencyContactRelation too)
--   EmployeeDependants              one row per dependant (spouse, child, parent), company-scoped
--   EmployeeHistory                 one row per change of a tracked field, with the date it takes effect; never updated
--
-- Additive only: nullable columns and two new tables; nothing existing is changed or removed.
-- Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.Employee','U') IS NULL
    THROW 51993, 'The Employee table is missing.', 1;

IF COL_LENGTH('dbo.Employee','CivilIdNumber')            IS NULL ALTER TABLE dbo.Employee ADD CivilIdNumber            nvarchar(30)  NULL;
IF COL_LENGTH('dbo.Employee','PassportNumber')           IS NULL ALTER TABLE dbo.Employee ADD PassportNumber           nvarchar(30)  NULL;
IF COL_LENGTH('dbo.Employee','Religion')                 IS NULL ALTER TABLE dbo.Employee ADD Religion                 nvarchar(20)  NULL;   -- Muslim | Christian | Other (drives Hajj / iddah)
IF COL_LENGTH('dbo.Employee','Iban')                     IS NULL ALTER TABLE dbo.Employee ADD Iban                     nvarchar(34)  NULL;
IF COL_LENGTH('dbo.Employee','BankName')                 IS NULL ALTER TABLE dbo.Employee ADD BankName                 nvarchar(100) NULL;
IF COL_LENGTH('dbo.Employee','EmergencyContactName')     IS NULL ALTER TABLE dbo.Employee ADD EmergencyContactName     nvarchar(150) NULL;
IF COL_LENGTH('dbo.Employee','EmergencyContactPhone')    IS NULL ALTER TABLE dbo.Employee ADD EmergencyContactPhone    nvarchar(30)  NULL;
IF COL_LENGTH('dbo.Employee','EmergencyContactRelation') IS NULL ALTER TABLE dbo.Employee ADD EmergencyContactRelation nvarchar(50)  NULL;

IF OBJECT_ID('dbo.EmployeeDependants','U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeDependants (
        ID             int IDENTITY(1,1) NOT NULL,
        CompanyID      int            NOT NULL,
        EmployeeID     int            NOT NULL,
        FullName       nvarchar(150)  NOT NULL,
        FullNameEn     nvarchar(150)  NULL,
        Relation       nvarchar(20)   NOT NULL,       -- Spouse | Son | Daughter | Father | Mother | Other
        DateOfBirth    date           NULL,
        CivilIdNumber  nvarchar(30)   NULL,
        IsSponsored    bit            NOT NULL CONSTRAINT DF_EmployeeDependants_Sponsored DEFAULT (0),
        CreatedAt      datetime2      NOT NULL CONSTRAINT DF_EmployeeDependants_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_EmployeeDependants PRIMARY KEY (ID),
        CONSTRAINT FK_EmployeeDependants_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employee(ID)
    );
    CREATE INDEX IX_EmployeeDependants_Employee ON dbo.EmployeeDependants (CompanyID, EmployeeID);
END;

IF OBJECT_ID('dbo.EmployeeHistory','U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeHistory (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyID           int            NOT NULL,
        EmployeeID          int            NOT NULL,
        Field               nvarchar(40)   NOT NULL,      -- JobTitle | Branch | Department | EmploymentType | Status | Hire
        OldValue            nvarchar(200)  NULL,
        NewValue            nvarchar(200)  NULL,
        EffectiveFrom       date           NOT NULL,
        ChangedByEmployeeId int            NULL,
        ChangedAt           datetime2      NOT NULL CONSTRAINT DF_EmployeeHistory_ChangedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_EmployeeHistory PRIMARY KEY (ID),
        CONSTRAINT FK_EmployeeHistory_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employee(ID)
    );
    CREATE INDEX IX_EmployeeHistory_Employee ON dbo.EmployeeHistory (CompanyID, EmployeeID, EffectiveFrom);
END;

COMMIT TRANSACTION;
