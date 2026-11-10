-- ==========================================================================================
-- Hospital H32 — ward requisitions to stores: supply units (a ward's own store and the store it draws from), their
-- par levels, and requisitions (Draft → Submitted → Approved → Picking → Dispatched, or Cancelled) with their lines.
-- Dispatching posts the inventory module's own store transfer; this slice only holds the hospital's side.
--
-- FOUR NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.Warehouses','U') IS NULL OR OBJECT_ID('dbo.Items','U') IS NULL
    THROW 51990, 'The inventory tables (Warehouses, Items) are required first.', 1;

IF OBJECT_ID('dbo.HospitalSupplyUnits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSupplyUnits (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSupplyUnits PRIMARY KEY,
        CompanyId             int            NOT NULL,
        Code                  nvarchar(20)   NOT NULL,
        Name                  nvarchar(100)  NOT NULL,
        NameEn                nvarchar(100)  NOT NULL,
        WardId                int            NULL,
        StoreWarehouseId      int            NOT NULL,
        SourceWarehouseId     int            NOT NULL,
        IsActive              bit            NOT NULL,
        CONSTRAINT CK_HospitalSupplyUnits_Stores CHECK (StoreWarehouseId <> SourceWarehouseId),
        CONSTRAINT FK_HospitalSupplyUnits_Store FOREIGN KEY (StoreWarehouseId) REFERENCES dbo.Warehouses(ID),
        CONSTRAINT FK_HospitalSupplyUnits_Source FOREIGN KEY (SourceWarehouseId) REFERENCES dbo.Warehouses(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSupplyUnits_CompanyId_Code ON dbo.HospitalSupplyUnits (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalSupplyPars','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSupplyPars (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSupplyPars PRIMARY KEY,
        CompanyId             int            NOT NULL,
        UnitId                int            NOT NULL,
        ItemId                int            NOT NULL,
        ParQty                decimal(18,3)  NOT NULL,
        CONSTRAINT CK_HospitalSupplyPars_Qty CHECK (ParQty > 0),
        CONSTRAINT FK_HospitalSupplyPars_Unit FOREIGN KEY (UnitId) REFERENCES dbo.HospitalSupplyUnits(ID),
        CONSTRAINT FK_HospitalSupplyPars_Item FOREIGN KEY (ItemId) REFERENCES dbo.Items(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSupplyPars_UnitId_ItemId ON dbo.HospitalSupplyPars (UnitId, ItemId);
END

IF OBJECT_ID('dbo.HospitalRequisitions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRequisitions (
        ID                      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRequisitions PRIMARY KEY,
        CompanyId               int            NOT NULL,
        ReqNo                   nvarchar(30)   NOT NULL,
        UnitId                  int            NOT NULL,
        Priority                nvarchar(20)   NOT NULL,
        Status                  nvarchar(20)   NOT NULL,
        Note                    nvarchar(500)  NULL,
        CreatedAt               datetime2      NOT NULL,
        RequestedByEmployeeId   int            NULL,
        SubmittedAt             datetime2      NULL,
        ApprovedAt              datetime2      NULL,
        ApprovedByEmployeeId    int            NULL,
        DispatchedAt            datetime2      NULL,
        DispatchedByEmployeeId  int            NULL,
        StockTransferId         int            NULL,
        CancelReason            nvarchar(300)  NULL,
        CONSTRAINT CK_HospitalRequisitions_Status CHECK (Status IN (N'Draft', N'Submitted', N'Approved', N'Picking', N'Dispatched', N'Cancelled')),
        CONSTRAINT CK_HospitalRequisitions_Priority CHECK (Priority IN (N'Routine', N'Urgent', N'Stat')),
        CONSTRAINT FK_HospitalRequisitions_Unit FOREIGN KEY (UnitId) REFERENCES dbo.HospitalSupplyUnits(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRequisitions_CompanyId_ReqNo ON dbo.HospitalRequisitions (CompanyId, ReqNo);
    CREATE INDEX IX_HospitalRequisitions_CompanyId_Status ON dbo.HospitalRequisitions (CompanyId, Status);
END

IF OBJECT_ID('dbo.HospitalRequisitionLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRequisitionLines (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRequisitionLines PRIMARY KEY,
        CompanyId             int            NOT NULL,
        RequisitionId         int            NOT NULL,
        ItemId                int            NOT NULL,
        RequestedQty          decimal(18,3)  NOT NULL,
        ApprovedQty           decimal(18,3)  NULL,
        CONSTRAINT CK_HospitalRequisitionLines_Qty CHECK (RequestedQty > 0 AND (ApprovedQty IS NULL OR ApprovedQty >= 0)),
        CONSTRAINT FK_HospitalRequisitionLines_Requisition FOREIGN KEY (RequisitionId) REFERENCES dbo.HospitalRequisitions(ID),
        CONSTRAINT FK_HospitalRequisitionLines_Item FOREIGN KEY (ItemId) REFERENCES dbo.Items(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRequisitionLines_RequisitionId_ItemId ON dbo.HospitalRequisitionLines (RequisitionId, ItemId);
END

COMMIT TRANSACTION;
