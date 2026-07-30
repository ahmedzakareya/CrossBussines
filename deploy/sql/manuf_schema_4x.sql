SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET NOCOUNT ON;

-- ===== Module 4 (Manufacturing) 4-1: WIP + applied-cost accounts + Work Orders =====

-- 1105 Work-in-progress (asset, under 11=ID 2, postable). 520108 applied manufacturing costs (under 52=ID 27).
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='1105')
    INSERT INTO Accounts(CompanyID,Code,Name,NameEn,AccountTypeId,ParentId,IsPostable,IsActive,CreatedAt)
    VALUES(1,'1105',N'إنتاج تحت التشغيل','Work in progress',1,2,1,1,SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='520108')
    INSERT INTO Accounts(CompanyID,Code,Name,NameEn,AccountTypeId,ParentId,IsPostable,IsActive,CreatedAt)
    VALUES(1,'520108',N'تكاليف تصنيع مطبّقة','Manufacturing applied',5,27,1,1,SYSUTCDATETIME());
GO

IF OBJECT_ID('ManufWorkOrders','U') IS NULL
BEGIN
    CREATE TABLE ManufWorkOrders(
        ID              int IDENTITY(1,1) PRIMARY KEY,
        CompanyID       int NOT NULL,
        WoNo            nvarchar(30) NULL,
        ItemId          int NOT NULL,
        Qty             decimal(19,4) NOT NULL,
        ProducedQty     decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Produced DEFAULT(0),
        WarehouseId     int NOT NULL,
        Status          nvarchar(20) NOT NULL CONSTRAINT DF_Wo_Status DEFAULT('Draft'), -- Draft|Released|Completed|Cancelled
        PlannedStart    date NULL,
        PlannedEnd      date NULL,
        LaborCost       decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Labor DEFAULT(0),
        OverheadCost    decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Oh DEFAULT(0),
        MaterialCost    decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Mat DEFAULT(0),
        UnitCost        decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Unit DEFAULT(0),
        Notes           nvarchar(1000) NULL,
        OwnerEmployeeId int NULL,
        JournalEntryId  int NULL,
        CompletedAt     datetime2 NULL,
        CreatedBy       nvarchar(256) NULL,
        CreatedAt       datetime2 NULL
    );
    CREATE INDEX IX_ManufWorkOrders_Company ON ManufWorkOrders(CompanyID, Status);
END

IF OBJECT_ID('ManufWorkOrderComponents','U') IS NULL
BEGIN
    CREATE TABLE ManufWorkOrderComponents(
        ID            int IDENTITY(1,1) PRIMARY KEY,
        CompanyID     int NOT NULL,
        WorkOrderId   int NOT NULL,
        ItemId        int NOT NULL,
        PlannedQty    decimal(19,4) NOT NULL,
        IssuedQty     decimal(19,4) NOT NULL CONSTRAINT DF_Woc_Issued DEFAULT(0),
        UoMId         int NULL,
        UnitCost      decimal(19,4) NOT NULL CONSTRAINT DF_Woc_Unit DEFAULT(0)
    );
    CREATE INDEX IX_ManufWorkOrderComponents_Wo ON ManufWorkOrderComponents(CompanyID, WorkOrderId);
END
GO
PRINT 'cb_manuf_4_1 done';

-- ===== Module 4 (Manufacturing) 4-2: work centers + routing operations =====
IF OBJECT_ID('ManufWorkCenters','U') IS NULL
BEGIN
    CREATE TABLE ManufWorkCenters(
        ID             int IDENTITY(1,1) PRIMARY KEY,
        CompanyID      int NOT NULL,
        Code           nvarchar(30) NULL,
        Name           nvarchar(150) NOT NULL,
        CostPerHour    decimal(19,4) NOT NULL CONSTRAINT DF_Wc_Cost DEFAULT(0),   -- labor rate
        OverheadPerHour decimal(19,4) NOT NULL CONSTRAINT DF_Wc_Oh DEFAULT(0),
        IsActive       bit NOT NULL CONSTRAINT DF_Wc_Active DEFAULT(1),
        CreatedAt      datetime2 NULL
    );
    CREATE INDEX IX_ManufWorkCenters_Company ON ManufWorkCenters(CompanyID, IsActive);
END

-- routing operations per manufactured item (ordered). Setup is per-order; Run is per-unit.
IF OBJECT_ID('ManufRoutingOps','U') IS NULL
BEGIN
    CREATE TABLE ManufRoutingOps(
        ID             int IDENTITY(1,1) PRIMARY KEY,
        CompanyID      int NOT NULL,
        ItemId         int NOT NULL,
        Seq            int NOT NULL CONSTRAINT DF_Rop_Seq DEFAULT(1),
        WorkCenterId   int NOT NULL,
        OperationName  nvarchar(150) NULL,
        SetupMins      decimal(19,4) NOT NULL CONSTRAINT DF_Rop_Setup DEFAULT(0),
        RunMinsPerUnit decimal(19,4) NOT NULL CONSTRAINT DF_Rop_Run DEFAULT(0),
        CreatedAt      datetime2 NULL
    );
    CREATE INDEX IX_ManufRoutingOps_Item ON ManufRoutingOps(CompanyID, ItemId, Seq);
END
GO
PRINT 'cb_manuf_4_2 done';

-- ===== Module 4 (Manufacturing) 4-3: production planning (MRP-lite) =====
IF OBJECT_ID('ManufPlans','U') IS NULL
BEGIN
    CREATE TABLE ManufPlans(
        ID         int IDENTITY(1,1) PRIMARY KEY,
        CompanyID  int NOT NULL,
        Name       nvarchar(150) NOT NULL,
        PlanDate   datetime2 NULL,
        Status     nvarchar(20) NOT NULL CONSTRAINT DF_Plan_Status DEFAULT('Draft'), -- Draft / Generated
        CreatedBy  nvarchar(256) NULL,
        CreatedAt  datetime2 NULL
    );
    CREATE INDEX IX_ManufPlans_Company ON ManufPlans(CompanyID, Status);
END

IF OBJECT_ID('ManufPlanDemands','U') IS NULL
BEGIN
    CREATE TABLE ManufPlanDemands(
        ID         int IDENTITY(1,1) PRIMARY KEY,
        CompanyID  int NOT NULL,
        PlanId     int NOT NULL,
        ItemId     int NOT NULL,
        Qty        decimal(19,4) NOT NULL CONSTRAINT DF_PlanDem_Qty DEFAULT(0),
        DueDate    datetime2 NULL,
        CreatedAt  datetime2 NULL
    );
    CREATE INDEX IX_ManufPlanDemands_Plan ON ManufPlanDemands(CompanyID, PlanId);
END
GO
PRINT 'cb_manuf_4_3 done';

-- ===== Module 4 (Manufacturing) 4-5: BOM scrap % (yield loss) per component =====
IF COL_LENGTH('ItemComponents','ScrapPct') IS NULL
    ALTER TABLE ItemComponents ADD ScrapPct decimal(19,4) NOT NULL CONSTRAINT DF_ItemComp_Scrap DEFAULT(0);
GO
PRINT 'cb_manuf_4_5_scrap done';

-- ===== Module 4 (staged WIP): per-item production method + WO staging fields =====
IF COL_LENGTH('Items','ProductionMethod') IS NULL
    ALTER TABLE Items ADD ProductionMethod nvarchar(20) NOT NULL CONSTRAINT DF_Items_ProdMethod DEFAULT('OrderBased');
GO
IF COL_LENGTH('ManufWorkOrders','Mode') IS NULL
    ALTER TABLE ManufWorkOrders ADD Mode nvarchar(20) NOT NULL CONSTRAINT DF_Wo_Mode DEFAULT('OrderBased');
GO
IF COL_LENGTH('ManufWorkOrders','WipBalance') IS NULL
    ALTER TABLE ManufWorkOrders ADD WipBalance decimal(19,4) NOT NULL CONSTRAINT DF_Wo_Wip DEFAULT(0);
GO
IF COL_LENGTH('ManufWorkOrders','ReleasedAt') IS NULL
    ALTER TABLE ManufWorkOrders ADD ReleasedAt datetime2 NULL;
GO
IF COL_LENGTH('ManufWorkOrders','ClosedAt') IS NULL
    ALTER TABLE ManufWorkOrders ADD ClosedAt datetime2 NULL;
GO
PRINT 'cb_manuf_staged done';

-- ===== Module 4 (بند3): work-order labor lines by source + employee manuf rate + standard hours =====
IF OBJECT_ID('ManufWorkOrderLabor','U') IS NULL
BEGIN
    CREATE TABLE ManufWorkOrderLabor(
        ID             int IDENTITY(1,1) PRIMARY KEY,
        CompanyID      int NOT NULL,
        WorkOrderId    int NOT NULL,
        SourceType     nvarchar(20) NOT NULL,            -- Employee | External | Applied
        EmployeeId     int NULL,
        WorkerName     nvarchar(150) NULL,
        Hours          decimal(19,4) NOT NULL CONSTRAINT DF_WoLab_Hours DEFAULT(0),
        RatePerHour    decimal(19,4) NOT NULL CONSTRAINT DF_WoLab_Rate DEFAULT(0),
        Amount         decimal(19,4) NOT NULL CONSTRAINT DF_WoLab_Amt DEFAULT(0),
        WhtCodeId      int NULL,
        WhtAmount      decimal(19,4) NOT NULL CONSTRAINT DF_WoLab_Wht DEFAULT(0),
        CreditAccountId int NULL,
        JournalEntryId int NULL,
        CreatedBy      nvarchar(256) NULL,
        CreatedAt      datetime2 NULL
    );
    CREATE INDEX IX_ManufWoLabor_Wo ON ManufWorkOrderLabor(CompanyID, WorkOrderId);
END
GO
IF COL_LENGTH('Employee','ManufHourlyRate') IS NULL
    ALTER TABLE Employee ADD ManufHourlyRate decimal(19,4) NULL;
GO
IF COL_LENGTH('PayrollSettings','StandardMonthlyHours') IS NULL
    ALTER TABLE PayrollSettings ADD StandardMonthlyHours decimal(19,4) NOT NULL CONSTRAINT DF_PaySet_StdHrs DEFAULT(176);
GO
PRINT 'cb_manuf_labor done';

-- ===== Module 4 (ب): foreign currency for EXTERNAL work-order labor lines (Amount stays functional) =====
IF COL_LENGTH('ManufWorkOrderLabor','CurrencyId') IS NULL
    ALTER TABLE ManufWorkOrderLabor ADD CurrencyId int NULL;
GO
IF COL_LENGTH('ManufWorkOrderLabor','ExchangeRate') IS NULL
    ALTER TABLE ManufWorkOrderLabor ADD ExchangeRate decimal(19,8) NULL;
GO
IF COL_LENGTH('ManufWorkOrderLabor','AmountForeign') IS NULL
    ALTER TABLE ManufWorkOrderLabor ADD AmountForeign decimal(19,4) NULL;
GO
PRINT 'cb_manuf_labor_fx done';
