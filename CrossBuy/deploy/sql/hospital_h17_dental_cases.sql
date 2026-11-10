-- ==========================================================================================
-- Hospital H17 — the long dental cases. Orthodontic cases (one contract: appliance, planned months, aligner
-- stages, price and monthly instalment), the adjustments logged on them and what was paid against them; and
-- the implant register (system, size, lot, day placed, stability, stage, surgeon).
--
-- FOUR NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalOrthoCases','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrthoCases (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalOrthoCases PRIMARY KEY,
        CompanyId            int            NOT NULL,
        CaseNo               nvarchar(20)   NOT NULL,
        PatientId            int            NOT NULL,
        PractitionerId       int            NULL,
        Appliance            nvarchar(20)   NOT NULL,
        StartDate            date           NOT NULL,
        PlannedMonths        tinyint        NOT NULL,
        Stages               tinyint        NULL,
        CurrentStage         tinyint        NULL,
        ContractTotal        decimal(18,3)  NOT NULL,
        MonthlyInstalment    decimal(18,3)  NOT NULL CONSTRAINT DF_HospitalOrthoCases_Inst DEFAULT(0),
        NextVisit            date           NULL,
        Status               nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalOrthoCases_Status DEFAULT('Active'),
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalOrthoCases_Appliance CHECK (Appliance IN ('FixedMetal','FixedCeramic','Lingual','ClearAligners','Functional','Removable','Retention')),
        CONSTRAINT CK_HospitalOrthoCases_Status CHECK (Status IN ('Active','Retention','Completed','Discontinued')),
        CONSTRAINT CK_HospitalOrthoCases_Months CHECK (PlannedMonths BETWEEN 1 AND 60),
        -- only clear aligners are staged, and the current stage is one of them
        CONSTRAINT CK_HospitalOrthoCases_Stages CHECK ((Appliance = 'ClearAligners' AND Stages BETWEEN 1 AND 99 AND CurrentStage BETWEEN 1 AND Stages) OR (Appliance <> 'ClearAligners' AND Stages IS NULL AND CurrentStage IS NULL)),
        CONSTRAINT CK_HospitalOrthoCases_Money CHECK (ContractTotal >= 0 AND MonthlyInstalment >= 0 AND MonthlyInstalment <= ContractTotal),
        CONSTRAINT FK_HospitalOrthoCases_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalOrthoCases_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalOrthoCases_CompanyId_CaseNo ON dbo.HospitalOrthoCases (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalOrthoCases_CompanyId_PatientId ON dbo.HospitalOrthoCases (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalOrthoVisits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrthoVisits (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalOrthoVisits PRIMARY KEY,
        CompanyId       int            NOT NULL,
        CaseId          int            NOT NULL,
        VisitDate       date           NOT NULL,
        AlignerStage    tinyint        NULL,
        Note            nvarchar(300)  NULL,
        PractitionerId  int            NULL,
        CreatedAt       datetime2      NOT NULL,
        CONSTRAINT FK_HospitalOrthoVisits_Case FOREIGN KEY (CaseId) REFERENCES dbo.HospitalOrthoCases(ID),
        CONSTRAINT FK_HospitalOrthoVisits_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE INDEX IX_HospitalOrthoVisits_CompanyId_CaseId ON dbo.HospitalOrthoVisits (CompanyId, CaseId);
END

IF OBJECT_ID('dbo.HospitalOrthoPayments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrthoPayments (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalOrthoPayments PRIMARY KEY,
        CompanyId            int            NOT NULL,
        CaseId               int            NOT NULL,
        PaidOn               date           NOT NULL,
        Amount               decimal(18,3)  NOT NULL,
        Reference            nvarchar(60)   NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalOrthoPayments_Amount CHECK (Amount > 0),
        CONSTRAINT FK_HospitalOrthoPayments_Case FOREIGN KEY (CaseId) REFERENCES dbo.HospitalOrthoCases(ID)
    );
    CREATE INDEX IX_HospitalOrthoPayments_CompanyId_CaseId ON dbo.HospitalOrthoPayments (CompanyId, CaseId);
END

IF OBJECT_ID('dbo.HospitalImplants','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImplants (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalImplants PRIMARY KEY,
        CompanyId            int            NOT NULL,
        ImplantNo            nvarchar(20)   NOT NULL,
        PatientId            int            NOT NULL,
        Tooth                tinyint        NOT NULL,
        System               nvarchar(60)   NOT NULL,
        Diameter             decimal(4,1)   NOT NULL,
        Length               decimal(4,1)   NOT NULL,
        Lot                  nvarchar(40)   NULL,
        PlacedOn             date           NULL,
        Isq                  tinyint        NULL,
        Stage                nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalImplants_Stage DEFAULT('Planned'),
        SurgeonId            int            NULL,
        RestoredOn           date           NULL,
        FailedOn             date           NULL,
        FailureReason        nvarchar(300)  NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalImplants_Tooth CHECK (Tooth BETWEEN 11 AND 48 AND Tooth % 10 BETWEEN 1 AND 8),
        CONSTRAINT CK_HospitalImplants_Stage CHECK (Stage IN ('Planned','Healing','Restored','Failed')),
        -- a placed fixture has its lot and its day
        CONSTRAINT CK_HospitalImplants_Placed CHECK (Stage = 'Planned' OR (Lot IS NOT NULL AND PlacedOn IS NOT NULL)),
        CONSTRAINT CK_HospitalImplants_Failed CHECK (Stage <> 'Failed' OR (FailedOn IS NOT NULL AND FailureReason IS NOT NULL)),
        CONSTRAINT CK_HospitalImplants_Isq CHECK (Isq IS NULL OR Isq BETWEEN 1 AND 100),
        CONSTRAINT FK_HospitalImplants_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalImplants_Surgeon FOREIGN KEY (SurgeonId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalImplants_CompanyId_ImplantNo ON dbo.HospitalImplants (CompanyId, ImplantNo);
    -- a recall by the maker is answered by lot
    CREATE INDEX IX_HospitalImplants_CompanyId_Lot ON dbo.HospitalImplants (CompanyId, Lot);
END

COMMIT TRANSACTION;
