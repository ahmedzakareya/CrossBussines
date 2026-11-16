-- ==========================================================================================
-- Hospital H138 - the unit-dose cart fill. Each fill turns a ward's active medication orders for the next 24 hours into
-- medication deliveries (the H21 path); this table keeps one row per fill.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalMedDeliveries','U') IS NULL
    THROW 51990, 'Run hospital_h21_med_routing.sql first.', 1;

IF OBJECT_ID('dbo.HospitalCartFills','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCartFills (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        FillNo nvarchar(20) NOT NULL,
        WardId int NOT NULL,
        WindowStart datetime2 NOT NULL,
        WindowEnd datetime2 NOT NULL,
        Patients int NOT NULL,
        Deliveries int NOT NULL,
        Units decimal(18,4) NOT NULL,
        Skipped int NOT NULL,
        DoneAt datetime2 NOT NULL,
        DoneByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCartFills PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalCartFills_CompanyId_FillNo ON dbo.HospitalCartFills (CompanyId, FillNo);
    CREATE INDEX IX_HospitalCartFills_CompanyId_WardId_DoneAt ON dbo.HospitalCartFills (CompanyId, WardId, DoneAt);
END

COMMIT TRANSACTION;
