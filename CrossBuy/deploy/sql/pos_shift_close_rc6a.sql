-- RC-6a: cash-drawer shift close. Idempotent, additive. Adds PosShifts close columns + cash over/short account 520111.
IF COL_LENGTH('PosShifts','ClosingFloat') IS NULL ALTER TABLE PosShifts ADD ClosingFloat DECIMAL(19,4) NULL;
IF COL_LENGTH('PosShifts','ExpectedCash') IS NULL ALTER TABLE PosShifts ADD ExpectedCash DECIMAL(19,4) NULL;
IF COL_LENGTH('PosShifts','CashVariance') IS NULL ALTER TABLE PosShifts ADD CashVariance DECIMAL(19,4) NULL;
IF COL_LENGTH('PosShifts','ClosedByEmployeeId') IS NULL ALTER TABLE PosShifts ADD ClosedByEmployeeId INT NULL;
IF COL_LENGTH('PosShifts','VarianceJournalEntryId') IS NULL ALTER TABLE PosShifts ADD VarianceJournalEntryId INT NULL;
GO
-- cash over/short account (sibling of 520108/520109 under 52, postable expense)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='520111')
INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, CreatedAt)
SELECT 1, '520111', N'عجز/زيادة نقدية الدرج', N'Cash over/short', 5, (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='52'), 1, 1, SYSUTCDATETIME();
GO
