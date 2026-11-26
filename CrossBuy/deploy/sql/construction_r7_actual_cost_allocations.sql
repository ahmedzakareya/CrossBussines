-- Analytical expense-line dimensions. GL remains authoritative; no financial postings or inferred currency.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.JournalEntries','FunctionalCurrencyId') IS NULL OR OBJECT_ID('dbo.ConstructionWbsNodes','U') IS NULL
 THROW 51700,'Apply journal valuation snapshots and construction dimensions first.',1;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalEntries') AND name='AK_JournalEntries_ID_CompanyID')
 ALTER TABLE dbo.JournalEntries ADD CONSTRAINT AK_JournalEntries_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalEntryLines') AND name='AK_JournalEntryLines_ID_JournalEntryId')
 ALTER TABLE dbo.JournalEntryLines ADD CONSTRAINT AK_JournalEntryLines_ID_JournalEntryId UNIQUE(ID,JournalEntryId);
IF OBJECT_ID('dbo.ConstructionActualCostAllocations','U') IS NULL
 CREATE TABLE dbo.ConstructionActualCostAllocations(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionActualCostAllocations PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, JournalEntryId int NOT NULL, JournalEntryLineId int NOT NULL,
  FunctionalCurrencyId int NOT NULL, FunctionalCurrencyDecimals int NOT NULL, NetBaseAmount decimal(28,8) NOT NULL,
  RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, SourceVersion nvarchar(64) NOT NULL,
  Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL, IsSealed bit NOT NULL,
  CONSTRAINT AK_ConstructionActualCostAllocations_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionActualCostAllocations_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionActualCostAllocations_Source UNIQUE(CompanyID,JournalEntryLineId),
  CONSTRAINT CK_ConstructionActualCostAllocations_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0 AND NetBaseAmount<>0 AND FunctionalCurrencyDecimals BETWEEN 0 AND 8 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND LEN(LTRIM(RTRIM(Reason)))>0),
  CONSTRAINT FK_ConstructionActualCostAllocations_Entry FOREIGN KEY(JournalEntryId,CompanyID) REFERENCES dbo.JournalEntries(ID,CompanyID),
  CONSTRAINT FK_ConstructionActualCostAllocations_Source FOREIGN KEY(JournalEntryLineId,JournalEntryId) REFERENCES dbo.JournalEntryLines(ID,JournalEntryId),
  CONSTRAINT FK_ConstructionActualCostAllocations_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionActualCostAllocations_Currency FOREIGN KEY(FunctionalCurrencyId) REFERENCES dbo.Currencies(ID)
 );
IF OBJECT_ID('dbo.ConstructionActualCostAllocationLines','U') IS NULL
 CREATE TABLE dbo.ConstructionActualCostAllocationLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionActualCostAllocationLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, AllocationId int NOT NULL, WbsNodeId int NOT NULL, CostCodeId int NOT NULL,
  CostCategory nvarchar(30) NOT NULL, NetBaseAmount decimal(28,8) NOT NULL,
  CONSTRAINT UQ_ConstructionActualCostAllocationLines_Dimension UNIQUE(AllocationId,WbsNodeId,CostCodeId),
  CONSTRAINT CK_ConstructionActualCostAllocationLines_Amount CHECK(NetBaseAmount<>0),
  CONSTRAINT FK_ConstructionActualCostAllocationLines_Header FOREIGN KEY(AllocationId,ProjectId,CompanyID) REFERENCES dbo.ConstructionActualCostAllocations(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionActualCostAllocationLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionActualCostAllocationLines_Code FOREIGN KEY(CostCodeId,CompanyID,CostCategory) REFERENCES dbo.ConstructionCostCodes(ID,CompanyID,Category)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionActualCostAllocations_Insert ON dbo.ConstructionActualCostAllocations AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalEntries e ON e.ID=i.JournalEntryId JOIN dbo.JournalEntryLines l ON l.ID=i.JournalEntryLineId
  LEFT JOIN dbo.Accounts a ON a.ID=l.AccountId AND a.CompanyID=i.CompanyID LEFT JOIN dbo.AccountTypes t ON t.ID=a.AccountTypeId
  WHERE i.IsSealed=1 OR e.Status NOT IN('Posted','Reversed') OR l.ProjectId IS NULL OR l.ProjectId<>i.ProjectId
   OR e.FunctionalCurrencyId IS NULL OR e.FunctionalCurrencyDecimals IS NULL OR i.FunctionalCurrencyId<>e.FunctionalCurrencyId
   OR i.FunctionalCurrencyDecimals<>e.FunctionalCurrencyDecimals OR i.NetBaseAmount<>l.Debit-l.Credit
   OR t.Code IS NULL OR t.Code<>'EXP' OR t.StatementType IS NULL OR t.StatementType<>'IncomeStatement')
  THROW 51701,'Actual allocation requires a proven functional-currency expense GL source in this project.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionActualCostAllocations_Seal ON dbo.ConstructionActualCostAllocations AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ID=d.ID WHERE i.ID IS NULL OR d.IsSealed=1 OR i.IsSealed<>1)
  THROW 51702,'Actual allocation history is immutable; only sealing a new allocation is allowed.',1;
 IF EXISTS(SELECT ID,CompanyID,ProjectId,JournalEntryId,JournalEntryLineId,FunctionalCurrencyId,FunctionalCurrencyDecimals,NetBaseAmount,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt FROM inserted
  EXCEPT SELECT ID,CompanyID,ProjectId,JournalEntryId,JournalEntryLineId,FunctionalCurrencyId,FunctionalCurrencyDecimals,NetBaseAmount,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt FROM deleted)
  THROW 51702,'Actual allocation source evidence cannot be rewritten.',1;
 IF EXISTS(SELECT 1 FROM inserted i WHERE NOT EXISTS(SELECT 1 FROM dbo.ConstructionActualCostAllocationLines l WHERE l.AllocationId=i.ID)
  OR i.NetBaseAmount<>(SELECT SUM(l.NetBaseAmount) FROM dbo.ConstructionActualCostAllocationLines l WHERE l.AllocationId=i.ID))
  THROW 51703,'Actual allocation must reconcile exactly to its GL net debit.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionActualCostAllocationLines_Insert ON dbo.ConstructionActualCostAllocationLines AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionActualCostAllocations h ON h.ID=i.AllocationId
  WHERE h.IsSealed=1 OR SIGN(i.NetBaseAmount)<>SIGN(h.NetBaseAmount))
  THROW 51703,'Actual allocation splits must preserve the GL sign and an unsealed header.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionActualCostAllocationLines_Immutable ON dbo.ConstructionActualCostAllocationLines AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51702,'Actual allocation line history cannot be changed or deleted.',1;
END;
GO
COMMIT TRANSACTION;
