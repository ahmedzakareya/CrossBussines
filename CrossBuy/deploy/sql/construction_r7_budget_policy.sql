-- R7 / D-21. Explicit per-company selection, no Mode default and no policy seed.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID('dbo.ConstructionBudgetPolicies','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionBudgetPolicies (
  CompanyID int NOT NULL CONSTRAINT PK_ConstructionBudgetPolicies PRIMARY KEY,
  Mode nvarchar(30) NOT NULL,
  Reason nvarchar(1000) NOT NULL,
  UpdatedBy int NULL,
  UpdatedAt datetime2 NOT NULL,
  ConcurrencyToken varbinary(16) NOT NULL,
  CONSTRAINT CK_ConstructionBudgetPolicies_Mode CHECK(Mode IN('Warning','ApprovalRequired','HardBlock')),
  CONSTRAINT CK_ConstructionBudgetPolicies_Reason CHECK(LEN(LTRIM(RTRIM(Reason)))>0),
  CONSTRAINT CK_ConstructionBudgetPolicies_Company CHECK(CompanyID>0)
 );
END;
