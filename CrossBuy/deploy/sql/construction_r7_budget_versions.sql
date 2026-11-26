-- R7/C2 versioned estimates. Schema before code. No data seed, GL or stock writes.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionWbsNodes','U') IS NULL OR OBJECT_ID('dbo.ConstructionCostCodes','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionBudgetPolicies','U') IS NULL OR OBJECT_ID('dbo.Currencies','U') IS NULL
 THROW 51200,'Apply R7 structure and explicit company policy schema before budget versions.',1;
IF ISNULL(COL_LENGTH('dbo.ConstructionAuditEntries','OldValue'),0)<>-1 OR ISNULL(COL_LENGTH('dbo.ConstructionAuditEntries','NewValue'),0)<>-1
 THROW 51200,'Apply the R7 lossless audit payload expansion before budget versions.',1;

IF OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetVersions(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetVersions PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, VersionNo int NOT NULL, PreviousVersionId int NULL,
  CurrencyId int NOT NULL, Kind nvarchar(20) NOT NULL, Status nvarchar(20) NOT NULL, IsBaseline bit NOT NULL,
  Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  SubmittedBy int NULL, SubmittedAt datetime2 NULL, ApprovedBy int NULL, ApprovedAt datetime2 NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT AK_ConstructionBudgetVersions_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionBudgetVersions_Number UNIQUE(CompanyID,ProjectId,VersionNo),
  CONSTRAINT FK_ConstructionBudgetVersions_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionBudgetVersions_Previous FOREIGN KEY(PreviousVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetVersions_Currency FOREIGN KEY(CurrencyId) REFERENCES dbo.Currencies(ID),
  CONSTRAINT FK_ConstructionBudgetVersions_Policy FOREIGN KEY(CompanyID) REFERENCES dbo.ConstructionBudgetPolicies(CompanyID),
  CONSTRAINT CK_ConstructionBudgetVersions_Identity CHECK(CompanyID>0 AND ProjectId>0 AND VersionNo>0 AND CreatedBy>0 AND (PreviousVersionId IS NULL OR PreviousVersionId<>ID)),
  CONSTRAINT CK_ConstructionBudgetVersions_Kind CHECK(Kind IN('Original','Revised','Scenario') AND (Kind<>'Original' OR PreviousVersionId IS NULL) AND (Kind<>'Revised' OR PreviousVersionId IS NOT NULL)),
  CONSTRAINT CK_ConstructionBudgetVersions_Status CHECK(Status IN('Draft','Submitted','Approved','Superseded')),
  CONSTRAINT CK_ConstructionBudgetVersions_Reason CHECK(LEN(LTRIM(RTRIM(Reason)))>0),
  CONSTRAINT CK_ConstructionBudgetVersions_Baseline CHECK(IsBaseline=0 OR (Status='Approved' AND Kind<>'Scenario')),
  CONSTRAINT CK_ConstructionBudgetVersions_Submission CHECK((Status='Draft' AND SubmittedBy IS NULL AND SubmittedAt IS NULL) OR (Status<>'Draft' AND SubmittedBy IS NOT NULL AND SubmittedBy>0 AND SubmittedAt IS NOT NULL)),
  CONSTRAINT CK_ConstructionBudgetVersions_Approval CHECK((Status IN('Draft','Submitted') AND ApprovedBy IS NULL AND ApprovedAt IS NULL) OR (Status IN('Approved','Superseded') AND ApprovedBy IS NOT NULL AND ApprovedAt IS NOT NULL AND ApprovedBy>0 AND ApprovedBy<>CreatedBy AND ApprovedBy<>SubmittedBy))
 );
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionBudgetVersions') AND name='UX_ConstructionBudgetVersions_Baseline')
 CREATE UNIQUE INDEX UX_ConstructionBudgetVersions_Baseline ON dbo.ConstructionBudgetVersions(CompanyID,ProjectId) WHERE IsBaseline=1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionBudgetVersions') AND name='UX_ConstructionBudgetVersions_Original')
 CREATE UNIQUE INDEX UX_ConstructionBudgetVersions_Original ON dbo.ConstructionBudgetVersions(CompanyID,ProjectId) WHERE Kind='Original';

IF OBJECT_ID('dbo.ConstructionBudgetLines','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, BudgetVersionId int NOT NULL,
  WbsNodeId int NOT NULL, CostCodeId int NOT NULL, CostCategory nvarchar(30) NOT NULL,
  Amount decimal(28,8) NOT NULL, Quantity decimal(28,8) NULL, Rate decimal(28,8) NULL, PreviousAmount decimal(28,8) NULL,
  IsContingency bit NOT NULL, ChangeReason nvarchar(1000) NOT NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT UQ_ConstructionBudgetLines_Dimension UNIQUE(BudgetVersionId,WbsNodeId,CostCodeId),
  CONSTRAINT FK_ConstructionBudgetLines_Version FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetLines_CostCode FOREIGN KEY(CostCodeId,CompanyID,CostCategory) REFERENCES dbo.ConstructionCostCodes(ID,CompanyID,Category),
  CONSTRAINT CK_ConstructionBudgetLines_Amounts CHECK(Amount>=0 AND (PreviousAmount IS NULL OR PreviousAmount>=0) AND ((Quantity IS NULL AND Rate IS NULL) OR (Quantity IS NOT NULL AND Rate IS NOT NULL AND Quantity>=0 AND Rate>=0))),
  CONSTRAINT CK_ConstructionBudgetLines_Contingency CHECK((IsContingency=1 AND CostCategory='Contingency') OR (IsContingency=0 AND CostCategory<>'Contingency')),
  CONSTRAINT CK_ConstructionBudgetLines_Reason CHECK(LEN(LTRIM(RTRIM(ChangeReason)))>0)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetVersions_History ON dbo.ConstructionBudgetVersions AFTER INSERT,UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ID=d.ID WHERE i.ID IS NULL)
  THROW 51201,'Budget versions are retained permanently.',1;
 IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.ID=i.ID WHERE d.ID IS NULL AND (i.Status<>'Draft' OR i.IsBaseline<>0))
  THROW 51202,'New budget versions must start as drafts.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE
  i.CompanyID<>d.CompanyID OR i.ProjectId<>d.ProjectId OR i.VersionNo<>d.VersionNo OR i.CurrencyId<>d.CurrencyId OR i.Kind<>d.Kind
  OR ISNULL(i.PreviousVersionId,0)<>ISNULL(d.PreviousVersionId,0) OR i.CreatedBy<>d.CreatedBy OR i.CreatedAt<>d.CreatedAt)
  THROW 51201,'Budget identity, origin and currency cannot change.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE
  (d.Status='Superseded') OR
  (d.Status='Approved' AND (i.Status<>'Superseded' OR i.IsBaseline<>0 OR i.Reason<>d.Reason
   OR i.SubmittedBy<>d.SubmittedBy OR i.SubmittedAt<>d.SubmittedAt OR i.ApprovedBy<>d.ApprovedBy OR i.ApprovedAt<>d.ApprovedAt)))
  THROW 51201,'Approved budgets are immutable; only supersession is allowed.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE i.Status<>d.Status AND NOT
  ((d.Status='Draft' AND i.Status='Submitted') OR (d.Status='Submitted' AND i.Status='Approved') OR (d.Status='Approved' AND i.Status='Superseded')))
  THROW 51202,'Invalid budget transition.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE d.Status='Submitted' AND
  (i.Status<>'Approved' OR i.SubmittedBy<>d.SubmittedBy OR i.SubmittedAt<>d.SubmittedAt))
  THROW 51202,'Submitted budgets can only advance to approval.',1;
 IF EXISTS(SELECT 1 FROM inserted i WHERE i.Status IN('Submitted','Approved') AND NOT EXISTS
  (SELECT 1 FROM dbo.ConstructionBudgetLines l WHERE l.BudgetVersionId=i.ID))
  THROW 51202,'A budget cannot be submitted or approved without lines.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetLines_DraftOnly ON dbo.ConstructionBudgetLines AFTER INSERT,UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM (SELECT BudgetVersionId FROM inserted UNION SELECT BudgetVersionId FROM deleted) a
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=a.BudgetVersionId WHERE b.Status<>'Draft')
  THROW 51201,'Submitted and approved budget lines are immutable.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE
  i.CompanyID<>d.CompanyID OR i.ProjectId<>d.ProjectId OR i.BudgetVersionId<>d.BudgetVersionId OR i.WbsNodeId<>d.WbsNodeId
  OR i.CostCodeId<>d.CostCodeId OR i.CostCategory<>d.CostCategory OR ISNULL(i.PreviousAmount,-1)<>ISNULL(d.PreviousAmount,-1))
  THROW 51201,'A budget line cannot be moved or have its prior amount rewritten.',1;
 -- Even native SQL edits invalidate an already displayed whole-draft token.
 UPDATE b SET Reason=b.Reason FROM dbo.ConstructionBudgetVersions b JOIN
  (SELECT BudgetVersionId FROM inserted UNION SELECT BudgetVersionId FROM deleted) a ON a.BudgetVersionId=b.ID;
END;
GO
COMMIT TRANSACTION;
