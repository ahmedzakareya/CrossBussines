-- ==========================================================================================
-- Hospital H235 - antimicrobial stewardship: the approval of a restricted antimicrobial and the 48-72 h time-out of
-- every running one, each a review row (continue / narrow / IV to PO / stop, approved / rejected) with its reason.
--
--   HospitalAbxReviews   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52035, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalAbxReviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAbxReviews (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        MedOrderId int NOT NULL,
        PatientId int NOT NULL,
        AdmissionId int NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Decision nvarchar(10) NOT NULL,
        Note nvarchar(500) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalAbxReviews PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAbxReviews_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalAbxReviews_Kind CHECK (Kind IN ('Approval','TimeOut')),
        CONSTRAINT CK_HospitalAbxReviews_Decision CHECK (Decision IN ('Approved','Rejected','Continue','Narrow','IvToPo','Stop'))
    );
    CREATE INDEX IX_HospitalAbxReviews_CompanyId_MedOrderId ON dbo.HospitalAbxReviews (CompanyId, MedOrderId);
    CREATE INDEX IX_HospitalAbxReviews_CompanyId_PatientId ON dbo.HospitalAbxReviews (CompanyId, PatientId);
END

COMMIT TRANSACTION;
