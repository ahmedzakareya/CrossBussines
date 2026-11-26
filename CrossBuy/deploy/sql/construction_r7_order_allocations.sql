-- R7/C2 immutable analytical PO-line allocations. No reservations, GL or stock writes.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL OR OBJECT_ID('dbo.PurchaseOrderInvoiceLinks','U') IS NULL
 THROW 51400,'Budget versions and procurement provenance must precede allocations.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.PurchaseOrderLines') AND name='AK_PurchaseOrderLines_ID_PurchaseOrderId')
 ALTER TABLE dbo.PurchaseOrderLines ADD CONSTRAINT AK_PurchaseOrderLines_ID_PurchaseOrderId UNIQUE(ID,PurchaseOrderId);
IF OBJECT_ID('dbo.ConstructionOrderAllocations','U') IS NULL
 CREATE TABLE dbo.ConstructionOrderAllocations(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionOrderAllocations PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, PurchaseOrderId int NOT NULL, BudgetVersionId int NOT NULL,
  RevisionNo int NOT NULL, RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL,
  SourceVersion nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL,
  CreatedAt datetime2 NOT NULL, IsSealed bit NOT NULL,
  CONSTRAINT AK_ConstructionOrderAllocations_ID_PurchaseOrderId_ProjectId_CompanyID UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionOrderAllocations_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionOrderAllocations_Revision UNIQUE(CompanyID,PurchaseOrderId,RevisionNo),
  CONSTRAINT CK_ConstructionOrderAllocations_Identity CHECK(CompanyID>0 AND ProjectId>0 AND RevisionNo>0 AND CreatedBy>0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0),
  CONSTRAINT FK_ConstructionOrderAllocations_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
  CONSTRAINT FK_ConstructionOrderAllocations_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID)
 );
IF OBJECT_ID('dbo.ConstructionOrderAllocationLines','U') IS NULL
 CREATE TABLE dbo.ConstructionOrderAllocationLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionOrderAllocationLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, AllocationId int NOT NULL, PurchaseOrderId int NOT NULL,
  SourceLineId int NOT NULL, WbsNodeId int NOT NULL, CostCodeId int NOT NULL, CostCategory nvarchar(30) NOT NULL,
  NetDocumentAmount decimal(28,8) NOT NULL,
  CONSTRAINT UQ_ConstructionOrderAllocationLines_Dimension UNIQUE(AllocationId,SourceLineId,WbsNodeId,CostCodeId),
  CONSTRAINT CK_ConstructionOrderAllocationLines_Amount CHECK(NetDocumentAmount>=0),
  CONSTRAINT FK_ConstructionOrderAllocationLines_Header FOREIGN KEY(AllocationId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionOrderAllocations(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionOrderAllocationLines_Source FOREIGN KEY(SourceLineId,PurchaseOrderId) REFERENCES dbo.PurchaseOrderLines(ID,PurchaseOrderId),
  CONSTRAINT FK_ConstructionOrderAllocationLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionOrderAllocationLines_Code FOREIGN KEY(CostCodeId,CompanyID,CostCategory) REFERENCES dbo.ConstructionCostCodes(ID,CompanyID,Category)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionOrderAllocations_Insert ON dbo.ConstructionOrderAllocations AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId
  WHERE i.IsSealed=1 OR p.CompanyID<>i.CompanyID OR p.ProjectId IS NULL OR p.ProjectId<>i.ProjectId
   OR b.CompanyID<>i.CompanyID OR b.ProjectId<>i.ProjectId OR b.Status<>'Approved' OR b.IsBaseline<>1
   OR p.CurrencyId IS NULL OR p.CurrencyId<>b.CurrencyId OR p.Status NOT IN('Draft','Approved','Received'))
  THROW 51401,'Allocation source must be an open order in the approved project baseline currency.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionOrderAllocations_Seal ON dbo.ConstructionOrderAllocations AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ID=d.ID WHERE i.ID IS NULL OR d.IsSealed=1 OR i.IsSealed<>1)
  THROW 51402,'Allocation history is immutable; only sealing a new revision is permitted.',1;
 IF EXISTS(SELECT ID,CompanyID,ProjectId,PurchaseOrderId,BudgetVersionId,RevisionNo,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt FROM inserted
  EXCEPT SELECT ID,CompanyID,ProjectId,PurchaseOrderId,BudgetVersionId,RevisionNo,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt FROM deleted)
  THROW 51402,'Allocation identities and source snapshots cannot be rewritten.',1;
 IF EXISTS(SELECT 1 FROM inserted i WHERE NOT EXISTS(SELECT 1 FROM dbo.PurchaseOrderLines p WHERE p.PurchaseOrderId=i.PurchaseOrderId)
  OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderLines p WHERE p.PurchaseOrderId=i.PurchaseOrderId AND
   (NOT EXISTS(SELECT 1 FROM dbo.ConstructionOrderAllocationLines a WHERE a.AllocationId=i.ID AND a.SourceLineId=p.ID)
    OR p.LineTotal<>(SELECT SUM(a.NetDocumentAmount) FROM dbo.ConstructionOrderAllocationLines a WHERE a.AllocationId=i.ID AND a.SourceLineId=p.ID))))
  THROW 51403,'Every source line must reconcile before allocation sealing.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionOrderAllocationLines_Insert ON dbo.ConstructionOrderAllocationLines AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionOrderAllocations h ON h.ID=i.AllocationId
  JOIN dbo.ConstructionWbsNodes w ON w.ID=i.WbsNodeId
  JOIN dbo.ConstructionCostCodes c ON c.ID=i.CostCodeId
  WHERE h.IsSealed=1 OR w.Status<>'Active' OR w.OwnsBudget<>1 OR w.OwnsCost<>1 OR c.IsActive<>1 OR c.AllowDirectPosting<>1
   OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionBudgetLines b WHERE b.BudgetVersionId=h.BudgetVersionId
    AND b.CompanyID=i.CompanyID AND b.ProjectId=i.ProjectId AND b.WbsNodeId=i.WbsNodeId AND b.CostCodeId=i.CostCodeId AND b.CostCategory=i.CostCategory))
  THROW 51404,'Allocation dimensions must belong to an unsealed revision and its approved baseline.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionOrderAllocationLines_Immutable ON dbo.ConstructionOrderAllocationLines AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51402,'Allocation line history cannot be changed or deleted.',1;
END;
GO
COMMIT TRANSACTION;
