-- ==========================================================================================
-- Hospital H171 - the clinical rule register. Every rule the software applies to a patient names its guideline and the
-- version it implements (in code); the clinical lead approves a version, with the clinic's values where the rule has
-- them. Each approval is a row kept for good; the latest per rule is in force.
--
--   HospitalClinicalRuleApprovals   (new)
--
-- New table only; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalClinicalRuleApprovals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalRuleApprovals (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(40) NOT NULL,
        Version nvarchar(20) NOT NULL,
        ParamsJson nvarchar(1000) NULL,
        Note nvarchar(300) NULL,
        ApprovedByEmployeeId int NOT NULL,
        ApprovedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalClinicalRuleApprovals PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalClinicalRuleApprovals_Json CHECK (ParamsJson IS NULL OR ISJSON(ParamsJson) = 1)
    );
    CREATE INDEX IX_HospitalClinicalRuleApprovals_CompanyId_Code_ApprovedAt ON dbo.HospitalClinicalRuleApprovals (CompanyId, Code, ApprovedAt);
END

COMMIT TRANSACTION;
