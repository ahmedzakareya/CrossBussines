-- HM-D39: account 510101 is the DE-FACTO cost-of-goods account, misnamed "Rent Expense".
-- Forensic (2026-08-02) proved it holds ONLY cost-of-goods postings — 411 Inventory-source JEs + 14 Reversal JEs,
-- and ZERO rent/purchase/manual entries (the seed-acc-demo rent purchase never landed on it). So the routing is
-- CORRECT and the fix is a NAME change only (verdict a). No re-routing, no historical reclassification.
-- Additionally create a SEPARATE real Rent Expense account (510105) so the seed-acc-demo fixture stops mapping rent
-- onto the COGS account. Idempotent (guarded); NOT a migration.

SET NOCOUNT ON;
DECLARE @co INT = 1;

-- 1) rename the COGS account (NAME only; ID/Code unchanged — every posting/report/reconciliation references it by ID/Code)
UPDATE Accounts SET Name = N'تكلفة البضاعة المباعة', NameEn = N'Cost of Goods Sold'
WHERE CompanyID = @co AND Code = '510101' AND NameEn <> 'Cost of Goods Sold';

-- 2) create a real Rent Expense account (510105) under 51 "Operating Expenses", if missing
DECLARE @parent INT = (SELECT ID FROM Accounts WHERE CompanyID = @co AND Code = '51');
IF @parent IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID = @co AND Code = '510105')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, CashFlowCategory, CreatedAt)
    VALUES (@co, '510105', N'مصروف إيجار', N'Rent Expense', 5, @parent, 1, 1, 'Operating', GETUTCDATE());

-- verify (expect: 510101 = Cost of Goods Sold, 510105 = Rent Expense)
SELECT (SELECT NameEn FROM Accounts WHERE CompanyID=@co AND Code='510101') AS Acct_510101,
       (SELECT NameEn FROM Accounts WHERE CompanyID=@co AND Code='510105') AS Acct_510105;
