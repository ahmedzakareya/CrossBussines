-- ==========================================================================================
-- Hospital H162 - insurance for this visit: each verification of the cover for an appointment (insured under a policy,
-- or self-pay), how it was verified and the insurer's reference, and the estimate given to the patient.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 51991, 'Run the hospital outpatient slices first.', 1;

IF OBJECT_ID('dbo.HospitalVisitCoverages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalVisitCoverages (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AppointmentId int NOT NULL,
        PatientId int NOT NULL,
        Decision nvarchar(10) NOT NULL,
        PolicyId int NULL,
        PayerId int NULL,
        Source nvarchar(10) NOT NULL,
        Reference nvarchar(60) NULL,
        VerifiedAt datetime2 NOT NULL,
        VerifiedByEmployeeId int NULL,
        EstimatedTotal decimal(18,3) NOT NULL CONSTRAINT DF_HospitalVisitCoverages_Total DEFAULT (0),
        EstimatedShare decimal(18,3) NOT NULL CONSTRAINT DF_HospitalVisitCoverages_Share DEFAULT (0),
        Note nvarchar(300) NULL,
        CONSTRAINT PK_HospitalVisitCoverages PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalVisitCoverages_Decision CHECK (Decision IN ('Insured','SelfPay')),
        CONSTRAINT CK_HospitalVisitCoverages_Source CHECK (Source IN ('Card','Portal','Phone','Letter','Patient'))
    );
    CREATE INDEX IX_HospitalVisitCoverages_CompanyId_AppointmentId ON dbo.HospitalVisitCoverages (CompanyId, AppointmentId);
    CREATE INDEX IX_HospitalVisitCoverages_CompanyId_PatientId ON dbo.HospitalVisitCoverages (CompanyId, PatientId);
END

COMMIT TRANSACTION;
