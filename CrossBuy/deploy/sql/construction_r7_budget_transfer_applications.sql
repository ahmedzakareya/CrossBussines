SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionBudgetTransferRequests','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionBudgetTransferDecisions','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL
 THROW 51970,'Apply the immutable request, decision and budget-version schemas first.',1;
IF OBJECT_ID('dbo.ConstructionBudgetTransferApplications','U') IS NULL
 CREATE TABLE dbo.ConstructionBudgetTransferApplications(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionBudgetTransferApplications PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,TransferRequestId int NOT NULL,TransferDecisionId int NOT NULL,
  OriginalBudgetVersionId int NOT NULL,ResultBudgetVersionId int NOT NULL,
  ApplicationId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
  Reason nvarchar(1000) NOT NULL,AppliedBy int NOT NULL,AppliedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionBudgetTransferApplications_Key UNIQUE(CompanyID,ApplicationId),
  CONSTRAINT UQ_ConstructionBudgetTransferApplications_Decision UNIQUE(CompanyID,TransferDecisionId),
  CONSTRAINT UQ_ConstructionBudgetTransferApplications_Request UNIQUE(CompanyID,TransferRequestId),
  CONSTRAINT UQ_ConstructionBudgetTransferApplications_Result UNIQUE(CompanyID,ResultBudgetVersionId),
  CONSTRAINT FK_ConstructionBudgetTransferApplications_Request FOREIGN KEY(TransferRequestId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetTransferRequests(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetTransferApplications_Decision FOREIGN KEY(TransferDecisionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetTransferDecisions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetTransferApplications_Original FOREIGN KEY(OriginalBudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionBudgetTransferApplications_Result FOREIGN KEY(ResultBudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionBudgetTransferApplications_Values CHECK(CompanyID>0 AND ProjectId>0 AND AppliedBy>0
   AND OriginalBudgetVersionId<>ResultBudgetVersionId AND ApplicationId<>'00000000-0000-0000-0000-000000000000'
   AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0)
 );
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionBudgetTransferApplications'))<>13
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferApplications') AND is_disabled=0 AND is_not_trusted=0)<>4
 THROW 51970,'Unexpected transfer-application schema; refuse partial activation.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferApplications_Immutable
 ON dbo.ConstructionBudgetTransferApplications INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51971,'Applied transfer evidence is immutable.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionBudgetTransferApplications_Proof
 ON dbo.ConstructionBudgetTransferApplications AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(
  SELECT 1 FROM inserted i
  JOIN dbo.ConstructionBudgetTransferRequests r ON r.ID=i.TransferRequestId AND r.CompanyID=i.CompanyID AND r.ProjectId=i.ProjectId
  JOIN dbo.ConstructionBudgetTransferDecisions d ON d.ID=i.TransferDecisionId AND d.CompanyID=i.CompanyID AND d.ProjectId=i.ProjectId
  JOIN dbo.ConstructionBudgetVersions old ON old.ID=i.OriginalBudgetVersionId AND old.CompanyID=i.CompanyID AND old.ProjectId=i.ProjectId
  JOIN dbo.ConstructionBudgetVersions target ON target.ID=i.ResultBudgetVersionId AND target.CompanyID=i.CompanyID AND target.ProjectId=i.ProjectId
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'BudgetTransfer'
  WHERE d.TransferRequestId<>r.ID OR d.Outcome<>N'Approved' OR d.CreatedBy=r.CreatedBy OR d.CreatedBy=i.AppliedBy
   OR i.SourceVersion<>r.SourceVersion OR d.SourceVersion<>r.SourceVersion OR r.BudgetVersionId<>old.ID
   OR old.Status<>N'Superseded' OR old.IsBaseline<>0 OR target.Status<>N'Approved' OR target.IsBaseline<>1
   OR target.Kind<>N'Revised' OR ISNULL(target.PreviousVersionId,0)<>old.ID OR target.CurrencyId<>old.CurrencyId
   OR target.CreatedBy<>i.AppliedBy OR ISNULL(target.SubmittedBy,0)<>i.AppliedBy OR ISNULL(target.ApprovedBy,0)<>d.CreatedBy OR target.Reason<>i.Reason
   OR ISNULL(p.Mode,N'')<>N'IndependentApproval' OR p.ConcurrencyToken<>r.PolicyToken)
  THROW 51972,'Applied transfer must match its independent decision, explicit policy and resulting baseline.',1;
 -- The entire allocation vector must be an exact clone with just the approved
 -- two-line delta. Counts plus every scoped original dimension rule out additions,
 -- omissions, hidden quantity/rate changes and altered contingency classification.
 IF EXISTS(SELECT 1 FROM inserted i WHERE
  (SELECT COUNT(*) FROM dbo.ConstructionBudgetLines l WHERE l.BudgetVersionId=i.OriginalBudgetVersionId AND l.CompanyID=i.CompanyID AND l.ProjectId=i.ProjectId)
  <>(SELECT COUNT(*) FROM dbo.ConstructionBudgetLines l WHERE l.BudgetVersionId=i.ResultBudgetVersionId AND l.CompanyID=i.CompanyID AND l.ProjectId=i.ProjectId))
  THROW 51972,'Transfer result must retain the original allocation dimensions.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionBudgetTransferRequests r ON r.ID=i.TransferRequestId
  JOIN dbo.ConstructionBudgetLines old ON old.BudgetVersionId=i.OriginalBudgetVersionId AND old.CompanyID=i.CompanyID AND old.ProjectId=i.ProjectId
  LEFT JOIN dbo.ConstructionBudgetLines target ON target.BudgetVersionId=i.ResultBudgetVersionId AND target.CompanyID=i.CompanyID AND target.ProjectId=i.ProjectId
   AND target.WbsNodeId=old.WbsNodeId AND target.CostCodeId=old.CostCodeId
  WHERE target.ID IS NULL OR target.Amount<>CASE WHEN old.ID=r.FromLineId THEN r.FromAfter WHEN old.ID=r.ToLineId THEN r.ToAfter ELSE old.Amount END
   OR ISNULL(target.PreviousAmount,-1)<>old.Amount OR target.CostCategory<>old.CostCategory OR target.IsContingency<>old.IsContingency
   OR ISNULL(target.Quantity,-1)<>ISNULL(old.Quantity,-1) OR ISNULL(target.Rate,-1)<>ISNULL(old.Rate,-1))
  THROW 51972,'Transfer result must apply only the authorized amount delta.',1;
END;
GO
COMMIT TRANSACTION;
