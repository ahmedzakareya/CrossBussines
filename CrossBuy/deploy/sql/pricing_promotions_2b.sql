-- ============================================================================
-- Pricing 2B — Time-bound promotions
-- Idempotent. A promotion layers an ADDITIONAL discount on the resolved list/base
-- price (net = price × (1−listDisc) × (1−promo)); best single promotion wins.
-- Amount discounts are stored in CurrencyId and converted to the document currency
-- at resolution; the final net still honors the margin floor (2A).
-- ============================================================================

IF OBJECT_ID('dbo.Promotions','U') IS NULL
BEGIN
    CREATE TABLE dbo.Promotions (
        ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID       int NOT NULL,
        Code            nvarchar(50) NOT NULL,
        Name            nvarchar(200) NOT NULL,
        NameEn          nvarchar(200) NULL,
        DiscountType    nvarchar(10) NOT NULL CONSTRAINT DF_Promotions_Type DEFAULT('Percent'),  -- Percent | Amount
        Value           decimal(19,4) NOT NULL CONSTRAINT DF_Promotions_Value DEFAULT(0),
        CurrencyId      int NULL,               -- Amount only; null = functional
        ItemId          int NULL,
        ItemCategoryId  int NULL,
        CustomerId      int NULL,
        Segment         nvarchar(100) NULL,
        MinQty          decimal(19,4) NOT NULL CONSTRAINT DF_Promotions_MinQty DEFAULT(1),
        Priority        int NOT NULL CONSTRAINT DF_Promotions_Priority DEFAULT(0),
        ValidFrom       datetime2(7) NULL,
        ValidTo         datetime2(7) NULL,
        IsActive        bit NOT NULL CONSTRAINT DF_Promotions_Active DEFAULT(1),
        CreatedBy       nvarchar(450) NULL,
        CreatedAt       datetime2(7) NULL
    );
    CREATE INDEX IX_Promotions_Co_Active ON dbo.Promotions(CompanyID, IsActive);
    CREATE INDEX IX_Promotions_Item ON dbo.Promotions(ItemId);
END
GO
