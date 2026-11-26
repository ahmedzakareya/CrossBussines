-- D-10 canonical approval binding/application. No seeds, backfill, source edits or financial writes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionDecisions','U') IS NULL OR OBJECT_ID('dbo.ConstructionPurchaseAdmissions','U') IS NULL THROW 52030,'Warehouse application prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionBindings','U') IS NULL
 CREATE TABLE dbo.ConstructionWarehouseExceptionBindings(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionWarehouseExceptionBindings PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,ApprovalRequestId int NOT NULL,DecisionId int NOT NULL,
 SourceFactsVersion nvarchar(64) NOT NULL,RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionWarehouseExceptionBindings_Scope UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionWarehouseExceptionBindings_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionBindings_Decision UNIQUE(CompanyID,DecisionId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionBindings_Approval UNIQUE(ApprovalRequestId),
 CONSTRAINT FK_ConstructionWarehouseExceptionBindings_Decision FOREIGN KEY(DecisionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWarehouseExceptionDecisions(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionWarehouseExceptionBindings_Approval FOREIGN KEY(ApprovalRequestId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionPurchaseApprovalRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionWarehouseExceptionBindings_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(SourceFactsVersion)=64 AND LEN(RequestHash)=64 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
IF OBJECT_ID('dbo.ConstructionWarehouseExceptionApplications','U') IS NULL
 CREATE TABLE dbo.ConstructionWarehouseExceptionApplications(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionWarehouseExceptionApplications PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,BindingId int NOT NULL,AdmissionId int NOT NULL,
 RequestId uniqueidentifier NOT NULL,ReviewVersion nvarchar(64) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionWarehouseExceptionApplications_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionApplications_Binding UNIQUE(BindingId),
 CONSTRAINT UQ_ConstructionWarehouseExceptionApplications_Admission UNIQUE(AdmissionId),
 CONSTRAINT FK_ConstructionWarehouseExceptionApplications_Binding FOREIGN KEY(BindingId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWarehouseExceptionBindings(ID,PurchaseOrderId,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionWarehouseExceptionApplications_Admission FOREIGN KEY(AdmissionId) REFERENCES dbo.ConstructionPurchaseAdmissions(ID),
 CONSTRAINT CK_ConstructionWarehouseExceptionApplications_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(ReviewVersion)=64 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionBindings'))<>11
 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionApplications'))<>10
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionWarehouseExceptionBindings'),OBJECT_ID('dbo.ConstructionWarehouseExceptionApplications')) AND is_disabled=0 AND is_not_trusted=0)<>4
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionWarehouseExceptionBindings'),OBJECT_ID('dbo.ConstructionWarehouseExceptionApplications')) AND is_disabled=0 AND is_not_trusted=0)<>2
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionBindings') AND is_unique=1 AND is_disabled=0)<>5
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionWarehouseExceptionApplications') AND is_unique=1 AND is_disabled=0)<>4
 THROW 52030,'Warehouse binding/application schema mismatch; no automatic repair.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionBindings_Immutable ON dbo.ConstructionWarehouseExceptionBindings INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52031,'Warehouse bindings are immutable.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionApplications_Immutable ON dbo.ConstructionWarehouseExceptionApplications INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52031,'Warehouse applications are immutable.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionBindings_Proof ON dbo.ConstructionWarehouseExceptionBindings AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionWarehouseExceptionDecisions d ON d.ID=i.DecisionId
 JOIN dbo.ConstructionWarehouseExceptionRequests r ON r.ID=d.ExceptionRequestId
 JOIN dbo.ConstructionPurchaseApprovalRequests a ON a.ID=i.ApprovalRequestId
 JOIN dbo.InventoryApprovals p ON p.ID=a.ApprovalId
 WHERE d.Outcome<>N'Approved' OR d.CreatedBy=r.CreatedBy OR d.SourceFactsVersion<>i.SourceFactsVersion OR r.SourceFactsVersion<>i.SourceFactsVersion
 OR r.CompanyID<>i.CompanyID OR r.ProjectId<>i.ProjectId OR r.PurchaseOrderId<>i.PurchaseOrderId OR r.DraftId<>a.DraftId
 OR a.RequestId<>i.RequestId OR a.CreatedBy<>i.CreatedBy OR p.Status<>N'Pending') THROW 52032,'Warehouse binding source/consent/approval mismatch.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWarehouseExceptionApplications_Proof ON dbo.ConstructionWarehouseExceptionApplications AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionWarehouseExceptionBindings b ON b.ID=i.BindingId
 JOIN dbo.ConstructionPurchaseAdmissions a ON a.ID=i.AdmissionId
 WHERE a.CompanyID<>i.CompanyID OR a.ProjectId<>i.ProjectId OR a.PurchaseOrderId<>i.PurchaseOrderId
 OR a.ApprovalRequestId<>b.ApprovalRequestId OR a.RequestId<>i.RequestId OR a.CreatedBy<>i.CreatedBy) THROW 52032,'Warehouse application admission mismatch.',1;
END;
GO
COMMIT;
