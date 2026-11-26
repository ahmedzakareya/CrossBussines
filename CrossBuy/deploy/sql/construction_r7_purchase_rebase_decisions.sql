-- Independent grant + canonical admission; no stock/GL realization is authorized here.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionPurchaseRebaseRequests','U') IS NULL
 THROW 51933,'Apply the canonical rebased purchase request foundation first.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionPurchaseRebaseRequests')
 AND name='AK_ConstructionPurchaseRebaseRequests_ID_PurchaseOrderId_ProjectId_CompanyID')
 ALTER TABLE dbo.ConstructionPurchaseRebaseRequests ADD CONSTRAINT AK_ConstructionPurchaseRebaseRequests_ID_PurchaseOrderId_ProjectId_CompanyID UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID);
IF OBJECT_ID('dbo.ConstructionPurchaseRebaseDecisions','U') IS NULL
 CREATE TABLE dbo.ConstructionPurchaseRebaseDecisions(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPurchaseRebaseDecisions PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,RebaseRequestId int NOT NULL,
 ReservationId int NOT NULL,TargetBudgetVersionId int NOT NULL,RequestVersion nvarchar(64) NOT NULL,
 SourceVersion nvarchar(64) NOT NULL,ReviewVersion nvarchar(64) NOT NULL,RequestId uniqueidentifier NOT NULL,
 RequestHash nvarchar(64) NOT NULL,Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionPurchaseRebaseDecisions_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionPurchaseRebaseDecisions_Bridge UNIQUE(RebaseRequestId),
 CONSTRAINT UQ_ConstructionPurchaseRebaseDecisions_Order UNIQUE(CompanyID,PurchaseOrderId),
 CONSTRAINT FK_ConstructionPurchaseRebaseDecisions_Request FOREIGN KEY(RebaseRequestId,PurchaseOrderId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionPurchaseRebaseRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseRebaseDecisions_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseRebaseDecisions_Budget FOREIGN KEY(TargetBudgetVersionId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionPurchaseRebaseDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
 AND LEN(RequestVersion)=64 AND LEN(SourceVersion)=64 AND LEN(ReviewVersion)=64 AND LEN(RequestHash)=64
 AND LEN(LTRIM(RTRIM(Reason)))>0 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseDecisions_Immutable
ON dbo.ConstructionPurchaseRebaseDecisions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51934,'Purchase rebase decisions are immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseDecisions_Insert
ON dbo.ConstructionPurchaseRebaseDecisions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
 JOIN dbo.ConstructionPurchaseRebaseRequests br ON br.ID=i.RebaseRequestId
 JOIN dbo.ConstructionPurchaseApprovalRequests q ON q.ID=br.ApprovalRequestId
 JOIN dbo.ConstructionPurchaseDrafts d ON d.ID=q.DraftId
 JOIN dbo.InventoryApprovals ap ON ap.ID=q.ApprovalId
 JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=br.ExposureRebaseId
 JOIN dbo.ConstructionCommitmentReservations r ON r.ID=i.ReservationId
 JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.TargetBudgetVersionId
 JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
 WHERE i.CreatedBy IN(q.CreatedBy,d.CreatedBy) OR q.ExceptionId IS NOT NULL OR ap.Status<>N'Pending' OR p.Status<>N'Draft'
 OR NULLIF(LTRIM(RTRIM(p.OrderNo)),N'') IS NULL
 OR i.RequestVersion<>br.ReviewVersion OR i.SourceVersion<>e.SourceVersion OR i.ReservationId<>e.ReservationId
 OR i.TargetBudgetVersionId<>e.TargetBudgetVersionId OR b.Status<>N'Approved' OR b.IsBaseline<>1
 OR r.PurchaseOrderId<>i.PurchaseOrderId OR r.AllocationId<>q.AllocationId OR r.SourceVersion<>q.SourceVersion
 OR r.DecisionCode NOT IN(N'WithinBudget',N'BudgetWarning')
 OR EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReleases rel WHERE rel.CompanyID=i.CompanyID AND rel.ReservationId=r.ID)
 OR EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions a WHERE a.CompanyID=i.CompanyID AND a.PurchaseOrderId=i.PurchaseOrderId))
 THROW 51935,'Decision must independently review the exact pending rebased purchase and active original commitment.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseRequests_AdmissionGate
ON dbo.ConstructionPurchaseAdmissions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseRebaseRequests br
 ON br.ApprovalRequestId=i.ApprovalRequestId AND br.CompanyID=i.CompanyID AND br.ProjectId=i.ProjectId
 WHERE NOT EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseRebaseDecisions d
 JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=br.ExposureRebaseId
 JOIN dbo.ConstructionBudgetVersions b ON b.ID=d.TargetBudgetVersionId
 WHERE d.RebaseRequestId=br.ID AND d.CompanyID=i.CompanyID AND d.ProjectId=i.ProjectId AND d.PurchaseOrderId=i.PurchaseOrderId
 AND d.ReservationId=i.ReservationId AND d.CreatedBy=i.CreatedBy AND d.RequestId=i.RequestId AND d.RequestHash=i.RequestHash
 AND d.RequestVersion=br.ReviewVersion AND d.SourceVersion=e.SourceVersion AND d.ReservationId=e.ReservationId
 AND d.TargetBudgetVersionId=e.TargetBudgetVersionId AND b.Status=N'Approved' AND b.IsBaseline=1
 AND DATALENGTH(d.Reason)=DATALENGTH(i.Reason) AND d.Reason COLLATE Latin1_General_100_BIN2=i.Reason COLLATE Latin1_General_100_BIN2))
 THROW 51932,'Rebased admission requires its matching fresh independently reviewed decision.',1;
END;
GO
