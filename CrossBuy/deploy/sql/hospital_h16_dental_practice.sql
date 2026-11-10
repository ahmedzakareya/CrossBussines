-- ==========================================================================================
-- Hospital H16 — the dental practice, on top of H15. Chairs (operatories) and the dental side of an H4
-- appointment (its chair, procedure, tooth, plan line and lab case); dental labs and lab cases (prescription,
-- due date, fit appointment, stages, remakes); the hygiene recall of each patient; autoclave cycles with their
-- chemical and biological indicators, and the kits used on patients from them.
--
-- SEVEN NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL OR OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
   OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalDentalPlanLines','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h4_outpatient.sql, hospital_h6_billing.sql and hospital_h15_dental.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDentalChairs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalChairs (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalChairs PRIMARY KEY,
        CompanyId       int            NOT NULL,
        Code            nvarchar(20)   NOT NULL,
        Name            nvarchar(100)  NOT NULL,
        NameEn          nvarchar(100)  NOT NULL,
        PractitionerId  int            NULL,
        SortOrder       int            NOT NULL CONSTRAINT DF_HospitalDentalChairs_Sort DEFAULT(0),
        IsActive        bit            NOT NULL CONSTRAINT DF_HospitalDentalChairs_Active DEFAULT(1),
        CONSTRAINT FK_HospitalDentalChairs_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalChairs_CompanyId_Code ON dbo.HospitalDentalChairs (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalDentalLabs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalLabs (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalLabs PRIMARY KEY,
        CompanyId  int            NOT NULL,
        Name       nvarchar(150)  NOT NULL,
        NameEn     nvarchar(150)  NOT NULL,
        Phone      nvarchar(30)   NULL,
        IsActive   bit            NOT NULL CONSTRAINT DF_HospitalDentalLabs_Active DEFAULT(1)
    );
END

IF OBJECT_ID('dbo.HospitalDentalLabCases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalLabCases (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalLabCases PRIMARY KEY,
        CompanyId            int            NOT NULL,
        CaseNo               nvarchar(20)   NOT NULL,
        PatientId            int            NOT NULL,
        PlanLineId           int            NULL,
        Teeth                nvarchar(20)   NULL,
        CaseType             nvarchar(20)   NOT NULL,
        Material             nvarchar(60)   NOT NULL,
        Shade                nvarchar(10)   NULL,
        LabId                int            NOT NULL,
        DueDate              date           NOT NULL,
        FitAppointmentId     int            NULL,
        Stage                nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalDentalLabCases_Stage DEFAULT('Prescribed'),
        SentAt               datetime2      NULL,
        ReceivedAt           datetime2      NULL,
        FittedAt             datetime2      NULL,
        RemakeFault          nvarchar(10)   NULL,
        RemakeReason         nvarchar(300)  NULL,
        Remakes              tinyint        NOT NULL CONSTRAINT DF_HospitalDentalLabCases_Remakes DEFAULT(0),
        Cost                 decimal(18,3)  NOT NULL CONSTRAINT DF_HospitalDentalLabCases_Cost DEFAULT(0),
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalDentalLabCases_Stage CHECK (Stage IN ('Prescribed','Sent','Fabricating','Shipped','Received','Fitted','Remake')),
        CONSTRAINT CK_HospitalDentalLabCases_Type CHECK (CaseType IN ('Crown','Bridge','Veneer','Inlay','ImplantCrown','Denture','Retainer','Aligner','NightGuard')),
        -- a remake says whose fault and why
        CONSTRAINT CK_HospitalDentalLabCases_Remake CHECK (Stage <> 'Remake' OR (RemakeFault IN ('Lab','Clinic') AND RemakeReason IS NOT NULL)),
        CONSTRAINT CK_HospitalDentalLabCases_Cost CHECK (Cost >= 0),
        CONSTRAINT FK_HospitalDentalLabCases_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalDentalLabCases_Lab FOREIGN KEY (LabId) REFERENCES dbo.HospitalDentalLabs(ID),
        CONSTRAINT FK_HospitalDentalLabCases_Line FOREIGN KEY (PlanLineId) REFERENCES dbo.HospitalDentalPlanLines(ID),
        CONSTRAINT FK_HospitalDentalLabCases_Fit FOREIGN KEY (FitAppointmentId) REFERENCES dbo.HospitalAppointments(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalLabCases_CompanyId_CaseNo ON dbo.HospitalDentalLabCases (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalDentalLabCases_CompanyId_Stage ON dbo.HospitalDentalLabCases (CompanyId, Stage);
END

IF OBJECT_ID('dbo.HospitalDentalVisits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalVisits (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalVisits PRIMARY KEY,
        CompanyId      int      NOT NULL,
        AppointmentId  int      NOT NULL,
        ChairId        int      NOT NULL,
        ServiceId      int      NULL,
        Tooth          tinyint  NULL,
        PlanLineId     int      NULL,
        LabCaseId      int      NULL,
        CONSTRAINT CK_HospitalDentalVisits_Tooth CHECK (Tooth IS NULL OR (Tooth BETWEEN 11 AND 48 AND Tooth % 10 BETWEEN 1 AND 8) OR (Tooth BETWEEN 51 AND 85 AND Tooth % 10 BETWEEN 1 AND 5)),
        CONSTRAINT FK_HospitalDentalVisits_Appointment FOREIGN KEY (AppointmentId) REFERENCES dbo.HospitalAppointments(ID),
        CONSTRAINT FK_HospitalDentalVisits_Chair FOREIGN KEY (ChairId) REFERENCES dbo.HospitalDentalChairs(ID),
        CONSTRAINT FK_HospitalDentalVisits_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT FK_HospitalDentalVisits_Line FOREIGN KEY (PlanLineId) REFERENCES dbo.HospitalDentalPlanLines(ID),
        CONSTRAINT FK_HospitalDentalVisits_LabCase FOREIGN KEY (LabCaseId) REFERENCES dbo.HospitalDentalLabCases(ID)
    );
    -- an appointment has one dental side
    CREATE UNIQUE INDEX IX_HospitalDentalVisits_AppointmentId ON dbo.HospitalDentalVisits (AppointmentId);
    CREATE INDEX IX_HospitalDentalVisits_CompanyId_ChairId ON dbo.HospitalDentalVisits (CompanyId, ChairId);
END

IF OBJECT_ID('dbo.HospitalDentalRecalls','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalRecalls (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalRecalls PRIMARY KEY,
        CompanyId       int        NOT NULL,
        PatientId       int        NOT NULL,
        IntervalMonths  tinyint    NOT NULL CONSTRAINT DF_HospitalDentalRecalls_Interval DEFAULT(6),
        LastVisit       date       NULL,
        DueDate         date       NOT NULL,
        Attempts        tinyint    NOT NULL CONSTRAINT DF_HospitalDentalRecalls_Attempts DEFAULT(0),
        LastContactAt   datetime2  NULL,
        UpdatedAt       datetime2  NOT NULL,
        CONSTRAINT CK_HospitalDentalRecalls_Interval CHECK (IntervalMonths BETWEEN 1 AND 24),
        CONSTRAINT FK_HospitalDentalRecalls_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalRecalls_CompanyId_PatientId ON dbo.HospitalDentalRecalls (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalSterilCycles','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSterilCycles (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSterilCycles PRIMARY KEY,
        CompanyId           int            NOT NULL,
        CycleNo             nvarchar(20)   NOT NULL,
        Unit                nvarchar(60)   NOT NULL,
        RunAt               datetime2      NOT NULL,
        Load                nvarchar(300)  NOT NULL,
        Chemical            nvarchar(8)    NOT NULL CONSTRAINT DF_HospitalSterilCycles_Chem DEFAULT('Pending'),
        Biological          nvarchar(8)    NOT NULL CONSTRAINT DF_HospitalSterilCycles_Bio DEFAULT('Pending'),
        OperatorEmployeeId  int            NULL,
        CreatedAt           datetime2      NOT NULL,
        CONSTRAINT CK_HospitalSterilCycles_Chem CHECK (Chemical IN ('Pending','Pass','Fail')),
        CONSTRAINT CK_HospitalSterilCycles_Bio CHECK (Biological IN ('Pending','Pass','Fail'))
    );
    CREATE UNIQUE INDEX IX_HospitalSterilCycles_CompanyId_CycleNo ON dbo.HospitalSterilCycles (CompanyId, CycleNo);
END

IF OBJECT_ID('dbo.HospitalSterilUses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSterilUses (
        ID                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSterilUses PRIMARY KEY,
        CompanyId         int        NOT NULL,
        CycleId           int        NOT NULL,
        PatientId         int        NOT NULL,
        AppointmentId     int        NULL,
        UsedAt            datetime2  NOT NULL,
        UsedByEmployeeId  int        NULL,
        CONSTRAINT FK_HospitalSterilUses_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilCycles(ID),
        CONSTRAINT FK_HospitalSterilUses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalSterilUses_Appointment FOREIGN KEY (AppointmentId) REFERENCES dbo.HospitalAppointments(ID)
    );
    CREATE INDEX IX_HospitalSterilUses_CompanyId_CycleId ON dbo.HospitalSterilUses (CompanyId, CycleId);
END

COMMIT TRANSACTION;
