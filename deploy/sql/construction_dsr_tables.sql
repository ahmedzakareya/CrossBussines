-- Construction daily site report (DSR): nine tables the EF model has always described and the
-- database never had. There was no slice for them in deploy/sql at all, so every query against the
-- feature met SQL-208 and the daily site report could not run.
--
-- Found 2026-11-25 while clearing ef_precision_vs_db: fourteen of its fifty-five "precision
-- divergences" were not precision at all - they were columns on tables that do not exist.
--
-- THE DDL BELOW IS EF'S OWN, taken from GenerateCreateScript() rather than written by hand. These
-- entities carry composite alternate keys and composite foreign keys, and a slice typed out beside
-- them is how a column ends up one decimal narrower than the model with nothing to catch it.
-- The guard is ef_precision_vs_db itself: after this runs it must report zero.
--
-- Idempotent: every object is created only when absent, so a re-run is a no-op.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- The model declares this alternate key on BoqItems (a DSR quantity line points at a BOQ item
-- inside its own project and company). It was never created, and the DSR foreign keys need it.
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'AK_BoqItems_ID_ProjectId_CompanyID')
BEGIN
    IF EXISTS (SELECT ID, ProjectId, CompanyID FROM dbo.BoqItems GROUP BY ID, ProjectId, CompanyID HAVING COUNT(*) > 1)
        PRINT 'REFUSED: BoqItems has duplicate (ID, ProjectId, CompanyID)';
    ELSE
    BEGIN
        ALTER TABLE dbo.BoqItems ADD CONSTRAINT AK_BoqItems_ID_ProjectId_CompanyID UNIQUE (ID, ProjectId, CompanyID);
        PRINT 'AK_BoqItems_ID_ProjectId_CompanyID created';
    END
END
ELSE PRINT 'AK_BoqItems_ID_ProjectId_CompanyID already present';
GO

IF OBJECT_ID('dbo.ConstructionDailySiteReports', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDailySiteReports] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportDate] datetime2 NOT NULL,
        [RevisionNo] int NOT NULL,
        [AmendsReportId] int NULL,
        [AmendmentReason] nvarchar(1000) NULL,
        [WeatherCondition] nvarchar(80) NULL,
        [TemperatureC] decimal(9,3) NULL,
        [RainfallStopped] bit NOT NULL,
        [WorkStartTime] time NULL,
        [WorkEndTime] time NULL,
        [TotalWorkingHours] decimal(9,3) NULL,
        [ShiftCount] int NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [NextDayPlan] nvarchar(2000) NULL,
        [GeneralRemarks] nvarchar(4000) NULL,
        [CreatedBy] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [SubmittedBy] int NULL,
        [SubmittedAt] datetime2 NULL,
        [ReviewedBy] int NULL,
        [ReviewedAt] datetime2 NULL,
        [ApprovedBy] int NULL,
        [ApprovedAt] datetime2 NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDailySiteReports] PRIMARY KEY ([ID]),
        CONSTRAINT [AK_ConstructionDailySiteReports_ID_ProjectId_CompanyID] UNIQUE ([ID], [ProjectId], [CompanyID]),
        CONSTRAINT [AK_ConstructionDailySiteReports_ID_SiteId_ProjectId_CompanyID] UNIQUE ([ID], [SiteId], [ProjectId], [CompanyID]),
        CONSTRAINT [AK_ConstructionDailySiteReports_ID_SiteId_ProjectId_CompanyID_ReportDate] UNIQUE ([ID], [SiteId], [ProjectId], [CompanyID], [ReportDate]),
        CONSTRAINT [FK_ConstructionDailySiteReports_ConstructionDailySiteReports_AmendsReportId_SiteId_ProjectId_CompanyID_ReportDate] FOREIGN KEY ([AmendsReportId], [SiteId], [ProjectId], [CompanyID], [ReportDate]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID], [ReportDate]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDailySiteReports_ConstructionSites_SiteId_ProjectId_CompanyID] FOREIGN KEY ([SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionSites] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDailySiteReports_Projects_ProjectId_CompanyID] FOREIGN KEY ([ProjectId], [CompanyID]) REFERENCES [Projects] ([ID], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDailySiteReports created';
END
ELSE PRINT 'ConstructionDailySiteReports already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrInstructionLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrInstructionLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [Source] nvarchar(20) NOT NULL,
        [InstructionReference] nvarchar(100) NULL,
        [Description] nvarchar(2000) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrInstructionLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrInstructionLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrInstructionLines created';
END
ELSE PRINT 'ConstructionDsrInstructionLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrVisitorLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrVisitorLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [VisitorName] nvarchar(150) NOT NULL,
        [Organisation] nvarchar(150) NULL,
        [Purpose] nvarchar(500) NOT NULL,
        [TimeIn] datetime2 NULL,
        [TimeOut] datetime2 NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrVisitorLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrVisitorLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrVisitorLines created';
END
ELSE PRINT 'ConstructionDsrVisitorLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrAttachments', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrAttachments] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [LibraryItemId] int NOT NULL,
        [Kind] nvarchar(20) NOT NULL,
        [Caption] nvarchar(250) NULL,
        [WbsNodeId] int NULL,
        [TakenAt] datetime2 NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [AddedAt] datetime2 NOT NULL,
        [AddedBy] int NOT NULL,
        CONSTRAINT [PK_ConstructionDsrAttachments] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrAttachments_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrAttachments_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrAttachments created';
END
ELSE PRINT 'ConstructionDsrAttachments already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrEquipmentLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrEquipmentLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [FixedAssetId] int NULL,
        [SubcontractId] int NULL,
        [EquipmentDescription] nvarchar(200) NULL,
        [OperatorEmployeeId] int NULL,
        [WbsNodeId] int NULL,
        [WorkingHours] decimal(9,3) NOT NULL,
        [IdleHours] decimal(9,3) NOT NULL,
        [DowntimeHours] decimal(9,3) NOT NULL,
        [DowntimeReason] nvarchar(1000) NULL,
        [MeterStart] decimal(18,3) NULL,
        [MeterEnd] decimal(18,3) NULL,
        [FuelQuantity] decimal(18,3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrEquipmentLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrEquipmentLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrEquipmentLines_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrEquipmentLines created';
END
ELSE PRINT 'ConstructionDsrEquipmentLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrEventLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrEventLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [EventType] nvarchar(20) NOT NULL,
        [Description] nvarchar(2000) NOT NULL,
        [Severity] nvarchar(20) NOT NULL,
        [ResponsibleParty] nvarchar(100) NULL,
        [WbsNodeId] int NULL,
        [StartedAt] datetime2 NULL,
        [EndedAt] datetime2 NULL,
        [NeededByDate] datetime2 NULL,
        [Reportable] bit NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrEventLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrEventLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrEventLines_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrEventLines created';
END
ELSE PRINT 'ConstructionDsrEventLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrManpowerLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrManpowerLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [Source] nvarchar(20) NOT NULL,
        [Trade] nvarchar(100) NOT NULL,
        [EmployeeId] int NULL,
        [SubcontractId] int NULL,
        [Headcount] int NOT NULL,
        [WbsNodeId] int NULL,
        [RegularHours] decimal(9,3) NOT NULL,
        [OvertimeHours] decimal(9,3) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrManpowerLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrManpowerLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrManpowerLines_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrManpowerLines created';
END
ELSE PRINT 'ConstructionDsrManpowerLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrMaterialLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [Direction] nvarchar(20) NOT NULL,
        [GoodsReceiptLineId] int NULL,
        [ProjectMaterialIssueLineId] int NULL,
        [ItemId] int NOT NULL,
        [Quantity] decimal(18,3) NOT NULL,
        [WbsNodeId] int NULL,
        [DeliveryNoteNo] nvarchar(100) NULL,
        [Remarks] nvarchar(1000) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrMaterialLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrMaterialLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrMaterialLines_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrMaterialLines_GoodsReceiptLines_GoodsReceiptLineId] FOREIGN KEY ([GoodsReceiptLineId]) REFERENCES [GoodsReceiptLines] ([ID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrMaterialLines_ProjectMaterialIssueLines_ProjectMaterialIssueLineId] FOREIGN KEY ([ProjectMaterialIssueLineId]) REFERENCES [ProjectMaterialIssueLines] ([ID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrMaterialLines created';
END
ELSE PRINT 'ConstructionDsrMaterialLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDsrQuantityLines', 'U') IS NULL
BEGIN
    CREATE TABLE [ConstructionDsrQuantityLines] (
        [ID] int NOT NULL IDENTITY,
        [CompanyID] int NOT NULL,
        [ProjectId] int NOT NULL,
        [SiteId] int NOT NULL,
        [ReportId] int NOT NULL,
        [WbsNodeId] int NOT NULL,
        [BoqItemId] int NOT NULL,
        [QuantityExecuted] decimal(18,3) NOT NULL,
        [Unit] nvarchar(40) NOT NULL,
        [Location] nvarchar(200) NULL,
        [Remarks] nvarchar(1000) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ConstructionDsrQuantityLines] PRIMARY KEY ([ID]),
        CONSTRAINT [FK_ConstructionDsrQuantityLines_BoqItems_BoqItemId_ProjectId_CompanyID] FOREIGN KEY ([BoqItemId], [ProjectId], [CompanyID]) REFERENCES [BoqItems] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrQuantityLines_ConstructionDailySiteReports_ReportId_SiteId_ProjectId_CompanyID] FOREIGN KEY ([ReportId], [SiteId], [ProjectId], [CompanyID]) REFERENCES [ConstructionDailySiteReports] ([ID], [SiteId], [ProjectId], [CompanyID]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ConstructionDsrQuantityLines_ConstructionWbsNodes_WbsNodeId_ProjectId_CompanyID] FOREIGN KEY ([WbsNodeId], [ProjectId], [CompanyID]) REFERENCES [ConstructionWbsNodes] ([ID], [ProjectId], [CompanyID]) ON DELETE NO ACTION
    );
    PRINT 'ConstructionDsrQuantityLines created';
END
ELSE PRINT 'ConstructionDsrQuantityLines already present';
GO

IF OBJECT_ID('dbo.ConstructionDailySiteReports','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDailySiteReports_AmendsReportId_SiteId_ProjectId_CompanyID_ReportDate' AND object_id=OBJECT_ID('dbo.ConstructionDailySiteReports'))
    CREATE INDEX [IX_ConstructionDailySiteReports_AmendsReportId_SiteId_ProjectId_CompanyID_ReportDate] ON [ConstructionDailySiteReports] ([AmendsReportId], [SiteId], [ProjectId], [CompanyID], [ReportDate]);
GO

IF OBJECT_ID('dbo.ConstructionDailySiteReports','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDailySiteReports_CompanyID_ProjectId_SiteId_ReportDate_RevisionNo' AND object_id=OBJECT_ID('dbo.ConstructionDailySiteReports'))
    CREATE UNIQUE INDEX [IX_ConstructionDailySiteReports_CompanyID_ProjectId_SiteId_ReportDate_RevisionNo] ON [ConstructionDailySiteReports] ([CompanyID], [ProjectId], [SiteId], [ReportDate], [RevisionNo]);
GO

IF OBJECT_ID('dbo.ConstructionDailySiteReports','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDailySiteReports_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDailySiteReports'))
    CREATE INDEX [IX_ConstructionDailySiteReports_ProjectId_CompanyID] ON [ConstructionDailySiteReports] ([ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDailySiteReports','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDailySiteReports_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDailySiteReports'))
    CREATE INDEX [IX_ConstructionDailySiteReports_SiteId_ProjectId_CompanyID] ON [ConstructionDailySiteReports] ([SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrAttachments','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrAttachments_CompanyID_ReportId_LibraryItemId' AND object_id=OBJECT_ID('dbo.ConstructionDsrAttachments'))
    CREATE UNIQUE INDEX [IX_ConstructionDsrAttachments_CompanyID_ReportId_LibraryItemId] ON [ConstructionDsrAttachments] ([CompanyID], [ReportId], [LibraryItemId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrAttachments','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrAttachments_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrAttachments'))
    CREATE INDEX [IX_ConstructionDsrAttachments_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrAttachments] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrAttachments','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrAttachments_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrAttachments'))
    CREATE INDEX [IX_ConstructionDsrAttachments_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrAttachments] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrEquipmentLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrEquipmentLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrEquipmentLines'))
    CREATE INDEX [IX_ConstructionDsrEquipmentLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrEquipmentLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrEquipmentLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrEquipmentLines_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrEquipmentLines'))
    CREATE INDEX [IX_ConstructionDsrEquipmentLines_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrEquipmentLines] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrEventLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrEventLines_CompanyID_ReportId_EventType' AND object_id=OBJECT_ID('dbo.ConstructionDsrEventLines'))
    CREATE INDEX [IX_ConstructionDsrEventLines_CompanyID_ReportId_EventType] ON [ConstructionDsrEventLines] ([CompanyID], [ReportId], [EventType]);
GO

IF OBJECT_ID('dbo.ConstructionDsrEventLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrEventLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrEventLines'))
    CREATE INDEX [IX_ConstructionDsrEventLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrEventLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrEventLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrEventLines_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrEventLines'))
    CREATE INDEX [IX_ConstructionDsrEventLines_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrEventLines] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrInstructionLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrInstructionLines_CompanyID_ReportId' AND object_id=OBJECT_ID('dbo.ConstructionDsrInstructionLines'))
    CREATE INDEX [IX_ConstructionDsrInstructionLines_CompanyID_ReportId] ON [ConstructionDsrInstructionLines] ([CompanyID], [ReportId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrInstructionLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrInstructionLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrInstructionLines'))
    CREATE INDEX [IX_ConstructionDsrInstructionLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrInstructionLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrManpowerLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrManpowerLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrManpowerLines'))
    CREATE INDEX [IX_ConstructionDsrManpowerLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrManpowerLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrManpowerLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrManpowerLines_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrManpowerLines'))
    CREATE INDEX [IX_ConstructionDsrManpowerLines_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrManpowerLines] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrMaterialLines_CompanyID_ReportId_Direction' AND object_id=OBJECT_ID('dbo.ConstructionDsrMaterialLines'))
    CREATE INDEX [IX_ConstructionDsrMaterialLines_CompanyID_ReportId_Direction] ON [ConstructionDsrMaterialLines] ([CompanyID], [ReportId], [Direction]);
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrMaterialLines_GoodsReceiptLineId' AND object_id=OBJECT_ID('dbo.ConstructionDsrMaterialLines'))
    CREATE INDEX [IX_ConstructionDsrMaterialLines_GoodsReceiptLineId] ON [ConstructionDsrMaterialLines] ([GoodsReceiptLineId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrMaterialLines_ProjectMaterialIssueLineId' AND object_id=OBJECT_ID('dbo.ConstructionDsrMaterialLines'))
    CREATE INDEX [IX_ConstructionDsrMaterialLines_ProjectMaterialIssueLineId] ON [ConstructionDsrMaterialLines] ([ProjectMaterialIssueLineId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrMaterialLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrMaterialLines'))
    CREATE INDEX [IX_ConstructionDsrMaterialLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrMaterialLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrMaterialLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrMaterialLines_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrMaterialLines'))
    CREATE INDEX [IX_ConstructionDsrMaterialLines_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrMaterialLines] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrQuantityLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrQuantityLines_BoqItemId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrQuantityLines'))
    CREATE INDEX [IX_ConstructionDsrQuantityLines_BoqItemId_ProjectId_CompanyID] ON [ConstructionDsrQuantityLines] ([BoqItemId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrQuantityLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrQuantityLines_CompanyID_ReportId_BoqItemId_WbsNodeId' AND object_id=OBJECT_ID('dbo.ConstructionDsrQuantityLines'))
    CREATE INDEX [IX_ConstructionDsrQuantityLines_CompanyID_ReportId_BoqItemId_WbsNodeId] ON [ConstructionDsrQuantityLines] ([CompanyID], [ReportId], [BoqItemId], [WbsNodeId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrQuantityLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrQuantityLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrQuantityLines'))
    CREATE INDEX [IX_ConstructionDsrQuantityLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrQuantityLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrQuantityLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrQuantityLines_WbsNodeId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrQuantityLines'))
    CREATE INDEX [IX_ConstructionDsrQuantityLines_WbsNodeId_ProjectId_CompanyID] ON [ConstructionDsrQuantityLines] ([WbsNodeId], [ProjectId], [CompanyID]);
GO

IF OBJECT_ID('dbo.ConstructionDsrVisitorLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrVisitorLines_CompanyID_ReportId' AND object_id=OBJECT_ID('dbo.ConstructionDsrVisitorLines'))
    CREATE INDEX [IX_ConstructionDsrVisitorLines_CompanyID_ReportId] ON [ConstructionDsrVisitorLines] ([CompanyID], [ReportId]);
GO

IF OBJECT_ID('dbo.ConstructionDsrVisitorLines','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ConstructionDsrVisitorLines_ReportId_SiteId_ProjectId_CompanyID' AND object_id=OBJECT_ID('dbo.ConstructionDsrVisitorLines'))
    CREATE INDEX [IX_ConstructionDsrVisitorLines_ReportId_SiteId_ProjectId_CompanyID] ON [ConstructionDsrVisitorLines] ([ReportId], [SiteId], [ProjectId], [CompanyID]);
GO
