-- Independent proposal review only; does not activate a reservation rebase.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionReservationRebaseRequests','U') IS NULL
 THROW 51923,'Apply rebase request schema first.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionReservationRebaseRequests') AND name='AK_ConstructionReservationRebaseRequests_Scope')
 ALTER TABLE dbo.ConstructionReservationRebaseRequests ADD CONSTRAINT AK_ConstructionReservationRebaseRequests_Scope UNIQUE(ID,ProjectId,CompanyID);
IF OBJECT_ID('dbo.ConstructionReservationRebaseDecisions','U') IS NULL
 CREATE TABLE dbo.ConstructionReservationRebaseDecisions(
 ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionReservationRebaseDecisions PRIMARY KEY,
 CompanyID int NOT NULL, ProjectId int NOT NULL, RebaseRequestId int NOT NULL,
 Outcome nvarchar(16) NOT NULL, RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL,
 SourceVersion nvarchar(64) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
 CONSTRAINT UQ_ConstructionReservationRebaseDecisions_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT UQ_ConstructionReservationRebaseDecisions_Proposal UNIQUE(CompanyID,RebaseRequestId),
 CONSTRAINT FK_ConstructionReservationRebaseDecisions_Proposal FOREIGN KEY(RebaseRequestId,ProjectId,CompanyID)
 REFERENCES dbo.ConstructionReservationRebaseRequests(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionReservationRebaseDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
 AND Outcome IN(N'Approved',N'Rejected') AND RequestId<>'00000000-0000-0000-0000-000000000000'
 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0)
 );
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationRebaseDecisions_Immutable
ON dbo.ConstructionReservationRebaseDecisions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51924,'Rebase decision history is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionReservationRebaseDecisions_Insert
ON dbo.ConstructionReservationRebaseDecisions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionReservationRebaseRequests r
 ON r.ID=i.RebaseRequestId AND r.CompanyID=i.CompanyID AND r.ProjectId=i.ProjectId
 WHERE i.CreatedBy=r.CreatedBy OR i.SourceVersion<>r.SourceVersion)
 THROW 51925,'An independent reviewer and the original proposal witness are required.',1;
END;
GO
