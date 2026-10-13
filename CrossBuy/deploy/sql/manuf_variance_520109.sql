-- ============================================================================
-- Manufacturing بند5 — production cost variance account 520109 (clones 520108's type/parent per company).
-- Idempotent. Used when a work order is produced partially / closed: remaining WIP clears to 520109.
-- ============================================================================
INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive)
SELECT a.CompanyID, '520109', N'انحراف تكلفة الإنتاج', N'Production cost variance', a.AccountTypeId, a.ParentId, 1, 1
FROM Accounts a
WHERE a.Code = '520108'
  AND NOT EXISTS (SELECT 1 FROM Accounts b WHERE b.CompanyID = a.CompanyID AND b.Code = '520109');
GO
