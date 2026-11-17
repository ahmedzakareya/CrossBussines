-- ==========================================================================================
-- Hospital H153 - compounding: the batch record of a sterile preparation (chemotherapy, IV admixture, TPN) for one
-- patient, with its beyond-use time, its storage, who made it and the independent pharmacist who checked and released it.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenseLines','U') IS NULL
    THROW 51994, 'Run the pharmacy slices first.', 1;

IF OBJECT_ID('dbo.HospitalCompoundings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCompoundings (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CompoundNo nvarchar(20) NOT NULL,
        Kind nvarchar(10) NOT NULL,
        PatientId int NOT NULL,
        DispenseId int NOT NULL,
        LineId int NOT NULL,
        Product nvarchar(200) NOT NULL,
        DoseMg decimal(18,4) NULL,
        Route nvarchar(10) NULL,
        Diluent nvarchar(100) NULL,
        FinalVolumeMl decimal(18,2) NULL,
        DrugBatch nvarchar(40) NULL,
        DiluentBatch nvarchar(40) NULL,
        Storage nvarchar(10) NOT NULL,
        BeyondUseAt datetime2 NOT NULL,
        Notes nvarchar(300) NULL,
        Status nvarchar(10) NOT NULL,
        RejectReason nvarchar(300) NULL,
        PreparedAt datetime2 NOT NULL,
        PreparedByEmployeeId int NULL,
        CheckedAt datetime2 NULL,
        CheckedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCompoundings PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalCompoundings_Status CHECK (Status IN ('Prepared','Released','Rejected')),
        CONSTRAINT CK_HospitalCompoundings_Kind CHECK (Kind IN ('Chemo','IV','TPN')),
        CONSTRAINT CK_HospitalCompoundings_Storage CHECK (Storage IN ('Room','Fridge'))
    );
    CREATE UNIQUE INDEX IX_HospitalCompoundings_CompanyId_CompoundNo ON dbo.HospitalCompoundings (CompanyId, CompoundNo);
    CREATE INDEX IX_HospitalCompoundings_CompanyId_DispenseId ON dbo.HospitalCompoundings (CompanyId, DispenseId);
    CREATE INDEX IX_HospitalCompoundings_CompanyId_Status ON dbo.HospitalCompoundings (CompanyId, Status);
END

COMMIT TRANSACTION;
