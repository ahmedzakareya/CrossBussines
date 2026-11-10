-- ==========================================================================================
-- Hospital H37 — a procedure's kit consumed from inventory (the prototype's "BOM explosion to consume the procedure
-- kit from inventory", #/or). A procedure (a catalogue service) names its KIT: an inventory item whose bill of
-- materials (ItemComponents, the inventory module's own) lists the consumables. When the case is closed, the kit is
-- exploded through IBomExplosionService and each component leaves the THEATRE STORE (a setting) through the
-- inventory's single stock writer, once:
--   HospitalProcedureKits     ServiceId → KitItemId (one kit per procedure)
--   HospitalTheatreSettings   the theatre store (the warehouse a case draws from), one row per company
--   HospitalSurgeries         KitIssuedAt (null until the kit has left the store — never twice)
--
-- New tables + one nullable column; nothing existing changes. Idempotent (OBJECT_ID / COL_LENGTH guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql and hospital_h29_theatre.sql first.', 1;

IF OBJECT_ID('dbo.HospitalProcedureKits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalProcedureKits (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int        NOT NULL,
        ServiceId   int        NOT NULL,
        KitItemId   int        NOT NULL,
        UpdatedAt   datetime2  NOT NULL,
        CONSTRAINT PK_HospitalProcedureKits PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalProcedureKits_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalProcedureKits_CompanyId_ServiceId ON dbo.HospitalProcedureKits (CompanyId, ServiceId);
END

IF OBJECT_ID('dbo.HospitalTheatreSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalTheatreSettings (
        ID                int IDENTITY(1,1) NOT NULL,
        CompanyId         int        NOT NULL,
        StoreWarehouseId  int        NULL,
        UpdatedAt         datetime2  NOT NULL,
        CONSTRAINT PK_HospitalTheatreSettings PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalTheatreSettings_CompanyId ON dbo.HospitalTheatreSettings (CompanyId);
END

IF COL_LENGTH('dbo.HospitalSurgeries', 'KitIssuedAt') IS NULL ALTER TABLE dbo.HospitalSurgeries ADD KitIssuedAt datetime2 NULL;

COMMIT TRANSACTION;
