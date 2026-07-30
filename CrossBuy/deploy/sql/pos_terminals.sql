-- POS-1: cashier terminals (isolated till) + shifts + PosOrder linkage. Idempotent.
-- Setup/config only — no GL/stock. Apply: sqlcmd -S . -d CrossBuyDB2 -E -C -b -i deploy\sql\pos_terminals.sql
SET NOCOUNT ON;

IF OBJECT_ID('dbo.PosTerminals','U') IS NULL
BEGIN
    CREATE TABLE dbo.PosTerminals(
        ID            INT IDENTITY(1,1) PRIMARY KEY,
        BranchId      INT NOT NULL,
        Code          NVARCHAR(30) NOT NULL,
        Name          NVARCHAR(100) NOT NULL CONSTRAINT DF_PosTerm_Name DEFAULT(''),
        CashAccountId INT NULL,
        ReceiptPrefix NVARCHAR(20) NOT NULL CONSTRAINT DF_PosTerm_Prefix DEFAULT(''),
        NextReceiptNo INT NOT NULL CONSTRAINT DF_PosTerm_Next DEFAULT(1),
        IsActive      BIT NOT NULL CONSTRAINT DF_PosTerm_Active DEFAULT(1),
        CreatedAt     DATETIME2 NULL CONSTRAINT DF_PosTerm_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_PosTerminals_Branch_Code ON dbo.PosTerminals(BranchId, Code);
    PRINT 'PosTerminals created';
END ELSE PRINT 'PosTerminals exists';

IF OBJECT_ID('dbo.PosShifts','U') IS NULL
BEGIN
    CREATE TABLE dbo.PosShifts(
        ID                 INT IDENTITY(1,1) PRIMARY KEY,
        TerminalId         INT NOT NULL,
        ShiftType          NVARCHAR(20) NOT NULL CONSTRAINT DF_PosShift_Type DEFAULT('Morning'),
        Status             NVARCHAR(20) NOT NULL CONSTRAINT DF_PosShift_Status DEFAULT('Open'),
        OpenedByEmployeeId INT NULL,
        OpeningFloat       DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosShift_Float DEFAULT(0),
        OpenedAt           DATETIME2 NOT NULL CONSTRAINT DF_PosShift_Opened DEFAULT(SYSUTCDATETIME()),
        ClosedAt           DATETIME2 NULL,
        Notes              NVARCHAR(300) NULL
    );
    CREATE INDEX IX_PosShifts_Terminal_Status ON dbo.PosShifts(TerminalId, Status);
    PRINT 'PosShifts created';
END ELSE PRINT 'PosShifts exists';

-- PosOrder linkage (additive, nullable — operation phase fills these)
IF COL_LENGTH('dbo.PosOrders','TerminalId') IS NULL ALTER TABLE dbo.PosOrders ADD TerminalId INT NULL;
IF COL_LENGTH('dbo.PosOrders','ShiftId')    IS NULL ALTER TABLE dbo.PosOrders ADD ShiftId    INT NULL;
IF COL_LENGTH('dbo.PosOrders','ReceiptNo')  IS NULL ALTER TABLE dbo.PosOrders ADD ReceiptNo  NVARCHAR(40) NULL;
PRINT 'PosOrders linkage columns ensured';

-- POS-3: receipt printing settings on the terminal (window.print now; ESC/POS bridge later)
IF COL_LENGTH('dbo.PosTerminals','ReceiptPrinterName')  IS NULL ALTER TABLE dbo.PosTerminals ADD ReceiptPrinterName NVARCHAR(120) NULL;
IF COL_LENGTH('dbo.PosTerminals','ReceiptPaperWidthMm') IS NULL ALTER TABLE dbo.PosTerminals ADD ReceiptPaperWidthMm INT NOT NULL CONSTRAINT DF_PosTerm_Paper DEFAULT(80);
IF COL_LENGTH('dbo.PosTerminals','ReceiptCopies')       IS NULL ALTER TABLE dbo.PosTerminals ADD ReceiptCopies INT NOT NULL CONSTRAINT DF_PosTerm_Copies DEFAULT(1);
PRINT 'PosTerminals receipt columns ensured';
