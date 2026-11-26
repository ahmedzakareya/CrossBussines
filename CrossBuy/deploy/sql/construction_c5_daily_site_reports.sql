-- C5 foundation: structured daily site report plus manpower and equipment evidence.
-- Additive and rerunnable. This file is authored for review; it is not applied by this change.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.ConstructionSites','U') IS NULL OR OBJECT_ID('dbo.ConstructionWbsNodes','U') IS NULL OR OBJECT_ID('dbo.BoqItems','U') IS NULL
 THROW 52000,'Daily site reports require the Construction C1 structure schema.',1;

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.BoqItems') AND name='UX_BoqItems_Id_Project_Company')
 CREATE UNIQUE INDEX UX_BoqItems_Id_Project_Company ON dbo.BoqItems(ID,ProjectId,CompanyID);

IF OBJECT_ID('dbo.ConstructionDailySiteReports','U') IS NULL
 CREATE TABLE dbo.ConstructionDailySiteReports(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDailySiteReports PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportDate date NOT NULL,
  RevisionNo int NOT NULL CONSTRAINT DF_ConstructionDailySiteReports_RevisionNo DEFAULT(1),
  AmendsReportId int NULL, AmendmentReason nvarchar(1000) NULL,
  WeatherCondition nvarchar(80) NULL, TemperatureC decimal(9,3) NULL, RainfallStopped bit NOT NULL,
  WorkStartTime time NULL, WorkEndTime time NULL, TotalWorkingHours decimal(9,3) NULL,
  ShiftCount int NOT NULL, Status nvarchar(20) NOT NULL,
  NextDayPlan nvarchar(2000) NULL, GeneralRemarks nvarchar(4000) NULL,
  CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  SubmittedBy int NULL, SubmittedAt datetime2 NULL, ReviewedBy int NULL, ReviewedAt datetime2 NULL,
  ApprovedBy int NULL, ApprovedAt datetime2 NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT AK_ConstructionDailySiteReports_ID_Project_Company UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT AK_ConstructionDailySiteReports_ID_Site_Project_Company UNIQUE(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT AK_ConstructionDailySiteReports_ID_Site_Project_Company_Date UNIQUE(ID,SiteId,ProjectId,CompanyID,ReportDate),
  CONSTRAINT UX_ConstructionDailySiteReports_SiteDateRevision UNIQUE(CompanyID,ProjectId,SiteId,ReportDate,RevisionNo),
  CONSTRAINT FK_ConstructionDailySiteReports_AmendsSameDate FOREIGN KEY(AmendsReportId,SiteId,ProjectId,CompanyID,ReportDate)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID,ReportDate),
  CONSTRAINT CK_ConstructionDailySiteReports_Revision CHECK(RevisionNo>=1 AND
   ((RevisionNo=1 AND AmendsReportId IS NULL AND AmendmentReason IS NULL) OR
    (RevisionNo>1 AND AmendsReportId IS NOT NULL AND AmendmentReason IS NOT NULL AND LEN(LTRIM(RTRIM(AmendmentReason)))>0))),
  CONSTRAINT FK_ConstructionDailySiteReports_Project FOREIGN KEY(ProjectId,CompanyID) REFERENCES dbo.Projects(ID,CompanyID),
  CONSTRAINT FK_ConstructionDailySiteReports_Site FOREIGN KEY(SiteId,ProjectId,CompanyID) REFERENCES dbo.ConstructionSites(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDailySiteReports_Status CHECK(Status IN(N'Draft',N'Submitted',N'Reviewed',N'Approved')),
  CONSTRAINT CK_ConstructionDailySiteReports_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND CreatedBy>0
   AND ShiftCount BETWEEN 1 AND 4 AND (TemperatureC IS NULL OR TemperatureC BETWEEN -80 AND 80)
   AND (TotalWorkingHours IS NULL OR TotalWorkingHours BETWEEN 0 AND 24)
   AND (WorkStartTime IS NULL OR WorkEndTime IS NULL OR WorkEndTime>=WorkStartTime))
 );

-- Upgrade databases created by an earlier C5 draft while keeping existing reports as revision 1.
IF COL_LENGTH('dbo.ConstructionDailySiteReports','RevisionNo') IS NULL
 ALTER TABLE dbo.ConstructionDailySiteReports ADD RevisionNo int NOT NULL
  CONSTRAINT DF_ConstructionDailySiteReports_RevisionNo DEFAULT(1) WITH VALUES;
IF COL_LENGTH('dbo.ConstructionDailySiteReports','AmendsReportId') IS NULL
 ALTER TABLE dbo.ConstructionDailySiteReports ADD AmendsReportId int NULL;
IF COL_LENGTH('dbo.ConstructionDailySiteReports','AmendmentReason') IS NULL
 ALTER TABLE dbo.ConstructionDailySiteReports ADD AmendmentReason nvarchar(1000) NULL;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='AK_ConstructionDailySiteReports_ID_Site_Project_Company_Date')
 ALTER TABLE dbo.ConstructionDailySiteReports ADD CONSTRAINT AK_ConstructionDailySiteReports_ID_Site_Project_Company_Date
  UNIQUE(ID,SiteId,ProjectId,CompanyID,ReportDate);
IF EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='UX_ConstructionDailySiteReports_SiteDate')
 ALTER TABLE dbo.ConstructionDailySiteReports DROP CONSTRAINT UX_ConstructionDailySiteReports_SiteDate;
IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='UX_ConstructionDailySiteReports_SiteDateRevision')
 ALTER TABLE dbo.ConstructionDailySiteReports ADD CONSTRAINT UX_ConstructionDailySiteReports_SiteDateRevision
  UNIQUE(CompanyID,ProjectId,SiteId,ReportDate,RevisionNo);
IF EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='FK_ConstructionDailySiteReports_Amends')
 ALTER TABLE dbo.ConstructionDailySiteReports DROP CONSTRAINT FK_ConstructionDailySiteReports_Amends;
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='FK_ConstructionDailySiteReports_AmendsSameDate')
 ALTER TABLE dbo.ConstructionDailySiteReports ADD CONSTRAINT FK_ConstructionDailySiteReports_AmendsSameDate
  FOREIGN KEY(AmendsReportId,SiteId,ProjectId,CompanyID,ReportDate)
  REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID,ReportDate);
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionDailySiteReports')
 AND name='CK_ConstructionDailySiteReports_Revision')
 ALTER TABLE dbo.ConstructionDailySiteReports ADD CONSTRAINT CK_ConstructionDailySiteReports_Revision
  CHECK(RevisionNo>=1 AND ((RevisionNo=1 AND AmendsReportId IS NULL AND AmendmentReason IS NULL) OR
   (RevisionNo>1 AND AmendsReportId IS NOT NULL AND AmendmentReason IS NOT NULL AND LEN(LTRIM(RTRIM(AmendmentReason)))>0)));

IF OBJECT_ID('dbo.ConstructionDsrManpowerLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrManpowerLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrManpowerLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  Source nvarchar(20) NOT NULL, Trade nvarchar(100) NOT NULL, EmployeeId int NULL, SubcontractId int NULL,
  Headcount int NOT NULL, WbsNodeId int NULL, RegularHours decimal(9,3) NOT NULL, OvertimeHours decimal(9,3) NOT NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrManpowerLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrManpowerLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrManpowerLines_Source CHECK((Source=N'OwnEmployee' AND EmployeeId IS NOT NULL AND SubcontractId IS NULL)
   OR (Source=N'Subcontractor' AND EmployeeId IS NULL AND SubcontractId IS NOT NULL)),
  CONSTRAINT CK_ConstructionDsrManpowerLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND Headcount>0
   AND RegularHours>=0 AND OvertimeHours>=0 AND LEN(LTRIM(RTRIM(Trade)))>0)
 );

IF OBJECT_ID('dbo.ConstructionDsrEquipmentLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrEquipmentLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrEquipmentLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  FixedAssetId int NULL, SubcontractId int NULL, EquipmentDescription nvarchar(200) NULL,
  OperatorEmployeeId int NULL, WbsNodeId int NULL,
  WorkingHours decimal(9,3) NOT NULL, IdleHours decimal(9,3) NOT NULL, DowntimeHours decimal(9,3) NOT NULL,
  DowntimeReason nvarchar(1000) NULL, MeterStart decimal(18,3) NULL, MeterEnd decimal(18,3) NULL,
  FuelQuantity decimal(18,3) NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrEquipmentLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrEquipmentLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrEquipmentLines_Identity CHECK((FixedAssetId IS NOT NULL AND SubcontractId IS NULL)
   OR (FixedAssetId IS NULL AND SubcontractId IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(EquipmentDescription,N''))))>0)),
  CONSTRAINT CK_ConstructionDsrEquipmentLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0
   AND WorkingHours>=0 AND IdleHours>=0 AND DowntimeHours>=0
   AND (MeterStart IS NULL OR MeterEnd IS NULL OR MeterEnd>=MeterStart)
   AND (FuelQuantity IS NULL OR FuelQuantity>=0))
 );


IF OBJECT_ID('dbo.ConstructionDsrQuantityLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrQuantityLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrQuantityLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  WbsNodeId int NOT NULL, BoqItemId int NOT NULL, QuantityExecuted decimal(18,3) NOT NULL,
  Unit nvarchar(40) NOT NULL, Location nvarchar(200) NULL, Remarks nvarchar(1000) NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrQuantityLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrQuantityLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrQuantityLines_Boq FOREIGN KEY(BoqItemId,ProjectId,CompanyID)
   REFERENCES dbo.BoqItems(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrQuantityLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND QuantityExecuted>0 AND LEN(LTRIM(RTRIM(Unit)))>0)
 );
 CREATE INDEX IX_ConstructionDsrQuantityLines_Report ON dbo.ConstructionDsrQuantityLines(CompanyID,ReportId,BoqItemId,WbsNodeId);

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrMaterialLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrMaterialLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  Direction nvarchar(20) NOT NULL, GoodsReceiptLineId int NULL, ProjectMaterialIssueLineId int NULL,
  ItemId int NOT NULL, Quantity decimal(18,3) NOT NULL, WbsNodeId int NULL,
  DeliveryNoteNo nvarchar(100) NULL, Remarks nvarchar(1000) NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrMaterialLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrMaterialLines_ReceiptLine FOREIGN KEY(GoodsReceiptLineId) REFERENCES dbo.GoodsReceiptLines(ID),
  CONSTRAINT FK_ConstructionDsrMaterialLines_IssueLine FOREIGN KEY(ProjectMaterialIssueLineId) REFERENCES dbo.ProjectMaterialIssueLines(ID),
  CONSTRAINT FK_ConstructionDsrMaterialLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrMaterialLines_Source CHECK(
   (Direction=N'Received' AND GoodsReceiptLineId IS NOT NULL AND ProjectMaterialIssueLineId IS NULL)
   OR (Direction=N'Consumed' AND GoodsReceiptLineId IS NULL AND ProjectMaterialIssueLineId IS NOT NULL)),
  CONSTRAINT CK_ConstructionDsrMaterialLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND ItemId>0 AND Quantity>0)
 );
 CREATE INDEX IX_ConstructionDsrMaterialLines_Report ON dbo.ConstructionDsrMaterialLines(CompanyID,ReportId,Direction);

IF OBJECT_ID('dbo.ConstructionDsrEventLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrEventLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrEventLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  EventType nvarchar(20) NOT NULL, Description nvarchar(2000) NOT NULL, Severity nvarchar(20) NOT NULL,
  ResponsibleParty nvarchar(100) NULL, WbsNodeId int NULL, StartedAt datetime2 NULL, EndedAt datetime2 NULL,
  NeededByDate datetime2 NULL, Reportable bit NOT NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrEventLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrEventLines_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrEventLines_Type CHECK(EventType IN(N'Delay',N'Blocker',N'Incident')),
  CONSTRAINT CK_ConstructionDsrEventLines_Severity CHECK(Severity IN(N'Low',N'Medium',N'High',N'Critical')),
  CONSTRAINT CK_ConstructionDsrEventLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0
   AND LEN(LTRIM(RTRIM(Description)))>0 AND (StartedAt IS NULL OR EndedAt IS NULL OR EndedAt>=StartedAt))
 );
 CREATE INDEX IX_ConstructionDsrEventLines_Report ON dbo.ConstructionDsrEventLines(CompanyID,ReportId,EventType);

IF OBJECT_ID('dbo.ConstructionDsrAttachments','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrAttachments(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrAttachments PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  LibraryItemId int NOT NULL, Kind nvarchar(20) NOT NULL, Caption nvarchar(250) NULL,
  WbsNodeId int NULL, TakenAt datetime2 NULL, Latitude decimal(9,6) NULL, Longitude decimal(9,6) NULL,
  AddedAt datetime2 NOT NULL, AddedBy int NOT NULL,
  CONSTRAINT FK_ConstructionDsrAttachments_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionDsrAttachments_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrAttachments_Kind CHECK(Kind IN(N'Photo',N'Document')),
  CONSTRAINT CK_ConstructionDsrAttachments_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND ReportId>0 AND LibraryItemId>0 AND AddedBy>0
   AND (Kind=N'Photo' OR (WbsNodeId IS NULL AND TakenAt IS NULL AND Latitude IS NULL AND Longitude IS NULL)))
 );
IF COL_LENGTH('dbo.ConstructionDsrAttachments','Kind') IS NULL
 ALTER TABLE dbo.ConstructionDsrAttachments ADD Kind nvarchar(20) NOT NULL CONSTRAINT DF_ConstructionDsrAttachments_Kind DEFAULT(N'Document') WITH VALUES;
IF COL_LENGTH('dbo.ConstructionDsrAttachments','WbsNodeId') IS NULL
 ALTER TABLE dbo.ConstructionDsrAttachments ADD WbsNodeId int NULL;
IF COL_LENGTH('dbo.ConstructionDsrAttachments','TakenAt') IS NULL
 ALTER TABLE dbo.ConstructionDsrAttachments ADD TakenAt datetime2 NULL;
IF COL_LENGTH('dbo.ConstructionDsrAttachments','Latitude') IS NULL
 ALTER TABLE dbo.ConstructionDsrAttachments ADD Latitude decimal(9,6) NULL;
IF COL_LENGTH('dbo.ConstructionDsrAttachments','Longitude') IS NULL
 ALTER TABLE dbo.ConstructionDsrAttachments ADD Longitude decimal(9,6) NULL;
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_ConstructionDsrAttachments_Wbs')
 ALTER TABLE dbo.ConstructionDsrAttachments ADD CONSTRAINT FK_ConstructionDsrAttachments_Wbs FOREIGN KEY(WbsNodeId,ProjectId,CompanyID)
  REFERENCES dbo.ConstructionWbsNodes(ID,ProjectId,CompanyID);
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='CK_ConstructionDsrAttachments_Kind')
 ALTER TABLE dbo.ConstructionDsrAttachments ADD CONSTRAINT CK_ConstructionDsrAttachments_Kind CHECK(Kind IN(N'Photo',N'Document'));
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='CK_ConstructionDsrAttachments_Geo')
 ALTER TABLE dbo.ConstructionDsrAttachments ADD CONSTRAINT CK_ConstructionDsrAttachments_Geo CHECK(
  (Latitude IS NULL AND Longitude IS NULL) OR (Latitude BETWEEN -90 AND 90 AND Longitude BETWEEN -180 AND 180));
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionDsrAttachments') AND name='UX_ConstructionDsrAttachments_Report_File')
 CREATE UNIQUE INDEX UX_ConstructionDsrAttachments_Report_File ON dbo.ConstructionDsrAttachments(CompanyID,ReportId,LibraryItemId);

IF OBJECT_ID('dbo.ConstructionDsrVisitorLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrVisitorLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrVisitorLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  VisitorName nvarchar(150) NOT NULL, Organisation nvarchar(150) NULL, Purpose nvarchar(500) NOT NULL,
  TimeIn datetime2 NULL, TimeOut datetime2 NULL, RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrVisitorLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrVisitorLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND ReportId>0
   AND LEN(LTRIM(RTRIM(VisitorName)))>0 AND LEN(LTRIM(RTRIM(Purpose)))>0 AND (TimeIn IS NULL OR TimeOut IS NULL OR TimeOut>=TimeIn))
 );
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionDsrVisitorLines') AND name='IX_ConstructionDsrVisitorLines_Report')
 CREATE INDEX IX_ConstructionDsrVisitorLines_Report ON dbo.ConstructionDsrVisitorLines(CompanyID,ReportId);

IF OBJECT_ID('dbo.ConstructionDsrInstructionLines','U') IS NULL
 CREATE TABLE dbo.ConstructionDsrInstructionLines(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionDsrInstructionLines PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, SiteId int NOT NULL, ReportId int NOT NULL,
  Source nvarchar(20) NOT NULL, InstructionReference nvarchar(100) NULL, Description nvarchar(2000) NOT NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT FK_ConstructionDsrInstructionLines_Report FOREIGN KEY(ReportId,SiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionDailySiteReports(ID,SiteId,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionDsrInstructionLines_Source CHECK(Source IN(N'Consultant',N'Client',N'Internal')),
  CONSTRAINT CK_ConstructionDsrInstructionLines_Values CHECK(CompanyID>0 AND ProjectId>0 AND SiteId>0 AND ReportId>0
   AND LEN(LTRIM(RTRIM(Description)))>0)
 );
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionDsrInstructionLines') AND name='IX_ConstructionDsrInstructionLines_Report')
 CREATE INDEX IX_ConstructionDsrInstructionLines_Report ON dbo.ConstructionDsrInstructionLines(CompanyID,ReportId);
COMMIT TRANSACTION;
GO



