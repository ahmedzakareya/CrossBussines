-- ==========================================================================================
-- Hospital H14 — the nursing station. Inpatient medication orders (drug, dose, route, the ward-clock hours
-- or as needed, controlled or not) on an admission; the medication administration record (one answer per
-- scheduled dose: given / held / missed, its reason, a controlled drug's witness); nursing tasks; and one
-- shift handover note per ward, day and shift.
--
-- FOUR NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalWards','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql, hospital_h2_patients.sql and hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalMedOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMedOrders (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalMedOrders PRIMARY KEY,
        CompanyId            int            NOT NULL,
        AdmissionId          int            NOT NULL,
        PatientId            int            NOT NULL,
        Drug                 nvarchar(200)  NOT NULL,
        ItemId               int            NULL,
        Dose                 nvarchar(60)   NOT NULL,
        Route                nvarchar(4)    NOT NULL CONSTRAINT DF_HospitalMedOrders_Route DEFAULT('PO'),
        Hours                varchar(80)    NULL,
        AsNeeded             bit            NOT NULL CONSTRAINT DF_HospitalMedOrders_Prn DEFAULT(0),
        Controlled           bit            NOT NULL CONSTRAINT DF_HospitalMedOrders_Controlled DEFAULT(0),
        Note                 nvarchar(300)  NULL,
        Status               nvarchar(8)    NOT NULL CONSTRAINT DF_HospitalMedOrders_Status DEFAULT('Active'),
        StartAt              datetime2      NOT NULL,
        StoppedAt            datetime2      NULL,
        OrderedByEmployeeId  int            NULL,
        StoppedByEmployeeId  int            NULL,
        CreatedAt            datetime2      NOT NULL,
        CONSTRAINT CK_HospitalMedOrders_Route CHECK (Route IN ('PO','IV','IM','SC','INH','TOP')),
        CONSTRAINT CK_HospitalMedOrders_Status CHECK (Status IN ('Active','Stopped') AND (Status <> 'Stopped' OR StoppedAt IS NOT NULL)),
        -- a schedule or "as needed", never both and never neither
        CONSTRAINT CK_HospitalMedOrders_Schedule CHECK ((AsNeeded = 1 AND Hours IS NULL) OR (AsNeeded = 0 AND Hours IS NOT NULL AND Hours <> '')),
        CONSTRAINT FK_HospitalMedOrders_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalMedOrders_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE INDEX IX_HospitalMedOrders_Admission ON dbo.HospitalMedOrders (CompanyId, AdmissionId, Status);
END

IF OBJECT_ID('dbo.HospitalMarEntries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMarEntries (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalMarEntries PRIMARY KEY,
        CompanyId             int            NOT NULL,
        OrderId               int            NOT NULL,
        PatientId             int            NOT NULL,
        DueAt                 datetime2      NULL,
        Status                nvarchar(8)    NOT NULL,
        Reason                nvarchar(300)  NULL,
        RecordedAt            datetime2      NOT NULL,
        RecordedByEmployeeId  int            NULL,
        WitnessEmployeeId     int            NULL,
        CONSTRAINT CK_HospitalMarEntries_Status CHECK (Status IN ('Given','Held','Missed')),
        CONSTRAINT CK_HospitalMarEntries_Reason CHECK (Status = 'Given' OR Reason IS NOT NULL),
        CONSTRAINT CK_HospitalMarEntries_Witness CHECK (WitnessEmployeeId IS NULL OR (Status = 'Given' AND (RecordedByEmployeeId IS NULL OR WitnessEmployeeId <> RecordedByEmployeeId))),
        CONSTRAINT FK_HospitalMarEntries_Order FOREIGN KEY (OrderId) REFERENCES dbo.HospitalMedOrders(ID),
        CONSTRAINT FK_HospitalMarEntries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    -- one answer per scheduled dose
    CREATE UNIQUE INDEX UX_HospitalMarEntries_Dose ON dbo.HospitalMarEntries (OrderId, DueAt) WHERE DueAt IS NOT NULL;
    CREATE INDEX IX_HospitalMarEntries_Patient ON dbo.HospitalMarEntries (CompanyId, PatientId, RecordedAt);
END

IF OBJECT_ID('dbo.HospitalNursingTasks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalNursingTasks (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalNursingTasks PRIMARY KEY,
        CompanyId            int            NOT NULL,
        AdmissionId          int            NOT NULL,
        PatientId            int            NOT NULL,
        Title                nvarchar(200)  NOT NULL,
        DueAt                datetime2      NOT NULL,
        DoneAt               datetime2      NULL,
        DoneByEmployeeId     int            NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT FK_HospitalNursingTasks_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalNursingTasks_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE INDEX IX_HospitalNursingTasks_Admission ON dbo.HospitalNursingTasks (CompanyId, AdmissionId, DueAt);
END

IF OBJECT_ID('dbo.HospitalShiftHandovers','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalShiftHandovers (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalShiftHandovers PRIMARY KEY,
        CompanyId            int             NOT NULL,
        WardId               int             NOT NULL,
        ShiftDate            date            NOT NULL,
        Shift                nvarchar(8)     NOT NULL,
        Note                 nvarchar(2000)  NOT NULL,
        UpdatedAt            datetime2       NOT NULL,
        UpdatedByEmployeeId  int             NULL,
        CONSTRAINT CK_HospitalShiftHandovers_Shift CHECK (Shift IN ('Day','Evening','Night')),
        CONSTRAINT FK_HospitalShiftHandovers_Ward FOREIGN KEY (WardId) REFERENCES dbo.HospitalWards(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalShiftHandovers_Shift ON dbo.HospitalShiftHandovers (CompanyId, WardId, ShiftDate, Shift);
END

COMMIT TRANSACTION;
