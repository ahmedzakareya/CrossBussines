-- HM-D38: branch-level default tax code, so the tax rate can follow the BRANCH (its country's regime) instead of only the
-- item category. A Kuwait branch has no VAT ⇒ its items are 0% (VATEX) without tagging each item. Resolution order in
-- PosOrderService.ResolveTaxRateAsync becomes: item.DefaultTaxCodeId → BranchPosSetting.DefaultTaxCodeId → company default.
-- A NULL branch value = the exact current behaviour (restaurant regression barrier). Idempotent; NOT a migration.

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='BranchPosSettings' AND COLUMN_NAME='DefaultTaxCodeId')
    ALTER TABLE BranchPosSettings ADD DefaultTaxCodeId INT NULL;
GO

-- Kuwait hyper branch (HYPER-DEMO) ⇒ VATEX (0%). Restaurant branches keep NULL ⇒ unchanged behaviour.
DECLARE @branch INT = (SELECT ID FROM Branches WHERE Name = 'HYPER-DEMO');
DECLARE @vatex  INT = (SELECT ID FROM TaxCodes WHERE CompanyID = 1 AND Code = 'VATEX');
IF @branch IS NOT NULL AND @vatex IS NOT NULL
    UPDATE BranchPosSettings SET DefaultTaxCodeId = @vatex
    WHERE BranchId = @branch AND (DefaultTaxCodeId IS NULL OR DefaultTaxCodeId <> @vatex);

SELECT (SELECT DefaultTaxCodeId FROM BranchPosSettings WHERE BranchId = @branch) AS Branch17TaxCode,
       (SELECT COUNT(*) FROM BranchPosSettings WHERE DefaultTaxCodeId IS NOT NULL) AS BranchesWithTaxOverride;
