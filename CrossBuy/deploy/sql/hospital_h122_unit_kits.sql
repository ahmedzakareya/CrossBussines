-- ==========================================================================================
-- Hospital H122 — a dialysis session's consumables leave the unit's store. A unit names the KIT one session uses (an
-- inventory bundle whose bill of materials lists the consumables: the dialyser, the blood lines, the needles…) and its
-- STORE; a finished session draws one kit through the inventory's single stock writer, once:
--   HospitalUnitKits                      one row per unit per company: the kit item and the store
--   HospitalDialysisSessions.KitIssuedAt  null until the session's kit has left the store — never twice
--
-- One new table + one nullable column; nothing existing changes. Idempotent (OBJECT_ID / COL_LENGTH guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDialysisSessions','U') IS NULL OR OBJECT_ID('dbo.Items','U') IS NULL OR OBJECT_ID('dbo.Warehouses','U') IS NULL
    THROW 51990, 'Run hospital_h87_dialysis.sql first.', 1;

IF OBJECT_ID('dbo.HospitalUnitKits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalUnitKits (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int           NOT NULL,
        Unit                 nvarchar(20)  NOT NULL,
        KitItemId            int           NULL,
        StoreWarehouseId     int           NULL,
        UpdatedAt            datetime2     NOT NULL,
        UpdatedByEmployeeId  int           NULL,
        CONSTRAINT PK_HospitalUnitKits PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalUnitKits_Item FOREIGN KEY (KitItemId) REFERENCES dbo.Items(ID),
        CONSTRAINT FK_HospitalUnitKits_Store FOREIGN KEY (StoreWarehouseId) REFERENCES dbo.Warehouses(ID),
        CONSTRAINT CK_HospitalUnitKits_Unit CHECK (Unit IN ('Dialysis'))
    );
    CREATE UNIQUE INDEX IX_HospitalUnitKits_CompanyId_Unit ON dbo.HospitalUnitKits (CompanyId, Unit);
END

IF COL_LENGTH('dbo.HospitalDialysisSessions', 'KitIssuedAt') IS NULL ALTER TABLE dbo.HospitalDialysisSessions ADD KitIssuedAt datetime2 NULL;

COMMIT TRANSACTION;
