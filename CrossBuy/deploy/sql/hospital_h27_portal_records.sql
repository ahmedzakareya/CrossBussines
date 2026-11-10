-- ==========================================================================================
-- Hospital H27 — what the patient portal shows of the record: the doctor's RELEASE of a result to the portal
-- (a verified lab order or a signed imaging report shows its values only once released), and the patient's
-- REFILL requests for a prescribed medicine. Bills need no table (H6 bills, H18 split).
--
-- TWO NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalAccounts','U') IS NULL OR OBJECT_ID('dbo.HospitalPrescriptionLines','U') IS NULL
    THROW 51990, 'Run hospital_h5_consultation.sql and hospital_h24_portal_accounts.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPortalReleases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalReleases (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalReleases PRIMARY KEY,
        CompanyId             int            NOT NULL,
        Source                nvarchar(20)   NOT NULL,
        OrderId               int            NOT NULL,
        ReleasedAt            datetime2      NOT NULL,
        ReleasedByEmployeeId  int            NULL,
        Note                  nvarchar(500)  NULL,
        CONSTRAINT CK_HospitalPortalReleases_Source CHECK (Source IN (N'Lab', N'Radiology'))
    );
    CREATE UNIQUE INDEX IX_HospitalPortalReleases_CompanyId_Source_OrderId ON dbo.HospitalPortalReleases (CompanyId, Source, OrderId);
END

IF OBJECT_ID('dbo.HospitalPortalRefills','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalRefills (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalRefills PRIMARY KEY,
        CompanyId             int            NOT NULL,
        AccountId             int            NOT NULL,
        PatientId             int            NOT NULL,
        PrescriptionLineId    int            NOT NULL,
        Status                nvarchar(20)   NOT NULL,
        Note                  nvarchar(500)  NULL,
        CreatedAt             datetime2      NOT NULL,
        HandledAt             datetime2      NULL,
        HandledByEmployeeId   int            NULL,
        Answer                nvarchar(500)  NULL,
        CONSTRAINT CK_HospitalPortalRefills_Status CHECK (Status IN (N'Open', N'Approved', N'Declined', N'Withdrawn')),
        CONSTRAINT FK_HospitalPortalRefills_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT FK_HospitalPortalRefills_Line FOREIGN KEY (PrescriptionLineId) REFERENCES dbo.HospitalPrescriptionLines(ID)
    );
    CREATE INDEX IX_HospitalPortalRefills_CompanyId_Status ON dbo.HospitalPortalRefills (CompanyId, Status);
    CREATE INDEX IX_HospitalPortalRefills_CompanyId_PrescriptionLineId ON dbo.HospitalPortalRefills (CompanyId, PrescriptionLineId);
END

COMMIT TRANSACTION;
