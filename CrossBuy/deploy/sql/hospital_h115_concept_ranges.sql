-- ==========================================================================================
-- Hospital H115 — reference ranges by who the patient is. A concept's own normal / critical limits are one range for
-- everybody; these are the extra ranges for a sex, an age band (in days) and/or a pregnancy (any / a trimester / not
-- pregnant). The most specific range that fits the patient replaces the concept's own limits for that reading.
--   HospitalConceptRanges   one range of one concept
--
-- Creates one table; changes no data. Idempotent.
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

IF OBJECT_ID('dbo.HospitalConcepts','U') IS NULL
    THROW 51990, 'Run hospital_h93_clinical_content.sql first.', 1;

IF OBJECT_ID('dbo.HospitalConceptRanges','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConceptRanges (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        ConceptId             int            NOT NULL,
        Sex                   nvarchar(1)    NULL,
        AgeFromDays           int            NULL,
        AgeToDays             int            NULL,
        Pregnancy             nvarchar(3)    NULL,
        NormalLow             decimal(18,4)  NULL,
        NormalHigh            decimal(18,4)  NULL,
        CriticalLow           decimal(18,4)  NULL,
        CriticalHigh          decimal(18,4)  NULL,
        NormalHigh2           decimal(18,4)  NULL,
        CriticalHigh2         decimal(18,4)  NULL,
        Source                nvarchar(200)  NULL,
        UpdatedAt             datetime2      NOT NULL,
        UpdatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalConceptRanges PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalConceptRanges_Concept FOREIGN KEY (ConceptId) REFERENCES dbo.HospitalConcepts(ID),
        CONSTRAINT CK_HospitalConceptRanges_Sex CHECK (Sex IS NULL OR Sex IN ('M','F')),
        CONSTRAINT CK_HospitalConceptRanges_Pregnancy CHECK (Pregnancy IS NULL OR Pregnancy IN ('No','Any','T1','T2','T3')),
        CONSTRAINT CK_HospitalConceptRanges_PregnantIsFemale CHECK (Pregnancy IS NULL OR Pregnancy = 'No' OR Sex IS NULL OR Sex = 'F'),
        CONSTRAINT CK_HospitalConceptRanges_Age CHECK ((AgeFromDays IS NULL OR AgeFromDays >= 0) AND (AgeToDays IS NULL OR AgeToDays >= 0)
            AND (AgeFromDays IS NULL OR AgeToDays IS NULL OR AgeToDays > AgeFromDays)),
        CONSTRAINT CK_HospitalConceptRanges_SaysWho CHECK (Sex IS NOT NULL OR Pregnancy IS NOT NULL OR AgeFromDays IS NOT NULL OR AgeToDays IS NOT NULL),
        CONSTRAINT CK_HospitalConceptRanges_HasLimit CHECK (NormalLow IS NOT NULL OR NormalHigh IS NOT NULL OR CriticalLow IS NOT NULL OR CriticalHigh IS NOT NULL)
    );
    CREATE INDEX IX_HospitalConceptRanges_Concept ON dbo.HospitalConceptRanges (CompanyId, ConceptId);
END

COMMIT TRANSACTION;
