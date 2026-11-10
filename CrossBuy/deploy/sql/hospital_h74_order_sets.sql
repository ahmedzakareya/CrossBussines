-- ==========================================================================================
-- Hospital H74 — order sets: a named protocol of medicines and nursing tasks a doctor places for an inpatient in one
-- step. Phase 1 of the 2026-11-09 gap research.
--   HospitalOrderSets      the protocol (code, name in both languages, department)
--   HospitalOrderSetItems  its medicines and nursing tasks, each ticked or not when the set is opened
--   HospitalOrderSetUses   a set placed for an admission (how much of it, by whom)
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards). No clinical content is shipped: the hospital writes
-- its own sets.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalOrderSets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrderSets (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(20)   NOT NULL,
        Name                 nvarchar(150)  NOT NULL,
        NameEn               nvarchar(150)  NOT NULL,
        DepartmentId         int            NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalOrderSets PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalOrderSets_CompanyId_Code ON dbo.HospitalOrderSets (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalOrderSetItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrderSetItems (
        ID           int IDENTITY(1,1) NOT NULL,
        CompanyId    int            NOT NULL,
        SetId        int            NOT NULL,
        SortOrder    int            NOT NULL,
        Kind         nvarchar(5)    NOT NULL,
        Drug         nvarchar(200)  NULL,
        Dose         nvarchar(60)   NULL,
        Route        nvarchar(10)   NULL,
        Hours        nvarchar(60)   NULL,
        AsNeeded     bit            NOT NULL,
        Note         nvarchar(300)  NULL,
        TitleAr      nvarchar(90)   NULL,
        TitleEn      nvarchar(90)   NULL,
        DueInHours   int            NULL,
        Preselected  bit            NOT NULL,
        CONSTRAINT PK_HospitalOrderSetItems PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalOrderSetItems_Set FOREIGN KEY (SetId) REFERENCES dbo.HospitalOrderSets(ID),
        CONSTRAINT CK_HospitalOrderSetItems_Kind CHECK (Kind IN ('Med','Task')),
        -- a medicine names its drug, dose and route; a task its words (in one language at least) and when it is due
        CONSTRAINT CK_HospitalOrderSetItems_Shape CHECK (
            (Kind = 'Med' AND Drug IS NOT NULL AND Dose IS NOT NULL AND Route IS NOT NULL)
            OR (Kind = 'Task' AND (TitleAr IS NOT NULL OR TitleEn IS NOT NULL) AND DueInHours BETWEEN 0 AND 72))
    );
    CREATE INDEX IX_HospitalOrderSetItems_CompanyId_SetId ON dbo.HospitalOrderSetItems (CompanyId, SetId);
END

IF OBJECT_ID('dbo.HospitalOrderSetUses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOrderSetUses (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int            NOT NULL,
        SetId         int            NOT NULL,
        AdmissionId   int            NOT NULL,
        PatientId     int            NOT NULL,
        ItemsPlaced   int            NOT NULL,
        ItemsInSet    int            NOT NULL,
        PlacedAt      datetime2      NOT NULL,
        ByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalOrderSetUses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalOrderSetUses_Set FOREIGN KEY (SetId) REFERENCES dbo.HospitalOrderSets(ID),
        CONSTRAINT FK_HospitalOrderSetUses_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalOrderSetUses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE INDEX IX_HospitalOrderSetUses_CompanyId_SetId ON dbo.HospitalOrderSetUses (CompanyId, SetId);
END

COMMIT TRANSACTION;
