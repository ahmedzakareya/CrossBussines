-- R7/C2 private reservation kernel. Additive history only; existing UI approval paths are not activated.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionOrderAllocations','U') IS NULL
 THROW 51500,'Apply authored source-line allocation schema first.',1;
IF OBJECT_ID('dbo.ConstructionCommitmentReservations','U') IS NULL
 CREATE TABLE dbo.ConstructionCommitmentReservations(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionCommitmentReservations PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, PurchaseOrderId int NOT NULL, AllocationId int NOT NULL,
  BudgetVersionId int NOT NULL, CurrencyId int NOT NULL, NetDocumentAmount decimal(28,8) NOT NULL,
  RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, SourceVersion nvarchar(64) NOT NULL,
  PolicyMode nvarchar(30) NOT NULL, DecisionCode nvarchar(40) NOT NULL, Reason nvarchar(1000) NOT NULL,
  CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT AK_ConstructionCommitmentReservations_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionCommitmentReservations_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionCommitmentReservations_Source UNIQUE(CompanyID,PurchaseOrderId),
  CONSTRAINT CK_ConstructionCommitmentReservations_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND NetDocumentAmount>=0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0 AND PolicyMode IN('Warning','ApprovalRequired','HardBlock') AND (DecisionCode='WithinBudget' OR (PolicyMode='Warning' AND DecisionCode='BudgetWarning'))),
  CONSTRAINT FK_ConstructionCommitmentReservations_Allocation FOREIGN KEY(AllocationId,PurchaseOrderId,ProjectId,CompanyID) REFERENCES dbo.ConstructionOrderAllocations(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionCommitmentReservations_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionCommitmentReservations_Currency FOREIGN KEY(CurrencyId) REFERENCES dbo.Currencies(ID)
 );
IF OBJECT_ID('dbo.ConstructionCommitmentReleases','U') IS NULL
 CREATE TABLE dbo.ConstructionCommitmentReleases(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionCommitmentReleases PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, ReservationId int NOT NULL, RequestId uniqueidentifier NOT NULL,
  RequestHash nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionCommitmentReleases_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionCommitmentReleases_Reservation UNIQUE(ReservationId),
  CONSTRAINT CK_ConstructionCommitmentReleases_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0),
  CONSTRAINT FK_ConstructionCommitmentReleases_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID) REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCommitmentReservations_Insert ON dbo.ConstructionCommitmentReservations AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionOrderAllocations a ON a.ID=i.AllocationId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId JOIN dbo.PurchaseOrders p ON p.ID=i.PurchaseOrderId
  LEFT JOIN dbo.ConstructionBudgetPolicies policy ON policy.CompanyID=i.CompanyID
  WHERE a.IsSealed<>1 OR a.BudgetVersionId<>i.BudgetVersionId OR a.SourceVersion<>i.SourceVersion
   OR b.IsBaseline<>1 OR b.Status<>'Approved' OR b.CurrencyId<>i.CurrencyId OR p.CurrencyId IS NULL OR p.CurrencyId<>i.CurrencyId
   OR p.ProjectId IS NULL OR p.ProjectId<>i.ProjectId OR p.Status NOT IN('Draft','Approved','Received')
   OR policy.CompanyID IS NULL OR policy.Mode<>i.PolicyMode
   OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionOrderAllocationLines l WHERE l.AllocationId=a.ID)
   OR i.NetDocumentAmount<>(SELECT SUM(l.NetDocumentAmount) FROM dbo.ConstructionOrderAllocationLines l WHERE l.AllocationId=a.ID))
  THROW 51501,'Reservation must retain the sealed source, currency, approved baseline and explicit policy.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCommitmentReservations_Immutable ON dbo.ConstructionCommitmentReservations AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51502,'Reservation history cannot be changed or deleted; cancellation needs a separate release fact.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCommitmentReleases_Insert ON dbo.ConstructionCommitmentReleases AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionCommitmentReservations r ON r.ID=i.ReservationId
  JOIN dbo.PurchaseOrders p ON p.ID=r.PurchaseOrderId WHERE p.Status<>'Cancelled'
   OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderLines l WHERE l.PurchaseOrderId=p.ID AND l.ReceivedQty<>0)
   OR EXISTS(SELECT 1 FROM dbo.GoodsReceipts g WHERE g.PurchaseOrderId=p.ID AND g.CompanyID=i.CompanyID)
   OR EXISTS(SELECT 1 FROM dbo.PurchaseOrderInvoiceLinks link WHERE link.PurchaseOrderId=p.ID AND link.CompanyID=i.CompanyID))
  THROW 51503,'Only a cancelled source without receipt or conversion history can be released.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCommitmentReleases_Immutable ON dbo.ConstructionCommitmentReleases AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51502,'Commitment release history cannot be changed or deleted.',1;
END;
GO
COMMIT TRANSACTION;
