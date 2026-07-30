-- ============================================================================
-- Cashier setup RC-1: quick-touch buttons per branch + item PLU code.
-- Configuration only — never posts GL or writes stock. 8 invariants untouched. Idempotent.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Items') AND name = N'QuickCode')
    ALTER TABLE dbo.Items ADD QuickCode NVARCHAR(30) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'PosMenuGroups')
    CREATE TABLE dbo.PosMenuGroups (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, Name NVARCHAR(100) NOT NULL, NameEn NVARCHAR(100) NULL,
        Sort INT NOT NULL DEFAULT(0), IsActive BIT NOT NULL DEFAULT(1));
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'PosQuickItems')
    CREATE TABLE dbo.PosQuickItems (
        ID INT IDENTITY(1,1) PRIMARY KEY, BranchId INT NOT NULL, GroupId INT NULL, ItemId INT NOT NULL,
        Sort INT NOT NULL DEFAULT(0), IsActive BIT NOT NULL DEFAULT(1),
        CONSTRAINT UQ_PosQuickItems UNIQUE (BranchId, ItemId));
    CREATE INDEX IX_PosQuickItems_branch ON dbo.PosQuickItems (BranchId);
GO
