-- ==========================================================================================
-- Hospital H9 — radiology. Imaging-exam setup (modality, contrast, report template) for services of
-- the Radiology category; the imaging rooms (machines) with a modality and a maintenance switch; and
-- imaging orders born from a signed visit's radiology order lines (one to one), walking
-- Scheduled → Arrived → Scanning → Reporting → Signed, with the radiologist's report.
-- A report signed Critical is told through the critical-value alerts of H8, which now name either a
-- lab order or an imaging order (exactly one).
--
-- THREE NEW TABLES and two changes to HospitalLabCriticalAlerts. Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabCriticalAlerts','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounterOrders','U') IS NULL
    THROW 51980, 'Run hospital_h8_lab.sql first.', 1;

IF OBJECT_ID('dbo.HospitalRadExams','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadExams (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRadExams PRIMARY KEY,
        CompanyId  int             NOT NULL,
        ServiceId  int             NOT NULL,
        Modality   nvarchar(4)     NOT NULL,
        Contrast   bit             NOT NULL CONSTRAINT DF_HospitalRadExams_Contrast DEFAULT(0),
        Template   nvarchar(2000)  NULL,
        TemplateEn nvarchar(2000)  NULL,
        CONSTRAINT CK_HospitalRadExams_Modality CHECK (Modality IN ('XR','CT','MR','US'))
    );
    CREATE UNIQUE INDEX UX_HospitalRadExams_Service ON dbo.HospitalRadExams (CompanyId, ServiceId);
END

IF OBJECT_ID('dbo.HospitalRadRooms','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadRooms (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRadRooms PRIMARY KEY,
        CompanyId       int            NOT NULL,
        Code            nvarchar(20)   NOT NULL,
        Name            nvarchar(100)  NOT NULL,
        NameEn          nvarchar(100)  NOT NULL,
        Modality        nvarchar(4)    NOT NULL,
        FacilityRoomId  int            NULL,
        InMaintenance   bit            NOT NULL CONSTRAINT DF_HospitalRadRooms_Maint DEFAULT(0),
        MaintenanceNote nvarchar(200)  NULL,
        IsActive        bit            NOT NULL CONSTRAINT DF_HospitalRadRooms_Active DEFAULT(1),
        SortOrder       int            NOT NULL CONSTRAINT DF_HospitalRadRooms_Sort DEFAULT(0),
        CONSTRAINT CK_HospitalRadRooms_Modality CHECK (Modality IN ('XR','CT','MR','US'))
    );
    CREATE UNIQUE INDEX UX_HospitalRadRooms_Code ON dbo.HospitalRadRooms (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadOrders (
        ID                               int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRadOrders PRIMARY KEY,
        CompanyId                        int             NOT NULL,
        AccessionNo                      nvarchar(30)    NOT NULL,
        PatientId                        int             NOT NULL,
        EncounterId                      int             NULL,
        EncounterOrderId                 int             NULL,
        ServiceId                        int             NOT NULL,
        Code                             nvarchar(40)    NOT NULL,
        Name                             nvarchar(200)   NOT NULL,
        NameEn                           nvarchar(200)   NOT NULL,
        Modality                         nvarchar(4)     NOT NULL,
        Contrast                         bit             NOT NULL CONSTRAINT DF_HospitalRadOrders_Contrast DEFAULT(0),
        OrderedByEmployeeId              int             NULL,
        Priority                         nvarchar(10)    NOT NULL CONSTRAINT DF_HospitalRadOrders_Prio DEFAULT('Routine'),
        Status                           nvarchar(12)    NOT NULL CONSTRAINT DF_HospitalRadOrders_Status DEFAULT('Scheduled'),
        RadRoomId                        int             NULL CONSTRAINT FK_HospitalRadOrders_Room REFERENCES dbo.HospitalRadRooms(ID),
        OrderedAt                        datetime2       NOT NULL,
        ArrivedAt                        datetime2       NULL,
        ArrivedByEmployeeId              int             NULL,
        ScanStartedAt                    datetime2       NULL,
        ScanEndedAt                      datetime2       NULL,
        ScannedByEmployeeId              int             NULL,
        Findings                         nvarchar(4000)  NULL,
        Impression                       nvarchar(1000)  NULL,
        ImpressionCategory               nvarchar(10)    NULL,
        ReportedAt                       datetime2       NULL,
        ReportedByEmployeeId             int             NULL,
        SignedAt                         datetime2       NULL,
        SignedByEmployeeId               int             NULL,
        IsCritical                       bit             NOT NULL CONSTRAINT DF_HospitalRadOrders_Critical DEFAULT(0),
        CriticalAcknowledgedAt           datetime2       NULL,
        CriticalAcknowledgedByEmployeeId int             NULL,
        CriticalNote                     nvarchar(400)   NULL,
        CONSTRAINT CK_HospitalRadOrders_Modality CHECK (Modality IN ('XR','CT','MR','US')),
        CONSTRAINT CK_HospitalRadOrders_Prio CHECK (Priority IN ('Routine','Urgent','Stat')),
        CONSTRAINT CK_HospitalRadOrders_Status CHECK (Status IN ('Scheduled','Arrived','Scanning','Reporting','Signed')),
        CONSTRAINT CK_HospitalRadOrders_Category CHECK (ImpressionCategory IS NULL OR ImpressionCategory IN ('Normal','Abnormal','Critical')),
        CONSTRAINT CK_HospitalRadOrders_Signed CHECK (Status <> 'Signed' OR (SignedAt IS NOT NULL AND Impression IS NOT NULL AND ImpressionCategory IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalRadOrders_Accession ON dbo.HospitalRadOrders (CompanyId, AccessionNo);
    CREATE UNIQUE INDEX UX_HospitalRadOrders_EncounterOrder ON dbo.HospitalRadOrders (CompanyId, EncounterOrderId) WHERE EncounterOrderId IS NOT NULL;
    CREATE INDEX IX_HospitalRadOrders_Status ON dbo.HospitalRadOrders (CompanyId, Status);
    CREATE INDEX IX_HospitalRadOrders_Patient ON dbo.HospitalRadOrders (CompanyId, PatientId);
END

-- The H8 alerts now name a lab order OR an imaging order.
IF COL_LENGTH('dbo.HospitalLabCriticalAlerts','RadOrderId') IS NULL
    ALTER TABLE dbo.HospitalLabCriticalAlerts ADD RadOrderId int NULL CONSTRAINT FK_HospitalLabCriticalAlerts_RadOrder REFERENCES dbo.HospitalRadOrders(ID);

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HospitalLabCriticalAlerts') AND name = 'LabOrderId' AND is_nullable = 0)
    ALTER TABLE dbo.HospitalLabCriticalAlerts ALTER COLUMN LabOrderId int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalLabCriticalAlerts_Source')
    EXEC('ALTER TABLE dbo.HospitalLabCriticalAlerts ADD CONSTRAINT CK_HospitalLabCriticalAlerts_Source CHECK ((LabOrderId IS NULL AND RadOrderId IS NOT NULL) OR (LabOrderId IS NOT NULL AND RadOrderId IS NULL))');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HospitalLabCriticalAlerts_RadOrder')
    EXEC('CREATE INDEX IX_HospitalLabCriticalAlerts_RadOrder ON dbo.HospitalLabCriticalAlerts (CompanyId, RadOrderId)');

COMMIT;
