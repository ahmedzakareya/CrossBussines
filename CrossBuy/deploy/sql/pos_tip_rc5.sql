-- RC-5 (tip): gratuity = a staff LIABILITY (210207), never revenue/tax/stock. Idempotent, additive.
IF COL_LENGTH('PosOrders','TipAmount') IS NULL ALTER TABLE PosOrders ADD TipAmount DECIMAL(19,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('PosOrders','TipMethod') IS NULL ALTER TABLE PosOrders ADD TipMethod NVARCHAR(20) NULL;
IF COL_LENGTH('PosOrders','TipJournalEntryId') IS NULL ALTER TABLE PosOrders ADD TipJournalEntryId INT NULL;
GO
-- tips-payable liability account (sibling of 210201 under 2102)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='210207')
INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, CreatedAt)
SELECT 1, '210207', N'إكراميات مستحقة', N'Tips payable', 2, (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='2102'), 1, 1, SYSUTCDATETIME();
GO
