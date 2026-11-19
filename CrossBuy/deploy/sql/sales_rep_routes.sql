-- ==========================================================================================
-- Sales-rep routes and visits (2026-11-18) — "زيارات اليوم" in the owner's mobile prototype.
--
-- A ROUTE is a rep's standing plan: a name ("خط سير السالمية"), the rep, and the sales the rep should bring in a
-- day. Its STOPS are the customers in visiting order, each with a planned time and the weekdays it applies to.
-- A VISIT is one stop on one day: generated from the route the first time the rep's day is read (idempotent on
-- route + customer + date), or added by a manager for a single day. It records the arrival, the order taken at the
-- visit (its id and value — the day's achievement against the target is the sum of these), what was collected, and
-- how the visit ended.
--
-- Every table carries CompanyID (the global company filter and the write guard isolate them). Visits are kept,
-- never deleted: a skipped visit says so.
--
-- Idempotent: each table, key and index is created only if missing; nothing is filled.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID('dbo.SalesRepRoutes', 'U') IS NULL
CREATE TABLE dbo.SalesRepRoutes (
    ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesRepRoutes PRIMARY KEY,
    CompanyID       int NOT NULL,
    Name            nvarchar(150) NOT NULL,
    NameEn          nvarchar(150) NULL,
    RepEmployeeID   int NOT NULL,
    DailyTarget     decimal(18,3) NOT NULL CONSTRAINT DF_SalesRepRoutes_DailyTarget DEFAULT (0),
    IsActive        bit NOT NULL CONSTRAINT DF_SalesRepRoutes_IsActive DEFAULT (1),
    CreatedBy       nvarchar(256) NULL,
    CreatedAt       datetime2 NOT NULL CONSTRAINT DF_SalesRepRoutes_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt       datetime2 NULL,
    CONSTRAINT FK_SalesRepRoutes_Employee FOREIGN KEY (RepEmployeeID) REFERENCES dbo.Employee (ID),
    CONSTRAINT CK_SalesRepRoutes_Target CHECK (DailyTarget >= 0)
);
GO

IF OBJECT_ID('dbo.SalesRepRouteStops', 'U') IS NULL
CREATE TABLE dbo.SalesRepRouteStops (
    ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesRepRouteStops PRIMARY KEY,
    CompanyID       int NOT NULL,
    RouteID         int NOT NULL,
    CustomerID      int NOT NULL,
    Seq             int NOT NULL CONSTRAINT DF_SalesRepRouteStops_Seq DEFAULT (0),
    PlannedTime     time(0) NULL,
    DaysMask        int NOT NULL CONSTRAINT DF_SalesRepRouteStops_Days DEFAULT (127),   -- bit 0 = Sunday … bit 6 = Saturday
    CONSTRAINT FK_SalesRepRouteStops_Route FOREIGN KEY (RouteID) REFERENCES dbo.SalesRepRoutes (ID),
    CONSTRAINT FK_SalesRepRouteStops_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (ID),
    CONSTRAINT CK_SalesRepRouteStops_Days CHECK (DaysMask BETWEEN 0 AND 127)
);
GO

IF OBJECT_ID('dbo.SalesRepVisits', 'U') IS NULL
CREATE TABLE dbo.SalesRepVisits (
    ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesRepVisits PRIMARY KEY,
    CompanyID       int NOT NULL,
    RouteID         int NULL,
    RepEmployeeID   int NOT NULL,
    CustomerID      int NOT NULL,
    VisitDate       date NOT NULL,
    PlannedTime     time(0) NULL,
    Seq             int NOT NULL CONSTRAINT DF_SalesRepVisits_Seq DEFAULT (0),
    Status          nvarchar(20) NOT NULL CONSTRAINT DF_SalesRepVisits_Status DEFAULT (N'Planned'),
    ArrivedAt       datetime2 NULL,
    CompletedAt     datetime2 NULL,
    SalesOrderID    int NULL,
    OrderTotal      decimal(18,3) NOT NULL CONSTRAINT DF_SalesRepVisits_Order DEFAULT (0),
    Collected       decimal(18,3) NOT NULL CONSTRAINT DF_SalesRepVisits_Collected DEFAULT (0),
    Outcome         nvarchar(400) NULL,
    CreatedBy       nvarchar(256) NULL,
    CreatedAt       datetime2 NOT NULL CONSTRAINT DF_SalesRepVisits_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_SalesRepVisits_Route FOREIGN KEY (RouteID) REFERENCES dbo.SalesRepRoutes (ID),
    CONSTRAINT FK_SalesRepVisits_Employee FOREIGN KEY (RepEmployeeID) REFERENCES dbo.Employee (ID),
    CONSTRAINT FK_SalesRepVisits_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (ID),
    CONSTRAINT CK_SalesRepVisits_Status CHECK (Status IN (N'Planned', N'Arrived', N'Done', N'Skipped'))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesRepVisits_RepDay' AND object_id = OBJECT_ID('dbo.SalesRepVisits'))
    CREATE INDEX IX_SalesRepVisits_RepDay ON dbo.SalesRepVisits (CompanyID, RepEmployeeID, VisitDate) INCLUDE (Status, Seq);
GO
-- a route stop becomes at most one visit a day (the generator relies on it; a manager's one-off visit has no route)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SalesRepVisits_RouteCustomerDay' AND object_id = OBJECT_ID('dbo.SalesRepVisits'))
    CREATE UNIQUE INDEX UX_SalesRepVisits_RouteCustomerDay ON dbo.SalesRepVisits (RouteID, CustomerID, VisitDate) WHERE RouteID IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesRepRouteStops_Route' AND object_id = OBJECT_ID('dbo.SalesRepRouteStops'))
    CREATE INDEX IX_SalesRepRouteStops_Route ON dbo.SalesRepRouteStops (RouteID, Seq);
GO
