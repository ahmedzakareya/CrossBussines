-- ==========================================================================================
-- Hospital H93 — the clinical content platform the specialty clinics are built on.
--   HospitalConcepts                   the concept dictionary: every measurable thing defined once
--   HospitalClinicalTemplates          a specialty template (its clinic)
--   HospitalClinicalTemplateVersions   its versions: a draft is edited, a published one is frozen
--   HospitalClinicalTemplateFields     the fields of one version, each from the dictionary
--   HospitalClinicalForms              one filled template on one visit, kept with its version
--   HospitalClinicalObservations       every signed value as one row — what trends, alerts and care gaps read
--
-- Additive only (six new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalDepartments','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h4_outpatient.sql and hospital_h5_consultation.sql first.', 1;

IF OBJECT_ID('dbo.HospitalConcepts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConcepts (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(30)   NOT NULL,
        CodeSystem           nvarchar(10)   NOT NULL,
        Name                 nvarchar(150)  NOT NULL,
        NameEn               nvarchar(150)  NOT NULL,
        ValueType            nvarchar(12)   NOT NULL,
        Unit                 nvarchar(30)   NULL,
        Decimals             int            NOT NULL,
        NormalLow            decimal(18,4)  NULL,
        NormalHigh           decimal(18,4)  NULL,
        NormalHigh2          decimal(18,4)  NULL,
        CriticalLow          decimal(18,4)  NULL,
        CriticalHigh         decimal(18,4)  NULL,
        CriticalHigh2        decimal(18,4)  NULL,
        NormalText           nvarchar(60)   NULL,
        NormalTextEn         nvarchar(60)   NULL,
        CriticalText         nvarchar(60)   NULL,
        CriticalTextEn       nvarchar(60)   NULL,
        Choices              nvarchar(2000) NULL,
        Calculator           nvarchar(20)   NULL,
        CalcInputs           nvarchar(200)  NULL,
        HasLaterality        bit            NOT NULL,
        LabAnalyteCode       nvarchar(20)   NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalConcepts PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalConcepts_ValueType CHECK (ValueType IN ('Number','Panel','Calculated','Score','Choice')),
        CONSTRAINT CK_HospitalConcepts_CodeSystem CHECK (CodeSystem IN ('LOINC','SNOMED','Local')),
        CONSTRAINT CK_HospitalConcepts_Calculator CHECK (Calculator IS NULL OR Calculator IN ('Bmi','EgfrCkdEpi2021','Kdigo','Sum')),
        CONSTRAINT CK_HospitalConcepts_Decimals CHECK (Decimals BETWEEN 0 AND 4)
    );
    CREATE UNIQUE INDEX IX_HospitalConcepts_CompanyId_Code ON dbo.HospitalConcepts (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalClinicalTemplates','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalTemplates (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(20)   NOT NULL,
        Name                 nvarchar(150)  NOT NULL,
        NameEn               nvarchar(150)  NOT NULL,
        DepartmentId         int            NULL,
        IsActive             bit            NOT NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalClinicalTemplates PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalTemplates_Department FOREIGN KEY (DepartmentId) REFERENCES dbo.HospitalDepartments(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalClinicalTemplates_CompanyId_Code ON dbo.HospitalClinicalTemplates (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalClinicalTemplateVersions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalTemplateVersions (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        TemplateId             int            NOT NULL,
        Version                int            NOT NULL,
        Status                 nvarchar(10)   NOT NULL,
        ChangeNote             nvarchar(300)  NULL,
        CreatedAt              datetime2      NOT NULL,
        CreatedByEmployeeId    int            NULL,
        PublishedAt            datetime2      NULL,
        PublishedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalClinicalTemplateVersions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalTemplateVersions_Template FOREIGN KEY (TemplateId) REFERENCES dbo.HospitalClinicalTemplates(ID),
        CONSTRAINT CK_HospitalClinicalTemplateVersions_Status CHECK (Status IN ('Draft','Published','Retired')),
        CONSTRAINT CK_HospitalClinicalTemplateVersions_Published CHECK ((Status = 'Draft' AND PublishedAt IS NULL) OR (Status <> 'Draft' AND PublishedAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalClinicalTemplateVersions_TemplateId_Version ON dbo.HospitalClinicalTemplateVersions (TemplateId, Version);
    -- at most one draft and one published version of a template at a time
    CREATE UNIQUE INDEX UX_HospitalClinicalTemplateVersions_OneDraft ON dbo.HospitalClinicalTemplateVersions (TemplateId) WHERE Status = 'Draft';
    CREATE UNIQUE INDEX UX_HospitalClinicalTemplateVersions_OnePublished ON dbo.HospitalClinicalTemplateVersions (TemplateId) WHERE Status = 'Published';
END

IF OBJECT_ID('dbo.HospitalClinicalTemplateFields','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalTemplateFields (
        ID               int IDENTITY(1,1) NOT NULL,
        CompanyId        int            NOT NULL,
        VersionId        int            NOT NULL,
        SortOrder        int            NOT NULL,
        Section          nvarchar(100)  NOT NULL,
        SectionEn        nvarchar(100)  NOT NULL,
        ConceptId        int            NOT NULL,
        Label            nvarchar(200)  NULL,
        LabelEn          nvarchar(200)  NULL,
        Required         bit            NOT NULL,
        ValueSource      nvarchar(10)   NOT NULL,
        LookbackDays     int            NULL,
        ShowWhenFieldId  int            NULL,
        ShowWhenAbove    decimal(18,4)  NULL,
        Note             nvarchar(300)  NULL,
        NoteEn           nvarchar(300)  NULL,
        AlertAbove       decimal(18,4)  NULL,
        AlertBelow       decimal(18,4)  NULL,
        AlertText        nvarchar(150)  NULL,
        AlertTextEn      nvarchar(150)  NULL,
        CareGapDays      int            NULL,
        CONSTRAINT PK_HospitalClinicalTemplateFields PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalTemplateFields_Version FOREIGN KEY (VersionId) REFERENCES dbo.HospitalClinicalTemplateVersions(ID),
        CONSTRAINT FK_HospitalClinicalTemplateFields_Concept FOREIGN KEY (ConceptId) REFERENCES dbo.HospitalConcepts(ID),
        CONSTRAINT FK_HospitalClinicalTemplateFields_ShowWhen FOREIGN KEY (ShowWhenFieldId) REFERENCES dbo.HospitalClinicalTemplateFields(ID),
        CONSTRAINT CK_HospitalClinicalTemplateFields_ValueSource CHECK (ValueSource IN ('Manual','LastLab')),
        CONSTRAINT CK_HospitalClinicalTemplateFields_Days CHECK ((LookbackDays IS NULL OR LookbackDays BETWEEN 1 AND 3650) AND (CareGapDays IS NULL OR CareGapDays BETWEEN 1 AND 3650))
    );
    CREATE INDEX IX_HospitalClinicalTemplateFields_VersionId_SortOrder ON dbo.HospitalClinicalTemplateFields (VersionId, SortOrder);
END

IF OBJECT_ID('dbo.HospitalClinicalForms','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalForms (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        PatientId            int            NOT NULL,
        EncounterId          int            NOT NULL,
        TemplateVersionId    int            NOT NULL,
        Status               nvarchar(10)   NOT NULL,
        AnswersJson          nvarchar(max)  NOT NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        UpdatedAt            datetime2      NULL,
        SignedAt             datetime2      NULL,
        SignedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalClinicalForms PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalForms_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalClinicalForms_Encounter FOREIGN KEY (EncounterId) REFERENCES dbo.HospitalEncounters(ID),
        CONSTRAINT FK_HospitalClinicalForms_Version FOREIGN KEY (TemplateVersionId) REFERENCES dbo.HospitalClinicalTemplateVersions(ID),
        CONSTRAINT CK_HospitalClinicalForms_Status CHECK (Status IN ('Draft','Signed')),
        CONSTRAINT CK_HospitalClinicalForms_Signed CHECK ((Status = 'Draft' AND SignedAt IS NULL) OR (Status = 'Signed' AND SignedAt IS NOT NULL))
    );
    CREATE INDEX IX_HospitalClinicalForms_CompanyId_EncounterId ON dbo.HospitalClinicalForms (CompanyId, EncounterId);
    CREATE INDEX IX_HospitalClinicalForms_CompanyId_PatientId ON dbo.HospitalClinicalForms (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalClinicalObservations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalObservations (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PatientId             int            NOT NULL,
        ConceptId             int            NOT NULL,
        EncounterId           int            NULL,
        FormId                int            NULL,
        Value                 decimal(18,4)  NULL,
        Value2                decimal(18,4)  NULL,
        ValueCode             nvarchar(40)   NULL,
        Side                  nvarchar(1)    NULL,
        Unit                  nvarchar(30)   NULL,
        Flag                  nvarchar(2)    NOT NULL,
        ObservedAt            datetime2      NOT NULL,
        Source                nvarchar(6)    NOT NULL,
        RecordedAt            datetime2      NOT NULL,
        RecordedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalClinicalObservations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalObservations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalClinicalObservations_Concept FOREIGN KEY (ConceptId) REFERENCES dbo.HospitalConcepts(ID),
        CONSTRAINT FK_HospitalClinicalObservations_Form FOREIGN KEY (FormId) REFERENCES dbo.HospitalClinicalForms(ID),
        CONSTRAINT CK_HospitalClinicalObservations_Side CHECK (Side IS NULL OR Side IN ('R','L')),
        CONSTRAINT CK_HospitalClinicalObservations_Flag CHECK (Flag IN ('N','L','H','LL','HH')),
        CONSTRAINT CK_HospitalClinicalObservations_Source CHECK (Source IN ('Form','Lab')),
        CONSTRAINT CK_HospitalClinicalObservations_Value CHECK (Value IS NOT NULL OR ValueCode IS NOT NULL)
    );
    CREATE INDEX IX_HospitalClinicalObservations_Trend ON dbo.HospitalClinicalObservations (CompanyId, PatientId, ConceptId, ObservedAt);
    CREATE INDEX IX_HospitalClinicalObservations_CompanyId_FormId ON dbo.HospitalClinicalObservations (CompanyId, FormId);
END

COMMIT TRANSACTION;
