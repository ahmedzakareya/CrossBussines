-- Storefront product gallery: extra images per Item (display only, additive, no inventory/GL impact). Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID('dbo.ItemImages','U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemImages (
        ID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemImages PRIMARY KEY,
        CompanyID  int          NOT NULL,
        ItemId     int          NOT NULL,
        [Path]     nvarchar(400) NOT NULL,
        SortOrder  int          NOT NULL CONSTRAINT DF_ItemImages_Sort DEFAULT(0),
        CreatedAt  datetime2    NULL
    );
    CREATE INDEX IX_ItemImages_Item ON dbo.ItemImages (CompanyID, ItemId, SortOrder);
END
