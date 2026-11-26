-- Immutable independent consent/refusal only. No effective budget, seed or balance rewrite.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionBudgetTransferRequests','U') IS NULL OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 THROW 51960,'Transfer decision prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionBudgetTransferDecisions','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionBudgetTransferDecisions(
 ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionBudgetTransferDecisions PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,TransferRequestId int NOT NULL,Outcome nvarchar(16) NOT NULL,
 DecisionId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
 Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionBudgetTransferDecisions_Scope UNIQUE(ID,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionBudgetTransferDecisions_Key UNIQUE(CompanyID,DecisionId),
 CONSTRAINT UQ_ConstructionBudgetTransferDecisions_Request UNIQUE(CompanyID,TransferRequestId),
 CONSTRAINT FK_ConstructionBudgetTransferDecisions_Request FOREIGN KEY(TransferRequestId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetTransferRequests(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionBudgetTransferDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND Outcome IN(N'Approved',N'Rejected')
  AND DecisionId<>'00000000-0000-0000-0000-000000000000' AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0)
 );
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionBudgetTransferDecisions'))<>11
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferDecisions') AND is_disabled=0 AND is_not_trusted=0)<>1
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferDecisions') AND is_disabled=0 AND is_not_trusted=0)<>1
 THROW 51960,'Transfer decision schema mismatch; no automatic repair performed.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferDecisions_Immutable ON dbo.ConstructionBudgetTransferDecisions
INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51961,'Transfer decisions are immutable; execution requires separate effective-version evidence.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferDecisions_Scope ON dbo.ConstructionBudgetTransferDecisions
AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionBudgetTransferRequests r ON r.ID=i.TransferRequestId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=r.BudgetVersionId
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'BudgetTransfer'
  WHERE i.CompanyID<>r.CompanyID OR i.ProjectId<>r.ProjectId OR i.CreatedBy=r.CreatedBy OR i.SourceVersion<>r.SourceVersion
   OR (i.Outcome=N'Approved' AND (b.IsBaseline<>1 OR b.Status<>N'Approved' OR p.Mode IS NULL OR p.Mode<>N'IndependentApproval' OR p.ConcurrencyToken<>r.PolicyToken)))
  THROW 51962,'Transfer decision actor, scope, baseline or policy mismatch.',1;
END;
GO
COMMIT;
