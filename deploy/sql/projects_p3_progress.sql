-- Projects & Contracting — P3 Execution / progress measurement.
-- OPERATIONAL ONLY (no GL). Dated cumulative snapshots of executed quantity per BOQ item.
-- Idempotent: CREATE-if-not-exists. Additive — touches nothing existing.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.ProjectProgresses','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectProgresses(
        ID              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        ProjectId       INT           NOT NULL,
        MeasurementNo   INT           NOT NULL,
        MeasurementDate DATETIME2     NOT NULL,
        Note            NVARCHAR(1000) NULL,
        Status          NVARCHAR(20)  NOT NULL CONSTRAINT DF_ProjectProgresses_Status DEFAULT('Draft'),
        OverallPercent  DECIMAL(9,4)  NOT NULL CONSTRAINT DF_ProjectProgresses_Pct DEFAULT(0),
        ExecutedValue   DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProjectProgresses_Exec DEFAULT(0),
        CreatedAt       DATETIME2     NULL,
        CreatedBy       INT           NULL
    );
    CREATE INDEX IX_ProjectProgresses_Project ON dbo.ProjectProgresses(CompanyID, ProjectId, MeasurementNo);
    PRINT 'CREATED ProjectProgresses';
END
ELSE PRINT 'ProjectProgresses EXISTS';
GO

IF OBJECT_ID('dbo.ProjectProgressLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectProgressLines(
        ID             INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ProgressId     INT           NOT NULL,
        BoqItemId      INT           NULL,       -- null = whole-project manual % (project without BOQ)
        CumulativeQty  DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProjectProgressLines_Cum DEFAULT(0),
        ManualPercent  DECIMAL(9,4)  NULL,
        Note           NVARCHAR(500) NULL,
        CONSTRAINT FK_ProjectProgressLines_Header FOREIGN KEY (ProgressId) REFERENCES dbo.ProjectProgresses(ID)
    );
    CREATE INDEX IX_ProjectProgressLines_Header ON dbo.ProjectProgressLines(ProgressId);
    PRINT 'CREATED ProjectProgressLines';
END
ELSE PRINT 'ProjectProgressLines EXISTS';
GO
