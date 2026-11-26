-- Explicit company choices only; no seed, default mode or operational activation.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 CREATE TABLE dbo.ConstructionCompanyPolicies(
 CompanyID int NOT NULL,PolicyKey nvarchar(40) NOT NULL,Mode nvarchar(40) NOT NULL,
 Reason nvarchar(1000) NOT NULL,UpdatedBy int NOT NULL,UpdatedAt datetime2 NOT NULL,
 ConcurrencyToken varbinary(16) NOT NULL,
 CONSTRAINT PK_ConstructionCompanyPolicies PRIMARY KEY(CompanyID,PolicyKey),
 CONSTRAINT CK_ConstructionCompanyPolicies_Mode CHECK(
  (PolicyKey=N'BudgetTransfer' AND Mode IN(N'Disabled',N'IndependentApproval')) OR
  (PolicyKey=N'CrossSiteWarehouse' AND Mode IN(N'Restricted',N'ApprovedException')) OR
  (PolicyKey=N'CostForecast' AND Mode IN(N'Disabled',N'ManualAuthorOwned'))),
 CONSTRAINT CK_ConstructionCompanyPolicies_Values CHECK(CompanyID>0 AND UpdatedBy>0 AND
 LEN(LTRIM(RTRIM(Reason)))>0 AND DATALENGTH(ConcurrencyToken)=16)
 );
IF COL_LENGTH('dbo.ConstructionCompanyPolicies','ConcurrencyToken') IS NULL
 THROW 51940,'Company policy schema mismatch; no automatic repair performed.',1;
COMMIT;
