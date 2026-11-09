-- ==========================================================================================
-- Tiered leave pay (2026-11-09): payroll deducts the unpaid part of each approved day of a leave whose rule-pack line
-- carries payTiers. Seeds the statutory sick-leave tiers on the packs whose rule text states them, only where the line
-- has none yet (a company's own tiers are never overwritten). Idempotent.
--   KW Art. 69:  15 full, 10 at 75%, 10 at 50%, 10 at 25%, 30 unpaid
--   SA Art. 117: 30 full, 60 at 75%, 30 unpaid
--   AE Art. 31:  15 full, 30 at 50%, 45 unpaid
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF COL_LENGTH(N'dbo.Payslips', N'SickLeaveDeduction') IS NULL
    ALTER TABLE dbo.Payslips ADD SickLeaveDeduction decimal(18,3) NOT NULL CONSTRAINT DF_Payslips_SickLeaveDeduction DEFAULT 0;

UPDATE dbo.HrRulePacks SET RulesJson = JSON_MODIFY(RulesJson, '$.leave[1].payTiers',
       JSON_QUERY(N'[{"days":15,"paidPct":100},{"days":10,"paidPct":75},{"days":10,"paidPct":50},{"days":10,"paidPct":25},{"days":30,"paidPct":0}]'))
 WHERE CountryCode = N'KW' AND JSON_VALUE(RulesJson, '$.leave[1].code') = N'sick' AND JSON_QUERY(RulesJson, '$.leave[1].payTiers') IS NULL;
UPDATE dbo.HrRulePacks SET RulesJson = JSON_MODIFY(RulesJson, '$.leave[1].payTiers',
       JSON_QUERY(N'[{"days":30,"paidPct":100},{"days":60,"paidPct":75},{"days":30,"paidPct":0}]'))
 WHERE CountryCode = N'SA' AND JSON_VALUE(RulesJson, '$.leave[1].code') = N'sick' AND JSON_QUERY(RulesJson, '$.leave[1].payTiers') IS NULL;
UPDATE dbo.HrRulePacks SET RulesJson = JSON_MODIFY(RulesJson, '$.leave[1].payTiers',
       JSON_QUERY(N'[{"days":15,"paidPct":100},{"days":30,"paidPct":50},{"days":45,"paidPct":0}]'))
 WHERE CountryCode = N'AE' AND JSON_VALUE(RulesJson, '$.leave[1].code') = N'sick' AND JSON_QUERY(RulesJson, '$.leave[1].payTiers') IS NULL;
