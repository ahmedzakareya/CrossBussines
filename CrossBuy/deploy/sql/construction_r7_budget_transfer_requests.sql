-- Immutable intent only. No approval, effective transfer, seed or balance rewrite.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL OR OBJECT_ID('dbo.ConstructionBudgetLines','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 THROW 51950,'Budget transfer request prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionBudgetTransferRequests','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionBudgetTransferRequests(
 ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionBudgetTransferRequests PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,BudgetVersionId int NOT NULL,
 FromLineId int NOT NULL,ToLineId int NOT NULL,Amount decimal(28,8) NOT NULL,
 FromBefore decimal(28,8) NOT NULL,ToBefore decimal(28,8) NOT NULL,FromAfter decimal(28,8) NOT NULL,ToAfter decimal(28,8) NOT NULL,
 RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
 PolicyToken varbinary(16) NOT NULL,Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionBudgetTransferRequests_Scope UNIQUE(ID,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionBudgetTransferRequests_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT FK_ConstructionBudgetTransferRequests_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionBudgetTransferRequests_From FOREIGN KEY(FromLineId) REFERENCES dbo.ConstructionBudgetLines(ID),
 CONSTRAINT FK_ConstructionBudgetTransferRequests_To FOREIGN KEY(ToLineId) REFERENCES dbo.ConstructionBudgetLines(ID),
 CONSTRAINT CK_ConstructionBudgetTransferRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND FromLineId<>ToLineId AND Amount>0
  AND FromBefore>=0 AND ToBefore>=0 AND FromAfter>=0 AND ToAfter>=0 AND FromAfter=FromBefore-Amount AND ToAfter=ToBefore+Amount
  AND LEN(LTRIM(RTRIM(Reason)))>0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND DATALENGTH(PolicyToken)=16
  AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionBudgetTransferRequests'))<>18
 OR COL_LENGTH('dbo.ConstructionBudgetTransferRequests','PolicyToken')<>16
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferRequests') AND is_disabled=0 AND is_not_trusted=0)<>3
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferRequests') AND is_disabled=0 AND is_not_trusted=0)<>1
 THROW 51950,'Budget transfer request schema mismatch; no automatic repair performed.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferRequests_Immutable ON dbo.ConstructionBudgetTransferRequests
INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51951,'Transfer requests are immutable; approval and execution require separate evidence.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferRequests_Scope ON dbo.ConstructionBudgetTransferRequests
AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId
  JOIN dbo.ConstructionBudgetLines f ON f.ID=i.FromLineId
  JOIN dbo.ConstructionBudgetLines t ON t.ID=i.ToLineId
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'BudgetTransfer'
  WHERE b.CompanyID<>i.CompanyID OR b.ProjectId<>i.ProjectId OR b.Status<>N'Approved' OR b.IsBaseline<>1
   OR f.CompanyID<>i.CompanyID OR t.CompanyID<>i.CompanyID OR f.ProjectId<>i.ProjectId OR t.ProjectId<>i.ProjectId
   OR f.BudgetVersionId<>i.BudgetVersionId OR t.BudgetVersionId<>i.BudgetVersionId
   OR f.Amount<>i.FromBefore OR t.Amount<>i.ToBefore OR f.Quantity IS NOT NULL OR f.Rate IS NOT NULL OR t.Quantity IS NOT NULL OR t.Rate IS NOT NULL
   OR p.Mode IS NULL OR p.Mode<>N'IndependentApproval' OR p.ConcurrencyToken<>i.PolicyToken)
  THROW 51952,'Transfer request source, scope or policy changed.',1;
END;
GO
COMMIT;
