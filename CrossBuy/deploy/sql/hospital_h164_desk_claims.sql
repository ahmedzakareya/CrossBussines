-- ==========================================================================================
-- Hospital H164 - who owns an item of the desk's queues: one row per item (taken by an employee, or released).
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDeskClaims','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeskClaims (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Kind nvarchar(20) NOT NULL,
        ItemKey nvarchar(40) NOT NULL,
        OwnerEmployeeId int NULL,
        ClaimedAt datetime2 NOT NULL,
        ReleasedAt datetime2 NULL,
        CONSTRAINT PK_HospitalDeskClaims PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDeskClaims_CompanyId_Kind_ItemKey ON dbo.HospitalDeskClaims (CompanyId, Kind, ItemKey);
    CREATE INDEX IX_HospitalDeskClaims_CompanyId_OwnerEmployeeId ON dbo.HospitalDeskClaims (CompanyId, OwnerEmployeeId);
END

COMMIT TRANSACTION;
