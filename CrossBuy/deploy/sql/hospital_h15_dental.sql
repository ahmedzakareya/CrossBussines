-- ==========================================================================================
-- Hospital H15 — dentistry, the first specialty pack. The dental profile of a catalogue service (category,
-- surfaces a multi-surface code bills, the whole-tooth work it does, pre-authorisation, lab, chair time); a
-- payer's dental benefit (cover per category, yearly limit); the patient's tooth chart (FDI 11–48 / 51–85,
-- marks on surfaces or the whole tooth: condition / planned / completed / existing); the periodontal exam
-- (six sites per tooth, mobility, furcation); and the treatment plan in phases of lines on teeth and surfaces.
--
-- NINE NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
   OR OBJECT_ID('dbo.HospitalPayers','U') IS NULL OR OBJECT_ID('dbo.HospitalPreauths','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h4_outpatient.sql, hospital_h6_billing.sql and hospital_h11_insurance.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDentalProcedures','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalProcedures (
        ID            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalProcedures PRIMARY KEY,
        CompanyId     int           NOT NULL,
        ServiceId     int           NOT NULL,
        Category      nvarchar(12)  NOT NULL,
        Surfaces      tinyint       NULL,
        ToothWork     nvarchar(10)  NULL,
        NeedsTooth    bit           NOT NULL CONSTRAINT DF_HospitalDentalProcedures_Tooth DEFAULT(0),
        NeedsPreauth  bit           NOT NULL CONSTRAINT DF_HospitalDentalProcedures_Preauth DEFAULT(0),
        NeedsLab      bit           NOT NULL CONSTRAINT DF_HospitalDentalProcedures_Lab DEFAULT(0),
        NotCovered    bit           NOT NULL CONSTRAINT DF_HospitalDentalProcedures_NotCovered DEFAULT(0),
        Minutes       smallint      NOT NULL CONSTRAINT DF_HospitalDentalProcedures_Minutes DEFAULT(30),
        CONSTRAINT CK_HospitalDentalProcedures_Category CHECK (Category IN ('Diagnostic','Preventive','Restorative','Endodontic','Prosthetic','Surgery','Implant','Cosmetic')),
        CONSTRAINT CK_HospitalDentalProcedures_Surfaces CHECK (Surfaces IS NULL OR Surfaces BETWEEN 1 AND 5),
        CONSTRAINT CK_HospitalDentalProcedures_Work CHECK (ToothWork IS NULL OR ToothWork IN ('Crown','RootCanal','Implant','Extraction')),
        -- on surfaces or on the whole tooth, never both
        CONSTRAINT CK_HospitalDentalProcedures_One CHECK (Surfaces IS NULL OR ToothWork IS NULL),
        CONSTRAINT CK_HospitalDentalProcedures_Minutes CHECK (Minutes BETWEEN 5 AND 480),
        CONSTRAINT FK_HospitalDentalProcedures_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalProcedures_CompanyId_ServiceId ON dbo.HospitalDentalProcedures (CompanyId, ServiceId);
END

IF OBJECT_ID('dbo.HospitalDentalBenefits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalBenefits (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalBenefits PRIMARY KEY,
        CompanyId            int           NOT NULL,
        PayerId              int           NOT NULL,
        YearlyLimit          decimal(18,3) NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Limit DEFAULT(0),
        DiagnosticPct        decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Diag DEFAULT(0),
        PreventivePct        decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Prev DEFAULT(0),
        RestorativePct       decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Rest DEFAULT(0),
        EndodonticPct        decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Endo DEFAULT(0),
        ProstheticPct        decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Prost DEFAULT(0),
        SurgeryPct           decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Surg DEFAULT(0),
        ImplantPct           decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Impl DEFAULT(0),
        CosmeticPct          decimal(5,2)  NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Cosm DEFAULT(0),
        UpdatedAt            datetime2     NOT NULL,
        UpdatedByEmployeeId  int           NULL,
        CONSTRAINT CK_HospitalDentalBenefits_Limit CHECK (YearlyLimit >= 0),
        CONSTRAINT CK_HospitalDentalBenefits_Pct CHECK (DiagnosticPct BETWEEN 0 AND 100 AND PreventivePct BETWEEN 0 AND 100 AND RestorativePct BETWEEN 0 AND 100
            AND EndodonticPct BETWEEN 0 AND 100 AND ProstheticPct BETWEEN 0 AND 100 AND SurgeryPct BETWEEN 0 AND 100 AND ImplantPct BETWEEN 0 AND 100 AND CosmeticPct BETWEEN 0 AND 100),
        CONSTRAINT FK_HospitalDentalBenefits_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalBenefits_CompanyId_PayerId ON dbo.HospitalDentalBenefits (CompanyId, PayerId);
END

IF OBJECT_ID('dbo.HospitalDentalCharts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalCharts (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalCharts PRIMARY KEY,
        CompanyId  int           NOT NULL,
        PatientId  int           NOT NULL,
        Dentition  nvarchar(10)  NULL,
        UpdatedAt  datetime2     NOT NULL,
        CONSTRAINT CK_HospitalDentalCharts_Dentition CHECK (Dentition IS NULL OR Dentition IN ('Permanent','Mixed','Primary')),
        CONSTRAINT FK_HospitalDentalCharts_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalCharts_CompanyId_PatientId ON dbo.HospitalDentalCharts (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalDentalPlans','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalPlans (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalPlans PRIMARY KEY,
        CompanyId            int           NOT NULL,
        PlanNo               nvarchar(20)  NOT NULL,
        PatientId            int           NOT NULL,
        PractitionerId       int           NULL,
        CurrencyId           int           NULL,
        CreatedAt            datetime2     NOT NULL,
        CreatedByEmployeeId  int           NULL,
        CONSTRAINT FK_HospitalDentalPlans_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalDentalPlans_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalPlans_CompanyId_PlanNo ON dbo.HospitalDentalPlans (CompanyId, PlanNo);
    CREATE INDEX IX_HospitalDentalPlans_CompanyId_PatientId ON dbo.HospitalDentalPlans (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalDentalPlanPhases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalPlanPhases (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalPlanPhases PRIMARY KEY,
        CompanyId            int            NOT NULL,
        PlanId               int            NOT NULL,
        Seq                  tinyint        NOT NULL,
        Title                nvarchar(150)  NOT NULL,
        TitleEn              nvarchar(150)  NOT NULL,
        Decision             nvarchar(8)    NOT NULL CONSTRAINT DF_HospitalDentalPlanPhases_Decision DEFAULT('Pending'),
        Signatory            nvarchar(150)  NULL,
        DecidedAt            datetime2      NULL,
        DecidedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalDentalPlanPhases_Decision CHECK (Decision IN ('Pending','Accepted','Declined')),
        -- a decision is signed and dated
        CONSTRAINT CK_HospitalDentalPlanPhases_Signed CHECK (Decision = 'Pending' OR (Signatory IS NOT NULL AND DecidedAt IS NOT NULL)),
        CONSTRAINT FK_HospitalDentalPlanPhases_Plan FOREIGN KEY (PlanId) REFERENCES dbo.HospitalDentalPlans(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalPlanPhases_PlanId_Seq ON dbo.HospitalDentalPlanPhases (PlanId, Seq);
END

IF OBJECT_ID('dbo.HospitalDentalPlanLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalPlanLines (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalPlanLines PRIMARY KEY,
        CompanyId              int            NOT NULL,
        PlanId                 int            NOT NULL,
        PhaseId                int            NOT NULL,
        Tooth                  tinyint        NULL,
        Surfaces               varchar(5)     NULL,
        ServiceId              int            NOT NULL,
        ProviderId             int            NULL,
        Fee                    decimal(18,3)  NOT NULL,
        Status                 nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalDentalPlanLines_Status DEFAULT('Planned'),
        PreauthId              int            NULL,
        CreatedAt              datetime2      NOT NULL,
        CompletedAt            datetime2      NULL,
        CompletedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalDentalPlanLines_Status CHECK (Status IN ('Planned','Completed','Cancelled') AND (Status <> 'Completed' OR CompletedAt IS NOT NULL)),
        CONSTRAINT CK_HospitalDentalPlanLines_Fee CHECK (Fee >= 0),
        CONSTRAINT CK_HospitalDentalPlanLines_Surfaces CHECK (Surfaces IS NULL OR Tooth IS NOT NULL),
        CONSTRAINT FK_HospitalDentalPlanLines_Plan FOREIGN KEY (PlanId) REFERENCES dbo.HospitalDentalPlans(ID),
        CONSTRAINT FK_HospitalDentalPlanLines_Phase FOREIGN KEY (PhaseId) REFERENCES dbo.HospitalDentalPlanPhases(ID),
        CONSTRAINT FK_HospitalDentalPlanLines_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT FK_HospitalDentalPlanLines_Provider FOREIGN KEY (ProviderId) REFERENCES dbo.HospitalPractitioners(ID),
        CONSTRAINT FK_HospitalDentalPlanLines_Preauth FOREIGN KEY (PreauthId) REFERENCES dbo.HospitalPreauths(ID)
    );
    CREATE INDEX IX_HospitalDentalPlanLines_CompanyId_PlanId ON dbo.HospitalDentalPlanLines (CompanyId, PlanId);
END

IF OBJECT_ID('dbo.HospitalDentalFindings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalFindings (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDentalFindings PRIMARY KEY,
        CompanyId             int            NOT NULL,
        PatientId             int            NOT NULL,
        Tooth                 tinyint        NOT NULL,
        Surfaces              varchar(5)     NULL,
        Work                  nvarchar(10)   NULL,
        Status                nvarchar(10)   NOT NULL,
        ServiceId             int            NULL,
        PlanLineId            int            NULL,
        PractitionerId        int            NULL,
        Note                  nvarchar(300)  NULL,
        RecordedAt            datetime2      NOT NULL,
        RecordedByEmployeeId  int            NULL,
        CompletedAt           datetime2      NULL,
        VoidedAt              datetime2      NULL,
        VoidedByEmployeeId    int            NULL,
        CONSTRAINT CK_HospitalDentalFindings_Tooth CHECK ((Tooth BETWEEN 11 AND 48 AND Tooth % 10 BETWEEN 1 AND 8) OR (Tooth BETWEEN 51 AND 85 AND Tooth % 10 BETWEEN 1 AND 5)),
        CONSTRAINT CK_HospitalDentalFindings_Status CHECK (Status IN ('Condition','Planned','Completed','Existing')),
        CONSTRAINT CK_HospitalDentalFindings_Work CHECK (Work IS NULL OR Work IN ('Crown','RootCanal','Implant','Extraction','Missing','Impacted')),
        -- on surfaces or on the whole tooth: exactly one
        CONSTRAINT CK_HospitalDentalFindings_One CHECK ((Surfaces IS NULL AND Work IS NOT NULL) OR (Surfaces IS NOT NULL AND Work IS NULL)),
        -- planned work is a plan's line
        CONSTRAINT CK_HospitalDentalFindings_Planned CHECK (Status <> 'Planned' OR PlanLineId IS NOT NULL),
        CONSTRAINT FK_HospitalDentalFindings_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalDentalFindings_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT FK_HospitalDentalFindings_Line FOREIGN KEY (PlanLineId) REFERENCES dbo.HospitalDentalPlanLines(ID),
        CONSTRAINT FK_HospitalDentalFindings_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE INDEX IX_HospitalDentalFindings_CompanyId_PatientId ON dbo.HospitalDentalFindings (CompanyId, PatientId);
    -- a plan line has one mark on the chart
    CREATE UNIQUE INDEX UX_HospitalDentalFindings_PlanLine ON dbo.HospitalDentalFindings (PlanLineId) WHERE PlanLineId IS NOT NULL;
END

IF OBJECT_ID('dbo.HospitalPerioExams','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPerioExams (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPerioExams PRIMARY KEY,
        CompanyId            int        NOT NULL,
        PatientId            int        NOT NULL,
        ExamDate             date       NOT NULL,
        PractitionerId       int        NULL,
        CreatedAt            datetime2  NOT NULL,
        CreatedByEmployeeId  int        NULL,
        CONSTRAINT FK_HospitalPerioExams_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPerioExams_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE INDEX IX_HospitalPerioExams_CompanyId_PatientId_ExamDate ON dbo.HospitalPerioExams (CompanyId, PatientId, ExamDate);
END

IF OBJECT_ID('dbo.HospitalPerioTeeth','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPerioTeeth (
        ID          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPerioTeeth PRIMARY KEY,
        CompanyId   int          NOT NULL,
        ExamId      int          NOT NULL,
        Tooth       tinyint      NOT NULL,
        Depths      varchar(40)  NOT NULL,
        Recession   varchar(40)  NOT NULL,
        Bleeding    char(6)      NOT NULL,
        Mobility    tinyint      NULL,
        Furcation   tinyint      NULL,
        -- perio is charted on the permanent teeth
        CONSTRAINT CK_HospitalPerioTeeth_Tooth CHECK (Tooth BETWEEN 11 AND 48 AND Tooth % 10 BETWEEN 1 AND 8),
        CONSTRAINT CK_HospitalPerioTeeth_Mobility CHECK (Mobility IS NULL OR Mobility <= 3),
        CONSTRAINT CK_HospitalPerioTeeth_Furcation CHECK (Furcation IS NULL OR Furcation BETWEEN 1 AND 3),
        CONSTRAINT FK_HospitalPerioTeeth_Exam FOREIGN KEY (ExamId) REFERENCES dbo.HospitalPerioExams(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPerioTeeth_ExamId_Tooth ON dbo.HospitalPerioTeeth (ExamId, Tooth);
END

COMMIT TRANSACTION;
