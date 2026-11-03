-- ==========================================================================================
-- Hospital H13 — the emergency department. Its bays (resus / acute / minor: the department's own list,
-- because the facility's beds belong to admission), and the ER visit from the door to its outcome (a known
-- patient or an unknown one by label; ESI triage 1–5 with vital signs; the board lane, the bay and the
-- doctor; the times door-to-triage and door-to-doctor; the outcome).
--
-- TWO NEW TABLES (the bays and the visits). Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h4_outpatient.sql first.', 1;

-- The department's own bays: the facility's beds belong to admission (H3) and must never offer an ER bay.
IF OBJECT_ID('dbo.HospitalErBays','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalErBays (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalErBays PRIMARY KEY,
        CompanyId  int            NOT NULL,
        Code       nvarchar(20)   NOT NULL,
        Name       nvarchar(100)  NOT NULL,
        NameEn     nvarchar(100)  NOT NULL,
        Zone       nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalErBays_Zone DEFAULT('Acute'),
        SortOrder  int            NOT NULL CONSTRAINT DF_HospitalErBays_Sort DEFAULT(0),
        IsActive   bit            NOT NULL CONSTRAINT DF_HospitalErBays_Active DEFAULT(1),
        CONSTRAINT CK_HospitalErBays_Zone CHECK (Zone IN ('Resus','Acute','Minor'))
    );
    CREATE UNIQUE INDEX UX_HospitalErBays_Code ON dbo.HospitalErBays (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalErVisits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalErVisits (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalErVisits PRIMARY KEY,
        CompanyId            int            NOT NULL,
        VisitNo              nvarchar(30)   NOT NULL,
        PatientId            int            NULL,
        UnknownLabel         nvarchar(100)  NULL,
        ArrivalMode          nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalErVisits_Mode DEFAULT('WalkIn'),
        Complaint            nvarchar(300)  NOT NULL,
        Esi                  tinyint        NULL,
        Stage                nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalErVisits_Stage DEFAULT('Triage'),
        BayId                int            NULL,
        PractitionerId       int            NULL,
        BpSystolic           smallint       NULL,
        BpDiastolic          smallint       NULL,
        HeartRate            smallint       NULL,
        RespRate             smallint       NULL,
        TemperatureC         decimal(4,1)   NULL,
        SpO2                 tinyint        NULL,
        PainScore            tinyint        NULL,
        TriageNote           nvarchar(500)  NULL,
        ArrivedAt            datetime2      NOT NULL,
        TriagedAt            datetime2      NULL,
        TriagedByEmployeeId  int            NULL,
        SeenAt               datetime2      NULL,
        Outcome              nvarchar(24)   NULL,
        OutcomeNote          nvarchar(500)  NULL,
        ClosedAt             datetime2      NULL,
        ClosedByEmployeeId   int            NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalErVisits_Mode CHECK (ArrivalMode IN ('WalkIn','Ambulance','Referral')),
        CONSTRAINT CK_HospitalErVisits_Stage CHECK (Stage IN ('Triage','Waiting','Treatment','Observation','Disposition','Closed')),
        CONSTRAINT CK_HospitalErVisits_Esi CHECK (Esi IS NULL OR Esi BETWEEN 1 AND 5),
        CONSTRAINT CK_HospitalErVisits_Pain CHECK (PainScore IS NULL OR PainScore BETWEEN 0 AND 10),
        CONSTRAINT CK_HospitalErVisits_Who CHECK (PatientId IS NOT NULL OR UnknownLabel IS NOT NULL),
        CONSTRAINT CK_HospitalErVisits_Triaged CHECK (Stage = 'Triage' OR Esi IS NOT NULL),
        CONSTRAINT CK_HospitalErVisits_Seen CHECK (Stage NOT IN ('Treatment','Observation') OR SeenAt IS NOT NULL),
        -- a closed visit has its time and outcome and has left its bay; an open one has neither
        CONSTRAINT CK_HospitalErVisits_Closed CHECK ((Stage = 'Closed' AND ClosedAt IS NOT NULL AND Outcome IS NOT NULL AND BayId IS NULL) OR (Stage <> 'Closed' AND ClosedAt IS NULL AND Outcome IS NULL)),
        CONSTRAINT CK_HospitalErVisits_Outcome CHECK (Outcome IS NULL OR Outcome IN ('Discharged','Admitted','Transferred','LeftWithoutBeingSeen','Deceased')),
        CONSTRAINT CK_HospitalErVisits_Lwbs CHECK (Outcome IS NULL OR Outcome <> 'LeftWithoutBeingSeen' OR SeenAt IS NULL),
        CONSTRAINT FK_HospitalErVisits_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalErVisits_Bay FOREIGN KEY (BayId) REFERENCES dbo.HospitalErBays(ID),
        CONSTRAINT FK_HospitalErVisits_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalErVisits_No ON dbo.HospitalErVisits (CompanyId, VisitNo);
    CREATE INDEX IX_HospitalErVisits_Stage ON dbo.HospitalErVisits (CompanyId, Stage);
    -- one open visit per bay, and one open visit per known patient
    CREATE UNIQUE INDEX UX_HospitalErVisits_OpenBay ON dbo.HospitalErVisits (BayId) WHERE BayId IS NOT NULL AND Stage <> N'Closed';
    CREATE UNIQUE INDEX UX_HospitalErVisits_OpenPatient ON dbo.HospitalErVisits (CompanyId, PatientId) WHERE PatientId IS NOT NULL AND Stage <> N'Closed';
END

COMMIT TRANSACTION;
