-- R7/C2: private, immutable, independently decided budget exceptions. No historical backfill.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionCommitmentReservations','U') IS NULL
 THROW 51800,'Apply authored construction reservation schema first.',1;
IF OBJECT_ID('dbo.ConstructionBudgetExceptionRequests','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetExceptionRequests(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetExceptionRequests PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, PurchaseOrderId int NOT NULL, AllocationId int NOT NULL,
  BudgetVersionId int NOT NULL, CurrencyId int NOT NULL, SourceVersion nvarchar(64) NOT NULL, AdmissionVersion nvarchar(64) NOT NULL,
  ProposedAmount decimal(28,8) NOT NULL, Overrun decimal(28,8) NOT NULL,
  RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL,
  CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT AK_ConstructionBudgetExceptionRequests_Scope UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionBudgetExceptionRequests_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT CK_ConstructionBudgetExceptionRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND ProposedAmount>=0 AND Overrun>0
   AND LEN(SourceVersion)=64 AND LEN(AdmissionVersion)=64 AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0
   AND RequestId<>'00000000-0000-0000-0000-000000000000'),
  CONSTRAINT FK_ConstructionBudgetExceptionRequests_Allocation FOREIGN KEY(AllocationId,PurchaseOrderId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionOrderAllocations(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetExceptionRequests_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetExceptionRequests_Currency FOREIGN KEY(CurrencyId) REFERENCES dbo.Currencies(ID)
 );
IF OBJECT_ID('dbo.ConstructionBudgetExceptionDecisions','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetExceptionDecisions(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetExceptionDecisions PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, ExceptionRequestId int NOT NULL, Outcome nvarchar(20) NOT NULL,
  RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL,
  CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionBudgetExceptionDecisions_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionBudgetExceptionDecisions_Exception UNIQUE(ExceptionRequestId),
  CONSTRAINT CK_ConstructionBudgetExceptionDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND Outcome IN('Approved','Rejected')
   AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0 AND RequestId<>'00000000-0000-0000-0000-000000000000'),
  CONSTRAINT FK_ConstructionBudgetExceptionDecisions_Request FOREIGN KEY(ExceptionRequestId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionBudgetExceptionRequests(ID,ProjectId,CompanyID)
 );
IF OBJECT_ID('dbo.ConstructionBudgetExceptionConsumptions','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetExceptionConsumptions(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetExceptionConsumptions PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, ExceptionRequestId int NOT NULL, ReservationId int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionBudgetExceptionConsumptions_Request UNIQUE(ExceptionRequestId),
  CONSTRAINT UQ_ConstructionBudgetExceptionConsumptions_Reservation UNIQUE(ReservationId),
  CONSTRAINT FK_ConstructionBudgetExceptionConsumptions_Request FOREIGN KEY(ExceptionRequestId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionBudgetExceptionRequests(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetExceptionConsumptions_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID)
 );
-- Keep all original checks; add the independently approved exception as an explicit decision.
ALTER TABLE dbo.ConstructionCommitmentReservations DROP CONSTRAINT CK_ConstructionCommitmentReservations_Values;
ALTER TABLE dbo.ConstructionCommitmentReservations WITH CHECK ADD CONSTRAINT CK_ConstructionCommitmentReservations_Values
 CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND NetDocumentAmount>=0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64
  AND LEN(LTRIM(RTRIM(Reason)))>0 AND PolicyMode IN('Warning','ApprovalRequired','HardBlock')
  AND (DecisionCode='WithinBudget' OR (PolicyMode='Warning' AND DecisionCode='BudgetWarning')
   OR (PolicyMode='ApprovalRequired' AND DecisionCode='ApprovedBudgetException')));
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionRequests_Insert ON dbo.ConstructionBudgetExceptionRequests AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionOrderAllocations a ON a.ID=i.AllocationId
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId
  LEFT JOIN dbo.ConstructionBudgetPolicies p ON p.CompanyID=i.CompanyID
  WHERE a.IsSealed<>1 OR a.SourceVersion<>i.SourceVersion OR a.BudgetVersionId<>i.BudgetVersionId
   OR b.IsBaseline<>1 OR b.Status<>'Approved' OR b.CurrencyId<>i.CurrencyId OR p.Mode IS NULL OR p.Mode<>'ApprovalRequired'
   OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionOrderAllocationLines l WHERE l.AllocationId=i.AllocationId)
   OR i.ProposedAmount<>(SELECT SUM(l.NetDocumentAmount) FROM dbo.ConstructionOrderAllocationLines l WHERE l.AllocationId=i.AllocationId))
  THROW 51801,'Exception requires the sealed proposal, approved baseline, exact currency and explicit approval policy.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionDecisions_Insert ON dbo.ConstructionBudgetExceptionDecisions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionBudgetExceptionRequests r ON r.ID=i.ExceptionRequestId WHERE i.CreatedBy=r.CreatedBy)
  THROW 51802,'An independent employee must decide the exception.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCommitmentReservations_Exception ON dbo.ConstructionCommitmentReservations AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i WHERE i.DecisionCode='ApprovedBudgetException' AND NOT EXISTS(
  SELECT 1 FROM dbo.ConstructionBudgetExceptionRequests r JOIN dbo.ConstructionBudgetExceptionDecisions d ON d.ExceptionRequestId=r.ID
  WHERE r.CompanyID=i.CompanyID AND r.ProjectId=i.ProjectId AND r.PurchaseOrderId=i.PurchaseOrderId AND r.AllocationId=i.AllocationId
   AND r.BudgetVersionId=i.BudgetVersionId AND r.CurrencyId=i.CurrencyId AND r.SourceVersion=i.SourceVersion AND r.ProposedAmount=i.NetDocumentAmount
   AND d.Outcome='Approved' AND d.CreatedBy<>r.CreatedBy AND d.CompanyID=r.CompanyID AND d.ProjectId=r.ProjectId
   AND NOT EXISTS(SELECT 1 FROM dbo.ConstructionBudgetExceptionConsumptions c WHERE c.ExceptionRequestId=r.ID)))
  THROW 51803,'An exact independently approved, unconsumed exception is required.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionConsumptions_Insert ON dbo.ConstructionBudgetExceptionConsumptions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionBudgetExceptionRequests q ON q.ID=i.ExceptionRequestId
  JOIN dbo.ConstructionCommitmentReservations r ON r.ID=i.ReservationId
  LEFT JOIN dbo.ConstructionBudgetExceptionDecisions d ON d.ExceptionRequestId=q.ID
  WHERE r.DecisionCode<>'ApprovedBudgetException' OR r.PolicyMode<>'ApprovalRequired' OR r.PurchaseOrderId<>q.PurchaseOrderId
   OR r.AllocationId<>q.AllocationId OR r.BudgetVersionId<>q.BudgetVersionId OR r.CurrencyId<>q.CurrencyId
   OR r.SourceVersion<>q.SourceVersion OR r.NetDocumentAmount<>q.ProposedAmount OR d.ID IS NULL OR d.Outcome<>'Approved' OR d.CreatedBy=q.CreatedBy)
  THROW 51803,'Consumption must bind the exact approved exception to its reservation.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionRequests_Immutable ON dbo.ConstructionBudgetExceptionRequests AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51804,'Budget exception history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionDecisions_Immutable ON dbo.ConstructionBudgetExceptionDecisions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51804,'Budget exception decisions are immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetExceptionConsumptions_Immutable ON dbo.ConstructionBudgetExceptionConsumptions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51804,'Budget exception consumption is immutable.',1;
END;
GO
COMMIT TRANSACTION;
