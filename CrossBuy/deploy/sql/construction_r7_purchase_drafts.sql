-- R7/C2: persisted non-executable proposals; identity and provenance only, no historical backfill.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.PurchaseOrders','U') IS NULL OR OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 THROW 51900,'Apply construction and purchase-order foundations first.',1;
IF OBJECT_ID('dbo.ConstructionPurchaseDrafts','U') IS NULL
 CREATE TABLE dbo.ConstructionPurchaseDrafts(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPurchaseDrafts PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,BudgetVersionId int NOT NULL,
  CurrencyId int NOT NULL,CurrencyDecimals int NOT NULL,RequestId uniqueidentifier NOT NULL,
  RequestHash nvarchar(64) NOT NULL,InitialSourceVersion nvarchar(64) NOT NULL,Reason nvarchar(1000) NOT NULL,
  CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionPurchaseDrafts_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionPurchaseDrafts_Order UNIQUE(CompanyID,PurchaseOrderId),
  CONSTRAINT CK_ConstructionPurchaseDrafts_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND CurrencyDecimals BETWEEN 0 AND 4
   AND LEN(RequestHash)=64 AND LEN(InitialSourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0
   AND RequestId<>'00000000-0000-0000-0000-000000000000'),
  CONSTRAINT FK_ConstructionPurchaseDrafts_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
  CONSTRAINT FK_ConstructionPurchaseDrafts_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionPurchaseDrafts_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionPurchaseDrafts_Currency FOREIGN KEY(CurrencyId) REFERENCES dbo.Currencies(ID)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseDrafts_Insert ON dbo.ConstructionPurchaseDrafts AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId
  LEFT JOIN dbo.ConstructionBudgetPolicies policy ON policy.CompanyID=i.CompanyID
  WHERE p.ProjectId IS NULL OR p.ProjectId<>i.ProjectId OR p.Status<>'Draft' OR p.CurrencyId IS NULL OR p.CurrencyId<>i.CurrencyId
   OR p.ExchangeRate IS NULL OR p.ExchangeRate<>1 OR b.Status<>'Approved' OR b.IsBaseline<>1 OR b.CurrencyId<>i.CurrencyId
   OR policy.Mode IS NULL OR policy.Mode NOT IN('Warning','ApprovalRequired','HardBlock')
   OR NOT EXISTS(SELECT 1 FROM dbo.PurchaseOrderLines l WHERE l.PurchaseOrderId=p.ID)
   OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderLines l WHERE l.PurchaseOrderId=p.ID AND l.ReceivedQty<>0)
   OR EXISTS(SELECT 1 FROM dbo.GoodsReceipts g WHERE g.CompanyID=i.CompanyID AND g.PurchaseOrderId=p.ID)
   OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderInvoiceLinks link WHERE link.CompanyID=i.CompanyID AND link.PurchaseOrderId=p.ID)
   OR EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReservations r WHERE r.CompanyID=i.CompanyID AND r.PurchaseOrderId=p.ID))
  THROW 51901,'A draft receipt requires its non-executable, unreceived source and approved baseline with explicit policy.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseDrafts_Immutable ON dbo.ConstructionPurchaseDrafts AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51902,'Draft creation identity and provenance are immutable.',1;
END;
GO
-- Fail closed until the atomic admitted source-transition contract is implemented and qualified.
-- Existing ordinary purchase orders, which have no construction draft receipt, keep their lifecycle.
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseDrafts_StateGuard ON dbo.PurchaseOrders AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseDrafts d ON d.PurchaseOrderId=i.ID
  WHERE i.CompanyID<>d.CompanyID OR i.ProjectId IS NULL OR i.ProjectId<>d.ProjectId
   OR i.CurrencyId IS NULL OR i.CurrencyId<>d.CurrencyId OR i.ExchangeRate IS NULL OR i.ExchangeRate<>1
   OR i.Status NOT IN('Draft','Cancelled'))
  THROW 51903,'Construction draft execution requires the qualified atomic admission transition.',1;
END;
GO
COMMIT TRANSACTION;
