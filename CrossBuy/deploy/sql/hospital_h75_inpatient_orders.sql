-- ==========================================================================================
-- Hospital H75 — lab tests and imaging for inpatients, charged to the stay's bill; and order sets that hold them.
--   HospitalInpatientOrders  one test or exam a doctor ordered for a patient in a bed (and its cancel, with why)
--   HospitalOrderSetItems    + ServiceId; an order set line may now be a lab test ('Lab') or an imaging exam ('Rad')
--
-- Additive (one new table, one new nullable column, two CHECKs widened). Idempotent (OBJECT_ID / COL_LENGTH guards).
-- Runs after hospital_h74_order_sets.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;
IF OBJECT_ID('dbo.HospitalOrderSetItems','U') IS NULL
    THROW 51990, 'Run hospital_h74_order_sets.sql first.', 1;

IF OBJECT_ID('dbo.HospitalInpatientOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInpatientOrders (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        AdmissionId            int            NOT NULL,
        PatientId              int            NOT NULL,
        Kind                   nvarchar(5)    NOT NULL,
        ServiceId              int            NOT NULL,
        LabOrderId             int            NULL,
        RadOrderId             int            NULL,
        AccessionNo            nvarchar(30)   NOT NULL,
        Code                   nvarchar(40)   NOT NULL,
        Name                   nvarchar(200)  NOT NULL,
        NameEn                 nvarchar(200)  NOT NULL,
        BillLineId             int            NULL,
        Priority               nvarchar(10)   NOT NULL,
        Note                   nvarchar(300)  NULL,
        Status                 nvarchar(10)   NOT NULL,
        OrderedAt              datetime2      NOT NULL,
        OrderedByEmployeeId    int            NULL,
        CancelledAt            datetime2      NULL,
        CancelledByEmployeeId  int            NULL,
        CancelReason           nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalInpatientOrders PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalInpatientOrders_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalInpatientOrders_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalInpatientOrders_Kind CHECK (Kind IN ('Lab','Rad')),
        CONSTRAINT CK_HospitalInpatientOrders_Priority CHECK (Priority IN ('Routine','Urgent','Stat')),
        CONSTRAINT CK_HospitalInpatientOrders_Status CHECK (Status IN ('Active','Cancelled')),
        -- an active order points at the order it created; a cancelled one says when and why
        CONSTRAINT CK_HospitalInpatientOrders_Path CHECK (
            (Status = 'Active' AND ((Kind = 'Lab' AND LabOrderId IS NOT NULL) OR (Kind = 'Rad' AND RadOrderId IS NOT NULL)))
            OR (Status = 'Cancelled' AND CancelledAt IS NOT NULL AND CancelReason IS NOT NULL))
    );
    CREATE INDEX IX_HospitalInpatientOrders_CompanyId_AdmissionId ON dbo.HospitalInpatientOrders (CompanyId, AdmissionId);
END

-- order sets: a line may be a lab test or an imaging exam (a catalogue service)
IF COL_LENGTH('dbo.HospitalOrderSetItems', 'ServiceId') IS NULL
    ALTER TABLE dbo.HospitalOrderSetItems ADD ServiceId int NULL;
COMMIT TRANSACTION;

-- the widened CHECKs name the new column, so they are written in their own batch (after the column exists)
BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalOrderSetItems_Kind' AND definition NOT LIKE '%Lab%')
BEGIN
    ALTER TABLE dbo.HospitalOrderSetItems DROP CONSTRAINT CK_HospitalOrderSetItems_Kind;
    ALTER TABLE dbo.HospitalOrderSetItems WITH CHECK ADD CONSTRAINT CK_HospitalOrderSetItems_Kind CHECK (Kind IN ('Med','Task','Lab','Rad'));
END
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalOrderSetItems_Shape' AND definition NOT LIKE '%ServiceId%')
BEGIN
    ALTER TABLE dbo.HospitalOrderSetItems DROP CONSTRAINT CK_HospitalOrderSetItems_Shape;
    EXEC(N'ALTER TABLE dbo.HospitalOrderSetItems WITH CHECK ADD CONSTRAINT CK_HospitalOrderSetItems_Shape CHECK (
            (Kind = ''Med'' AND Drug IS NOT NULL AND Dose IS NOT NULL AND Route IS NOT NULL)
            OR (Kind = ''Task'' AND (TitleAr IS NOT NULL OR TitleEn IS NOT NULL) AND DueInHours BETWEEN 0 AND 72)
            OR (Kind IN (''Lab'',''Rad'') AND ServiceId IS NOT NULL))');
END
COMMIT TRANSACTION;
