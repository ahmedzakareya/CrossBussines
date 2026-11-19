-- ==========================================================================================
-- Hospital H215 - machine interfaces: an analyser (HL7 results in, acknowledgement out) or a worklist broker (the
-- scheduled imaging orders out), each with its own key (stored as a hash) and the employee it acts for.
--
--   HospitalInterfaces   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52015, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalInterfaces','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInterfaces (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(30) NOT NULL,
        Name nvarchar(100) NOT NULL,
        Kind nvarchar(20) NOT NULL,
        KeyHash nvarchar(64) NOT NULL,
        KeyHint nvarchar(8) NOT NULL,
        ActsAsEmployeeId int NOT NULL,
        IsActive bit NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        LastUsedAt datetime2 NULL,
        Uses int NOT NULL,
        CONSTRAINT PK_HospitalInterfaces PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalInterfaces_Kind CHECK (Kind IN ('LabResults', 'ImagingWorklist'))
    );
    CREATE UNIQUE INDEX IX_HospitalInterfaces_CompanyId_Code ON dbo.HospitalInterfaces (CompanyId, Code);
    CREATE UNIQUE INDEX IX_HospitalInterfaces_KeyHash ON dbo.HospitalInterfaces (KeyHash);
END

COMMIT TRANSACTION;
