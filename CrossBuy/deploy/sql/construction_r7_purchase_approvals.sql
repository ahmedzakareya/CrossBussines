-- R7/C2: atomic approval identity for persisted construction proposals; no historical backfill.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionPurchaseDrafts','U') IS NULL OR OBJECT_ID('dbo.ConstructionBudgetExceptionConsumptions','U') IS NULL
 THROW 52000,'Apply the authored draft and budget exception foundations first.',1;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.InventoryApprovals') AND name='Amount' AND (scale>8 OR precision-scale>20))
 THROW 52000,'Approval amount precision cannot be narrowed.',1;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.InventoryApprovals') AND name='Amount' AND (precision<>28 OR scale<>8))
 ALTER TABLE dbo.InventoryApprovals ALTER COLUMN Amount decimal(28,8) NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.InventoryApprovals') AND name='AK_InventoryApprovals_ID_CompanyID')
 ALTER TABLE dbo.InventoryApprovals ADD CONSTRAINT AK_InventoryApprovals_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionPurchaseDrafts') AND name='AK_ConstructionPurchaseDrafts_ID_PurchaseOrderId_ProjectId_CompanyID')
 ALTER TABLE dbo.ConstructionPurchaseDrafts ADD CONSTRAINT AK_ConstructionPurchaseDrafts_ID_PurchaseOrderId_ProjectId_CompanyID UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID);
IF OBJECT_ID('dbo.ConstructionPurchaseApprovalRequests','U') IS NULL
 CREATE TABLE dbo.ConstructionPurchaseApprovalRequests(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPurchaseApprovalRequests PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,DraftId int NOT NULL,AllocationId int NOT NULL,ApprovalId int NOT NULL,ExceptionId int NULL,
 RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,ContentHash nvarchar(64) NOT NULL,
 PayloadJson nvarchar(max) NOT NULL,Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionPurchaseApprovalRequests_Source UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionPurchaseApprovalRequests_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionPurchaseApprovalRequests_Approval UNIQUE(CompanyID,ApprovalId),
 CONSTRAINT CK_ConstructionPurchaseApprovalRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(ContentHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0 AND RequestId<>'00000000-0000-0000-0000-000000000000'),
 CONSTRAINT FK_ConstructionPurchaseApprovalRequests_Draft FOREIGN KEY(DraftId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionPurchaseDrafts(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseApprovalRequests_Allocation FOREIGN KEY(AllocationId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionOrderAllocations(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseApprovalRequests_Approval FOREIGN KEY(ApprovalId,CompanyID) REFERENCES dbo.InventoryApprovals(ID,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseApprovalRequests_Exception FOREIGN KEY(ExceptionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetExceptionRequests(ID,ProjectId,CompanyID));
IF OBJECT_ID('dbo.ConstructionPurchaseAdmissions','U') IS NULL
 CREATE TABLE dbo.ConstructionPurchaseAdmissions(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPurchaseAdmissions PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,ApprovalRequestId int NOT NULL,ReservationId int NOT NULL,
 RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,BeforeVersion nvarchar(64) NOT NULL,ApprovedVersion nvarchar(64) NOT NULL,ContentHash nvarchar(64) NOT NULL,
 Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionPurchaseAdmissions_Request UNIQUE(CompanyID,RequestId),CONSTRAINT UQ_ConstructionPurchaseAdmissions_Source UNIQUE(CompanyID,PurchaseOrderId),
 CONSTRAINT UQ_ConstructionPurchaseAdmissions_Approval UNIQUE(ApprovalRequestId),
 CONSTRAINT CK_ConstructionPurchaseAdmissions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(RequestHash)=64 AND LEN(BeforeVersion)=64 AND LEN(ApprovedVersion)=64 AND LEN(ContentHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0 AND RequestId<>'00000000-0000-0000-0000-000000000000'),
 CONSTRAINT FK_ConstructionPurchaseAdmissions_Approval FOREIGN KEY(ApprovalRequestId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionPurchaseApprovalRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseAdmissions_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID) REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID));
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseApprovalRequests_Insert ON dbo.ConstructionPurchaseApprovalRequests AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseDrafts d ON d.ID=i.DraftId JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
  JOIN dbo.ConstructionOrderAllocations a ON a.ID=i.AllocationId JOIN dbo.InventoryApprovals ap ON ap.ID=i.ApprovalId
  WHERE p.Status<>'Draft' OR a.IsSealed<>1 OR a.SourceVersion<>i.SourceVersion OR a.BudgetVersionId<>d.BudgetVersionId
   OR d.InitialSourceVersion<>i.ContentHash OR ap.DocType<>'ConstructionPurchaseOrder' OR ap.Status<>'Pending'
   OR ap.RequestedByEmployeeId IS NULL OR ap.RequestedByEmployeeId<>i.CreatedBy OR ap.RequestedAt IS NULL OR ap.Amount<>p.GrandTotal
   OR ap.PayloadJson IS NULL OR DATALENGTH(ap.PayloadJson)<>DATALENGTH(i.PayloadJson) OR ap.PayloadJson COLLATE Latin1_General_100_BIN2<>i.PayloadJson COLLATE Latin1_General_100_BIN2
   OR p.CurrencyId IS NULL OR p.CurrencyId<>d.CurrencyId OR p.ExchangeRate IS NULL OR p.ExchangeRate<>1
   OR (i.ExceptionId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.ConstructionBudgetExceptionRequests ex WHERE ex.ID=i.ExceptionId AND ex.CompanyID=i.CompanyID AND ex.ProjectId=i.ProjectId AND ex.AllocationId=i.AllocationId AND ex.SourceVersion=i.SourceVersion)))
  THROW 52001,'Approval request must pin its exact pending proposal and canonical inventory approval.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseApprovalRequests_Immutable ON dbo.ConstructionPurchaseApprovalRequests AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 52002,'Construction approval request snapshots are immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseAdmissions_Insert ON dbo.ConstructionPurchaseAdmissions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseApprovalRequests q ON q.ID=i.ApprovalRequestId
  JOIN dbo.ConstructionPurchaseDrafts d ON d.ID=q.DraftId JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
  JOIN dbo.InventoryApprovals ap ON ap.ID=q.ApprovalId JOIN dbo.ConstructionCommitmentReservations r ON r.ID=i.ReservationId
  WHERE p.Status<>'Draft' OR ap.Status<>'Pending' OR ap.DocType<>'ConstructionPurchaseOrder' OR i.CreatedBy IN(q.CreatedBy,d.CreatedBy)
   OR i.BeforeVersion<>q.SourceVersion OR i.ContentHash<>q.ContentHash OR i.ContentHash<>d.InitialSourceVersion
   OR r.PurchaseOrderId<>i.PurchaseOrderId OR r.AllocationId<>q.AllocationId OR r.SourceVersion<>q.SourceVersion OR r.CurrencyId<>d.CurrencyId OR r.NetDocumentAmount<>p.SubTotal
   OR ap.RequestedByEmployeeId IS NULL OR ap.RequestedByEmployeeId<>q.CreatedBy OR ap.Amount<>p.GrandTotal
   OR ap.PayloadJson IS NULL OR DATALENGTH(ap.PayloadJson)<>DATALENGTH(q.PayloadJson) OR ap.PayloadJson COLLATE Latin1_General_100_BIN2<>q.PayloadJson COLLATE Latin1_General_100_BIN2
   OR EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReleases rel WHERE rel.ReservationId=r.ID)
   OR (q.ExceptionId IS NULL AND r.DecisionCode NOT IN('WithinBudget','BudgetWarning'))
   OR (q.ExceptionId IS NOT NULL AND (r.DecisionCode<>'ApprovedBudgetException' OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionBudgetExceptionConsumptions usex WHERE usex.ExceptionRequestId=q.ExceptionId AND usex.ReservationId=r.ID AND usex.CompanyID=i.CompanyID AND usex.ProjectId=i.ProjectId))))
  THROW 52003,'Admission must bind one fresh scoped reservation to an independently decided exact proposal.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseAdmissions_Immutable ON dbo.ConstructionPurchaseAdmissions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 52004,'Construction admission history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseDrafts_StateGuard ON dbo.PurchaseOrders AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseDrafts d ON d.PurchaseOrderId=i.ID
  WHERE i.CompanyID<>d.CompanyID OR i.ProjectId IS NULL OR i.ProjectId<>d.ProjectId OR i.CurrencyId IS NULL OR i.CurrencyId<>d.CurrencyId OR i.ExchangeRate IS NULL OR i.ExchangeRate<>1
   OR (NOT EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.PurchaseOrderId=i.ID) AND i.Status NOT IN('Draft','Cancelled'))
   OR (EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.PurchaseOrderId=i.ID) AND i.Status NOT IN('Approved','Received','Closed','Cancelled')))
  THROW 51903,'Construction draft execution requires its atomic admitted transition; reopening is not permitted.',1;
 IF EXISTS(SELECT i.ID,i.CompanyID,i.ProjectId,i.VendorId,i.WarehouseId,i.OrderNo COLLATE Latin1_General_100_BIN2,DATALENGTH(i.OrderNo),i.OrderDate,i.ExpectedDate,i.Notes COLLATE Latin1_General_100_BIN2,DATALENGTH(i.Notes),i.CurrencyId,i.ExchangeRate,i.SubTotal,i.TaxTotal,i.GrandTotal
  FROM inserted i WHERE EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.PurchaseOrderId=i.ID)
  EXCEPT SELECT d.ID,d.CompanyID,d.ProjectId,d.VendorId,d.WarehouseId,d.OrderNo COLLATE Latin1_General_100_BIN2,DATALENGTH(d.OrderNo),d.OrderDate,d.ExpectedDate,d.Notes COLLATE Latin1_General_100_BIN2,DATALENGTH(d.Notes),d.CurrencyId,d.ExchangeRate,d.SubTotal,d.TaxTotal,d.GrandTotal FROM deleted d)
  THROW 52005,'An admitted purchase source requires an explicit financial amendment workflow.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseAdmissions_LineGuard ON dbo.PurchaseOrderLines AFTER INSERT,UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT i.ID,i.PurchaseOrderId,i.[LineNo],i.ItemId,i.ItemDescription COLLATE Latin1_General_100_BIN2,DATALENGTH(i.ItemDescription),i.Qty,i.UoMId,i.UnitPrice,i.DiscountAmount,i.TaxRate,i.LineTotal FROM inserted i WHERE EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.PurchaseOrderId=i.PurchaseOrderId)
  EXCEPT SELECT d.ID,d.PurchaseOrderId,d.[LineNo],d.ItemId,d.ItemDescription COLLATE Latin1_General_100_BIN2,DATALENGTH(d.ItemDescription),d.Qty,d.UoMId,d.UnitPrice,d.DiscountAmount,d.TaxRate,d.LineTotal FROM deleted d)
 OR EXISTS(SELECT d.ID,d.PurchaseOrderId,d.[LineNo],d.ItemId,d.ItemDescription COLLATE Latin1_General_100_BIN2,DATALENGTH(d.ItemDescription),d.Qty,d.UoMId,d.UnitPrice,d.DiscountAmount,d.TaxRate,d.LineTotal FROM deleted d WHERE EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.PurchaseOrderId=d.PurchaseOrderId)
  EXCEPT SELECT i.ID,i.PurchaseOrderId,i.[LineNo],i.ItemId,i.ItemDescription COLLATE Latin1_General_100_BIN2,DATALENGTH(i.ItemDescription),i.Qty,i.UoMId,i.UnitPrice,i.DiscountAmount,i.TaxRate,i.LineTotal FROM inserted i)
  THROW 52006,'Admitted purchase line identity and commercial values cannot be edited or deleted.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseAdmissions_ApprovalGuard ON dbo.InventoryApprovals AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE i.DocType='ConstructionPurchaseOrder' AND d.DocType<>i.DocType)
  THROW 52007,'An existing approval cannot be retyped as a construction request.',1;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ID=d.ID WHERE d.DocType='ConstructionPurchaseOrder' AND
  (i.ID IS NULL OR d.Status<>'Pending' OR i.DocType<>d.DocType OR i.CompanyID<>d.CompanyID OR i.Amount<>d.Amount
   OR ISNULL(i.RequestedByEmployeeId,0)<>ISNULL(d.RequestedByEmployeeId,0) OR (i.RequestedAt<>d.RequestedAt OR (i.RequestedAt IS NULL AND d.RequestedAt IS NOT NULL) OR (i.RequestedAt IS NOT NULL AND d.RequestedAt IS NULL))
   OR DATALENGTH(ISNULL(i.PayloadJson,N''))<>DATALENGTH(ISNULL(d.PayloadJson,N''))
   OR ISNULL(i.PayloadJson,N'') COLLATE Latin1_General_100_BIN2<>ISNULL(d.PayloadJson,N'') COLLATE Latin1_General_100_BIN2))
  THROW 52007,'Pending request identity and terminal inventory decisions cannot be rewritten.',1;
 IF EXISTS(SELECT 1 FROM inserted i WHERE i.DocType='ConstructionPurchaseOrder' AND i.Status NOT IN('Pending','Approved','Rejected'))
  THROW 52007,'Unknown construction approval state.',1;
 IF EXISTS(SELECT 1 FROM inserted i WHERE i.DocType='ConstructionPurchaseOrder' AND i.Status IN('Approved','Rejected') AND
  (i.DecidedByEmployeeId IS NULL OR i.DecidedAt IS NULL OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseApprovalRequests q JOIN dbo.ConstructionPurchaseDrafts d ON d.ID=q.DraftId
   WHERE q.ApprovalId=i.ID AND q.CompanyID=i.CompanyID AND i.DecidedByEmployeeId<>q.CreatedBy AND i.DecidedByEmployeeId<>d.CreatedBy)
   OR (i.Status='Approved' AND NOT EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseApprovalRequests q JOIN dbo.ConstructionPurchaseAdmissions a ON a.ApprovalRequestId=q.ID JOIN dbo.PurchaseOrders p ON p.ID=a.PurchaseOrderId
    WHERE q.ApprovalId=i.ID AND a.CompanyID=i.CompanyID AND a.CreatedBy=i.DecidedByEmployeeId AND p.Status='Approved' AND p.OrderNo=i.ResultDocNo AND a.Reason COLLATE Latin1_General_100_BIN2=i.DecisionNote COLLATE Latin1_General_100_BIN2))
   OR (i.Status='Rejected' AND EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseApprovalRequests q JOIN dbo.ConstructionPurchaseAdmissions a ON a.ApprovalRequestId=q.ID WHERE q.ApprovalId=i.ID))))
  THROW 52008,'Inventory decision must agree with its independent atomic construction admission.',1;
END;
GO
COMMIT TRANSACTION;
