-- Modifiers (setup only): reusable option groups attached to items (M:N). Idempotent.
-- Definition/linking only — no GL/stock. Apply: sqlcmd -S . -d CrossBuyDB2 -E -C -b -i deploy\sql\pos_modifiers.sql
SET NOCOUNT ON;

IF OBJECT_ID('dbo.ModifierGroups','U') IS NULL
BEGIN
    CREATE TABLE dbo.ModifierGroups(
        ID        INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID INT NOT NULL,
        Name      NVARCHAR(150) NOT NULL,
        NameEn    NVARCHAR(150) NULL,
        Type      NVARCHAR(20) NOT NULL CONSTRAINT DF_ModGrp_Type DEFAULT('AddOn'),
        MinSelect INT NOT NULL CONSTRAINT DF_ModGrp_Min DEFAULT(0),
        MaxSelect INT NOT NULL CONSTRAINT DF_ModGrp_Max DEFAULT(0),
        Sort      INT NOT NULL CONSTRAINT DF_ModGrp_Sort DEFAULT(0),
        IsActive  BIT NOT NULL CONSTRAINT DF_ModGrp_Active DEFAULT(1),
        CreatedAt DATETIME2 NULL CONSTRAINT DF_ModGrp_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_ModifierGroups_Company ON dbo.ModifierGroups(CompanyID);
    PRINT 'ModifierGroups created';
END ELSE PRINT 'ModifierGroups exists';

IF OBJECT_ID('dbo.ModifierOptions','U') IS NULL
BEGIN
    CREATE TABLE dbo.ModifierOptions(
        ID           INT IDENTITY(1,1) PRIMARY KEY,
        GroupId      INT NOT NULL,
        Name         NVARCHAR(150) NOT NULL CONSTRAINT DF_ModOpt_Name DEFAULT(''),
        NameEn       NVARCHAR(150) NULL,
        LinkedItemId INT NOT NULL,
        QtyDeducted  DECIMAL(19,4) NOT NULL CONSTRAINT DF_ModOpt_Qty DEFAULT(1),
        ExtraPrice   DECIMAL(19,4) NOT NULL CONSTRAINT DF_ModOpt_Price DEFAULT(0),
        IsDefault    BIT NOT NULL CONSTRAINT DF_ModOpt_Def DEFAULT(0),
        Sort         INT NOT NULL CONSTRAINT DF_ModOpt_Sort DEFAULT(0),
        IsActive     BIT NOT NULL CONSTRAINT DF_ModOpt_Active DEFAULT(1)
    );
    CREATE INDEX IX_ModifierOptions_Group ON dbo.ModifierOptions(GroupId);
    PRINT 'ModifierOptions created';
END ELSE PRINT 'ModifierOptions exists';

IF OBJECT_ID('dbo.ItemModifierGroups','U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemModifierGroups(
        ID      INT IDENTITY(1,1) PRIMARY KEY,
        ItemId  INT NOT NULL,
        GroupId INT NOT NULL,
        Sort    INT NOT NULL CONSTRAINT DF_ItemMod_Sort DEFAULT(0)
    );
    CREATE UNIQUE INDEX UX_ItemModifierGroups ON dbo.ItemModifierGroups(ItemId, GroupId);
    PRINT 'ItemModifierGroups created';
END ELSE PRINT 'ItemModifierGroups exists';
