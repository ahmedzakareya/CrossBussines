-- ==========================================================================================
-- Hospital H8 — the laboratory. Lab-test setup (specimen, section) and analytes (unit, reference and
-- critical limits) for services of the Lab category; lab orders born from a signed visit's lab order
-- lines (one to one), walking Ordered → Collected → Received → Resulted → Verified; and the results,
-- each a snapshot of its analyte as it was when entered. Two-step: entered, then verified.
--
-- And the critical-value alerts: one per doctor told, shown on any screen of the system until acknowledged
-- (a critical one by reading the value back), reminded, then escalated within the department.
--
-- FIVE NEW TABLES. Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounterOrders','U') IS NULL
    THROW 51970, 'Run hospital_h5_consultation.sql and hospital_h6_billing.sql first.', 1;

IF OBJECT_ID('dbo.HospitalLabTests','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabTests (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalLabTests PRIMARY KEY,
        CompanyId  int            NOT NULL,
        ServiceId  int            NOT NULL,
        Specimen   nvarchar(100)  NOT NULL,
        SpecimenEn nvarchar(100)  NULL,
        Section    nvarchar(100)  NOT NULL,
        SectionEn  nvarchar(100)  NULL
    );
    CREATE UNIQUE INDEX UX_HospitalLabTests_Service ON dbo.HospitalLabTests (CompanyId, ServiceId);
END

IF OBJECT_ID('dbo.HospitalLabAnalytes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabAnalytes (
        ID           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalLabAnalytes PRIMARY KEY,
        CompanyId    int            NOT NULL,
        ServiceId    int            NOT NULL,
        Code         nvarchar(30)   NOT NULL,
        Name         nvarchar(150)  NOT NULL,
        NameEn       nvarchar(150)  NOT NULL,
        Unit         nvarchar(30)   NULL,
        RefLow       decimal(18,4)  NULL,
        RefHigh      decimal(18,4)  NULL,
        CriticalLow  decimal(18,4)  NULL,
        CriticalHigh decimal(18,4)  NULL,
        Decimals     int            NOT NULL CONSTRAINT DF_HospitalLabAnalytes_Dec DEFAULT(1),
        SortOrder    int            NOT NULL CONSTRAINT DF_HospitalLabAnalytes_Sort DEFAULT(0),
        IsActive     bit            NOT NULL CONSTRAINT DF_HospitalLabAnalytes_Active DEFAULT(1),
        CONSTRAINT CK_HospitalLabAnalytes_Range CHECK (RefLow IS NULL OR RefHigh IS NULL OR RefLow <= RefHigh),
        CONSTRAINT CK_HospitalLabAnalytes_Dec CHECK (Decimals BETWEEN 0 AND 4)
    );
    CREATE UNIQUE INDEX UX_HospitalLabAnalytes_Code ON dbo.HospitalLabAnalytes (CompanyId, ServiceId, Code);
END

IF OBJECT_ID('dbo.HospitalLabOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabOrders (
        ID                              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalLabOrders PRIMARY KEY,
        CompanyId                       int            NOT NULL,
        AccessionNo                     nvarchar(30)   NOT NULL,
        PatientId                       int            NOT NULL,
        EncounterId                     int            NULL,
        EncounterOrderId                int            NULL,
        ServiceId                       int            NOT NULL,
        Code                            nvarchar(40)   NOT NULL,
        Name                            nvarchar(200)  NOT NULL,
        NameEn                          nvarchar(200)  NOT NULL,
        OrderedByEmployeeId             int            NULL,
        Priority                        nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalLabOrders_Prio DEFAULT('Routine'),
        Status                          nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalLabOrders_Status DEFAULT('Ordered'),
        OrderedAt                       datetime2      NOT NULL,
        CollectedAt                     datetime2      NULL,
        CollectedByEmployeeId           int            NULL,
        ReceivedAt                      datetime2      NULL,
        ReceivedByEmployeeId            int            NULL,
        ResultedAt                      datetime2      NULL,
        ResultedByEmployeeId            int            NULL,
        VerifiedAt                      datetime2      NULL,
        VerifiedByEmployeeId            int            NULL,
        Comment                         nvarchar(1000) NULL,
        IsCritical                      bit            NOT NULL CONSTRAINT DF_HospitalLabOrders_Critical DEFAULT(0),
        CriticalAcknowledgedAt          datetime2      NULL,
        CriticalAcknowledgedByEmployeeId int           NULL,
        CriticalNote                    nvarchar(400)  NULL,
        CONSTRAINT CK_HospitalLabOrders_Prio CHECK (Priority IN ('Routine','Urgent','Stat')),
        CONSTRAINT CK_HospitalLabOrders_Status CHECK (Status IN ('Ordered','Collected','Received','Resulted','Verified')),
        CONSTRAINT CK_HospitalLabOrders_Verified CHECK (Status <> 'Verified' OR (VerifiedAt IS NOT NULL AND ResultedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_HospitalLabOrders_Accession ON dbo.HospitalLabOrders (CompanyId, AccessionNo);
    CREATE UNIQUE INDEX UX_HospitalLabOrders_EncounterOrder ON dbo.HospitalLabOrders (CompanyId, EncounterOrderId) WHERE EncounterOrderId IS NOT NULL;
    CREATE INDEX IX_HospitalLabOrders_Status ON dbo.HospitalLabOrders (CompanyId, Status);
    CREATE INDEX IX_HospitalLabOrders_Patient ON dbo.HospitalLabOrders (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalLabResults','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabResults (
        ID          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalLabResults PRIMARY KEY,
        CompanyId   int            NOT NULL,
        LabOrderId  int            NOT NULL CONSTRAINT FK_HospitalLabResults_Order REFERENCES dbo.HospitalLabOrders(ID),
        AnalyteId   int            NOT NULL,
        Name        nvarchar(150)  NOT NULL,
        NameEn      nvarchar(150)  NOT NULL,
        Unit        nvarchar(30)   NULL,
        Value       decimal(18,4)  NULL,
        TextValue   nvarchar(200)  NULL,
        RefLow      decimal(18,4)  NULL,
        RefHigh     decimal(18,4)  NULL,
        Flag        nvarchar(2)    NOT NULL CONSTRAINT DF_HospitalLabResults_Flag DEFAULT('N'),
        Decimals    int            NOT NULL CONSTRAINT DF_HospitalLabResults_Dec DEFAULT(1),
        SortOrder   int            NOT NULL CONSTRAINT DF_HospitalLabResults_Sort DEFAULT(0),
        CONSTRAINT CK_HospitalLabResults_Flag CHECK (Flag IN ('N','L','H','LL','HH'))
    );
    CREATE UNIQUE INDEX UX_HospitalLabResults_Analyte ON dbo.HospitalLabResults (LabOrderId, AnalyteId);
END

IF OBJECT_ID('dbo.HospitalLabCriticalAlerts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabCriticalAlerts (
        ID                       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalLabCriticalAlerts PRIMARY KEY,
        CompanyId                int            NOT NULL,
        LabOrderId               int            NOT NULL CONSTRAINT FK_HospitalLabCriticalAlerts_Order REFERENCES dbo.HospitalLabOrders(ID),
        Level                    nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalLabCriticalAlerts_Level DEFAULT('Critical'),
        Reason                   nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalLabCriticalAlerts_Reason DEFAULT('Ordering'),
        ToEmployeeId             int            NOT NULL,
        ToPractitionerId         int            NULL,
        Value                    nvarchar(40)   NULL,
        ViaSms                   bit            NOT NULL CONSTRAINT DF_HospitalLabCriticalAlerts_Sms DEFAULT(0),
        SentAt                   datetime2      NOT NULL,
        SentByEmployeeId         int            NULL,
        SeenAt                   datetime2      NULL,
        AcknowledgedAt           datetime2      NULL,
        AcknowledgedByEmployeeId int            NULL,
        ReadBack                 nvarchar(40)   NULL,
        RemindedAt               datetime2      NULL,
        EscalatedAt              datetime2      NULL,
        EscalatedFromAlertId     int            NULL,
        CONSTRAINT CK_HospitalLabCriticalAlerts_Level CHECK (Level IN ('Critical','Abnormal')),
        CONSTRAINT CK_HospitalLabCriticalAlerts_Reason CHECK (Reason IN ('Ordering','Chosen','Escalation'))
    );
    CREATE INDEX IX_HospitalLabCriticalAlerts_To ON dbo.HospitalLabCriticalAlerts (CompanyId, ToEmployeeId, AcknowledgedAt);
    CREATE INDEX IX_HospitalLabCriticalAlerts_Order ON dbo.HospitalLabCriticalAlerts (CompanyId, LabOrderId);
END

COMMIT;
