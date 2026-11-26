-- R7 structural foundation. Preserve schema-before-code activation ordering.
-- Additive/rerunnable. Explicit company depth choice; no company settings seed.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.Projects','U') IS NULL OR OBJECT_ID('dbo.Warehouses','U') IS NULL
 OR OBJECT_ID('dbo.Employee','U') IS NULL OR OBJECT_ID('dbo.Accounts','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionAuditEntries','U') IS NULL
 THROW 51100,'Construction structure requires the existing masters and C1 audit schema.',1;
-- C1's authored audit payloads are nvarchar(1000), while new dimension/version snapshots may be
-- longer (including escaped Arabic). Widen losslessly; never truncate or rewrite history rows.
IF COL_LENGTH('dbo.ConstructionAuditEntries','OldValue') IS NULL OR COL_LENGTH('dbo.ConstructionAuditEntries','NewValue') IS NULL
 THROW 51100,'The C1 audit payload columns are required.',1;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionAuditEntries') AND name IN('OldValue','NewValue') AND (system_type_id<>231 OR is_nullable<>1))
 THROW 51100,'Unexpected audit payload schema; manual reconciliation is required.',1;
IF COL_LENGTH('dbo.ConstructionAuditEntries','OldValue')<>-1
 ALTER TABLE dbo.ConstructionAuditEntries ALTER COLUMN OldValue nvarchar(max) NULL;
IF COL_LENGTH('dbo.ConstructionAuditEntries','NewValue')<>-1
 ALTER TABLE dbo.ConstructionAuditEntries ALTER COLUMN NewValue nvarchar(max) NULL;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.Projects') AND name='AK_Projects_ID_CompanyID')
 ALTER TABLE dbo.Projects ADD CONSTRAINT AK_Projects_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.Warehouses') AND name='AK_Warehouses_ID_CompanyID')
 ALTER TABLE dbo.Warehouses ADD CONSTRAINT AK_Warehouses_ID_CompanyID UNIQUE(ID,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.Employee') AND name='AK_Employee_ID_EmpCompanyID')
 ALTER TABLE dbo.Employee ADD CONSTRAINT AK_Employee_ID_EmpCompanyID UNIQUE(ID,EmpCompanyID);

IF OBJECT_ID('dbo.ConstructionStructureSettings','U') IS NULL
 CREATE TABLE dbo.ConstructionStructureSettings(
  CompanyID int NOT NULL CONSTRAINT PK_ConstructionStructureSettings PRIMARY KEY,
  MaxWbsDepth int NOT NULL,
  Reason nvarchar(1000) NOT NULL, UpdatedBy int NOT NULL, UpdatedAt datetime2 NOT NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT CK_ConstructionStructureSettings_Company CHECK(CompanyID>0),
  CONSTRAINT CK_ConstructionStructureSettings_Depth CHECK(MaxWbsDepth BETWEEN 1 AND 32),
  CONSTRAINT CK_ConstructionStructureSettings_Reason CHECK(LEN(LTRIM(RTRIM(Reason)))>0)
 );
IF OBJECT_ID('dbo.ConstructionWbsNodes','U') IS NULL
 CREATE TABLE dbo.ConstructionWbsNodes(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionWbsNodes PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, ParentId int NULL,
  Code nvarchar(50) NOT NULL, Name nvarchar(200) NOT NULL, NameEn nvarchar(200) NULL,
  Path nvarchar(1000) NOT NULL, Depth int NOT NULL, Sequence int NOT NULL,
  Status nvarchar(20) NOT NULL, StartDate datetime2 NULL, EndDate datetime2 NULL,
  ResponsibleEmployeeId int NULL, ProgressMethod nvarchar(30) NOT NULL,
  OwnsBudget bit NOT NULL, OwnsCost bit NOT NULL,
  CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT AK_ConstructionWbsNodes_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UX_ConstructionWbsNodes_Code UNIQUE(CompanyID,ProjectId,Code),
  CONSTRAINT FK_ConstructionWbsNodes_Settings FOREIGN KEY(CompanyID) REFERENCES dbo.ConstructionStructureSettings(CompanyID),
  CONSTRAINT FK_ConstructionWbsNodes_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionWbsNodes_Parent FOREIGN KEY(ParentId,ProjectId,CompanyID) REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionWbsNodes_Employee FOREIGN KEY(ResponsibleEmployeeId,CompanyID) REFERENCES dbo.Employee(ID,EmpCompanyID),
  CONSTRAINT CK_ConstructionWbsNodes_Identity CHECK(ParentId IS NULL OR ParentId<>ID),
  CONSTRAINT CK_ConstructionWbsNodes_Depth CHECK(Depth BETWEEN 1 AND 32 AND ((ParentId IS NULL AND Depth=1) OR(ParentId IS NOT NULL AND Depth>1))),
  CONSTRAINT CK_ConstructionWbsNodes_Text CHECK(LEN(LTRIM(RTRIM(Code)))>0 AND LEN(LTRIM(RTRIM(Name)))>0 AND LEN(Path)>0),
  CONSTRAINT CK_ConstructionWbsNodes_Dates CHECK(StartDate IS NULL OR EndDate IS NULL OR EndDate>=StartDate),
  CONSTRAINT CK_ConstructionWbsNodes_Status CHECK(Status IN('Draft','Active','Suspended','Completed','Archived')),
  CONSTRAINT CK_ConstructionWbsNodes_Progress CHECK(ProgressMethod IN('Quantity','Milestone','WeightedActivity','ManualApproved'))
 );
IF OBJECT_ID('dbo.ConstructionCostCodes','U') IS NULL
 CREATE TABLE dbo.ConstructionCostCodes(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionCostCodes PRIMARY KEY,
  CompanyID int NOT NULL, ParentId int NULL,
  Code nvarchar(50) NOT NULL, Name nvarchar(200) NOT NULL, NameEn nvarchar(200) NULL,
  Category nvarchar(30) NOT NULL, DefaultAccountId int NULL,
  IsActive bit NOT NULL, AllowDirectPosting bit NOT NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT AK_ConstructionCostCodes_ID_CompanyID_Category UNIQUE(ID,CompanyID,Category),
  CONSTRAINT UX_ConstructionCostCodes_Code UNIQUE(CompanyID,Code),
  CONSTRAINT FK_ConstructionCostCodes_Parent FOREIGN KEY(ParentId,CompanyID,Category) REFERENCES dbo.ConstructionCostCodes(ID,CompanyID,Category),
  CONSTRAINT FK_ConstructionCostCodes_Account FOREIGN KEY(DefaultAccountId) REFERENCES dbo.Accounts(ID),
  CONSTRAINT CK_ConstructionCostCodes_Company CHECK(CompanyID>0),
  CONSTRAINT CK_ConstructionCostCodes_Parent CHECK(ParentId IS NULL OR ParentId<>ID),
  CONSTRAINT CK_ConstructionCostCodes_Text CHECK(LEN(LTRIM(RTRIM(Code)))>0 AND LEN(LTRIM(RTRIM(Name)))>0),
  CONSTRAINT CK_ConstructionCostCodes_Category CHECK(Category IN('Material','Labor','Equipment','Subcontractor','SiteExpense','Transportation','Accommodation','Overhead','Contingency','Other'))
 );
IF OBJECT_ID('dbo.ConstructionSites','U') IS NULL
 CREATE TABLE dbo.ConstructionSites(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionSites PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL,
  Code nvarchar(50) NOT NULL, Name nvarchar(200) NOT NULL, NameEn nvarchar(200) NULL,
  Address nvarchar(1000) NULL, WarehouseId int NULL, IsActive bit NOT NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT AK_ConstructionSites_ID_ProjectId_CompanyID UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UX_ConstructionSites_Code UNIQUE(CompanyID,ProjectId,Code),
  CONSTRAINT FK_ConstructionSites_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionSites_Warehouse FOREIGN KEY(WarehouseId,CompanyID) REFERENCES dbo.Warehouses(ID,CompanyID),
  CONSTRAINT CK_ConstructionSites_Text CHECK(LEN(LTRIM(RTRIM(Code)))>0 AND LEN(LTRIM(RTRIM(Name)))>0)
 );
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionStructureSettings_Depth ON dbo.ConstructionStructureSettings AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionWbsNodes n ON n.CompanyID=i.CompanyID WHERE n.Depth>i.MaxWbsDepth)
  THROW 51101,'The configured depth cannot exclude an existing WBS node.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionWbsNodes_Hierarchy ON dbo.ConstructionWbsNodes AFTER INSERT,UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionStructureSettings s ON s.CompanyID=i.CompanyID WHERE i.Depth>s.MaxWbsDepth)
  THROW 51102,'The WBS depth exceeds the explicit company setting.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionWbsNodes p ON p.ID=i.ParentId WHERE i.Depth<>p.Depth+1)
  OR EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionWbsNodes c ON c.ParentId=i.ID WHERE c.Depth<>i.Depth+1)
  THROW 51103,'WBS parent and child depths are inconsistent.',1;
 -- Structural relocation is deliberately unavailable until its preview/permission workflow exists.
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE ISNULL(i.ParentId,0)<>ISNULL(d.ParentId,0))
  THROW 51104,'WBS relocation requires a separately authorized impact workflow.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE i.Status<>d.Status AND NOT(
  (d.Status='Draft' AND i.Status IN('Active','Archived')) OR(d.Status='Active' AND i.Status IN('Suspended','Completed')) OR
  (d.Status='Suspended' AND i.Status IN('Active','Archived')) OR(d.Status='Completed' AND i.Status='Archived')))
  THROW 51105,'Invalid WBS status transition.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCostCodes_AccountCompany ON dbo.ConstructionCostCodes AFTER INSERT,UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.Accounts a ON a.ID=i.DefaultAccountId WHERE a.CompanyID<>i.CompanyID OR a.CompanyID IS NULL)
  THROW 51106,'The default account must belong to the cost code company.',1;
END;
GO
COMMIT TRANSACTION;
