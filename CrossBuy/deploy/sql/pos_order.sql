-- RC-2: cashier order → pay → invoice + stock + GL. Idempotent.
-- Config/transaction tables ONLY create rows; all GL/stock happens via existing services at pay.
-- Apply with:  sqlcmd -S . -d CrossBuyDB2 -E -C -b -i deploy\sql\pos_order.sql
SET NOCOUNT ON;

IF OBJECT_ID('dbo.PosOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.PosOrders(
        ID            INT IDENTITY(1,1) PRIMARY KEY,
        CompanyId     INT NOT NULL,
        BranchId      INT NOT NULL,
        BrandId       INT NULL,
        OrderType     NVARCHAR(20) NOT NULL CONSTRAINT DF_PosOrders_Type DEFAULT('Takeaway'),
        TableId       INT NULL,
        GuestCount    INT NULL,
        Status        NVARCHAR(20) NOT NULL CONSTRAINT DF_PosOrders_Status DEFAULT('Open'),
        CurrencyId    INT NULL,
        SubTotal      DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrders_Sub DEFAULT(0),
        ServiceAmount DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrders_Svc DEFAULT(0),
        TaxTotal      DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrders_Tax DEFAULT(0),
        GrandTotal    DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrders_Grand DEFAULT(0),
        InvoiceId     INT NULL,
        ReceiptId     INT NULL,
        CashierUserId INT NULL,
        Notes         NVARCHAR(500) NULL,
        OpenedAt      DATETIME2 NOT NULL CONSTRAINT DF_PosOrders_Opened DEFAULT(SYSUTCDATETIME()),
        ClosedAt      DATETIME2 NULL
    );
    CREATE INDEX IX_PosOrders_Branch_Status ON dbo.PosOrders(BranchId, Status);
    PRINT 'PosOrders created';
END ELSE PRINT 'PosOrders exists';

IF OBJECT_ID('dbo.PosOrderLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.PosOrderLines(
        ID            INT IDENTITY(1,1) PRIMARY KEY,
        OrderId       INT NOT NULL,
        ItemId        INT NOT NULL,
        ItemName      NVARCHAR(200) NOT NULL CONSTRAINT DF_PosOrderLines_Name DEFAULT(''),
        Qty           DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrderLines_Qty DEFAULT(1),
        UnitPrice     DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrderLines_Price DEFAULT(0),
        DiscountAmount DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrderLines_Disc DEFAULT(0),
        TaxRate       DECIMAL(9,4)  NOT NULL CONSTRAINT DF_PosOrderLines_TaxR DEFAULT(0),
        LineTotal     DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrderLines_LT DEFAULT(0),
        Notes         NVARCHAR(300) NULL,
        Sort          INT NOT NULL CONSTRAINT DF_PosOrderLines_Sort DEFAULT(0)
    );
    CREATE INDEX IX_PosOrderLines_Order ON dbo.PosOrderLines(OrderId);
    PRINT 'PosOrderLines created';
END ELSE PRINT 'PosOrderLines exists';

IF OBJECT_ID('dbo.PosPayments','U') IS NULL
BEGIN
    CREATE TABLE dbo.PosPayments(
        ID            INT IDENTITY(1,1) PRIMARY KEY,
        OrderId       INT NOT NULL,
        PaymentMethod NVARCHAR(30) NOT NULL CONSTRAINT DF_PosPayments_Method DEFAULT('Cash'),
        Amount        DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosPayments_Amt DEFAULT(0),
        TargetAccountId INT NULL,
        Reference     NVARCHAR(100) NULL,
        CreatedAt     DATETIME2 NOT NULL CONSTRAINT DF_PosPayments_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_PosPayments_Order ON dbo.PosPayments(OrderId);
    PRINT 'PosPayments created';
END ELSE PRINT 'PosPayments exists';
