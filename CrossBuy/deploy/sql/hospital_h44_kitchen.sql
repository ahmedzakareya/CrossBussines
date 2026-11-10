-- ==========================================================================================
-- Hospital H44 — the central kitchen (the prototype's #/kitchen). One kitchen, three consumers: patients (the trays
-- of H43, per ward), staff and the cafeteria (meals counted per destination). Every meal is built on a recipe (an
-- inventory item with a bill of materials). The kitchen's costs gather in a pool account; at month end the pool is
-- allocated by meals x standard cost and posted as one journal with cost centres on its lines (owner ruling).
--   HospitalKitchenSettings         the kitchen store and the four accounts of the allocation journal
--   HospitalDietRecipes             the recipe each diet's tray is built on
--   HospitalKitchenDestinations     staff groups, cafeteria outlets, and wards' cost centres
--   HospitalKitchenCounts           meals served a destination per day and round
--   HospitalKitchenAllocations      one row per month allocated (and its journal)
--   HospitalKitchenAllocationLines  the month's split
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalWards','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql first.', 1;

IF OBJECT_ID('dbo.HospitalKitchenSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalKitchenSettings (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int         NOT NULL,
        KitchenWarehouseId      int         NULL,
        PoolAccountId           int         NULL,
        PatientMealsAccountId   int         NULL,
        StaffMealsAccountId     int         NULL,
        CafeteriaCogsAccountId  int         NULL,
        UpdatedAt               datetime2   NOT NULL,
        CONSTRAINT PK_HospitalKitchenSettings PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalKitchenSettings_CompanyId ON dbo.HospitalKitchenSettings (CompanyId);
END

IF OBJECT_ID('dbo.HospitalDietRecipes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDietRecipes (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int           NOT NULL,
        Diet          nvarchar(20)  NOT NULL,
        RecipeItemId  int           NOT NULL,
        CONSTRAINT PK_HospitalDietRecipes PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDietRecipes_Diet CHECK (Diet IN (N'Regular', N'Diabetic', N'LowSalt', N'Renal', N'Soft', N'ClearFluids', N'TubeFeed', N'NPO'))
    );
    CREATE UNIQUE INDEX IX_HospitalDietRecipes_CompanyId_Diet ON dbo.HospitalDietRecipes (CompanyId, Diet);
END

IF OBJECT_ID('dbo.HospitalKitchenDestinations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalKitchenDestinations (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int            NOT NULL,
        Name          nvarchar(150)  NOT NULL,
        NameEn        nvarchar(150)  NULL,
        Stream        nvarchar(20)   NOT NULL,
        WardId        int            NULL,
        RecipeItemId  int            NULL,
        CostCenterId  int            NULL,
        PosBranchId   int            NULL,
        IsActive      bit            NOT NULL,
        CreatedAt     datetime2      NOT NULL,
        CONSTRAINT PK_HospitalKitchenDestinations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalKitchenDestinations_Ward FOREIGN KEY (WardId) REFERENCES dbo.HospitalWards(ID),
        CONSTRAINT CK_HospitalKitchenDestinations_Stream CHECK (Stream IN (N'Patient', N'Staff', N'Cafeteria')),
        -- a patient destination is a ward; the others are not
        CONSTRAINT CK_HospitalKitchenDestinations_Ward CHECK ((Stream = N'Patient' AND WardId IS NOT NULL) OR (Stream <> N'Patient' AND WardId IS NULL))
    );
END

IF OBJECT_ID('dbo.HospitalKitchenCounts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalKitchenCounts (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int           NOT NULL,
        DestinationId         int           NOT NULL,
        Day                   date          NOT NULL,
        Round                 nvarchar(10)  NOT NULL,
        Meals                 int           NOT NULL,
        RecordedByEmployeeId  int           NULL,
        RecordedAt            datetime2     NOT NULL,
        CONSTRAINT PK_HospitalKitchenCounts PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalKitchenCounts_Destination FOREIGN KEY (DestinationId) REFERENCES dbo.HospitalKitchenDestinations(ID),
        CONSTRAINT CK_HospitalKitchenCounts_Round CHECK (Round IN (N'Breakfast', N'Lunch', N'Snack', N'Dinner')),
        CONSTRAINT CK_HospitalKitchenCounts_Meals CHECK (Meals BETWEEN 0 AND 100000)
    );
    CREATE UNIQUE INDEX IX_HospitalKitchenCounts_DestinationId_Day_Round ON dbo.HospitalKitchenCounts (DestinationId, Day, Round);
END

IF OBJECT_ID('dbo.HospitalKitchenAllocations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalKitchenAllocations (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyId           int             NOT NULL,
        Month               date            NOT NULL,
        PoolTotal           decimal(18,3)   NOT NULL,
        Meals               int             NOT NULL,
        JournalEntryId      int             NULL,
        PostedAt            datetime2       NOT NULL,
        PostedByEmployeeId  int             NULL,
        CONSTRAINT PK_HospitalKitchenAllocations PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalKitchenAllocations_Month CHECK (DAY(Month) = 1)
    );
    CREATE UNIQUE INDEX IX_HospitalKitchenAllocations_CompanyId_Month ON dbo.HospitalKitchenAllocations (CompanyId, Month);
END

IF OBJECT_ID('dbo.HospitalKitchenAllocationLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalKitchenAllocationLines (
        ID             int IDENTITY(1,1) NOT NULL,
        AllocationId   int             NOT NULL,
        Stream         nvarchar(20)    NOT NULL,
        WardId         int             NULL,
        DestinationId  int             NULL,
        Name           nvarchar(150)   NOT NULL,
        NameEn         nvarchar(150)   NULL,
        Meals          int             NOT NULL,
        StandardCost   decimal(18,3)   NOT NULL,
        Allocated      decimal(18,3)   NOT NULL,
        CostCenterId   int             NULL,
        CONSTRAINT PK_HospitalKitchenAllocationLines PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalKitchenAllocationLines_Allocation FOREIGN KEY (AllocationId) REFERENCES dbo.HospitalKitchenAllocations(ID),
        CONSTRAINT CK_HospitalKitchenAllocationLines_Stream CHECK (Stream IN (N'Patient', N'Staff', N'Cafeteria'))
    );
END

COMMIT TRANSACTION;
