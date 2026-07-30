-- POS-C2: delivery drivers (idempotent). Per-branch driver + order assignment.
IF OBJECT_ID('Drivers','U') IS NULL
CREATE TABLE Drivers (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    Name NVARCHAR(200) NOT NULL,
    Phone NVARCHAR(50) NOT NULL DEFAULT '',
    IsActive BIT NOT NULL DEFAULT 1
);
GO
IF COL_LENGTH('PosOrders','DriverId') IS NULL ALTER TABLE PosOrders ADD DriverId INT NULL;
GO
-- seed a couple of drivers for branch 15 (only if none yet)
IF NOT EXISTS (SELECT 1 FROM Drivers WHERE BranchId = 15)
INSERT INTO Drivers (BranchId, Name, Phone, IsActive) VALUES
    (15, N'أحمد السائق', '01000000001', 1),
    (15, N'محمود التوصيل', '01000000002', 1);
GO
