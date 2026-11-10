-- ==========================================================================================
-- Hospital H22 — the catalogue's single items: any active inventory item can join the hospital catalogue (and
-- so any price list) on its own, as a medicine or a consumable, whatever its inventory category.
--
-- ONE NEW TABLE. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.Items','U') IS NULL OR OBJECT_ID('dbo.HospitalItemCategories','U') IS NULL
    THROW 51990, 'Run hospital_h7_pricelists.sql first (and the inventory module).', 1;

IF OBJECT_ID('dbo.HospitalCatalogItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCatalogItems (
        ID                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalCatalogItems PRIMARY KEY,
        CompanyId          int            NOT NULL,
        ItemId             int            NOT NULL,
        Kind               nvarchar(20)   NOT NULL,
        AddedAt            datetime2      NOT NULL,
        AddedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalCatalogItems_Kind CHECK (Kind IN (N'Medicine', N'Consumable')),
        CONSTRAINT FK_HospitalCatalogItems_Item FOREIGN KEY (ItemId) REFERENCES dbo.Items(ID)
    );
    -- an item joins the catalogue once
    CREATE UNIQUE INDEX IX_HospitalCatalogItems_CompanyId_ItemId ON dbo.HospitalCatalogItems (CompanyId, ItemId);
END

COMMIT TRANSACTION;
