-- ==========================================================================================
-- Hospital H1 — facility structure: room types, floors, wards, rooms, beds.
--
-- FIVE NEW TABLES, no ALTER of any existing table, so an environment that has not run this
-- script keeps every other module working; only the hospital screens are absent.
--
-- The building is drawn by the admin: a room's place on its floor (GridColumn/GridRow/
-- ColumnSpan/RowSpan on a 12-column grid) and its interior (LayoutJson) are data, never code.
--
-- Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- ---- room types: beds, features, daily rate, sex rule -----------------------------------
IF OBJECT_ID('dbo.HospitalRoomTypes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRoomTypes (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRoomTypes PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NULL,
        BedCount            int            NOT NULL CONSTRAINT DF_HospitalRoomTypes_Beds DEFAULT(1),
        DailyRate           decimal(18,4)  NOT NULL CONSTRAINT DF_HospitalRoomTypes_Rate DEFAULT(0),
        Features            nvarchar(400)  NULL,   -- comma-separated codes: bath,companion,oxygen,...
        SameSexOnly         bit            NOT NULL CONSTRAINT DF_HospitalRoomTypes_Sex DEFAULT(0),
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalRoomTypes_Sort DEFAULT(0),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalRoomTypes_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalRoomTypes_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalRoomTypes_Beds CHECK (BedCount BETWEEN 1 AND 20),
        CONSTRAINT CK_HospitalRoomTypes_Rate CHECK (DailyRate >= 0)
    );
    CREATE UNIQUE INDEX UX_HospitalRoomTypes_Code ON dbo.HospitalRoomTypes (CompanyId, Code);
END

-- ---- floors ------------------------------------------------------------------------------
IF OBJECT_ID('dbo.HospitalFloors','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFloors (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalFloors PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NULL,
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalFloors_Sort DEFAULT(0),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalFloors_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalFloors_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL
    );
    CREATE UNIQUE INDEX UX_HospitalFloors_Code ON dbo.HospitalFloors (CompanyId, Code);
END

-- ---- wards (nursing units) ----------------------------------------------------------------
IF OBJECT_ID('dbo.HospitalWards','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalWards (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalWards PRIMARY KEY,
        CompanyId           int            NOT NULL,
        FloorId             int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NULL,
        SexPolicy           nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalWards_Sex DEFAULT('Any'),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalWards_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalWards_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalWards_Sex CHECK (SexPolicy IN ('Any','Male','Female'))
    );
    CREATE UNIQUE INDEX UX_HospitalWards_Code ON dbo.HospitalWards (CompanyId, Code);
    CREATE INDEX IX_HospitalWards_Floor ON dbo.HospitalWards (CompanyId, FloorId);
END

-- ---- rooms and spaces: place on the floor + interior drawing -------------------------------
IF OBJECT_ID('dbo.HospitalRooms','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRooms (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRooms PRIMARY KEY,
        CompanyId           int            NOT NULL,
        FloorId             int            NOT NULL,
        WardId              int            NULL,
        RoomTypeId          int            NULL,   -- null for a space with no beds
        Number              nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NULL,
        NameEn              nvarchar(200)  NULL,
        Kind                nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalRooms_Kind DEFAULT('Ward'),
        GridColumn          int            NOT NULL CONSTRAINT DF_HospitalRooms_Col DEFAULT(1),
        GridRow             int            NOT NULL CONSTRAINT DF_HospitalRooms_Row DEFAULT(1),
        ColumnSpan          int            NOT NULL CONSTRAINT DF_HospitalRooms_ColSpan DEFAULT(2),
        RowSpan             int            NOT NULL CONSTRAINT DF_HospitalRooms_RowSpan DEFAULT(1),
        LayoutJson          nvarchar(max)  NULL,
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalRooms_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalRooms_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalRooms_Kind CHECK (Kind IN ('Ward','Icu','Clinic','Emergency','Imaging','Laboratory','Pharmacy','Theatre','Service')),
        CONSTRAINT CK_HospitalRooms_Grid CHECK (GridColumn BETWEEN 1 AND 12 AND ColumnSpan BETWEEN 1 AND 12
                                                AND GridColumn + ColumnSpan <= 13 AND GridRow >= 1 AND RowSpan >= 1),
        CONSTRAINT CK_HospitalRooms_Layout CHECK (LayoutJson IS NULL OR ISJSON(LayoutJson) = 1)
    );
    CREATE UNIQUE INDEX UX_HospitalRooms_Number ON dbo.HospitalRooms (CompanyId, Number);
    CREATE INDEX IX_HospitalRooms_Floor ON dbo.HospitalRooms (CompanyId, FloorId);
END

-- ---- beds ---------------------------------------------------------------------------------
IF OBJECT_ID('dbo.HospitalBeds','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBeds (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBeds PRIMARY KEY,
        CompanyId           int            NOT NULL,
        RoomId              int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Status              nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalBeds_Status DEFAULT('Vacant'),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalBeds_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalBeds_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalBeds_Status CHECK (Status IN ('Vacant','Occupied','Cleaning','Reserved','Blocked'))
    );
    CREATE UNIQUE INDEX UX_HospitalBeds_Code ON dbo.HospitalBeds (CompanyId, Code);
    CREATE INDEX IX_HospitalBeds_Room ON dbo.HospitalBeds (CompanyId, RoomId);
END

-- ---- schema guard: a table that exists with the wrong shape is a failure, not a pass ------
IF COL_LENGTH('dbo.HospitalRooms','LayoutJson') IS NULL OR COL_LENGTH('dbo.HospitalRooms','GridColumn') IS NULL
    THROW 51901, 'HospitalRooms exists but lacks the H1 layout columns: schema mismatch.', 1;
IF COL_LENGTH('dbo.HospitalBeds','Status') IS NULL
    THROW 51902, 'HospitalBeds exists but lacks Status: schema mismatch.', 1;

COMMIT;
