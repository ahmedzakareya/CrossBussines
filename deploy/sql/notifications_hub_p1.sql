-- Communication Hub P1: expand the Notifications table (additive, all new columns nullable).
-- Idempotent. Safe to re-run.
IF COL_LENGTH('Notifications','CompanyID')      IS NULL ALTER TABLE Notifications ADD CompanyID       INT           NULL;
GO
IF COL_LENGTH('Notifications','BranchID')       IS NULL ALTER TABLE Notifications ADD BranchID        INT           NULL;
GO
IF COL_LENGTH('Notifications','Url')            IS NULL ALTER TABLE Notifications ADD Url             NVARCHAR(400) NULL;
GO
IF COL_LENGTH('Notifications','Priority')       IS NULL ALTER TABLE Notifications ADD Priority        NVARCHAR(20)  NULL;
GO
IF COL_LENGTH('Notifications','Category')       IS NULL ALTER TABLE Notifications ADD Category        NVARCHAR(40)  NULL;
GO
IF COL_LENGTH('Notifications','ActorEmployeeID') IS NULL ALTER TABLE Notifications ADD ActorEmployeeID INT          NULL;
GO
IF COL_LENGTH('Notifications','DedupKey')       IS NULL ALTER TABLE Notifications ADD DedupKey        NVARCHAR(120) NULL;
GO
IF COL_LENGTH('Notifications','ExpiresAt')      IS NULL ALTER TABLE Notifications ADD ExpiresAt       DATETIME2     NULL;
GO
IF COL_LENGTH('Notifications','ReadAt')         IS NULL ALTER TABLE Notifications ADD ReadAt          DATETIME2     NULL;
GO
IF COL_LENGTH('Notifications','Icon')           IS NULL ALTER TABLE Notifications ADD Icon            NVARCHAR(60)  NULL;
GO

-- Helpful indexes for the bell (unread per recipient, scoped by company) and dedup lookups.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Notif_Recipient_Unread')
    CREATE INDEX IX_Notif_Recipient_Unread ON Notifications (RecipientEmployeeID, IsRead) INCLUDE (CompanyID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Notif_Dedup')
    CREATE INDEX IX_Notif_Dedup ON Notifications (RecipientEmployeeID, DedupKey, IsRead);
GO

-- Backfill CompanyID for existing rows from the recipient employee (best-effort; leaves nulls where unknown).
UPDATE n SET n.CompanyID = e.EmpCompanyID
FROM Notifications n
JOIN Employee e ON e.ID = n.RecipientEmployeeID
WHERE n.CompanyID IS NULL;
GO