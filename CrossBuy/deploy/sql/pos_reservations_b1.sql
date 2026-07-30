-- POS-B1: table reservations (operational, no GL). Idempotent.
IF OBJECT_ID('Reservations','U') IS NULL
CREATE TABLE Reservations (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    BranchId INT NOT NULL,
    TableId INT NOT NULL,
    CustomerId INT NULL,
    GuestName NVARCHAR(200) NOT NULL DEFAULT '',
    GuestPhone NVARCHAR(50) NOT NULL DEFAULT '',
    ReservedAtUtc DATETIME2 NOT NULL,
    DurationMinutes INT NOT NULL DEFAULT 120,
    PartySize INT NOT NULL DEFAULT 2,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Booked',
    Notes NVARCHAR(1000) NULL,
    OrderId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Reservations_Branch_Table' AND object_id = OBJECT_ID('Reservations'))
    CREATE INDEX IX_Reservations_Branch_Table ON Reservations (BranchId, TableId, Status);
GO
