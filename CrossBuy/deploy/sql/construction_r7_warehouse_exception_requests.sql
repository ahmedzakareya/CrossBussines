-- D-10 proposal receipts only. No policy seeds, approvals, source rewrites, stock or GL.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionPurchaseDrafts','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 THROW 52010,'Warehouse request prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionWarehouseExceptionRequests(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionWarehouseExceptionRequests PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,DraftId int NOT NULL,
 BudgetVersionId int NOT NULL,SourceWarehouseId int NOT NULL,DestinationSiteId int NOT NULL,
 RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
 SourceFactsVersion nvarchar(64) NOT NULL,CreationVersion nvarchar(64) NOT NULL,MappingVersion nvarchar(64) NOT NULL,PolicyToken varbinary(16) NOT NULL,
 Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionWarehouseExceptionRequests_Scope UNIQUE(ID,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionWarehouseExceptionRequests_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionRequests_Intent UNIQUE(CompanyID,PurchaseOrderId,DestinationSiteId,SourceVersion,CreatedBy),
 CONSTRAINT FK_ConstructionWarehouseExceptionRequests_Draft FOREIGN KEY(DraftId) REFERENCES dbo.ConstructionPurchaseDrafts(ID),
 CONSTRAINT FK_ConstructionWarehouseExceptionRequests_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
 CONSTRAINT FK_ConstructionWarehouseExceptionRequests_Site FOREIGN KEY(DestinationSiteId,ProjectId,CompanyID) REFERENCES dbo.ConstructionSites(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionWarehouseExceptionRequests_Warehouse FOREIGN KEY(SourceWarehouseId,CompanyID) REFERENCES dbo.Warehouses(ID,CompanyID),
 CONSTRAINT FK_ConstructionWarehouseExceptionRequests_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionWarehouseExceptionRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
  AND LEN(LTRIM(RTRIM(Reason)))>0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64
  AND LEN(SourceFactsVersion)=64 AND LEN(CreationVersion)=64 AND LEN(MappingVersion)=64 AND DATALENGTH(PolicyToken)=16
  AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests'))<>18
 OR COL_LENGTH('dbo.ConstructionWarehouseExceptionRequests','PolicyToken')<>16
 OR COL_LENGTH('dbo.ConstructionWarehouseExceptionRequests','RequestHash')<>128
 OR COL_LENGTH('dbo.ConstructionWarehouseExceptionRequests','Reason')<>2000
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests') AND is_disabled=0 AND is_not_trusted=0)<>5
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests') AND is_disabled=0 AND is_not_trusted=0)<>1
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionRequests') AND is_unique=1 AND is_disabled=0)<>4
 THROW 52010,'Warehouse request schema mismatch; no automatic repair.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionRequests_Immutable ON dbo.ConstructionWarehouseExceptionRequests
INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 52011,'Warehouse exception requests are immutable proposal receipts.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionRequests_Proof ON dbo.ConstructionWarehouseExceptionRequests
AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 -- Relational receipt proof only. Full source hash and mapping witness are re-read by the locked service.
 IF EXISTS(SELECT 1 FROM inserted i
  LEFT JOIN dbo.ConstructionPurchaseDrafts d ON d.ID=i.DraftId
  LEFT JOIN dbo.PurchaseOrders o ON o.ID=i.PurchaseOrderId AND o.CompanyID=i.CompanyID
  LEFT JOIN dbo.ConstructionSites s ON s.ID=i.DestinationSiteId AND s.CompanyID=i.CompanyID AND s.ProjectId=i.ProjectId
  LEFT JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId AND b.ProjectId=i.ProjectId AND b.CompanyID=i.CompanyID
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CrossSiteWarehouse'
  WHERE d.ID IS NULL OR d.CompanyID<>i.CompanyID OR d.ProjectId<>i.ProjectId OR d.PurchaseOrderId<>i.PurchaseOrderId
   OR d.BudgetVersionId<>i.BudgetVersionId OR d.InitialSourceVersion<>i.CreationVersion
   OR o.ID IS NULL OR o.ProjectId IS NULL OR o.ProjectId<>i.ProjectId OR o.WarehouseId IS NULL OR o.WarehouseId<>i.SourceWarehouseId OR o.Status<>N'Draft'
   OR s.ID IS NULL OR s.IsActive<>1 OR s.WarehouseId IS NULL OR s.WarehouseId=i.SourceWarehouseId
   OR b.ID IS NULL OR b.Status<>N'Approved' OR b.IsBaseline<>1
   OR p.Mode IS NULL OR p.Mode<>N'ApprovedException' OR p.ConcurrencyToken<>i.PolicyToken)
  THROW 52012,'Warehouse request source, destination, baseline or explicit policy is inconsistent.',1;
END;
GO
COMMIT;
