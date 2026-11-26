-- Immutable analytical exposure mapping; never edits a reservation or purchase admission.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionReservationRebaseDecisions','U') IS NULL
 THROW 51926,'Apply rebase request and decision schemas first.',1;
IF OBJECT_ID('dbo.ConstructionReservationExposureRebases','U') IS NULL
 CREATE TABLE dbo.ConstructionReservationExposureRebases(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionReservationExposureRebases PRIMARY KEY,
 CompanyID int NOT NULL, ProjectId int NOT NULL, ReservationId int NOT NULL, RebaseRequestId int NOT NULL,
 TargetBudgetVersionId int NOT NULL, SourceVersion nvarchar(64) NOT NULL, RequestId uniqueidentifier NOT NULL,
 RequestHash nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionReservationExposureRebases_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionReservationExposureRebases_Reservation UNIQUE(CompanyID,ReservationId),
 CONSTRAINT FK_ConstructionReservationExposureRebases_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionReservationExposureRebases_Proposal FOREIGN KEY(RebaseRequestId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionReservationRebaseRequests(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionReservationExposureRebases_Baseline FOREIGN KEY(TargetBudgetVersionId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionReservationExposureRebases_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
 AND LEN(SourceVersion)=64 AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0
 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionReservationExposureRebases')
 AND name='AK_ConstructionReservationExposureRebases_ID_ProjectId_CompanyID')
 ALTER TABLE dbo.ConstructionReservationExposureRebases ADD CONSTRAINT AK_ConstructionReservationExposureRebases_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID);
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationExposureRebases_Immutable
ON dbo.ConstructionReservationExposureRebases AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51927,'Exposure rebase history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationExposureRebases_Insert
ON dbo.ConstructionReservationExposureRebases AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionReservationRebaseRequests r
 ON r.ID=i.RebaseRequestId AND r.CompanyID=i.CompanyID AND r.ProjectId=i.ProjectId
 JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.TargetBudgetVersionId AND b.CompanyID=i.CompanyID AND b.ProjectId=i.ProjectId
 WHERE r.ReservationId<>i.ReservationId OR r.TargetBudgetVersionId<>i.TargetBudgetVersionId OR b.Status<>N'Approved' OR b.IsBaseline<>1
 OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionReservationRebaseDecisions d WHERE d.CompanyID=i.CompanyID
 AND d.ProjectId=i.ProjectId AND d.RebaseRequestId=r.ID AND d.Outcome=N'Approved'
 AND d.CreatedBy<>r.CreatedBy AND d.SourceVersion=r.SourceVersion))
 THROW 51928,'Exposure rebasing requires a matching independently approved proposal and current baseline.',1;
END;
GO
