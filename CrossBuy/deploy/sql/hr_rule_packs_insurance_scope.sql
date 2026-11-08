-- ==========================================================================================
-- HR — RULE PACKS: who each social-insurance line covers (2026-11-08).
--   "appliesTo": "nationals" | "all"   and   "nationalCountryName": the CountriesLookup.CountryName of the nationals.
-- PIFSS (Kuwait) and the GOSI annuities and SANED (KSA) and the GPSSA pension (UAE) cover nationals only; KSA
-- occupational hazards and Egypt's social insurance cover everyone. Applied to the templates AND to company copies
-- of the same country that predate the field (a copy whose lines have no appliesTo yet).
-- Data only. Idempotent: a line that already carries appliesTo is never rewritten.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HrRulePacks','U') IS NULL
    THROW 51992, 'Run hr_rule_packs.sql first.', 1;

DECLARE @scope TABLE (CountryCode nvarchar(2), LineIndex int, AppliesTo nvarchar(10));
INSERT INTO @scope VALUES
  (N'KW',0,N'nationals'),(N'KW',1,N'nationals'),(N'KW',2,N'nationals'),(N'KW',3,N'nationals'),(N'KW',4,N'nationals'),
  (N'SA',0,N'nationals'),(N'SA',1,N'nationals'),(N'SA',2,N'all'),
  (N'AE',0,N'nationals'),
  (N'EG',0,N'all');

DECLARE @code nvarchar(2), @i int, @a nvarchar(10);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT CountryCode, LineIndex, AppliesTo FROM @scope;
OPEN c; FETCH NEXT FROM c INTO @code, @i, @a;
WHILE @@FETCH_STATUS = 0
BEGIN
    UPDATE dbo.HrRulePacks
       SET RulesJson = JSON_MODIFY(RulesJson, '$.insurance[' + CAST(@i AS nvarchar(5)) + '].appliesTo', @a)
     WHERE CountryCode = @code
       AND JSON_VALUE(RulesJson, '$.insurance[' + CAST(@i AS nvarchar(5)) + '].nameEn') IS NOT NULL
       AND JSON_VALUE(RulesJson, '$.insurance[' + CAST(@i AS nvarchar(5)) + '].appliesTo') IS NULL;
    FETCH NEXT FROM c INTO @code, @i, @a;
END
CLOSE c; DEALLOCATE c;

UPDATE dbo.HrRulePacks
   SET RulesJson = JSON_MODIFY(RulesJson, '$.nationalCountryName',
        CASE CountryCode WHEN N'KW' THEN N'Kuwait' WHEN N'SA' THEN N'Saudi Arabia' WHEN N'AE' THEN N'United Arab Emirates' WHEN N'EG' THEN N'Egypt' END)
 WHERE JSON_VALUE(RulesJson, '$.nationalCountryName') IS NULL AND CountryCode IN (N'KW', N'SA', N'AE', N'EG');

COMMIT TRANSACTION;
