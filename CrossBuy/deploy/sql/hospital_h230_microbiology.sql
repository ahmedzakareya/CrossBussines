-- ==========================================================================================
-- Hospital H230 - microbiology: a lab test marked as a culture is reported from its culture record (source, Gram
-- stain, preliminaries, isolates and their susceptibility) instead of an analyte grid.
--
--   HospitalLabTests.IsCulture          (new column, default 0)
--   HospitalLabResults.TextValue        widened 200 -> 2000 (a culture's report is written there)
--   HospitalMicroOrganisms, HospitalMicroAntibiotics, HospitalMicroPanelItems   (dictionaries, per company)
--   HospitalMicroCultures, HospitalMicroIsolates, HospitalMicroSusceptibilities (the bench's work)
--
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabOrders','U') IS NULL
    THROW 52030, 'Run the hospital lab slice (hospital_h8_lab.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalLabTests','IsCulture') IS NULL
    ALTER TABLE dbo.HospitalLabTests ADD IsCulture bit NOT NULL CONSTRAINT DF_HospitalLabTests_IsCulture DEFAULT(0);

IF COL_LENGTH('dbo.HospitalLabResults','TextValue') < 4000   -- bytes: nvarchar(2000)
    ALTER TABLE dbo.HospitalLabResults ALTER COLUMN TextValue nvarchar(2000) NULL;

IF OBJECT_ID('dbo.HospitalMicroOrganisms','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroOrganisms (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        Name nvarchar(120) NOT NULL,
        NameEn nvarchar(120) NOT NULL,
        OrganismGroup nvarchar(20) NOT NULL,
        Gram nvarchar(3) NOT NULL,
        IsAureus bit NOT NULL,
        IsActive bit NOT NULL,
        CONSTRAINT PK_HospitalMicroOrganisms PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalMicroOrganisms_Group CHECK (OrganismGroup IN ('Staphylococci','Streptococci','Enterococci','Enterobacterales','Pseudomonas','Acinetobacter','Fungi','Other'))
    );
    CREATE UNIQUE INDEX IX_HospitalMicroOrganisms_CompanyId_Code ON dbo.HospitalMicroOrganisms (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalMicroAntibiotics','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroAntibiotics (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        Name nvarchar(120) NOT NULL,
        NameEn nvarchar(120) NOT NULL,
        Class nvarchar(40) NOT NULL,
        IsActive bit NOT NULL,
        CONSTRAINT PK_HospitalMicroAntibiotics PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalMicroAntibiotics_CompanyId_Code ON dbo.HospitalMicroAntibiotics (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalMicroPanelItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroPanelItems (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        OrganismGroup nvarchar(20) NOT NULL,
        AntibioticId int NOT NULL,
        Tier int NOT NULL,
        SortOrder int NOT NULL,
        CONSTRAINT PK_HospitalMicroPanelItems PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMicroPanelItems_Antibiotic FOREIGN KEY (AntibioticId) REFERENCES dbo.HospitalMicroAntibiotics (ID),
        CONSTRAINT CK_HospitalMicroPanelItems_Tier CHECK (Tier IN (1, 2))
    );
    CREATE UNIQUE INDEX IX_HospitalMicroPanelItems_CompanyId_Group_AntibioticId ON dbo.HospitalMicroPanelItems (CompanyId, OrganismGroup, AntibioticId);
END

IF OBJECT_ID('dbo.HospitalMicroCultures','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroCultures (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        LabOrderId int NOT NULL,
        PatientId int NOT NULL,
        Source nvarchar(20) NOT NULL,
        SourceDetail nvarchar(120) NULL,
        Status nvarchar(12) NOT NULL,
        Growth nvarchar(12) NOT NULL,
        GramStain nvarchar(300) NULL,
        Preliminary nvarchar(500) NULL,
        PreliminaryAt datetime2 NULL,
        PreliminaryByEmployeeId int NULL,
        Comment nvarchar(1000) NULL,
        Mdro nvarchar(60) NULL,
        StartedAt datetime2 NOT NULL,
        StartedByEmployeeId int NULL,
        CompletedAt datetime2 NULL,
        CompletedByEmployeeId int NULL,
        FinalAt datetime2 NULL,
        InfectionCaseId int NULL,
        CONSTRAINT PK_HospitalMicroCultures PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMicroCultures_Order FOREIGN KEY (LabOrderId) REFERENCES dbo.HospitalLabOrders (ID),
        CONSTRAINT FK_HospitalMicroCultures_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalMicroCultures_Status CHECK (Status IN ('Incubating','Preliminary','Completed','Final')),
        CONSTRAINT CK_HospitalMicroCultures_Growth CHECK (Growth IN ('Pending','NoGrowth','Growth','Contaminated')),
        CONSTRAINT CK_HospitalMicroCultures_Final CHECK (Status <> 'Final' OR FinalAt IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalMicroCultures_CompanyId_LabOrderId ON dbo.HospitalMicroCultures (CompanyId, LabOrderId);
    CREATE INDEX IX_HospitalMicroCultures_CompanyId_PatientId ON dbo.HospitalMicroCultures (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalMicroIsolates','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroIsolates (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CultureId int NOT NULL,
        PatientId int NOT NULL,
        No int NOT NULL,
        OrganismId int NOT NULL,
        Quantity nvarchar(60) NULL,
        Mdro nvarchar(10) NULL,
        Comment nvarchar(300) NULL,
        CONSTRAINT PK_HospitalMicroIsolates PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMicroIsolates_Culture FOREIGN KEY (CultureId) REFERENCES dbo.HospitalMicroCultures (ID),
        CONSTRAINT FK_HospitalMicroIsolates_Organism FOREIGN KEY (OrganismId) REFERENCES dbo.HospitalMicroOrganisms (ID),
        CONSTRAINT FK_HospitalMicroIsolates_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalMicroIsolates_Mdro CHECK (Mdro IS NULL OR Mdro IN ('MRSA','VRE','ESBL','CRE','CRAB','CRPA'))
    );
    CREATE UNIQUE INDEX IX_HospitalMicroIsolates_CompanyId_CultureId_No ON dbo.HospitalMicroIsolates (CompanyId, CultureId, No);
    CREATE INDEX IX_HospitalMicroIsolates_CompanyId_PatientId ON dbo.HospitalMicroIsolates (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalMicroSusceptibilities','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMicroSusceptibilities (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        IsolateId int NOT NULL,
        AntibioticId int NOT NULL,
        Result nvarchar(1) NOT NULL,
        Mic decimal(9,3) NULL,
        Reported bit NOT NULL,
        CONSTRAINT PK_HospitalMicroSusceptibilities PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMicroSusceptibilities_Isolate FOREIGN KEY (IsolateId) REFERENCES dbo.HospitalMicroIsolates (ID),
        CONSTRAINT FK_HospitalMicroSusceptibilities_Antibiotic FOREIGN KEY (AntibioticId) REFERENCES dbo.HospitalMicroAntibiotics (ID),
        CONSTRAINT CK_HospitalMicroSusceptibilities_Result CHECK (Result IN ('S','I','R'))
    );
    CREATE UNIQUE INDEX IX_HospitalMicroSusceptibilities_CompanyId_IsolateId_AntibioticId ON dbo.HospitalMicroSusceptibilities (CompanyId, IsolateId, AntibioticId);
END

COMMIT TRANSACTION;
