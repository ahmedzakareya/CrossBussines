-- Immutable independent consent/refusal only. No warehouse access grant, source rewrite, stock or GL.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests','U') IS NULL OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 THROW 52020,'Warehouse exception decision prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionDecisions','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionWarehouseExceptionDecisions(
 ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionWarehouseExceptionDecisions PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,ExceptionRequestId int NOT NULL,Outcome nvarchar(16) NOT NULL,
 DecisionId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceFactsVersion nvarchar(64) NOT NULL,
 Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionWarehouseExceptionDecisions_Scope UNIQUE(ID,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionWarehouseExceptionDecisions_Key UNIQUE(CompanyID,DecisionId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionDecisions_Request UNIQUE(CompanyID,ExceptionRequestId),
 CONSTRAINT FK_ConstructionWarehouseExceptionDecisions_Request FOREIGN KEY(ExceptionRequestId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWarehouseExceptionRequests(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionWarehouseExceptionDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND Outcome IN(N'Approved',N'Rejected')
  AND DecisionId<>'00000000-0000-0000-0000-000000000000' AND LEN(RequestHash)=64 AND LEN(SourceFactsVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0)
 );
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionDecisions'))<>11
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionDecisions') AND is_disabled=0 AND is_not_trusted=0)<>1
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionDecisions') AND is_disabled=0 AND is_not_trusted=0)<>1
 THROW 52020,'Warehouse exception decision schema mismatch; no automatic repair performed.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionDecisions_Immutable ON dbo.ConstructionWarehouseExceptionDecisions
INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 52021,'Warehouse exception decisions are immutable; consumption requires separate canonical writer evidence.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionDecisions_Scope ON dbo.ConstructionWarehouseExceptionDecisions
AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionWarehouseExceptionRequests r ON r.ID=i.ExceptionRequestId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=r.BudgetVersionId
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CrossSiteWarehouse'
  WHERE i.CompanyID<>r.CompanyID OR i.ProjectId<>r.ProjectId OR i.CreatedBy=r.CreatedBy OR i.SourceFactsVersion<>r.SourceFactsVersion
   OR (i.Outcome=N'Approved' AND (b.IsBaseline<>1 OR b.Status<>N'Approved' OR p.Mode IS NULL OR p.Mode<>N'ApprovedException' OR p.ConcurrencyToken<>r.PolicyToken)))
  THROW 52022,'Warehouse exception decision actor, scope, baseline or policy mismatch.',1;
END;
GO
COMMIT;
