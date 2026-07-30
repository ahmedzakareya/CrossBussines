-- Cashier setup: per-branch payment methods (→ GL account) + per-branch user POS roles. Idempotent.
-- Definition/mapping ONLY — no payment recorded, no GL, no stock. Apply:
--   sqlcmd -S . -d CrossBuyDB2 -E -C -b -i deploy\sql\pos_payment_roles.sql
SET NOCOUNT ON;

IF OBJECT_ID('dbo.BranchPaymentMethods','U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchPaymentMethods(
        ID              INT IDENTITY(1,1) PRIMARY KEY,
        BranchId        INT NOT NULL,
        PaymentMethod   NVARCHAR(30) NOT NULL CONSTRAINT DF_BPM_Method DEFAULT('Cash'),
        DisplayName     NVARCHAR(100) NULL,
        TargetAccountId INT NULL,
        IsActive        BIT NOT NULL CONSTRAINT DF_BPM_Active DEFAULT(1),
        Sort            INT NOT NULL CONSTRAINT DF_BPM_Sort DEFAULT(0)
    );
    CREATE INDEX IX_BranchPaymentMethods_Branch ON dbo.BranchPaymentMethods(BranchId);
    PRINT 'BranchPaymentMethods created';
END ELSE PRINT 'BranchPaymentMethods exists';

IF OBJECT_ID('dbo.BranchUserRoles','U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchUserRoles(
        ID         INT IDENTITY(1,1) PRIMARY KEY,
        BranchId   INT NOT NULL,
        EmployeeId INT NOT NULL,
        PosRole    NVARCHAR(30) NOT NULL CONSTRAINT DF_BUR_Role DEFAULT('pos-cashier'),
        IsActive   BIT NOT NULL CONSTRAINT DF_BUR_Active DEFAULT(1),
        CreatedAt  DATETIME2 NULL CONSTRAINT DF_BUR_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_BranchUserRoles ON dbo.BranchUserRoles(BranchId, EmployeeId, PosRole);
    PRINT 'BranchUserRoles created';
END ELSE PRINT 'BranchUserRoles exists';
