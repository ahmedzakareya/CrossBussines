-- ==========================================================================================
-- Hospital H240 - the denial work-queue: one work item per rejected claim line, with its root cause, owner,
-- appeal deadline and status. Nothing here holds money; the claim line and the ledger keep it.
--
--   HospitalDenialWork   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalClaimLines','U') IS NULL
    THROW 52040, 'Run the hospital insurance slice first.', 1;

IF OBJECT_ID('dbo.HospitalDenialWork','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDenialWork (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ClaimLineId int NOT NULL,
        PatientId int NOT NULL,
        RootCause nvarchar(15) NOT NULL,
        OwnerEmployeeId int NULL,
        DueDate date NOT NULL,
        Status nvarchar(12) NOT NULL,
        Note nvarchar(500) NULL,
        CreatedAt datetime2 NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDenialWork PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDenialWork_Line FOREIGN KEY (ClaimLineId) REFERENCES dbo.HospitalClaimLines (ID),
        CONSTRAINT FK_HospitalDenialWork_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalDenialWork_RootCause CHECK (RootCause IN ('Authorization','Eligibility','Coding','Necessity','Documentation','Duplicate','Timely','Pricing','Other')),
        CONSTRAINT CK_HospitalDenialWork_Status CHECK (Status IN ('Open','Working','Resubmitted','NotPursued'))
    );
    CREATE UNIQUE INDEX IX_HospitalDenialWork_CompanyId_ClaimLineId ON dbo.HospitalDenialWork (CompanyId, ClaimLineId);
    CREATE INDEX IX_HospitalDenialWork_CompanyId_Status_DueDate ON dbo.HospitalDenialWork (CompanyId, Status, DueDate);
    CREATE INDEX IX_HospitalDenialWork_CompanyId_PatientId ON dbo.HospitalDenialWork (CompanyId, PatientId);
END

COMMIT TRANSACTION;
