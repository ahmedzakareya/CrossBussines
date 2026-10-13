-- Projects & Contracting — P6-د (variation orders). OPERATIONAL / estimate only — NO GL. Idempotent. Company 1.
-- Adds VariationOrderId flag on BOQ items + the variation-order header/line tables. Money effect comes later via P4/P5.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) flag BOQ items added by a variation order (null = original scope)
IF COL_LENGTH('dbo.BoqItems','VariationOrderId') IS NULL
    ALTER TABLE dbo.BoqItems ADD VariationOrderId INT NULL;
GO

-- 2) variation-order header
IF OBJECT_ID('dbo.VariationOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.VariationOrders(
        ID          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        ProjectId   INT           NOT NULL,
        VoNo        INT           NOT NULL,           -- sequence per project
        Description NVARCHAR(400) NULL,
        Reason      NVARCHAR(1000) NULL,
        Value       DECIMAL(19,4) NOT NULL CONSTRAINT DF_VariationOrders_Value DEFAULT(0),  -- derived Σ line changes (+/-)
        Status      NVARCHAR(20)  NOT NULL CONSTRAINT DF_VariationOrders_Status DEFAULT('Draft'),  -- Draft / Approved
        CreatedAt   DATETIME2     NULL,
        CreatedBy   INT           NULL,
        ApprovedAt  DATETIME2     NULL,
        ApprovedBy  INT           NULL
    );
    CREATE INDEX IX_VariationOrders_Project ON dbo.VariationOrders(CompanyID, ProjectId, VoNo);
    PRINT 'CREATED VariationOrders';
END
ELSE PRINT 'VariationOrders EXISTS';
GO

-- 3) variation-order line (New = a new BOQ item ; Adjust = revise an existing BoqItem, keeping the old snapshot)
IF OBJECT_ID('dbo.VariationOrderLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.VariationOrderLines(
        ID              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VariationOrderId INT          NOT NULL,
        Kind            NVARCHAR(10)  NOT NULL,        -- New | Adjust
        BoqItemId       INT           NULL,            -- Adjust: the existing item
        Code            NVARCHAR(50)  NULL,
        Description     NVARCHAR(400) NULL,
        DescriptionEn   NVARCHAR(400) NULL,
        Unit            NVARCHAR(50)  NULL,
        Quantity        DECIMAL(19,4) NOT NULL CONSTRAINT DF_VOLines_Qty DEFAULT(0),
        UnitPrice       DECIMAL(19,4) NOT NULL CONSTRAINT DF_VOLines_Price DEFAULT(0),
        MaterialCost    DECIMAL(19,4) NULL,
        LaborCost       DECIMAL(19,4) NULL,
        SubcontractCost DECIMAL(19,4) NULL,
        EquipmentCost   DECIMAL(19,4) NULL,
        OldQuantity     DECIMAL(19,4) NULL,            -- Adjust: snapshot of the original
        OldUnitPrice    DECIMAL(19,4) NULL,
        CONSTRAINT FK_VariationOrderLines_Header FOREIGN KEY (VariationOrderId) REFERENCES dbo.VariationOrders(ID)
    );
    CREATE INDEX IX_VariationOrderLines_Header ON dbo.VariationOrderLines(VariationOrderId);
    PRINT 'CREATED VariationOrderLines';
END
ELSE PRINT 'VariationOrderLines EXISTS';
GO

SELECT COL_LENGTH('dbo.BoqItems','VariationOrderId') AS BoqVoCol,
       (SELECT COUNT(*) FROM sys.tables WHERE name IN ('VariationOrders','VariationOrderLines')) AS Tables;
GO
