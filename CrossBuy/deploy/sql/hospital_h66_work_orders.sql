-- ==========================================================================================
-- Hospital H66 — equipment work orders: a broken device reported, assigned to a technician, worked and closed with
-- what was done (a registered asset's repair is then logged in Accounting's maintenance records), or cancelled.
--   HospitalWorkOrders  one order and its whole path
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRooms','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql first.', 1;

IF OBJECT_ID('dbo.HospitalWorkOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalWorkOrders (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        OrderNo               nvarchar(20)   NOT NULL,
        AssetId               int            NULL,
        Equipment             nvarchar(200)  NULL,
        RoomId                int            NULL,
        Problem               nvarchar(1000) NOT NULL,
        Priority              nvarchar(10)   NOT NULL,
        Status                nvarchar(12)   NOT NULL,
        ReportedAt            datetime2      NOT NULL,
        ReportedByEmployeeId  int            NOT NULL,
        TechnicianEmployeeId  int            NULL,
        AssignedAt            datetime2      NULL,
        StartedAt             datetime2      NULL,
        DoneAt                datetime2      NULL,
        Resolution            nvarchar(1000) NULL,
        Cost                  decimal(18,4)  NULL,
        Vendor                nvarchar(200)  NULL,
        CancelReason          nvarchar(300)  NULL,
        LoggedToAsset         bit            NOT NULL,
        CONSTRAINT PK_HospitalWorkOrders PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalWorkOrders_Room FOREIGN KEY (RoomId) REFERENCES dbo.HospitalRooms(ID),
        CONSTRAINT CK_HospitalWorkOrders_Priority CHECK (Priority IN ('Urgent','High','Normal')),
        CONSTRAINT CK_HospitalWorkOrders_Status CHECK (Status IN ('Open','Assigned','InProgress','Done','Cancelled')),
        -- the device is named one way or the other
        CONSTRAINT CK_HospitalWorkOrders_Device CHECK (AssetId IS NOT NULL OR Equipment IS NOT NULL),
        -- each state carries what it needs
        CONSTRAINT CK_HospitalWorkOrders_Path CHECK (
            (Status = 'Open' AND TechnicianEmployeeId IS NULL)
            OR (Status = 'Assigned' AND TechnicianEmployeeId IS NOT NULL AND AssignedAt IS NOT NULL)
            OR (Status = 'InProgress' AND TechnicianEmployeeId IS NOT NULL AND StartedAt IS NOT NULL)
            OR (Status = 'Done' AND TechnicianEmployeeId IS NOT NULL AND DoneAt IS NOT NULL AND Resolution IS NOT NULL)
            OR (Status = 'Cancelled' AND CancelReason IS NOT NULL)),
        CONSTRAINT CK_HospitalWorkOrders_Cost CHECK (Cost IS NULL OR Cost >= 0)
    );
    CREATE UNIQUE INDEX IX_HospitalWorkOrders_CompanyId_OrderNo ON dbo.HospitalWorkOrders (CompanyId, OrderNo);
    CREATE INDEX IX_HospitalWorkOrders_CompanyId_Status ON dbo.HospitalWorkOrders (CompanyId, Status);
    CREATE INDEX IX_HospitalWorkOrders_CompanyId_AssetId ON dbo.HospitalWorkOrders (CompanyId, AssetId);
END

COMMIT TRANSACTION;
