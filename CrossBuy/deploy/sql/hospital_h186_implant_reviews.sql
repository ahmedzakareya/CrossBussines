-- ==========================================================================================
-- Hospital H186 - implant traceability and peri-implant maintenance.
--
--   HospitalImplants         + Ref (catalogue reference / UDI), GraftMaterial, GraftLot, AbutmentLot
--   HospitalImplantReviews   (new) the peri-implant review of a restored implant, classified (2017 World Workshop)
--
-- Nullable columns and a new table; nothing existing changes meaning. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalImplants','U') IS NULL
    THROW 51986, 'Run the dental cases slice (hospital_h17_dental_cases.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalImplants', 'Ref') IS NULL ALTER TABLE dbo.HospitalImplants ADD Ref nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'GraftMaterial') IS NULL ALTER TABLE dbo.HospitalImplants ADD GraftMaterial nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'GraftLot') IS NULL ALTER TABLE dbo.HospitalImplants ADD GraftLot nvarchar(40) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'AbutmentLot') IS NULL ALTER TABLE dbo.HospitalImplants ADD AbutmentLot nvarchar(40) NULL;

IF OBJECT_ID('dbo.HospitalImplantReviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImplantReviews (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ImplantId int NOT NULL,
        PatientId int NOT NULL,
        ReviewedOn date NOT NULL,
        MaxProbingMm decimal(4,1) NOT NULL,
        Bleeding bit NOT NULL,
        Suppuration bit NOT NULL,
        BoneLossMm decimal(4,1) NULL,
        Mobility bit NOT NULL,
        Status nvarchar(16) NOT NULL,
        Note nvarchar(300) NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalImplantReviews PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalImplantReviews_Implant FOREIGN KEY (ImplantId) REFERENCES dbo.HospitalImplants (ID),
        CONSTRAINT FK_HospitalImplantReviews_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalImplantReviews_Status CHECK (Status IN ('Health', 'Mucositis', 'Periimplantitis', 'Failing'))
    );
    CREATE INDEX IX_HospitalImplantReviews_CompanyId_ImplantId_ReviewedOn ON dbo.HospitalImplantReviews (CompanyId, ImplantId, ReviewedOn);
END

COMMIT TRANSACTION;
