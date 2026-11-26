-- Canonical pending purchase approval bridge only; never renews admission.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionReservationExposureRebases','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseApprovalRequests','U') IS NULL
 THROW 51929,'Apply exposure rebases and canonical purchase approval foundations first.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionReservationExposureRebases')
 AND name='AK_ConstructionReservationExposureRebases_ID_ProjectId_CompanyID')
 ALTER TABLE dbo.ConstructionReservationExposureRebases ADD CONSTRAINT AK_ConstructionReservationExposureRebases_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID);
IF OBJECT_ID('dbo.ConstructionPurchaseRebaseRequests','U') IS NULL
 CREATE TABLE dbo.ConstructionPurchaseRebaseRequests(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionPurchaseRebaseRequests PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,ApprovalRequestId int NOT NULL,
 ExposureRebaseId int NOT NULL,ReviewVersion nvarchar(64) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionPurchaseRebaseRequests_Approval UNIQUE(ApprovalRequestId),
 CONSTRAINT FK_ConstructionPurchaseRebaseRequests_Approval FOREIGN KEY(ApprovalRequestId,PurchaseOrderId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionPurchaseApprovalRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionPurchaseRebaseRequests_Exposure FOREIGN KEY(ExposureRebaseId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionReservationExposureRebases(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionPurchaseRebaseRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(ReviewVersion)=64)
 );
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseRequests_Immutable
ON dbo.ConstructionPurchaseRebaseRequests AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51930,'Purchase rebase request history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseRequests_Insert
ON dbo.ConstructionPurchaseRebaseRequests AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
 JOIN dbo.ConstructionPurchaseApprovalRequests q ON q.ID=i.ApprovalRequestId
 JOIN dbo.InventoryApprovals a ON a.ID=q.ApprovalId
 JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=i.ExposureRebaseId
 JOIN dbo.ConstructionCommitmentReservations r ON r.ID=e.ReservationId
 JOIN dbo.ConstructionBudgetVersions b ON b.ID=e.TargetBudgetVersionId
 JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
 WHERE i.CreatedBy<>q.CreatedBy OR q.ExceptionId IS NOT NULL OR a.Status<>N'Pending'
 OR a.DocType<>N'ConstructionPurchaseOrder' OR p.Status<>N'Draft' OR b.Status<>N'Approved' OR b.IsBaseline<>1
 OR r.PurchaseOrderId<>i.PurchaseOrderId OR r.ProjectId<>i.ProjectId OR r.CompanyID<>i.CompanyID
 OR r.AllocationId<>q.AllocationId OR r.SourceVersion<>q.SourceVersion
 OR r.DecisionCode NOT IN(N'WithinBudget',N'BudgetWarning')
 OR EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReleases rel WHERE rel.CompanyID=i.CompanyID AND rel.ReservationId=r.ID)
 OR EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseAdmissions admitted WHERE admitted.CompanyID=i.CompanyID AND admitted.PurchaseOrderId=i.PurchaseOrderId))
 THROW 51931,'Rebased purchase request must bind its pending exact canonical approval and active original commitment.',1;
END;
GO
-- Deliberate closure until a separately qualified immutable readmission decision writer exists.
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseRequests_AdmissionGate
ON dbo.ConstructionPurchaseAdmissions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseRebaseRequests q
 ON q.ApprovalRequestId=i.ApprovalRequestId AND q.CompanyID=i.CompanyID AND q.ProjectId=i.ProjectId)
 THROW 51932,'Rebased purchase admission requires the separately qualified readmission decision workflow.',1;
END;
GO
