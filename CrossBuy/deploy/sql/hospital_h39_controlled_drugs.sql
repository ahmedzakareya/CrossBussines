-- ==========================================================================================
-- Hospital H39 — the controlled-drugs register of a ward (the prototype's "Controlled drugs register — Ward 3: every
-- movement signed by two nurses", #/requisitions). Which inventory items are controlled is a setting; a ward's
-- register is read from its store: the opening balance, what came IN (the requisitions' transfers), what was GIVEN
-- to patients and WASTED — each of those signed by the nurse who did it AND a witness, who confirms with her own
-- password on the same screen (owner ruling, 2026-11-05) — and the balance. A dose or a waste leaves the ward store
-- through the inventory's single stock writer, so the register and the stock never disagree.
--   HospitalControlledItems    the controlled items (one row per item)
--   HospitalControlledEntries  one row per dose given or amount wasted, with both signatures
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalControlledItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalControlledItems (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int        NOT NULL,
        ItemId      int        NOT NULL,
        CreatedAt   datetime2  NOT NULL,
        CONSTRAINT PK_HospitalControlledItems PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalControlledItems_CompanyId_ItemId ON dbo.HospitalControlledItems (CompanyId, ItemId);
END

IF OBJECT_ID('dbo.HospitalControlledEntries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalControlledEntries (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        WarehouseId           int            NOT NULL,
        ItemId                int            NOT NULL,
        Kind                  nvarchar(10)   NOT NULL,
        Qty                   decimal(18,3)  NOT NULL,
        PatientId             int            NULL,
        Note                  nvarchar(300)  NULL,
        RecordedByEmployeeId  int            NOT NULL,
        WitnessEmployeeId     int            NOT NULL,
        RecordedAt            datetime2      NOT NULL,
        CONSTRAINT PK_HospitalControlledEntries PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalControlledEntries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalControlledEntries_Kind CHECK (Kind IN (N'Given', N'Wasted')),
        CONSTRAINT CK_HospitalControlledEntries_Qty CHECK (Qty > 0),
        CONSTRAINT CK_HospitalControlledEntries_TwoPeople CHECK (WitnessEmployeeId <> RecordedByEmployeeId)
    );
    CREATE INDEX IX_HospitalControlledEntries_Ward ON dbo.HospitalControlledEntries (CompanyId, WarehouseId, ItemId, RecordedAt);
END

COMMIT TRANSACTION;
