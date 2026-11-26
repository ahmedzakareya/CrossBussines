-- Proposal history only. No existing reservation, admission, invoice or financial balance is updated.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionCommitmentReservations','U') IS NULL OR OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 THROW 51920,'Apply construction reservation and budget schemas first.',1;
IF OBJECT_ID('dbo.ConstructionReservationRebaseRequests','U') IS NULL
 CREATE TABLE dbo.ConstructionReservationRebaseRequests(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionReservationRebaseRequests PRIMARY KEY,
 CompanyID int NOT NULL, ProjectId int NOT NULL, ReservationId int NOT NULL, TargetBudgetVersionId int NOT NULL,
 RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, SourceVersion nvarchar(64) NOT NULL,
 Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionReservationRebaseRequests_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT FK_ConstructionReservationRebaseRequests_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionReservationRebaseRequests_Target FOREIGN KEY(TargetBudgetVersionId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionReservationRebaseRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
 AND RequestId<>'00000000-0000-0000-0000-000000000000' AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64
 AND LEN(LTRIM(RTRIM(Reason)))>0)
 );
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationRebaseRequests_Immutable
ON dbo.ConstructionReservationRebaseRequests AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51921,'Rebase request history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationRebaseRequests_Insert
ON dbo.ConstructionReservationRebaseRequests AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
 JOIN dbo.ConstructionCommitmentReservations r ON r.ID=i.ReservationId AND r.ProjectId=i.ProjectId AND r.CompanyID=i.CompanyID
 JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.TargetBudgetVersionId AND b.ProjectId=i.ProjectId AND b.CompanyID=i.CompanyID
 JOIN dbo.ConstructionBudgetVersions old ON old.ID=r.BudgetVersionId AND old.ProjectId=i.ProjectId AND old.CompanyID=i.CompanyID
 WHERE b.Status<>N'Approved' OR b.IsBaseline<>1 OR old.Status<>N'Superseded'
 OR b.PreviousVersionId IS NULL OR b.PreviousVersionId<>old.ID OR b.CurrencyId<>r.CurrencyId OR old.CurrencyId<>b.CurrencyId
 OR EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReleases rel WHERE rel.ReservationId=r.ID AND rel.CompanyID=i.CompanyID))
 THROW 51922,'Rebase request requires an active reservation and its approved replacement baseline.',1;
END;
GO
