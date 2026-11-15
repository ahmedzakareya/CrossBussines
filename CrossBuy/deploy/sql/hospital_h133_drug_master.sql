-- ==========================================================================================
-- Hospital H133 - the drug master (formulary). One row per medicine, hung off its inventory item:
-- generic name (ar/en), Tall Man spelling, strength + unit, form, routes, ATC, therapeutic class, formulary status
-- with its restriction note, high-alert, look-alike/sound-alike names, controlled schedule, hazardous, cold chain,
-- VEN class.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPharmacySettings','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDrugs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDrugs (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ItemId int NOT NULL,
        GenericName nvarchar(150) NOT NULL,
        GenericNameEn nvarchar(150) NULL,
        TallMan nvarchar(150) NULL,
        Strength decimal(18,4) NULL,
        StrengthUnit nvarchar(10) NULL,
        Form nvarchar(20) NULL,
        Routes nvarchar(60) NULL,
        Atc nvarchar(10) NULL,
        TherapeuticClass nvarchar(120) NULL,
        FormularyStatus nvarchar(20) NOT NULL,
        RestrictionNote nvarchar(300) NULL,
        HighAlert bit NOT NULL,
        LasaWith nvarchar(200) NULL,
        ControlledSchedule nvarchar(20) NULL,
        Hazardous bit NOT NULL,
        ColdChain bit NOT NULL,
        Ven nvarchar(1) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDrugs PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDrugs_CompanyId_ItemId ON dbo.HospitalDrugs (CompanyId, ItemId);
    CREATE INDEX IX_HospitalDrugs_CompanyId_FormularyStatus ON dbo.HospitalDrugs (CompanyId, FormularyStatus);
END

COMMIT TRANSACTION;
