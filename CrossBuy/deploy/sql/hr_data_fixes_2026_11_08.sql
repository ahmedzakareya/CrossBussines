-- ==========================================================================================
-- HR data corrections, 2026-11-08 (owner: TAB-0 acts with full authority on the HR programme).
--
-- 1. Leave policy 8 ("main policy", company 1, 9 employees) was below Kuwait's Law 6/2010 (Art. 69): sick leave was
--    15 days; the statutory entitlement is 75 (15 full · 10 at 75% · 10 at 50% · 10 at 25% · 30 unpaid). Raised to 75.
--    The rule-pack compliance check on /Admin/Leave showed it "below the law".
-- 2. The same policy carried Maternity leave TWICE (70 and 30 days). The 70-day line is the statutory one; the 30-day
--    duplicate is removed. No row references a LeavePolicies row (requests reference the leave TYPE), so nothing else
--    moves.
-- 3. CountriesLookup had no Saudi Arabia and no UAE, so no employee could be recorded as a Saudi or Emirati national
--    and the rule packs' "nationals only" insurance lines could never apply to them. Both added.
--    (NationalityName holds the ENGLISH nationality and NationalityNameEn the ARABIC one — the table's columns are
--    swapped, and the new rows follow the existing convention so every reader stays consistent.)
--
-- Idempotent: each step checks the state it changes.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. sick leave 15 → 75 on policy 8 (only while it is still 15)
UPDATE lp SET lp.EntitlementDaysPerYear = 75
  FROM dbo.LeavePolicies lp JOIN dbo.LeaveTypes t ON t.ID = lp.LeaveTypeID
 WHERE lp.LeavePolicyTypeID = 8 AND t.NameEn = N'Medical Leave' AND lp.EntitlementDaysPerYear = 15;

-- 2. the duplicate 30-day maternity line, only while the 70-day line for the same type exists beside it
DELETE dup
  FROM dbo.LeavePolicies dup
 WHERE dup.LeavePolicyTypeID = 8 AND dup.EntitlementDaysPerYear = 30
   AND dup.LeaveTypeID = (SELECT ID FROM dbo.LeaveTypes WHERE NameEn = N'Maternity leave')
   AND EXISTS (SELECT 1 FROM dbo.LeavePolicies keep WHERE keep.LeavePolicyTypeID = 8 AND keep.LeaveTypeID = dup.LeaveTypeID AND keep.EntitlementDaysPerYear = 70);

-- 3. Saudi Arabia and the UAE in the countries list
IF NOT EXISTS (SELECT 1 FROM dbo.CountriesLookup WHERE CountryName = N'Saudi Arabia')
    INSERT INTO dbo.CountriesLookup (CountryName, CountryNameAr, NationalityName, NationalityNameEn, Flage)
    VALUES (N'Saudi Arabia', N'السعودية', N'Saudi', N'سعودي', N'saudi-arabia_flag.png');
IF NOT EXISTS (SELECT 1 FROM dbo.CountriesLookup WHERE CountryName = N'United Arab Emirates')
    INSERT INTO dbo.CountriesLookup (CountryName, CountryNameAr, NationalityName, NationalityNameEn, Flage)
    VALUES (N'United Arab Emirates', N'الإمارات', N'Emirati', N'إماراتي', N'united-arab-emirates_flag.png');

-- 4. (owner-approved 2026-11-08) sick leave needs NO advance notice: with 2 days' notice the leave engine refused every
--    sick leave for today or tomorrow — and illness is not announced in advance. Only while it is still 2.
UPDATE lp SET lp.NoticePeriodDays = 0
  FROM dbo.LeavePolicies lp JOIN dbo.LeaveTypes t ON t.ID = lp.LeaveTypeID
 WHERE lp.LeavePolicyTypeID = 8 AND t.NameEn = N'Medical Leave' AND lp.NoticePeriodDays = 2;

COMMIT TRANSACTION;
