-- ==========================================================================================
-- Hospital H182 - the payer's dental contract terms (from the dental review: "contractual insurance rules"): a waiting
-- period for major work counted from the policy's start, and per-procedure rules — covered at most once every N months
-- (on the same tooth), at most N times a year, or excluded. A line the contract does not pay shows why on the quotation.
--
--   HospitalDentalBenefits       + WaitingMonthsMajor (0 = none)
--   HospitalDentalBenefitRules   (new)
--
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDentalBenefits','U') IS NULL
    THROW 51978, 'Run hospital_h15_dental.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDentalBenefits', 'WaitingMonthsMajor') IS NULL
    ALTER TABLE dbo.HospitalDentalBenefits ADD WaitingMonthsMajor int NOT NULL CONSTRAINT DF_HospitalDentalBenefits_Waiting DEFAULT (0)
        CONSTRAINT CK_HospitalDentalBenefits_Waiting CHECK (WaitingMonthsMajor BETWEEN 0 AND 60);

IF OBJECT_ID('dbo.HospitalDentalBenefitRules','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalBenefitRules (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PayerId int NOT NULL,
        ServiceId int NOT NULL,
        FrequencyMonths int NULL,
        PerYear int NULL,
        Excluded bit NOT NULL,
        CONSTRAINT PK_HospitalDentalBenefitRules PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDentalBenefitRules_Frequency CHECK (FrequencyMonths IS NULL OR FrequencyMonths BETWEEN 1 AND 120),
        CONSTRAINT CK_HospitalDentalBenefitRules_PerYear CHECK (PerYear IS NULL OR PerYear BETWEEN 1 AND 52)
    );
    CREATE UNIQUE INDEX IX_HospitalDentalBenefitRules_CompanyId_PayerId_ServiceId ON dbo.HospitalDentalBenefitRules (CompanyId, PayerId, ServiceId);
END

COMMIT TRANSACTION;
