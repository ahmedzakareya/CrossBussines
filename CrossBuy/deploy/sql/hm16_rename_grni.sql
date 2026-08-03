-- HM-16 (HM-D55): account 210203 is the DE-FACTO GRNI clearing account, misnamed "Payroll Tax Payable".
-- Census (2026-08-03) proved EVERY posting on 210203 is GRNI-nature — inventory receipts (Dr 1103 / Cr 210203),
-- reversals, purchase returns, fixed-asset capitalization, landed cost, stock reconcile — and ZERO tax/payroll.
-- The code hard-codes Code='210203' as GRNI in three writers (PayableService, ProcurementService, StockService),
-- 24 item categories map GrniAccountId->210203, real withholding tax posts to 210202, and a duplicate payroll-tax
-- account 210205 exists (empty). So the fix is a NAME change ONLY (verdict a; precedent HM-D39 510101->COGS).
-- No re-routing, no historical reclassification, and NO tree move — 210203's parent stays 2102 (Taxes Payable);
-- the semantic re-parenting is deferred to the chart-of-accounts review (HM-D56), because moving it changes report
-- roll-ups. New name drops the word "مستحقة" so it cannot be confused with its tax siblings in 2102xx.
-- Idempotent (guarded by the NameEn check); NOT a migration.

SET NOCOUNT ON;

-- rename across ALL companies (ID/Code unchanged — every posting/report references it by ID/Code)
UPDATE Accounts
SET Name = N'بضاعة وردت ولم تُفوتَر', NameEn = N'Goods Received Not Invoiced (GRNI)'
WHERE Code = '210203' AND NameEn <> 'Goods Received Not Invoiced (GRNI)';

-- verify (expect 210203 = GRNI; 210205 stays the empty payroll-tax account; 210202 untouched WHT)
SELECT CompanyID, Code, Name, NameEn FROM Accounts WHERE Code IN ('210202','210203','210205') ORDER BY CompanyID, Code;
